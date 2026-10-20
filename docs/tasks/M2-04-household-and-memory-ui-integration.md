# M2-04: Household and memory UI, integration, and runnable demo

## Objective and acceptance

Finish M2 as the final M2 task: connect the registered Home Assistant and
confirmed-memory tools to local chat, expose owner-controlled household,
approval, memory, and activity experiences, and ship the runnable controlled
simulator flow. Acceptance is issue #29 and the project specification §§2
(stories 1–4, 7–9), 7–9, 12–14, 18–21. The applicable §14 scenarios include
ambiguous entity clarification; unregistered/disallowed tool denial; exact
action replay; submitted write timeout as Unknown with observation but no
blind replay; expired/replayed/changed approval rejection; cancellation
truthfulness; durable memory across engine loss; malicious memory as data;
unauthorized/CSRF mutation rejection; event-stream reconnect; secret redaction;
and provider failure not disabling local features.

## Scope

- Wire the existing home/memory tool registrations through the production
  local coordinator and dispatcher, preserving bounded tool/turn execution,
  repeated-failure limits, cancellation, SSE ordering/resume, and durable
  action outcomes.
- Add authenticated owner APIs and UI for exact-action approval decisions,
  confirmed memory proposals/conflicts/edit/delete, household connection and
  allowlist status, and useful safe Activity/audit details.
- Apply the accepted M1-06 Soft editorial visual foundation to M2 screens.
  Distinguish target/arguments, expiry, sensor units/time/freshness, and
  Verified/Failed/Unknown outcomes; provide accessible responsive and
  keyboard/focus behavior for narrow and desktop layouts, long names/facts,
  and empty/loading/error/conflict states.
- Retain provider-outage access to approvals, memory, household status, and
  Activity. Keep credentials as named references and never display secret
  values.
- Exercise deterministic sensor reads, ambiguity, permitted simulated light
  action, uncertain timeout/restart, approval accept/reject/expiry/replay,
  confirmed memory conflict/edit/delete/restart, audit, and the secure API/UI
  boundaries. Exercise thermostat approval only behind a trusted feature
  gate; it stays disabled by default.

## Exclusions

No cloud consent or cloud review, reminders, new integrations, live household
connection by default, real device writes in required tests, M3/M4 deliverables,
release certification, generic service execution, or redesign of policy owned
by M2-01/02/03. No real credentials, cloud disclosure, or private development
data are used by credential-free validation.

## Dependencies and assumptions

- Closed prerequisites: M1 and M2-01 #5, M2-02 #8, M2-03 #9. The accepted
  visual foundation in M1-06 #42 is authoritative.
- SQLite and Application use cases remain authoritative for memory,
  approvals, action journal, audit, and conversations; Copilot runtime state
  is replaceable.
- Exact owner confirmation is a trusted UI event. All mutations retain
  authentication, CSRF, owner checks, expected-version/conflict handling,
  expiry/replay protections, and durable idempotency.
- Physical service submissions can have uncertain outcomes. Unknown remains
  explicit, observation does not claim causality, and neither restart nor
  cancellation retries or rolls back a submitted write.
- Simulator/E2E controls use isolated data and managed deterministic
  endpoints; they are unavailable through model input and must not leak into
  live profiles.
- Keep established application contracts and policy intact unless code
  inspection identifies a narrow integration gap; any material conflict with
  accepted ADRs requires an explicit documented resolution.
- The first Release validation exposed transitive `MessagePack` 2.5.302
  advisory GHSA-qhrr-8q5h-9q3h from the pinned Aspire graph; pin the compatible
  patched 2.5.303 release centrally and refresh only affected lock files rather
  than suppressing NuGet audit.

## Owned files and boundaries

- Application tool/coordinator integration only where required:
  `src/PersonalAgent.Application/` and tests for any changed contract.
- Web composition/API/UI:
  `src/PersonalAgent.Web/Program.cs`, `ChatApi.cs`, `Pages/Index.cshtml`,
  `wwwroot/js/chat.js`, `wwwroot/css/site.css`, and `README.md`.
- Controlled fixtures and verification:
  `tests/PersonalAgent.TestSupport/`,
  `tests/PersonalAgent.IntegrationTests/`,
  `tests/PersonalAgent.E2ETests/`, and simulator endpoint composition only
  where required for deterministic acceptance.
- Directly affected scenario, architecture, development, testing, and
  operations guidance. Migration/recovery changes require focused upgrade
  tests and documentation.
- Web handlers call Application use cases, never raw SQLite/provider/Home
  Assistant clients. Infrastructure references remain confined to the Web
  composition root.

## Tests and documentation

- Add unit/integration tests for application-facing proposal/decision flow,
  owner isolation, CSRF, stale versions, request/action replay, expiry,
  changed arguments, and truthful safe failures.
- Add controlled Aspire API and Playwright scenarios for chat tool routing,
  sensor units/timestamps/staleness, ambiguous target clarification, allowed
  light control, uncertain submitted action and restart, approval decision
  lifecycle, memory proposal/conflict/edit/delete and persistence, audit
  inspection, and provider outage.
- Exercise 1440/768/390/320px layouts, keyboard/focus behavior, 180-character
  fact/entity names, a 420-character fact value, and empty states using
  privacy-safe fixtures. The pathological long-content display fixture
  intercepts only the read API; it does not represent stored household data.
  Document measured accessibility evidence and limits; do not claim complete
  WCAG conformance.
- Preserve representative privacy-safe empty-state screenshots for the four
  owner surfaces plus a narrow household view in this task record.
- Update `docs/testing.md` scenario mapping, `docs/architecture.md`,
  `docs/development.md`, `docs/operations.md`, `src/PersonalAgent.Web/README.md`,
  and `README.md` where behavior/setup changes. Preserve the opt-in boundary
  and consent prerequisites for live HA smoke tests.
- Run the required pinned-SDK repository validation gates: build/analyzers,
  architecture, unit, integration, SDK contracts, coverage, Aspire E2E,
  browser E2E, and docs. Do not weaken any gate or claim unrun evidence.

### Acceptance evidence map

- Production-composition chat routing and one verified simulated light write:
  `AspireSimulatorTests.WebChatDispatchesAllowedSimulatedLightThroughProductionComposition`.
- Owner-filtered APIs, bounded lists, allowlisted entities, and antiforgery
  rejection: `AspireSimulatorTests.OwnerUiApisExposeOnlyBoundedMemoryApprovalsAndAllowlistedHouseholdStatus`.
- Sensor timestamps/availability/staleness, ambiguity, secret redaction,
  submitted-write uncertainty, cancellation, and restart observation:
  `HomeAssistantTests.ReadsDistinguishStalenessAvailabilityAndSourceTimeWithoutInventingValues`,
  `HomeAssistantTests.AmbiguousSecurityUnknownAndOutOfBoundsProposalsMakeNoRequests`,
  `HomeAssistantTests.SecretsNeverAppearInErrorsToolResultsJournalOrCapturedLogs`,
  `HomeAssistantTests.UncertainWriteReconcilesOnlyByObservationAndNeverResends`,
  and `HomeAssistantTests.LostWriteResponseAndRestartRecoveryObserveWithoutReplaying`.
