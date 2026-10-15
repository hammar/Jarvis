# PersonalAgent.TestSupport

Test-only isolated data directories and deterministic Home Assistant state.
The HA fake depends on Application contracts, owns instance-local synthetic
lights/thermostat/sensors and accepts an injected UTC clock. It is not a
production device adapter or evidence of live hardware behavior.

`FakeHomeAssistantClient.SetState` and `ApplyWrites` are deliberate fixture
controls, never model tools. The standalone non-production simulator executable
uses this state over real HTTP. Production projects must not reference
TestSupport. Validate through the unit/integration/Aspire gates in
`tools/validate.sh`.
