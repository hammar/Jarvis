# T03: SQLite persistence, migrations, repositories, backup, and restore

## Objective and acceptance

Implement M1 durable SQLite state owned by the application. This task covers
specification §§9–11, §14 restart/memory/job-recovery scenarios, and backlog
task T03. It must demonstrate clean initialization, persistence across reopened
connections, optimistic concurrency, FTS retrieval, migration upgrades,
consistent backup/restore, and deterministic retention.

## Scope

- Add versioned SQLite migrations for conversations/messages/turns/events,
  memory facts/proposals, approval/action journals, jobs/runs, notifications,
  cloud consents, and audit events.
- Implement the T02 conversation, memory, and job storage contracts, plus
  storage-only journal operations for approvals, actions, and audit.
- Provide SQLite online backup and validated restore primitives.
- Add fresh-file test support and real SQLite integration tests.
- Delete ordinary conversation history after the configured retention period
  (90 days by default) and audit events after 30 days. Retain facts, jobs,
  action state, and unknown outcomes.

## Exclusions

No memory proposal/business rules, approval authorization, action dispatch,
scheduling worker, UI, production backup automation, or live household/cloud
integration. Restore must be performed with workers stopped by the caller.

## Owned files

`src/PersonalAgent.Infrastructure/Persistence/`, Infrastructure package/README,
`tests/PersonalAgent.IntegrationTests/`, `tests/PersonalAgent.TestSupport/`,
`docs/architecture.md`, `docs/testing.md`, `docs/adr/`, and this brief.

## Dependencies and decisions

- Depends on completed T02 contracts and architecture boundaries.
- Uses the existing pinned `Microsoft.Data.Sqlite` package and hand-written
  versioned SQL migrations, avoiding an additional ORM/tool dependency. Each
  migration is applied transactionally and recorded in the database.
- UTC instants are stored as invariant round-trip strings. SQLite is local,
  single-host authoritative state; WAL and foreign keys are enabled.
- Application interfaces remain free of provider/SQLite types. Journal
  primitives are Infrastructure-owned until a consuming use-case contract is
  defined in its own task.

## Tests and documentation

Run `tools/validate.sh integration`, `tools/validate.sh build`,
`tools/validate.sh coverage`, and `tools/validate.sh docs`. Integration tests
use isolated real SQLite files and cover empty migration, restart, compare and
swap conflict, FTS, upgrade from an earlier migration, backup/restore integrity,
and repeatable controlled-clock retention. Update architecture/storage ownership,
scenario mapping, migration rationale, and Infrastructure local migration
instructions.

## Completion criteria and status

- Required schemas, repositories, migration and recovery primitives are
  implemented without leaking SQLite dependencies into Domain/Application.
- Real SQLite integration scenarios pass; Infrastructure meets the configured
  coverage gate.
- **Status: implementation complete.** Local validation on 2026-10-04:
  - `tools/validate.sh build` — Release build, analyzers, and formatting passed.
  - `tools/validate.sh unit` — 7 tests passed.
  - `tools/validate.sh integration` — 18 real SQLite/Web integration tests passed.
  - `tools/validate.sh architecture` — 6 boundary tests passed.
  - `tools/validate.sh sdk-contracts` — actual pinned runtime contracts and the
    SDK package contract test passed.
  - `tools/validate.sh coverage` — Infrastructure 99.3% lines / 93.6% branches;
    overall thresholds, reports, and discovery passed.
  - `tools/validate.sh docs` — internal Markdown links passed.

The Web composition root and T06/T08/T09 use cases are not wired to the new
stores by this task; those integrations remain follow-up work. No production
backup automation or live household-data validation is claimed.
