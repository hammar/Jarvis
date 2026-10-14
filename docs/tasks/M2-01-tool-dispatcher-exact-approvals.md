# M2-01: Tool dispatcher, exact approvals, and action journal

## Objective and acceptance

Implement the application-owned registered-tool dispatcher and exact approval
flow for issue #5 / M2-01. Model-proposed calls remain untrusted. Host policy
must validate, authorize, journal before execution, and report uncertain
physical outcomes without blind retries. SQLite remains authoritative.

**Specification:** §§5, 7, 14, 15, and 20. **Dependencies:** completed M1
(T02/T03/T04 and M1-01–M1-06). **Issue:** #5.

Acceptance scenarios include:

- An unregistered tool or disallowed entity is denied before external execution.
- An ambiguous entity alias requires clarification and does not select a target.
- Re-dispatch with the same action identity returns the recorded outcome and
  never repeats execution; a distinct later request with identical arguments
  remains a distinct action.
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

- **M2-01-R1 — Fixed:** a process restart could leave an action in `Executing`
  indefinitely because redispatch returned that status without observation.
  The dispatcher now distinguishes active in-process execution from orphaned
  persisted execution and performs bounded verification only for the latter.
  Unit and real-SQLite regression scenarios verify recovery without a second
  execution; a same-dispatcher concurrent duplicate remains `Executing`.
- The change is validated against the current working tree; no commit or PR
  review revision exists yet.

## Completion evidence

- **Release build/format:** `tools/validate.sh build` passed; zero warnings and
  errors.
- **Unit and integration:** `tools/validate.sh unit` passed (159/159);
  `tools/validate.sh integration` passed (241/241), with test discovery
  verified for both reports.
- **Coverage:** `tools/validate.sh coverage
  artifacts/coverage/unit/20261014T063608Z-10995
  artifacts/coverage/integration/20261014T063354Z-9940` passed. Changed
  executable lines measured 98.3% (770/783); Application coverage measured
  98.3% lines and 93.8% branches. All critical-module thresholds passed.
- **Other gates:** `tools/validate.sh architecture` passed (6/6);
  `tools/validate.sh docs` passed; `tools/validate.sh sdk-contracts` passed
  (55/55); `tools/validate.sh aspire-e2e` passed (7/7);
  `tools/validate.sh browser-e2e` passed (29/29), and its deliberately
  failing gate probe failed as expected.
- **SDK observation:** runtime termination required the bounded send deadline;
  immediate interruption was false. SDK validation still passed, but bounded
  send-deadline behavior is the observed cancellation limit, not a claim of
  immediate interruption.
- Exercised behavior: controlled typed handlers, in-memory and real SQLite
  journals, restart recovery, exact approval lifecycle, SDK contract and
  required Aspire/browser E2E scenarios.
- Unexercised behavior: live Home Assistant transport/device outcomes and the
  approval UI are excluded from M2-01 and remain for M2-02/M2-04.
- Residual risk: recovery correctness depends on the registered verifier being
  a bounded observer that never issues the action again; this is part of the
  registration contract and is covered by controlled tests, not a live
  provider/device integration.
- Review disposition is against the current working tree; no commit or PR
  review revision exists because this change has not been committed or opened
  as a pull request.
