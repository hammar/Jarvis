# Testing and quality gates

## Test layers

| Layer | Project/check | Scope |
| --- | --- | --- |
| Unit | `PersonalAgent.UnitTests` / `unit-tests` | Domain and Application contracts without network or real clocks. |
| Architecture | `PersonalAgent.ArchitectureTests` / `architecture` | Direct project references, compiled dependency boundaries, Copilot confinement, and Web composition exception. |
| Integration | `PersonalAgent.IntegrationTests` / `integration-tests` | Real isolated SQLite and in-process Web HTTP composition. |
| SDK contract | `tools/sdk-validation` and `PersonalAgent.SdkContractTests` / `sdk-contracts` | Actual pinned Copilot runtime and adapter contracts against controlled endpoints, credential-free on Linux and macOS. |
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
same outcome. Runtime modules reported by Cobertura must also be present in
OpenCover, including Domain/Application for the unit-only gate, so absent
branch reports cannot be mistaken for branchless assemblies. Critical-module
gates additionally fail when their source has no OpenCover branch data.

Enforced targets follow AGENTS.md: Domain and Application unit-only at 90%
lines / 85% branches; combined unit+integration first-party runtime at 85% /
75%; each runtime assembly at 80% / 70%; approval persistence invariants in
`SqliteApprovalStore.cs` and action-journal idempotency in
`SqliteActionJournalStore.cs`, plus the Copilot turn terminal and cancellation
state machine in `AgentEngine/Copilot/CopilotTurnStateMachine.cs`, at 95% /
90%; changed executable lines at 90%.
The coverage validator requires each critical source file to exist at its
exact owned path, so renaming it cannot silently remove the gate. A module with no coverable lines
does not count as evidence of passing its threshold; runtime assemblies with
no coverable lines fail, and branch coverage is N/A only when a module has no
coverable branches. Thresholds apply as executable production code is
introduced; compiler-generated record boilerplate is excluded by attribute,
while handwritten code remains covered. E2E execution is not counted as
in-process coverage. The adapter runtime contracts are linked into the
integration test project and run with the integration collector so exercised
adapter code contributes to the combined runtime and changed-line gates. No
broad Infrastructure exclusion is allowed.

The `gate-self-test` operation runs low-line, low-branch, low-critical-module,
complementary branch-outcome, empty/missing OpenCover-module, empty-coverage,
missing-report, and zero-discovery fixtures through the same validator used by CI. Architecture tests include forbidden project-reference,
reflected type-dependency, and Web non-composition source fixtures. The browser check runs the expected
Playwright smoke and confirms a deliberately incorrect browser assertion is
reported as a failure.

## Acceptance evidence and independent review

For each change, report the exact revision and validation commands/results,
the platforms and runtime behaviors exercised, and what remains unexercised.
Mocks, in-process tests, actual SDK contracts, out-of-process E2E and opt-in
live tests establish different evidence; do not present one as another.
Passing automation demonstrates only the conditions it measured, not a
guarantee of correctness or safety.

Each PR also receives independent agentic review against its specification,
acceptance criteria, implementation, tests, documentation and meaningful
risks. Keep a cumulative finding ledger across review rounds, with an explicit
disposition and evidence for every finding regardless of severity. Carry
summary-only “Previously missed” findings forward even if a later review
omits them; omission is not resolution. The owner accepts the requirements,
validation evidence and limits, review dispositions and disclosed residual
risks, and retains control of requirements, material scope/tradeoffs and risk
acceptance. This is not a requirement for the owner to manually inspect or
certify generated code. None of this replaces CI, coverage, architecture,
security/privacy or conversation-resolution gates.

## Unavoidable interactive or live E2E evidence

Coding and independent review agents must automate agent-observable acceptance
first, using existing API, browser, process and controlled-endpoint tests.
Human participation is the exception, not a substitute for automation.
Review agents must flag missing required evidence and request this procedure
rather than asking the owner to certify code or inventing manual tests.

For each genuinely unavoidable gap, identify the accepted scenario/criterion,
the evidence missing, why available automation or agent access cannot obtain
it, and its impact on acceptance. Classify it as required acceptance evidence
or an optional opt-in live evaluation. Live credentials or real devices do not
by themselves make a test mandatory. Do not silently change that classification
or weaken gates: an unavailable required test remains unverified and blocks
completion under its existing gate. Record optional unrun tests as limitations;
owner acceptance of residual risk is not a waiver of mandatory checks.

