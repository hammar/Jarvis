# M1-02: Local turn coordinator, lifecycle, limits, and recovery

## Objective and acceptance

Implement issue #12 as the local-only turn use case for M1. An Application
caller can submit a stable, bounded request, observe ordered durable events,
and cancel a turn. SQLite remains authoritative; the Copilot runtime remains
replaceable. Limits, terminal outcomes, recovery, and overload behavior are
host-enforced and explicit.

**Specification:** §§5, 9, 12–14, 18–21. **Acceptance scenarios:** one active
turn per conversation; four active turns globally; bounded 20-turn queue;
validated 120-second interactive deadline and permitted override; duplicate
client request IDs return the original turn; ordered reconnectable events;
truthful cancellation, timeout, runtime failure, shutdown and restart
outcomes; safe readiness and privacy-filtered audit. Depends on M1-01 (#7),
T03 (#4), and T04 (#6). Supplies the Application use case for M1-03 (#11).

## Scope

- Coordinate the existing LocalOnly router/context builder, typed turn events,
  durable conversation store, and isolated Copilot engine behind Application
  use cases. One application turn ID is independent of the engine session.
- Persist accepted submission, user message, client request identity, and
  initial turn/event state transactionally. Matching retries return the
  original turn. Reusing an ID with a conflicting request is rejected
  explicitly. The request identity is scoped to its conversation.
- Add a bounded scheduler: at most one executing turn per conversation, four
  executing turns globally, and at most 20 queued requests. Overflow is a
  typed rejection; no accepted turn is silently dropped.
- Enforce an interactive deadline of 120 seconds by default, with validated
  trusted configuration for a permitted provider/model override. Cancellation
  and shutdown are bounded and persist terminal outcomes independently of a
  cancelled caller token.
- Persist ordered events and final assistant messages without duplication.
  Event reads are bounded and resume from a sequence cursor. A slow reader does
  not control model execution.
- Resolve every recovered nonterminal turn as `Interrupted`; never automatically
  replay an uncertain inference request. Treat process/runtime failure and
  uncertain cleanup as interruption, not successful completion.
- Register a fail-closed dispatcher: M1 has no tools, and any unexpected
  proposal is explicitly denied rather than accepted as a successful no-op.
- Wire explicit local provider settings and named secret resolution in Web
  composition. Readiness reports database, policy, runtime and configuration
  availability; model connectivity is separate. Do not log prompts, private
  context, credentials, or provider payloads.
- Use controlled endpoints and isolated data for Simulator/E2E. Test controls
  must not be activated by model input or exposed in live profiles.

## Exclusions

No authenticated HTTP endpoints, SSE transport, browser chat UI, Home
Assistant, memory, cloud consent/provider, reminders, job worker, or full M4
acceptance/demo. M1-03 owns the public API and UI. No retry of interrupted
turns, no tool authorization surface beyond explicit denial, and no live
household/cloud credential use.

## Owned files

- `docs/tasks/M1-02-local-turn-coordinator-lifecycle-recovery.md`
- `src/PersonalAgent.Application/Contracts.cs` and new Application
  `TurnCoordination` use-case/contracts
- `src/PersonalAgent.Infrastructure/Persistence/` conversation storage and a
  forward-only migration for durable request IDs/atomic submission
- `src/PersonalAgent.Infrastructure/AgentEngine/Copilot/` only where adapter
  integration requires it; keep SDK types confined to this namespace
- `src/PersonalAgent.Web/Program.cs` and narrowly scoped readiness/lifecycle
  composition
- `tests/PersonalAgent.UnitTests/`,
  `tests/PersonalAgent.IntegrationTests/`,
  `tests/PersonalAgent.SdkContractTests/`, and `tests/PersonalAgent.E2ETests/`
- `tools/PersonalAgent.Validation/Program.cs` for critical-module ownership
- `src/PersonalAgent.Application/README.md`,
  `src/PersonalAgent.Infrastructure/README.md`, `docs/architecture.md`,
  `docs/development.md`, `docs/operations.md`, and `docs/testing.md`

## Dependencies and decisions

- Reuse the existing `IAgentEngine`, `IModelRouter`, `IContextBuilder`,
  `IConversationStore`, typed events, optimistic turn versions, terminal
  compare-and-swap storage, and bounded Copilot lifecycle. Avoid duplicate
  state machines.
- A narrow SQLite migration is necessary for durable per-conversation request
  ID deduplication; the owner confirmed this scope. Keep schema evolution
  forward-only and preserve existing data.
- Application owns queue policy and lifecycle; Infrastructure implements
  persistence and engine adapters; Web is the composition root. No SQLite,
  ASP.NET Core, Aspire, or Copilot types enter Domain/Application.
- Startup recovery uses bounded reads/writes and terminal CAS, including
  failure reporting; it never re-enqueues persisted nonterminal work.
- Test behavior through observable persisted states, events, messages, and
  process/readiness results, not implementation mirrors or arbitrary sleeps.

## Tests and documentation

Unit tests cover request validation, scheduling/queue boundaries, global and
per-conversation concurrency, duplicate and conflicting request IDs, overload,
deadlines, cancellation races, terminal outcomes, and no-tool denial. Real
SQLite integration tests cover atomic submission/deduplication, monotonic event
sequences, final-message idempotency, bounded retrieval, restart recovery, and
concurrent callers. Actual SDK contract tests use only controlled endpoints.
Aspire E2E launches real processes and verifies production profile composition,
readiness failure reporting, and bounded graceful shutdown; no household or
cloud credentials are required.

Add scenario mappings and critical coverage ownership for the coordinator,
recovery, and lifecycle modules. Preserve the Domain/Application unit-only
thresholds, combined runtime/assembly thresholds, changed-line threshold,
architecture negative fixtures, actual SDK contract gate, Aspire/browser E2E
gates, and docs checks. Document configuration, restart behavior, queue/deadline
semantics, status outcomes, and the absence of an automatic replay guarantee.

## Completion criteria

- The production Application caller can submit and cancel local turns and read
  ordered durable events; duplicate submissions return the original turn
  across process restart.
- Per-conversation/global concurrency, the 20-item queue, request/deadline
  validation, explicit overload, cancellation, timeout, crash and shutdown
  outcomes are enforced and regression-tested.
- Recovery persists truthful interruption and never automatically replays a
  nonterminal turn. At most one final assistant message is persisted.
- No tool proposal is acknowledged as successful without dispatch. Production
  local provider configuration is explicit and secret values remain server-side.
- Required checks pass with coverage thresholds and architecture boundaries
  intact. Handoff records exact commands/results, schema impact, acceptance
  scenarios, independent review findings/dispositions, and unexercised behavior.

## Status

Implementation and automated acceptance checks are present in the worktree.
The coordinator measures the deadline from durable acceptance, expires queued
and deferred work independently, checks the remaining budget after context
construction, and propagates deadline cancellation to active engine execution.
An independent reviewer re-reviewed the deadline monitor and found R5 resolved.
R6 identified that forwarding deadline cancellation as the engine's ordinary
caller token could persist `Cancelled` before the coordinator persisted the
deadline outcome. The engine request now carries deadline cancellation
separately from caller cancellation; the Copilot adapter links both for
execution while classifying deadline expiry as `Interrupted`. The adapter
records the first observed cancellation cause after observer registration;
signals already present during registration use explicit tie precedence:
host shutdown, accepted deadline, then caller cancellation. The actual SDK
adapter regressions cover deadline-then-caller cancellation during stalled
inference and pre-cancelled caller/system-signal pairs. Independent re-review
confirmed the R6 fix with no remaining significant issue.
Regressions cover an expired item still in the channel, FIFO deferred work,
deadline expiry during an uncooperative context build, and active engine
deadline cancellation.

**Schema impact:** forward-only migration
`003-turn-request-idempotency.sql` adds durable conversation-scoped request-ID
deduplication. Submission, user-message persistence, and initial turn/event
state are atomic; no existing conversation data is intentionally removed.

**Review ledger:** findings R1–R4 from the independent review are fixed:
R1 deferred FIFO and queue-capacity accounting; R2 durable clarification event
and assistant message; R3 Simulator provider discovery/configuration wiring;
R4 read-only SQLite readiness. R5's deadline lifecycle gap is fixed and
independently confirmed. R3's full Simulator coordinator turn through Web
remains unverified because M1-02 exposes no public chat API; process-level
shutdown/restart recovery through the launched Web process is also not covered
by the current Aspire tests. The existing unit, integration, SDK-contract, and
Aspire layers exercise their respective narrower behaviors. R6 is fixed and
independently re-reviewed on the current worktree; no finding has been accepted
as a residual risk by the owner. The initial implementation is committed as
`e81bf294ed15f3cbb8317f6999038efecc4b713d` in PR #35.

**R7 (medium, previously missed): cancellation publication ordering.**
Separate linked cancellation sources could wake execution/event persistence
before the source callback recorded the cancellation cause, allowing a
deadline-triggered interruption to be persisted as completed. Fixed by
recording the cause before cancelling execution/events, while preserving
tool-budget rejection-event draining. Independent agentic review examined
setup, disposal, reentrancy, host shutdown, and both budget failure paths and
reported no significant issue. The actual SDK suite passed 19/19; the native
runtime-crash regression passed five consecutive focused runs, each checking
one interruption, no completion, durable interrupted state, and a fresh
successful subsequent turn. Immediate runtime-kill detection remains
unsupported by the compatibility harness; bounded deadline interruption is
the exercised behavior, not an immediate-detection guarantee.

**Linux ARM64 validation after R7:** using the repository's .NET
10.0.401 SDK image and an isolated host-backed Linux build directory:

- `tools/validate.sh build`: passed; Release build, format, nullable, and
  analyzers completed with zero warnings/errors.
- `tools/validate.sh architecture`: passed, 6/6.
- `tools/validate.sh unit`: passed, 56/56.
- `tools/validate.sh integration`: passed, 91/91.
- `tools/validate.sh sdk-contracts`: passed, 19/19; the compatibility harness
  exercised the pinned Copilot runtime against controlled endpoints. Runtime
  crash caller completion required the bounded send deadline, and immediate
  turn interruption after runtime kill was false. These are recorded
  observations, not claimed successes.
- `tools/validate.sh coverage` with the latest Linux reports: passed after
  normalizing isolated OpenCover source paths to the worktree. Application
  measured 97.5% lines (861/883) and 92.6% branches (274/296); Infrastructure
  measured 91.2% lines (2203/2416) and 73.0% branches (512/701). Changed
  executable lines measured 92.9% (1039/1119). All critical-module thresholds
  passed; counts differ slightly from native macOS execution.
- `tools/validate.sh aspire-e2e`: passed, 3/3.
- `tools/validate.sh browser-e2e`: passed, 1/1; the deliberately failing
  browser-gate probe was discovered and rejected as expected. Earlier APT
  signature/dependency failures were resolved using host-backed build storage
  to avoid exhausting Docker's filesystem, with verified HTTPS Ubuntu
  sources. No signature checks were disabled and no shared Docker resources
  were pruned.
- `tools/validate.sh docs`: passed; all internal Markdown links resolve.

**macOS revalidation after R7:** `tools/validate.sh build`, `sdk-contracts`
(19/19), `unit` (56/56), `integration` with XPlat coverage (91/91), and
`coverage`, `architecture` (6/6), `aspire-e2e` (3/3), `browser-e2e` (1/1 with
the deliberately failing gate probe), and `docs` passed. Five focused native
runtime-crash runs also passed. Earlier
collector stalls were resolved by recreating only generated test output
directories containing thousands of numbered duplicate assemblies. Duplicate
generated NuGet imports also caused a build failure and were removed; no
source or durable application data was deleted. Linux re-ran the corresponding
runtime gates after R7. A temporary Linux source snapshot initially made native
architecture discovery report an extra SDK-validation project; the snapshot
was removed after retaining its validation reports, and the unchanged
architecture check passed.

The native combined coverage gate measured Application 97.6% lines (862/883)
and 93.2% branches (276/296), Infrastructure 91.2% lines (2203/2416) and
73.2% branches (513/701), Web 98.6%/77.5%, ServiceDefaults 100%/100%, and
Domain 100% lines with branches N/A. Changed executable lines measured
92.9% (1040/1119). The critical-module and gate self-tests passed.

No live Ollama/cloud credentials or household/device writes were used. The
initial implementation is published in PR #35. The remaining unexercised behavior is the complete
Simulator coordinator turn through Web and process-level shutdown/restart
recovery through the launched Web process; those paths have no public chat API
in M1-02 and are outside its implemented surface. No manual or live E2E
evidence is required for the implemented acceptance criteria.

## PR review follow-up: R8-R13

All six medium findings were labelled **Previously missed** by the PR reviewer;
they extend, rather than replace, the R1-R7 ledger above. The follow-up changes
retain the existing schema and add an optional turn boundary to
`IConversationStore.ReadRecentAsync`; callers without a boundary retain ordinary
append-order history. All implementation and forwarding adapters are updated.

| Finding | Disposition and regression evidence |
| --- | --- |
| R8: promoted deferred work can be overtaken by newer channel work | Fixed: a newer same-conversation item defers while any older queued item remains. `DeferredTurnPrecedesNewerChannelTurnWhenAllOtherWorkersAreBusy` exercises the mixed channel/deferred case with three other workers occupied. |
| R9: later accepted user messages enter earlier context and displace valid history | Fixed: SQLite filters by predecessor turn acceptance before bounded selection, including predecessor answers appended after current acceptance. `ContextExcludesLaterSubmissionsButIncludesLateAnswerFromPrecedingTurn` uses the real builder/store with 40 later submissions, validates missing/foreign boundaries and preserves unbounded-boundary append ordering. |
| R10: cancelled/expired pending work retains queue reservations | Fixed: terminal persistence removes the pending reservation exactly once, stops monitoring, and drains stale channel/deferred entries only for disposal. `TerminalPendingTurnsImmediatelyReleaseCapacity` covers cancellation and expiry in both queue locations, with unrelated execution proving deferral. |
| R11: delayed routing/context cancellation reclassifies the original cause | Fixed: the first cause is recorded before signalling execution; owner/deadline signals are published through application-owned sources. `DelayedContextPreservesFirstDeadlineOrOwnerCancellation` covers both signal orders while context cleanup is held, using a queued deadline witness rather than an arbitrary delay. |
| R12: unhealthy coordination still admits durable submissions | Fixed: admission checks actual worker/monitor readiness before creating a turn. Both persistence-failure health regressions assert that a distinct request is rejected without another stored message. |
| R13: cancelled workers remain invisible to readiness | Fixed: unexpected worker completion, including cancellation, fails readiness. `TerminalPersistenceTimeoutCancelsWorkerAndRejectsFurtherSubmissions` exercises the real five-second terminal-write timeout and verifies readiness, admission and shutdown failure. |

Native macOS follow-up commands passed: `tools/validate.sh build`, `unit`
(64/64), `integration` (92/92), `coverage`, `architecture` (6/6),
`sdk-contracts` (19/19 plus pinned runtime compatibility harness), `aspire-e2e`
(3/3), `browser-e2e` (1/1 plus deliberate failure probe), and `docs`.
The final SQLite boundary assertions also passed in a focused integration run.
Combined Application coverage is 97.7% lines (861/881), 93.7% branches
(284/303); Infrastructure is 91.3% lines (2222/2435), 73.3% branches (517/705).
Changed executable lines are 93.2% (1064/1142); all critical-module thresholds
and negative gate fixtures passed unchanged.

`origin/main` was fetched before this follow-up; no new base commits required
integration. Linux follow-up checks will run against the pushed revision in CI;
the earlier Linux results above apply to the initial implementation, not this
revision. Independent agentic review of
`f387490b22143aadbed600aa059bd2462b616d9e` found no significant issue and
no residual R8-R13 finding. The reviewer separately ran eight focused
coordinator unit cases and the real SQLite context-boundary integration
regression successfully; it did not re-run the full suite or Linux checks.
No live credentials
or physical writes were used; the previously disclosed M1-02 surface limits
remain unchanged.

## PR review follow-up: R14-R17

The review of `4fb04da` added four **Previously missed** findings, retained
alongside the complete R1-R13 ledger above. R14-R16 are medium; R17 is high.
All CI checks on `4fb04da` passed before these changes. This revision has no
public-contract or schema change and does not expand M1-02's product scope.

| Finding | Disposition and regression evidence |
| --- | --- |
| R14: cause selection and active-engine publication can race | Fixed: one winner selects its engine token, losing owner/deadline/shutdown signals are suppressed, and direct engine cancel/stop is dispatched only for its corresponding winning cause. Winning engine publication precedes routing/context cleanup. Disposal defers while publication is in progress to handle synchronous engine completion. `ActiveEngineOnlyReceivesWinningCauseWhileCancellationPublicationIsPaused` pauses winning-signal delivery before the active engine's observer in both orders and verifies no losing signal or losing direct cancel becomes terminal. |
| R15: cancelled requests accumulate in an unbounded channel while workers are occupied | Fixed: remove the request-bearing channel and deferred queue; keep only the bounded pending set and at most four coalesced wake-up bytes. Workers select the oldest eligible pending item. Terminal pending work is removed and reclaimed by its deadline monitor before its slot is released; owner cancellation awaits cleanup. `RepeatedPendingCancellationReclaimsRequestResourcesWhileAllWorkersRemainOccupied` performs 100 cancel/resubmit cycles, verifies work items are collectible without freeing execution workers, and then verifies the 20-pending limit still rejects overflow. Existing FIFO, starvation, shutdown and cancellation/expiry capacity regressions remain passing. This supersedes R8/R10's channel/deferred mechanics, not their behavioral guarantees. |
| R16: CASE ordering forces whole-conversation sorting for ordinary history reads | Fixed: boundary-free reads use the original conversation/sequence-indexed SQL; only boundary reads use turn-aware ordering. The real SQLite context regression examines the exact production boundary-free query plan, asserting indexed search and only one outer bounded-page sort, while retaining both ordering behaviors. |
| R17: bare local Ollama origins miss the `/v1` API prefix | Fixed: Web maps an empty/root path to `/v1` and preserves explicit API prefixes. Four `LocalProviderCompositionExecutesActualRuntimeAgainstStrictApiPath` cases execute the actual pinned Copilot runtime through production Local/Hybrid DI against a strict controlled endpoint, asserting completion, one stored answer and the exact request path for bare, root, `/v1` and custom-prefix configuration. |

The removable scheduler is a cohesive replacement for the request-retaining
channel, not an unrelated refactor. Runtime tests share a named process-isolation
collection because the crash contract identifies its own child using the set
of newly launched Copilot processes. The first full integration run exposed
multiple new child processes when the new production-composition runtime tests
ran concurrently with that contract. The affected classes now serialize within
one xUnit collection; no test, assertion, gate or global parallelization policy
is disabled. All other test collections retain their existing parallelism.

Native validation commands passed: `tools/validate.sh build`, `unit` (67/67),
`integration` (96/96), `coverage`, `architecture` (6/6), `sdk-contracts` (19/19
and pinned runtime harness), `aspire-e2e` (3/3), `browser-e2e` (1/1 and deliberate
failure probe), and `docs`. Application coverage is 98.0% lines (871/889),
93.5% branches (261/279); Infrastructure is 91.2% lines (2221/2434), 73.4%
branches (519/707); changed executable lines are 93.4% (1074/1150).
Critical-module thresholds and negative gate fixtures passed unchanged.
Independent review of `90b02099e6aabd19c80b2c07356e2c5f43b99655`
confirmed the focused coordinator and production-path/query-plan tests, but
identified R18 below. Linux CI against this new revision is pending. Earlier
CI evidence applies to `4fb04da`, not this revision. No live provider credentials,
cloud disclosure, household data or physical writes were used.

## Independent review follow-up: R18

**R18 (medium, Previously missed):** the production adapter's independent
deadline bypasses the coordinator's selected cancellation cause. The reviewer
reproduced an owner winner being durably persisted as `Interrupted` while a
LIFO callback held owner-signal delivery across the adapter's timeout. Its
reproduction stalled controlled secret resolution before SDK startup; it was
production-adapter evidence, not actual SDK inference validation.

Fixed by adding host-owned `CancellationCause` and the optional trusted
`AgentTurnRequest.ResolveDeadlineCancellation` delegate. The coordinator's
atomic winner selection also handles engine-local expiry; the adapter maps
the resulting selected cause before cancelling execution. Its independent
finite timer remains enabled, including for standalone requests without an
arbiter. No SDK/provider types enter Application and no schema changes.

`ProductionEngineDeadlineUsesFirstCauseWhileWinningSignalPublicationIsPaused`
exercises the real coordinator, real SQLite and production adapter with both
owner and shutdown winners, held across the adapter's timer by event-based
callback barriers. It asserts durable classification and a single terminal
event, with zero provider requests; the SDK is deliberately not launched.
`DeadlineBoundsBlockedEventPersistenceWithoutMisclassifyingCancellation`
now covers both standalone and host-arbitrated engine deadlines.

Final native validation passed: `tools/validate.sh build`, `unit` (67/67),
`integration` (99/99), `coverage`, `architecture` (6/6), `sdk-contracts`
(20/20 plus pinned runtime harness), `aspire-e2e` (3/3), `browser-e2e`
(1/1 and deliberate failure probe), and `docs`. Application measured 97.9%
lines (874/893), 93.2% branches (260/279); Infrastructure measured 91.6%
lines (2236/2442), 73.6% branches (522/709); changed executable lines are
93.9% (1091/1162). All required thresholds and negative fixtures passed.
An attempted simultaneous pair of focused builds briefly collided on shared
generated reference assemblies; sequential reruns passed without deleting
data or changing validation policy. Independent agentic re-review of exact
commit `6c1842f7cf99421e2cf4baa03fe839bb85f04658` found no significant
issue and confirmed R18 resolved without changing R1-R17 dispositions.
It independently passed 12 focused native cases: two production
coordinator/SQLite/adapter held-signal regressions; four adapter
deadline/precedence cases; five coordinator cancellation/deadline cases; and
one actual-runtime bounded-output consumer-disposal case. It did not re-run
the full gates/harness or Linux CI, and did not exhaustively exercise all
callback interleavings. The held-shutdown test checks durable interruption and
terminal-event uniqueness, not its reason code. Linux CI against the new
revision remains pending. No finding is accepted as a residual risk by the
owner; no live credentials or physical writes were used.

## PR review follow-up: R19-R20

The review of `16ab471` adds two high **Previously missed** findings,
retained alongside R1-R18 rather than replacing any earlier disposition.
Both concern arbitrary synchronous cancellation callbacks running under
the coordinator lifecycle lock. Required CI on that earlier revision passed;
it is not validation of this follow-up.

| Finding | Disposition and regression evidence |
| --- | --- |
| R19: shutdown callback publication holds the lifecycle lock before its timeout starts | Fixed: start the production 15-second budget before selecting shutdown; select causes and close admission/queue under the lock, then await tracked asynchronous publication and direct engine stops outside it. `HeldPublicationDoesNotBlockReadinessAndShutdownOrCallerBounds` holds a callback across the actual timeout, verifies responsive readiness and rejection of new submissions, then releases it and verifies truthful terminal state and completed workers. Its caller-cancelled shutdown case verifies that caller cancellation bounds the wait too. |
| R20: owner cancellation holds the lifecycle lock and ignores the caller's wait bound | Fixed: select the winner under the lock without invoking callbacks; await its publication outside the lock with the caller token. Active and pending cases in the same theory verify bounded caller return, responsive readiness and resources remaining usable until callback release. Pending terminal persistence/reclamation belongs exclusively to the monitor, so abandoning a caller wait cannot strand accepted work. |

Publication is owned by a retained task. Winning engine callbacks precede
execution cleanup signals; losing causes still cannot publish or dispatch
direct engine cancellation. Callback errors still wake execution and are
surfaced, including both errors when engine and execution callbacks fail.
Workers and monitors await publication before disposal; monitor reclamation
runs in `finally`. `PublicationCallbackFailuresSurfaceWithoutStrandingPendingCleanup`
exercises one and two callback failures, explicit unhealthy readiness,
terminal persistence, pending removal and shutdown error propagation.
`WorkerClaimDuringPendingCancellationPersistenceReclaimsExactlyOnce` holds
the monitor's terminal write while a worker claims the cancelled request,
checks a single terminal event without inference or direct engine cancel,
and verifies that exactly 20 pending slots remain available afterward.

`MissingAcceptedTurnReadSurfacesInterruptionWithoutCallingEngine` verifies
explicit interruption on a missing accepted record; the concurrent-completion
case verifies no inference and idempotent cancellation of completed/unknown
turns. Terminal-event waits avoid consuming test-store read mutations.
The repeated compare-and-swap conflict regression now checks the retained
monitor failure and unhealthy readiness; simultaneous persistence owners no
longer consume those fixture conflicts. Caller-cancelled shutdown intentionally
returns promptly while owned persistence continues, rather than waiting for
cleanup before reporting cancellation.

There is no public API signature or schema change. Contract XML, architecture,
operations and testing documentation describe the bounded wait and retained
cleanup semantics. This cohesive lifecycle fix adds about 400 handwritten
lines, primarily failure-path/concurrency tests; it does not expand scope.

Final native macOS commands passed: `tools/validate.sh build` (zero
warnings/errors), `unit` (76/76), `integration` (99/99), `coverage`,
`architecture` (6/6), `sdk-contracts` (20/20 and pinned runtime harness),
`aspire-e2e` (3/3), `browser-e2e` (1/1 and deliberate failure probe), and
`docs`. Application measured 97.2% lines (978/1006), 93.1% branches (269/289);
Infrastructure measured 91.5% lines (2234/2442), 73.6% branches (522/709);
changed executable lines measured 94.0% (1134/1207). All critical-module
thresholds and negative gate fixtures passed unchanged. Initial intermediate
coverage runs correctly rejected 88.4% and 89.9% coordinator branch coverage;
the additional observable failure/race cases above passed the unchanged gate.
The real SQLite/production-adapter held-owner/shutdown regressions also
passed with the asynchronous publication implementation; they stall before
SDK startup and are not live inference evidence.

Independent review of the exact follow-up commit remains pending at this
recording point. Linux validation of this revision remains pending CI; prior
green CI applies only to `16ab471`. No credentials, cloud disclosure,
household data or physical writes were used. Timeout or caller cancellation
does not prove callback/worker cleanup has finished: stalled callbacks retain
their owned resources until they return, and bounded failure is reported
explicitly. No finding is accepted as a residual risk by the owner.