- Approval exactness, rejection, expiry, replay and stale-version/owner
  protection: `ToolDispatcherTests.OwnerWorkflowReviewsAndExecutesOnlyTheExactApprovedHomeAction`,
  `ToolDispatcherTests.ExpiredApprovalIsJournaledExpiredWithoutExecution`,
  `ToolDispatcherTests.ApprovalListExpiresPendingActionsAndKeepsNewerApprovalPending`,
  `ToolDispatcherTests.ChangedProposalWhileApprovalIsPendingInvalidatesTheAction`,
  `ToolDispatcherTests.ApprovalListRecoversInterruptedApprovedHomeActionByObservationOnly`,
  `ToolDispatcherTests.AlreadyApprovedUnclaimedHomeActionCanResumeAfterDispatcherRestart`,
  `BrowserSmokeTests.ApprovedUnclaimedActionOffersAnExplicitResumeControl`,
  `ToolDispatcherTests.ExpiredApprovedUnclaimedActionCanBeFinalizedWithoutExecution`,
  `BrowserSmokeTests.ExpiredApprovedActionOffersNoExecutionFinalization`, and
  `ConfirmedMemoryTests.RejectionExpiryAndForgedSourceCannotCommit`.
- Interrupted expiry settlement, invalidated memory approval expiry, and
  active-action budget preservation:
  `ApprovalWorkflowExpiryTests.RecentApprovalReadSettlesPreviouslyExpiredApprovalWhoseActionIsStillPending`,
  `ApprovalWorkflowExpiryTests.RecentApprovalReadDoesNotReleaseTurnBudgetWhileActionIsStillExecuting`,
  `ConfirmedMemoryTests.DeleteRedactsDerivedCopiesAndOldCallsCannotRestoreThem`,
  and `ToolDispatcherTests.ApprovalListRecoversExpiredDecisionWhoseActionSettlementWasInterrupted`.
- An Unknown approved write remains blocked for an equivalent pending sibling
  across turn completion and dispatcher restart:
  `ToolDispatcherTests.UnknownApprovedWriteRemainsProtectedAcrossTurnReleaseWhileSiblingApprovalIsPending`
  and `ToolDispatcherTests.UnknownApprovedWriteRemainsProtectedAfterDispatcherRestartWhileSiblingApprovalIsPending`.
- Memory confirmation, restart/runtime loss, conflicts, retained edits,
  deletion/redaction and receipt recovery:
  `ConfirmedMemoryTests.ExplicitSensitiveFactRequiresApprovalAndSurvivesRuntimeAndStoreLoss`,
  `ConfirmedMemoryTests.ConflictsRequireExplicitResolutionAndRetainedFactsNeedDistinctContext`,
  `ConfirmedMemoryTests.RetainedFactCanBeEditedInPlaceWithExactSiblingSnapshotAndDistinctContext`,
  `ConfirmedMemoryTests.DeleteRedactsDerivedCopiesAndOldCallsCannotRestoreThem`,
  `ConfirmedMemoryTests.BoundedProposalSourcePreservesPreviouslyAcceptedRequestFingerprints`,
  `ConfirmedMemoryTests.LargeEscapedProposalAndConflictSnapshotStayOutsideTheChatMessageLimit`,
  `ConfirmedMemoryTests.ApprovalListRecoversInterruptedMemoryCommitFromItsReceipt`,
  and `ConfirmedMemoryTests.ReceiptRecoversInterruptedSettlementWithoutRepeatingTheMutation`.
- Simulator tool-result answers and same-conversation command matching:
  `AspireSimulatorTests.WebChatShowsSimulatorToolResultsAndMatchesCommandsAfterConversationHistory`.
- Simulator memory citations, explicit dispatch status, and observation-only
  recovery of an automatic light write are covered by the same Aspire
  production-composition scenario.
- Stale browser proposal responses cannot change the selected conversation or
  reset the current draft:
  `BrowserSmokeTests.DelayedMemoryProposalCannotResetFormOrSelectionAfterConversationSwitch`.
- A delayed proposal response preserves newer editor changes, and approval
  arguments remain visible before a decision:
  `BrowserSmokeTests.DelayedMemoryProposalResponsePreservesAnEditedDraft`,
  `BrowserSmokeTests.MemoryProposalRequiresExactOwnerApprovalBeforeItAppearsAsAFact`,
  and `BrowserSmokeTests.MemoryUiCreatesAndApprovesTypedProposalWithoutInference`.
- Duplicate proposal retries settle a nonterminal turn after interruption,
  and upgrade backfill rejects malformed turn identities:
  `ConfirmedMemoryTests.DuplicateProposalRetrySettlesATurnAfterItsFirstSettlementWasInterrupted`
  and `ActionOutcomePersistenceTests.VersionSevenUpgradeBackfillsDurableActionOwnerAndTurnAttribution`.
- Real Aspire host restart recovers household actions by observation without
  another write and settles memory actions from their receipt:
  `AspireSimulatorTests.ApprovalListRecoversInterruptedActionsAfterHostRestartWithoutCrossingHomeInstallationBoundary`.
  When a full AppHost restart changes the managed simulator endpoint, the
  E2E asserts fail-closed observation-scope behavior; stable-scope
  observation-only recovery is independently covered by the SQLite-backed
  integration regression.
- Browser owner surfaces, status-endpoint failure independence, keyboard
  focus, responsive widths, long-content wrapping, and privacy-safe screenshots:
  `BrowserSmokeTests.HouseholdMemoryApprovalAndActivitySurfacesReflowAndKeepKeyboardFocusVisible`.
- This evidence is controlled, isolated, and credential-free. The status
  endpoint failure is not a live model outage; no live Home Assistant,
  physical-device effect, human observation, or assistive-technology
  certification is claimed. M1-06 and M2-01–03 scenario maps provide the
  detailed prior-slice coverage; the cumulative review ledgers are listed
  below and remain independent acceptance evidence.

## Review and completion criteria

- Carry the complete M2-01, M2-02, and M2-03 finding ledgers (including all
  fixed and summary-only “Previously missed” findings) into independent review;
  do not infer dispositions from their closure or omission in later reviews.
  The full cumulative ledgers are recorded in
  `docs/tasks/M2-01-tool-dispatcher-exact-approvals.md` (M2-01-R1–R25),
  `docs/tasks/M2-02-home-assistant-tools.md` (M2-02-R1–R6), and
  `docs/tasks/M2-03-confirmed-memory.md` (M2-03-R1–R8). The reviewer must read
  and receive all three complete records, including their summary-only entries
  and review-history notes.
- Obtain independent exact-commit agentic review of this task against issue
  #29, the specification, relevant accepted ADRs, implementation, tests, and
  documentation. Give the reviewer the exact commit, merge base, and complete
  prior finding ledgers. Disposition every new finding with verifying
  evidence; owner acceptance is required for any residual risk.
