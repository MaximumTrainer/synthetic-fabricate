using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Chat.Tools;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using Fabricate.Infrastructure.Repositories;
using Fabricate.Tests.Application;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #91: <see cref="AgentClarificationEvalTests"/> checks the prompt the provider receives and the harness's
/// handling of each shape of reply, but it cannot check the thing #87 actually claimed — that the agent asks
/// rather than guesses. That is the model's judgement, and a scripted client testing it would only be testing the
/// script. This drives the same fixtures through a real model.
///
/// <para>
/// It needs a real key, so it is opt-in: set <c>FABRICATE_LIVE_LLM_API_KEY</c> (or <c>ANTHROPIC_API_KEY</c>) to
/// run it, and it costs one API call per fixture. When the key is absent the run says so rather than passing
/// silently, because a behavioural eval that quietly does nothing is the failure mode this whole issue exists to
/// correct.
/// </para>
///
/// <para>
/// <strong>Why a rate and not an assertion per fixture.</strong> A model's reply is not deterministic, and a
/// suite that fails whenever one borderline prompt goes the other way would be turned off within a week. The
/// eval reports how many fixtures behaved as expected and fails only when the rate drops below
/// <see cref="MinimumPassRate"/> — a regression in the guidance, not a coin landing the other way up.
/// </para>
/// </summary>
public sealed class AgentClarificationLiveEvalTests(ITestOutputHelper output)
{
    /// <summary>
    /// Six of seven. One fixture going the other way on a given run is the model exercising judgement; two is the
    /// guidance no longer landing.
    /// </summary>
    private const double MinimumPassRate = 6d / 7d;

    /// <summary>
    /// Reads an environment variable, treating blank as absent.
    ///
    /// <para>
    /// This matters more than it looks. A workflow writes <c>FABRICATE_LIVE_LLM_API_KEY: ${{ secrets.… }}</c>
    /// unconditionally, so an unconfigured secret arrives as an <em>empty string</em>, not an absent variable — and
    /// on Linux .NET hands that back as <c>""</c> rather than <c>null</c>. A <c>is null</c> gate therefore lets it
    /// straight through, and the eval spends a scheduled run discovering that the provider rejects a blank
    /// credential (401). It also silently defeats the <c>ANTHROPIC_API_KEY</c> fallback below, because
    /// <c>"" ?? x</c> is <c>""</c>. The smoke suite's fixture already gates this way.
    /// </para>
    /// </summary>
    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ApiKey => Env("FABRICATE_LIVE_LLM_API_KEY") ?? Env("ANTHROPIC_API_KEY");

    private static string Model => Env("FABRICATE_LIVE_LLM_MODEL") ?? "claude-opus-5";

    /// <summary>
    /// Whether the caller has declared that this eval must really run. CI sets it once it has seen a key in the job
    /// environment, so a key that is configured but does not reach the test is a red run rather than a quiet pass —
    /// the same protection <c>SMOKE_REQUIRE_EXECUTION</c> gives the smoke suite (#61, #90, #91).
    /// </summary>
    private static bool ExecutionRequired =>
        Environment.GetEnvironmentVariable("FABRICATE_REQUIRE_LIVE_EVAL") == "1";

