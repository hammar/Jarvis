# T03: SQLite persistence, migrations, repositories, backup/restore

## Objective and acceptance

Implement the SQLite persistence slice for T03 / milestone M1. SQLite is the
authoritative application store and Copilot session state remains replaceable.

**Specification:** §§9–11 and §14; backlog T03 in §15. Relevant acceptance
scenarios are “Memory survives runtime session loss,”
and “Crash after job lease claim.” The database is stored outside the deployed
binary directory to preserve durable records during upgrades. Configuration
and credential-reference preservation beyond the database file is not
implemented by this storage task and is not claimed by its tests. The task
originates from issue #4 and depends on completed T02 (#3).

## Scope

- Add embedded, forward-only, versioned SQLite migrations for conversations,
  messages, turns/events, memory facts and FTS5, memory proposals, approval
  requests, action journal, jobs/job runs, notifications, cloud consents, and
  audit events.
- Implement Infrastructure repositories for the existing conversation and
  memory contracts, job lease storage, plus approval, action-journal, and audit
  storage.
- Extend `IConversationStore` with durable turn-state and ordered-event
  operations required by the specification; T02 exposed message operations
  only.
- Add minimal Application persistence interfaces for approval records, action
  journals, and audit events, which T02 did not define. Do not add their
  approval or action use-case policies.
- Add isolated file-backed integration database support, SQLite online
  backup/restore, migration-on-copy validation, and deterministic retention
  for normal conversation history (90-day default) and audit events (30-day
  default), with validated owner overrides.

## Exclusions

No memory conflict/supersede business logic (T08), scheduler/lease worker logic
(T09), approval authorization or action execution logic (T06), UI, real model
or household integration, backup automation, schema downgrade, secure-erasure
claim, or automatic replay of jobs/actions during restore.

## Owned files

`src/PersonalAgent.Application/Contracts.cs`,
`src/PersonalAgent.Infrastructure/Persistence/`,
`src/PersonalAgent.Infrastructure/PersonalAgent.Infrastructure.csproj`,
`src/PersonalAgent.Infrastructure/README.md`,
`src/PersonalAgent.Web/Program.cs`,
`tests/PersonalAgent.TestSupport/IsolatedDatabaseFile.cs`,
`tests/PersonalAgent.IntegrationTests/`,
`docs/architecture.md`, this brief, and a new migration/storage ADR.

## Decisions and assumptions

- Use the existing pinned `Microsoft.Data.Sqlite` package and a small
  hand-written SQL migration runner; do not introduce EF Core. Required FTS,
  compare-and-swap, lease, and journal operations need explicit SQLite
  transactions regardless of mapping choice.
- Use `PRAGMA user_version`, with each migration applied in its own immediate
  transaction; no automatic downgrade.
- Enable foreign keys on each connection and WAL at startup. Persist data
  outside the deployment folder under `JARVIS_DATA_DIR`, falling back to
  per-user LocalApplicationData.
- Conversation/audit retention defaults are 90/30 days, configurable from
  1–3650 days. Retention leaves facts, jobs, active action state, approvals,
  and `Unknown` outcomes intact.
- Restore requires workers and connections to be stopped. SQLite online
  backup, a migrated private copy, integrity checking, and a same-directory
  database replacement make the restore rehearsal repeatable.

## Tests and documentation

Real SQLite integration tests cover fresh migrations and reopen, upgrade from
an older schema with data preservation, FTS insert/update/delete, optimistic
concurrency, approval expiry/single-use ownership checks, expired job-lease
recovery and no-reclaim for completed outcomes, backup/corruption/restore,
pending migration-on-copy and conflicting-schema preservation, and
default/overridden retention including repeat/restart behavior. Infrastructure
must meet the 80% line / 70% branch combined unit+integration coverage minimum.
`SqliteApprovalStore.cs` is explicitly owned as a critical module at 95% line /
90% branch coverage, enforced by the coverage validator and its negative
fixture.

Update `docs/architecture.md` with storage ownership and restore boundaries,
and `src/PersonalAgent.Infrastructure/README.md` with local migration and
backup/restore instructions.

## Completion criteria

- Fresh and older databases migrate transactionally, preserving existing
  records; newer unsupported schema versions fail explicitly.
- Application storage contracts are backed by real SQLite repositories;
  stale compare-and-swap, expired approvals, and expired leases are rejected.
- Backups are consistent, verified, and safely restorable; unknown action
  outcomes are not replayed or deleted.
- Required targeted validation scripts pass, and actual results/limitations
  are reported without claiming hosted CI or live integration behavior.
