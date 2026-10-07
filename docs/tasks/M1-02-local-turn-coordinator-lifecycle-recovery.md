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
as a residual risk by the owner. No review commit or PR exists.

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
worktree is uncommitted. The remaining unexercised behavior is the complete
Simulator coordinator turn through Web and process-level shutdown/restart
recovery through the launched Web process; those paths have no public chat API
in M1-02 and are outside its implemented surface. No manual or live E2E
evidence is required for the implemented acceptance criteria.
