# T04: Copilot adapter, runtime isolation, streaming, and cancellation

## Objective and acceptance

Implement the pinned Copilot SDK behind the frozen `IAgentEngine` contract.
Application state remains authoritative in SQLite; Copilot client/session
state is isolated and replaceable.

**Specification:** §§4–5, §14 (SDK process crash and event-stream reconnect),
and T04 in §15. **Issue:** #6. **Dependencies:** T01 (#2) and T02 (#3).
Accepted ADR 0001 establishes the native Copilot runtime as a trusted dependency
for the initial MVP; this task does not claim native OS-level network
containment.

## Scope

- Implement `PersonalAgent.Infrastructure.AgentEngine.Copilot.CopilotAgentEngine`
  with host-selected provider configuration, context and tool definitions,
  typed event streaming, `IToolDispatcher` callbacks, cancellation, deadline
  and tool-call budget enforcement, bounded runtime stop/disposal, and a single
  truthful terminal event.
- Run one fresh Copilot client and session per turn in a dedicated private
  runtime/work directory. Use `CopilotClientMode.Empty`, `UseLoggedInUser=false`,
  disabled session storage, a sanitized child environment, an explicit provider,
  and only the request's registered custom tools.
- Persist ordered typed events using `IConversationStore`; atomically commit a
  terminal status and its terminal event through the Infrastructure persistence
  capability without changing the frozen Application contract.
- Resolve optional provider API keys only from `SecretReference` through the
  host-owned `ISecretResolver`; never place a secret in the child environment
  or an event/error message.
- Add actual pinned-runtime contracts against deterministic loopback
  OpenAI-compatible fixtures and real SQLite status/sequence tests.

## Exclusions

No model routing, context selection/privacy/consent policy, tool or approval
policy implementation, Home Assistant integration, UI/SSE endpoint,
turn-coordinator workflow, live provider credentials, or native OS-enforced
runtime containment. The adapter only forwards tool proposals to
`IToolDispatcher`; it does not make a proposal authorized. The public
Application contract is unchanged.

## Owned files

- `src/PersonalAgent.Infrastructure/AgentEngine/Copilot/`
- `src/PersonalAgent.Infrastructure/Persistence/IAtomicTurnOutcomeStore.cs`
- `src/PersonalAgent.Infrastructure/Persistence/SqliteConversationStore.cs`
- `src/PersonalAgent.Infrastructure/PersonalAgent.Infrastructure.csproj`
- `src/PersonalAgent.Infrastructure/README.md`
- `tests/PersonalAgent.SdkContractTests/`
- `tests/PersonalAgent.IntegrationTests/` (linked actual-runtime adapter
  contracts are also run under the integration coverage collector)
- `tools/validate.sh` and `tools/PersonalAgent.Validation/Program.cs`
  (full-base changed-line measurement and critical cancellation coverage)
- `tools/PersonalAgent.Validation/Program.cs` (critical turn state-machine
  coverage ownership)
- `docs/architecture.md`, `docs/testing.md`, and this brief

## Decisions and assumptions

- Preserve GitHub.Copilot SDK version `1.0.16` and bundled runtime `1.0.90`;
  preserve .NET SDK `10.0.401` from `global.json`.
- Provider choice comes from `AgentTurnRequest.Provider`; endpoint, model,
  protocol, and optional secret reference are trusted host configuration.
  Local provider URIs must be loopback; non-loopback cloud URIs must use HTTPS.
- The SDK requires an API-key string in its provider configuration. The
  adapter resolves references server-side, passes that value only to the SDK,
  and does not log or add it to the child environment.
- SDK process death may not promptly complete an in-flight send. The
  application deadline bounds the caller wait; cancellation/timeout triggers
  bounded abort and process stop. Failure to clean up is reported as an
  interruption rather than success.
- Every event is appended through the store's atomic per-turn sequence
  operation before it is yielded. SSE reconnection and duplicate-final-message
  coordination remain the responsibility of the later application/API layer.
- A terminal status update and its typed terminal event commit in one SQLite
  transaction with the expected turn version; the frozen Application contract
  remains unchanged.
- The runtime directory is private on Unix. This protects state at rest from
  other local users but is not a sandbox and does not establish network
  containment.

## Tests and documentation

- `CopilotAgentEngineContractTests`: actual SDK/runtime streaming,
  Local/Cloud provider selection and transcript separation, ambient workspace
  instruction exclusion, exact tool catalog and tool-result forwarding,
  host secret resolution without event persistence, invalid endpoint rejection,
  duplicate-turn prevention, cancellation, bounded tool budget, provider failure
  redaction, runtime process crash/interruption, and a fresh successful runtime
  on the next turn.
