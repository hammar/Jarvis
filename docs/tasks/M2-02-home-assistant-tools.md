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

Implementation revision: `3afa27bbecb60f1598116b9d9b79e7573db19ae4`, based on
`f332e9e`; fetched main before publishing with no missing upstream commits.
Draft PR [#46](https://github.com/hammar/Jarvis/pull/46).
No live devices or household/cloud credentials have been accessed.

Native macOS / pinned .NET `10.0.401` evidence:

| Command | Executed result |
| --- | --- |
| `dotnet restore Jarvis.sln --force-evaluate` | Passed after project-reference changes; lock graphs updated, no package version changes |
| `dotnet format Jarvis.sln --no-restore` | Passed |
| `tools/validate.sh build` | Passed Release, formatting, analyzers and warnings-as-errors |
| `tools/validate.sh architecture` | Passed, including explicit TestSupport/simulator edges and existing negative fixtures |
| `tools/validate.sh unit` | 308 discovered/passed |
| `tools/validate.sh integration` | 335 discovered/passed, including 41 HA controlled-HTTP cases and actual runtime regressions |
| `tools/validate.sh coverage` | Passed: changed executable lines 98.1% (363/370); catalog/intent critical thresholds retained |
| `tools/validate.sh sdk-contracts` | First run passed 55 actual runtime contracts; final post-review rerun pending |
| `tools/validate.sh aspire-e2e` | First run passed 8 scenarios including four-tool managed HA HTTP/dispatcher approval integration; final rerun pending |
| `tools/validate.sh docs` | Passed internal Markdown links |
| `tools/validate.sh browser-e2e` | First long-running invocation stopped without a result; not claimed passing |
| Browser diagnostic with `--blame-hang --blame-hang-timeout 3m` | In progress; final standard gate remains required |

Linux CI and review follow-up are in progress. No completion or readiness is
claimed while mandatory gates and review remain pending.

| Reference | Severity | Finding | Disposition and evidence |
| --- | --- | --- | --- |
| M2-02-R1 | High | Approval configuration omitted installation endpoint and named credential reference, permitting pending consent to survive a target-installation change. | Fixed in review follow-up: normalized connection identity plus complete catalog hash; `ChangingInstallationOrSecretReferenceInvalidatesApprovedActionWithoutSending` passes both real HTTP/SQLite restart cases with zero writes. |
| M2-02-R2 | Medium | Known credential/cancellation/deadline failures before submission threw through the dispatcher and became false durable Unknown writes. | Fixed in review follow-up: explicit not-submitted rejection before `SendAsync`; `KnownPreSubmissionFailuresAreDefinitiveAndDoNotLockOutTheRemainingWrite` passes missing/invalid credentials, cancellation and timeout with Failed, zero POSTs, exact replay and a permitted corrected second attempt. Possible submission remains Unknown. |

Independent review examined `3afa27b` against the task/spec and full diff,
source-traced both findings, and ran existing Release home-tool unit and
architecture binaries (25 and 6 passing respectively, not fresh rebuild
provenance). It did not independently rerun HTTP, SDK, Aspire, browser,
coverage or Linux gates. The full ledger is retained for follow-up review.

Review-fix targeted command:
`dotnet test tests/PersonalAgent.IntegrationTests -c Release --no-restore --filter FullyQualifiedName~HomeAssistantTests`
passed all 54 controlled HTTP/SQLite cases. Bounded browser diagnostic completed
29/29 passing; it measured no three-minute inactivity hang. The original stopped
invocation remains unverified, and the standard browser gate including its
deliberately failing enforcement probe is still required on the final revision.

Unexercised: live HA/device delivery and physical causality, live model
evaluation for automatic household writes, and M2-04 Web chat/approval UI flows.
No schema change; existing state and named secret references are preserved.
Source-age staleness and configured temperature units have the documented
limits in development/operations guidance.
