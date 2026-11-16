using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// <see cref="IChatCompletionClient"/> over the official Anthropic SDK. Used for the first-party API and, through the
/// same <see cref="IAnthropicClient"/> surface, for Claude on Bedrock, Vertex AI and Foundry.
/// Current Claude models reject sampling parameters and <c>budget_tokens</c>, so neither is ever sent; thinking is adaptive.
/// </summary>
public sealed class AnthropicChatCompletionClient(IAnthropicClient client, string providerId) : IChatCompletionClient
{
    public string ProviderId { get; } = providerId;

    public ModelCapabilities Capabilities { get; } = new(
        SupportsSampling: false,
        SupportsStreaming: true,
        SupportsToolCalling: true,
        SupportsEffort: true,
        SupportsStructuredOutput: true,
        MaxOutputTokens: 64_000);

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = BuildParameters(request);
        Message response;
        try
        {
            response = await client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }

        return MapResponse(response);
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = BuildParameters(request);
        var text = new StringBuilder();
        var toolCalls = new SortedDictionary<long, (string Id, string Name, StringBuilder Json)>();
        var stopReason = LlmStopReason.EndTurn;
        string? stopDetail = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var modelId = request.Model;

        // The SDK stream is pulled manually so its exceptions can be translated; a yield cannot live inside a catch.
        IAsyncEnumerator<RawMessageStreamEvent> events;
        try
        {
            events = client.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }

        await using (events.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Translate(ex);
                }

                if (!moved) break;
                var evt = events.Current;

                if (evt.TryPickStart(out var start))
                {
                    modelId = ModelIdOf(start.Message.Model) ?? modelId;
                    inputTokens = (int)(start.Message.Usage.InputTokens);
                    continue;
                }

