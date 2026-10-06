# Chronological milestone backlog restructuring

## Objective and acceptance

Owner-directed planning change: replace cross-milestone workstreams with tasks
that each belong to, and finish in, exactly one chronological milestone.
Every milestone closes with a cumulative runnable demo.

**Specification:** sections 15-16, preserving sections 2, 4-14 and 18-21.
**Scope:** GitHub milestone descriptions, remaining task issues, scope migration
mapping, specification, README and directly affected task/testing/operations
references. No runtime, contracts, schema, thresholds, credentials or devices
change. Completed T01-T04 records and closed historical tracking issues remain
unchanged. Existing issue numbers are retained wherever scope is narrowed.

## Dependencies, ownership and assumptions

The owner authorizes this restructuring. All original deliverables retain a
named owner; cloud-dependent invalidation tests move to the cloud task rather
than blocking M2 on M3. M1 has no registered tools and denies unexpected tool
proposals; it does not ship a success-shaped dispatcher stub.

Owned repository files: specification, README, testing/operations guidance,
ADR 0003's scheduler-task reference, T03's exclusion references and this brief.
Existing staged work is not part of this change and must not be committed as
backlog work. GitHub changes are persistent independently of repository edits.

## Validation and completion

- Every remaining task has a unique milestone-local ID, exactly one matching
  GitHub milestone, explicit prerequisites and no unfinished later-milestone
  scope. Completed historical issues retain their original milestones.
- Dependencies form a DAG and never require an open task in a later milestone.
- Each milestone has a final runnable demo task, cumulative exit criteria and
  required evidence. M0 remains a runnable feasibility harness, not a service.
- Preserve original acceptance requirements, coverage/CI gates and review
  policy. Live smoke runs stay opt-in; simulator evidence is never live proof.
- Run `tools/validate.sh docs`; independently review the restructure for scope
  loss, impossible dependencies and inconsistent gates. Record findings and
  dispositions here. No runtime suites are needed for documentation-only edits.

## Finding ledger and evidence

Independent review examined the original/revised issue and milestone snapshots,
the unstaged documentation diff and this brief against baseline
`3b3d18f8f11a171ec91c35a386a75026c8615280`. No product implementation was reviewed.

| Finding | Severity | Disposition and evidence |
|---|---|---|
| MR-01: safe external-read retry lacked an implementation owner after splitting T11 | Medium | Fixed: M2-02 explicitly owns one transient Home Assistant read retry within the original deadline, request-count/cancellation tests, no nested retries/hedging and no uncertain-write retry. Specification backlog names that ownership; M2-04 exercises it cumulatively and M4-01 verifies it. |
| MR-02: reusable handoff template still used obsolete Txx placeholder (PR review comment 4191955353 against 7213d80) | Low | Fixed: the template requires the exact backlog-table ID, with M1-01 and historical T02 examples. Documentation/link validation and whitespace checks passed for this correction before commit. |

`tools/validate.sh docs` passed before review and after the MR-01 correction;
`git diff --check` passed. Direct GitHub REST verification confirmed all 12
remaining tasks have unique IDs and matching single milestones, no blocked
labels, and matching specification rows. The planned dependency graph is
acyclic with no future-milestone prerequisite. T01-T04 bodies, titles, closed
states and milestone numbers are unchanged. All four open milestones name their
own demo/closure task; the corrected retry ownership and request-count tests
are present in the live M2-02 issue.

Repository changes are documentation-only and remain in this worktree; GitHub
issue/milestone changes are already persisted. No runtime suites were run or
feature/milestone marked complete by this administrative change. Existing
staged source/test additions were left untouched. No contracts/schema or
accepted ADR decisions changed, and no PR was merged or protection weakened.
