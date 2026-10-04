# T02: Aspire scaffold, solution contracts, and quality gates

## Objective and acceptance

Deliver T02 / milestone M1 before feature implementation: establish the
solution and initial contracts, Aspire orchestration, architecture boundaries,
test tooling, and non-placeholder CI gates.

**Specification:** §§3, 5, 14, 18, 19, and 20. The T02 issue defines the exact
project inventory and all nine required CI check names. M0 SDK compatibility
and the current version authority are documented in `docs/sdk-validation.md`
and the T01 GA refresh brief.

Acceptance scenarios include: no real-model or device credentials in
Simulator/E2E; local/cloud endpoints are explicit configuration; application
IDs and events are independent from Copilot session state; disallowed project
and compiled dependencies fail architecture validation; missing test
discovery/reports and below-threshold coverage fail; Aspire launches controlled
endpoints; browser assertions exercise the launched Web process.

## Scope

- Add the Domain, Application, Infrastructure, Web, AppHost, ServiceDefaults,
  and six test projects in spec §3.
- Freeze documented, strongly typed IDs, application interfaces, event types,
  provider/context/approval contracts, and turn status values from spec §5.
- Add C# Aspire Simulator, Local, Hybrid, and E2E profiles. Simulator/E2E
  launch two controlled fixture resources. Local Ollama, optional Home
  Assistant, and Hybrid cloud use explicit HTTP(S) endpoint settings and
  protected secret references; no actual credential is stored or printed.
- Add project graph and compiled assembly/type boundary checks, with
  test-only negative fixtures for forbidden edges and the Web composition
  root exception.
- Pin and exercise xUnit 2 + VSTest + Coverlet, Aspire 13.6, and Playwright for
  .NET. Use one shared validation tool with a native POSIX shell entry point.
  PowerShell wrappers are optional; macOS does not require PowerShell.
- Replace placeholders for the nine stable checks: `build-and-analyzers`,
  `architecture`, `unit-tests`, `integration-tests`, `sdk-contracts`,
  `coverage`, `aspire-e2e`, `browser-e2e`, and `docs`.
- Add architecture/development/testing/operations guidance, this brief, ADR,
  root and per-production-project READMEs, and cross-platform scripts.

## Exclusions

No SQLite schema/repositories/migrations, production Copilot adapter,
routing/context policy, tool dispatcher, Home Assistant adapter, memory/job
business logic, real household or cloud integrations, production UI, login,
or deployment supervisor. The only UI is a profile-identifying Razor page
needed to verify process and browser wiring.

## Owned files

`Jarvis.sln`, shared build/package properties, `src/`, `tests/`,
`tools/PersonalAgent.Validation/`, `tools/PersonalAgent.SimulatorEndpoints/`,
`tools/validate.*`, `.github/workflows/ci.yml`, `.github/CODEOWNERS`,
`README.md`, `docs/architecture.md`, `docs/development.md`,
`docs/operations.md`, `docs/testing.md`, `docs/adr/0002-architecture-test-strategy.md`,
and this task brief.

## Dependencies and assumptions

- T01 (#2) is a qualified go under accepted ADR 0001; it does not establish
  native OS network containment or replace host policy.
- `global.json` is authoritative and pins .NET SDK `10.0.401` / runtime
  `10.0.12`. The Copilot package remains exactly `1.0.16`, with its actual
  runtime contract check in `tools/sdk-validation/`.
- NuGet pins use central package management and lock files. Runner/collector
  compatibility is VSTest-based; Microsoft.Testing.Platform switches are not
  mixed into those commands.
- AppHost starts only its managed Web and fake fixture resources. It does not
  own external Ollama, Home Assistant, cloud endpoints, or user data.

## Tests and documentation

Required checks are invoked through `tools/validate.sh` and include Release
build/analyzers/format, filtered unit/integration/architecture tests, locked
real-runtime SDK contracts on Linux and macOS, coverage/report/discovery
validation, out-of-process Aspire E2E, Playwright E2E, and Markdown/link
validation. The `gate-self-test` command must reject synthetic low coverage,
missing expected reports, and empty test discovery. The browser gate must
prove a deliberately failing Playwright assertion is observed as a test
failure. None of these harnesses may report empty scaffolding as meeting
runtime coverage thresholds.

`docs/testing.md` records the runner/collector versions, reproducible commands,
coverage scope and current no-business-logic exclusions. Each production
project README states ownership, dependencies, entry point and test command.

## Completion criteria and status

- All specified projects, contracts, four profiles, negative architecture
  fixtures, scripts, nine real checks, docs and test-of-test demonstrations
  exist.
- Locked restore, Release build/analyzers/format, all applicable test layers,
  coverage gate self-tests, Aspire and browser smoke flows, and Markdown/link
  checks pass.
- No Domain/Application dependency on ASP.NET Core, Aspire, SQLite/EF,
  Copilot, or provider SDKs; production Copilot assembly references remain
  Infrastructure-only; Web Infrastructure type use is limited to composition.

**Status: implementation complete; hosted CI confirmation pending.** Validation
evidence from the native macOS worktree:

- `tools/validate.sh build` — locked restore, `dotnet format` verification,
  and Release build passed with zero warnings/errors.
- `tools/validate.sh unit` — 7 tests passed; `tools/validate.sh integration`
  — 6 tests passed, including SQLite, Web, health, and profile fallback
  behavior.
- `tools/validate.sh architecture` — all 6 project/type boundary tests passed.
- `tools/validate.sh sdk-contracts` — the pinned T01 Copilot CLI runtime
  contracts and the T02 SDK package contract test passed on macOS.
- `tools/validate.sh coverage` — gate self-tests and thresholds passed:
  Domain/Application unit coverage 100% lines; Domain, Application,
  ServiceDefaults and Web combined coverage 100% lines; all reported
  coverable branches were 100%; changed executable lines were 100%.
- `tools/validate.sh aspire-e2e` — 2 out-of-process tests passed, exercising
  Simulator plus Local/Hybrid with explicit test-only endpoint and secret
  references. The same gate passed in a clean Linux x64 SDK container with
  Docker CLI/daemon access after switching AppHost project endpoints to
  dynamic allocation, avoiding port collisions between its process resources.
- `tools/validate.sh browser-e2e` — Playwright smoke passed; the deliberately
  failing assertion produced a failed TRX and the failure verifier accepted
  it as expected.
- `tools/validate.sh gate-self-test` and `tools/validate.sh docs` — synthetic
  enforcement fixtures and internal Markdown links passed.

The Linux x64 Docker validation also passed the Release build, unit,
integration, architecture, SDK contract, coverage, and Aspire E2E gates;
loopback containment passed separately. GitHub-hosted CI and the remote-link
action were not run from this session. No production adapter, persistence
schema, or household/cloud integration is claimed by this scaffold.
