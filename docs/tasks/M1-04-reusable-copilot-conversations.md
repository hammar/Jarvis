# M1-04: Reusable Copilot conversations, native history, and safe recovery

## Objective and acceptance

Establish and implement reusable, role-separated Copilot SDK conversations for
Local turns where the pinned SDK/runtime supports the required lifecycle,
isolation, compaction and recovery semantics. Preserve SQLite as the
authoritative conversation store; SDK session state remains sensitive,
replaceable working state.

**Issue:** #38. **Specification:** §§3–6, 9, 11–14, 16, 18–21; accepted
ADRs 0001 and 0003. **Acceptance:** issue criteria A1–A10, including actual
runtime evidence, native role continuity, reliability, bounded lifecycle,
recovery/idempotency, compaction/context policy, routing/privacy, existing
authenticated API/SSE behavior, validation gates and independent review.

## Scope

- Validate exact SDK/runtime behavior at repository pins before choosing
  production session management. Exercise lifecycle, provider-bound roles,
  resume/reconstruction, model/provider changes, compaction, isolation,
  cancellation/crash, recovery and bounded resource/latency observations.
- Reuse SDK native history across successive Local turns only if actual-runtime
  evidence supports safe conversation binding and recovery. Define session
  ownership, active-turn serialization, finite resource/idle cleanup and
  truthful failure outcomes.
- Preserve explicit LocalOnly routing, registered-tool denial, SQLite durable
  messages/events and idempotency, API/SSE reconnect, authentication,
  cancellation and upgrade behavior. Never replay completed tool effects or
  blindly retry an uncertain turn.
- Keep model switching distinct from provider switching. Cloud use remains
  disabled unless explicitly configured and consented; cloud sessions remain
  separate and use only the existing reviewed minimal-packet policy.
- Reconcile the current application history/context budget with native SDK
  history and supported compaction. Compacted SDK summaries are not durable
  facts, approvals or authoritative application messages.
- Carry forward the tested prompt-reliability correction from the
  `hammar-authenticated-chat-ui-apis-sse-demo` branch when its commits apply
  cleanly and remain relevant. Keep its evidence and finding dispositions.

## Exclusions

No direct-Ollama replacement, provider/framework substitution, cloud enablement,
household/device operations, new tools, browser/shell access, runtime
multi-agent behavior, unrelated UI redesign or broad persistence refactor.
Do not inspect or edit private SDK transcript files. Do not install models or
use household conversations. Native Ollama checks are limited to authorized
synthetic prompts and already-installed models.

If the pinned runtime cannot satisfy a required capability, document exact
observations and alternatives and obtain a scoped owner decision before
changing the accepted SDK/provider architecture. Unsupported behavior is a
blocker, not a successful fallback.

## Likely owned files

- This brief; a focused accepted lifecycle/recovery ADR.
- `src/PersonalAgent.Application/Contracts.cs`,
  `src/PersonalAgent.Application/TurnCoordination/`,
  `src/PersonalAgent.Application/Context/` only where the validated boundary
  requires it.
- `src/PersonalAgent.Infrastructure/AgentEngine/Copilot/`,
  Infrastructure persistence/migrations only if durable session metadata is
  demonstrably needed, and Web composition in `src/PersonalAgent.Web/Program.cs`.
- `tests/PersonalAgent.SdkContractTests/`,
  `tests/PersonalAgent.IntegrationTests/`,
  `tests/PersonalAgent.UnitTests/`, `tests/PersonalAgent.E2ETests/`, and
  `tools/sdk-validation/` if the existing actual-runtime harness needs
  capabilities not covered by contract tests.
- Relevant project READMEs and `docs/{architecture,development,operations,
  testing,sdk-validation}.md`; acceptance scenario and review/finding records.

Shared contracts, migration and composition edits remain owned by one
integrator. Avoid unrelated changes and preserve all prior finding ledgers.

## Dependencies, assumptions, and decisions

- M1-01, M1-02, M1-03, T01–T04 and accepted ADRs are present on current
  `main` (`305dbb7` at task start). `global.json`, lock files and current
  SDK/runtime compatibility records—not historical issue values—govern pins.
- The issue-owner authorized synthetic live local-model checks if native Ollama
  and the existing Gemma/Llama models are available. No live cloud, credentials,
  household data, tools or physical writes are authorized.
