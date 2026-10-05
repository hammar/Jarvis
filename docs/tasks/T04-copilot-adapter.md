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
- `tools/validate.sh integration`: 60/60 discovered tests passed.
- `tools/validate.sh sdk-contracts`: pinned SDK/runtime validation harness
  passed; 9/9 adapter SDK contract tests passed.
- `tools/validate.sh coverage`: passed. Changed executable lines 381/421
  (90.9%); Infrastructure 1638/1801 lines (90.9%) and 416/544 branches
  (76.5%). The coverage gate also enforced the 95% line / 90% branch threshold
  for the owned terminal/cancellation state machine.
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

This review was against the uncommitted worktree. The eventual PR must retain
these references and dispositions and receive its required independent review
against the exact PR commit.

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