                if (evt.TryPickContentBlockStart(out var blockStart))
                {
                    if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        toolCalls[blockStart.Index] = (toolUse.ID, toolUse.Name, new StringBuilder());
                    }
                    continue;
                }

                if (evt.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var textDelta))
                    {
                        text.Append(textDelta.Text);
                        yield return new ChatCompletionChunk(textDelta.Text);
                    }
                    else if (blockDelta.Delta.TryPickInputJson(out var jsonDelta) && toolCalls.TryGetValue(blockDelta.Index, out var call))
                    {
                        call.Json.Append(jsonDelta.PartialJson);
                    }
                    continue;
                }

                if (evt.TryPickDelta(out var messageDelta))
                {
                    outputTokens = (int)messageDelta.Usage.OutputTokens;
                    (stopReason, stopDetail) = MapStop(messageDelta.Delta.StopReason, messageDelta.Delta.StopDetails);
                }
            }
        }

        var calls = toolCalls.Values
            .Select(c => new LlmToolCall(c.Id, c.Name, c.Json.Length == 0 ? "{}" : c.Json.ToString()))
            .ToArray();

        if (calls.Length > 0 && stopReason == LlmStopReason.EndTurn)
            stopReason = LlmStopReason.ToolUse;

        yield return new ChatCompletionChunk(null, new ChatCompletionResult(
            text.Length == 0 ? null : text.ToString(),
            calls,
            stopReason,
            new TokenUsage(inputTokens, outputTokens),
            modelId,
            stopDetail));
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────

    private static MessageCreateParams BuildParameters(ChatCompletionRequest request)
    {
        return new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Messages = request.Messages.Select(ToMessageParam).ToList(),
            Thinking = new ThinkingConfigAdaptive(),
            System = string.IsNullOrWhiteSpace(request.SystemInstructions) ? null : (MessageCreateParamsSystem?)request.SystemInstructions,
            Tools = request.Tools.Count > 0 ? request.Tools.Select(ToTool).ToList() : null,
            OutputConfig = request.Effort is { } effort ? new OutputConfig { Effort = ToEffort(effort) } : null,
        };
    }

    private static Effort ToEffort(LlmEffort effort) => effort switch
    {
        LlmEffort.Low => Effort.Low,
        LlmEffort.Medium => Effort.Medium,
        LlmEffort.Max => Effort.Max,
        _ => Effort.High,
    };

    private static ToolUnion ToTool(LlmToolDefinition definition)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(definition.InputSchemaJson) ? "{}" : definition.InputSchemaJson);
        var root = doc.RootElement;

        Dictionary<string, JsonElement>? properties = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            properties = props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }

        List<string>? required = null;
        if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            required = req.EnumerateArray().Select(r => r.GetString()).Where(r => r is not null).Cast<string>().ToList();
        }

        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = new InputSchema
            {
                Properties = properties,
                Required = required,
            },
        };
    }

    private static MessageParam ToMessageParam(LlmMessage message)
    {
        switch (message.Role)
        {
            case LlmMessageRole.User:
                return new MessageParam { Role = Role.User, Content = message.Text ?? string.Empty };

            case LlmMessageRole.Tool:
                var result = message.ToolResult ?? throw new ArgumentException("Tool messages require a tool result.");
                return new MessageParam
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ToolResultBlockParam { ToolUseID = result.ToolCallId, Content = result.Content, IsError = result.IsError },
                    },
                };

            default:
                var blocks = new List<ContentBlockParam>();
                if (!string.IsNullOrEmpty(message.Text))
                    blocks.Add(new TextBlockParam { Text = message.Text });
                foreach (var call in message.ToolCalls ?? [])
                {
                    blocks.Add(new ToolUseBlockParam
                    {
                        ID = call.Id,
                        Name = call.Name,
                        Input = ParseArguments(call.ArgumentsJson),
                    });
                }
                if (blocks.Count == 0)
                    blocks.Add(new TextBlockParam { Text = "(no content)" });
                return new MessageParam { Role = Role.Assistant, Content = blocks };
        }
    }

    private static Dictionary<string, JsonElement> ParseArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ChatCompletionResult MapResponse(Message response)
    {
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text.Append(textBlock.Text);
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                calls.Add(new LlmToolCall(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            }
        }

        var (stopReason, stopDetail) = MapStop(response.StopReason, response.StopDetails);
        if (calls.Count > 0 && stopReason == LlmStopReason.EndTurn)
            stopReason = LlmStopReason.ToolUse;

        return new ChatCompletionResult(
            text.Length == 0 ? null : text.ToString(),
            calls,
            stopReason,
            new TokenUsage((int)response.Usage.InputTokens, (int)response.Usage.OutputTokens),
            ModelIdOf(response.Model) ?? string.Empty,
            stopDetail);
    }

    /// <summary>
    /// The SDK's Model is an enum-struct like StopReason: its ToString() is the JSON form, quotes included, so it
    /// reported <c>"claude-opus-5"</c> with the quotes as the model id. Raw() is the wire value. Found by the live
    /// provider suite (#86); the canned-response tests never asserted the id.
    /// </summary>
    private static string? ModelIdOf(ApiEnum<string, Model> model)
        => model.Raw();

    /// <summary>The SDK's StopReason is an enum-struct; its JSON form is the wire value, its ToString() is not.</summary>
    private static (LlmStopReason, string?) MapStop(ApiEnum<string, StopReason>? stopReason, RefusalStopDetails? details)
    {
        // Raw() is the wire value ("refusal", "tool_use", ...); Value() would throw on an enum member this SDK build predates.
        var wire = stopReason?.Raw()?.ToString();
        var reason = wire switch
        {
            "tool_use" => LlmStopReason.ToolUse,
            "max_tokens" => LlmStopReason.MaxTokens,
            "refusal" => LlmStopReason.Refusal,
            _ => LlmStopReason.EndTurn,
        };

        string? detail = null;
        if (reason == LlmStopReason.Refusal && details is not null)
        {
            detail = string.Join(": ", new[] { details.Category?.ToString(), details.Explanation }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return (reason, detail);
    }

    /// <summary>Most-specific first: the SDK defines a class per status so retryable and non-retryable failures stay distinct.</summary>
    internal static LlmProviderException Translate(Exception ex) => ex switch
    {
        LlmProviderException already => already,
        AnthropicUnauthorizedException => new LlmProviderException(LlmFailureKind.Authentication, "The provider rejected the credential (401).", ex),
        AnthropicForbiddenException => new LlmProviderException(LlmFailureKind.Authentication, "The provider refused access with this credential (403).", ex),
        AnthropicRateLimitException => new LlmProviderException(LlmFailureKind.RateLimited, "The provider rate-limited the request (429).", ex),
        AnthropicNotFoundException => new LlmProviderException(LlmFailureKind.InvalidRequest, "The requested model or endpoint was not found (404).", ex),
        AnthropicBadRequestException bad => new LlmProviderException(
            bad.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) || bad.Message.Contains("context", StringComparison.OrdinalIgnoreCase)
                ? LlmFailureKind.ContextLengthExceeded
                : LlmFailureKind.InvalidRequest,
            "The provider rejected the request as invalid (400).", ex),
        Anthropic4xxException => new LlmProviderException(LlmFailureKind.InvalidRequest, "The provider rejected the request.", ex),
        Anthropic5xxException => new LlmProviderException(LlmFailureKind.ProviderError, "The provider reported an internal error.", ex),
        AnthropicIOException or HttpRequestException => new LlmProviderException(LlmFailureKind.Transport, "Could not reach the provider.", ex),
        TimeoutException => new LlmProviderException(LlmFailureKind.Timeout, "The provider did not respond in time.", ex),
        AnthropicException => new LlmProviderException(LlmFailureKind.ProviderError, "The provider call failed.", ex),
        _ => new LlmProviderException(LlmFailureKind.ProviderError, "The provider call failed unexpectedly.", ex),
    };
}