- Required automated gates pass; acceptance scenarios and changed executable
  line coverage meet existing thresholds. Migration 008 adds indexed
  owner/turn attribution to action journal rows so malformed canonical JSON
  cannot hide unresolved writes from restart recovery; its upgrade backfill
  and malformed-envelope behavior require dedicated integration coverage.
- Report the exact revision and commands/results, changed files, contract or
  schema impact, exercised and unexercised platforms/runtime behavior,
  independent review dispositions, blockers, and residual risks. Live HA,
  physical causality, and human/device observations are not represented as
  passed when unrun.

## Privacy-safe visual evidence

The controlled Playwright test captures desktop approval, memory, household,
and Activity views plus a 390-pixel household view. The fixtures contain no
household credentials or private memory; they evidence appearance and layout
only, not complete WCAG conformance or assistive-technology behavior.

- [Approvals](M2-04-screenshots/approvals.png)
- [Memory](M2-04-screenshots/memory.png)
- [Household at desktop width](M2-04-screenshots/household.png)
- [Household at 390px](M2-04-screenshots/household-mobile-390.png)
- [Activity](M2-04-screenshots/activity.png)

## Validation and remaining limits

The R8/R9 follow-up's credential-free gate baseline ran on native macOS with
the pinned .NET SDK and isolated simulator/SQLite data, against merge base
`ede7885ca87cc6bf1919f3b670f031f905f04f23`:
these results predate R10–R14 and are retained as historical evidence only.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 332/332 discovered tests, including expiry-finalization decision boundaries and concurrent approval settlement/removal. |
| `tools/validate.sh integration` | Passed; 450/450 discovered tests, including schema-v8 upgrade backfill, malformed/unattributed journal recovery, approved-action resumption and expiry finalization. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes plus 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 97.4% (596/612), all configured runtime/critical-module gates met. Application: 98.5% lines / 94.4% branches; Infrastructure: 94.2% / 82.8%; Web: 98.8% / 79.0%. |
| `tools/validate.sh aspire-e2e` | Passed; 10/10 out-of-process scenarios, including the managed simulator write and bounded/audited owner APIs. |
| `tools/validate.sh browser-e2e` | Passed; 33/33 Playwright scenarios, and the required deliberately failing browser probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |

No live Home
Assistant credentials, real household data, cloud inference, or physical
devices were used. Linux CI, live HA/provider availability, physical effect
causality, human observations, and assistive-technology certification remain
unexercised; the controlled status failure and pathological long-content
fixture are not substitutes for those opt-in/live observations.

### Exact-commit review follow-up

The independent review of implementation commit
`36586095483e2277c42a8f02887952fa7d7f709f` reported four findings. Their
dispositions below are being verified against the follow-up revision; they do
not replace the complete earlier M2 ledgers above.

| Reference | Severity | Finding | Follow-up disposition |
| --- | --- | --- | --- |
| M2-04-R1 | High | Completing the model turn could clear equivalent-write uncertainty while an approved sibling action remained actionable, allowing a second equivalent approved send after an Unknown outcome. | Implemented approval-scoped failure-budget holds: turn release defers cleanup while any same-proposal approval remains unresolved, and a settled decision releases its hold. `ToolDispatcherTests.UnknownApprovedWriteRemainsProtectedAcrossTurnReleaseWhileSiblingApprovalIsPending` reproduces Unknown → turn release → blocked sibling approval. |
| M2-04-R2 | Medium | Listing an invalidated memory approval parsed its redacted `{}` envelope before checking invalidation and failed the approval-list request. | Implemented an invalidation-first, owner-bound redacted summary with no stale content and a non-actionable `Invalidated` presentation state. `ConfirmedMemoryTests.DeleteRedactsDerivedCopiesAndOldCallsCannotRestoreThem` checks the real SQLite invalidation/list path and content redaction. |
| M2-04-R3 | Medium | Owner navigation remained enabled during an active turn and could hide the only Cancel control. | Implemented active-turn disabling for Approvals, Memory and Household navigation. `BrowserSmokeTests` asserts all three remain disabled while the delayed streaming turn and Cancel control are active. |
| M2-04-R4 | Medium | The approval workflow had no explicit critical-module 95% line / 90% branch coverage ownership. | Added `ApprovalWorkflowService.cs` to the critical-module gate and behavioral integration coverage for invalidation/redaction, owner binding, malformed/missing state, expiry, rejection, replay and execution. The refreshed coverage gate must pass before this record is complete. |
| M2-04-R5 | High | A follow-up review reproduced that dispatcher-local approval holds and write uncertainty disappear on restart; approving an equivalent pending sibling could submit a second physical write after the first was Unknown. | Added a complete SQLite owner/turn action-history read, then seed the equivalent proposal budget from persisted Unknown/orphaned Executing actions before dispatch or approval execution. The new real-SQLite restart regression asserts the sibling is rejected with `equivalent_write_outcome_unknown` and execution count remains one. Full validation and an independent exact-commit follow-up review are still required. |
| M2-04-R6 | High | A persisted unresolved action with invalid JSON could not be assigned to an owner/turn by SQLite JSON extraction and was omitted from restart uncertainty seeding. | Added migration 008 with indexed durable owner/turn columns, backfilled from valid historical envelopes. New rows retain attribution independently of later envelope integrity; any unresolved non-invalidated row without attribution fails closed. `VersionSevenUpgradeBackfillsDurableActionOwnerAndTurnAttribution`, `MalformedSameTurnUnknownEnvelopeFailsClosedBeforeFreshWrite`, and `UnattributedUnknownJournalRowFailsClosedBeforeFreshWrite` exercise upgrade and recovery boundaries. Full validation and independent exact-commit review remain required. |
| M2-04-R7 | Medium | An approval could persist as Approved while a process interruption left its Home Assistant action AwaitingApproval; the UI and workflow then had no recovery path. | Added an owner-initiated “Resume approved action” control only for an exact already-approved action that remains unclaimed and within its approval expiry. Repeated resume is idempotent; startup does not auto-submit, and Executing/Unknown actions remain outside the resume path. `AlreadyApprovedUnclaimedHomeActionCanResumeAfterDispatcherRestart` and `ApprovedUnclaimedActionOffersAnExplicitResumeControl` cover service and UI behavior. The fix and refreshed gates need independent exact-commit follow-up review. |
| M2-04-R8 | Medium | An expired Approved + AwaitingApproval action could remain nonterminal indefinitely and retain an in-process approval hold because the UI hid resume without settling the action. | Added `ExpireApprovedUnclaimedAsync` and a CSRF-protected `/expire` endpoint. The owner can finalize only an already-approved, host-clock-expired, still-unclaimed Home Assistant action; it persists Expired without execution and releases the hold. `ApprovalWorkflowExpiryTests` and the real-SQLite `ExpiredApprovedUnclaimedActionCanBeFinalizedWithoutExecution` plus `ExpiredApprovedActionOffersNoExecutionFinalization` cover policy, persistence and UI behavior. Refreshed native gates pass; exact-commit independent review is pending. |
| M2-04-R9 | Medium | Owner Activity queried `OwnerId` from canonical JSON, hiding newly-attributed journal rows after canonical-envelope corruption. | Changed owner action listing to use durable `action_owner_id` and retained `memory_owner_id` for redacted memory rows. `MalformedSameTurnUnknownEnvelopeFailsClosedBeforeFreshWrite` verifies the damaged row remains visible only to its owner. Refreshed native gates pass; exact-commit independent review is pending. |

