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
Migrations are forward-only and each version applies transactionally.

The owner can set `JARVIS_CONVERSATION_RETENTION_DAYS` (default 90) and
`JARVIS_AUDIT_RETENTION_DAYS` (default 30); each must be an integer from 1 to
3650. Startup removes expired conversations/messages/turn events and audit
events only. It does not delete durable facts, jobs, approval/action journals,
or `Unknown` outcomes.

`SqliteBackupRestoreService` exposes SQLite-consistent backup, restore, and
migration-on-copy validation. Create backups to a new file; an existing backup
is never overwritten. For restore, stop the Web host and every worker, validate
the backup and its migrated copy, then replace the live database. Restore
never replays jobs or uncertain physical actions. Schema downgrade is not
automatic; restore a backup made for the prior binary. Use
`tools/validate.sh integration` for the real SQLite persistence suite.

Tests: `tools/validate.sh integration`, `tools/validate.sh architecture`, and
`tools/validate.sh coverage`.