Provide a scenario-specific procedure before asking the owner to participate:
the tested revision/profile, isolated data and backup prerequisites as needed,
safe commands or UI steps, expected measurable outcomes, stop conditions,
and cleanup/restoration of test-owned state. Prepare and execute everything
the agent can safely run, including diagnostics, without touching real
household state or credentials without authorization. Obtain explicit consent
before credential use, cloud disclosure or physical writes, stating scope,
data disclosure, cost or side effects as applicable. Have the owner configure
secrets through protected local mechanisms; never solicit secret values in
chat or logs. Do not retry an ambiguous physical write blindly.

Collect and interpret privacy-redacted logs, traces and measurable outcomes
yourself. Use `ask_user` only for consent or the minimum actions/observations
unavailable to the agent, such as an actual physical effect that telemetry
cannot verify; do not ask the owner to diagnose logs or confirm agent-observable
facts. Redact secrets, raw prompts and private memory before sharing artifacts.
Record failures and uncertainty honestly, not only successful outcomes.

The task/PR handoff records the criterion and classification, exact revision
and environment, steps actually executed versus unrun, measured results,
owner-only observations, cleanup outcome, remaining acceptance gaps and
disclosed residual risks. Coding/review agents must guide required participation
before claiming completion; never mark an unrun test as passed. Preserve the
independent finding ledger and the owner's evidence-based acceptance and merge
authority.

## Scenario mapping

