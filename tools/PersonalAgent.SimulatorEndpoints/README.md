# PersonalAgent.SimulatorEndpoints

Non-production Aspire managed HTTP fixtures. `JARVIS_SIMULATOR_KIND` selects
the controlled OpenAI-shaped model or HA-shaped state/service endpoints.
The HA host uses isolated `TestSupport/HomeAssistantFake` state, never household
credentials or devices. It accepts only the synthetic token
`synthetic-home-token`. No state-update endpoint or generic service is supplied.

`JARVIS_SIMULATOR_CONTROLS_ENABLED=true` enables E2E-only controls:
`POST /fixture/control/configure` takes `ReadFailures`, `ApplyWrites` and
`WriteStatus`; `GET /fixture/control/counts` reports request counts.
The model fixture also accepts a bounded one-shot
`POST /fixture/control/next-tool-call` with a registered model-facing tool name
and JSON-object arguments, allowing E2E tests to exercise the real Web/SDK/
dispatcher composition. The scripted response is consumed by one completion
request; malformed controls are rejected and the host still enforces its
registered-tool and authorization policies.
AppHost enables this only for E2E. Integration tests link the endpoint helper
for real loopback HTTP tests with controlled callbacks; these callbacks are
not remotely available in live profiles or model arguments.

Entry point: `Program.cs`, HA routes: `HomeAssistantSimulator.cs`.
Validate with `tools/validate.sh integration` and `tools/validate.sh aspire-e2e`.
