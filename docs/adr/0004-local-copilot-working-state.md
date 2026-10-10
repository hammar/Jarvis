# ADR 0004: Local Copilot working-state lifecycle

## Status

Proposed for M1-04 (#38); not an accepted scope reduction. Production compaction
remains blocked. The owner explicitly chose automatic reconstruction and
processing of the current request when successful Local SDK state is missing.

## Context

SQLite owns durable messages, turn outcomes and request deduplication. SDK
working state must not retain context that the host has omitted, expose Local
history to cloud inference, or replay uncertain inference/tool effects.
The pinned SDK 1.0.16 / runtime 1.0.90 supports native history across disk resume,
but there is no established public assistant-role seeding API at these pins.

## Proposed decision and current implementation

Reuse disk SDK history only for tool-free Local turns. Retain one private
working-state root per application conversation. Each turn still owns a fresh
runtime/client; cancellation cannot abort another conversation's runtime.
Cloud and tool-bearing turns always use disposable fresh sessions.

Resume only when a private application checkpoint identifies a Completed turn
in the same SQLite conversation and its selected role/text/privacy history,
host instructions, context-policy version, SDK version and provider options
match. Provenance identifiers do not change history content. A changed model,
provider configuration or history selection reconstructs bounded labelled
history using `LocalChatPrompt`, without generating earlier answers.
Subsequent resumed turns submit only the current request and use native roles.
The prompt-transformation correction, explicit streaming and empty capability
catalog apply to both create and resume.

Publish a durable pending marker before Send. Publish ready working state
before committing terminal success; a ready marker is never usable until its
checkpoint turn is durably Completed. This ordering also handles a crash
between marker publication and SQLite commit. Checkpoint-write failure produces
`Interrupted` / `session_checkpoint_failed`, not a durable Completed turn
followed by an event-stream failure. No answer is committed in that case.

The actual missing-session signature is an `IOException` containing a
`RemoteRpcException` with code `-32603` and `Session not found`. Only this
pre-Send resume failure is rebuilt and the current request then executed once.
Other provider, tool or runtime errors are never retried. Missing/corrupt
markers, selection changes and offline SQLite restore also reconstruct.
`RuntimeContextRebuilt` records a safe reason without prompt/summary content.

Retain at most 32 Local conversation roots, including active roots, and evict
idle copies after 30 days. Startup/hourly maintenance removes orphan copies
whose checkpoint turn no longer exists in SQLite, without inference.
Registration and pruning share an ownership gate; active roots and unresolved
runtime shutdowns cannot be deleted. The coordinator's existing four-executing,
one-active-turn-per-conversation limits remain authoritative.

## Compaction acceptance blocker

Actual-runtime contracts prove automatic compaction events and persisted summary
use after resume. The public manual `History.CompactAsync` RPC and context
metadata also work, but the request type is explicitly experimental (`GHCP001`).
An isolated test-only suppression evaluates it; production does not use it.

Its `TokenLimit = 8192` is not a hard enforcement boundary: a controlled
144,022-character summary produced `success = true`, with 18,088 current tokens
and a reported 8,192-token limit, and reached the next provider request.
Metadata reports token usage but does not apply the application's
UTF-16/4-plus-20% estimator or its selected-message privacy boundary.
Therefore neither automatic thresholds nor the manual token-limit parameter
establish safe production compaction under the current context contract.
Production infinite sessions remain disabled. Truncation/reconstruction is not
reported as compaction, and A6 remains incomplete.

Further public-state experiments show that `SummaryContent` is not the complete
compacted provider message. The runtime wraps it with original user instructions
and additional framing; the first original request survives even when its
assistant answer is removed. `GetEventsAsync` retains the original user and
assistant events after compaction rather than exposing only active model
context. Context attribution/heaviest-message APIs return counts and truncated
labels, not the full message text. Neither the event archive nor the summary
alone is therefore a validated substitute for the complete compacted request.

Further work must establish enforceable summary/retained-context accounting
before inference, including summary failure and oversize rejection, or obtain
an explicit owner-approved scope change. No acceptance waiver is inferred from
owner unavailability.

## Consequences and limitations

No SQLite schema migration or extraction from private SDK files is required.
SDK copies remain sensitive, replaceable data and are not included in SQLite
backups. An offline restore can leave newer SDK copies, but checkpoint/history
validation prevents their reuse. Cache deletion is not inference replay.

This design reduces prompt reconstruction within the selected history window,
not runtime launch count. Controlled measurements are recorded in the task brief;
they are not universal latency claims. Tool-bearing conversations do not yet
have cross-turn native continuity. Independent review and remaining required
quality gates are prerequisites for acceptance.