The restart recovery follow-up includes migration 008 and owner-initiated
approval resumption, plus R8's explicit no-execution expiry finalization and
R9's durable owner attribution for Activity. Validation was rerun on native
macOS against merge base `ede7885ca87cc6bf1919f3b670f031f905f04f23`; the
current branch has no newer `origin/main` commits to integrate. The final
exact-commit independent review of R8/R9 and the complete cumulative M2-01/02/03
ledgers remains required before owner acceptance. The owner retains the
acceptance and merge decisions.

### Exact-commit review follow-up: R10–R14

An independent review of `f03cf8644b0e8ee8d4160de439280476a7fbdaa9` reported
five additional findings. The follow-up implementation and focused regressions
are in progress; the previous full-gate evidence above predates these changes.

| Reference | Severity | Finding | Follow-up disposition |
| --- | --- | --- | --- |
| M2-04-R10 | High | Memory edits/deletions and conflict resolution depended on model inference; the Memory surface had no typed proposal path. | Added authenticated `/api/memory/conflicts` and CSRF-protected `/api/memory/proposals`, plus typed create/edit/delete controls and explicit current-conflict supersede/retain-both decisions. `MemoryProposalService` persists owner source provenance, binds optimistic target/conflict versions, uses a stable request identity for exact replay, and routes only through the existing dispatcher/approval policy. `OwnerEnteredCreateEditAndDeleteProposalsNeedNoModelInference`, `OwnerEnteredConflictProposalRejectsAStaleReviewedVersion`, and `MemoryUiCreatesAndApprovesTypedProposalWithoutInference` cover durable SQLite and browser approval/retain-both flow. Full gates and exact-commit review remain required. |
| M2-04-R11 | Medium | Resuming an already-approved but unclaimed memory proposal attempted to settle an already-Approved decision; expired memory approvals could not be finalized. | The approval workflow now completes an already-Approved memory action from its durable host proposal and permits expiry finalization only for the matching owner-attributed memory action. `AlreadyApprovedUnclaimedMemoryProposalCanResumeAfterInterruption`, `ExpiredApprovedUnclaimedMemoryProposalCanBeFinalizedWithoutCommit`, and `ApprovalWorkflowExpiryTests` verify resume, host-clock expiry, no commit on finalization, and owner attribution. Full gates and exact-commit review remain required. |
| M2-04-R12 | High | A stale action-history snapshot could register uncertainty after a matching write had already succeeded, blocking equivalent future writes. | Candidate uncertainty is provisionally seeded, then serialized through the proposal gate and refreshed from durable action state before retention. `HistorySeedingDoesNotResurrectUncertaintyAfterMatchingExecutionSucceeds` pauses the history read, completes the matching write, then resumes stale history and verifies the durable success is not converted to uncertainty. Full gates and exact-commit review remain required. |
| M2-04-R13 | Medium | The ordinary Simulator model returned fixed text rather than deterministic tool calls, making the documented household/memory demo non-runnable without E2E-only injection. | The managed Simulator/E2E model now recognizes a small documented set of literal read/write/memory commands and emits only registered tool calls. Every call still passes the production authorization, explicit-intent, exact-approval, and memory provenance boundaries. `SimulatorDemoMapsDocumentedCommandsToBoundedRegisteredTools` and `WebChatRunsDocumentedSimulatorLightCommandWithoutFixtureInjection` verify the model protocol and full controlled Web/HA path. Local/Hybrid never launch the fixture; full gates and exact-commit review remain required. |
| M2-04-R14 | Medium | Activity read the oldest bounded audit page and filtered afterward, omitting recent action/approval events. | Added a dedicated SQLite `ReadRecentActivityAsync` query that filters the requested event families before applying a newest-first bound; general audit reads retain their previous ordering. `AuditPageIncludesNewestEventsBeforeApplyingItsBoundedLimit` adds 205 unrelated events and 202 approval events, then verifies the newest bounded activity page. Full gates and exact-commit review remain required. |
| M2-04-R15 | Medium | The typed owner-proposal path dispatched directly without releasing its dispatcher turn state, retaining per-turn failure-budget entries for process lifetime. | Fixed in `4fd3e2e99589366c07f56f016cd4424bd92b7280`: `MemoryProposalService.ProposeAsync` now calls `ReleaseTurn` in `finally` after dispatch and turn-status settlement, including dispatch exceptions. `OwnerEnteredProposalReleasesItsDispatcherTurnAfterSuccessOrFailure` verifies release both after an approval-required result and a thrown dispatch. Independent exact-commit review found no new finding and passed the focused unit regression plus the related real-dispatcher approval-hold and typed-proposal integration cases. |

### R10–R14 validation baseline

Final credential-free validation for the current follow-up source ran on native
macOS with the pinned .NET SDK and isolated Simulator/SQLite data. The initial
coverage run exposed a unit-only Application branch shortfall and an M2-03
memory-proposal critical-module shortfall; focused behavior tests were added,
and the refreshed unit and combined coverage gates passed.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, format and analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 335/335 discovered tests. |
| `tools/validate.sh integration` | Passed; 456/456 discovered tests. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.1% (811/844). Application 98.4% lines / 94.0% branches; Infrastructure 94.2% / 82.6%; ServiceDefaults 100% / 100%; Web 98.0% / 79.0%; all critical-module and unit-only thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 12/12 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 34/34 Playwright scenarios; the deliberately failing browser gate probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |

These results apply to `f574ef0a08d6b0cca1b50b449f746b8f88e2b244`, before R15,
and are retained as the R10–R14 baseline.

### Post-R15 validation

The R15 fix was validated on native macOS with the pinned .NET SDK and isolated
Simulator/SQLite data. `tools/validate.sh integration` passed 456/456 without
coverage instrumentation. The combined coverage gate used the fresh 336/336
unit report and the prior 456/456 integration coverage report from the R10–R14
run; it did not claim that the standalone post-fix integration run generated
coverage.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 336/336 discovered tests, including the R15 release regression. |
| `tools/validate.sh integration` | Passed; 456/456 discovered tests, run without coverage instrumentation. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed using current unit coverage and the preceding R10–R14 integration coverage report; changed executable lines 96.1% (819/852). Application 98.4% lines / 94.1% branches; Infrastructure 94.2% / 82.6%; ServiceDefaults 100% / 100%; Web 98.0% / 79.0%. |
| `tools/validate.sh aspire-e2e` | Passed; 12/12 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 34/34 Playwright scenarios; the deliberately failing browser gate probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |

