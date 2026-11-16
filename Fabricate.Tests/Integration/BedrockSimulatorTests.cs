using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic.Bedrock;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using FluentAssertions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #99: Claude on Bedrock against a local simulator, so the Bedrock-specific half of the path runs in CI with no AWS
/// account.
///
/// <para>
/// <strong>Why a purpose-built simulator.</strong> The adapter talks to Bedrock's Messages endpoint
/// (<c>bedrock-mantle</c>, <c>POST /v1/messages</c>), not to <c>bedrock-runtime</c>'s InvokeModel. None of the AWS
/// simulators can stand in for that: LocalStack Community (the <c>3.8</c> image the KMS tests use) has no Bedrock at
/// all; current LocalStack images will not start without a paid auth token; moto routes only
/// <c>bedrock-runtime.*</c> hosts and its InvokeModel returns an empty body. So this is a loopback HTTP server that
/// behaves like the Mantle endpoint in the ways the adapter depends on, and is strict where AWS is strict.
/// </para>
///
/// <para>
/// <strong>What it verifies.</strong> That every request is SigV4-signed for the <c>bedrock-mantle</c> service in the
/// configured region, and that the signature is <em>cryptographically valid</em> for the credential — a request
/// that would be rejected by AWS is rejected here with the same 403. That the body is the Messages API shape with the
/// Bedrock model id (<c>anthropic.</c> prefix), adaptive thinking and no sampling parameters. That completion,
/// tool round trip, streaming, usage and stop reasons map through the Bedrock client. That a bad signature and an
/// unknown model surface as named <see cref="LlmFailureKind"/>s, not a generic provider error.
/// </para>
///
/// <para>
/// <strong>What it cannot verify</strong>, and #99 keeps open: that real Bedrock accepts every field the adapter
/// sends (adaptive thinking, <c>output_config.effort</c>); Bedrock's actual error bodies and status codes for
/// throttling and model access; whether Mantle streams plain server-sent events as modelled here; and credential
/// resolution from a real role or OIDC token. Those need <c>LiveProviderTests</c> with an AWS account.
/// </para>
/// </summary>
public sealed class BedrockSimulatorTests : IDisposable
{
    private const string Region = "eu-west-2";
    private const string Model = "anthropic.claude-haiku-5-5";
    private const string AccessKey = "AKIASIMULATOR0EXAMPLE";
    private const string SecretKey = "simulator/secret/key/not/a/real/aws/credential0";

    private readonly MantleSimulator _simulator = new(Region, AccessKey, SecretKey, [Model]);

    public void Dispose() => _simulator.Dispose();

