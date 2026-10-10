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
| A3 | Authorized Gemma3:4b and llama3.2:latest synthetic engine/exact-replay/native-role comparisons each pass3/3 at 10cc799. Prompt preservation and streaming apply on resume. No universal model/GUI output claim. |
| A4 | Two successive controlled turns reused one disk conversation but launched two separate runtimes, both verified exited. One native macOS sample measured 1,180/221 ms to first delta, 1,232/252 ms total and 95,977,472/92,651,520 working-set bytes. No launch saving or universal speed claim. Active/idle/orphan maintenance contracts pass. |
| A5 | Production coordinator reconstructs missing SDK state, corrupt marker and offline SQLite restore with one new request, deduplication and no rolled-back content. Checkpoint-write failure is Interrupted without a committed answer. Contested registration and delayed actual-runtime shutdown regressions pass; independent fix dispositions are recorded below. |
| A6 | Automatic compaction emits observed events and resumes a summary. Manual public RPC returns summary/context metrics, but accepts a 144,022-character summary with 18,088 current tokens despite TokenLimit 8,192 and sends it onward. Safe production budget/privacy accounting is not established; compaction remains disabled and this criterion is unmet. |
| A7 | Cloud/tool turns remain fresh. Configuration/history changes reset Local state. Actual-runtime configuration invalidation and unadvertised resumed bash denial pass; scoped independent review found no new defect. No live cloud or household execution is claimed. |
| A8 | Production coordinator and required HTTP/SSE integration pass; Aspire API E2E7 on both platforms and native browser23 plus deliberately failing discovery probe pass at 10cc799. |
| A9 | Native and Linux aarch64 core gates pass at the revisions recorded below; Aspire API E2E7 passes on both at 10cc799. Native browser23 and both authorized local-model reruns pass. No hosted CI or Linux browser result is claimed. |
| A10 | Proposed ADR and affected docs updated. Full cumulative ledger retained below; independent reviews recommend Fixed for R01-R08, with R02 limited to its original history bypass. A6 remains unresolved and no owner scope/risk acceptance is inferred. |

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
| M104-R01 | Medium | Missing SDK state failed the current request rather than rebuilding as the owner chose. | Fixed; exact-runtime missing-state and production coordinator recovery pass. Independent recommendation at f3c0bba retained at 25d890b/10cc799. |
| M104-R02 | High | Resumed native history bypassed host context selection/bounds. | Fixed for original bypass through selected-history matching and disabled production compaction; real coordinator count/token/oversized-answer boundaries pass. Independent recommendation at f3c0bba retained at 25d890b/10cc799. This does not resolve A6. |
| M104-R03 | High | Pruning snapshots raced registration/concurrent pruning. | Fixed; shared ownership gate and deterministic contested-registration regression. Independently verified at 25d890b. |
| M104-R04 | High | Failure cleanup deleted state while runtime shutdown remained uncertain. | Fixed; confirmed-shutdown guard, blocked reuse, and full-engine delayed actual-runtime shutdown regression at 10cc799. Independent targeted test1/1 and recommendation at that exact revision; limitations below. |
| M104-R05 | High | Ready state did not validate SQLite continuity after restore. | Fixed; Completed-turn/history checkpoint, offline restore and ready-marker write-failure regressions. Independent recommendation at f3c0bba retained at 25d890b/10cc799. |
| M104-R06 | Medium | Idle/orphan copies were cleaned only on new inference. | Fixed; startup/hourly idle/orphan/retention and hosted worker failure paths. Independently verified at 25d890b. |