An independent exact-commit review of the R15 fix and complete M2-01/02/03
cumulative ledgers reviewed `4fd3e2e99589366c07f56f016cd4424bd92b7280`
(parent `f574ef0a08d6b0cca1b50b449f746b8f88e2b244`,
merge base `ede7885ca87cc6bf1919f3b670f031f905f04f23`). The reviewer read the
full M2-04 and M2-01/02/03 task ledgers, issue #29, the relevant specification
acceptance excerpts and ADRs 0001, 0003, 0004, 0005 and 0006. It independently
ran the focused R15 unit regression (1/1) and related integration cases for
typed proposals and approval-hold preservation (2/2) from the exact checkout;
it did not rerun the full validation matrix. The review found no new issue and
confirmed the R15 release preserves pending approval holds: turn release
defers cleanup until the last approval hold is released. This disposition
applies to the bounded R15 delta; earlier findings retain their recorded
evidence and were not all rerun. The reviewer inspected relevant specification
excerpts, not the entire specification end-to-end.

No live Home Assistant credentials, private household data, cloud inference,
or physical devices were used. Linux CI, live HA/provider availability,
physical-effect causality, human observations, and assistive-technology
certification remain unexercised.

### Exact-commit review follow-up: R16–R21

PR #48 review identified six additional actionable findings. The fixes and
their regressions are tracked below; full validation results and the exact
review revision will be recorded after this follow-up is verified.

| Reference | Severity | Finding | Follow-up disposition |
| --- | --- | --- | --- |
| M2-04-R16 | High | Approved actions left `Executing` after process interruption had no owner-workflow recovery path; household writes could remain unsettled and committed memory receipts could be missed. | `ApprovalWorkflowService.ReadRecentAsync` now performs bounded recovery of Approved+Executing actions from durable owner/action data. Household actions go through the dispatcher's observation-only restart branch; missing source is settled as Unknown. Memory actions use receipt recovery. Real SQLite and Aspire host-restart regressions are `ApprovalListRecoversInterruptedApprovedHomeActionByObservationOnly`, `ApprovalListRecoversInterruptedMemoryCommitFromItsReceipt`, and `ApprovalListRecoversInterruptedActionsAfterHostRestartWithoutCrossingHomeInstallationBoundary`. The Aspire scenario also proves a changed managed simulator endpoint is not observed as the previously approved installation. |
| M2-04-R17 | Medium | Valid memory proposals could exceed the 8,000-character conversation-message limit when serialized values, escaping, and conflict references were used as source text. | Persist the original owner message as a short provenance marker while retaining the exact canonical mutation and request fingerprint in the durable action. The fingerprint preserves its pre-change serialization for upgrade-safe retries. `LargeEscapedProposalAndConflictSnapshotStayOutsideTheChatMessageLimit` checks an 8,192-character escaped value, all 100 conflict references, exact preview/commit, and bounded source text; `BoundedProposalSourcePreservesPreviouslyAcceptedRequestFingerprints` verifies old accepted request IDs remain idempotent. |
| M2-04-R18 | Medium | Expired Pending approvals remained `AwaitingApproval` and retained in-process dispatcher budget holds after the UI removed their decision controls. | Approval-list reads now settle expired Pending approvals using the host clock and release the corresponding hold only after durable settlement. `ApprovalListExpiresPendingActionsAndKeepsNewerApprovalPending` advances a controlled clock without redispatch, settles an expired approval, and preserves a newer actionable sibling. |
| M2-04-R19 | Medium | A delayed memory-proposal response could restore an old conversation selection and clear the editor after the owner switched conversations. | Capture the local proposal conversation and selection generation; stale responses no longer change selection, reset the draft, or show stale errors. The delayed Playwright regression switches conversations while the POST is held and checks both selection and draft after response. |
| M2-04-R20 | Medium | The Simulator emitted registered tool calls but did not display their results as final answers. | The controlled model now unwraps the production tool-result envelope and emits bounded, deterministic answers from actual household, memory, and action results, preserving freshness/availability and non-causality. `WebChatShowsSimulatorToolResultsAndMatchesCommandsAfterConversationHistory` exercises the production Aspire Web/SDK composition and asserts visible final answers. |
| M2-04-R21 | Medium | Literal Simulator commands stopped matching when prior conversation history prefixed the current user request. | Strip only the known `LocalChatPrompt` current-request framing before matching documented demo commands. The protocol test supplies that framing, and the same-conversation Aspire scenario follows household and memory turns with a simulated light command. |
| M2-04-R22 | Medium | A process interruption after persisting an expired approval but before settling its action left the action AwaitingApproval; later owner-list reads skipped it because the approval was no longer Pending. | Added owner/version-checked `IApprovalService.SettleExpiredAsync` and invoke it for every durable Expired approval. It settles only AwaitingApproval actions, accepts concurrent terminal outcomes without overwriting them, and does not release an executing action's hold. `RecentApprovalReadSettlesPreviouslyExpiredApprovalWhoseActionIsStillPending` and the real-SQLite `ApprovalListRecoversExpiredDecisionWhoseActionSettlementWasInterrupted` cover recovery and no-execution behavior. |
| M2-04-R23 | Medium | A successful proposal response held during a conversation switch retained its request ID but lost the originating conversation ID; retrying the draft from another conversation therefore created a second proposal. | Retain the originating conversation ID together with the request ID and replay that pair. Editing the proposal clears both; a replay from another selected conversation does not switch away from that selection. The extended `DelayedMemoryProposalCannotResetFormOrSelectionAfterConversationSwitch` regression checks both request IDs/conversation IDs and the preserved selection. |
| M2-04-R24 | Medium | An expired invalidated memory proposal still attempted approval expiry settlement, which rejects its already-Rejected action and could fail the entire approvals list. | Pending expiry settlement now skips invalidated memory actions so they reach the owner-checked redacted summary. `DeleteRedactsDerivedCopiesAndOldCallsCannotRestoreThem` advances the controlled clock past expiry and verifies the invalidated item remains listable. |
| M2-04-R25 | High | Listing an actively executing action could release its shared turn budget while an equivalent sibling was waiting to enter execution. | Interrupted-action recovery releases the turn budget only after a non-Executing durable result; an active Executing result retains the shared budget. `RecentApprovalReadDoesNotReleaseTurnBudgetWhileActionIsStillExecuting` interleaves list recovery with active execution arbitration and verifies no turn release. |
| M2-04-R26 | Medium | Migration 008 accepted malformed legacy TurnId text as durable attribution, allowing unresolved actions to evade valid-turn recovery checks. | Backfill now requires a valid GUID shape and leaves both attribution columns null for malformed rows. `VersionSevenUpgradeBackfillsDurableActionOwnerAndTurnAttribution` verifies valid normalization, malformed null attribution, and fail-closed history reads. |
| M2-04-R27 | Medium | A duplicate proposal retry could dispatch successfully but leave its original turn nonterminal after interruption between dispatch and status persistence. | Proposal status settlement now runs for any persisted nonterminal turn, including exact duplicate retries, and preserves concurrent terminal outcomes. `DuplicateProposalRetrySettlesATurnAfterItsFirstSettlementWasInterrupted` injects a SQLite settlement failure and verifies retry completion without duplicating the approval. |
| M2-04-R28 | High | An automatic light write interrupted after submission had no approval record and was invisible to approval-list recovery. | The owner-scoped `/api/actions` read now performs bounded recovery of recent unapproved Executing actions through the observation-only dispatcher path; it never resubmits a write and leaves active Executing actions untouched. The real Aspire restart regression verifies a terminal outcome and no second write. |
| M2-04-R29 | Medium | Exact action arguments were hidden in a closed disclosure while approval controls were immediately available. | Redacted exact arguments now render inline before decision controls. Browser approval scenarios assert the proposal subject and value are visible without opening a disclosure, while owner/provenance fields remain absent. |
| M2-04-R30 | Medium | A delayed successful proposal response could clear editor changes made while the request was in flight. | Memory editor revision tracking prevents stale preflight submissions and preserves newer edits after a successful response. `DelayedMemoryProposalResponsePreservesAnEditedDraft` verifies the posted value and retained newer draft separately. |
| M2-04-R31 | Medium | Simulator memory search answers omitted fact identity and update time, losing citations and freshness information. | Bounded memory answers now include each fact ID, source, and `UpdatedAtUtc`; a non-empty search through production Web/SDK composition asserts those fields. |
| M2-04-R32 | Medium | An accepted write with an unconfirmed outcome was rendered identically to a verified success because the simulator answer discarded dispatch status. | Simulator action answers include the outer dispatch status separately from submission acceptance and causality. Aspire coverage compares verified and accepted-but-unconfirmed outcomes and asserts Unknown remains explicit. |

