# CI secrets for integration tests

Most of Fabricate's integration tests need no secret at all. The database adapters run against **emulators and
containers** in the main [`ci`](../../.github/workflows/ci.yml) job — PostgreSQL, MongoDB, DynamoDB Local, the
Firestore emulator, MinIO, Azurite and fake-gcs-server — so they run on every push, and on a laptop with Docker,
without anyone holding a cloud account.

What is left needs either an account nobody can emulate or money per run, and lives in the
[`integration-tests`](../../.github/workflows/integration-tests.yml) workflow: it runs weekly and on manual
dispatch, never on pull requests from forks, because secrets are not available there.

Every gated test **states whether it was exercised** in its output. A suite that quietly does nothing is the
failure this arrangement exists to avoid, so absence is reported rather than inferred from a green run.

## What runs with no credentials

| Suite | What it needs |
| --- | --- |
| `NoSqlProfilerTests` (MongoDB) | Docker — `mongo:7` |
| `NoSqlEmulatorTests` (DynamoDB) | Docker — `amazon/dynamodb-local` |
| `NoSqlEmulatorTests` (Firestore) | Docker — the Google Cloud CLI emulators image |
| `CloudArtifactStoreTests` (Azure Blob, GCS) | Docker — Azurite and fake-gcs-server |
| `S3ArtifactStoreTests` | Docker — MinIO |
| `PostgresPersistenceIntegrationTests` | Docker — `postgres:16` |

Set `FABRICATE_SKIP_DOCKER_TESTS=1` to skip all of them where no Docker daemon is available. Without that
variable, each one reports that it could not start rather than passing silently.

## PostgreSQL (schema discovery)

`PostgreSqlSchemaProviderTests` reads `FABRICATE_POSTGRES_TEST_CONNECTION`. The workflow starts a `postgres:16`
service container and points the variable at it; the same variable enables the suite locally:

```bash
export FABRICATE_POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=fabricate_test"
dotnet test --filter "FullyQualifiedName~PostgreSql"
```

## Azure Cosmos DB (issue #53)

Cosmos has an emulator, and the suite uses the `vnext-preview` image, which serves in about ten seconds. It is
opt-in rather than part of every push because the image is still 1.7 GB to pull:

```bash
export FABRICATE_COSMOS_EMULATOR=1
dotnet test --filter "FullyQualifiedName~NoSqlEmulatorTests"
```

The weekly workflow sets it. With the variable unset, the Cosmos tests print
`Cosmos DB NOT exercised` and the suite's coverage report names it as not run.

Two connection-string keys exist for reaching an emulator: `ConnectionMode=Gateway` (also the mode to use from
behind a restrictive egress policy) and `DisableServerCertificateValidation=True`, which is accepted **only** for
a loopback endpoint, so it cannot weaken a connection to a real account.

The emulator serves plain HTTP unless started with `--protocol https`. Pointing a Cosmos client at `https://`
without it fails with "the SSL connection could not be established", which reads like a certificate problem and
is really a protocol mismatch — the suite asks for HTTPS so that the certificate exemption stays under test.

No account is needed for CI. If you want to point the suite at a real account instead, supply a full connection
string in place of the emulator's.

## The clarifying-question eval (issues #87, #91)

Whether the agent asks before acting is the model's judgement, so it can only be checked against a real model.

