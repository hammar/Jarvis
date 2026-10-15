# PersonalAgent.AppHost

Owns Aspire development resource composition and the trusted Simulator,
Local, Hybrid, and E2E profiles. It references Web and the controlled simulator
endpoint executable as resources only. It does not own SQLite data, external
Ollama/Home Assistant/cloud lifecycles, or business rules.

Entry point: `AppHost.cs`. Launch Simulator with
`dotnet run --project src/PersonalAgent.AppHost`; validate using
`tools/validate.sh aspire-e2e`.

Simulator/E2E discovers `simulator-home-assistant` via the managed HTTP endpoint;
Web's production REST adapter talks to its controlled HA-shaped routes with a
synthetic test-only token. Only E2E enables fixture controls. Local/Hybrid never
launch that resource and forward explicit HA endpoint/catalog/secret-reference
settings. Neither profile discovers the owner's household entities. Live
automatic lights and thermostat writes default off. Chat/approval UI integration
remains M2-04 scope.