    private AnthropicChatCompletionClient Client(string region = Region, string secret = SecretKey)
    {
        // Built exactly as ChatCompletionClientFactory builds it for LlmProvider.AwsBedrock, plus the two things a
        // simulator needs: explicit credentials instead of the ambient chain, and a base URL instead of the
        // regional amazonaws.com host.
        var sdk = new AnthropicBedrockMantleClient(new MantleAwsClientOptions
        {
            AwsRegion = region,
            AwsAccessKey = AccessKey,
            AwsSecretAccessKey = secret,
        })
        {
            BaseUrl = _simulator.BaseUrl,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new AnthropicChatCompletionClient(sdk, "bedrock");
    }

    private static readonly LlmToolDefinition Weather = new(
        "get_weather", "Weather for a city.",
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""");

    [Fact]
    public async Task ACompletionIsSignedForBedrockMantleAndMapsTextUsageAndStop()
    {
        var result = await Client().CompleteAsync(new ChatCompletionRequest(
            Model, "Be terse.", [LlmMessage.User("Say pong")], [], 64, Temperature: 0.7, Effort: LlmEffort.Low));

        result.Text.Should().Be("pong");
        result.StopReason.Should().Be(LlmStopReason.EndTurn);
        result.Usage.Should().Be(new TokenUsage(9, 2));
        result.ModelId.Should().Be(Model);

        var request = _simulator.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/v1/messages");
        request.SignatureScope.Should().EndWith($"/{Region}/bedrock-mantle/aws4_request");

        var body = JsonNode.Parse(request.Body)!;
        body["model"]!.GetValue<string>().Should().Be(Model, "Bedrock model ids keep their anthropic. prefix");
        body["thinking"]!["type"]!.GetValue<string>().Should().Be("adaptive");
        body["temperature"].Should().BeNull("current Claude models reject sampling parameters on Bedrock too");
        body["output_config"]!["effort"]!.GetValue<string>().Should().Be("low");
    }

    [Fact]
    public async Task AToolCallRoundTripsThroughBedrock()
    {
        var client = Client();
        var first = await client.CompleteAsync(new ChatCompletionRequest(
            Model, null, [LlmMessage.User("weather in Paris?")], [Weather], 256));

        first.StopReason.Should().Be(LlmStopReason.ToolUse);
        var call = first.ToolCalls.Should().ContainSingle().Subject;
        call.Name.Should().Be("get_weather");

        var second = await client.CompleteAsync(new ChatCompletionRequest(Model, null,
        [
            LlmMessage.User("weather in Paris?"),
            LlmMessage.Assistant(first.Text, first.ToolCalls),
            LlmMessage.FromToolResult(new LlmToolResult(call.Id, """{"temperatureC":17}""")),
        ], [Weather], 256));

        second.StopReason.Should().Be(LlmStopReason.EndTurn);
        second.Text.Should().Contain("17");

        // The simulator answers the second leg only if the tool_result names the tool_use id it issued, as the
        // real API does; this pins the request-side mapping, not just the response parsing.
        var sent = JsonNode.Parse(_simulator.Requests.Last().Body)!["messages"]!.AsArray();
        sent[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>().Should().Be(call.Id);
    }

    [Fact]
    public async Task AStreamThroughBedrockReassemblesTextToolInputAndUsage()
    {
        var chunks = new List<ChatCompletionChunk>();
        await foreach (var chunk in Client().StreamAsync(new ChatCompletionRequest(
            Model, null, [LlmMessage.User("stream: weather in Paris?")], [Weather], 256)))
        {
            chunks.Add(chunk);
        }

        chunks.Count(c => c.Final is not null).Should().Be(1);
        string.Concat(chunks.Select(c => c.TextDelta)).Should().Be("Checking.");
        var final = chunks.Single(c => c.Final is not null).Final!;
        final.StopReason.Should().Be(LlmStopReason.ToolUse);
        final.ToolCalls.Should().ContainSingle().Which.ArgumentsJson.Should().Be("""{"city":"Paris"}""");
        final.Usage.Should().Be(new TokenUsage(21, 12));
        final.ModelId.Should().Be(Model);
        _simulator.Requests.Single().SignatureScope.Should().EndWith("/bedrock-mantle/aws4_request");
    }

    [Fact]
    public async Task ATruncatedReplyMapsToMaxTokens()
    {
        var result = await Client().CompleteAsync(new ChatCompletionRequest(
            Model, null, [LlmMessage.User("count to 300")], [], 16));

        result.StopReason.Should().Be(LlmStopReason.MaxTokens);
    }

    [Fact]
    public async Task ASignatureForTheWrongSecretIsRejectedAsAnAuthenticationFailure()
    {
        var act = () => Client(secret: "not-the-secret").CompleteAsync(new ChatCompletionRequest(
            Model, null, [LlmMessage.User("Say pong")], [], 64));

        (await act.Should().ThrowAsync<LlmProviderException>()).Which.Kind.Should().Be(LlmFailureKind.Authentication);
        _simulator.Rejections.Should().ContainSingle().Which.Should().Contain("signature does not match");
    }

    [Fact]
    public async Task ASignatureScopedToAnotherRegionIsRejected()
    {
        var act = () => Client(region: "us-east-1").CompleteAsync(new ChatCompletionRequest(
            Model, null, [LlmMessage.User("Say pong")], [], 64));

        (await act.Should().ThrowAsync<LlmProviderException>()).Which.Kind.Should().Be(LlmFailureKind.Authentication);
        _simulator.Rejections.Should().ContainSingle().Which.Should().Contain("region");
    }

    [Fact]
    public async Task AModelTheAccountCannotUseIsANamedFailureNotAGenericOne()
    {
        var act = () => Client().CompleteAsync(new ChatCompletionRequest(
            "anthropic.claude-not-enabled", null, [LlmMessage.User("Say pong")], [], 64));

        (await act.Should().ThrowAsync<LlmProviderException>()).Which.Kind.Should().Be(LlmFailureKind.InvalidRequest);
    }

    // ── The simulator ────────────────────────────────────────────────────────────

    private sealed record SeenRequest(string Path, string SignatureScope, string Body);

    /// <summary>
    /// A loopback stand-in for <c>bedrock-mantle.{region}.amazonaws.com</c>. It verifies SigV4 the way AWS does —
    /// recomputes the signature from the canonical request and compares — and answers with Messages API responses
    /// chosen from the prompt, so each test can drive one path.
    /// </summary>
    private sealed class MantleSimulator : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _region;
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly HashSet<string> _models;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public MantleSimulator(string region, string accessKey, string secretKey, IEnumerable<string> models)
        {
            (_region, _accessKey, _secretKey, _models) = (region, accessKey, secretKey, models.ToHashSet());
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }
        public ConcurrentQueue<SeenRequest> Requests { get; } = new();
        public ConcurrentQueue<string> Rejections { get; } = new();

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* listener closed */ }
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                try { await HandleAsync(context); }
                catch (Exception ex)
                {
                    Rejections.Enqueue("simulator fault: " + ex.Message);
                    try { await RespondAsync(context.Response, 500, Error("api_error", ex.Message)); } catch { }
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var (scope, failure) = VerifySignature(request, body);
            if (failure is not null)
            {
                Rejections.Enqueue(failure);
                await RespondAsync(context.Response, 403, Error("permission_error", failure));
                return;
            }

            Requests.Enqueue(new SeenRequest(request.Url!.AbsolutePath, scope!, body));

            if (request.HttpMethod != "POST" || request.Url.AbsolutePath != "/v1/messages")
            {
                await RespondAsync(context.Response, 404, Error("not_found_error", "unknown route"));
                return;
            }

            var json = JsonNode.Parse(body)!;
            var model = json["model"]!.GetValue<string>();
            if (!_models.Contains(model))
            {
                await RespondAsync(context.Response, 400, Error("invalid_request_error", $"The provided model identifier is invalid: {model}"));
                return;
            }

            var messages = json["messages"]!.AsArray();
            var lastUser = messages.Last()!;
            var prompt = lastUser["content"] is JsonValue v ? v.GetValue<string>() : string.Empty;
            var isStream = json["stream"]?.GetValue<bool>() == true;

            if (isStream)
            {
                await StreamAsync(context.Response, model);
                return;
            }

            if (lastUser["content"] is JsonArray blocks && blocks[0]?["type"]?.GetValue<string>() == "tool_result")
            {
                var issued = messages[^2]!["content"]!.AsArray().First(b => b!["type"]!.GetValue<string>() == "tool_use")!["id"]!.GetValue<string>();
                if (blocks[0]!["tool_use_id"]!.GetValue<string>() != issued)
                {
                    await RespondAsync(context.Response, 400, Error("invalid_request_error", "tool_result does not match a tool_use id"));
                    return;
                }

                await RespondAsync(context.Response, 200, Message(model, """[{"type":"text","text":"It is 17°C in Paris."}]""", "end_turn", 40, 8));
                return;
            }

            if (prompt.Contains("weather"))
            {
                await RespondAsync(context.Response, 200, Message(model,
                    """[{"type":"tool_use","id":"toolu_sim_1","name":"get_weather","input":{"city":"Paris"}}]""", "tool_use", 30, 10));
                return;
            }

            if (prompt.Contains("count"))
            {
                await RespondAsync(context.Response, 200, Message(model, """[{"type":"text","text":"one, two, three"}]""", "max_tokens", 7, 16));
                return;
            }

            await RespondAsync(context.Response, 200, Message(model, """[{"type":"text","text":"pong"}]""", "end_turn", 9, 2));
        }

        private static async Task StreamAsync(HttpListenerResponse response, string model)
        {
            string[] events =
            [
                """{"type":"message_start","message":{"id":"msg_sim","type":"message","role":"assistant","model":"MODEL","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":21,"output_tokens":1}}}""".Replace("MODEL", model),
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Check"}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ing."}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_sim_2","name":"get_weather","input":{}}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Paris\"}"}}""",
                """{"type":"content_block_stop","index":1}""",
                """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":12}}""",
                """{"type":"message_stop"}""",
            ];

            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            foreach (var data in events)
            {
                var type = JsonNode.Parse(data)!["type"]!.GetValue<string>();
                var bytes = Encoding.UTF8.GetBytes($"event: {type}\ndata: {data}\n\n");
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }

            response.Close();
        }

        /// <summary>
        /// SigV4 as AWS checks it: rebuild the canonical request from what arrived, derive the signing key from the
        /// secret, and compare signatures. Scope must name this region and the <c>bedrock-mantle</c> service.
        /// </summary>
        private (string? Scope, string? Failure) VerifySignature(HttpListenerRequest request, string body)
        {
            var authorization = request.Headers["Authorization"];
            if (authorization is null || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
                return (null, "request is not SigV4-signed");

            var parts = authorization["AWS4-HMAC-SHA256 ".Length..]
                .Split(',', StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p[1]);

            var credential = parts["Credential"].Split('/', 2);
            if (credential[0] != _accessKey) return (null, $"unknown access key {credential[0]}");

            var scope = credential[1];
            var scopeParts = scope.Split('/');
            var (date, region, service) = (scopeParts[0], scopeParts[1], scopeParts[2]);
            if (region != _region) return (null, $"credential scope names region {region}, endpoint is {_region}");
            if (service != "bedrock-mantle") return (null, $"credential scope names service {service}, expected bedrock-mantle");

            var amzDate = request.Headers["x-amz-date"] ?? string.Empty;
            if (!amzDate.StartsWith(date, StringComparison.Ordinal)) return (null, "x-amz-date does not match the credential scope date");

            var payloadHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            var declared = request.Headers["x-amz-content-sha256"];
            if (declared is not null && declared != payloadHash) return (null, "x-amz-content-sha256 does not match the body");

            var signedHeaders = parts["SignedHeaders"];
            if (!signedHeaders.Split(';').Contains("host")) return (null, "host is not a signed header");

            var canonicalHeaders = string.Concat(signedHeaders.Split(';').Select(name =>
                $"{name}:{CollapseWhitespace(request.Headers[name] ?? string.Empty)}\n"));

            var canonicalRequest = string.Join('\n',
                request.HttpMethod,
                request.Url!.AbsolutePath,
                CanonicalQuery(request.Url.Query),
                canonicalHeaders,
                signedHeaders,
                payloadHash);

            var stringToSign = string.Join('\n',
                "AWS4-HMAC-SHA256", amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

            var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + _secretKey), date);
            key = Hmac(key, region);
            key = Hmac(key, service);
            key = Hmac(key, "aws4_request");
            var expected = Hex(Hmac(key, stringToSign));

            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["Signature"]))
                ? (scope, null)
                : (null, "signature does not match");
        }

        private static string CanonicalQuery(string query)
            => string.Join('&', query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Select(p => (Uri.EscapeDataString(Uri.UnescapeDataString(p[0])), Uri.EscapeDataString(Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty))))
                .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
                .Select(p => $"{p.Item1}={p.Item2}"));

        private static string CollapseWhitespace(string value)
            => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static string Message(string model, string content, string stopReason, int input, int output)
            => "{\"id\":\"msg_sim\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + model + "\",\"content\":" + content +
               ",\"stop_reason\":\"" + stopReason + "\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}";

        private static string Error(string type, string message)
            => new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } }.ToJsonString();

        private static async Task RespondAsync(HttpListenerResponse response, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