| Secret / variable | Description |
| --- | --- |
| `FABRICATE_LIVE_LLM_API_KEY` | An Anthropic API key. `ANTHROPIC_API_KEY` is also read, for a local run. |
| `FABRICATE_LIVE_LLM_MODEL` | *(optional, a repository variable)* Model id; defaults to `claude-opus-5`. Last measured rates per model are in the [self-hosting guide](self-hosting.md#whether-the-agent-asks-before-it-acts). |
| `FABRICATE_REQUIRE_LIVE_EVAL` | *(set by CI)* `1` turns the eval's self-skip into a failure. |

It costs one API call per fixture — seven at present — and reports a **pass rate** rather than asserting each
fixture, because a model's reply is not deterministic and a suite that fails on one borderline prompt would be
switched off within a week. It fails when the rate falls below 6/7, which is a regression in the guidance rather
than a coin landing the other way up.

```bash
export FABRICATE_LIVE_LLM_API_KEY="sk-ant-…"
dotnet test --filter "FullyQualifiedName~AgentClarificationLiveEvalTests" --logger "console;verbosity=detailed"
```

Without the key the test prints that behaviour was **not** verified and points at
`AgentClarificationEvalTests`, which covers the prompt contract and the harness offline.

### Blank is not the same as absent

A workflow writes `FABRICATE_LIVE_LLM_API_KEY: ${{ secrets.FABRICATE_LIVE_LLM_API_KEY }}` unconditionally, so an
unconfigured secret reaches the job as an **empty string** rather than an absent variable — and on Linux .NET
returns that as `""`, not `null`. A gate written as `is null` therefore lets it straight through, and the eval
spends the run discovering that the provider rejects a blank credential with a 401. That is what turned the
scheduled run red for a secret nobody had set. The gate treats blank as absent, which also restores the
`ANTHROPIC_API_KEY` fallback above — `"" ?? x` is `""`, so the fallback never fired either.

The mirror image of that mistake is a key that *is* configured but does not reach the test — a secret scoped to
the wrong environment, say. That would skip quietly and report success. `FABRICATE_REQUIRE_LIVE_EVAL=1` closes it:
the workflow sets it once it has seen a key in the job environment, and the skip becomes a failure. It is the same
protection `SMOKE_REQUIRE_EXECUTION` gives the smoke suite, and it lives in the test rather than the YAML so it
cannot be lost by editing the workflow.

## The live-provider suite (issue #86)

`LiveProviderTests` runs every LLM adapter against the real provider — completion, tool round trip, streamed text,
streamed tool-input fragments, usage and stop reason — so documentation drift or an auth detail that only fails
live is caught on a schedule rather than at the next incident. The `live-provider-tests` workflow runs it weekly
and on dispatch, in the `integration` environment. Each provider self-skips unless its variables are set:

| Provider | Secrets | Variables |
| --- | --- | --- |
| `anthropic` | `FABRICATE_LIVE_LLM_API_KEY` (shared with the eval) | `FABRICATE_LIVE_ANTHROPIC_MODEL` (default `claude-haiku-5-5`) |
| `openai` | `FABRICATE_LIVE_OPENAI_API_KEY` | `FABRICATE_LIVE_OPENAI_MODEL` (required) |
| `azure-openai` | `FABRICATE_LIVE_AZURE_OPENAI_API_KEY` | `FABRICATE_LIVE_AZURE_OPENAI_ENDPOINT`, `FABRICATE_LIVE_AZURE_OPENAI_DEPLOYMENT` |
| `bedrock` | `FABRICATE_LIVE_AWS_ACCESS_KEY_ID`, `FABRICATE_LIVE_AWS_SECRET_ACCESS_KEY` | `FABRICATE_LIVE_BEDROCK_REGION`, `FABRICATE_LIVE_BEDROCK_MODEL` (default `anthropic.claude-haiku-5-5`) |
| `vertex` | `FABRICATE_LIVE_GCP_CREDENTIALS_JSON` | `FABRICATE_LIVE_VERTEX_PROJECT_ID`, `FABRICATE_LIVE_VERTEX_LOCATION` (default `global`), `FABRICATE_LIVE_VERTEX_MODEL` |
| `foundry` | `FABRICATE_LIVE_FOUNDRY_API_KEY` | `FABRICATE_LIVE_FOUNDRY_RESOURCE`, `FABRICATE_LIVE_FOUNDRY_MODEL` |

The workflow passes the providers it has credentials for as `FABRICATE_REQUIRE_LIVE_PROVIDERS`, which turns a
skip for any of them into a failure, and writes an exercised-versus-skipped table to the job summary. Every request
is the smallest that exercises its path; a full run is six calls per provider, about 0.02 US cents on the default
Claude model.

```bash
export ANTHROPIC_API_KEY="sk-ant-…"
dotnet test --filter "FullyQualifiedName~LiveProviderTests" --logger "console;verbosity=detailed"
```

Each call prints its stop reason, usage and model id on a `[live-provider]` line. Deliberately breaking a mapping
fails the matching test: sending back the wrong `tool_use_id` makes the tool round trip fail with a 400.

## Which gates which

| Test class | Gate |
| --- | --- |
| `PostgreSqlSchemaProviderTests` | `FABRICATE_POSTGRES_TEST_CONNECTION` |
| `NoSqlEmulatorTests` (DynamoDB, Firestore) | Docker daemon reachable |
| `NoSqlEmulatorTests` (Cosmos DB) | `FABRICATE_COSMOS_EMULATOR=1` + Docker |
| `NoSqlProfilerTests` (MongoDB) | Docker daemon reachable |
| `AgentClarificationLiveEvalTests` | `FABRICATE_LIVE_LLM_API_KEY` or `ANTHROPIC_API_KEY` |
| `LiveProviderTests` | Per provider; see [the live-provider suite](#the-live-provider-suite-issue-86) |

A secret that is present but wrong produces a **failing** test, not a skipped one, so misconfiguration is visible.
A secret that is present but **blank** counts as absent, not as wrong — see above.

## Hygiene

- Never print secrets in workflow logs; the workflow only echoes which gates are enabled.
- Rotate keys on a schedule and after any contributor with access leaves.
- The Cosmos emulator key in the test fixture is Microsoft's published, non-secret emulator key. It is not a
  credential and grants access to nothing outside the container.
