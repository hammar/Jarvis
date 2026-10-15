# ADR 0005: Single-copy durable tool outcomes

## Status

Accepted by the owner for M2-01 on 2026-10-14.

## Context

T03 stored canonical action requests and statuses but not terminal response
payloads. Conversation answers and status-only tool events cannot reconstruct
an exact tool response after duplicate dispatch or restart. The owner authorized
extending the earlier schema/contracts rather than accepting this R6 limitation.

## Decision

Store each terminal application-owned tool response once in the action journal,
including its result JSON, reason and response identifiers. Commit response,
status and optimistic version in the same SQLite update. Preserve request
binding and never repeat execution to recover missing response data.

Completion events retain chronological call/status metadata and reference the
action ID when one exists. They do not copy the result body. Conversation
messages remain user/assistant dialogue; no extra tool message or automatic
model-context inclusion is introduced. Completion is published only after the
action outcome is durable.

Use an additive migration. Older rows without a stored outcome remain explicitly
status-only, not fabricated full responses. Outcomes share action retention and
backup/restore: deleting conversation/event history does not authorize deleting
idempotency or unresolved-action evidence. Retain existing owner-private database
protection and never copy payloads into diagnostic logs or telemetry.

Bound result JSON to 1,000,000 characters and reason codes to 256 characters;
oversized/invalid persistence input fails explicitly rather than truncating a
response. This bounds stored application data, not a new model-token allowance.

## Consequences

The action journal is the authoritative response source; a later details UI can
follow event references without duplicating payloads. SQLite still cannot
transact with a device. A crash after sending but before journaling remains
uncertain and permits observation only, not blind retry. Completed legacy
responses cannot be recovered retrospectively. No event-sourcing redesign,
tool-history UI or new logging surface is required.
