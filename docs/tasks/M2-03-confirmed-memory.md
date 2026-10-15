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

Implementation is committed and independently reviewed.
Native macOS evidence after the review fix:
`tools/validate.sh build` passes Release formatting/analyzers with zero
warnings/errors; `architecture` passes 6 tests; `docs` passes link checks;
`unit` passes 324 tests; `integration` passes 430 tests; `coverage` passes all
existing and new critical thresholds with 99.8% changed executable lines
(631/632). Coverage also runs the negative gate fixtures.

The actual-runtime memory test initially exposed the pinned runtime's rejection
of dotted tool names. The adapter now translates dots to underscores at the
wire boundary and rejects collisions; host names/authorization remain unchanged.
Both runtime memory retrieval/denial and pre-provider collision checks pass.
This is protocol compatibility mapping, not a dependency upgrade or an
inferred owner acceptance of a dependency defect.

After the R6 host-completion fix, `sdk-contracts` passed the standalone actual-runtime
harness and 57 adapter contracts; `aspire-e2e` passes 8 scenarios;
`browser-e2e` passes 29 scenarios and verifies that its deliberately failing
browser probe fails; explicit `gate-self-test` passed. These runtime/E2E gates
are rerun after R7; their final evidence is recorded on PR #47.

Linux CI results and exact run links are recorded in PR #47 and its required
checks. The first run passed build, architecture, unit, integration, coverage,
SDK (Linux/macOS) and Aspire gates; its docs link checker encountered external
GitHub HTTP 503 responses on two unchanged existing links. No exclusions or
gate weakening were applied. Final readiness requires all required checks,
including docs/browser, to pass on the submitted head.

No live models, cloud or devices were used.
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
| M2-03-R2 | Medium | Retained facts could not be updated in place with siblings active. | Fixed: updates accept the complete reviewed sibling snapshot and require a distinct qualifier. Regression preserves both IDs, increments only the target version and rejects an omitted sibling snapshot. |
| M2-03-R3 | Medium | A stale missing-receipt recovery observation could settle Failed after a concurrent mutation committed. | Fixed: journal settlement checks receipts inside the immediate transaction that serializes with commit authorization; commit-first publishes the receipt as success and settlement-first prevents a later write. Two-dispatcher gated regression covers both publications; separate regression covers settlement-first. |
| M2-03-R4 | Medium | Valid large escaped search results exceeded the journal response limit. | Fixed: serialized facts/citations/framing are budgeted below 900,000 characters, returning matching fact/reference subsets and explicit Truncated metadata. A 100-fact maximum-size backslash-value SQLite regression verifies publication, replay and deletion invalidation. |
| M2-03-R5 | Medium | Invalidation during preparation caused terminal reconciliation to parse a redacted envelope. | Fixed: terminal reconciliation returns invalidated entries before envelope parsing. Deterministic deletion-between-journal-reads regression verifies a content-free invalidation response. |
| M2-03-R6 | Medium | Call-generated action IDs in canonical commands gave equivalent memory failures separate retry budgets. | Fixed in the subsequent revision: a host-owned semantic failure key omits generated IDs, retains owner/source/content/reviewed versions and sorts conflict references. Same-turn create/update regressions reject the third equivalent call, count exact replay once, separate different content and verify turn-release reset. Exact journal/approval/receipt identities are unchanged. |
| M2-03-R7 | Medium | Previously missed (summary-only): retain_both or moving an update into a 100-fact group created an unreviewable 101st active fact. | Fixed in the subsequent revision: reject growth when 100 reviewed siblings already exist, but allow in-place updates with 99 siblings and supersession of all 100. Real-SQLite boundary regression verifies both rejected operations preserve the full reviewable group/other fact, in-place version advancement and successful supersession. |

R2-R5 were reported by automated PR review on `c9511ec` and repeated on
`80a382f`; their thread roots are respectively 4285248818, 4285248922,
4285248973 and 4285249038. The original narrow review's no-new-findings result
does not dispose of these later findings.

Independent R2-R5 follow-up reviewed exact fix
`90f8b6548380ee7414f29cfeb66a7a4a2980aefb`, confirmed each of R2-R5 fixed with
concrete boundary/test evidence and reported no new findings. It independently
ran 27 memory SQLite integration and 9 memory unit tests. Both transaction
orderings were inspected; commit-first uses two gated dispatchers and
settlement-first uses recovery followed by a late direct store commit rather
than a second gated dispatcher ordering. The review used existing Release
binaries and did not independently rebuild or rerun broad gates/platforms.
Final readiness still requires current-head CI/runtime gates and resolved PR
threads; passing an automated review workflow alone is not a disposition.

R6 was reported on `96642b4` in thread 4285597934. R2-R5 remained explicitly
resolved in that review; they are not omitted or erased by this later round.
Independent R6 follow-up reviewed
`94cde2233bd41b8b14500e8e41aa6d1d0cb8f09b` against `96642b4`, verified the
semantic accounting/unchanged exact bindings and independently ran 29 memory
SQLite cases, with no R7+ findings. It used existing Release binaries and did
not independently establish binary/source correspondence or rerun broad gates.
Conflict ordering and owner/source separation were inspected, not separately
varied at runtime. Final runtime/CI evidence remains on PR #47.

The R6 follow-up also exercises both direct redispatch and trusted
`MemoryProposalService.ResolveAsync` for create/update retries. The latter
reconstructs the explicit generated fact ID from the durable command; semantic
normalization therefore recognizes the deterministic new-fact/action ID
relationship rather than relying on an omitted optional argument. These four
route/operation cases pass; final-head automated PR review supplements the
earlier exact-commit review.

R7 is retained from the automated review summary on
`a188e0dec9df4f3c2dd4e93987ad786f1d82d5e1` (review 5536787893), despite its
“0 open findings” count and lack of an inline thread. All seven findings need
explicit dispositions; neither omission from an inline list nor green CI is
evidence of resolution.

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
