# PersonalAgent.SimulatorEndpoints

Non-production Aspire managed HTTP fixtures. `JARVIS_SIMULATOR_KIND` selects
the controlled OpenAI-shaped model or HA-shaped state/service endpoints.
The HA host uses isolated `TestSupport/HomeAssistantFake` state, never household
credentials or devices. It accepts only the synthetic token
`synthetic-home-token`. No state-update endpoint or generic service is supplied.

`JARVIS_SIMULATOR_CONTROLS_ENABLED=true` enables E2E-only controls:
`POST /fixture/control/configure` takes `ReadFailures`, `ApplyWrites` and
`WriteStatus`; `GET /fixture/control/counts` reports request counts.
AppHost enables this only for E2E. Integration tests link the endpoint helper
for real loopback HTTP tests with controlled callbacks; these callbacks are
not remotely available in live profiles or model arguments.

Entry point: `Program.cs`, HA routes: `HomeAssistantSimulator.cs`.
Validate with `tools/validate.sh integration` and `tools/validate.sh aspire-e2e`.
