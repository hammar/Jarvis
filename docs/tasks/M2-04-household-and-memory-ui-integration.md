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
  `ToolDispatcherTests.ChangedProposalWhileApprovalIsPendingInvalidatesTheAction`,
  `ToolDispatcherTests.AlreadyApprovedUnclaimedHomeActionCanResumeAfterDispatcherRestart`,
  `BrowserSmokeTests.ApprovedUnclaimedActionOffersAnExplicitResumeControl`,
  `ToolDispatcherTests.ExpiredApprovedUnclaimedActionCanBeFinalizedWithoutExecution`,
  `BrowserSmokeTests.ExpiredApprovedActionOffersNoExecutionFinalization`, and
  `ConfirmedMemoryTests.RejectionExpiryAndForgedSourceCannotCommit`.
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
  and `ConfirmedMemoryTests.ReceiptRecoversInterruptedSettlementWithoutRepeatingTheMutation`.
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
| M2-04-R15 | Medium | The typed owner-proposal path dispatched directly without releasing its dispatcher turn state, retaining per-turn failure-budget entries for process lifetime. | `MemoryProposalService.ProposeAsync` now calls `ReleaseTurn` in `finally` after dispatch and turn-status settlement, including dispatch exceptions. `OwnerEnteredProposalReleasesItsDispatcherTurnAfterSuccessOrFailure` verifies release both after an approval-required result and a thrown dispatch. Post-fix native validation passed; the independent exact-commit review below remains required. |

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
cumulative ledgers is still required. No live Home Assistant credentials,
private household data, cloud inference, or physical devices were used. Linux
CI, live HA/provider availability, physical-effect causality, human
observations, and assistive-technology certification remain unexercised.
