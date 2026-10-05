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
Simulator and E2E Web processes require an explicit isolated
`JARVIS_DATA_DIR`; AppHost supplies it for managed profiles. Schema downgrade
is not automatic; restore a backup made for the prior binary. Use
`tools/validate.sh integration` for the real SQLite persistence suite.

Tests: `tools/validate.sh integration`, `tools/validate.sh architecture`, and
`tools/validate.sh coverage`.
