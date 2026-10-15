# PersonalAgent.Infrastructure

Infrastructure owns SQLite persistence and external-system adapters. It
depends on Application and Domain; `Microsoft.Data.Sqlite` and SQLite types
remain inside this project. GitHub Copilot SDK types are confined to
`AgentEngine/Copilot/`.

## Home Assistant adapter

`HomeAssistant/HomeAssistantRestClient` uses only approved state reads and
fixed light/climate service calls. It never updates `/api/states` to control
devices. `HomeAssistantConfiguration` supplies explicit synthetic defaults
only for Simulator/E2E; live catalogs are administrator-authored JSON.
See [configuration](../../docs/development.md#home-assistant-configuration).

The adapter repeats catalog/bounds checks before HTTP. Its client disables
redirects and generic retries; safe reads alone get one transient retry within
one shared deadline. Transient transport errors are connection failures, ended
responses and typed reset/aborted/timed-out socket causes; TLS, authentication,
protocol and unclassified errors are not retried. Exact approvals bind the normalized installation endpoint
and named credential reference as well as the complete catalog. Tokens resolve
server-side through `ISecretResolver`.
The same opaque identity binds interrupted-write observation; changing it
preserves Unknown without observing the replacement installation.
Remote error bodies, headers and exception payloads are not returned or logged;
state parsing is bounded and projects only typed fields. Unauthorized, unknown
entity, invalid response, transport and deadline failures have safe typed codes.
Snapshots separately carry availability and source-age staleness.

Service acceptance is not physical success. A possibly submitted write is
Unknown and can only be observed, never automatically resent. Known pre-submission
credential/deadline/cancellation failures return a definitive not-submitted
result. Unsuccessful service HTTP responses, including 400 and authorization
errors, do not prove rollback; they remain Unknown with distinct safe reasons,
followed only by observation. Verification compares target power/brightness or thermostat setpoint, not room temperature,
and cannot establish which command caused an observed state. Security devices,
wildcards, generic services, switches, covers and WebSockets are unsupported.
Tests: `tools/validate.sh integration` and `tools/validate.sh aspire-e2e`.

## SQLite migrations and local use

`SqliteConfirmedMemoryStore` is the production owner-scoped memory adapter.
Migration 007 adds provenance, distinguishing context, action-bound commit
receipts, deletion tombstones and journal dependencies. Approved mutations
validate versions/conflicts/expiry under an immediate transaction. Recovery
observes the receipt without reapplying. Derived responses/drafts are
invalidated on change/deletion, including results published after a racing
deletion; see [ADR 0006](../../docs/adr/0006-confirmed-memory.md).
The unscoped T03 `SqliteMemoryStore` remains a low-level compatibility fixture,
not a registered production mutation API. Tests:
`dotnet test tests/PersonalAgent.IntegrationTests --filter FullyQualifiedName~ConfirmedMemoryTests`.

The Web composition root initializes the database before serving requests.
Set `JARVIS_DATA_DIR` to a writable directory outside the deployment folder;
the database is `jarvis.db` inside it. If unset, the Web host uses the
per-user LocalApplicationData `Jarvis` directory. Startup enables WAL and
applies the embedded SQL migrations in order using SQLite `user_version`.
Migration 003 adds nullable client request identity/fingerprint columns and a
per-conversation unique request key, preserving existing turn rows. It also
enforces at most one user and one assistant message per turn. Submission
persists the received turn, user message, request identity, and initial event
in one transaction; terminal completion persists its answer, status, and event
atomically.
Migration 004 adds the single-owner password-verifier row and constrained
owner retention settings. It does not store plaintext passphrases or
credentials. The passphrase hash is produced by ASP.NET Identity in Web; owner
settings remain Application contracts backed here by SQLite.
Migration 005 adds the manual-title provenance flag without changing existing
titles or UTC creation instants. The first accepted user message can set a
normalized automatic display title atomically with the turn; owner rename is
owner-scoped and durably prevents later automatic replacement.
Migration 006 adds nullable `actions.outcome_json`. The complete terminal
application response is stored once with its status/version in the same CAS
transaction. Completion events reference the action ID; no payload is copied
into events/messages. Old rows remain explicitly status-only. Result JSON is
bounded to 1,000,000 characters and reasons to 256 without truncation. Outcomes
share action retention and backups, not conversation/event cleanup; see
[ADR 0005](../../docs/adr/0005-single-copy-tool-outcomes.md).
If only the previous AppHost `PersonalAgent` default contains a database, the
Web host continues using that location; if both defaults contain a database,
configure `JARVIS_DATA_DIR` explicitly.
Migrations are forward-only and each version applies transactionally. On
macOS/Linux, the data directory must be mode `700` or stricter; startup rejects
an existing directory accessible to group or other users and restricts the
database, WAL/SHM sidecars, and recovery lock to mode `600`. New data
directories and backup destinations are created owner-private. Keep custom
Windows data directories under a per-user location owned by the current
account; access entries may grant the current account, SYSTEM, and built-in
Administrators only. New Windows data directories use that protected ACL, and
existing directories granting other identities are rejected.

The owner can set `JARVIS_CONVERSATION_RETENTION_DAYS` (default 90) and
`JARVIS_AUDIT_RETENTION_DAYS` (default 30) as trusted first-run defaults; each
must be an integer from 1 to 3650. Owner-edited values persist in SQLite and
take precedence over later configuration defaults. Startup removes expired
conversations/messages/turn events only when they have no unresolved turns;
expired audit events are removed independently. Cleanup runs at startup,
hourly using the latest persisted values, and in the same SQLite transaction
as a retention-settings update.
Running, approval-waiting, and interrupted turn state is retained for recovery.
It does not delete durable facts, jobs, approval/action journals, or
`Unknown` outcomes.
This includes stored terminal result bodies: trimming conversation history
does not delete action/idempotency evidence. Their cumulative storage can grow;
there is no new automatic action-pruning or payload-logging feature.

`SqliteBackupRestoreService` exposes SQLite-consistent backup, restore, and
migration-on-copy validation. Create backups to a new file; an existing backup
is never overwritten. Backup opens the live database read-only and fails if it
is missing rather than creating an empty source. For restore, stop the Web
host and every worker, validate the backup and its migrated copy, then replace
the live database. A durable restore marker and rollback snapshot recover the
previous committed database state (including committed WAL data) after a
failed or interrupted replacement; startup completes that recovery under a
cross-process file lock before opening SQLite. Rollback database/WAL snapshots
are flushed before the prepared marker is durably published; directory changes
are flushed around marker publication and database replacement on macOS/Linux.
On Windows, durable file renames use `MoveFileExW` with
`MOVEFILE_WRITE_THROUGH`; old live database sidecars are durably renamed out of
the way before replacement, and cleanup remains recoverable from the marker.
Backup and
restore reject live database sidecars and reserved recovery filenames,
including the staging database's recovery marker, lock, and rollback names,
live/staged rollback journals, symlink aliases, and case variants on Windows
and macOS. Claims refresh the caller's time under the write transaction before
calculating the lease deadline, so write-lock waits cannot consume the lease.
Lease completion checks the current clock only after acquiring its write
transaction, so waiting for another writer cannot authorize an expired lease.
Restore never replays jobs or uncertain physical actions.
Hot rollback journals are snapshotted and retired before replacement, and
restored only with the original database during rollback. They cannot replay
old database pages over a successful restore.
Backup destinations with existing WAL/SHM/rollback journals are rejected
without modification; the caller must exclusively own the destination and
sidecar namespace during backup. Unix database paths resolve symlinks before
SQLite connections and recovery artifacts are configured, preserving the
target's WAL and leaving the database-file alias intact during restore.
Simulator and E2E Web processes require an explicit isolated
`JARVIS_DATA_DIR`; AppHost supplies it for managed profiles. Schema downgrade
is not automatic; restore a backup made for the prior binary. Use
`tools/validate.sh integration` for the real SQLite persistence suite.

## Copilot agent engine

`AgentEngine/Copilot/CopilotAgentEngine` implements the Application-owned
`IAgentEngine` without allowing SDK types to cross the Infrastructure adapter
namespace. The caller supplies the selected provider, host instructions,
already-selected context, exact tool catalog, deadline, and maximum tool-call
count. The adapter does not route, approve context, or authorize a tool;
callbacks always pass through the host's `IToolDispatcher`.

M2-01 carries the authenticated owner and current request from the accepted
host context through each callback. `ToolDispatcher` validates registered
inputs, applies the exact host allowlist, journals action intent, and routes
approval/verification through Application services. M2-02 registers the initial home tools when Home Assistant is configured,
using exact permissions derived from its validated catalog. Chat orchestration
and approval UI remain M2-04 scope. Controlled HTTP tests establish adapter/
dispatcher integration, not actual device behavior.

The adapter uses `LocalChatPrompt` for selected history/current-task framing
and the supported `OnUserPromptTransformed` hook to keep the model-facing prompt
identical to the application-owned text. Runtime-added date/XML framing caused
reproducible refusals in native Llama chat; opaque provenance packets also
obscured the current task. No runtime/private transcript files are seeded and
no real prompts are logged. See the
[prompt reliability investigation](../../docs/tasks/local-chat-prompt-reliability.md)
and [live comparison commands](../../docs/testing.md#local-chat-model-comparison).

Configure `CopilotAgentEngineOptions` from trusted host configuration. Set
`RuntimeDirectory` beneath a private application data directory; local and
cloud provider settings each require an explicit OpenAI-compatible `Model` and
`BaseUrl`. Local endpoints must be loopback. Non-loopback cloud endpoints must
use HTTPS. `WireApi` is optional. Use `ApiKeyReference` and an
application-owned `ISecretResolver` for provider keys; resolved key values are
passed only to the SDK and never enter the child-process environment, events,
or logs. A configured key reference without a registered resolver fails the
turn explicitly.

Every turn starts a fresh SDK client/runtime. Cloud and tool-bearing turns
create fresh sessions in `<RuntimeDirectory>/turn-<TurnId>/`. Tool-free Local
turns retain private working state in
`<RuntimeDirectory>/conversation-<ConversationId>/local/` and resume only when
the Completed SQLite checkpoint, selected history and compatibility hash match.
Otherwise bounded application context reconstructs a fresh session without
replaying earlier inference. Create and resume both use explicit streaming,
empty SDK mode, logged-in-user discovery disabled, cross-session store/search
disabled, and a sanitized environment. `EnableSessionStore = false` does not
disable native disk persistence. Tool-free Local sessions use the pinned
experimental named-model API to propagate 8,192 input / 2,048 output tokens and
enable SDK-owned automatic compaction at 80%/95%. The ordinary capability
override is ignored at this pin; attribution metadata can report 128,000 while
actual compaction correctly uses the registered budget.
No manual compactor or request gateway is introduced. Already-started background
compaction settles before turn-owned runtime disposal, under the same deadline.
`RuntimeContextCompacted` persists only outcome/token metrics.
The [lifecycle ADR](../../docs/adr/0004-local-copilot-working-state.md)
records the owner's accepted dependency limitation and policy distinction.
Only request-registered custom tools are exposed. The adapter streams typed
events and appends each to `IConversationStore`, which assigns an atomic,
monotonic per-turn sequence for later event replay. Terminal status changes and
their terminal event are committed together through
`IAtomicTurnOutcomeStore`, using the persisted turn version; a stale or
already-terminal update fails rather than overwriting another outcome.

The host deadline bounds a send even when the SDK runtime dies without
completing its wait. Cancellation/deadline requests abort the active session
and stop the turn-owned runtime with bounded cleanup. A process crash or
cleanup failure is `Interrupted`, provider failure is `Failed`, and explicit
cancellation is `Cancelled`; an ambiguous physical result remains the
dispatcher’s responsibility. This design uses the accepted native trusted
runtime from ADR 0001 and does not claim OS-enforced egress containment.
Observed-event and caller-output channels are bounded to 128 events each, and
each turn accepts at most 10,000 events and 1,000,000 streamed UTF-16 code
units. Reaching either the channel capacity or turn event budget cancels the
runtime and persists an `event_budget_exceeded` failure rather than dropping
text silently. Terminal outcomes are persisted before publishing to the caller;
if the caller stops reading, delivery is cancelled without blocking turn
shutdown, and the durable event remains available for replay. A slow reader
cannot block terminal delivery indefinitely. Cancellation or deadline expiry
while acquiring the durable turn claim also persists a terminal outcome using
the pre-claim version, so a competing engine's claim cannot be overwritten.
Disposable turn directories are removed after bounded runtime shutdown.
Successful tool-free Local state is retained, bounded to 32 conversation roots
and 30 idle days; startup/hourly maintenance removes idle/orphan copies without
inference. Incomplete shutdown retains ownership and prevents unsafe deletion
or same-conversation reuse. A removal failure is surfaced as cleanup uncertainty. Terminal
outcome persistence has its own bounded timeout so turn cancellation does not
prevent recording the terminal result.
Event persistence observes the turn cancellation/deadline token. Runtime
force-stop and disposal share one in-flight shutdown operation; disposal and
runtime-directory removal do not race a force-stop that exceeded its wait
bound.
The adapter is not yet an end-user conversation flow: routing, consent,
dispatcher policy, UI/SSE, and coordinator integration are later task scope.

Tests: `tools/validate.sh integration`, `tools/validate.sh architecture`,
`tools/validate.sh sdk-contracts`, and `tools/validate.sh coverage`.