Prior prompt-reliability finding **R1 — Not applicable** and its rationale remain
in [the original cumulative record](local-chat-prompt-reliability.md#independent-review-finding-ledger).
This work neither reopens nor erases it. No residual-risk acceptance, scope
waiver, PR creation or merge authorization is inferred.

## Independent review round 2 and subsequent fixes

Independent review examined `f3c0bba569e773b22b025cbbe0296b9991037b48` against
`0d9be55`, independently executing 63 targeted integration checks, all 40 SDK
contracts, 6 architecture checks and Markdown-link validation. It recommended
Fixed for M104-R01, R02 (original history bypass only), R03 (with contested
registration verification gap), R05, and ordinary R06 cleanup; R04 closure still
needs an engine-level uncertain-shutdown regression. A6 remains blocking.

| Ref | Severity | Finding and disposition |
| --- | --- | --- |
| M104-R07 | High | Previously missed: directory deletion in the terminal finally could throw before output completion, stranding enumeration/ownership. Fixed after `f3c0bba`: unconditional channel completion preserves cleanup exceptions. Helper deletion failure and full-engine pre-claim failure regressions pass; independently verified at 25d890b. Full engine/coordinator directory-deletion failure injection remains unexercised, not claimed by the helper test. |
| M104-R08 | Medium | Previously missed: delayed successful shutdown stayed protected forever unless the same conversation submitted another turn. Fixed after `f3c0bba`: maintenance reconciles confirmed shutdown under the gate, removing ownership only after successful deletion. Helper timeout/completion/active/deletion-failure regressions independently verified at 25d890b; full-engine delayed completion added at 10cc799. |

Round-2 recommendations do not erase earlier findings or verification gaps.
The original six rows above retain their history; the original prompt R1 is
unchanged. No owner residual-risk acceptance has occurred.

Subsequent native evidence:

- Explicit synthetic-only `Category=LiveOllama` runs using
  `JARVIS_OLLAMA_MODEL=gemma3:4b` and `llama3.2:latest`: each 3/3 passed.
  Both exercise the production context/engine, exact captured-request replay
  and direct native-role comparison; no tools/cloud/household inputs.
- Full `tools/validate.sh browser-e2e`: **22/23 passed, gate failed**.
  `FirstRunReportsAuthStatusFailureAndSerializesBootstrapThenReauthenticatesWithoutReload`
  failed when Aspire's simulator-model resource failed to start. No root cause
  is established and no rerun/pass is claimed. The deliberate gate probe did not
  execute because the required browser suite failed first.
- After R07/R08 fixes, `tools/validate.sh build` passed; full integration
  **188/188** passed; coverage passed with changed executable lines **297/308
  (96.4%)**, Infrastructure 2751/2959 lines and 679/875 branches.
  `tools/validate.sh docs` passed. The first targeted fix run hit misplaced
  using directives; corrected before these successful runs.

Current blockers: safe production compaction A6; failed browser gate and its
uninvestigated simulator startup; current Linux execution; remaining
engine-level failure/concurrency/maintenance/credential regressions and final
independent dispositions at the final revision. Earlier green results do not
waive these. The coherent checkpoint exceeds the roughly 400-line review target
because it includes actual-runtime capability experiments, recovery,
context-boundary regressions and their evidence; no unrelated refactor is
included.

## Continued regression and platform evidence

At `3834e04`, Linux aarch64 `tools/validate.sh build`, unit (96), integration
(188), architecture (6), SDK contracts (40 plus standalone actual runtime),
coverage and docs all passed in `mcr.microsoft.com/dotnet/sdk:10.0.401-noble`.
The unchanged scripts used private source archives and a synthetic local Git
baseline; no checkout Git configuration or credentials entered the container.
Logs/TRX/coverage are under `artifacts/m104-linux/`. Containers were removed
after exit. Initial Linux attempts failed because Aspire setup needed a writable
tool cache and copying the entire shared package cache exceeded available tmpfs.
The successful run retained the shared package cache read-only and installed
the pinned Aspire CLI in an isolated writable tool cache instead. No shared
Docker storage was pruned or thresholds changed.

The originally failed browser scenario passed in isolation. A subsequent full
`tools/validate.sh browser-e2e` passed **23/23**, including the required discovery
check and deliberately failing browser gate probe. Earlier failure is retained
above; its simulator-startup root cause remains unestablished, not silently
declared fixed. This is native macOS evidence, not Linux browser/E2E evidence.

Additional automated regression work:

- `RegistrationWaitsForContestedPruningAndRetainsTheCompletedConversation`
  deterministically holds the pruning checkpoint read while same-conversation
  registration contests the gate, then verifies native continuity and retained
  state. This fills M104-R03's contested-registration gap.
- `ChangedCompatibilityReconstructsBoundedContextBeforeInference` covers
  selected model, host instructions and context-policy changes with actual
  provider-bound model/context assertions.
- `UnregisteredToolOnResumedSessionCannotInvokeHostDispatcher` forces a `bash`
  proposal without any advertised tool on a resumed session; no host dispatcher
  or tool proposal event is invoked.
- Startup and hourly engine-maintenance failure tests now exercise the actual
  worker. Startup logs only error type and throws a sanitized host-stopping
  exception; hourly failure remains degraded and privacy-filtered.
- M104-R07's unconditional output completion now encloses the entire execution,
  including pre-claim reads. `ClaimReadFailureTerminatesEnumerationAndReleasesConversationOwnership`
  injects that earlier failure and verifies another turn can execute.

Before that last execution-wrapper regression, native build, full integration
**195/195**, SDK **45/45** plus standalone harness, coverage (302/314 changed
lines, 96.2%) and docs passed. Initial targeted adversarial-test compilation used
incorrect fixture property names; corrected before the successful actual-runtime
run. Those results are not claimed for later unvalidated edits. Final affected
gates and fix review remain pending.

The compaction blocker is not just an arbitrary threshold choice: the public
SDK API provides summary results and token metadata, not a reviewed pre-provider
hook that can validate the complete compacted model-facing request with the
host's selected-history and UTF-16 accounting contract. Prompt instructions or
an advisory TokenLimit cannot enforce untrusted summary size. Using a new
provider-interception boundary, changing the context contract, or refreshing SDK
APIs would be material design work requiring compatibility/policy validation,
not a quick fallback. No such architectural change or scope waiver is inferred.

## Reviewed correction revision and remaining shutdown verification

Independent scoped fix review at
`25d890b51a6e13a360566394d8b0366e19794b81` independently ran 11 targeted
integration/actual-runtime checks, found no new high-confidence defect and
recommended Fixed for R03, ordinary/worker R06, R07 and R08. It retained Fixed
recommendations for R01, the original R02 bypass and R05, and retained original
prompt R1 as Not applicable. It left R04 open solely for full-engine
failed-shutdown verification. Production compaction A6 remains blocking.

Linux aarch64 core gates at that exact revision passed: build, unit96,
integration196, architecture6, SDK46 plus standalone harness, coverage and
docs. Current-revision logs/reports are under `artifacts/m104-linux/current-*`.
No Linux Aspire/browser execution or hosted CI is claimed.

The subsequent `ActualRuntimeDelayedShutdownPreservesStateBlocksReuseThenMaintenanceDeletesWithoutInference`
regression fills R04's engine-level gap. An internal constructor seam supplies
the same shutdown component with a controlled completion delay; it is not
exposed through public configuration, DI, HTTP or model input. The first actual
runtime turn is Interrupted after bounded cleanup times out, its private state
survives maintenance, same-conversation reuse rejects without another provider
request, and completion permits no-inference maintenance deletion. A later
fresh turn completes. Actual SDK process stop/disposal still run; the test does
not simulate SDK compatibility. This targeted check passed.

After that regression, native `tools/validate.sh build`, integration197,
architecture6, coverage and docs passed. Changed executable coverage is
318/328 (97.0%); Infrastructure 2765/2971 lines and 687/881 branches.
The prior SDK46/standalone result predates the constructor seam; fresh SDK/Linux
and narrow independent R04 review remain pending.

R01–R08 remain in the cumulative record, including every earlier verification
gap and fix. No owner acceptance or merge decision is inferred. Final readiness
is still blocked by A6 and any outstanding final-revision checks, not by an
unsubstantiated claim that green capability tests make compaction safe.

## Shutdown review and refreshed evidence at 10cc799

Committed shutdown seam/regression revision:
`10cc7990e6b5ea2083b60c49109621a5e3145a2b`. Independent narrow review compared
this exact commit with `25d890b`, read the entire cumulative ledger, executed
the new actual-runtime integration test1/1, recommended R04 Fixed and found no
new concrete finding. The internal seam has no public configuration, DI, HTTP
or model-input activation; production shutdown defaults are unchanged.

The deliberate delay starts after real `ForceStopAsync` returns and before
disposal. It proves uncertain shutdown completion, preserved state, blocked
reuse and later no-inference maintenance cleanup, not a genuinely hung OS
process or permanent SDK shutdown failure. Existing bounded shutdown helper
tests cover timeout/failure behavior within their stated limits. Original
prompt R1 remains Not applicable; no finding is erased or residual risk accepted.

At this exact revision, native `tools/validate.sh sdk-contracts` passed the
standalone actual-runtime harness and SDK46/46; `tools/validate.sh aspire-e2e`
passed7/7 with discovery. Linux aarch64 core build, unit96, integration197,
architecture6, SDK46 plus standalone, coverage and docs all passed using the
same scripts, pins and isolated container procedure described above.
Linux Aspire API E2E also passed7/7 with discovery, with its console evidence
retained in `artifacts/m104-linux/final-aspire-e2e.log`.
Core logs/TRX/coverage are under `artifacts/m104-linux/final-*`.

Linux changed executable coverage is301/311 (96.8%) relative to the synthetic
`0d9be55` baseline; the earlier native318/328 (97.0%) uses `origin/main` and
therefore also includes the carried-forward prompt correction.
Linux Infrastructure coverage is2765/2971 lines and686/881 branches.
These counts are not averaged across platforms or attributed to E2E.
Native `tools/validate.sh browser-e2e` passed23/23 with discovery and the
deliberately failing gate probe; evidence is in `artifacts/m104-final-browser.log`.
The earlier simulator-startup failure and its unestablished cause remain
recorded above; this passing run does not retroactively diagnose it.
`JARVIS_OLLAMA_MODEL=gemma3:4b` and `llama3.2:latest` runs of
`dotnet test tests/PersonalAgent.SdkContractTests/PersonalAgent.SdkContractTests.csproj -c Release --no-restore --filter Category=LiveOllama --logger 'console;verbosity=minimal'`
each passed3/3. Only previously authorized synthetic Local inputs were used.
No hosted CI, Linux browser, Linux x64, live cloud or household evidence is
claimed. Updated evidence documentation passed `tools/validate.sh docs`.

Fetched `origin/main` again; it remains `305dbb7`. No merge/rebase was necessary.
Nothing pushed, no PR created and no merge authorized.

Re-presented the material A6 choice: validate a new Local-only full-request
inference boundary, explicitly defer production compaction, or retain the
blocker without changing transport. The owner was unavailable and no choice
was made. Autonomous continuation is not approval to change accepted
transport/privacy policy or waive A6. Production compaction stays disabled;
this task remains incomplete pending that decision and its resulting evidence.
