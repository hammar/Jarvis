# M2 follow-up: Local Ollama with simulated Home Assistant

## Objective and acceptance

Owner-requested development configuration for the cumulative M2 demo
(specification sections 16 and 18): native Ollama inference with a managed,
simulated Home Assistant, without household credentials or physical effects.
This is an explicit Local-profile opt-in, not a replacement for deterministic
Simulator/E2E evidence or the M3 cloud/reminder milestone.

## Scope and ownership

- AppHost composition: `JARVIS_LOCAL_SIMULATED_HOME=true` is Local-only;
  launch the HA simulator with controls disabled and a discovered endpoint.
- Register only the simulated lamp and temperature sensor through existing
  production catalog validation. Force approval for lamp writes; no thermostat.
- Use separate persistent demo data by default, with
  `JARVIS_LOCAL_DEMO_DATA_DIR` for explicit isolated test/demo paths.
  Reject `JARVIS_DATA_DIR` rather than silently reuse live data.
- Do not launch a simulated model or forward live HA settings. Cloud remains
  disabled under Local. External Ollama remains owner-managed.
- Owned files: AppHost entry point/README, E2E profile tests, root README,
  development/testing guidance and this brief.

No SDK, application contract, schema, UI policy, migration, dependency or CI
changes. Depends on completed M1/M2 production composition and simulator routes.
Assumes an explicitly selected, installed tool-capable Ollama model with at
least 10,240 tokens of context. Live model quality is not guaranteed.

## Automated acceptance

- Distributed Local demo startup discovers managed HA, not a model fixture;
  authenticated household API exposes only the two synthetic entities.
- A controlled external model exercises actual runtime inference/tool dispatch,
  reads the managed sensor, and requires approval before any simulated write.
- Demo configuration ignores hostile ambient live HA endpoint/catalog/options,
  rejects incompatible profiles and live data-path reuse.
- Resource disposal stops managed processes and preserves demo data.
- Existing Local/Hybrid/Simulator/E2E startup remains covered.

Run the shared build, architecture, unit, integration, SDK contracts, coverage,
Aspire E2E, browser E2E and docs gates. Native macOS is the local execution
platform; Linux evidence comes from CI. Record exact results and independent
review dispositions below before reporting readiness.

## Evidence and review ledger

Implementation/review revision: `bc585adc699a51be571d880a1c06838af72be271`,
based on current `origin/main` at `633b246`. Native macOS, pinned .NET SDK
10.0.401. No schema, SDK, dependency or application contract changes.

| Shared command | Executed result |
| --- | --- |
| `tools/validate.sh build` | Passed, zero warnings/errors, formatting/analyzers included. |
| `tools/validate.sh architecture` | Passed 6/6. |
| `tools/validate.sh unit` | Passed 361/361 with coverage/discovery. |
| `tools/validate.sh integration` | Passed 482/482 with coverage/discovery. |
| `tools/validate.sh sdk-contracts` | Actual-runtime harness passed; 57/57 contract tests passed. |
| `tools/validate.sh coverage` | Passed all configured thresholds and gate self-tests. Changed coverable lines N/A: AppHost is outside instrumented runtime coverage; new orchestration has distributed acceptance coverage, not claimed line coverage. |
| `tools/validate.sh aspire-e2e` | Passed 20/20, including five new Local demo cases. |
| `tools/validate.sh browser-e2e` | Passed 36/36; the separate deliberate failure probe failed 1/1 as expected and its rejection was verified. |
| `tools/validate.sh docs` | Passed all internal links. |

Five new cases were also executed in a focused run before the full gates.
The positive distributed case covers actual Copilot runtime inference/tool
dispatch through an external controlled model, the managed sensor, lamp
approval-before-write, restricted catalog, disabled controls, startup/shutdown,
and preservation of the isolated account/database. Existing profile tests
continue to cover ordinary Local/Hybrid and Simulator/E2E.
Logs/reports are local ignored artifacts; do not commit private application
data or bootstrap tokens.

Independent read-only code-review agent examined the exact implementation
revision against the brief, specification, composition, profile restrictions,
HA wiring, data isolation, approvals, tests and documentation. No significant
findings were reported; the cumulative follow-up finding ledger is empty.
This evidence does not replace owner acceptance or authorize merging.

Limitations: Linux checks remain CI evidence, not locally executed. These
required tests use a controlled external model, not live Ollama. Earlier ad hoc
demo setup verified a direct native model response and host availability, not
an end-to-end live tool-quality evaluation of this committed configuration.
No live HA, cloud, physical effects or live-model quality claim is made.
Native runtime dependency trust remains as accepted in ADR 0001. This follow-up
does not waive any required CI gate. The interactive demo remains stopped.
