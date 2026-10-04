# ADR 0003: SQLite migrations and consistent backup

## Status

Accepted for T03.

## Context

SQLite is the authoritative store for local conversations, memory, approvals,
actions, jobs, and audit. T02 already pins `Microsoft.Data.Sqlite`, while
Domain/Application must remain independent of SQLite and EF. The initial schema
needs an upgrade path without imposing an ORM migration runtime.

## Decision

Use small hand-written SQL migrations owned by Infrastructure. A
`SchemaMigrations` ledger records each applied version; every schema migration
executes in a transaction and runs in order. Configure foreign keys and WAL on
database connections. Store timestamps as invariant UTC round-trip strings.
Use SQL compare-and-swap updates for versioned facts and records.

Use SQLite's online backup API rather than copying live database files.
Backup and restore validate integrity, migration history, and required schema
objects. Restore first stages and validates the backup, then requires a complete
WAL checkpoint before removing sidecars and atomically replacing the main file.
An unreadable target with a nonempty WAL is rejected rather than risking loss of
committed frames. File and symlink-parent aliases are rejected. The host must
stop workers and close connections before restore; unknown action outcomes
remain durable and are never implicitly replayed.

## Consequences

The schema and migration SQL are explicit and dependency-light, but schema
changes require a new numbered migration and an upgrade fixture. WAL sidecars
are managed/checkpointed by SQLite; file copying is not the live backup
mechanism. Restore's final same-volume rename is atomic; before that rename the
existing main database remains in place, and a failed checkpoint leaves it
untouched. This does not prescribe production backup scheduling, retention,
service supervision, or secure erasure; those remain operations work.

EF Core migrations were not selected because T03 requires no change tracking or
LINQ layer, SQLite is restricted to Infrastructure, and the existing provider
is sufficient for transactions, FTS5, and the online backup API.