- M1-02 already permits one executing turn per conversation and four globally;
  use those existing semantics rather than inventing concurrent turns inside
  one SDK conversation.
- The accepted spec permits reconstructable replaceable SDK sessions and
  requires separate local/cloud execution. ADR 0001 accepts the native trusted
  runtime boundary; ADR 0003 requires forward-only SQLite changes and
  interruption instead of replay after uncertain outcomes.

## Tests and evidence

- Actual pinned SDK/runtime contracts against instrumented loopback providers:
  assert outbound native role ordering, chosen model/provider, compaction
  observations, isolated session state, lifecycle and controlled failures.
  Fakes alone cannot establish compatibility.
- Real SQLite/HTTP integration and unit tests: conversation identity,
  concurrency, event/message durability, cancellation, terminal outcomes,
  interruption, restart/incompatible state, no duplicate output or side
  effects, and migration-on-existing-data if a schema change is needed.
- Production-composed Aspire API and Playwright E2E: existing auth, reconnect,
  SSE, cancellation and history after restart with controlled endpoints and
  isolated data.
- When available, opt-in native Ollama comparisons using only synthetic
  arithmetic/context-recall prompts and installed Gemma/Llama models; separate
  exact captured-request replay from native role-separated requests.
- Measure SDK runtime launch counts, latency and resource use under controlled
  conditions. Never report simulator evidence as live inference or infer
  compaction from a long transcript alone.
- Run applicable `tools/validate.sh` required gates, including coverage,
  architecture, SDK contracts, Aspire/browser E2E, docs and native macOS
  process/path/endpoint checks. Record exact commands, revisions, results,
  unavailable evidence, review findings and dispositions.

## Completion criteria

Complete only when issue A1–A10 have evidence at the exact reviewed revision,
required scripts and thresholds pass, docs/ADR/brief reflect actual behavior,
and every independent-review finding is dispositioned. Record unexercised
platform/runtime behavior and residual risk. If actual-runtime requirements
cannot be met, leave the task blocked and report the concrete decision needed;
do not claim implementation complete.

## Current implementation and acceptance status

**Incomplete.** Proposed lifecycle/recovery decisions are recorded in
[ADR 0004](../adr/0004-local-copilot-working-state.md). No production-compaction
scope reduction is authorized. Required compaction acceptance A6 remains open,
despite observable runtime capability evidence.

| Criterion | Evidence and remaining limits |
| --- | --- |
| A1 | Pinned-runtime native history, disk resume, separate model/provider-switch contracts, exact missing-state error and automatic/manual compaction contracts. Manual compaction RPC is experimental and its token limit is advisory. |
| A2 | Tool-free Local provider messages have native system/user/assistant/user ordering on matched resume; separate conversations are isolated. Reconstruction uses labelled text, not native assistant-role seeding. |
| A3 | Earlier authorized Gemma/Llama synthetic comparisons passed; final rerun is pending in this record. Prompt preservation and streaming apply on resume. |
| A4 | Two successive controlled turns reused one disk conversation but launched two separate runtimes, both verified exited. One native macOS sample measured 1,180/221 ms to first delta, 1,232/252 ms total and 95,977,472/92,651,520 working-set bytes. No launch saving or universal speed claim. Active/idle/orphan maintenance contracts pass. |
| A5 | Production coordinator reconstructs missing SDK state, corrupt marker and offline SQLite restore with one new request, deduplication and no rolled-back content. Checkpoint-write failure is Interrupted without a committed answer. Remaining cleanup/race review evidence is not complete. |
| A6 | Automatic compaction emits observed events and resumes a summary. Manual public RPC returns summary/context metrics, but accepts a 144,022-character summary with 18,088 current tokens despite TokenLimit 8,192 and sends it onward. Safe production budget/privacy accounting is not established; compaction remains disabled and this criterion is unmet. |
| A7 | Cloud/tool turns remain fresh. Configuration/history changes reset Local state. Credential/configuration-change and adversarial resumed-tool coverage still require final review. |
| A8 | Production coordinator and required HTTP/SSE integration pass; process/browser gate evidence is being refreshed. |
| A9 | Current native build, unit, integration, architecture, SDK, docs and coverage results below. Current Linux execution remains unrun; prior prompt-correction Linux evidence does not validate this change. |
| A10 | Proposed ADR and affected docs updated. Full cumulative ledger retained below. Exact-revision independent review and final dispositions remain required. |