### R16–R21 refreshed validation

The refreshed credential-free checks ran on native macOS with .NET SDK
`10.0.401` and isolated Simulator/SQLite data. `origin/main` was fetched and
the branch is up to date with it. The earlier one-off simulator readiness 404
did not recur after rebuilding the Release simulator and rerunning the
affected browser scenarios.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed after the final simulator/test adjustment; Release build, formatting and analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 340/340 discovered tests, including expiry, interrupted execution, concurrent settlement, and missing-journal regressions. |
| `tools/validate.sh integration` | Passed; 461/461 discovered tests, including durable recovery, proposal provenance, and expiry/budget behavior. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.1% (872/907). Application 98.3% lines / 93.7% branches; Infrastructure 94.2% / 82.9%; Web 98.0% / 79.0%; all configured runtime and critical-module thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed after the final simulator adjustment; 14/14 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed after a Release rebuild; 35/35 Playwright scenarios. The deliberately incorrect browser assertion was discovered and failed as expected by the gate probe. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

The full browser gate includes the delayed-proposal selection/draft regression
and the simulator response streaming check. The previously failing isolated
scenarios also passed 2/2 after rebuilding. Independent exact-commit review of
the complete implementation delta, including the full M2-01/02/03 cumulative
ledgers, and GitHub thread replies/resolutions remain pending. No live Home
Assistant credentials, private household data, cloud inference, or physical
devices were used; Linux CI, live provider availability, physical-effect
causality, human observations, and assistive-technology certification remain
unexercised.

### R22–R23 validation

The follow-up regressions ran on native macOS with .NET SDK `10.0.401`,
isolated SQLite data, and the controlled browser simulator. The approval
settlement recovery coverage initially fell below its 95% critical-module
line threshold; additional `ToolDispatcherUnitTests` now exercise input
validation, stale ownership/version, idempotent replay, settlement failure,
concurrent terminal outcomes, and missing journal state. The refreshed
coverage gate passes without changing thresholds.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 343/343 discovered tests. |
| `tools/validate.sh integration` | Passed; 462/462 discovered tests, including real-SQLite interrupted expiry settlement recovery. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.3% (900/935); Application 98.3% lines / 93.6% branches; Infrastructure 94.2% / 82.9%; Web 98.0% / 79.0%; all configured critical-module thresholds, including approval settlement, met. |
| `tools/validate.sh aspire-e2e` | Passed; 14/14 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 35/35 Playwright scenarios, including delayed proposal retry after conversation switch. The deliberately failing browser probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

At this historical checkpoint, R22–R23 were uncommitted. The R22–R32
validation and review status is recorded below. No live Home Assistant
credentials, private household data, cloud inference, or physical-device
writes were used. Linux CI, live provider availability, physical-effect
causality, human observations, and assistive-technology certification remain
unexercised.

### R22–R32 final validation

The final R22–R32 source was validated on native macOS with .NET SDK
`10.0.401`, isolated Simulator/SQLite data, and no live Home Assistant
credentials, private household data, cloud inference, or physical-device
writes.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed after the final browser assertion; Release build, formatting and analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 352/352 discovered tests. |
| `tools/validate.sh integration` | Passed; 463/463 discovered tests. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.9% (935/965). Application 98.5% lines / 94.0% branches; Infrastructure 94.3% / 82.9%; ServiceDefaults 100% / 100%; Web 98.0% / 79.0%; all critical-module and unit-only thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 14/14 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 36/36 Playwright scenarios and the deliberately failing browser gate probe behaved as expected. This full run preceded the final resubmit-request-ID assertion. |
| `dotnet test tests/PersonalAgent.E2ETests/PersonalAgent.E2ETests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~DelayedMemoryProposalResponsePreservesAnEditedDraft'` | Passed on the final source; 1/1 browser regression, including the resubmission request-ID assertion. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

The final source has not yet received an independent exact-commit review, and
the corresponding implementation changes have not yet been committed or pushed
to PR #48. GitHub checks therefore have not run against this source revision.
Linux CI, live provider availability, physical-effect causality, human
observations, and assistive-technology certification remain unexercised.
These limits are not represented as passing evidence.

### Exact-commit review follow-up: R33

Independent review of `9c529ed41abdf9fd0e47b8875bd416976a9c640d` identified
that recent terminal action/approval records could displace older unresolved
records from their bounded owner queries, preventing recovery and expiry
settlement. The persistence reads now prioritize unresolved action states and
pending approvals or approvals linked to AwaitingApproval/Executing actions
before filling the remaining page with recent history. Regressions seed 101
newer terminal records and verify both an older orphaned execution is recovered
without resubmission and an older pending approval is expired by the owner
workflow.

The R33 follow-up validation ran on native macOS with .NET SDK `10.0.401`,
isolated Simulator/SQLite data, and no live Home Assistant credentials,
private household data, cloud inference, or physical-device writes.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting and analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 352/352 discovered tests. |
| `tools/validate.sh integration` | Passed; 465/465 discovered tests, including both 101-record overflow regressions. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.9% (943/973). Application 98.7% lines / 94.3% branches; Infrastructure 94.2% / 82.9%; ServiceDefaults 100% / 100%; Web 98.0% / 79.0%; all critical-module and unit-only thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 14/14 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed on full rerun; 36/36 Playwright scenarios and the deliberately failing browser gate probe behaved as expected. An earlier run had one layout-test failure at 768px in Dark mode; the focused rerun passed 1/1 and the full rerun passed. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

