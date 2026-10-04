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
Backup/restore validates the resulting database with `PRAGMA integrity_check`
before replacing the target file. The host must stop workers before restore;
unknown action outcomes remain durable and are never implicitly replayed.

## Consequences

The schema and migration SQL are explicit and dependency-light, but schema
changes require a new numbered migration and an upgrade fixture. WAL sidecars
are managed by SQLite; file copying is not the live backup mechanism. This does
not prescribe production backup scheduling, retention, service supervision, or
secure erasure; those remain operations work.

EF Core migrations were not selected because T03 requires no change tracking or
LINQ layer, SQLite is restricted to Infrastructure, and the existing provider
is sufficient for transactions, FTS5, and the online backup API.
