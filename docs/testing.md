# Testing and quality gates

## Test layers

| Layer | Project/check | Scope |
| --- | --- | --- |
| Unit | `PersonalAgent.UnitTests` / `unit-tests` | Domain and Application contracts without network or real clocks. |
| Architecture | `PersonalAgent.ArchitectureTests` / `architecture` | Direct project references, compiled dependency boundaries, Copilot confinement, and Web composition exception. |
| Integration | `PersonalAgent.IntegrationTests` / `integration-tests` | Real isolated SQLite and in-process Web HTTP composition. |
| SDK contract | `tools/sdk-validation` and `PersonalAgent.SdkContractTests` / `sdk-contracts` | Actual pinned Copilot runtime checks against deterministic loopback endpoints, credential-free on Linux and macOS. |
| Aspire E2E | `PersonalAgent.E2ETests` / `aspire-e2e` | Out-of-process AppHost Web and managed deterministic endpoints; temporary data is isolated. |
| Browser E2E | `PersonalAgent.E2ETests` / `browser-e2e` | Playwright drives the Razor page through the Aspire resource endpoint. |

The pinned stack is xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1, Visual Studio
VSTest adapter 4.0.0, and Coverlet collector 10.1.0. Aspire AppHost/Testing
are 13.6.0, Playwright for .NET is 1.63.0, and the SDK package/runtime remain
GitHub.Copilot.SDK 1.0.16 / Copilot CLI 1.0.90. Exact dependency graphs are in
committed NuGet lock files.

## Coverage semantics

`tools/PersonalAgent.Validation` consumes VSTest TRX discovery results and
Coverlet Cobertura output. Missing reports, missing expected assemblies,
zero discovered tests, or malformed reports fail validation. Unit and
integration reports are merged by normalized source path and line/condition
identity rather than averaging project percentages.

Enforced targets follow AGENTS.md: Domain and Application unit-only at 90%
lines / 85% branches; combined unit+integration first-party runtime at 85% /
75%; each runtime assembly at 80% / 70%; critical modules at 95% / 90% when
implemented; changed executable lines at 90%. A module with no coverable lines
does not count as evidence of passing its threshold; runtime assemblies with
no coverable lines fail, and branch coverage is N/A only when a module has no
coverable branches. Thresholds apply as executable production code is
introduced; compiler-generated record boilerplate is excluded by attribute,
while handwritten code remains covered. E2E execution is not counted as
in-process coverage. No broad Infrastructure exclusion is allowed. The
Copilot adapter's actual-runtime contracts also run as Integration tests with
Coverlet so its handwritten runtime code is counted toward the Infrastructure
80%/70% threshold.

The `gate-self-test` operation runs low-line, low-branch, empty-coverage,
missing-report, and zero-discovery fixtures through the same validator used by CI. Architecture
tests include forbidden project-reference, reflected type-dependency, and
Web non-composition source fixtures. The browser check runs the expected
Playwright smoke and confirms a deliberately incorrect browser assertion is
reported as a failure.

## Scenario mapping

| Acceptance scenario | Test |
| --- | --- |
| Strong application IDs are independent | `IdentifierContractTests` |
| Real SQLite file persists across connections and stays test-owned | `SqliteAndWebSmokeTests.SqlitePersistsDataInAnIsolatedTestDatabase` |
| Razor host works without external services | `SqliteAndWebSmokeTests.RazorHostServesTheConfiguredProfileWithoutExternalServices` |
| Aspire launches Web and discovers deterministic fakes | `AspireSimulatorTests.SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints` |
| Local and Hybrid use explicit external endpoint configuration | `ExternalEndpointProfileTests.LocalAndHybridProfilesLaunchWithExplicitExternalEndpointReferences` |
| Browser reaches actual AppHost Web resource | `BrowserSmokeTests.PlaywrightLoadsTheAspireSimulatorPage` |
| SDK compatibility uses pinned actual runtime | T01 `--contracts` suite, `sdk-contracts-platform` on Linux/macOS |
| Prohibited dependency edges are rejected | `ArchitectureBoundaryTests` negative fixtures |
| Missing discovery/report/low coverage fails | `PersonalAgent.Validation gate-self-test` |
| Browser test failure is visible | `BrowserSmokeTests.DeliberatelyIncorrectBrowserAssertionFails` probe |

| T04 explicit route, transcript isolation, tool dispatch and streaming | `CopilotAgentEngineContractTests.AdapterStreamsExplicitRoutesAndDispatchesOnlyTheRegisteredTool` (actual runtime; run by `integration-tests` and `sdk-contracts`) |
| T04 cancellation, runtime crash/restart, exactly-one terminal event, and host tool budget | `CopilotAgentEngineContractTests` lifecycle and budget contracts |
| T04 sequenced replay after a simulated subscriber disconnect | `CopilotTurnStateTests.ReplayingAfterDisconnectReturnsOrderedEventsWithoutRepeatingTerminalOutcome` |

The adapter contracts run the real pinned runtime and replace only the model
endpoint with a deterministic loopback fixture. T01's `--contracts` suite
continues to verify hostile ambient configuration, process lifecycle and SDK
limits; neither suite uses GitHub login, cloud credentials, Ollama or household
services. T04 sequence replay is in-memory only: durable event persistence and
HTTP/SSE reconnection remain application responsibilities for T10.

The full MVP acceptance matrix continues as T03–T12 features land; passing the
implemented scenarios is not a claim that unimplemented scenarios are covered.
