# Agents Guide

This document defines implementation expectations for all agents working in this repository.

## 1) Architecture First: Hexagonal + Dependency Injection

- Use **Hexagonal Architecture** (Ports and Adapters).
- Keep domain logic isolated from infrastructure concerns.
- Depend on abstractions (ports) in the application/domain layers.
- Implement adapters for external systems (DB, queues, files, LLM providers, telemetry, etc.).
- Wire dependencies through **.NET DI** (`Microsoft.Extensions.DependencyInjection`) in the composition root.
- Avoid service location and static/global mutable state.

### Recommended layering

- **Domain**: entities, value objects, invariants, domain services.
- **Application**: use cases, orchestration, port interfaces, DTOs.
- **Infrastructure**: adapter implementations for ports.
- **Presentation/API/UI**: HTTP/endpoints, Blazor/UI contracts.

Only inward dependencies are allowed (outer layers depend on inner abstractions, never the reverse).

## 2) Test-Driven Development (TDD) by Default

- Follow **Red → Green → Refactor** for new behavior and bug fixes.
- Start with failing tests that express required behavior.
- Prefer fast unit tests for domain and application logic.
- Add focused integration tests for adapters and cross-boundary behavior.
- Keep tests deterministic and isolated (seeded random, fake clocks, test doubles).
- Do not merge behavior changes without corresponding test coverage.

### Test strategy

- Domain rules: unit tests.
- Use case orchestration: unit tests with mocked/fake ports.
- Adapters: integration tests against realistic dependencies where possible.
- Security-sensitive paths: explicit negative tests (unauthorized access, invalid inputs, data leakage checks).

## 3) Security-by-Design Requirements

- Treat all external input as untrusted; validate and constrain it.
- Enforce least privilege for data access, credentials, and runtime permissions.
- Never move or expose production PII in prompts, logs, traces, or test fixtures.
- Use parameterized queries and safe APIs to prevent injection risks.
- Apply output encoding/sanitization where content may be rendered.
- Keep secrets out of source control; use secure configuration providers.
- Emit auditable, privacy-aware logs (no secrets, no sensitive payload dumps).
- Require explicit allowlists for model/tool access and outbound integrations.
- Include threat modeling for new adapters and external integrations.

## 4) Definition of Done for Agent-Authored Changes

- Architecture boundaries remain clean (ports/adapters respected).
- Dependencies are registered via DI in the composition root.
- Tests are added/updated first (or alongside) and pass.
- Security checks are considered and relevant tests are present.
- markdown and website documentation is updated when architecture or behaviour changes.

## Shared agent skills

Shared skills live in [MaximumTrainer/agent-skills](https://github.com/MaximumTrainer/agent-skills). Before writing a new
skill, runbook or repeated procedure, check the catalogue - and send genuinely
general improvements back so the other repositories get them too.

```bash
python3 .claude/skills/skill-exchange/scripts/skills.py list
python3 .claude/skills/skill-exchange/scripts/skills.py status
```

See `.claude/skills/skill-exchange/` for the workflow.
