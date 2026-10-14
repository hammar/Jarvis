# M2-01: Tool dispatcher, exact approvals, and action journal

## Objective and acceptance

Implement the application-owned registered-tool dispatcher and exact approval
flow for issue #5 / M2-01. Model-proposed calls remain untrusted. Host policy
must validate, authorize, journal before execution, and report uncertain
physical outcomes without blind retries. SQLite remains authoritative.

**Specification:** §§5, 7, 13, 14, 15, and 20. **Dependencies:** completed M1
(T02/T03/T04 and M1-01–M1-06). **Issue:** #5.

Acceptance scenarios include:

- An unregistered tool or disallowed entity is denied before external execution.
- An ambiguous entity alias requires clarification and does not select a target.
- Re-dispatch with the same action identity returns the recorded outcome and
  never repeats execution; a distinct later request with identical arguments
  remains a distinct action. The current T03 contract records status only,
  not the original result JSON or reason; full outcome replay remains an
  unresolved acceptance gap under M2-01-R6 below.
- A process restart that finds a persisted `Executing` action performs bounded
  observation only, records the observed outcome or `Unknown`, and never
  replays the execution handler; a concurrent duplicate on the active
  dispatcher remains an in-progress action.
- A service call that times out after send is journaled `Unknown`, is reconciled
  only through bounded observation, and is never blindly replayed.
- Expired, replayed, wrong-owner, stale-version, or changed-action approvals
  cannot execute.
- Cancellation after a side effect was sent does not claim rollback or
  verified failure.
- Required typed arguments cannot be omitted, and policy/approval expiry are
  rechecked after the durable execution claim before sending.
- Equivalent failed reads and known failed writes stop after two attempts in
  one turn. An uncertain write blocks any further equivalent send in the same
  turn, including concurrent attempts, fresh call IDs, and normalized aliases.
- A stale approval expiry transition cannot misreport an action that another
  dispatch already claimed or completed.
- Concurrent approval decisions and matching action settlement return the
  recorded winner; unresolved conflicts and other persistence errors surface
  without falsely reporting success or sending.
- A missing-approval snapshot can return an existing exact approval after another
  dispatch starts/completes execution, without renewing it or relaxing ownership.
- Observation-only recovery shares equivalent-proposal accounting: a durable
  known outcome clears only its own write uncertainty, verified success removes
  its provisional failure, and confirmed failure consumes one distinct slot.
- Nonexpired Pending snapshots cannot mask a winning rejection, approval, or
  active/completed execution, or solicit another decision after resolution.
- Definitive non-application is known failure even after uncertain delivery;
  only the remaining bounded attempt is permitted. A mere unchanged snapshot
  with possible late application must remain Unknown.
- Recovery rechecks current permission after awaiting its proposal gate and
  before observation; revoked/Unsupported permission records Unknown without
  observing, while an allowed policy-version change can still be observed.
- Fresh-dispatcher recovery seeds every unresolved write's original persisted
  risk before observation. Inconclusive/error/unjournaled outcomes remain
  blocked; reconciling one orphan cannot discard another. Original read-only
  actions are not overblocked, and unreadable risk metadata fails closed.
- Normal verification rechecks permission after delivery; recovery refreshes
  journal state under the proposal gate before observing a still-Executing
  action. Winning outcomes are returned without another observation.

## Scope

- Add host-owned tool registration and dispatch policy with typed JSON
  validation, entity/operation allowlists, risk classification, canonical
  requests, execution, verification, and cancellation-aware outcomes.
- Add approval creation/resolution bound to one authenticated owner and one
  journaled action, with five-minute default expiry, compare-and-swap version,
  single use, and execution-time policy/freshness checks.
- Use the existing `IActionJournalStore` and `IApprovalStore` persistence
  contracts. Keep exact entity, policy/config version, and request provenance
  in the canonical persisted action request envelope/hash; do not change T03
  schema or storage contracts.
- The Copilot adapter carries the authenticated owner and the current request
  from the host-selected context to the dispatcher. The original request text
  is used only in memory for a trusted handler's exact-intent check; only its
  SHA-256 hash is retained in the canonical action envelope. Low-risk writes
  without handler-verified explicit intent are elevated to approval-required.
- Replace production's rejecting dispatcher registration with the real
  dispatcher. Do not register any functional production tool until its owning
  feature task supplies a real implementation.

## Exclusions

- Home Assistant REST client or home tools (M2-02).
- Memory retrieval/mutations (M2-03).
- Approval UI/API and cumulative household demo (M2-04).
- Reminder/scheduler tools (M3-02).
- Arbitrary HTTP, shell, filesystem, purchase, outbound-message, or
  unsupported physical capabilities.
