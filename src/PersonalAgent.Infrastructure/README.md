# PersonalAgent.Infrastructure

Infrastructure owns SQLite persistence and external-system adapters. It
depends on Application and Domain; `Microsoft.Data.Sqlite` and SQLite types
remain inside this project. GitHub Copilot SDK types are confined to
`AgentEngine/Copilot/`.

## SQLite migrations and local use

The Web composition root initializes the database before serving requests.
Set `JARVIS_DATA_DIR` to a writable directory outside the deployment folder;
the database is `jarvis.db` inside it. If unset, the Web host uses the
per-user LocalApplicationData `Jarvis` directory. Startup enables WAL and
applies the embedded SQL migrations in order using SQLite `user_version`.
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
`JARVIS_AUDIT_RETENTION_DAYS` (default 30); each must be an integer from 1 to
3650. Startup removes expired conversations/messages/turn events only when
they have no unresolved turns; expired audit events are removed independently.
Running, approval-waiting, and interrupted turn state is retained for recovery.
It does not delete durable facts, jobs, approval/action journals, or
`Unknown` outcomes.

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

Configure `CopilotAgentEngineOptions` from trusted host configuration. Set
`RuntimeDirectory` beneath a private application data directory; local and
cloud provider settings each require an explicit OpenAI-compatible `Model` and
`BaseUrl`. Local endpoints must be loopback. Non-loopback cloud endpoints must
use HTTPS. `WireApi` is optional. Use `ApiKeyReference` and an
application-owned `ISecretResolver` for provider keys; resolved key values are
passed only to the SDK and never enter the child-process environment, events,
or logs. A configured key reference without a registered resolver fails the
turn explicitly.

Every turn starts a fresh SDK client and session in
`<RuntimeDirectory>/turn-<TurnId>/`, with empty SDK mode, logged-in-user
discovery disabled, session storage disabled, and a sanitized environment.
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
text silently. Cancellation or deadline expiry while acquiring the durable
turn claim also persists a terminal outcome; a duplicate claim does not alter
the active owner's turn.
The per-turn runtime/workspace directory is removed after bounded runtime
shutdown; a removal failure is surfaced as cleanup uncertainty. Terminal
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
