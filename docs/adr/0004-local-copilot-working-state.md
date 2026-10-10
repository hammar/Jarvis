# ADR 0004: Local Copilot working-state lifecycle

## Status

Accepted design direction for M1-04 (#38), 2026-10-10: the owner explicitly chose
SDK-owned automatic compaction, no Jarvis workaround for its observed defect,
and separate upstream reproduction work. The owner also chose automatic
reconstruction and processing of the current request when Local SDK state is
missing. Implementation completion still requires validation and independent
review; these decisions do not make an unrun gate pass.

## Context

SQLite owns durable messages, turn outcomes and request deduplication. SDK
working state must not retain context that the host has omitted, expose Local
history to cloud inference, or replay uncertain inference/tool effects.
The pinned SDK 1.0.16 / runtime 1.0.90 supports native history across disk resume,
but there is no established public assistant-role seeding API at these pins.

## Decision and implementation

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

## SDK-owned compaction and model budget

The owner directed Jarvis to lean on library-managed flows instead of modifying
or reimplementing compaction. Tool-free Local create/resume uses the pinned
public experimental named-provider/model configuration, with exactly one Local
model selected and explicit telemetry disabled. It installs 8,192 input and
2,048 output tokens (10,240 total), and automatic background/blocking thresholds
of 0.80/0.95. Cloud and tool-bearing paths keep their separate fresh-session
configuration. No mixed local/cloud picker or runtime delegation is exposed.
Compatibility version 2 invalidates pre-change working copies without touching
durable history.

Actual-runtime experiments establish that ordinary capability overrides are
ignored, while named-model limits reach compaction and survive cold resume.
Attribution metadata still reports 128,000 even when actual compaction reports
8,192; do not infer runtime configuration from that projection. No automatic
Ollama context discovery is assumed; the operator must configure usable server
context at least as large as the declared total budget. Jarvis never changes
server settings autonomously. Actual OpenAI-compatible requests at this pin
contain neither `max_tokens` nor `max_completion_tokens`: the 2,048-token
registered output allowance is a context reservation, not a verified wire
generation cap. This matches the prior uncapped generation behavior; the host
still rejects missing or over-one-million-character final responses. Do not
claim that output metadata alone enforces maximum generated tokens.

The host's UTF-16 estimate and selected-history limits continue to bound
host-provided input and reconstruction. They are not an independent hard
estimator for SDK-generated compacted wrappers. Native working-context
capacity/compaction is delegated to the SDK. Host-selected history hashes still
invalidate copies when authoritative selection or privacy/configuration
changes; compaction does not authorize keeping omitted content.
SQLite facts, approvals, jobs, messages and audit remain independent of summaries.

An already-started SDK compaction is allowed to settle before disposing the
turn-owned runtime, within the existing cancellation/deadline. This is lifecycle
ownership, not manual compaction or summary validation. Outcomes are recorded
as `RuntimeContextCompacted` with success and token metrics only, never private
summary text or checkpoint paths. A failed background compaction is reported
as such; it does not automatically turn a valid assistant answer into failure.

### Accepted upstream limitation

With a genuinely configured 8,192-input-token budget, the controlled runtime
reported successful automatic compaction with 12,617 tokens remaining, then
omitted the oversized summary from the next request. The owner explicitly
directed us to assume normal automatic compaction works and not build a
workaround; a separate owner-led session will prepare the upstream reproduction.
This is accepted dependency reliance, not a claim that the defect is fixed or
that every detail survives summarization. The failure observation remains in
tests/evidence. A6 now verifies supported model-budget propagation, observable
SDK-owned compaction and normal continuity within this accepted limitation,
rather than requiring a Jarvis pre-provider enforcement boundary.

## Historical investigation and corrected interpretation

Actual-runtime contracts prove automatic compaction events and persisted summary
use after resume. The public manual `History.CompactAsync` RPC and context
metadata also work, but the request type is explicitly experimental (`GHCP001`).
An isolated test-only suppression evaluates it; production does not use it.

Its `TokenLimit = 8192` is event attribution metadata, explicitly documented as
not changing how much compaction removes; it is not a requested size target.
The earlier experiment treated it incorrectly as a budget knob. A controlled
144,022-character summary produced `success = true`, with 18,088 current tokens
and a reported 8,192-token limit, and reached the next provider request.
Metadata reports token usage but does not apply the application's
UTF-16/4-plus-20% estimator or its selected-message privacy boundary.
Those observations alone did not evaluate the later-validated named-model
budget. They led to the original blocker, subsequently resolved by the owner's
explicit policy/dependency decision above. Truncation/reconstruction is still
not reported as compaction.

Further public-state experiments show that `SummaryContent` is not the complete
compacted provider message. The runtime wraps it with original user instructions
and additional framing; in the tested single prior exchange, the original
request survives even when its assistant answer is removed. Multi-exchange
retention patterns have not been established by this experiment.
`GetEventsAsync` retains the original user and
assistant events after compaction rather than exposing only active model
context. Context attribution/heaviest-message APIs return counts and truncated
labels, not the full message text in these tested cases. Label truncation,
absence of a content field, positive message-token attribution and the
compaction count are regression-asserted; these metadata shapes do not
establish a hard pre-provider context boundary. Neither the event archive nor the summary
alone is therefore a validated substitute for the complete compacted request.

No decision was inferred from owner unavailability. The accepted adjustment
was made only after the owner's explicit live instructions on 2026-10-10.

## Consequences and limitations

No SQLite schema migration or extraction from private SDK files is required.
SDK copies remain sensitive, replaceable data and are not included in SQLite
backups. An offline restore can leave newer SDK copies, but checkpoint/history
validation prevents their reuse. Cache deletion is not inference replay.

This design reduces prompt reconstruction within the selected history window,
not runtime launch count. Controlled measurements are recorded in the task brief;
they are not universal latency claims. Tool-bearing conversations do not yet
have cross-turn native continuity. Independent review and required quality gates
remain prerequisites for implementation completion.
