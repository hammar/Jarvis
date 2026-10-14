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
- Equivalent failed or uncertain proposals stop after two attempts in one
  turn, including concurrent attempts, fresh call IDs, and normalized aliases.

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
The follow-up commit containing this record addresses R2-R5; its exact hash
and validation evidence are recorded in the PR body and thread replies.

| Reference | Severity | Finding | Disposition and evidence |
| --- | --- | --- | --- |
| M2-01-R1 | Medium | Restarted actions could remain `Executing` without observation. | Fixed in `3840d03`: bounded observation-only recovery, active-execution arbitration, and unit/SQLite regressions. |
| M2-01-R2 | High | Missing non-optional constructor arguments could synthesize defaults and alter a proposed action. | Fixed in the review follow-up: enforce required constructor parameters. `MissingRequiredArgumentsAreRejectedBeforePreparationOrJournalWrites` rejects omitted target/operation arguments and an empty object before preparation, journaling, or execution. |
| M2-01-R3 | High | Approval expiry or policy could change while awaiting the durable execution claim. | Fixed in the review follow-up: recheck expiry and policy immediately after the claim. `AuthorizationIsRecheckedAfterDurableClaimBeforeSending` and `AutomaticActionCannotExecuteWhenPolicyChangesWhileClaimingIt` assert terminal rejection/expiry and zero sends. |
| M2-01-R4 | Medium | Fresh call IDs bypassed the two-equivalent-failure limit. | Fixed in the review follow-up: canonical turn-scoped failure accounting serializes equivalent attempts; `TwoEquivalentFailuresStopFreshCallIdsButNotLaterTurns` and `ConcurrentEquivalentFailuresCannotRacePastTheTwoAttemptLimit` cover errors, aliases/property order, fresh call IDs, and concurrency. SDK contracts assert turn accounting cleanup. |
| M2-01-R5 | Medium | Changed policy prevented recovery of an orphaned `Executing` action. | Fixed in the review follow-up: permit observation-only recovery when current policy allows; otherwise journal `Unknown` without observing or sending. `RestartedExecutionHandlesChangedPolicyWithoutReplayingIt` and `PolicyChangeDoesNotMistakeActiveExecutionForRestartRecovery` cover both restart permissions and active-call arbitration against real SQLite. |
| M2-01-R6 | Medium | Duplicate/restarted dispatch loses the original result JSON and detailed reason. | Open, owner decision required. The retained T03 journal persists intent/status/version only. Documentation now explicitly describes status-only replay, but that does not fix full response replay. No migration or persistence-contract expansion is authorized by the earlier unchanged-T03 decision, and no new acceptance of this specific limitation has been obtained. |

For R6, the concrete alternatives are explicit owner acceptance of status-only
replay for M2-01 or authorization to extend durable terminal-result persistence
with duplicate/restart payload-equality tests and a reviewed privacy/retention
contract. The owner was unavailable when asked during the check-in. The R6
thread remains unresolved; passing automation does not dispose of this finding
or make the PR ready for owner acceptance.

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

## Completion evidence

The following checks ran on native macOS against the review-fix tree based on
`3840d03`; the PR identifies the resulting commit. This is validation evidence,
not a completion claim while R6 remains open.

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
  recorded above. R6 remains unresolved and blocks claiming the complete
  acceptance/disposition basis. The later documentation-only commit records
  that review without changing the reviewed runtime, tests, or contracts.
