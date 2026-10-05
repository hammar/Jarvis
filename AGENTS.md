# Agent instructions

This file belongs at the repository root. Place the companion specification at `docs/Personal-Agent-Project-Spec.md`. These instructions govern development of the C# local-first personal assistant; they do not grant the running assistant development privileges.

## Sources of truth and completion

Read this file, the project specification, relevant accepted ADRs, the task brief and existing code before editing. Follow the owner's current instructions. The specification defines product scope and acceptance; accepted ADRs explain implementation decisions. Surface a material conflict and propose a concrete resolution rather than silently changing requirements. Routine reversible implementation choices do not need repeated approval.

The selected stack is C#, GitHub Copilot SDK behind an application-owned adapter, ASP.NET Core/Razor Pages with SSE, SQLite, Ollama and approved Home Assistant tools. Aspire AppHost and ServiceDefaults are required for development and distributed testing. Cloud inference requires explicit routing and reviewed consent. Preserve configuration, credentials, durable conversations and jobs across updates.

An implementation is complete only when its acceptance criteria, meaningful tests, relevant documentation and required checks pass. Do not report mock behavior as SDK validation or live behavior. Never return success stubs, swallow errors to pass tests, disable a failing gate or claim checks that were not run.

## Scope and coordination

1. Start with T01 SDK validation, then T02 scaffold/contracts/enforcement. Do not build dependent functionality on unvalidated SDK assumptions. Record compatibility results and pinned versions.
2. For each task, write a brief under `docs/tasks/` covering objective, spec/scenario IDs, scope and exclusions, owned files, dependencies, assumptions, tests, docs and completion criteria.
3. Prefer one coherent vertical slice per PR. Approximately 400 non-generated changed lines is a review target, not a hard limit or permission to omit work. Explain larger cohesive changes. Separate unrelated refactors and avoid speculative features or abstractions.
4. Use one integration owner and independent implementation/review roles. Parallel development requires merged shared contracts, isolated branches/worktrees and explicit ownership. Do not concurrently edit shared interfaces, migrations, composition or CI. Keep coordination records concise and current.
5. Do not expand MVP into browser automation, arbitrary shell execution, email, door locks, runtime multi-agent orchestration or other excluded capabilities. Record follow-up ideas separately.

## Architecture requirements

| Project | Allowed internal dependencies |
|---|---|
| Domain | None |
| Application | Domain |
| Infrastructure | Application, Domain |
| Web | Application, Domain; Infrastructure only in composition/bootstrap; ServiceDefaults |
| ServiceDefaults | No application-layer projects |
| AppHost | Aspire resource/composition references; no business logic |
| Tests | Tested projects and TestSupport; never referenced by production |

Domain/Application must not depend on ASP.NET Core, Aspire hosting, SQLite/EF, Copilot or provider SDK types. Web handlers call application use cases, not raw database/provider/Home Assistant clients. Confine all GitHub.Copilot types to `Infrastructure/AgentEngine/Copilot`; enforce by type/assembly dependency as well as namespace. An AppHost resource reference to Web is an orchestration reference, not a runtime layer violation. Avoid dependency cycles and service locators.

Keep application state authoritative in SQLite. Copilot sessions are replaceable engine state, not the source of truth for memory, approvals, jobs or audit. Model output is untrusted: typed validation, policy, approval and execution are host responsibilities. Enforce approved tool surfaces and exact consent context. Handle uncertain physical outcomes explicitly; do not retry ambiguous writes blindly.

T02 implements project-reference and compiled dependency checks, including negative fixtures and the narrow composition-root exception. A namespace rename must not bypass a boundary. Architecture checks and their exclusions require independent review.

## Aspire and local development

Provide documented Simulator, Local, Hybrid and E2E profiles. Simulator/E2E use deterministic controlled endpoints and isolated data without household/cloud credentials. Tests exercise production composition through deliberate test adapters; test controls cannot be enabled through model input or exposed in live profiles.

On macOS, configure native Ollama as an external endpoint; do not assume container access to Apple GPU acceleration. AppHost owns resources it launches, not external Ollama/Home Assistant or real development data. Use discovered endpoints for managed resources and explicit parameters for external ones. Test startup/cleanup, concurrent isolated runs and readiness failure behavior.

ServiceDefaults supplies health checks and privacy-filtered local telemetry. Review retries/hedging before enabling HTTP resilience: provider turns and physical writes require explicit semantics. Keep the dashboard local/authenticated. Never log raw prompts, secrets or private memory by default. Document direct Web host deployment and native restart/backup behavior separately from Aspire development orchestration.

## Documentation standards

