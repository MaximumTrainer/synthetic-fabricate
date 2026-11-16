using System.Text;
using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #86: every LLM adapter against the real provider. The unit suites check our mapping against wire formats we
/// hand-wrote from the documentation; these check it against what the provider actually sends back, which is
/// the only thing that catches documentation drift, an SDK behaviour change, or an auth detail that fails only
/// against the live service (the Azure <c>api-key</c> header that survived until #64 is the reference case).
///
/// <para>
/// <strong>Gating.</strong> Each provider self-skips unless its variables are present, so the default suite is
/// unaffected and spends nothing. A skip is written to the test output with the variables that were missing, so a
/// green run says which providers it exercised. Setting <c>FABRICATE_REQUIRE_LIVE_PROVIDERS</c> to a
/// comma-separated list of provider names turns a skip for any of those into a failure — the workflow sets it for
/// every provider whose secret it can see, so a secret that is configured but does not reach the test is red.
/// </para>
///
/// <para>
/// <strong>Cost.</strong> Every request is the smallest that still exercises its path: a one-word reply, a
/// 16-token cap, one tool with one string argument. A full run is six calls per provider. Measured on 2026-11-16
/// against the default <c>claude-haiku-5-5</c>: about 1,550 input and 160 output tokens, roughly 0.02 US cents.
/// The same calls on <c>claude-opus-5-5</c> come to about one cent. Other providers are of the same order at
/// their own list price. The usage of every call is printed, so the real figure is in each run's log.
/// </para>
/// </summary>
public sealed class LiveProviderTests(ITestOutputHelper output)
{
    private const string WeatherToolName = "get_weather";

    private static readonly LlmToolDefinition WeatherTool = new(
        WeatherToolName,
        "Returns the current weather for a city.",
        """{"type":"object","properties":{"city":{"type":"string","description":"City name, e.g. Paris"}},"required":["city"]}""");

    private const string ToolPrompt =
        "What is the weather in Paris right now? Call the get_weather tool to find out; do not guess.";

