# PersonalAgent.Infrastructure

Owns adapters for SQLite and external systems. It depends on Application and
Domain; the pinned GitHub Copilot SDK package is confined to this assembly and
SDK types remain under `AgentEngine/Copilot/`.

The Copilot engine receives an explicit host-selected provider, context and tool
catalog for each turn. Its disposable runtime uses an isolated data/work
directory, empty mode, no ambient user login and a replaced, allowlisted child
environment. The host enforces tool-call and elapsed-time limits, streams
sequenced events, and reports one terminal outcome. Copilot session state is
ephemeral; durable conversations and replay history belong to application
storage. Per T01, child-process death may not promptly complete an SDK wait, so
the adapter must keep a deadline and force-stop an unresponsive runtime.
Native OS network containment is not claimed; see ADR 0001 and
[the T01 validation report](../../docs/sdk-validation.md).

Tests: `dotnet test tests/PersonalAgent.UnitTests` and
`tools/validate.sh sdk-contracts` (which runs both T01 SDK probes and the
production adapter's actual-runtime contracts).
