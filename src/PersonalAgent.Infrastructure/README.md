# PersonalAgent.Infrastructure

Infrastructure owns local SQLite schema initialization, versioned migrations,
repositories, FTS indexing, retention, and online backup/restore. It depends on
Application and Domain. SQLite types remain within this assembly; Copilot SDK
types remain under `AgentEngine/Copilot/`.

## Run migrations locally

Create a `SqliteDatabase` for the intended persistent path and await
`MigrateAsync()` during trusted host startup:

```csharp
var database = new SqliteDatabase(databasePath);
await database.MigrateAsync(cancellationToken);
```

The migration runner creates the parent directory, enables WAL and foreign-key
checks for connections, and applies each numbered migration transactionally.
Do not point development commands at household data. To exercise migrations on
a fresh isolated file, run:

```sh
tools/validate.sh integration
```

`BackupAsync` uses SQLite's online backup API. Before calling `RestoreAsync`,
stop workers and close all connections to the target database. The method
validates migration history and required schema, stages the backup, checkpoints
the current WAL, and atomically replaces the main file. An unreadable target
with a nonempty WAL is rejected because it cannot safely establish that
committed transactions are captured. File and symlink-parent aliases are
rejected. Unknown action outcomes are persisted as journal state; restore does
not dispatch or replay actions.

## Tests

Run `tools/validate.sh integration` for real-file migration, persistence,
concurrency, FTS, upgrade, journal, retention, and restore tests. Run
`tools/validate.sh build` for Release analyzers and formatting. The Copilot
runtime contract tests are separate and do not validate SQLite behavior.