- New SQLite schema migration, data retention changes, or automatic retry of
  an uncertain side effect.

## Owned files

- `src/PersonalAgent.Application/Contracts.cs` and application `Tools/` and
  `Approvals/` components.
- `src/PersonalAgent.Infrastructure/` only for dispatch/approval composition
  and existing T03 store integration; no SQLite schema changes.
- `src/PersonalAgent.Web/Program.cs` for production registrations.
- `tests/PersonalAgent.UnitTests/`,
  `tests/PersonalAgent.IntegrationTests/`, and coverage validation ownership
  if new critical modules need exact-path gates.
- `docs/architecture.md`, `docs/testing.md`, applicable project README, and
  this task brief.

## Assumptions and decisions

- Existing frozen Application storage contracts and T03 database schema are
  retained. The canonical action request is an immutable envelope containing
  the tool/action type, canonical validated arguments, resolved entity,
  policy/config version, turn/tool-call provenance, and operation. Its stable
  hash binds the approval to the complete proposal.
- Action identity is stable for one originating turn/tool-call ID. Changed
  input under that identity is rejected; similar arguments from a later call
  are not deduplicated.
- Approval resolution records a trusted owner decision only. An approved
  action must pass current allowlist/policy/freshness checks before execution.
  No endpoint accepts model-generated approval or identity.
- Tests use controlled registered handlers, clocks, and isolated real SQLite
  files. No live provider, secret, cloud, or household device is required.
- If the current composition cannot expose approval decisions without an
  approval UI, the behavior is verified through the application service and
  controlled dispatcher integration; no placeholder UI is included.

## Required tests

- Reject unregistered tools, invalid JSON/schema, unsupported operations,
  disallowed entities, duplicate aliases, and all Unsupported risk actions
  without invoking a handler.
- Verify safe registered reads and permitted explicit low-risk writes execute,
  verify configured approval-required operations wait, and verify approved
  actions execute only once after policy/freshness recheck.
- Verify approval owner, expiry boundary, optimistic version, replay/single
  use, action binding, and changed canonical request.
- Verify deterministic same-action identity deduplicates execution while a
  different call ID with identical arguments is not deduplicated.
- Verify a fresh dispatcher reconciles a persisted `Executing` action using
  bounded observation without invoking the execution handler a second time,
  including against a real SQLite journal.
- Verify action journal transitions, failure outcomes, cancellation before
  send, cancellation after send, timeout-after-send `Unknown`, bounded
  observation/reconciliation, and no automatic replay.
- Meet the project critical-module 95% line / 90% branch and changed-executable
  line-coverage gates with real behavioral evidence.

## Documentation and completion criteria

- Update `docs/architecture.md` with the authorization/journal boundary and
  honest approval/unknown-outcome lifecycle.
- Update `docs/testing.md` with scenario-to-test mapping and evidence limits.
- Document new public Application APIs/XML contracts and any production
  project composition changes.
- Run the relevant `tools/validate.sh` checks and all applicable required
  gates. Record actual commands/results, reviewed revision, unexercised
  behavior, and residual risks; do not represent controlled handlers as live
  Home Assistant or real approval UI validation.

## Independent review follow-up

