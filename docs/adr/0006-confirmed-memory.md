# ADR 0006: Confirmed memory and privacy-safe deletion

## Status

Accepted by the owner for M2-03.

## Decision

Memory mutations reuse exact-action approvals. Facts change only after the
authenticated owner approves the exact operation. Subject/key conflicts are
detected deterministically after normalization, not inferred semantically.
Supersession identifies exact versions; retaining both requires distinguishing
context. Changed facts/conflict sets require a new proposal.
An in-place update of a retained fact binds the complete sibling version set
and a qualifier distinct from the siblings, preserving both fact identities.

New facts are LocalOnly. Model arguments cannot supply another owner, assert
approval, forge source provenance or reclassify data for cloud disclosure.
Source type and an opaque message reference identify original evidence;
deleted transcripts are explicitly unavailable, never reconstructed from a
copied prompt. Legacy references remain labelled as legacy.

SQLite commits fact changes and action-bound receipts atomically. Recovery
observes receipts rather than retrying a mutation or guessing from current
fact values. Journal settlement rechecks receipts under the same immediate
write lock as commit authorization: a stale missing-receipt observation cannot
settle failure after a commit; settlement winning first prevents that commit.
Terminal replay and interrupted settlement use the recorded
source identifier and request fingerprint when the transcript is gone; they
never execute another mutation. New/uncommitted changes still require the
original owner message. Durable facts survive conversation/audit retention
and SDK loss.

Explicit deletion removes fact content and FTS entries and redacts dependent
proposal/journal content. Content-free fact/action IDs, versions, timestamps
and deletion/invalidation markers remain for concurrency and replay protection.
Journal publication checks dependencies transactionally so a racing write
cannot reintroduce deleted content.

This narrowly supersedes [ADR 0005](0005-single-copy-tool-outcomes.md):
memory-dependent invalidated results no longer replay their original payload.
They report invalidation and never execute again. Unrelated actions retain
exact-response replay. Edits also invalidate stale derived results.

Original conversation messages and configured backups retain their separate
retention policies. Logical deletion is not secure erasure of SQLite pages,
WAL, backups or already delivered answers. No memory cache or cloud packet
subsystem is introduced here. M2-04 must apply these dependency rules to new
context copies; M3-01 owns cloud packet/consent invalidation.

## Consequences

The pinned runtime's tool-name alphabet excludes dots. At the SDK boundary,
Jarvis maps dots to underscores and rejects collisions before runtime startup;
callbacks, permissions, approvals and audit retain the original registered
names. This is protocol name adaptation, not a change to tool authority or a
dependency upgrade. Actual-runtime tests cover the mapping. The owner was
unavailable for the follow-up compatibility question; no residual-risk
acceptance is inferred from that unavailability.

Retrieval returns bounded, owner-scoped, provenance-bearing evidence, never
instructions or authority. Pending changes cannot silently overwrite conflicts.
Deletion takes precedence over historical payload replay, while content-free
receipts prevent recreating deleted data through an old action.
