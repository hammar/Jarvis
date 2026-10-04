# PersonalAgent.Infrastructure

Reserved for SQLite and external-system adapter implementations. It depends
on Application and Domain; the pinned GitHub Copilot SDK package is confined
to this assembly and future Copilot adapter files belong under
`AgentEngine/Copilot/`. T02 defines the dependency boundary but does not
implement an adapter.

Tests: `dotnet test tests/PersonalAgent.IntegrationTests` and the actual
runtime contracts in `tools/sdk-validation/`.
