# Self-hosting Fabricate

Fabricate runs entirely on infrastructure you control. It ships with **no embedded credentials**: you bring your own
LLM key (or cloud identity), your own PostgreSQL, and your own API key for the first login. This guide covers the
configuration contract, the one-command local setup, the Fly.io reference deployment, the documented alternatives, and
what leaves the instance over the network.

## Configuration contract

Everything is configured by environment variables, so the same image runs unchanged on any host.

### Core

| Variable | Required | Purpose |
| --- | --- | --- |
| `FABRICATE__BootstrapApiKey` | yes, first run | Seeds a bootstrap account and API key so you can authenticate. Generate with `openssl rand -base64 32`. |
| `FABRICATE_DB_PROVIDER` | yes | `postgres` for any hosted deployment; `sqlite` for local experiments; unset = in-memory (state lost on restart). |
| `FABRICATE_CONNECTION_STRING` | with `postgres` | PostgreSQL connection string. Carries credentials — set it as a **secret**. |
| `FABRICATE_DATA_PROTECTION_KEY_STORE` | no | `filesystem` (default) or `database`. See [Key ring](#key-ring) — the default is wrong on any platform with an ephemeral disk. |
| `FABRICATE_DATA_PROTECTION_KEYS_PATH` | with `filesystem` | Directory for the Data Protection key ring that encrypts tenant LLM credentials. Must persist across restarts and be shared across instances. |
| `FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED` | with `database`, no KEK | Acknowledges that an unwrapped key ring in the application database means one dump decrypts every tenant secret. Startup is refused without it. |
| `FABRICATE_DATA_PROTECTION_KEK` | no | `none` (default) or `aws-kms`. Wraps the key ring with a key-encryption key, which is what makes the `database` store safe. |
| `FABRICATE_DATA_PROTECTION_KMS_KEY_ID` | with `aws-kms` | KMS key id, ARN or alias. The instance needs `kms:GenerateDataKey` and `kms:Decrypt` on it. |
| `FABRICATE_DATA_PROTECTION_KMS_REGION` | no | Region for KMS. Falls back to the ambient AWS configuration. |
| `ASPNETCORE_URLS` | no | Defaults to `http://+:8080` in the image. TLS is terminated by your platform. |
| `FABRICATE_API_RATE_LIMIT_PER_MINUTE` | no | Requests per minute per API key across every authenticated route (default 100). `/healthz` and Swagger are exempt. Exceeding it returns `429` with `Retry-After`. |
| `FABRICATE_ARTIFACTS_PATH` | no | Directory for generated artifacts (CSV/JSON/SQL/Parquet). Defaults to the OS temp directory, which is ephemeral on every hosted platform; mount a volume here if artifacts must survive a restart, or use object storage (below). |
| `FABRICATE_ARTIFACT_STORE` | no | `filesystem` (default) or `s3`. See [Artifact storage](#artifact-storage). |
| `FABRICATE_ARTIFACT_BUCKET` | with `s3` | Bucket generated artifacts are written to. |
| `FABRICATE_ARTIFACT_S3_ENDPOINT` | for non-AWS | Endpoint URL for MinIO, Cloudflare R2 or Backblaze B2. Unset means AWS. |
| `FABRICATE_ARTIFACT_S3_REGION` | for AWS | Region. Still required by request signing even where the store ignores it. |
| `FABRICATE_ARTIFACT_S3_FORCE_PATH_STYLE` | for non-AWS | `true` for MinIO and most S3-compatible stores, which do not support virtual-host addressing. |
| `FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET` | no | Secret **name** holding the access key. Omit both key variables to use ambient cloud identity. |
| `FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET` | no | Secret **name** holding the secret key. |
| `FABRICATE_ARTIFACT_AZURE_ACCOUNT_URL` | with `azure-blob` | Storage account URL, e.g. `https://acme.blob.core.windows.net`. Used with managed identity. |
| `FABRICATE_ARTIFACT_AZURE_CONNECTION_STRING_SECRET` | no | Secret **name** holding a connection string, for running outside Azure. Omit to use managed identity. |
| `FABRICATE_ARTIFACT_GCS_PROJECT_ID` | no | GCP project id. Usually supplied by Application Default Credentials. |
| `FABRICATE_ARTIFACT_GCS_CREDENTIALS_SECRET` | no | Secret **name** holding service-account key JSON, for running outside GCP. Omit to use ADC. |
| `FABRICATE_ARTIFACT_RETENTION_DAYS` | no | Days to keep generated artifacts. Default `0` keeps them. |

| `FABRICATE_AUDIT_RETENTION_DAYS` | no | Days of audit history to keep. **Default `0` keeps everything**, so an existing deployment never starts deleting on upgrade. Set it and a background sweep purges anything older; see [Audit retention](#audit-retention). |
| `FABRICATE_AUDIT_SWEEP_MINUTES` | no | How often the retention sweep runs (default 360, six hours). Only read when retention is enabled. |
| `FABRICATE_AUDIT_PURGE_BATCH_SIZE` | no | Rows deleted per statement (default 1000), so clearing a long backlog does not hold one long write lock. |
| `FABRICATE_API_USAGE_SAMPLING` | no | Fraction of authenticated requests recorded as `api.request` audit events, 0.0–1.0. Default `1.0` records every request; `0` switches per-request usage auditing off. A busy deployment that wants the signal without the volume can sample — but note a sampled log cannot answer "did this key call that endpoint", only "how often, roughly". |

### Audit retention

Audit events are insert-only and otherwise grow forever, which on a hosted deployment eventually costs more than
the log is worth. Retention is **off by default**: `FABRICATE_AUDIT_RETENTION_DAYS=0` keeps every event.

Setting it to a positive number starts a background sweep that deletes events strictly older than the window,
across every account, in batches. An event exactly on the boundary is kept. The sweep audits itself as
`audit.retention_applied`, recording the window, the cutoff and the number removed — and records nothing when it
deleted nothing, so an idle sweep does not become its own source of growth.

```bash
FABRICATE_AUDIT_RETENTION_DAYS=90    # keep a quarter of history
FABRICATE_AUDIT_SWEEP_MINUTES=360    # sweep every six hours (the default)
```

Deleting audit history may itself be regulated in your jurisdiction. Export before you shorten a window — see
[Exporting the audit log](../user-guide.md#exporting-the-audit-log) — and keep the export somewhere the retention
window does not reach.

Pending EF Core migrations are applied automatically at startup, so a fresh database needs no manual step. Several
instances starting at once apply the schema exactly once (EF Core's migration lock).

### Operator (platform) LLM credential — `FABRICATE_LLM_*`

Optional. Leave `FABRICATE_LLM_PROVIDER` unset and the platform credential is disabled; workspaces can still
[bring their own keys](byok-llm-credentials.md). If it **is** set and invalid, the API refuses to start and names the
offending variable.

| Variable | Required | Purpose |
| --- | --- | --- |
| `FABRICATE_LLM_PROVIDER` | to enable | `anthropic` \| `openai-compatible` \| `bedrock` \| `vertex` \| `foundry` |
| `FABRICATE_LLM_MODEL` | yes | Default model id, e.g. `claude-opus-5` |
| `FABRICATE_LLM_ALLOWED_MODELS` | yes | Comma-separated allowlist; `FABRICATE_LLM_MODEL` must be a member. Also constrains models that workspaces may register. |
| `FABRICATE_LLM_API_KEY_SECRET` | `anthropic`, `foundry` | The **name** of the variable holding the key (e.g. `ANTHROPIC_API_KEY`) — never the key itself. |
| `FABRICATE_LLM_BASE_URL` | `openai-compatible`, `foundry` | Endpoint base URL. |
| `FABRICATE_LLM_REGION` | `bedrock` | AWS region; credentials come from the ambient IAM identity. |
| `FABRICATE_LLM_PROJECT_ID`, `FABRICATE_LLM_LOCATION` | `vertex` | GCP project and location; credentials come from Application Default Credentials. |
| `FABRICATE_LLM_PLATFORM_FALLBACK` | no | `always` (single-operator self-hosting), `workspace-opt-in` (default; multi-tenant), `never`. |
| `FABRICATE_LLM_EFFORT` | no | `low` \| `medium` \| `high` \| `max` where the provider supports it. |
| `FABRICATE_LLM_MAX_OUTPUT_TOKENS` | no | Default 16000. |
| `FABRICATE_LLM_TIMEOUT_SECONDS` | no | Default 120. Set your platform's proxy timeout above this. |
| `FABRICATE_LLM_MAX_TOOL_ITERATIONS` | no | Default 8. Caps the model/tool loop per turn. |
| `FABRICATE_LLM_HISTORY_WINDOW` | no | Default 40 messages sent as context. |
| `FABRICATE_LLM_MAX_INPUT_TOKENS` | no | Default 120000. Estimated input budget per request; oldest history is dropped to fit. `0` disables trimming. |
| `FABRICATE_LLM_MAX_RETRIES` | no | Default 2. Retries of retryable provider failures (rate limit, transport, timeout, 5xx) with exponential backoff from 500 ms. Authentication and invalid-request failures are never retried. |
| `FABRICATE_LLM_ALLOWED_ENDPOINT_HOSTS` | no | Hosts that workspace-supplied endpoints may target (suffix match). Empty = any public HTTPS host. |
| `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS` | no | `true` permits `http://` and private/loopback endpoints — for air-gapped local runtimes only. |

Provider notes:

- **anthropic** — the official Anthropic API. The adapter sends adaptive thinking and effort and never sends sampling
  parameters or `budget_tokens`, which current Claude models reject.
- **openai-compatible** — one adapter for OpenAI, Azure OpenAI, Gemini's OpenAI-compatible endpoint, vLLM, Ollama
  and gateways such as OpenRouter. A keyless local runtime works with `FABRICATE_LLM_API_KEY_SECRET` unset. Azure
  OpenAI is recognised by its host: the key is sent as the `api-key` header Azure requires, and a bare resource URL
  is completed with `/openai/deployments/<model>/chat/completions?api-version=…` (the model id is the deployment name).
- **bedrock / vertex / foundry** — Claude through your cloud account, authenticated by IAM / ADC / a Foundry key.
  Bedrock model ids take the `anthropic.` prefix (e.g. `anthropic.claude-opus-5`); Vertex uses the bare id.

### Which credential a chat turn uses

Project-bound credential → workspace default for the provider → the workspace's single active credential → the
platform credential (only where `FABRICATE_LLM_PLATFORM_FALLBACK` allows) → none, in which case the chat returns a
clear notice and the direct `/tool` commands still work.

### Whether the agent asks before it acts

In `Guided` mode the agent is instructed to ask rather than guess when a request leaves the row counts, the target
database or the compliance profile open, or when it would overwrite existing data. Whether a given model actually
does that is the model's judgement, not something the code can enforce, so it is measured rather than asserted:
`AgentClarificationLiveEvalTests` runs seven fixtures through a real model and reports how many behaved as
expected. The `integration-tests` workflow runs it weekly and on dispatch; see
[CI secrets for integration tests](ci-integration-secrets.md).

| Model | Fixtures as expected | Last verified |
| --- | --- | --- |
| `claude-opus-5` | *not yet run* — needs `FABRICATE_LIVE_LLM_API_KEY` | — |

The eval fails below 6/7. One fixture going the other way is a model exercising judgement; two is the guidance no
longer landing. **Until a row above carries a rate, treat "the agent asks clarifying questions" as designed for
and unverified** — the prompt contract and the harness are covered offline, the behaviour is not.

### Artifact storage

Generated artifacts default to the local file system (`FABRICATE_ARTIFACTS_PATH`, else the OS temp directory).
That is right for local use and **wrong on every hosted target**: Fly machines are replaced, Cloud Run and
Container Apps revisions are immutable, ECS tasks restart. The files disappear and a completed run is left
pointing at artifacts that no longer exist.

Set `FABRICATE_ARTIFACT_STORE` to use object storage instead. Three adapters cover the field: `s3` for **AWS S3,
MinIO, Cloudflare R2 and Backblaze B2** (one API between them), `azure-blob` for **Azure Blob Storage**, and
`gcs` for **Google Cloud Storage**.

GCS and R2 both offer S3-compatible modes, so the `s3` adapter can reach them — but those modes need HMAC keys,
which is exactly the stored credential the native adapters avoid. On Azure and GCP, prefer the native one.

```bash
# AWS S3, using the task or instance role — no keys stored anywhere
FABRICATE_ARTIFACT_STORE=s3
FABRICATE_ARTIFACT_BUCKET=acme-fabricate-artifacts
FABRICATE_ARTIFACT_S3_REGION=eu-west-1

# MinIO, Cloudflare R2 or Backblaze B2
FABRICATE_ARTIFACT_STORE=s3
FABRICATE_ARTIFACT_BUCKET=fabricate-artifacts
FABRICATE_ARTIFACT_S3_ENDPOINT=https://s3.example.internal
FABRICATE_ARTIFACT_S3_FORCE_PATH_STYLE=true
FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET=ARTIFACT_ACCESS_KEY
FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET=ARTIFACT_SECRET_KEY

# Azure Blob, using managed identity - no keys stored anywhere
FABRICATE_ARTIFACT_STORE=azure-blob
FABRICATE_ARTIFACT_BUCKET=fabricate-artifacts        # the container name
FABRICATE_ARTIFACT_AZURE_ACCOUNT_URL=https://acme.blob.core.windows.net

# Google Cloud Storage, using Application Default Credentials - likewise no keys
FABRICATE_ARTIFACT_STORE=gcs
FABRICATE_ARTIFACT_BUCKET=acme-fabricate-artifacts
```

**Credentials.** Ambient cloud identity first on all three — an IAM role on ECS or EKS, managed identity on
Azure, Application Default Credentials on GCP — because that means no key is stored anywhere at all. The
`*_SECRET` variables are the fallback for running outside the cloud in question, and they hold the *name* of a
secret, not its value, so artifact credentials follow the same path as every other secret. Supplying an S3 access
key without its secret key is refused at startup rather than falling through to ambient credentials and failing
later with an unrelated permissions error.

The configuration is validated when the container is built, so a mistake stops the instance starting rather than
being discovered by the first person to generate data.

**Uploads and downloads stream.** Size and SHA-256 are computed as the bytes pass through and stored as object
metadata, which is what lets the run manifest be served without reading the blobs.

**Retention.** `FABRICATE_ARTIFACT_RETENTION_DAYS` defaults to `0`, keeping everything. A positive value starts a
sweep that deletes the artifacts of runs older than the window; the run record keeps its checksums — still the
record of what was produced — but reports an empty artifact manifest rather than paths that no longer resolve.
Where your object store offers a lifecycle policy, configuring one on the bucket is cheaper than this sweep and
does the same job; the sweep exists for stores that do not, and for operators who prefer the rule in one place.

## One command locally

```bash
cp .env.example .env        # fill in FABRICATE__BootstrapApiKey and your LLM key
docker compose up --build
curl -s http://localhost:8080/healthz
```

Compose runs the API against PostgreSQL with a persisted volume for the Data Protection key ring, so the local shape is
the hosted shape. Swagger UI is at `http://localhost:8080/swagger`.

## Fly.io (reference deployment)

Why Fly: it runs the unchanged image, injects secrets as environment variables, health-checks `/healthz`, scales to
zero, and charges no plan fee or minimum — so an idle instance costs close to nothing. It can also put PostgreSQL on a
private network next to the API, though that is the expensive option rather than the default one; see [Cost](#cost).

1. Fork the repository and install `flyctl`.
2. `fly launch --no-deploy` (accepts the checked-in `fly.toml`; choose your app name and region).
3. Attach a PostgreSQL database. **Which one you pick dominates the bill** — see [Cost](#cost).

   *Recommended for a low-cost instance* — an external free-tier PostgreSQL such as [Neon](https://neon.tech).
   Set the secret by hand; `SSL Mode=Require` is mandatory for any database outside Fly's private network:
   ```bash
   fly secrets set FABRICATE_CONNECTION_STRING="Host=ep-xxx.neon.tech;Database=fabricate;Username=…;Password=…;SSL Mode=Require"
   ```

   *If you want the database on the private network* — Fly Managed Postgres, from $38/month:
   ```bash
   fly mpg create --name fabricate-db --region lhr
   fly mpg attach fabricate-db --app fabricate --variable-name FABRICATE_CONNECTION_STRING
   ```

   Do **not** use `fly postgres create`. That is unmanaged Fly Postgres, which Fly has deprecated and will not
   support, so it is not a foundation to build on however cheap it looks.
4. Set the remaining secrets — these never enter GitHub:
   ```bash
   fly secrets set FABRICATE__BootstrapApiKey="$(openssl rand -base64 32)"
   fly secrets set ANTHROPIC_API_KEY="sk-ant-…"
   ```
5. In GitHub: add `FLY_API_TOKEN` (`fly tokens create deploy -x 999999h`) as a secret, `SMOKE_API_KEY` (the bootstrap
   key) as a secret in an environment named `fly`, and optionally `FLY_APP` as a variable.
6. Push to `main`. The [deploy workflow](../../.github/workflows/deploy-fly.yml) builds remotely, deploys, waits for
   `/healthz` through the cold start, and runs the smoke tests — and fails if they were skipped rather than executed.

   Until step 5 is done the workflow's preflight job finds no `FLY_API_TOKEN`, reports that no Fly deployment is
   configured, and skips the deploy and its verification. That is why a fresh clone is not red on every push. The
   gate is about configuration only: once the token exists, a deploy that goes wrong still fails loudly.

Rotating any secret is `fly secrets set …`; it restarts the machine and needs no redeploy. Scaling out is
`fly scale count 2` — the API is stateless.

### Cold start

With `min_machines_running = 0` the first request after idle starts a machine. Measure it after your first deploy:

```bash
fly machine stop; time curl -s -o /dev/null https://<app>.fly.dev/healthz
```

Record the number in your runbook and set `grace_period` in `fly.toml` a little above it. Expect single-digit seconds
for the default image; if it is materially longer, trim the image (ReadyToRun/trimmed publish) rather than raising
`min_machines_running`.

### Key ring

Per-workspace LLM credentials and connection secrets are encrypted with ASP.NET Core Data Protection. The key ring
is what decrypts them, so where it lives decides two things that are easy to discover too late: whether a replaced
machine can still read what its predecessor wrote, and whether two instances can read each other's rows at all.

`FABRICATE_DATA_PROTECTION_KEY_STORE` chooses:

| Value | Ring lives in | Use when |
| --- | --- | --- |
| `filesystem` (default) | `FABRICATE_DATA_PROTECTION_KEYS_PATH` | The directory is on shared, persistent storage — a mounted volume, one machine. |
| `database` | The application database, alongside your other tables | The disk is ephemeral or unshared: Fly, Cloud Run, Container Apps, ECS, or more than one instance. |

**On an ephemeral disk the file-system store loses data permanently.** Not "the instance restarts empty" — the
ciphertext survives in the database and nothing can ever decrypt it again, and every user re-registers their
credentials. Two instances with separate rings fail the same way in a subtler form: whichever instance serves the
request decides whether the credential is readable.

**On its own, the database store has a real cost.** The ring ends up in the same database as the ciphertext it
protects, so a single database dump decrypts every tenant secret — where the file-system store forces an attacker
to obtain two separate things.

**A key-encryption key removes that cost, and is the recommended configuration.** With one, the database holds
only wrapped keys, and unwrapping them needs a KMS permission a dump does not carry:

```bash
FABRICATE_DATA_PROTECTION_KEY_STORE=database
FABRICATE_DATA_PROTECTION_KEK=aws-kms
FABRICATE_DATA_PROTECTION_KMS_KEY_ID=alias/fabricate-keyring
FABRICATE_DATA_PROTECTION_KMS_REGION=eu-west-1
```

The instance needs `kms:GenerateDataKey` and `kms:Decrypt` on that key, and nothing else. Credentials come from
the standard AWS chain, so an IAM role means no key is stored anywhere. Encryption is enveloped — a data key per
ring entry, wrapped by the KMS key — because KMS caps direct encryption at 4 KB and a ring entry exceeds it.

Only AWS KMS is implemented. Azure Key Vault and GCP KMS are tracked by
[#92](https://github.com/MaximumTrainer/synthetic-fabricate/issues/92); neither has an emulator, so neither would
be verifiable in CI, and shipping an adapter that has never run is how this project got its worst bugs.

**Without a KEK the database store is refused at startup** unless you say you accept the trade-off:

```bash
FABRICATE_DATA_PROTECTION_KEY_STORE=database
FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED=true
```

In order of strength: `database` with a KEK, then `filesystem` on a mounted volume, then `database` unwrapped.
The file-system store on an ephemeral disk is not on the list because it is not a configuration, it is a
countdown.

The table is created by the standard migrations, so no manual step is needed when switching.

**Turning a KEK on does not reach back over the ring you already have.** Data Protection encrypts on write and
never revisits stored entries, so enabling `FABRICATE_DATA_PROTECTION_KEK` protects newly created entries only —
the existing ones stay in the clear while the configuration says the ring is wrapped. Run the rewrap once, with
the same environment the API uses:

```bash
FABRICATE_DB_PROVIDER=postgres FABRICATE_CONNECTION_STRING="..." FABRICATE_DATA_PROTECTION_KEY_STORE=database FABRICATE_DATA_PROTECTION_KEK=aws-kms FABRICATE_DATA_PROTECTION_KMS_KEY_ID=alias/fabricate-keyring   fabricate secrets rewrap
```

It re-protects the ring without touching tenant ciphertext, so nothing needs re-encrypting and there is no
half-migrated state. It is idempotent — running it on an already-wrapped ring reports that there was nothing to
do — and safe to run while the API is up.

## Alternatives

- **Render** — `render.yaml` at the repository root is a Blueprint: a Docker web service plus a managed PostgreSQL,
  with secrets prompted in the dashboard.
- **Railway** — create a service from the repository (Dockerfile auto-detected) and add the PostgreSQL plugin; set the
  same variables as `.env.example`, with `FABRICATE_CONNECTION_STRING` from the plugin's connection URL.
- **Cloudflare Containers + Neon** — viable now that the API is stateless; documented only, no maintained pipeline.
  Cloudflare **Workers** cannot run .NET; Cloudflare **Pages** is a good host for this `docs/` site. Not cheaper than
  Fly for this workload — Containers require the $5/month Workers Paid plan before any usage. See [Cost](#cost).
- **AWS / Azure / GCP with Terraform** — the production-grade path under `infra/`. The stacks set
  `FABRICATE_DB_PROVIDER=postgres` and `FABRICATE_CONNECTION_STRING` to the database they provision, so state is
  durable there too; add the `FABRICATE_LLM_*` variables and your key secret to enable chat.

## Egress profile

A running instance makes outbound connections **only** to:

| Destination | When |
| --- | --- |
| Your PostgreSQL | always |
| The configured LLM endpoint | during chat turns, credential validation, and nothing else |
| Databases you point schema discovery at | when you run discovery |

Selecting `openai-compatible` with a private base URL and `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS=true` yields an
instance that makes no calls outside your network.

### The prompt data boundary

What may reach a model provider is enforced in code, not left to convention. Every tool declares the most
sensitive class of content its result can carry, and a tool whose class the boundary forbids is **never offered to
the model** — so the model cannot ask for it and be refused halfway through a turn, which would both disclose that
the data exists and leave the user with a broken conversation.

| Content class | What it is | When it may be sent |
| --- | --- | --- |
| `Metadata` | Table, column, type and relationship names; run summaries. Describes the data rather than containing it. | Always. Without it the agent is useless. |
| `AggregateStatistics` | Histograms, distinct counts, min/max over real rows. No single row is disclosed, but a min/max is a real value and a histogram over a small table can identify individuals. | Only with the workspace opt-in. |
| `SampledValues` | Values copied from real rows — samples, examples, few-shot rows. | Only with the workspace opt-in. |

The opt-in is `allowSampledDataInPrompts` on the workspace LLM policy
(`PUT /workspaces/{id}/llm-credentials/policy`), and it defaults to **false**.

**It cannot be enabled at all on a `Healthcare` or `Finance` workspace.** The request is refused with `409` and an
explanation, and the policy is left exactly as it was — refused rather than silently ignored, because an
administrator told "saved" while the setting did not take is worse off than one told why it cannot be. A
workspace's compliance profile is fixed when it is created. The profile is also re-checked at every decision, so
an opt-in written before a profile changed does not survive the change.

Every refusal is audited as `llm.boundary_blocked` with the tool name and content class, and never the payload.

Today's tools all return `Metadata`, so the boundary changes nothing for them. It exists ahead of the tools that
will need it — NoSQL discovery samples documents to infer field types, data profilers compute per-column
statistics, and any future "explain this data" tool sends values by construction.

## Cost

Published rates, checked 14 September 2026. They move more often than this document does — re-check before you rely
on them.

**The database dominates the bill, not the host.** The API scales to zero and costs pennies while idle. The database
does not scale to zero, so it is the floor — and the choice between the two options below is a factor of ten, where
the choice of hosting platform is a couple of dollars.

| Reference configuration | Approximate monthly |
| --- | --- |
| Fly machine scaling to zero + external free-tier PostgreSQL (Neon) | **$1—3** |
| Fly machine scaling to zero + Fly Managed Postgres (Basic) | **$38+**, plus $0.28 per provisioned GB |

The figures behind that: `shared-cpu-1x` 256 MB is $1.94/month run continuously and is billed by the second, so a
machine that sleeps costs a fraction of it; volumes are $0.15/GB/month; Fly has **no free tier and no minimum monthly
charge**. Neon's free tier gives 0.5 GB of storage and 100 compute-hours a month, and autosuspends after five minutes
idle.

### Why not Cloudflare

Cloudflare **Workers** cannot run .NET at all. Cloudflare **Containers** can, but it requires the **$5/month Workers
Paid plan before any usage at all**, where Fly has no minimum — so for an idle-heavy instance Fly is the cheaper of
the two, and the gap widens the more it sleeps. Cloudflare also has no PostgreSQL (D1 is SQLite, not Postgres), so an
external database is needed there too: the database decision is identical on both platforms, and it is the one that
matters. Containers would additionally need a Worker plus Durable Object front end and a deploy pipeline this
repository does not have.

Use Cloudflare for what it is genuinely cheapest at: **R2** for generated artifacts — free-tier storage, no egress
charges, and the S3 API that `FABRICATE_ARTIFACT_STORE=s3` already speaks. That works perfectly well from a
Fly-hosted instance; the two are not an either/or.

## Health and readiness

`GET /healthz` (unauthenticated) reports `status`, a `database` state and an `llm` block. It never includes a
secret, a connection string or an exception message — the reason for a failure is in the logs, not in a response
anyone can fetch.

The two dependencies are treated differently, because they fail differently:

| Dependency | State | Status |
| --- | --- | --- |
| Database unreachable | `unreachable` | **503** — every authenticated route reads through it, so the instance is taken out of rotation rather than left answering 500s |
| No database configured | `not configured` | 200 — the in-memory repositories are a legitimate local configuration |
| LLM credential missing or misconfigured | `disabled` | 200 — the rest of the API does not depend on a model |

The database probe opens and drops a connection and touches no table, with a three-second budget, so it stays
cheap enough to run on the platform's health-check interval.

**A database that is missing at startup is a different case.** Migrations run before the app serves traffic, so
the process fails to start rather than coming up unhealthy, and the platform restarts it. That is deliberate: an
instance with no schema cannot serve anything, and a crash loop is a clearer signal than a machine that is up and
broken. The readiness signal above is for the other failure — a database that was reachable and went away while
the process kept running, which is otherwise invisible.

## Backups

Use your PostgreSQL provider's automated backups and record the retention window. Rehearse a restore into a fresh
database followed by an API boot against it; the migrator will find nothing pending and the bootstrap key will still
authenticate.
