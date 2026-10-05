# Testing and quality gates

## Test layers

| Layer | Project/check | Scope |
| --- | --- | --- |
| Unit | `PersonalAgent.UnitTests` / `unit-tests` | Domain and Application contracts without network or real clocks. |
| Architecture | `PersonalAgent.ArchitectureTests` / `architecture` | Direct project references, compiled dependency boundaries, Copilot confinement, and Web composition exception. |
| Integration | `PersonalAgent.IntegrationTests` / `integration-tests` | Real isolated SQLite and in-process Web HTTP composition. |
| SDK contract | `tools/sdk-validation` / `sdk-contracts` | Actual pinned Copilot runtime contracts, credential-free on Linux and macOS. |
| Aspire E2E | `PersonalAgent.E2ETests` / `aspire-e2e` | Out-of-process AppHost Web and managed deterministic endpoints; temporary data is isolated. |
| Browser E2E | `PersonalAgent.E2ETests` / `browser-e2e` | Playwright drives the Razor page through the Aspire resource endpoint. |

The pinned stack is xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1, Visual Studio
VSTest adapter 4.0.0, and Coverlet collector 10.1.0. Aspire AppHost/Testing
are 13.6.0, Playwright for .NET is 1.63.0, and the SDK package/runtime remain
GitHub.Copilot.SDK 1.0.16 / Copilot CLI 1.0.90. Exact dependency graphs are in
committed NuGet lock files.

## Coverage semantics

`tools/PersonalAgent.Validation` consumes VSTest TRX discovery results and
Coverlet Cobertura output for line coverage and OpenCover output for individual
branch outcomes. Missing reports, missing expected assemblies,
zero discovered tests, or malformed reports fail validation. Unit and
integration reports are merged by normalized source path and line identity
rather than averaging project percentages. Branch outcomes use the stable
method name and branch-point ordinal from OpenCover, so complementary test
runs can combine coverage without mistaking aggregate branch counts for the
same outcome.

Enforced targets follow AGENTS.md: Domain and Application unit-only at 90%
lines / 85% branches; combined unit+integration first-party runtime at 85% /
75%; each runtime assembly at 80% / 70%; approval persistence invariants in
`SqliteApprovalStore.cs` and action-journal idempotency in
`SqliteActionJournalStore.cs` at 95% / 90%; changed executable lines at 90%.
The coverage validator requires each critical source file to exist at its
exact owned path, so renaming it cannot silently remove the gate. A module with no coverable lines
does not count as evidence of passing its threshold; runtime assemblies with
no coverable lines fail, and branch coverage is N/A only when a module has no
coverable branches. Thresholds apply as executable production code is
introduced; compiler-generated record boilerplate is excluded by attribute,
while handwritten code remains covered. E2E execution is not counted as
in-process coverage. No broad Infrastructure exclusion is allowed: the
currently empty Infrastructure assembly activates its per-assembly 80%/70%
requirement as soon as it contains handwritten C#.

The `gate-self-test` operation runs low-line, low-branch, low-critical-module,
complementary branch-outcome, empty-coverage, missing-report, and
zero-discovery fixtures through the same validator used by CI. Architecture tests include forbidden project-reference,
reflected type-dependency, and Web non-composition source fixtures. The browser check runs the expected
Playwright smoke and confirms a deliberately incorrect browser assertion is
reported as a failure.

## Scenario mapping

| Acceptance scenario | Test |
| --- | --- |
| Strong application IDs are independent | `IdentifierContractTests` |
| Real SQLite file persists across connections and stays test-owned | `SqliteAndWebSmokeTests.SqlitePersistsDataInAnIsolatedTestDatabase` |
| Razor host works without external services | `SqliteAndWebSmokeTests.RazorHostServesTheConfiguredProfileWithoutExternalServices` |
| Direct test-profile startup requires isolated data | `SqliteAndWebSmokeTests.TestProfileRequiresAnExplicitDataDirectory` |
| Aspire launches Web and discovers deterministic fakes | `AspireSimulatorTests.SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints` |
| Local and Hybrid use explicit external endpoint configuration | `ExternalEndpointProfileTests.LocalAndHybridProfilesLaunchWithExplicitExternalEndpointReferences` |
| Browser reaches actual AppHost Web resource | `BrowserSmokeTests.PlaywrightLoadsTheAspireSimulatorPage` |
| SDK compatibility uses pinned actual runtime | T01 `--contracts` suite, `sdk-contracts-platform` on Linux/macOS |
| Prohibited dependency edges are rejected | `ArchitectureBoundaryTests` negative fixtures |
| Missing discovery/report/low coverage fails | `PersonalAgent.Validation gate-self-test` |
| Browser test failure is visible | `BrowserSmokeTests.DeliberatelyIncorrectBrowserAssertionFails` probe |

The full MVP acceptance matrix is added as T03–T12 features land; a passing
scaffold is not a claim that those unimplemented scenarios are covered.
