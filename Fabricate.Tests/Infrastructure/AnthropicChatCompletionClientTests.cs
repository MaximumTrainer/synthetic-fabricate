using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using FluentAssertions;

namespace Fabricate.Tests.Infrastructure;

/// <summary>Request/response mapping through the real Anthropic SDK against a canned HTTP handler; no network.</summary>
public sealed class AnthropicChatCompletionClientTests
{
    private static (AnthropicChatCompletionClient client, OpenAiCompatibleChatCompletionClientTests.CannedHandler handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new OpenAiCompatibleChatCompletionClientTests.CannedHandler(respond);
        var sdk = new AnthropicClient { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        return (new AnthropicChatCompletionClient(sdk, "anthropic"), handler);
    }

    private static ChatCompletionRequest Request(LlmEffort? effort = null, params LlmToolDefinition[] tools) => new(
        "claude-opus-5", "Be terse.",
        [LlmMessage.User("hi"), LlmMessage.Assistant("calling", [new LlmToolCall("toolu_1", "echo", """{"a":1}""")]), LlmMessage.FromToolResult(new LlmToolResult("toolu_1", "{\"ok\":true}"))],
        tools, 512, Temperature: 0.7, Effort: effort);

    [Fact]
    public async Task Complete_BuildsMessagesApiRequest_WithAdaptiveThinking_NoSampling_NoBudget()
    {
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"text","text":"Sure."},{"type":"tool_use","id":"toolu_2","name":"echo","input":{"v":2}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":20,"output_tokens":8}}
            """));

        var result = await client.CompleteAsync(Request(LlmEffort.High, new LlmToolDefinition("echo", "Echoes", """{"type":"object","properties":{"v":{"type":"number"}},"required":["v"]}""")));

        result.Text.Should().Be("Sure.");
        result.ToolCalls.Should().ContainSingle().Which.Should().Be(new LlmToolCall("toolu_2", "echo", """{"v":2}"""));
        result.StopReason.Should().Be(LlmStopReason.ToolUse);
        result.Usage.Should().Be(new TokenUsage(20, 8));
        result.ModelId.Should().Be("claude-opus-5", "the wire value, not the SDK enum's JSON form with its quotes (#86)");

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/v1/messages");
        handler.LastRequest.Headers.TryGetValues("x-api-key", out var keys).Should().BeTrue();
        keys!.Single().Should().Be("sk-ant-test");

        var body = JsonNode.Parse(handler.LastBody!)!;
        body["model"]!.GetValue<string>().Should().Be("claude-opus-5");
        body["max_tokens"]!.GetValue<int>().Should().Be(512);
        body["system"]!.GetValue<string>().Should().Be("Be terse.");
        body["thinking"]!["type"]!.GetValue<string>().Should().Be("adaptive");
        body["thinking"]!["budget_tokens"].Should().BeNull("budget_tokens is rejected by current models");
        body["temperature"].Should().BeNull("sampling parameters are rejected by current models");
        body["output_config"]!["effort"]!.GetValue<string>().Should().Be("high");

        var messages = body["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().Should().Be("user");
        messages[1]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).Should().Equal("text", "tool_use");
        messages[1]!["content"]![1]!["input"]!["a"]!.GetValue<int>().Should().Be(1);
        messages[2]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("tool_result");
        messages[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>().Should().Be("toolu_1");

        body["tools"]![0]!["input_schema"]!["properties"]!["v"]!["type"]!.GetValue<string>().Should().Be("number");
        body["tools"]![0]!["input_schema"]!["required"]![0]!.GetValue<string>().Should().Be("v");
    }

    [Fact]
    public async Task Complete_Refusal_IsAResult_NotAnException()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.OK, """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5","content":[],
             "stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":"policy"},
             "usage":{"input_tokens":5,"output_tokens":0}}
            """));

        var result = await client.CompleteAsync(Request());

        result.StopReason.Should().Be(LlmStopReason.Refusal);
        result.Text.Should().BeNull();
        result.StopDetail.Should().Contain("cyber");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, LlmFailureKind.ProviderError)]
    [InlineData(HttpStatusCode.NotFound, LlmFailureKind.InvalidRequest)]
    public async Task Complete_TranslatesSdkExceptions_MostSpecificFirst(HttpStatusCode status, LlmFailureKind expected)
    {
        var (client, _) = Build(_ => Json(status, """{"type":"error","error":{"type":"x","message":"bad key sk-ant-test"}}"""));

        var act = () => client.CompleteAsync(Request());

        var ex = (await act.Should().ThrowAsync<LlmProviderException>()).Which;
        ex.Kind.Should().Be(expected);
        ex.Message.Should().NotContain("sk-ant-test");
    }

    [Fact]
    public async Task Stream_AccumulatesTextAndToolInput_AndMapsStop()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":11,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"echo","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"v\""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":":3}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":14}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var c in client.StreamAsync(Request())) chunks.Add(c);

        chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta).Should().Equal("Hel", "lo");
        var final = chunks.Last().Final!;
        final.Text.Should().Be("Hello");
        final.ToolCalls.Should().ContainSingle().Which.Should().Be(new LlmToolCall("toolu_9", "echo", """{"v":3}"""));
        final.StopReason.Should().Be(LlmStopReason.ToolUse);
        final.Usage.Should().Be(new TokenUsage(11, 14));
        final.ModelId.Should().Be("claude-opus-5", "the wire value, not the SDK enum's JSON form with its quotes (#86)");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
