# ADR 0003: SQLite migration and recovery strategy

## Status

Accepted for T03.

## Context

The application uses one local SQLite file and requires versioned schema
upgrades, foreign keys, WAL, FTS5, optimistic concurrency, job-lease updates,
action/approval journaling, and consistent backup/restore. T02 already pins
`Microsoft.Data.Sqlite`; no EF Core entities, conventions, or migrations exist.

## Decision

Use a small first-party migration runner with embedded, ordered SQL scripts and
SQLite `PRAGMA user_version`. Apply each migration in an immediate transaction
and fail startup when the database schema is newer than the application. There
is no automatic downgrade. Keep migrations, repositories, backup/restore, and
SQLite-specific behavior in Infrastructure.

Enable foreign-key enforcement per connection and WAL at startup. Use SQLite's
online backup facility; validate integrity and foreign keys, migrate a private
copy, then replace the live file only after copy validation succeeds. Restore
is an offline operation: the owner stops the Web host and workers first.
Actions with `Unknown` outcomes remain durable and are never automatically
replayed.

Use optimistic compare-and-swap updates for versioned facts, approval requests,
and action journal state. Keep job due-work leasing as an atomic persistence
primitive; scheduler policy remains in T09.

## Rationale and consequences

Explicit SQL keeps SQLite transaction and concurrency semantics visible and
avoids introducing ORM packages/model conventions for a single embedded
provider. FTS5 triggers and backup/restore still require SQLite-specific
operations if an ORM is added later. The runner is intentionally narrow: the
project must maintain migration ordering, transaction boundaries, compatibility
fixtures, and upgrade-on-copy tests. A future EF Core adoption must explicitly
bridge the existing `user_version` history rather than assuming generated
migrations can replace it in place.

## Evidence

T03 integration tests exercise fresh schema creation, an older-schema upgrade
with data, FTS synchronization, optimistic concurrency, WAL, backup/restore,
integrity checks, and migration-on-copy.