- `CopilotTurnStateMachineTests`: running/terminal state changes, missing and
  already-terminal turn failures, expected-version updates, and every terminal
  signal mapping against real SQLite.
- Architecture/build/format, unit, integration, SDK contracts, coverage, Aspire
  E2E, browser E2E, and docs validation use the existing `tools/validate.sh`
  gates. No live cloud/Ollama/device credentials are required.

## Executed validation evidence

- `tools/validate.sh build`: passed format verification and Release solution
  build with nullable enabled and zero warnings/errors.
- `tools/validate.sh architecture`: 6/6 tests passed.
- `tools/validate.sh unit`: 8/8 discovered tests passed.
- `tools/validate.sh integration`: 70/70 discovered tests passed.
- `tools/validate.sh sdk-contracts`: pinned SDK/runtime validation harness
  passed; 12/12 adapter SDK contract tests passed.
- `tools/validate.sh coverage`: passed using the `origin/main...HEAD`
  merge-base comparison plus tracked worktree changes. Changed executable
  lines 600/630 (95.2%); Infrastructure 1788/1933 lines (92.5%) and 455/585
  branches (77.8%).
  The gate enforces 95% line / 90% branch
  thresholds for both the terminal state machine and active-turn cancellation
  and runtime lifecycle module.
- `tools/validate.sh aspire-e2e`: 2/2 tests passed.
- `tools/validate.sh browser-e2e`: 1/1 browser test passed; the deliberate
  failing-browser probe failed as expected and was recognized by the gate.
- `tools/validate.sh docs`: passed internal Markdown link validation.

Validation ran on macOS with the pinned .NET SDK and deterministic loopback
providers. Linux CI, live cloud credentials, native Ollama, and physical-device
behavior were not exercised; they are outside this task's required credential-
free checks.

## Independent review finding ledger

Initial pre-PR worktree review findings and dispositions:

| Reference | Finding | Disposition and evidence |
|---|---|---|
| T04-R1 | Terminal status and terminal event could be persisted in separate transactions. | Fixed with `IAtomicTurnOutcomeStore`; stale-version and combined status/event behavior are covered by `CopilotTurnStateMachineTests.TurnStateMachineAppliesRunningAndTerminalTransitionsWithCompareAndSwap`. Release integration and coverage gates passed after the change. |
| T04-R2 | A failed/timed-out runtime stop could skip client disposal and prevent a later cleanup attempt. | Fixed by serializing client cleanup and attempting bounded disposal in a `finally` path. Release build, integration, SDK runtime contracts, and coverage gates passed after the change. |
| T04-R3 | Atomic compare-and-swap could rewrite an already-terminal turn when called with its current version. | Fixed by excluding all terminal statuses in both status-update predicates. The SQLite integration regression verifies stale and current versions cannot change the status or append a second event. |
| T04-R4 | Session/client cleanup exceptions were reported as generic engine failures rather than interrupted runtime cleanup. | Fixed with a cleanup exception classifier and interruption signal; cleanup now attempts all resources even when an earlier cleanup action throws. `RuntimeCleanupAttemptsEveryResourceAndReportsCleanupFailure` covers all cleanup failures and their classification. |
| T04-R5 | No disconnect/reconnect cursor test proved ordered replay without repeating the tool or terminal event. | Fixed in `ActualRuntimeInvokesOnlyRegisteredToolAndForwardsHostOutcome`, which resumes SQLite event replay after the observed prefix and verifies sequence, event order, one tool dispatch, and one terminal event. |
| T04-R6 | Changed-line coverage evidence was stale and calculated inconsistently. | The earlier 39/39 uncommitted-diff metric was superseded; see T04-R9 for the corrected full-PR comparison against `origin/main` and its final changed-line result. |
| T04-R7 | Session creation did not observe turn cancellation/deadline. | Fixed by awaiting `CreateSessionAsync` with the linked turn token; the SDK contract/runtime suite passes with the bounded session lifecycle. |
| T04-R8 | Interrupted terminal turns were returned as nonterminal recovery work. | Fixed by excluding `Interrupted` in `ReadNonterminalTurnsAsync`; the SQLite restart/recovery test now verifies interrupted turns are not returned. Retention intentionally continues to preserve interrupted conversations, as asserted by its existing durability test. |
| T04-R9 | Local changed-line coverage used `git diff HEAD`, omitting the PR's existing changes. | Fixed the validator comparison to use the merge-base range `origin/<base>...HEAD` and made `tools/validate.sh coverage` require the base ref for local runs. The recorded coverage run compares only the PR's changes against `origin/main`. |
| T04-R10 | Critical coverage ownership did not include the active cancellation/runtime lifecycle implementation. | Extracted the active-turn cancellation and runtime lifecycle into `CopilotActiveTurn.cs`, added cancellation and precedence tests, and assigned it a 95% line / 90% branch critical-module gate. Final full-PR coverage passed both critical module gates. |
| T04-R11 | Changed-line coverage compared directly to the base tip and could include unrelated base-branch commits. | Updated the validator to use the three-dot merge-base comparison for PR changes; `tools/validate.sh coverage` passed against `origin/main...HEAD` with tracked worktree changes included. |
| T04-R12 | The async iterator awaited the execution task twice, potentially replacing the original failure site during cleanup. | It now awaits execution once in the iterator's `finally` path; output completion no longer faults the reader independently, so the single await propagates execution errors. Release build and all 64 integration tests passed. |
| T04-R13 | Terminal outcome persistence hard-coded `CancellationToken.None`, preventing a bounded wait. | `SetTerminalOutcomeAsync` now accepts and forwards a token to both persistence operations. The engine supplies a dedicated five-second persistence timeout, preserving terminal writes after turn cancellation while bounding shutdown; a canceled-token SQLite regression passes in the integration suite. |
| T04-R14 | A tool-budget cancellation racing with caller/host cancellation was classified as caller cancellation due to catch ordering. | The operation-canceled handlers now give the recorded tool-budget failure precedence; the terminal-precedence regression verifies budget wins over cancellation and deadline. |
| T04-R15 | Per-turn SDK runtime/workspace directories accumulated because production never removed them. | Runtime cleanup now deletes the turn directory after disposing session/client resources, attempts deletion after earlier cleanup failures, and classifies deletion failures as cleanup interruption. Integration tests verify cleanup ordering, removal, and failure aggregation. |
| T04-R16 | A nonterminal event-persistence failure faulted the event pump before terminal persistence, potentially leaving a turn `Running`. | The pump now cancels/aborts the active runtime and returns the persistence failure to terminal handling, which records `Interrupted` with a safe reason code through the bounded atomic outcome path. The actual-runtime SDK contract injects the first event append failure and verifies the durable interrupted status/event; all 10 SDK contracts passed. |
| T04-R17 | Local changed-line coverage omitted tracked staged and unstaged edits when a base ref was configured. | The validator now merges the PR merge-base diff with staged and unstaged tracked source diffs, plus untracked sources. Its gate self-test creates a temporary git repo with both a committed PR edit and a tracked worktree edit and requires both changed lines; the full coverage gate passed at 510/549 changed executable lines. |
| T04-R18 | Event persistence ignored the turn deadline and could wait beyond the turn budget. | The event pump now reads and persists with a token linked to caller, host cancellation, and deadline, but separate from tool-budget SDK stop. Expected cancellation is classified from its actual signal rather than as a persistence failure. Real-runtime regressions cover both a stalled provider and a blocked event append, each producing one durable deadline interruption; both pass in the 12/12 SDK contract suite. |
| T04-R19 | Combining base, index, and worktree diffs could use inconsistent line-number coordinates after insertions. | Changed-line discovery now finds the merge base and runs one diff from that base directly to the current worktree, then adds untracked sources. The self-test inserts lines before tracked PR edits and asserts their final worktree line coordinates; the coverage gate passed at 600/630 changed executable lines. |
| T04-R20 | A timed-out SDK force-stop could race with disposal and a later duplicate cleanup attempt. | Client force-stop and disposal now share one in-flight shutdown task. Disposal starts only after force-stop finishes, retries join the existing task, and runtime directory removal is withheld unless shutdown completed. Bounded regressions verify one stop, no premature disposal, eventual disposal, session-abort timeout fallback, and cleanup-failure disposal; critical coverage passes. |
| T04-R21 | Deadline enforcement lacked a runtime-level regression against an actually stalled provider. | The controlled fake provider can stall inference; the SDK contract verifies the deadline cancels the call, returns one `engine_deadline_exceeded` interruption, persists it, and removes the per-turn runtime directory within the bounded test deadline. |
| T04-R22 | Separate engine instances could both execute a turn already persisted as `Running`. | `EnsureRunningAsync` now rejects an already-running status and always performs the version-checked transition; concurrent claims from separate state-machine instances are covered by `ConcurrentEngineClaimRejectsTurnAlreadyMarkedRunning`. |
| T04-R23 | The terminal persistence timeout included time spent draining the event pump. | The dedicated timeout now starts immediately before the atomic terminal persistence call, after event draining and outcome classification. |
| T04-R24 | Event persistence could ignore cancellation while synchronously waiting for SQLite's write lock. | The event store opens a connection with a one-second SQLite lock timeout and retries immediate-transaction acquisition with cancellation checks between attempts. `EventAppendHonorsCancellationWhileWaitingForImmediateWriteLock` holds a writer lock and verifies cancellation is observed within two seconds without persisting an event. |
| T04-R25 | Terminal status/event persistence could ignore cancellation while synchronously waiting for SQLite's write lock. | Terminal outcome writes use the same one-second lock timeout and cancellation-aware immediate transaction acquisition. `TerminalOutcomeHonorsCancellationWhileWaitingForImmediateWriteLock` verifies cancellation within two seconds leaves both status and event unchanged. |
| T04-R26 | A failed execution claim was caught as an engine failure and could terminalize another engine's active turn. | Durable claiming now occurs before the event pump and terminal-outcome handling; a rejected claim completes the output channel and propagates without writing events or terminal state. `RejectedDuplicateClaimDoesNotTerminalizeTheActiveTurn` verifies a pre-existing Running turn remains unchanged and does not reach the provider. |
| T04-R27 | Cleanup `TimeoutException` could be classified as request-deadline expiration even when the deadline token had not fired. | The timeout handler now applies only when the request deadline is canceled; otherwise cancellation/cleanup exception classification remains in effect. |
| T04-R28 | Repeated short SQLite busy waits could retry forever without caller cancellation. | Cancellable immediate-transaction retries now have a 10-second overall bound and rethrow the final SQLite busy error if the lock remains held. `ImmediateWriteLockWaitHasOverallBoundWithoutCallerCancellation` verifies a no-token write fails within 12 seconds and persists no event. |
| T04-R29 | The PR description validation evidence and finding range were stale. | After pushing and validating the latest code revision, the PR description is refreshed with that exact revision, current local evidence, the coverage limitation, and the finding range through T04-R29. |

