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

Implementation and validation in progress. No acceptance or live inference
claim is made by this record yet.