    [Fact]
    public async Task TheAgentAsksBeforeActingOnAnAmbiguousRequestAndProceedsOnASpecificOne()
    {
        if (ApiKey is null)
        {
            if (ExecutionRequired)
            {
                throw new InvalidOperationException(
                    "FABRICATE_REQUIRE_LIVE_EVAL=1 says this eval must call a real model, but neither " +
                    "FABRICATE_LIVE_LLM_API_KEY nor ANTHROPIC_API_KEY carries a value. Skipping here would " +
                    "report success for behaviour nothing checked.");
            }

            output.WriteLine(
                "Clarifying-question behaviour NOT exercised against a live model: set FABRICATE_LIVE_LLM_API_KEY " +
                "(or ANTHROPIC_API_KEY) to run it. The prompt contract and harness behaviour are covered offline " +
                "by AgentClarificationEvalTests.");
            return;
        }

        var results = new List<(AgentClarificationEvalTests.Fixture Fixture, Outcome Outcome, string Reply)>();

        foreach (var fixture in Fixtures())
        {
            var harness = new Harness(ApiKey, Model);
            var (userId, session) = await harness.CreateSessionAsync(fixture.Mode);

            var turn = await harness.Chat.SendMessageAsync(
                new SendMessageCommand(session.Id, userId, fixture.Prompt));

            var reply = turn.AssistantMessage?.Content ?? string.Empty;

            // Acting means the mutating tool was called — run or parked for review, either way the model committed
            // to a write. Reading the schema or stating a plan is not acting. Asking means it did not act and put a
            // question to the user. A reply that does neither (declines, stalls) matches no expectation: the first
            // version scored "no tool and no '?'" as proceeding, which credited a refusal as success (#91).
            var acted = turn.ToolInvocations.Any(t => t.ToolName == "generate_data");
            var outcome = acted ? Outcome.Proceeded : reply.Contains('?') ? Outcome.Asked : Outcome.Neither;

            results.Add((fixture, outcome, reply));
        }

        bool Matches((AgentClarificationEvalTests.Fixture Fixture, Outcome Outcome, string Reply) r)
            => r.Outcome == (r.Fixture.ExpectsQuestion ? Outcome.Asked : Outcome.Proceeded);

        var matched = results.Where(Matches).ToList();
        var rate = (double)matched.Count / results.Count;

        output.WriteLine($"Clarifying-question eval against {Model}: {matched.Count}/{results.Count} as expected.");
        foreach (var result in results)
        {
            var (fixture, outcome, reply) = result;
            var verdict = Matches(result) ? "ok  " : "MISS";
            var behaviour = outcome.ToString().ToLowerInvariant();
            output.WriteLine(
                $"  {verdict} {fixture.Name,-22} {fixture.Mode,-14} expected {(fixture.ExpectsQuestion ? "ask" : "proceed"),-7} got {behaviour}");
            output.WriteLine($"       {Excerpt(reply)}");
        }

        rate.Should().BeGreaterThanOrEqualTo(MinimumPassRate,
            "the clarifying-question guidance must still land; misses were {0}",
            string.Join(", ", results.Where(r => !Matches(r)).Select(r => r.Fixture.Name)));
    }

    private enum Outcome { Asked, Proceeded, Neither }

    private static IEnumerable<AgentClarificationEvalTests.Fixture> Fixtures()
        => AgentClarificationEvalTests.Fixtures
            .Select((object[] row) => (AgentClarificationEvalTests.Fixture)row[0]);

    private static string Excerpt(string reply)
    {
        var single = reply.ReplaceLineEndings(" ").Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }

    /// <summary>
    /// The real agent, wired to the real provider factory. Everything the agent persists is in-memory — the point
    /// is the model's reply, not the storage — but the prompt, the tools and the turn loop are production code.
    /// </summary>
    private sealed class Harness
    {
        private readonly WorkspaceService _workspaces;
        private readonly Guid _accountId = Guid.NewGuid();

        public AgentChatService Chat { get; }