Independent exact-commit review of the R33 follow-up and current-head GitHub
checks remain pending after commit and push. Linux CI, live provider
availability, physical-effect causality, human observations, and
assistive-technology certification remain unexercised.

### Exact-commit review follow-up: R34–R43

The review of `5b460466afa027fc43111dfc80e7d8f8d0afdca3` raised five inline
recovery findings and four summary-only "Previously missed" findings. The
subsequent R33 follow-up also found that terminal approval decisions still
needed priority over newer Pending requests. The fixes and regressions below
are in progress; no full-gate or exact-commit review evidence is claimed yet.

| Reference | Severity | Finding | Follow-up disposition |
| --- | --- | --- | --- |
| M2-04-R34 | Medium | An interrupted Rejected approval decision could leave its action AwaitingApproval and retain its dispatcher hold. | Added owner/version-checked idempotent rejected-action settlement and repair during owner approval reads. `ApprovalListRepairsRejectedDecisionWhoseActionSettlementWasInterrupted` covers durable recovery. |
| M2-04-R35 | Medium | Concurrent owner reads recovering the same interrupted action could fail the whole list when the competing update won. | Recovery rereads the durable action after a concurrency conflict and accepts only an already-terminal outcome; disappeared or nonterminal actions remain errors. `ConcurrentInterruptedActionRecoveryAcceptsTheFirstTerminalOutcome` controls overlapping reads. |
| M2-04-R36 | High | A removed registration or allowlist capability could leave an interrupted household action Executing. | Recovery records Unknown when the registration is absent and relies on the existing observation-only dispatcher path when policy no longer permits observation; neither path resends the write or overwrites active execution. `InterruptedActionBecomesUnknownWhenItsCapabilityWasRemoved`, `UnavailableRecoveryDoesNotOverwriteAnActiveExecutionOrTerminalAction`, and `UnavailableRecoveryAcceptsConcurrentTerminalStateAndRejectsNonterminalConflict` cover removed capabilities and concurrent action states. |
| M2-04-R37 | High | Releasing a turn after one action recovered could discard a shared failure budget while sibling dispatches were still active or waiting. | Per-turn dispatch leases retain shared budgets until all same-turn dispatches exit, in addition to unresolved approval holds. `TurnReleaseDuringSiblingHistoryReadPreservesUncertaintyBudget` interleaves turn release with sibling history seeding. |
| M2-04-R38 | Medium | Malformed canonical data in one Executing action could break the complete owner-scoped action-list response. | Recovery now reports a stable per-action error while leaving malformed actions unexecuted and returning the rest of the owner list. The API exposes the separate `recoveryError`; `MalformedExecutingActionReportsRecoveryIssueWithoutBreakingOwnerList` and `AspireSimulatorTests.MalformedExecutingActionDoesNotBreakOwnerActionsApi` cover a durably attributed malformed row. |
| M2-04-R39 | Medium | An older interrupted Expired decision could be hidden behind newer Pending approvals in the bounded owner query. | Approval query ordering now prioritizes resolved decisions linked to unresolved actions, then actionable Pending records, ahead of non-actionable history. `ApprovalListPrioritizesOlderInterruptedExpiryBeforeNewerPendingApprovals` covers the terminal-decision overflow case. |
| M2-04-R40 | Medium | One hundred newer Unknown actions could hide an older Executing action from the bounded recovery query. | Owner action queries prioritize Prepared/AwaitingApproval/Executing before Unknown and terminal history. `UnapprovedExecutingActionRecoveryPrioritizesOlderUnresolvedActions` now seeds 101 newer Unknown outcomes. |
| M2-04-R41 | Medium | Newer invalidated Pending memory approvals could displace an older actionable approval from the bounded approval query. | Approval query ordering distinguishes non-invalidated AwaitingApproval actions from invalidated Pending records. `ApprovalListPrioritizesActionableApprovalOverNewerInvalidatedPendingRecords` seeds 101 newer invalidated requests. |
| M2-04-R42 | Medium | The Aspire restart test inferred simulator state continuity from URL reuse, although a restarted simulator process begins with zero writes and a reset lamp. | The restart scenario now forces observation failure after the graph restart and always asserts zero simulator writes plus Unknown, independent of whether the endpoint URL was reused. The targeted restart E2E passed. |
| M2-04-R43 | Medium | Simulator final-answer generation could reuse an earlier turn's tool result when the current turn made no tool call. | Tool-result lookup is restricted to tool messages following the latest user message. The multi-turn `SimulatorDemoMapsDocumentedCommandsToBoundedRegisteredTools` regression verifies an unrelated follow-up receives the normal controlled response instead of stale temperature data. |

The focused lifecycle integration regressions passed 7/7, and the focused
bounded-query cases passed 6/6 (including two repeated capability-removal
cases); focused unit tests passed 32/32; focused SDK contract tests passed
56/56; the simulator protocol and restart E2E scenarios passed 2/2; and the
malformed-action production API E2E passed 1/1. The active-sibling budget test
also reproduces recovery of action A while action B is executing and action C
waits on the same-turn budget; it passed 1/1. GitHub's browser gate for
pre-follow-up commit `5b460466afa027fc43111dfc80e7d8f8d0afdca3` failed 35/36
because the delayed-proposal retry test sampled the editor immediately after
its HTTP response, before client-side completion cleared the unchanged draft.
The test now waits for the observable editor reset before asserting; the
focused regression passed 1/1, and the refreshed full browser gate passed.

The final credential-free validation ran on native macOS with .NET SDK
`10.0.401` and isolated Simulator/SQLite data:

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 352/352 discovered tests. |
| `tools/validate.sh integration` | Passed; 476/476 discovered tests. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.4% (1030/1068); Application 98.4% lines / 93.6% branches; all configured unit-only, runtime, and critical-module thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 15/15 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 36/36 Playwright scenarios, and the deliberately failing browser probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

The follow-up remains uncommitted at this checkpoint. Independent exact-commit
review, replies/resolutions for all outstanding review threads, and GitHub
checks on the pushed revision remain pending. Linux CI, live Home Assistant
and provider availability, physical-effect causality, human observations,
and assistive-technology certification remain unexercised. No live Home
Assistant credentials, private household data, cloud inference, or
physical-device writes were used.

### Independent review follow-up: R44–R48

Independent review of `5ef9f845fa86e6fb45296b885f777f6710dc846c` found five
additional recovery/lifecycle issues. The R44–R48 fixes and focused
regressions are in the current follow-up; full validation and a new
exact-commit review remain pending.