The T04-R1 through T04-R21 findings were raised against earlier PR revisions
and fixed in follow-up commits. Their review threads were resolved after those
commits were pushed.

Follow-up validation after T04-R22–R25:

- `tools/validate.sh build`: passed Release build and format verification with
  zero warnings or errors.
- `tools/validate.sh unit`: 8/8 passed.
- `tools/validate.sh integration`: 73/73 passed, including both held-writer
  cancellation regressions and the simultaneous engine-claim regression.
- `tools/validate.sh sdk-contracts`: pinned runtime harness passed; 12/12
  actual SDK contract tests passed.
- `tools/validate.sh architecture`: 6/6 passed.
- `tools/validate.sh coverage`: passed; changed executable lines 618/650
  (95.1%); Infrastructure 1798/1945 lines (92.4%) and 448/579 branches
  (77.4%).
- `tools/validate.sh docs`: internal Markdown link validation passed.

Follow-up validation for the T04-R26–R28 fixes:

- `tools/validate.sh build`: Release build and formatting passed with zero
  warnings or errors.
- `tools/validate.sh unit`: 8/8 passed.
- Integration suite: 75/75 passed, including bounded lock acquisition,
  cancellation during lock contention, and duplicate-claim safety.
- `tools/validate.sh sdk-contracts`: pinned runtime harness passed; 13/13
  actual SDK contract tests passed.
- `tools/validate.sh architecture`: 6/6 passed.
- `tools/validate.sh docs`: internal Markdown link validation passed.
- The coverage-enabled integration command did not start a test host in this
  local environment and hung in VSTest/collector startup on repeated attempts;
  it was stopped. The full coverage gate therefore remains unverified locally
  for this revision and must be established by the PR's CI coverage check.

## Completion criteria

- SDK types remain confined to `Infrastructure/AgentEngine/Copilot`; Domain
  and Application remain independent of the SDK.
- Provider and tool capability are explicit and host-controlled; no ambient
  built-ins, tools, session state, user login, or workspace instructions leak
  into the request.
- Streaming events have durable monotonic sequences; every successfully
  persisted turn has exactly one terminal status/event. Timeout and process
  crash are interruption, cancellation is cancellation, and provider/tool
  failures are not reported as successful turns.
- Critical terminal/cancellation state-machine coverage meets 95% lines / 90%
  branches, and standard runtime/changed-line coverage gates pass.
- The adapter README, architecture/test scenario mapping, and this brief state
  operating guarantees and limitations accurately.