No schema changes. `AgentTurnRequest` now requires durable `ConversationId`;
the coordinator supplies it. `RuntimeContextRebuilt` is a typed, persisted
safe-reason event. Optional `IEngineStateMaintenance` runs on the same configured
engine instance at startup/hourly cleanup; deliberate test-engine replacement
does not accidentally instantiate the live engine for maintenance.

## Current native macOS evidence

Executed against uncommitted M1-04 work over `0d9be55` at .NET SDK 10.0.401,
GitHub.Copilot.SDK 1.0.16 and runtime 1.0.90. `origin/main` was fetched and
remained `305dbb7`; there were no newer main commits to integrate.

- `tools/validate.sh build`: passed formatting and Release warnings-as-errors.
- `tools/validate.sh unit`: 96/96 passed with discovery and coverage reports.
- First full integration run: 182/186 passed; maintenance composition
  unintentionally instantiated the live engine when tests replaced it. Fixed
  composition to discover maintenance on the configured engine, then
  `tools/validate.sh integration`: 186/186 passed with reports/discovery.
- `tools/validate.sh architecture`: 6/6 passed.
- `tools/validate.sh sdk-contracts`: standalone actual-runtime harness passed;
  SDK tests 40/40 passed with discovery.
- `tools/validate.sh docs`: internal Markdown links passed.
- `tools/validate.sh coverage`: passed all enforced thresholds; changed
  executable lines 266/278 (95.7%). Application 956/973 lines and 326/347
  branches; Infrastructure 2719/2930 lines and 678/875 branches; Web 714/724
  lines and 120/148 branches. These are merged unit/integration reports, not E2E
  instrumentation.
- `tools/validate.sh aspire-e2e`: 7/7 passed with discovery.

Targeted experiments initially exposed missing streaming in ResumeSessionConfig
and an incorrect fixture source ID; both corrected before the full results
above. Context-token boundary regression initially assumed the context builder
dropped an entire pair; it actually omits an oversized earlier answer while
retaining a short earlier question. The final regression asserts the precise
omitted answer and measured envelope, not the incorrect pair assumption.

Browser/live reruns, current Linux checks, final review and final validation
revision remain to be recorded. No hosted CI, Linux x64, live cloud, household
data/tools, physical writes or independent Ollama GUI execution are claimed.

## Cumulative independent finding ledger

Intermediate review examined uncommitted work over
`0d9be55dbb6be9b3f149afb59ec1832493950bd3`, independently running 33 SDK,
96 unit and 173 integration tests. Implementation progress is not final review
disposition; no finding is removed because a later round omits it.

| Ref | Severity | Finding | Current disposition/evidence |
| --- | --- | --- | --- |
| M104-R01 | Medium | Missing SDK state failed the current request rather than rebuilding as the owner chose. | Fix implemented; exact-runtime missing-state and production coordinator recovery pass. Pending final revision review. |
| M104-R02 | High | Resumed native history bypassed host context selection/bounds. | Checkpointed selected-history matching and production compaction disabled; real coordinator count/token/oversized-answer boundaries pass. A6 unresolved; pending final review. |
| M104-R03 | High | Pruning snapshots raced registration/concurrent pruning. | Shared ownership gate implemented; active-path maintenance test passes. Deterministic contested-registration coverage/final review remain. |
| M104-R04 | High | Failure cleanup deleted state while runtime shutdown remained uncertain. | Deletion guarded by confirmed shutdown; unresolved ownership blocks reuse. Dedicated failed-shutdown path regression/final review remain. |
| M104-R05 | High | Ready state did not validate SQLite continuity after restore. | Completed-turn and selected-history checkpoint implemented; offline restore excludes rolled-back context. Ready-marker ordering corrected with write-failure regression. Pending final review. |
| M104-R06 | Medium | Idle/orphan copies were cleaned only on new inference. | Startup/hourly maintenance and orphan checkpoint validation implemented; idle/no-inference/retention tests pass. Hosted worker failure-path review remains. |

Prior prompt-reliability finding **R1 — Not applicable** and its rationale remain
in [the original cumulative record](local-chat-prompt-reliability.md#independent-review-finding-ledger).
This work neither reopens nor erases it. No residual-risk acceptance, scope
waiver, PR creation or merge authorization is inferred.
