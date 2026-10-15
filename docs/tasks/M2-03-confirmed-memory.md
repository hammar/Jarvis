# M2-03: Confirmed memory

## Objective and acceptance

Issue #9 implements specification sections 5, 7 and 9: structured,
owner-confirmed local facts, bounded FTS retrieval, explicit conflicts,
versioned changes, deletion and the three memory tools. Dependencies T03 (#4)
and M2-01 (#5) are complete.

Acceptance includes memory surviving runtime loss, malicious retrieved text
remaining data, no unconfirmed sensitive-fact persistence, no silent conflict
overwrite, optimistic concurrency and deletion of derived copies.

## Scope, ownership and exclusions

Application owns memory policy, contracts and typed tool registrations.
Infrastructure owns migration 007, transactional memory persistence and
memory-dependent journal invalidation. Web changes are composition only.
Tests own controlled unit, SQLite and actual-runtime evidence.

No UI, memory HTTP endpoints or coordinator tool-catalog wiring (M2-04), cloud
packets (M3-01), embeddings, semantic inference, or live device/cloud use.
Existing validation thresholds remain unchanged; memory policy/deletion uses
95% line / 90% branch critical coverage. The size of the cohesive change may
exceed the approximate 400-line target because privacy-safe journaling,
transactions, migration and regression tests cannot be separated safely.

## Accepted assumptions and decisions

The owner selected deterministic normalized subject/key conflicts, explicit
supersede/retain-both resolution, LocalOnly defaults, and the existing exact
Approve/Reject mechanism. A changed proposal requires a new approval.
Deletion redacts derived proposal/journal payloads, retaining content-free
invalidation evidence; see [ADR 0006](../adr/0006-confirmed-memory.md).

## Tests and documentation

Automate validation, privacy/owner boundaries, conflict races, approval
expiry/replay, receipts across interrupted settlement, FTS synchronization,
restart/retention survival, deletion races and malicious evidence. Run the
existing build, architecture, unit, integration, SDK, coverage, gate-self-test,
Aspire, browser and docs scripts. Linux CI is required in addition to applicable
native macOS evidence. Unavailable required gates block completion.

Update architecture, testing, operations and affected project READMEs.
Public contracts explain provenance, UTC timestamps, versions and logical
deletion limitations. M2-04 must propagate memory dependencies into derived
context; M3-01 must invalidate cloud packets/consent on change/deletion.

## Completion and review ledger

Implementation is committed; final runtime/platform evidence is in progress.
Native macOS evidence after the review fix:
`tools/validate.sh build` passes Release formatting/analyzers with zero
warnings/errors; `architecture` passes 6 tests; `docs` passes link checks;
`unit` passes 324 tests; `integration` passes 420 tests; `coverage` passes all
existing and new critical thresholds with 100% changed executable lines
(583/583). Coverage also runs the negative gate fixtures.

The actual-runtime memory test initially exposed the pinned runtime's rejection
of dotted tool names. The adapter now translates dots to underscores at the
wire boundary and rejects collisions; host names/authorization remain unchanged.
Both runtime memory retrieval/denial and pre-provider collision checks pass.
This is protocol compatibility mapping, not a dependency upgrade or an
inferred owner acceptance of a dependency defect.

SDK aggregate and Aspire/browser regressions passed before the recovery fix
(57 SDK adapter tests and 8 Aspire scenarios plus browser/gate probe); they are
being rerun on the reviewed fix. Linux CI is not yet claimed. No live models,
cloud or devices were used.
Memory UI/chat end-to-end acceptance remains M2-04, and cloud invalidation
remains M3-01; neither is represented as implemented by these tests.

### Cumulative independent review ledger

Review of `3f527047e5995447f6ee4b255bfdf060e5179b87` against
`36d32a4ec00b43880ed0fad54111547ad80b55ee` independently traced approval,
provenance, receipt, invalidation and source retention boundaries, and ran
18 memory integration and 9 unit tests. It did not rerun the full platform
matrix or establish live-model behavior.

| ID | Severity | Finding | Disposition and evidence |
|---|---|---|---|
| M2-03-R1 | Medium | Transcript deletion blocked completed replay and receipt-based settlement because preparation required the deleted source. | Fixed in the follow-up revision: only terminal/Executing memory actions reuse the journal's source identifier; host completion also reuses its fingerprint. Strict typed arguments, owner/action/request binding, current observation policy and invalidation remain enforced. Four new cases cover completed replay, both restart recovery routes and refusal of new/uncommitted work after source loss. Focused memory integration suite passes 22 cases. |

Independent follow-up review of
`6e1ea399b3721b49900973e576000d55b09e5ee3` against the original reviewed
revision confirmed R1 fixed, independently reran 22 real-SQLite memory tests,
and reported no new findings. It inspected original-request binding, strict
decoding, local/current observation policy, invalidation precedence and
non-execution during receipt reconciliation. Wrong-owner direct redispatch,
Executing-without-receipt and policy/invalidation races specifically combined
with transcript deletion were code-inspected, not new combined test cases.
No owner residual-risk acceptance or merge authorization is inferred.

The slice exceeds the approximate 400-line target because the approval,
transactional persistence and deletion/replay boundary must remain coherent;
most additional lines are meaningful policy/SQLite/runtime regressions. It
introduces schema 7 and additive application metadata/contracts but no data
classification upgrade, live memory endpoint or automatic prompt injection.