Maintain `README.md`, `docs/architecture.md`, `docs/development.md`, `docs/operations.md`, `docs/testing.md`, `docs/adr/` and task briefs. Every production project needs a short README explaining ownership, dependencies, entry points and test commands. Document important boundary/storage/deployment decisions in small ADRs; do not create an ADR for each routine edit.

Public/protected production classes, interfaces and methods require useful XML documentation. Explain responsibilities, inputs/outputs, invariants, failure behavior and side effects where relevant. Document cancellation, privacy/approval requirements and uncertain outcomes. Use `inheritdoc` when the inherited contract is sufficient. Describe DTO units, identifiers, allowed values and UTC/local-time meaning where ambiguity matters.

Document non-obvious internal/private policy, concurrency, recovery and algorithms; avoid comments that merely repeat code. Test names describe behavior and do not need boilerplate XML. Generated-file exceptions must be explicit. First-party libraries enable XML output and fail builds on missing required documentation. Markdown/link checks are mandatory; reviewers also check semantic accuracy. Update affected docs in the same PR as behavior, configuration or API changes.

## Tests and measurable coverage

Use the exact .NET SDK selected by the current `global.json`; it is authoritative over historical task/issue evidence. Preserve the .NET 10 LTS target and keep CI/container SDK pins aligned with `global.json`. SDK refreshes require compatibility validation and documentation; historical version references are not instructions to downgrade.

Use pinned compatible .NET/test tooling: xUnit-compatible tests, Playwright for .NET and Aspire.Hosting.Testing. T02 selects and validates the runner/coverage collector together; do not mix incompatible VSTest and Microsoft.Testing.Platform switches. Create common validation scripts under `tools/` as POSIX shell scripts (`tools/validate.sh`) runnable on macOS/Linux; PowerShell is not used. CI and local agents use the same scripts and documented prerequisites.

Required test layers are unit, real SQLite/HTTP integration, actual Copilot runtime contract, Aspire API E2E and Playwright browser E2E. In-process adapter/API tests may use WebApplicationFactory. Aspire tests launch real processes; do not assume they support replacing application DI after startup. Fake engine tests cannot establish actual SDK compatibility. Live local/cloud/device tests are opt-in and separate from credential-free required checks.

| Gate | Minimum |
|---|---|
| Unit-only Domain and Application, each | 90% lines; 85% branches |
| Combined unit/integration runtime overall | 85% lines; 75% branches |
| Each first-party runtime assembly, combined | 80% lines; 70% branches |
| Critical policy/context/approval/action/scheduler modules | 95% lines; 90% branches and specified failure scenarios |
| Changed executable lines | 90% line coverage |
| E2E | All implemented acceptance scenarios pass |

Use covered/coverable counts rather than averaging percentages. Deduplicate merged unit/integration reports. Missing expected reports, missing test discovery or missing assembly coverage fails. Branch coverage is N/A only when the reported module has no coverable branches. Define critical-module ownership and coverage filters explicitly so renaming cannot evade the gate.

Narrow reviewed exclusions may cover generated compiler/Razor/migration code, test projects, AppHost and untouched template ServiceDefaults. Handwritten migration/recovery and custom orchestration/defaults behavior still need integration/E2E tests. Never exclude critical code or all Infrastructure to improve scores. E2E runs out of process and has a separate pass gate; do not claim its execution contributes to coverage without verified instrumentation.

Tests must assert observable behavior and important failure paths, not getters or implementation duplication. Bug fixes require regression tests. Use controlled clocks, isolated databases and event/state waits with deadlines; no arbitrary sleeps or blanket retries to conceal flakes. Quarantine requires an issue, expiry, replacement coverage and independent approval; critical acceptance scenarios cannot be quarantined to merge. Publish redacted reports/traces with retention limits.

T02 proves that low coverage, an architecture violation fixture, a failed browser test and a missing report/discovery each fail validation. Empty scaffolding does not prove thresholds. Add scenario mappings as features land; the complete MVP matrix must pass at M4.

## PR review and integration

This is a solo personal project: the owner is the only human, and every PR — whether pushed directly by the owner or produced by an agent session — is authenticated as the owner's own GitHub identity. GitHub never allows a PR author to approve their own PR, so a mandatory reviewer-approval gate is not achievable here and branch protection does not require one. Do not reintroduce a required-approving-review-count or treat its absence as a gap to silently work around.

