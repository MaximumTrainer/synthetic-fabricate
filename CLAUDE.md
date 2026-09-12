# Fabricate

Synthetic data generation: discover a schema, profile it without copying it, generate data that matches its
shape, export it. A .NET 10 solution with a CLI, a REST API and a TypeScript SDK.

## Build and test — read this first

**Clear two environment variables before every `dotnet` command.** The VS Code C# extension leaks
`MSBuildSDKsPath` and `MSBUILD_EXE_PATH` into the shell, which pins MSBuild to SDK 8.0.302 and makes every project
fail with `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0` — even though `global.json`
pins 10.0.204 and it is installed. The variables win over `global.json`, so the error is a lie about what is
installed.

```powershell
$env:MSBuildSDKsPath=$null; $env:MSBUILD_EXE_PATH=$null; dotnet build
```

Shell state does not persist between tool calls, so this prefix goes on **each** invocation.

**Never run tests against stale binaries.** `dotnet test --no-build` will happily run the previous build's
output, so a fix can appear to fail and a broken change can appear to pass. Gate on the build first:

```powershell
$env:MSBuildSDKsPath=$null; $env:MSBUILD_EXE_PATH=$null; dotnet build 2>&1 | Select-String "Build succeeded"
```

A full `dotnet test Fabricate.Tests` takes ~13 minutes because of the container-backed suites. Filter while
iterating (`--filter "FullyQualifiedName~Foo"`), then run the whole thing once before committing.

`FABRICATE_SKIP_DOCKER_TESTS=1` skips every container-backed suite.

## Language and framework constraints

- **.NET 10, `LangVersion` 12.0.** C# 13+ features do not compile. In particular: no partial properties, no
  `System.Threading.Lock`. `[GeneratedRegex]` goes on a partial **method**, not a property.
- EF Core 9.0.10 with **two migration sets** — `FabricateDbContext` (SQLite) and `FabricatePostgresDbContext`
  (PostgreSQL). A model change needs a migration in both, or one provider breaks at startup.
- Tests are xUnit v2 with FluentAssertions (Xceed community licence — the licence banner on every run is
  expected, not a failure).

## Architecture

Hexagonal. The dependency rule is enforced by tests in `Fabricate.Tests/Architecture/`.

| Project | Holds |
| --- | --- |
| `Fabricate.Domain` | Records and enums. No dependencies. |
| `Fabricate.Application` | Services and **every port interface**, all in `Abstractions/Ports.cs`. |
| `Fabricate.Infrastructure` | Every adapter, and the only project that references a vendor SDK. |
| `Fabricate.Api` | Minimal-API endpoints and hosted services. |
| `Fabricate.Cli` | Command-line entry point. |

A new capability is a port in `Ports.cs` plus an adapter in `Fabricate.Infrastructure`. Vendor SDK types must not
appear in `Application` or `Domain`.

### Traps that have bitten this codebase

- **Captive dependencies.** A singleton must not consume a scoped repository. Take `IServiceScopeFactory` and
  open a scope per call, or inject a delegate. This shipped a silent data-loss bug once (#78).
- **Null-role authorization.** `null < WorkspaceRole.Editor` is `false`, so a null role passes a naive check.
  Compare explicitly. Six sites had this bypass (#66).
- **Minimal API model binding.** Any parameter type that declares a `BindAsync` method is treated as a custom
  model binder and silently stops working as a service. Name such methods something else.
- **Streaming responses.** `Utf8JsonWriter` and `StreamWriter` flush synchronously, which Kestrel rejects. Write
  through `Response.BodyWriter` (a `PipeWriter`).

## Docs

Every `docs/**/*.md` has a **hand-maintained** `.html` twin that must be updated alongside it. The twin is a full
page: `<head>`, `<nav>`, `<div class="page">`…`</div>`, `<footer>`.

Edit the changed section by hand. Do **not** regenerate the page from the markdown — a naive converter mangles
ordered lists, splits tables at blank lines, and rewrites `.html` links to `.md`. Inside `<pre><code>` quotes are
`&quot;`; in prose an apostrophe stays raw. Links point at `.html` from HTML and `.md` from markdown.

## Tests

The house rule is **a green run must never be mistakable for coverage**. This codebase has repeatedly shipped
tests that passed while doing nothing: five artifact-store tests skipped silently (#90); three NoSQL profilers
never ran at all (#91); a discovery suite iterated collections that were empty even when its gate opened.

So: every self-skipping suite carries a test that fails when its fixture did not start, and reports what was
exercised versus skipped. See `Fabricate.Tests/Integration/NoSqlEmulatorTests.cs` for the shape.

Prefer an emulator to a cloud account. Emulators in use: `mongo:7`, `postgres:16`, `amazon/dynamodb-local`, the
Google Cloud CLI Firestore emulator, `azure-cosmos-emulator:vnext-preview`, MinIO, Azurite, fake-gcs-server.

## Commits

`type(scope): imperative summary (#issue)` — e.g. `feat(nosql): aggregate-only data profilers (#71)`.

The body is prose explaining **why**, and names any defect the work uncovered. Trailer:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

## Shared agent skills

Shared skills live in [MaximumTrainer/agent-skills](https://github.com/MaximumTrainer/agent-skills). Before writing a new
skill, runbook or repeated procedure, check the catalogue - and send genuinely
general improvements back so the other repositories get them too.

```bash
python3 .claude/skills/skill-exchange/scripts/skills.py list
python3 .claude/skills/skill-exchange/scripts/skills.py status
```

See `.claude/skills/skill-exchange/` for the workflow.
