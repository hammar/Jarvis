# T04: Copilot agent-engine adapter

## Objective and acceptance

Implement the production `IAgentEngine` adapter for T04 / milestone M1. The
adapter runs bounded turns against the provider explicitly selected by the
host, streams typed sequenced events, dispatches only the supplied host tools,
and reports exactly one truthful terminal outcome. This covers spec §§4, 5,
14, 15, and 20.

Acceptance includes real pinned-runtime contracts for streaming, provider and
transcript isolation, tool allowlisting, cancellation, runtime interruption,
and restart; unit coverage for terminal compare-and-swap and event sequencing;
and ordered reconnect replay without duplicate tool execution or terminal
events.

## Scope and exclusions

- Use one disposable Copilot runtime and isolated session state per turn, with
  explicit provider settings, `Empty` mode, disabled ambient login/config
  discovery, and a replaced child environment.
- Enforce the turn deadline and tool-call budget in host code; cancel and
  terminate a runtime that cannot complete cooperatively.
- Adapt only the given `IToolDispatcher` and tool catalog. No tool behavior,
  model routing, approval UI, persistence schema, or live credentials.
- Do not claim native OS network containment; ADR 0001 accepts the native
  trusted-runtime boundary.

## Owned files

- `src/PersonalAgent.Infrastructure/AgentEngine/Copilot/`
- `src/PersonalAgent.Application/Contracts.cs` (event sequence metadata)
- `tests/PersonalAgent.SdkContractTests/`
- `tests/PersonalAgent.UnitTests/`
- `docs/tasks/T04-copilot-agent-engine.md`
- `src/PersonalAgent.Infrastructure/README.md`
- `docs/testing.md`

## Dependencies and assumptions

- Depends on T01 (#2) and T02 (#3). SDK package `1.0.16`, bundled runtime
  `1.0.90`, and .NET SDK `10.0.401` are pinned and documented.
- `IAgentEngine` receives host-approved context and provider choice. Durable
  conversations and replay persistence remain application-owned and are not
  stored in Copilot sessions.
- SDK process death may not promptly complete a pending turn; T01 requires a
  host deadline and explicit runtime stop/restart behavior.

## Tests and documentation

- Run focused unit tests for compare-and-swap terminal transitions and strictly
  increasing per-turn sequence numbers, including replay after a simulated
  subscriber disconnect.
- Run `tools/validate.sh sdk-contracts` against the actual pinned runtime and a
  deterministic loopback model endpoint.
- Update the Infrastructure README and testing guide with isolation guarantees,
  SDK limitations, and actual commands/results.

## Completion criteria

- The adapter emits a single terminal event under normal completion,
  cancellation, provider failure, timeout, and process interruption.
- Each runtime receives only explicit host-supplied instructions, context,
  provider configuration, and registered tools, with no inherited Copilot
  login or user configuration.
- Real-runtime tests demonstrate streaming, isolation, cancellation,
  interruption/restart, and tool-budget enforcement; sequenced replay does not
  re-execute tools or duplicate terminal output.
- Targeted tests and required validation pass, and the reviewed diff contains no
  unrelated changes.
