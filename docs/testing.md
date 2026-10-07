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
state machine in `AgentEngine/Copilot/CopilotTurnStateMachine.cs` and active
turn cancellation/runtime lifecycle in `AgentEngine/Copilot/CopilotActiveTurn.cs`,
and M1 routing/context policy in `Application/Routing/LocalOnlyModelRouter.cs`
and `Application/Context/ConversationContextBuilder.cs`, plus M1 bounded local
turn lifecycle, queue, and recovery in
`Application/TurnCoordination/LocalTurnCoordinator.cs`, at 95% / 90%;
changed executable lines at 90%.
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
| LocalOnly text cannot change the selected provider or invoke cloud | `RoutingAndContextTests.LocalOnlyRouterKeepsUntrustedEscalationTextOnTheSelectedLocalRoute`, `RoutingAndContextTests.LocalOnlyRouterRejectsCloudModesAndTasksWithoutDowngrading` |
| Ambiguous categories clarify through a durable owner-facing event; unsupported routes persist a safe limitation | `RoutingAndContextTests.LocalOnlyRouterClarifiesAmbiguousAndEmptyTasksAndRejectsUnsupportedCategories`, `LocalTurnCoordinatorTests.ClarifyingRoutePersistsPromptWithoutInvokingEngine`, `LocalTurnCoordinatorTests.ConflictingRequestIdentityIsRejectedAndRouteRejectionIsTerminal` |
| Context is bounded, ordered, provenance-labelled and LocalOnly; omitted-history count is explicitly a lower bound when the bounded read detects older rows | `RoutingAndContextTests.ContextBuilderIncludesOrderedRecentHistoryAndLabelsEveryItemLocalOnly`, `ConversationContextBuilderTests.ContextBuilderUsesIsolatedSqliteHistoryAndPreservesOrderProvenanceAndPrivacyLabels` |
| Unsupported roles, cross-conversation messages and oversized history are excluded | `RoutingAndContextTests.ContextBuilderExcludesUnsupportedRolesCrossConversationAndOversizedHistory` |
| Prompt estimates include instructions, task, tool catalog, history, serialization and reserve | `RoutingAndContextTests.ContextEstimateIncludesInstructionsTaskToolsSerializationAndReserve` |
| Mandatory context over budget is rejected before reading conversation history | `RoutingAndContextTests.ContextBuilderFailsExplicitlyBeforeReadingHistoryWhenRequiredPromptExceedsBudget` |
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
| Durable request ID retries return the original turn and conflicting reuse fails after reopening SQLite | `SqlitePersistenceTests.TurnSubmissionIsAtomicAndDeduplicatedAcrossStoreReopen` |
| Turn completion atomically stores one final assistant message and terminal event | `SqlitePersistenceTests.TerminalCompletionPersistsItsFinalAssistantMessageAtomicallyOnce`, `LocalTurnCoordinatorTests.CoordinatorPersistsOneCompletedAnswerAndReturnsDuplicateRequest` |
| Coordinator restart interrupts recovered work without replaying inference | `LocalTurnCoordinatorTests.StartupInterruptsRecoveredTurnWithoutCallingInferenceAgain` |
| Concurrent startup cannot duplicate recovery/workers; failed/cancelled recovery releases its reservation and shutdown during recovery prevents later admission | `LocalTurnCoordinatorTests.ConcurrentStartupReservesRecoveryAndMaintainsFourWorkers`, `LocalTurnCoordinatorTests.FailedOrCancelledRecoveryReleasesStartupReservation`, `LocalTurnCoordinatorTests.StopDuringRecoveryPreventsWorkerLaunchAndAdmission` |
| The bounded scheduler runs four turns and rejects a 21st queued request without persisting it | `LocalTurnCoordinatorTests.CoordinatorRejectsTwentyFirstQueuedTurnWithoutPersistingIt` |
| Same-conversation waiters remain inside the 20-item queue bound without starving unrelated conversations | `LocalTurnCoordinatorTests.SameConversationWaitersDoNotStarveOtherConversations`, `LocalTurnCoordinatorTests.SameConversationDeferredTurnsRemainInsideQueueLimit` |
| Deferred turns cannot be overtaken by newer channel entries while the other workers are occupied | `LocalTurnCoordinatorTests.DeferredTurnPrecedesNewerChannelTurnWhenAllOtherWorkersAreBusy` |
| Cancelled and expired pending entries immediately free reservations, both in the channel and deferred queue | `LocalTurnCoordinatorTests.TerminalPendingTurnsImmediatelyReleaseCapacity` |
| Delayed context cleanup preserves the first deadline/owner cancellation cause in both orders | `LocalTurnCoordinatorTests.DelayedContextPreservesFirstDeadlineOrOwnerCancellation` |
| Failed deadline monitors and cancelled workers reject new durable submissions and surface shutdown failures | `LocalTurnCoordinatorTests.DeadlinePersistenceFailureFailsReadinessAndShutdown`, `LocalTurnCoordinatorTests.TerminalPersistenceTimeoutCancelsWorkerAndRejectsFurtherSubmissions` |
| Context excludes later submissions before bounded selection but retains late answers from predecessor turns | `LocalTurnCoordinatorTests.ContextExcludesLaterSubmissionsButIncludesLateAnswerFromPrecedingTurn` |
| Only the winning cancellation cause reaches active inference while publication is paused; losing direct engine cancellation is suppressed | `LocalTurnCoordinatorTests.ActiveEngineOnlyReceivesWinningCauseWhileCancellationPublicationIsPaused` |
| Held callbacks leave readiness responsive; shutdown enforces the actual 15-second budget, and caller tokens bound shutdown/active/pending cancellation while lifetime and cleanup remain owned | `LocalTurnCoordinatorTests.HeldPublicationDoesNotBlockReadinessAndShutdownOrCallerBounds`, `LocalTurnCoordinatorTests.CallerCancellationBoundsShutdownWhileInterruptionPersistenceContinues` |
| Concurrent/repeated shutdown waits share one operation, retain timeout/failure after cleanup, and cannot report success while held publication is unfinished | `LocalTurnCoordinatorTests.HeldPublicationDoesNotBlockReadinessAndShutdownOrCallerBounds`, `LocalTurnCoordinatorTests.ShutdownSurfacesEngineStopFailureAfterResolvingActiveTurn`, `LocalTurnCoordinatorTests.ShutdownAggregatesEngineAndTerminalPersistenceFailures`, `LocalTurnCoordinatorTests.PublicationCallbackFailuresSurfaceWithoutStrandingPendingCleanup` |
| Shutdown admission closure selects accepted-turn causes before context completion, preserving earlier owner/deadline winners and exactly one truthful terminal event without inference | `LocalTurnCoordinatorTests.ShutdownAdmissionClosureSelectsCauseBeforeContextReturns` |
| Engine/execution callback failures remain explicit and pending terminal cleanup is not stranded | `LocalTurnCoordinatorTests.PublicationCallbackFailuresSurfaceWithoutStrandingPendingCleanup` |
| Selected owner cancellation remains authoritative while publication is held at pending, routing and pre-engine boundaries; no clarification/rejection/inference replaces it | `LocalTurnCoordinatorTests.SelectedOwnerCausePreventsRoutingOutcomeOrInferenceBeforePublication` |
| A worker claiming cancelled work during pending persistence cannot double-release capacity or duplicate terminal outcomes | `LocalTurnCoordinatorTests.WorkerClaimDuringPendingCancellationPersistenceReclaimsExactlyOnce` |
| The production adapter's independent deadline respects an owner/shutdown winner whose signal delivery is paused; standalone and host-arbitrated engine timeouts remain bounded | `LocalTurnCoordinatorTests.ProductionEngineDeadlineUsesFirstCauseWhileWinningSignalPublicationIsPaused`, `CopilotAgentEngineContractTests.DeadlineBoundsBlockedEventPersistenceWithoutMisclassifyingCancellation` |
| Successful actual-runtime provider responses respect selected owner/shutdown before callback delivery, without a completed event/final answer; shutdown reason is `host_shutdown` | `LocalTurnCoordinatorTests.SuccessfulActualRuntimeResponsePreservesSelectedCauseBeforePublication` |
| Repeated cancel/resubmit cycles reclaim pending requests independently while all four execution workers stay occupied | `LocalTurnCoordinatorTests.RepeatedPendingCancellationReclaimsRequestResourcesWhileAllWorkersRemainOccupied` |
| Boundary-free history uses the conversation/sequence index before LIMIT, sorting only the bounded outer page | `LocalTurnCoordinatorTests.ContextExcludesLaterSubmissionsButIncludesLateAnswerFromPrecedingTurn` (actual production SQL query-plan assertion) |
| Bare Ollama origins map to `/v1`, explicit API paths survive, and Local/Hybrid composition completes actual runtime turns on strict endpoints | `SqliteAndWebSmokeTests.LocalProviderCompositionExecutesActualRuntimeAgainstStrictApiPath` |
| The interactive deadline starts at durable acceptance, includes queue/context time, cancels active engine work without misclassifying it as owner cancellation, and prevents expired or stalled work from entering inference | `LocalTurnCoordinatorTests.QueuedTurnThatExceedsItsAcceptedDeadlineIsInterruptedWithoutExecution`, `LocalTurnCoordinatorTests.InteractiveDeadlineCancelsBlockedContextConstruction`, `LocalTurnCoordinatorTests.InteractiveDeadlineIsCheckedAfterUncooperativeContextBuilderReturns`, `LocalTurnCoordinatorTests.InteractiveDeadlineInterruptsActiveEngineTurn`, `CopilotAgentEngineContractTests.AcceptanceDeadlineCancellationPersistsInterruptedRatherThanCancelled`, `CopilotAgentEngineContractTests.DeadlineCancelsStalledProviderAndPersistsOneInterruptedOutcome` |
| Ambiguous local requests persist an owner-facing clarification without invoking inference | `LocalTurnCoordinatorTests.AmbiguousLocalRequestPersistsClarificationWithoutInvokingTheEngine` |
| SQLite readiness detects missing or outdated storage without creating a replacement database | `SqlitePersistenceTests.DatabaseReadinessChecksSchemaWithoutCreatingMissingDatabase`, `AspireSimulatorTests.ReadinessReportsDatabaseUnavailableAfterStartup` |
| Existing default databases are preserved and ambiguous defaults fail closed | `SqlitePersistenceTests.DataDirectoryPreservesLegacyAppHostStateAndRejectsAmbiguousDefaults` |
| Direct Web startup rejects unsupported profiles before persistence access | `SqliteAndWebSmokeTests.DirectHostRejectsUnsupportedProfilesBeforeOpeningStorage` |
| Concurrent startup recovers a prepared restore once | `SqlitePersistenceTests.ConcurrentStartupSerializesPreparedRestoreRecovery` |
| Independent workers race on optimistic writes and startup migrations | `SqlitePersistenceTests.ConcurrentMemoryWritersAllowOnlyOneExpectedVersionUpdate`, `SqlitePersistenceTests.SimultaneousStartupAppliesEachMigrationOnlyOnce` |
| Expired conversations with unresolved turns survive retention | `SqlitePersistenceTests.RetentionUsesDefaultAndOverrideWindowsAndKeepsDurableState` |
| Razor host works without external services | `SqliteAndWebSmokeTests.RazorHostServesTheConfiguredProfileWithoutExternalServices` |
| Direct test-profile startup requires isolated data | `SqliteAndWebSmokeTests.TestProfileRequiresAnExplicitDataDirectory` |
| Aspire configures Web against its controlled OpenAI-compatible simulator and reports database loss after startup | `AspireSimulatorTests.SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints`, `AspireSimulatorTests.ReadinessReportsDatabaseUnavailableAfterStartup` |
| Local and Hybrid use explicit external endpoint configuration | `ExternalEndpointProfileTests.LocalAndHybridProfilesLaunchWithExplicitExternalEndpointReferences` |
| Browser reaches actual AppHost Web resource | `BrowserSmokeTests.PlaywrightLoadsTheAspireSimulatorPage` |
| SDK compatibility uses pinned actual runtime | T01 `--contracts` suite, `sdk-contracts-platform` on Linux/macOS |
| Adapter streams events with explicit local/cloud provider and transcript isolation | `CopilotAgentEngineContractTests.ActualRuntimeStreamsEventsUsesExplicitProviderAndPersistsOrderedTerminalOutcome` |
| Adapter exposes only the exact registered tool and forwards host outcome; reconnect replay resumes after the observed sequence without redispatching or duplicating the final event | `CopilotAgentEngineContractTests.ActualRuntimeInvokesOnlyRegisteredToolAndForwardsHostOutcome` |
| Tool execution count cannot exceed the host budget | `CopilotAgentEngineContractTests.ActualRuntimeToolLoopCannotExceedHostBudget` |
| Explicit cancellation reaches a blocked host callback exactly once | `CopilotAgentEngineContractTests.CancelAsyncCancelsHostToolAndEmitsOnePersistedCancellationOutcome` |
| SDK child-process crash becomes interruption; next turn starts a fresh runtime | `CopilotAgentEngineContractTests.RuntimeProcessCrashBecomesInterruptedAndNextTurnStartsFreshRuntime` |
| Provider failure is explicit and does not include provider error content | `CopilotAgentEngineContractTests.ProviderFailureEmitsSafeFailureCodeWithoutLeakingProviderMessage` |
| Event-persistence failure aborts runtime and commits an interrupted terminal outcome when storage recovers; deadline cancellation bounds a blocked append without being classified as a persistence failure | `CopilotAgentEngineContractTests.EventPersistenceFailureInterruptsRuntimeAndPersistsTerminalOutcome`, `CopilotAgentEngineContractTests.DeadlineBoundsBlockedEventPersistenceWithoutMisclassifyingCancellation` |
| A stalled provider is canceled at the request deadline and persists one interrupted terminal outcome | `CopilotAgentEngineContractTests.DeadlineCancelsStalledProviderAndPersistsOneInterruptedOutcome` |
| Terminal status and event commit atomically; stale compare-and-swap writes do not append an event | `CopilotTurnStateMachineTests.TurnStateMachineAppliesRunningAndTerminalTransitionsWithCompareAndSwap` |
| Terminal persistence observes its bounded cancellation token | `CopilotTurnStateMachineTests.TurnStateMachineAppliesRunningAndTerminalTransitionsWithCompareAndSwap` |
| Every terminal signal maps to one typed status/event pair | `CopilotTurnStateMachineTests.TurnStateMachineMapsEveryEngineSignalToOneTypedTerminalOutcome` |
| Active-turn cancellation, tool-budget transitions, and terminal-signal precedence | `CopilotTurnStateMachineTests.ActiveTurnCancellationAndBudgetTransitionsAreHostControlled`, `CopilotTurnStateMachineTests.ActiveTurnSelectsTerminalOutcomeByBudgetCancellationDeadlinePrecedence` |
| Tool-budget rejection drains normally, while deadline/caller/shutdown still cancel blocked rejection persistence and permit one durable budget-failure outcome | `CopilotAgentEngineContractTests.ActualRuntimeToolLoopCannotExceedHostBudget`, `CopilotAgentEngineContractTests.ExternalCancellationBoundsBlockedToolBudgetRejectionPersistence` |
| Production environment-secret lookup returns only named controlled values and rejects missing/empty values, invalid names and cancellation without credential disclosure | `EnvironmentSecretResolverTests.NamedLookupReturnsFixtureOrExplicitlyRejectsUnavailableValue`, `EnvironmentSecretResolverTests.InvalidNamesAreRejectedBeforeLookup`, `EnvironmentSecretResolverTests.NullOrOversizedReferenceAndCancellationAreExplicit` |
| Interrupted turns are terminal and excluded from post-restart recovery work | `SqlitePersistenceTests.NonterminalTurnStateAndVersionCanBeRecoveredAfterRestart` |
| Runtime cleanup deletes the turn directory after shutdown and classifies cleanup failures | `CopilotTurnStateMachineTests.RuntimeCleanupDeletesTurnDirectoryAfterStoppingRuntime`, `CopilotTurnStateMachineTests.RuntimeCleanupAttemptsEveryResourceAndReportsCleanupFailure` |
| A timed-out runtime force-stop is shared; disposal does not race the still-running stop | `CopilotTurnStateMachineTests.RuntimeStopTimeoutSharesInFlightStopAndWaitsBeforeDisposal` |
| Prohibited dependency edges are rejected | `ArchitectureBoundaryTests` negative fixtures |
| Missing discovery/report/low coverage fails | `PersonalAgent.Validation gate-self-test` |
| Browser test failure is visible | `BrowserSmokeTests.DeliberatelyIncorrectBrowserAssertionFails` probe |

Acceptance mappings grow with each milestone-local task in
[the backlog](Personal-Agent-Project-Spec.md#15-agent-ready-implementation-backlog).
Each milestone's cumulative runnable demo and implemented scenarios must pass
before it closes. M4-01 consolidates the complete MVP matrix; it does not defer
earlier feature checks. A passing scaffold is not evidence for unimplemented
scenarios, and simulator results are not live model/device results.