| Reference | Severity | Finding | Follow-up disposition |
| --- | --- | --- | --- |
| M2-04-R44 | Medium | A valid but incomplete action envelope could deserialize with a missing `TurnId`; `Guid.Parse` then threw `ArgumentNullException`, bypassing per-action recovery reporting and breaking the owner actions API. | `ReadEnvelope` now validates required envelope fields, GUID and risk enum before recovery. `IncompleteExecutingActionEnvelopeReportsRecoveryIssueWithoutBreakingOwnerList` verifies an attributed malformed row yields `invalid_action_journal` while remaining actions stay readable and no write is retried. |
| M2-04-R45 | Medium | `ReleaseTurn` could run while a dispatch was still preparing, before its failure budget existed; the late-created budget was not marked released and could remain in memory after the final dispatch lease ended. | Track released turns while active dispatch leases remain and mark any subsequently-created budget released under the same lifecycle lock. `TurnReleaseDuringPreparationCleansBudgetCreatedByActiveDispatch` pauses preparation across turn release and asserts the budget is removed after dispatch completion. |
| M2-04-R46 | Medium | An expired Pending approval whose action had already been Rejected was sent through approval expiry settlement, which requires `AwaitingApproval`; the concurrency catch then rethrew while the approval remained Pending, making the whole owner list unavailable. | Expiry settlement is now limited to actions still `AwaitingApproval`. `ChangedProposalWhileApprovalIsPendingInvalidatesTheAction` now reads the owner list after the approval expires and verifies the terminal action remains visible without breaking the list. |
| M2-04-R47 | Medium | Household recovery compared the pre-dispatch immutable `Executing` snapshot after dispatch, so terminal recovery could skip `ReleaseTurn` and retain a restarted dispatcher’s failure budget. | Re-read the durable action after dispatch before releasing the turn. `ApprovalListRecoversInterruptedApprovedHomeActionByObservationOnly` now verifies the terminal recovery leaves no failure budget. |
| M2-04-R48 | Medium | Resuming an approved household or memory action after restart allocated a new turn budget without releasing it after terminal completion; cleanup must still preserve active dispatches and sibling approval holds. | Release the household turn after terminal dispatch and release memory-completion turns in `MemoryProposalService`; existing dispatch-lease and pending-approval holds remain authoritative. Restart-resume tests cover both action types, and sibling-approval regressions verify equivalent-write uncertainty remains protected. |
| M2-04-R49 | High | Recovery did not verify that a valid envelope's turn/call identity derived the journaled action ID, that its action type matched the journal, or that its owner/turn matched durable attribution. A tampered envelope could dispatch under a fresh action identity and resubmit a physical write. | Recovery now checks the derived action ID and action type, then requires the action to appear in the journal's owner/turn-attributed history before memory settlement or dispatcher recovery. The `MismatchedValidExecutingActionEnvelopeReportsIssueWithoutResending` theory covers changed turn, call, and action type; `DurableOwnerTurnAttributionMismatchFailsRecoveryClosed` covers inconsistent stored attribution. Focused tests passed 4/4 integration and 16/16 unit tests; the complete validation now passes with R50. |
| M2-04-R50 | Medium | Reading an expired pending approval whose action was already Rejected left its approval hold in the dispatcher's turn budget. If the turn was released first, `approvalBudgets` and `failureBudgets` retained the budget until restart. | `ReadRecentAsync` now releases approval holds only for definitive terminal outcomes (`Succeeded`, `Failed`, `Rejected`, or `Expired`), preserving uncertainty holds such as `Unknown` and any sibling approvals. `ChangedProposalWhileApprovalIsPendingInvalidatesTheAction` verifies the released turn's failure budget is cleaned up; focused regression passed 1/1 and the full integration suite passed 482/482. |

Before the final run, the R44–R45 focused integration regressions passed 2/2
and the R44 malformed-envelope unit cases passed 5/5. The refreshed full
validation below covers the combined R44–R48 changes and the approval
coverage additions.

### R44–R48 final validation

The current source was validated on native macOS with .NET SDK `10.0.401`,
isolated Simulator/SQLite data, and no live Home Assistant credentials,
private household data, cloud inference, or physical-device writes.
`origin/main` was fetched; the branch is 12 commits ahead and 0 behind.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 361/361 discovered tests. |
| `tools/validate.sh integration` | Passed; 478/478 discovered tests. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.6% (1068/1106). Application 98.4% lines / 93.5% branches; Infrastructure 94.2% / 82.9%; ServiceDefaults 100% / 100%; Web 97.8% / 78.0%; all unit-only, runtime, and critical-module thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 15/15 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 36/36 Playwright scenarios. The deliberately failing browser probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

The coverage gate initially reported 88.6% branch coverage for the exact
M2-01 approval lifecycle module. Added unit scenarios for already-resolved
approval records, missing durable actions during resolution/settlement, and
idempotent terminal settlement; coverage now passes without changing any
threshold. The complete R44–R48 behavior has not yet received an independent
exact-commit review, and GitHub checks have not run against the follow-up
revision. The existing R46–R48 review threads still need replies and
resolution after the validated change is pushed. Linux CI, live Home
Assistant/provider availability, physical-effect causality, human
observations, and assistive-technology certification remain unexercised.

### R49–R50 validation follow-up

R49–R50 were validated on native macOS with .NET SDK `10.0.401` and isolated
Simulator/SQLite data, without live Home Assistant credentials, private
household data, cloud inference, or physical-device writes. The two memory
browser scenarios that intermittently timed out before reaching the Memory
navigation button now use the same direct sign-in flow as the passing
browser scenarios; the affected three memory browser tests passed together.

| Command | Result |
| --- | --- |
| `tools/validate.sh build` | Passed; Release build, formatting/analyzers, zero warnings/errors. |
| `tools/validate.sh architecture` | Passed; 6/6 discovered tests. |
| `tools/validate.sh unit` | Passed; 361/361 discovered tests. |
| `tools/validate.sh integration` | Passed; 482/482 discovered tests. |
| `tools/validate.sh sdk-contracts` | Passed; actual pinned-runtime probes and 57/57 contract tests. |
| `tools/validate.sh coverage` | Passed; changed executable lines 96.6% (1076/1114); Application 98.3% lines / 93.8% branches; all configured thresholds met. |
| `tools/validate.sh aspire-e2e` | Passed; 15/15 out-of-process scenarios. |
| `tools/validate.sh browser-e2e` | Passed; 36/36 Playwright scenarios and the deliberately failing browser probe was discovered and failed as expected. |
| `tools/validate.sh docs` | Passed; all internal Markdown links resolve. |
| `git diff --check` | Passed. |

An earlier local integration invocation failed because `Web.deps.json` was
temporarily unavailable to the test host; rerunning the repository's
`tools/validate.sh integration` completed successfully, and the subsequent
full gate run also passed integration. An earlier browser run failed two
memory tests at the pre-navigation sign-in boundary; after normalizing those
tests to the direct sign-in flow, all browser scenarios passed. GitHub CI on
the previous committed revision `ae650f4` still records its browser failure;
CI on the new pushed revision is required. An exact-commit independent
review, review-thread resolution, and current GitHub checks remain pending.
Linux CI, live Home Assistant/provider availability, physical-effect
causality, human observations, and assistive-technology certification remain
unexercised.
