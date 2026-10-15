# M2-02: Home Assistant adapter, fixtures and approved tools

## Objective and acceptance

Issue [#8](https://github.com/hammar/Jarvis/issues/8), specification sections
5, 7, 8, 13, 14, 18-21. Dependencies: completed M1 and merged M2-01 (#5).
Implement narrow REST reads/light/thermostat operations, four typed home tools,
an explicit approved catalog, and deterministic credential-free fixtures.

Acceptance includes timestamped readings; stale/unavailable/unauthorized/
unknown-entity/transport distinctions; ambiguous porch clarification; exact
permissions and bounds; thermostat approval; one transient safe-read retry
within the original deadline; and one submitted write followed only by bounded
observation, never blind replay. No secrets in output or diagnostics.

## Scope and ownership

Application HA contracts and `Tools/Home/`; Infrastructure `HomeAssistant/`;
Web composition; AppHost managed HA discovery; TestSupport HA fake and
SimulatorEndpoints; corresponding unit/HTTP/SQLite/Aspire tests and critical
coverage ownership. No SQLite migration, dispatcher redesign, arbitrary
services, wildcard entities, security devices, WebSockets, scheduling, cloud,
chat/approval UI or live-device smoke implementation. M2-04 owns chat/browser
composition; this task's E2E is HTTP adapter/dispatcher integration.

## Owner-approved decisions

Exact whole-request light commands can establish explicit intent; other
phrasing requires exact approval. Live automatic writes remain gated off until
the specification's model evaluation is satisfied. Thermostat is disabled by
default and always approval-required when enabled. HA is disabled without
explicit live configuration.

Defaults: shared five-second read deadline for at most two attempts,
five-second verification window, 250-ms observation interval, per-entity stale
age (five minutes for simulated sensors). Brightness uses HA native 0-255
units; temperature uses the configured thermostat unit. HA source
`last_updated` is an age heuristic, not proof of physical measurement.

## Tests, documentation and completion

Use strict argument/catalog/intent unit tests, real controlled HTTP and
isolated SQLite integration, and managed Aspire endpoints with production
adapter/registrations. Assert request counts, cancellation/deadlines, no
security/wildcard requests, stale readings, bounded verification, replay and
approval semantics. Captured synthetic secret tests cover output/diagnostics.

Run existing `tools/validate.sh` build, architecture, unit, integration,
sdk-contracts, coverage, aspire-e2e, browser-e2e and docs gates. Preserve all
thresholds; catalog/intent authorization is critical at 95% lines/90% branches.
Update affected project READMEs and architecture/development/operations/testing
guidance. No required gate is waived; live hardware/model evidence is optional
and unrun unless explicitly consented.

Independent exact-commit review and a cumulative finding ledger are required.
Completion/handoff records commands actually run, revision, results,
unexercised behavior and residual risks. The cohesive slice exceeds the
approximately 400-line review target because failure-path evidence, fixtures,
composition and the adapter/tool boundary cannot safely be omitted.

## Finding ledger and execution evidence

Pending implementation and independent review. No live devices or credentials
have been accessed.