        public Harness(string apiKey, string model)
        {
            var audit = new AuditLogService(new InMemoryAuditLogRepository(), new InMemoryAccountRepository());
            var workspaceRepo = new InMemoryWorkspaceRepository();
            _workspaces = new WorkspaceService(workspaceRepo, new InMemoryAccountGroupRepository(), audit);

            // The production tools' names, descriptions and input schemas, with the side effects removed. The first
            // live run (#91) used a stand-in generate_data with no input schema and no discover_schema at all, and
            // the model — correctly — declined to fire a write it could not parameterise, which the eval then
            // scored as "asked" or "proceeded" depending on whether the refusal happened to contain a '?'.
            var tools = new ToolRegistry();
            tools.Register(new StatePlanTool());
            tools.Register(new Inert(new DiscoverSchemaTool(null!), DiscoveredSchema));
            tools.Register(new Inert(new GenerateDataTool(null!), """{"status":"completed","runId":"eval-run"}"""));

            var credential = new ResolvedLlmCredential(
                LlmProvider.Anthropic, LlmCredentialKind.ApiKey, model, apiKey, null,
                new Dictionary<string, string>(), LlmCredentialSource.WorkspaceDefault);

            var httpClientFactory = new ServiceCollection().AddHttpClient()
                .BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            var factory = new ChatCompletionClientFactory(
                httpClientFactory, NullLoggerFactory.Instance, new LlmOptions());

            Chat = new AgentChatService(
                new InMemorySessionRepository(), tools, _workspaces,
                new InstructionVersionService(new InMemoryInstructionVersionRepository(), _workspaces),
                new FixedResolver(credential), factory, new HeuristicTokenBudgetEstimator(),
                new InMemoryLlmCredentialStore(), audit, workspaceRepo, new PromptDataBoundary(),
                new UnlimitedUsage(), new LlmOptions());
        }

        public async Task<(Guid UserId, ChatSession Session)> CreateSessionAsync(ChatMode mode)
        {
            var userId = Guid.NewGuid();
            var workspace = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "WS", userId));
            var session = await Chat.CreateSessionAsync(
                new CreateChatSessionCommand(workspace.Id, null, userId, "S", mode));
            return (userId, session);
        }
    }

    /// <summary>A small schema for <c>discover_schema</c> to return, so a model that looks first has something to see.</summary>
    private const string DiscoveredSchema = """
        {"tableCount":2,"tables":[
          {"name":"main.users","columns":[{"name":"id","kind":"Integer"},{"name":"email","kind":"Email"},{"name":"created_at","kind":"DateTime"}],"foreignKeys":[]},
          {"name":"main.orders","columns":[{"name":"id","kind":"Integer"},{"name":"user_id","kind":"Integer"},{"name":"total","kind":"Decimal"}],"foreignKeys":[{"name":"fk_orders_users","referencedTable":"main.users"}]}
        ]}
        """;

    /// <summary>
    /// A production tool as the model sees it — name, description, input schema, content class — that does
    /// nothing when called. What the model is shown is the thing under test, so it must not drift from what
    /// production advertises; only the side effect is replaced.
    /// </summary>
    private sealed class Inert(ITool shape, string result) : ITool
    {
        public string Name => shape.Name;
        public string Description => shape.Description;
        public string InputSchemaJson => shape.InputSchemaJson;
        public PromptContentClass ContentClass => shape.ContentClass;

        public Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class FixedResolver(ResolvedLlmCredential credential) : ILlmCredentialResolver
    {
        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken ct = default)
            => Task.FromResult<ResolvedLlmCredential?>(credential);

        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, Guid? userId, Guid? sessionId, LlmProvider? preferredProvider = null, CancellationToken ct = default)
            => Task.FromResult<ResolvedLlmCredential?>(credential);
    }

    private sealed class UnlimitedUsage : ILlmUsageService
    {
        public Task<LlmUsageSummary> GetWorkspaceUsageAsync(Guid workspaceId, Guid requestingUserId, DateTimeOffset? from = null, DateTimeOffset? to = null, LlmUsageGrouping groupBy = LlmUsageGrouping.Model, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmUsageSummary> GetAccountUsageAsync(Guid accountId, Guid requestingUserId, DateTimeOffset? from = null, DateTimeOffset? to = null, LlmUsageGrouping groupBy = LlmUsageGrouping.Model, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmBudgetVerdict> CheckBudgetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmBudgetVerdict.Allowed);
    }
}