PR [#45](https://github.com/hammar/Jarvis/pull/45) contains the original
implementation at `3840d03508882d4daa623967b5641de5b89cab5c`. The initial
independent working-tree review found R1; GitHub Copilot's independent review
of `3840d03` found R2-R6. The table is cumulative; no findings are dropped.
Commit `aefba04` addresses R2-R5. The next review against `c8d62dd` also raised
R7/R8, both labelled "Previously missed"; both are retained below. Commit
`c5f3d635118f732e781f1e3994e617ffc0cbfcc3` fixes R7/R8.

| Reference | Severity | Finding | Disposition and evidence |
| --- | --- | --- | --- |
| M2-01-R1 | Medium | Restarted actions could remain `Executing` without observation. | Fixed in `3840d03`: bounded observation-only recovery, active-execution arbitration, and unit/SQLite regressions. |
| M2-01-R2 | High | Missing non-optional constructor arguments could synthesize defaults and alter a proposed action. | Fixed in the review follow-up: enforce required constructor parameters. `MissingRequiredArgumentsAreRejectedBeforePreparationOrJournalWrites` rejects omitted target/operation arguments and an empty object before preparation, journaling, or execution. |
| M2-01-R3 | High | Approval expiry or policy could change while awaiting the durable execution claim. | Fixed in the review follow-up: recheck expiry and policy immediately after the claim. `AuthorizationIsRecheckedAfterDurableClaimBeforeSending` and `AutomaticActionCannotExecuteWhenPolicyChangesWhileClaimingIt` assert terminal rejection/expiry and zero sends. |
| M2-01-R4 | Medium | Fresh call IDs bypassed the two-equivalent-failure limit. | Fixed in the review follow-up: canonical turn-scoped failure accounting serializes equivalent attempts; `TwoEquivalentFailuresStopFreshCallIdsButNotLaterTurns` and `ConcurrentEquivalentFailuresCannotRacePastTheTwoAttemptLimit` cover errors, aliases/property order, fresh call IDs, and concurrency. SDK contracts assert turn accounting cleanup. |
| M2-01-R5 | Medium | Changed policy prevented recovery of an orphaned `Executing` action. | Fixed in the review follow-up: permit observation-only recovery when current policy allows; otherwise journal `Unknown` without observing or sending. `RestartedExecutionHandlesChangedPolicyWithoutReplayingIt` and `PolicyChangeDoesNotMistakeActiveExecutionForRestartRecovery` cover both restart permissions and active-call arbitration against real SQLite. |
| M2-01-R6 | Medium | Duplicate/restarted dispatch loses the original result JSON and detailed reason. | Deferred outside the authorized unchanged-T03 persistence scope, not fixed or owner-accepted. The journal persists intent/status/version only; API/docs explicitly disclose status-only replay. The review thread is handled as a scope deferral, not as proof that the acceptance gap is fixed. |
| M2-01-R7 (Previously missed) | Medium | A stale approval read could falsely report expiry after another dispatch began or completed execution. | Fixed in `c5f3d63`: report `Expired` only when the transition actually produced that status; otherwise return the recorded action outcome. `StaleApprovalReadCannotReportExpiryAfterExecutionBegins` exercises both `Executing` and `Succeeded` across the expiry boundary. |
| M2-01-R8 (Previously missed) | High | A fresh equivalent call ID could resend after an uncertain write. | Fixed in `c5f3d63`: serialize equivalent proposals and keep their write gate closed from the send boundary until a known outcome is durably recorded. `UncertainWritesBlockEquivalentFreshIdsIncludingQueuedAttempts` and `EquivalentUncertainWritesCannotResendWithFreshIds` cover sequential/concurrent fresh IDs, unknown results, cancellation, handler/verification exceptions, and terminal-journal failure. Known failed writes retain two attempts; later owner turns remain independent. |
| M2-01-R9 (review label R7; Previously missed) | Medium | A delayed duplicate returning a recorded failure could consume the second failure slot without executing. | Fixed in `1669296`: deduplicate failed-attempt accounting by action ID on both returned-outcome and exception paths. `DelayedDuplicateDoesNotConsumeASecondFailureSlot` pauses a duplicate's `Prepared` snapshot until the first attempt fails; the next fresh call still executes, and a third distinct attempt is rejected only after two failures. |
| M2-01-R10 (Previously missed) | Medium | Action settlement could throw after the approval decision and matching action status were both successfully committed by concurrent callers. | Fixed in the follow-up after `1669296`: accept an action-update CAS conflict only when rereading proves the same terminal `Rejected`/`Expired` status. `ConcurrentActionSettlementReturnsTheCommittedApprovalDecision` covers both outcomes; `ApprovalSettlementPreservesOtherConflictsAndPersistenceErrors` proves incompatible states and other failures still surface. |
| M2-01-R11 (review label R9; Previously missed) | Medium | A stale expired `Pending` snapshot could throw after another expiry or owner decision won resolution. | Fixed in the follow-up after `1669296`: reread the winning approval and current action after a resolution conflict; retain the execution-time expiry check and propagate conflicts with no decision. `StalePendingApprovalReadUsesTheWinningDecisionWithoutSending` covers expiry/approval/rejection; the stale-read execution test also covers in-progress/completed winning actions. |
| M2-01-R12 | Medium | A delayed duplicate that read no approval could fail creation after another dispatch obtained approval and began/completed execution. | Fixed in the follow-up after `08d061c`: validate and return an existing owner/action binding before enforcing the action status for new creation. `MissingApprovalSnapshotReturnsTheExistingDecisionAfterExecutionStarts` covers active/completed execution and retains wrong-owner rejection. |
| M2-01-R13 | Medium | Observation-only recovery could persist a known outcome without clearing its proposal's write uncertainty or updating failure accounting. | Fixed in the follow-up after `08d061c`: share the execution proposal gate, track the uncertain action ID, and reconcile accounting only after a durable outcome. `KnownRecoveryUpdatesTheSameProposalBudgetBeforeFreshCalls` covers sequential/queued success/failure; negative controls retain uncertainty on inconclusive observation, errors, failed journaling, or another action's recovery. Read-failure and queued-cancellation tests cover accounting and cleanup. |
| M2-01-R14 | Medium | A nonexpired stale Pending snapshot could ask for another approval after rejection, approval, or active/completed execution had already won. | Fixed in the follow-up after `461e81c`: refresh both approval and action before reporting Pending; retain owner binding, expiry, policy, and execution arbitration. `NonexpiredPendingSnapshotReturnsTheWinningDecisionWithoutRequestingAnotherApproval` covers four no-clock-advance winners; the expired-resolution regression now pauses the action snapshot immediately before resolution to preserve its actual CAS-conflict coverage. |
| M2-01-R15 | Medium | Definitive NotApplied verification after Unknown execution was still classified Unknown, blocking the safe remaining attempt. | Fixed in the follow-up after `8dafbab`: use the verifier's existing definitive-non-application contract consistently with recovery. Unit/SQLite regressions assert Failed persistence, duplicate replay, a permitted second attempt, and a blocked third. XML clarifies that an unchanged snapshot with possible late application is insufficient. |
| M2-01-R16 | Medium | Recovery used authorization sampled before awaiting the equivalent-proposal gate, allowing observation after permission was revoked. | Fixed in the follow-up after `8dafbab`: evaluate current policy after acquiring the gate, immediately before observation. `QueuedRecoveryRechecksCurrentAuthorizationBeforeObserving` queues behind an equivalent call and verifies revoked/Unsupported permissions cause Unknown without observing, while an allowed version change still permits observation. |
| M2-01-R17 | High | A fresh dispatcher did not seed an orphaned write's uncertainty, permitting an equivalent fresh send after inconclusive/error/unjournaled recovery. | Fixed in the follow-up after `5d60757`: seed uncertainty before observing from strictly parsed original persisted risk; track all unresolved action IDs, not one nullable owner. Sequential/queued unit/SQLite regressions assert no extra send; additional controls cover multiple orphans, changed current risk, read-only failure limits and malformed/missing/null metadata. Known durable outcomes remove only their own uncertainty. |
| M2-01-R18 (Previously missed) | Medium | Permission revoked or Unsupported during execution was not checked before normal external verification. | Fixed in the follow-up after `a1fd7de`: re-evaluate before verification; record Unknown without observing and retain equivalent-write uncertainty. Completed/Unknown paused-execution unit/SQLite regressions assert zero observations; still-allowed version changes remain observable. |
| M2-01-R19 (Previously missed) | Medium | A stale Executing snapshot could observe after another dispatch completed, then fail its terminal CAS rather than return the winner. | Fixed in the follow-up after `a1fd7de`: reread under the proposal gate and return non-Executing state with budget reconciliation before observing. Unit/SQLite races cover Succeeded/Failed/Unknown, revoked/Unsupported policy and cancelled observation; pre-send Rejected/Expired controls preserve the winner without inventing uncertainty. |

R9 uniquely identifies review comment `4273196189`, which reused the R7 label
already assigned to the distinct approval-expiry race. Its review was against
`c8d62dd`; the comment remains tracked separately so neither finding is erased.
The complete top-level review contained R6-R9 and no additional summary-only
findings; R6's deferral and R7/R8 fixes above remain unchanged.
R10 maps to comment `4273628171`; R11 maps to `4273628253`, whose review reused
R9's label. The subsequent full review against `b54e558` contained these two
additional findings and acknowledged the earlier four conversations resolved.
It did not add summary-only findings or accept R6's deferred limitation.
R12 maps to comment `4273719016`; R13 maps to `4273719101`. The subsequent full
review against `1669296` raised these two findings and repeated R10/R11, already
fixed in `08d061c`; its summary also retained R6's need for an owner decision.
No additional summary-only finding was omitted.
R14 maps to comment `4273847535` in the subsequent full review against
`08d061c`. That review repeated R12/R13, fixed by `461e81c`, and added no other
summary-only findings. Prior dispositions, including R6, remain unchanged.
R15 maps to comment `4274033158`; R16 maps to `4274033257` in the subsequent
full review against `461e81c`. It repeated R14, already fixed by `8dafbab`, and
contained no additional summary-only findings. R6 remains deferred/unaccepted.
R17 maps to comment `4274173142` in the subsequent full review against
`8dafbab`. It repeated R15/R16, fixed by `5d60757`, and its summary retained
R6's unaccepted gap; no additional summary-only finding was omitted.
R18 maps to `4274599641`; R19 maps to `4274599736`. The full subsequent
review against `5d60757` labelled both Previously missed and repeated R17,
already fixed by `4abe280`. It added no further summary-only findings; none
of the earlier dispositions or R6's unaccepted gap is erased.

For R6, the concrete alternatives are explicit owner acceptance of status-only
replay for M2-01 or authorization to extend durable terminal-result persistence
with duplicate/restart payload-equality tests and a reviewed privacy/retention
contract. The owner was unavailable when asked during the check-in. Resolving
the R6 discussion records the deferral without authorizing storage work,
claiming full response replay, or accepting the limitation on the owner's
behalf. The unresolved product acceptance impact remains explicit: passing
automation or resolving GitHub conversations does not dispose of this gap
under the repository's owner-acceptance procedure.

### Exact-commit independent review

An independent code-review agent inspected
`aefba04483d7d710411a8a93b4df1818367d0abe` against merge base
`0f4f481b31760a8a941e486921f5194c4203f205`. It examined the specification,
accepted ADRs, changed implementation/tests/docs, and complete R1-R6 ledger.
It found no new significant issues, confirmed the R1-R5 fixes, and independently
confirmed R6 remains an acceptance gap at `ToolDispatcher.Recorded`. This
read-only source review did not rerun tests or exercise live devices/UI.
The earlier explicit no-schema/no-storage-contract-change decision remains
binding; a second attempt to obtain a concrete R6 decision also found the
owner unavailable. An unattended instruction to continue autonomously is not
treated as permission to override that specific scope constraint.

An independent code-review agent subsequently reviewed the bounded R7/R8
fix at exact commit `c5f3d635118f732e781f1e3994e617ffc0cbfcc3` against parent
`c8d62dd97e7fee5127e48f094944ff34a1bf40b3`, including the complete R1-R8
ledger, changed dispatcher/tests/docs, and spec §§7/13/14. It found no new
significant issues and supported the R7/R8 fixes. It retained R6 as a deferred,
unaccepted acceptance gap. This was source review, not a claim to have rerun
tests or exercised live/cross-process device behavior.

The bounded independent review of exact commit
`08d061c8a5780e01e8094f55c2437d714e1ec529` against `1669296` supported
R10/R11 and found no new significant issues within that follow-up. It retained
R6's deferred, unaccepted acceptance gap and did not rerun tests.

The bounded independent review of exact commit
`461e81c573d3752912d19db345103af2ba6eddb5` against `08d061c` supported
R12/R13, found no new significant findings, and retained the complete R1-R13
ledger including deferred/unaccepted R6. It was source review only, without
independent test reruns or live/cross-process evidence.

The bounded independent review of exact commit
`8dafbab89629a50be0722f48ac4418d5311419df` against `461e81c` supported
R14 and found no new significant findings within that follow-up. It examined
the complete R1-R14 ledger and retained all earlier dispositions, including
deferred/unaccepted R6. It did not rerun validation or exercise live devices.

The bounded independent review of exact commit
`5d60757591efbb381c2cb8cd672adf2a6e2966cb` against `8dafbab` supported
R15/R16 and found no new significant findings within that follow-up. It
examined the full R1-R16 ledger, relevant specification/ADRs and changed
runtime/tests/docs, retained deferred/unaccepted R6, and did not rerun checks
or exercise live devices.

The bounded independent review of exact commit
`4abe28088f05569fd9357367f5fc77ffce0a9c5c` against `5d60757` supported
R17 and found no new significant findings. It checked gate ownership,
original-risk parsing, per-action uncertainty removal, all 18 new unit and
four SQLite cases, retained controls and the complete R1-R17 ledger.
This was source-only review, not independent execution or live/cross-process
validation. R6 remains deferred and unaccepted.

The bounded independent review of exact commit
`07d5f039e1498496121f5981236cae0fe0629872` against `a1fd7de` supported
R18/R19 and found no new significant findings. It examined permission checks,
under-gate refresh, retained original-risk seeding/error behavior, action-owned
accounting and all 13 new unit/seven SQLite cases against the complete R1-R19
ledger and relevant spec/ADRs. It did not rerun checks or establish live-device,
cross-process or current-head SDK/Aspire/browser evidence. R6 remains deferred
and unaccepted.

## R7/R8 regression evidence

- Before the production fix, the targeted R7/R8 unit regressions failed in all
  14 cases. After the fix, the dispatcher unit subset passed (50/50), then an
  additional unsent-claim-conflict regression was added and passed in the full
  unit gate.
- `tools/validate.sh build`, `unit`, `integration`, and `coverage` passed on
  the new follow-up tree: 182 unit tests, 248 integration tests, and verified
  discovery. The dispatcher SQLite integration subset passed (28/28).
- `tools/validate.sh architecture` passed (6/6); `tools/validate.sh docs`
  passed, including the updated complete finding ledger.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T162827Z-27623` and
  `artifacts/coverage/integration/20261014T162914Z-27789`. All configured gates
  passed; changed executable lines were 98.7% (834/845), Application lines
  98.3% (1765/1796), and branches 93.9% (758/807).
- Dedicated SDK/Aspire/browser gates below describe `aefba04`, not execution
  against this follow-up. The integration gate includes SDK-contract sources;
  current-head CI must still establish all required checks after pushing.
- No storage contracts, schema, SDK integration, process launch, or production
  feature registrations changed for R7/R8.

## R9 regression evidence

- All three targeted delayed-duplicate regressions failed before the accounting
  fix: known failed writes, failed reads, and exceptional/unknown reads. After
  replacing the raw counter with an action-ID set, `tools/validate.sh build`,
  `unit` (185/185), `integration` (248/248), `coverage`, and `docs` passed.
- Latest coverage inputs:
  `artifacts/coverage/unit/20261014T163747Z-30074` and
  `artifacts/coverage/integration/20261014T163853Z-30370`. All configured gates
  passed: changed executable lines 98.7% (834/845), Application lines 98.3%
  (1766/1796), and branches 93.8% (757/807).
- The uncertainty-write gate remains stricter than the two-failure count.
  Accounting and uncertainty state are still accessed under the same
  per-proposal semaphore and released only after the turn's callbacks stop.
- This small internal accounting fix changes neither storage nor public
  contracts. Full required current-head CI remains a separate gate.

## R10/R11 regression evidence

- The targeted pre-fix run had five failing approval-race cases and five passing
  negative controls. After the fixes, the dispatcher unit subset passed (64/64).
  The stale-read scenario was then expanded to cover both Pending/Approved
  snapshots against active/completed winning execution.
- `tools/validate.sh build`, `unit` (197/197), `integration` (248/248),
  `coverage`, `architecture` (6/6), and `docs` passed against the final
  approval-reconciliation tree, with discovery verified.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T164850Z-34033` and
  `artifacts/coverage/integration/20261014T165023Z-34350`. All configured gates
  passed: changed executable lines 98.7% (841/852), Application lines 98.3%
  (1773/1803), and branches 93.7% (769/821).
- Reconciliation accepts only a matching committed terminal action state or a
  winning decided approval. Still-pending conflicts, incompatible action
  outcomes, and non-concurrency persistence failures remain explicit errors.
  A winning approval cannot bypass expiry or replay its side effect.
- The public approval XML contract now documents the separate-write failure
  boundary. No signatures, storage contracts, schema, or dependencies changed.
  Dedicated SDK/Aspire/browser gates remain for current-head CI rather than
  being claimed from earlier-head execution.

## R12/R13 regression evidence

- Before the production fixes, the targeted 13-case run had seven failures
  demonstrating stale approval creation, blocked known write recovery, and
  omitted recovered-read failure accounting; six negative controls passed.
  After the fixes and additional exceptional-read/queued-cancellation controls,
  the complete dispatcher unit subset passed (81/81).
- `tools/validate.sh build`, `unit` (212/212), `integration` (248/248),
  `coverage`, `architecture` (6/6), and `docs` passed on the final runtime/test
  tree, with discovery verified and zero build warnings/errors.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T170509Z-37354` and
  `artifacts/coverage/integration/20261014T170728Z-37835`. All configured gates
  passed: changed executable lines 99.1% (864/872), Application lines 98.5%
  (1796/1823), and branches 94.0% (796/847).
- Recovery remains bounded observation, never execution replay. The larger
  diff extracts the existing observer from its arbitration wrapper so recovery
  and normal execution can share one canonical proposal gate and outcome
  accounting helper; it does not add a new recovery mechanism.
- No storage contracts, schema, dependencies, SDK integration, process launch,
  or functional production registrations changed. Public XML remarks document
  the existing-binding creation behavior; R6 remains deferred and unaccepted.
- Current-head CI on `08d061c` found the unchanged SDK README URL returned HTTP
  503 during remote Markdown validation. A direct GET reproduced 503; GitHub
  refused the targeted docs-job rerun while the workflow was in progress.
  Local Markdown validation passed. No link or required check was weakened;
  the subsequent pushed head must establish fresh required CI evidence.

## R14 regression evidence

- All four targeted cases failed before the fix, returning `AwaitingApproval`
  instead of a recorded rejection, active/completed execution, or a newly
  approved execution. The clock remains unchanged throughout these regressions.
  After the fix, the complete dispatcher unit subset passed (85/85).
- `tools/validate.sh build`, `unit` (216/216), `integration` (248/248),
  `coverage`, `architecture` (6/6), and `docs` passed against this final
  runtime/test tree, with discovery verified and zero build warnings/errors.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T171735Z-40470` and
  `artifacts/coverage/integration/20261014T172018Z-40897`. All configured gates
  passed: changed executable lines 99.1% (871/879), Application lines 98.5%
  (1802/1830), and branches 93.6% (800/855).
- The existing expired-Pending regression pauses the action snapshot after
  refreshing Pending and before resolution. Its three winning decisions still
  exercise an actual resolution CAS conflict instead of being accidentally
  short-circuited by the new refresh.
- The change adds no persistence contracts/schema, dependencies, feature tools,
  execution retries, or SDK/process-launch changes. R6 remains deferred and
  unaccepted.

## R15/R16 regression evidence

- The targeted five-case pre-fix unit run had three failures proving Unknown
  classification and revoked/Unsupported queued observation; Completed
  non-application and allowed-version controls already passed.
- After the fixes, all dispatcher unit cases passed (90/90), and the real
  SQLite dispatcher subset passed (29/29), including definitive non-application
  after Unknown delivery, persisted failure, duplicate replay and bounded retry.
- The initial default-output gate run passed build/unit (221/221), then was
  stopped during delayed integration coverage collection. Its generated
  integration output contained 1,717 numbered duplicate files; the cause of
  those duplicates was not established. No cache or coverage filters changed.
- Re-ran the unchanged `tools/validate.sh build`, `unit` (221/221),
  `integration` (249/249), `coverage`, `architecture` (6/6), and `docs` gates
  with standard isolated MSBuild output:
  `UseArtifactsOutput=true ArtifactsPath="$PWD/artifacts/isolated-validation-r15-r16"`.
  All passed, with discovery verified and zero build warnings/errors.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T180645Z-47741` and
  `artifacts/coverage/integration/20261014T180712Z-47915`. All configured gates
  passed: changed executable lines 99.1% (872/880), Application lines 98.5%
  (1804/1831), and branches 94.1% (784/833).
- `NotApplied` already means the adapter established that the effect did not
  occur. Its XML now explicitly forbids interpreting an unchanged snapshot with
  possible late application as proof; that remains Unknown. No real device
  verifier is registered in M2-01, and controlled evidence is not live proof.
- No schema, storage contracts, dependencies, SDK/process-launch changes, or
  automatic retry were added. R6 remains deferred and unaccepted.

## R17 regression evidence

- The targeted pre-fix 16-case unit run had 15 failures proving fresh equivalent
  sends, erased original write risk, lost orphan ownership and unreadable
  metadata handling. The original-read negative control already passed.
  After fixing the shared uncertainty set and adding missing/null metadata
  cases, the complete dispatcher unit subset passed (108/108).
- The four real-SQLite regressions cover sequential/queued fresh IDs after
  inconclusive or throwing observation, asserting the orphan remains Unknown,
  the fresh action is Rejected, and no additional handler execution occurs.
- With `UseArtifactsOutput=true` and
  `ArtifactsPath="$PWD/artifacts/isolated-validation-r15-r16"`, the unchanged
  `tools/validate.sh build`, `unit` (239/239), `integration` (253/253),
  `coverage`, `architecture` (6/6), and `docs` passed on this runtime/test tree.
  Discovery was verified; Release/format completed with zero warnings/errors.
  This reuses the isolated output described above, without deleting caches,
  changing coverage filters, or claiming the aborted default run passed.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T181651Z-50286` and
  `artifacts/coverage/integration/20261014T181717Z-50374`. All configured gates
  passed: changed executable lines 99.1% (883/891), Application lines 98.5%
  (1814/1842), and branches 94.0% (781/831).
- Dedicated SDK/Aspire/browser local evidence below remains historical;
  required checks must establish current-head CI evidence after pushing.
- Recovery first marks the persisted execution conservatively uncertain under
  its proposal gate, then strictly reads its original canonical risk. Only a
  validated ReadOnly classification removes that marker without observation.
  Unknown/error/unjournaled outcomes keep it; a known durably recorded outcome
  removes only that action ID, preserving other unresolved orphans.
- This is defensive dispatcher behavior, not resumption of interrupted turns
  or a durable multi-host retry mechanism. No schema, storage API, dependencies,
  actual feature tools or SDK/process-launch behavior changed. R6 remains
  deferred and unaccepted.

## R18/R19 regression evidence

- The targeted pre-fix 11-case unit run had nine failures (four post-delivery
  permission races and five stale-Executing races) and two passing allowed
  policy-version controls. After fixing both boundaries and adding two
  pre-send Rejected/Expired controls, the dispatcher unit subset passed
  (121/121); the SQLite subset passed (40/40), including seven new cases.
- Normal observation checks current permission immediately after delivery;
  denied/Unsupported outcomes become Unknown without a verification callback.
  Recovery seeds original risk conservatively, refreshes journal state under
  the same proposal gate and returns any winning non-Executing state without
  another observation or terminal CAS. Read/journal errors remain explicit.
- A known durable pre-send Rejected/Expired winner clears only its own
  provisional uncertainty, just as known Succeeded/Failed does; it cannot
  erase another orphan. Tests assert known outcomes retain a permitted fresh
  attempt while Unknown remains blocked, with no extra execution/observation.
- SQLite race tests use production stores behind a read-pause adapter; their
  optimistic-version checks and winning terminal records are real SQLite,
  not claims of live-device or cross-process crash validation.
- With the same isolated MSBuild output and unchanged scripts described for
  R17, `tools/validate.sh build`, `unit` (252/252), `integration` (260/260),
  `coverage`, `architecture` (6/6), and `docs` passed. Discovery was verified;
  format/Release completed with zero warnings/errors.
- Coverage inputs:
  `artifacts/coverage/unit/20261014T182846Z-52938` and
  `artifacts/coverage/integration/20261014T182927Z-53041`. All configured gates
  passed: changed lines 99.1% (891/899), Application lines 98.5% (1822/1850)
  and branches 94.2% (824/875).
- No schema/storage contracts, dependencies, functional tools, SDK integration
  or process-launch behavior changed. Full replay R6 remains deferred and
  unaccepted; dedicated SDK/Aspire/browser current-head CI is separate.

## Earlier full-gate evidence

The following checks ran on native macOS against the review-fix tree based on
`3840d03`; the PR identifies the resulting commit. This is validation evidence,
not a completion claim for the deferred R6 acceptance gap.

- **Release build/format:** `tools/validate.sh build` passed; zero warnings and
  errors.
- **Unit and integration:** `tools/validate.sh unit` passed (166/166);
  `tools/validate.sh integration` passed (244/244), with discovery verified.
  The targeted dispatcher integration filter also passed (24/24).
- **Coverage:** `tools/validate.sh coverage` passed using unit report
  `artifacts/coverage/unit/20261014T151020Z-18758` and integration report
  `artifacts/coverage/integration/20261014T151048Z-18968`. Changed executable
  lines measured 98.4% (823/836); Application coverage measured 98.2% lines
  (1754/1787) and 93.7% branches (743/793). All critical-module thresholds passed.
- **Other gates:** `tools/validate.sh architecture` passed (6/6);
  `tools/validate.sh docs` passed; `tools/validate.sh sdk-contracts` passed
  (55/55); `tools/validate.sh aspire-e2e` passed (7/7);
  `tools/validate.sh browser-e2e` passed (29/29), and its deliberately
  failing gate probe failed as expected.
- **SDK observation:** runtime termination required the bounded send deadline;
  immediate interruption was false. SDK validation still passed, but bounded
  send-deadline behavior is the observed cancellation limit, not a claim of
  immediate interruption.
- **CI investigation:** run `38583579637` initially failed during disposal of
  the pre-existing SDK automatic-compaction cancellation fixture with an HTTP
  listener broken pipe. No unrelated fixture change was made. One failed-job
  rerun completed successfully, including integration and coverage; all jobs
  passed on attempt 2 against the original `3840d03` head. New-head Linux CI
  must run again after pushing the review fixes.
- `origin/main` was fetched during the check-in; the branch was zero commits
  behind, so no base integration or conflict resolution was needed.
- Exercised behavior: controlled typed handlers, in-memory and real SQLite
  journals, restart recovery, exact approval lifecycle, SDK contract and
  required Aspire/browser E2E scenarios.
- Unexercised behavior: live Home Assistant transport/device outcomes and the
  approval UI are excluded from M2-01 and remain for M2-02/M2-04.
- Residual risk: recovery correctness depends on the registered verifier being
  a bounded observer that never issues the action again; this is part of the
  registration contract and is covered by controlled tests, not a live
  provider/device integration.
- The exact runtime follow-up commit `aefba04` received the independent review
  recorded above. R6 remains deferred and blocks claiming the complete
  acceptance/disposition basis. The later documentation-only commit records
  that review without changing the reviewed runtime, tests, or contracts.