| Acceptance scenario | Test |
| --- | --- |
| Strong application IDs are independent | `IdentifierContractTests` |
| Real SQLite file persists across connections and stays test-owned | `SqliteAndWebSmokeTests.SqlitePersistsDataInAnIsolatedTestDatabase` |
| Restore recovery artifacts cannot be used as backup paths | `SqlitePersistenceTests.BackupAndRestoreRejectRestoreArtifactsWithoutDeletingSourceBackup` |
| Staging WAL/SHM aliases cannot consume a backup source | `SqlitePersistenceTests.BackupAndRestoreRejectRestoreArtifactsWithoutDeletingSourceBackup` |
| Staging recovery markers, locks, and rollback names cannot be backup paths | `SqlitePersistenceTests.BackupAndRestoreRejectRestoreArtifactsWithoutDeletingSourceBackup` |
| Lease completion checks expiry inside the acquired write transaction | `SqlitePersistenceTests.CompletionReadsExpiryClockOnlyAfterAcquiringTheWriteTransaction` |
| Contended lease claims refresh the deadline inside the write transaction | `SqlitePersistenceTests.ContendedClaimRefreshesItsDeadlineAfterTheWriterReleasesTheLock` |
| Live and staged rollback journals cannot be backup or restore paths | `SqlitePersistenceTests.BackupAndRestoreRejectRestoreArtifactsWithoutDeletingSourceBackup` |
| Hot rollback journals cannot overwrite restored data and survive failed-replacement rollback | `SqlitePersistenceTests.RestoreRetiresHotJournalAndFailedReplacementRecoversOriginal` |
| Backup rejects existing destination sidecars without changing their bytes | `SqlitePersistenceTests.BackupRejectsPreexistingDestinationSidecarsWithoutChangingBytes` |
| Unix database-file symlinks preserve WAL data and the link during failed restore | `SqlitePersistenceTests.FailedRestorePreservesCommittedWalDataAndStartupRecoversPreparedReplacement` |
| A frozen version-1 schema upgrades with working constraints and cascading retention | `SqlitePersistenceTests.UpgradedVersionOneSchemaSupportsWritesConstraintsAndRetention` |
| Symlink aliases cannot bypass restore path reservations | `SqlitePersistenceTests.BackupAndRestoreRejectArtifactPathsReachedThroughDirectorySymlinks` |
| Shared data directories are rejected and database files are private | `SqlitePersistenceTests.DatabaseRejectsSharedDataDirectoryAndRestrictsDatabasePermissions` |
| Windows data directories reject ACL access for other identities | `SqlitePersistenceTests.WindowsDatabaseRejectsDirectoriesGrantingAccessToOtherUsers` |
| Turn state/version survive restart and nonterminal recovery reads are bounded | `SqlitePersistenceTests.NonterminalTurnStateAndVersionCanBeRecoveredAfterRestart` |
| Existing default databases are preserved and ambiguous defaults fail closed | `SqlitePersistenceTests.DataDirectoryPreservesLegacyAppHostStateAndRejectsAmbiguousDefaults` |
| Direct Web startup rejects unsupported profiles before persistence access | `SqliteAndWebSmokeTests.DirectHostRejectsUnsupportedProfilesBeforeOpeningStorage` |
| Concurrent startup recovers a prepared restore once | `SqlitePersistenceTests.ConcurrentStartupSerializesPreparedRestoreRecovery` |
| Independent workers race on optimistic writes and startup migrations | `SqlitePersistenceTests.ConcurrentMemoryWritersAllowOnlyOneExpectedVersionUpdate`, `SqlitePersistenceTests.SimultaneousStartupAppliesEachMigrationOnlyOnce` |
| Expired conversations with unresolved turns survive retention | `SqlitePersistenceTests.RetentionUsesDefaultAndOverrideWindowsAndKeepsDurableState` |
| Razor host works without external services | `SqliteAndWebSmokeTests.RazorHostServesTheConfiguredProfileWithoutExternalServices` |
| Direct test-profile startup requires isolated data | `SqliteAndWebSmokeTests.TestProfileRequiresAnExplicitDataDirectory` |
| Aspire launches Web and discovers deterministic fakes | `AspireSimulatorTests.SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints` |
| Local and Hybrid use explicit external endpoint configuration | `ExternalEndpointProfileTests.LocalAndHybridProfilesLaunchWithExplicitExternalEndpointReferences` |
| Browser reaches actual AppHost Web resource | `BrowserSmokeTests.PlaywrightLoadsTheAspireSimulatorPage` |
| SDK compatibility uses pinned actual runtime | T01 `--contracts` suite, `sdk-contracts-platform` on Linux/macOS |
| Adapter streams events with explicit local/cloud provider and transcript isolation | `CopilotAgentEngineContractTests.ActualRuntimeStreamsEventsUsesExplicitProviderAndPersistsOrderedTerminalOutcome` |
| Adapter exposes only the exact registered tool and forwards host outcome; reconnect replay resumes after the observed sequence without redispatching or duplicating the final event | `CopilotAgentEngineContractTests.ActualRuntimeInvokesOnlyRegisteredToolAndForwardsHostOutcome` |
| Tool execution count cannot exceed the host budget | `CopilotAgentEngineContractTests.ActualRuntimeToolLoopCannotExceedHostBudget` |
| Explicit cancellation reaches a blocked host callback exactly once | `CopilotAgentEngineContractTests.CancelAsyncCancelsHostToolAndEmitsOnePersistedCancellationOutcome` |
| SDK child-process crash becomes interruption; next turn starts a fresh runtime | `CopilotAgentEngineContractTests.RuntimeProcessCrashBecomesInterruptedAndNextTurnStartsFreshRuntime` |
| Provider failure is explicit and does not include provider error content | `CopilotAgentEngineContractTests.ProviderFailureEmitsSafeFailureCodeWithoutLeakingProviderMessage` |
| Terminal status and event commit atomically; stale compare-and-swap writes do not append an event | `CopilotTurnStateMachineTests.TurnStateMachineAppliesRunningAndTerminalTransitionsWithCompareAndSwap` |
| Every terminal signal maps to one typed status/event pair | `CopilotTurnStateMachineTests.TurnStateMachineMapsEveryEngineSignalToOneTypedTerminalOutcome` |
| Cleanup attempts subscription, session, and client disposal and classifies uncertainty as interruption | `CopilotTurnStateMachineTests.RuntimeCleanupAttemptsEveryResourceAndReportsCleanupFailure` |
| Prohibited dependency edges are rejected | `ArchitectureBoundaryTests` negative fixtures |
| Missing discovery/report/low coverage fails | `PersonalAgent.Validation gate-self-test` |
| Browser test failure is visible | `BrowserSmokeTests.DeliberatelyIncorrectBrowserAssertionFails` probe |

The full MVP acceptance matrix is added as T03–T12 features land; a passing
scaffold is not a claim that those unimplemented scenarios are covered.