    public static TheoryData<string> Providers => new(LiveProvider.All.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task APlainCompletionReturnsTextUsageAndEndTurn(string provider)
    {
        if (Client(provider) is not { } live) return;

        var result = await live.Client.CompleteAsync(Request(live,
            [LlmMessage.User("Reply with exactly the single word: pong")], maxTokens: 512));
        Log(live, "completion", result);

        result.Text.Should().NotBeNull().And.Subject.ToLowerInvariant().Should().Contain("pong");
        result.StopReason.Should().Be(LlmStopReason.EndTurn);
        result.ToolCalls.Should().BeEmpty();
        AssertUsage(result);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ATruncatedReplyMapsToMaxTokens(string provider)
    {
        if (Client(provider) is not { } live) return;

        var result = await live.Client.CompleteAsync(Request(live,
            [LlmMessage.User("Write the numbers from 1 to 300 in words, separated by commas.")], maxTokens: 16));
        Log(live, "max-tokens", result);

        result.StopReason.Should().Be(LlmStopReason.MaxTokens);
        AssertUsage(result);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AToolCallRoundTripsToAFinalAnswer(string provider)
    {
        if (Client(provider) is not { } live) return;

        var first = await live.Client.CompleteAsync(Request(live, [LlmMessage.User(ToolPrompt)], tools: [WeatherTool]));
        Log(live, "tool-call", first);

        first.StopReason.Should().Be(LlmStopReason.ToolUse);
        var call = first.ToolCalls.Should().ContainSingle().Subject;
        call.Name.Should().Be(WeatherToolName);
        call.Id.Should().NotBeNullOrWhiteSpace();
        CityArgument(call).Should().ContainEquivalentOf("Paris");
        AssertUsage(first);

        // The second leg is where the request-side mapping is tested: the assistant's tool call and our result
        // must go back in the shape the provider expects, or it rejects the conversation outright.
        var second = await live.Client.CompleteAsync(Request(live,
        [
            LlmMessage.User(ToolPrompt),
            LlmMessage.Assistant(first.Text, first.ToolCalls),
            LlmMessage.FromToolResult(new LlmToolResult(call.Id, """{"city":"Paris","temperatureC":17,"conditions":"light rain"}""")),
        ], tools: [WeatherTool]));
        Log(live, "tool-result", second);

        second.StopReason.Should().Be(LlmStopReason.EndTurn);
        second.ToolCalls.Should().BeEmpty();
        second.Text.Should().Contain("17", "the final answer must be built from the tool result we returned");
        AssertUsage(second);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStreamedReplyAccumulatesTextAndEndsWithOneFinalChunk(string provider)
    {
        if (Client(provider) is not { } live) return;

        var (deltas, final, finals) = await Drain(live.Client.StreamAsync(Request(live,
            [LlmMessage.User("Count from 1 to 10, separated by single spaces. Output nothing else.")], maxTokens: 512)));
        Log(live, "stream-text", final, $"{deltas.Count} text deltas");

        finals.Should().Be(1, "exactly one chunk per stream carries the final result");
        deltas.Should().NotBeEmpty();
        string.Concat(deltas).Should().Be(final.Text, "the streamed deltas must add up to the final text");
        final.Text.Should().Contain("1").And.Contain("10");
        final.StopReason.Should().Be(LlmStopReason.EndTurn);
        AssertUsage(final);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStreamedToolCallReassemblesItsArgumentFragments(string provider)
    {
        if (Client(provider) is not { } live) return;

        var (_, final, finals) = await Drain(live.Client.StreamAsync(Request(live, [LlmMessage.User(ToolPrompt)], tools: [WeatherTool])));
        Log(live, "stream-tool", final);

        finals.Should().Be(1);
        final.StopReason.Should().Be(LlmStopReason.ToolUse);
        var call = final.ToolCalls.Should().ContainSingle().Subject;
        call.Name.Should().Be(WeatherToolName);
        call.Id.Should().NotBeNullOrWhiteSpace();
        CityArgument(call).Should().ContainEquivalentOf("Paris");
        AssertUsage(final);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────

    private LiveClient? Client(string providerName)
    {
        var provider = LiveProvider.All.Single(p => p.Name == providerName);
        var credential = provider.Resolve(out var missing);

        if (credential is null)
        {
            var message = $"[live-provider] {provider.Name}: SKIPPED — not exercised; missing {string.Join(", ", missing)}";
            if (LiveProvider.Required.Contains(provider.Name))
            {
                throw new InvalidOperationException(
                    $"FABRICATE_REQUIRE_LIVE_PROVIDERS names '{provider.Name}', but {string.Join(", ", missing)} " +
                    "carries no value. Skipping here would report a provider as verified when nothing called it.");
            }

            output.WriteLine(message);
            return null;
        }

        var httpClientFactory = new ServiceCollection().AddHttpClient()
            .BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var factory = new ChatCompletionClientFactory(httpClientFactory, NullLoggerFactory.Instance, new LlmOptions());

        output.WriteLine($"[live-provider] {provider.Name}: EXERCISED against {credential.Model}");
        return new LiveClient(provider.Name, credential.Model, factory.Create(credential));
    }

    private static ChatCompletionRequest Request(
        LiveClient live, IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null, int maxTokens = 1024)
        => new(live.Model, "You are a terse test fixture. Follow instructions exactly.", messages, tools ?? [], maxTokens,
            // Low effort keeps the cheap paths cheap; providers that do not take effort ignore it in the adapter.
            Effort: live.Client.Capabilities.SupportsEffort ? LlmEffort.Low : null);

    private static async Task<(List<string> Deltas, ChatCompletionResult Final, int Finals)> Drain(IAsyncEnumerable<ChatCompletionChunk> stream)
    {
        var deltas = new List<string>();
        ChatCompletionResult? final = null;
        var finals = 0;
        await foreach (var chunk in stream)
        {
            if (chunk.TextDelta is { Length: > 0 } delta) deltas.Add(delta);
            if (chunk.Final is not null)
            {
                final = chunk.Final;
                finals++;
            }
        }

        final.Should().NotBeNull("a stream must end with a final chunk");
        return (deltas, final!, finals);
    }

    private static string CityArgument(LlmToolCall call)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        doc.RootElement.TryGetProperty("city", out var city).Should().BeTrue($"the arguments were {call.ArgumentsJson}");
        return city.GetString() ?? string.Empty;
    }

    private static void AssertUsage(ChatCompletionResult result)
    {
        result.Usage.InputTokens.Should().BeGreaterThan(0, "the provider reports input usage on every response");
        result.Usage.OutputTokens.Should().BeGreaterThan(0, "the provider reports output usage on every response");
        // A bare identifier: the Anthropic adapter once reported the SDK enum's JSON form, quotes and all (#86).
        result.ModelId.Should().MatchRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@-]*$");
    }

    private void Log(LiveClient live, string step, ChatCompletionResult result, string? extra = null)
    {
        var text = (result.Text ?? string.Empty).ReplaceLineEndings(" ");
        if (text.Length > 80) text = text[..80] + "…";
        var sb = new StringBuilder($"[live-provider] {live.Name} {step,-12} stop={result.StopReason} ");
        sb.Append($"usage={result.Usage.InputTokens}in/{result.Usage.OutputTokens}out model={result.ModelId} ");
        if (result.ToolCalls.Count > 0) sb.Append($"tools=[{string.Join(", ", result.ToolCalls.Select(c => $"{c.Name}{c.ArgumentsJson}"))}] ");
        if (extra is not null) sb.Append(extra).Append(' ');
        sb.Append($"text=\"{text}\"");
        output.WriteLine(sb.ToString());
    }

    private sealed record LiveClient(string Name, string Model, IChatCompletionClient Client);

    /// <summary>
    /// One provider path and the variables that switch it on. Blank counts as absent: a workflow writes
    /// <c>${{ secrets.X }}</c> unconditionally, so an unset secret arrives as an empty string (#91).
    /// </summary>
    private sealed record LiveProvider(string Name, Func<Func<string, string?>, ResolvedLlmCredential?> Build, string[] Variables)
    {
        public static readonly LiveProvider[] All =
        [
            new("anthropic",
                env => Credential(LlmProvider.Anthropic, LlmCredentialKind.ApiKey,
                    env("FABRICATE_LIVE_ANTHROPIC_MODEL") ?? "claude-haiku-5-5",
                    (env("FABRICATE_LIVE_LLM_API_KEY") ?? env("ANTHROPIC_API_KEY"))!),
                ["FABRICATE_LIVE_LLM_API_KEY|ANTHROPIC_API_KEY"]),

            new("openai",
                env => Credential(LlmProvider.OpenAiCompatible, LlmCredentialKind.ApiKey,
                    env("FABRICATE_LIVE_OPENAI_MODEL")!, env("FABRICATE_LIVE_OPENAI_API_KEY")!,
                    env("FABRICATE_LIVE_OPENAI_BASE_URL") ?? "https://api.openai.com/v1"),
                ["FABRICATE_LIVE_OPENAI_API_KEY", "FABRICATE_LIVE_OPENAI_MODEL"]),

            new("azure-openai",
                // The model id doubles as the deployment name on Azure; the adapter builds the deployment route.
                env => Credential(LlmProvider.OpenAiCompatible, LlmCredentialKind.ApiKey,
                    env("FABRICATE_LIVE_AZURE_OPENAI_DEPLOYMENT")!, env("FABRICATE_LIVE_AZURE_OPENAI_API_KEY")!,
                    env("FABRICATE_LIVE_AZURE_OPENAI_ENDPOINT")),
                ["FABRICATE_LIVE_AZURE_OPENAI_ENDPOINT", "FABRICATE_LIVE_AZURE_OPENAI_API_KEY", "FABRICATE_LIVE_AZURE_OPENAI_DEPLOYMENT"]),

            new("bedrock",
                // Authenticates with the ambient AWS identity (AWS_ACCESS_KEY_ID / OIDC role) the SDK resolves.
                env => Credential(LlmProvider.AwsBedrock, LlmCredentialKind.CloudIdentity,
                    env("FABRICATE_LIVE_BEDROCK_MODEL") ?? "anthropic.claude-haiku-5-5", string.Empty,
                    settings: new() { ["region"] = env("FABRICATE_LIVE_BEDROCK_REGION")! }),
                ["FABRICATE_LIVE_BEDROCK_REGION"]),

            new("vertex",
                // Authenticates with Application Default Credentials.
                env => Credential(LlmProvider.GcpVertexAi, LlmCredentialKind.CloudIdentity,
                    env("FABRICATE_LIVE_VERTEX_MODEL") ?? "claude-haiku-5-5", string.Empty,
                    settings: new()
                    {
                        ["projectId"] = env("FABRICATE_LIVE_VERTEX_PROJECT_ID")!,
                        ["location"] = env("FABRICATE_LIVE_VERTEX_LOCATION") ?? "global",
                    }),
                ["FABRICATE_LIVE_VERTEX_PROJECT_ID"]),

            new("foundry",
                env => Credential(LlmProvider.AzureFoundry, LlmCredentialKind.ApiKey,
                    env("FABRICATE_LIVE_FOUNDRY_MODEL") ?? "claude-haiku-5-5", env("FABRICATE_LIVE_FOUNDRY_API_KEY")!,
                    settings: new() { ["resourceName"] = env("FABRICATE_LIVE_FOUNDRY_RESOURCE")! }),
                ["FABRICATE_LIVE_FOUNDRY_RESOURCE", "FABRICATE_LIVE_FOUNDRY_API_KEY"]),
        ];

        public static IReadOnlySet<string> Required { get; } =
            (Env("FABRICATE_REQUIRE_LIVE_PROVIDERS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public ResolvedLlmCredential? Resolve(out IReadOnlyList<string> missing)
        {
            // A variable written "A|B" is satisfied by either.
            missing = Variables.Where(v => v.Split('|').All(name => Env(name) is null)).ToArray();
            return missing.Count == 0 ? Build(Env) : null;
        }

        private static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static ResolvedLlmCredential Credential(
            LlmProvider provider, LlmCredentialKind kind, string model, string secret,
            string? endpoint = null, Dictionary<string, string>? settings = null)
            => new(provider, kind, model, secret, endpoint, settings ?? [], LlmCredentialSource.WorkspaceDefault);
    }
}