This project treats agentic implementation and review as a compiler from owner-accepted, higher-abstraction specifications to lower-level implementation artifacts. The owner intentionally does not manually inspect generated code or certify its correctness. The owner remains responsible for requirements, acceptance criteria, material scope and tradeoffs, and explicit acceptance of disclosed residual risks. For each PR, record the applicable specification and acceptance criteria, automated evidence, independent agentic review dispositions, and residual risks so the owner can accept that basis for the change without a code-inspection mandate.

The implementer records the task plan, implements the scoped change with meaningful tests and documentation, and runs the required scripts. Every PR requires an independent agentic review against the task/specification and meaningful risks in the implementation, tests, and documentation. The reviewer records concrete findings with severity and evidence against the exact commit. Describe the problem and resulting behavior, scope/spec IDs, actual checks/results, exercised and unexercised platforms/runtime behavior, migrations and limitations. Do not attach unrelated cleanup.

Maintain a cumulative finding ledger across review rounds. Give each finding a stable reference and record its disposition and supporting evidence: fixed (with the verifying revision/check), not applicable (with rationale), or accepted as a residual risk (with the owner's explicit acceptance). Include every finding regardless of severity, including summary-only findings labelled “Previously missed.” A later review's omission does not resolve or erase a prior finding; provide the full ledger to each reviewer and retain it in the PR/task record. All findings must have an explicit disposition before the owner accepts the PR. Automated checks and agentic review are evidence within stated limits, not guarantees; clearly disclose unexercised behavior and uncertainty.

Keep an open PR current with `main`. Before handoff, and whenever a session resumes work on an open PR, fetch `main`; if other PRs have merged since the branch point, bring the branch up to date (merge `main` into it, or rebase only where force-pushing the PR branch is permitted), resolve conflicts, re-run the required scripts and let CI re-run before reporting the PR ready. Treat overlapping changes to shared contracts, migrations, composition or CI as semantic conflicts even when Git merges cleanly: re-check them against the merged code, not only the textual diff. Report what was integrated and the re-run results in the PR. This never authorizes merging.

Human sign-off is enforced procedurally instead of by a GitHub-approval gate: an agent session must never merge a PR, enable auto-merge, or enqueue it into a merge queue on its own initiative, regardless of how green its checks are. Only the owner's explicit action — clicking Merge, or an explicit live instruction to an agent to do so — lands a PR. Before that decision, the owner accepts the applicable specification/criteria, automated validation evidence, complete finding dispositions, and disclosed residual risks; this is not manual code inspection or code certification. Required CI checks and conversation resolution remain mandatory gates and must not be weakened or bypassed to go green.

Independent agentic review must examine correctness and meaningful risks including scope/spec conformance, architecture, side effects, concurrency, idempotency, uncertain physical outcomes, context/permissions, privacy, upgrade preservation, dependencies, tests and documentation. An agent's unsubstantiated "LGTM" is not review evidence. The owner retains control over requirements, material scope/tradeoffs, acceptance of residual risk and the merge decision. Never silently lower thresholds or protection; any authorized emergency exception is narrow, documented, audited and time-bounded.

## CI enforcement

Implement stable required checks: `build-and-analyzers`, `architecture`, `unit-tests`, `integration-tests`, `sdk-contracts`, `coverage`, `aspire-e2e`, `browser-e2e`, `docs`. Build Release with nullable checks, formatting and configured analyzer warnings as errors. Pin tooling/actions and use read-only workflow permissions by default. Normal/untrusted PR workflows receive no cloud or household secrets. Cache dependencies/build intermediates, not credentials or assistant state.

Run Linux checks for every code PR, plus native macOS checks for process launch, OS paths/secrets, external endpoint or packaging changes. Docs-only classification may skip runtime suites explicitly, but docs/workflow-policy results remain required. CI, dependencies, AppHost profiles and enforcement changes run the full relevant gates. Required checks must not pass accidentally on skipped jobs, absent reports or omitted paths; test the protection configuration before M1 release.

Configure CODEOWNERS for contracts, policy boundaries, migrations and CI for documentation/reference purposes; it does not gate merges here since there is no second reviewer to assign. The owner enables branch protection requiring passing required checks on a branch that is up to date with `main`, resolved conversations, no force-push/deletion of `main`, and — per "PR review and integration" above — relies on the never-self-merge rule rather than a reviewer-approval count to keep a human in the loop. Writing instructions does not enable these controls. Never silently lower thresholds or protection; any authorized emergency exception is narrow, documented, audited and time-bounded.

## Handoff

Report changed files, contract/schema impact, acceptance IDs/tests, documentation updates, commands actually run and their results, review commit, blockers and follow-up tasks. If validation cannot run, state why and what remains unverified. Keep the task status aligned with this evidence. Preserve secrets and real household data during development; use controlled fixtures for required checks.
