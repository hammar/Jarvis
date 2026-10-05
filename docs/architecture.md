# Architecture and boundaries

## Runtime ownership

The C# host owns user identity, route policy, privacy/context selection,
approval, tool authorization, durable conversations/jobs/audit, cancellation,
and outcome reporting. The Copilot runtime is a replaceable inference engine,
not the authority for application state. SQLite is the durable store;
schema and repository implementation is delivered by T03.

```mermaid
flowchart TD
  AppHost["Aspire AppHost"] --> Web["PersonalAgent.Web"]
  Web --> Application["Application contracts and use cases"]
  Web -. composition only .-> Infrastructure["Infrastructure adapters"]
  Application --> Domain["Domain IDs and state types"]
  Infrastructure --> Application
  Infrastructure --> Domain
  Web --> Defaults["ServiceDefaults"]
  AppHost --> FakeModel["Controlled simulator model"]
  AppHost --> FakeHA["Controlled simulator Home Assistant"]
```

## Project boundaries

| Project | Responsibility | Internal dependencies |
| --- | --- | --- |
| Domain | Strong IDs and core state types | None |
| Application | Frozen use-case and adapter contracts, typed events | Domain |
| Infrastructure | SQLite persistence and provider/device adapters | Application, Domain |
| Web | Razor host and composition root | Application, Domain, ServiceDefaults; Infrastructure only in composition |
| ServiceDefaults | Health, service discovery and filtered local telemetry | No application projects |
| AppHost | Aspire resource graph and trusted profiles | Web and simulator resource references only |
| Test projects | Verification and deterministic fixtures | Tested projects and TestSupport only |

Domain/Application do not depend on ASP.NET Core, Aspire, SQLite/EF, Copilot or
provider SDKs. In production, all `GitHub.Copilot` types and package
references are restricted to `Infrastructure/AgentEngine/Copilot`; the
separate `tools/sdk-validation` feasibility harness is the documented
pre-T02 exception. Web request handlers must call Application contracts
rather than raw database, provider or Home Assistant clients. The architecture
tests enforce direct project edges, compiled assembly/type dependencies,
Copilot adapter namespaces, and the Web composition exception.

## Turn and privacy model

The application owns a stable `TurnId` independent of Copilot runtime session
IDs. Events use UTC observation times and typed outcomes. `Interrupted` is not
success: a child process stopping or a request timing out after a side effect
must not be described as a confirmed failure or automatically retried.

Model proposals are untrusted data. A future dispatcher validates typed
arguments, applies host policy, journals before writes, requires exact consent
where specified, and reports uncertain physical outcomes explicitly. Cloud
context must be a minimal inspectable packet bound to the chosen provider and
consent; neither the Local profile nor an SDK flag is an air-gap claim.

T04's `CopilotAgentEngine` creates a fresh SDK client/session and dedicated
runtime/work directory for each turn. It uses empty SDK mode, explicit
provider configuration, disabled logged-in-user discovery, a sanitized child
environment, and only the caller's registered tool catalog. Every tool
callback goes through `IToolDispatcher`; the engine does not provide tool
authorization. Typed events are appended through `IConversationStore` before
they are yielded, so persistent per-turn sequence numbers are the replay
cursor. Terminal status and its terminal event are committed atomically using
the stored expected version. Deadlines
bound SDK waits and abort/stop are bounded; timeout and process cleanup
uncertainty are reported as interruption. The per-turn SDK runtime/workspace
directory is removed after shutdown, with cleanup failures surfaced as
interruption. Terminal persistence has a separate bounded timeout so
cancellation cannot skip the durable outcome. The native runtime remains a
trusted dependency under ADR 0001, not an OS-isolated process.

## Aspire resource ownership

AppHost owns only processes it launches. The Simulator/E2E profiles start Web
and deterministic model/HA fixture processes with random managed endpoints.
They explicitly clear external provider and Home Assistant endpoint/secret
references for Web rather than forwarding developer credentials.
Simulator data is persistent beneath the user's application data directory;
E2E requires a unique, test-owned temporary directory. AppHost never stops or
deletes external Ollama, Home Assistant, or real developer data. SQLite is a
file, not a database container.

## SQLite storage ownership

`PersonalAgent.Infrastructure.Persistence` owns the SQLite file, forward-only
schema migrations, FTS5 synchronization, and implementations of the
Application storage contracts. Conversation storage includes messages, turns,
and ordered event cursors; memory, jobs, approvals, action journals, and audit
use separate storage operations. Web composition selects `JARVIS_DATA_DIR` (or
the per-user LocalApplicationData `Jarvis` directory when unset). It reuses
the previous AppHost `PersonalAgent` location only when that is the sole
default database; if both default locations contain data, startup requires an
explicit path. AppHost leaves the Local/Hybrid fallback to Web so both launch
modes resolve the same store. The database file is `jarvis.db`, outside the
deployment folder. `Microsoft.Data.Sqlite` is confined to Infrastructure and
integration tests. Domain and Application remain independent of SQLite.

Migrations use SQLite `user_version` and apply each embedded SQL migration
inside its own immediate transaction. Foreign keys are enabled per connection;
WAL is enabled during startup. Apply pending upgrades to a SQLite backup copy
before swapping application binaries. Schema downgrade is not automatic: a
rollback restores a matched backup and prior binary.

`SqliteBackupRestoreService` uses SQLite's online backup facility and verifies
integrity plus foreign keys. Restore first copies the backup, migrates and
validates that copy, then replaces the database file. Stop the Web host and
all workers before restoring; no database connection or active worker may
remain open. Restore does not run jobs or replay an action, and preserves
recorded `Unknown` action outcomes. Backups can retain logically deleted data;
SQLite file-page secure erasure is not claimed.

The Web host applies validated retention settings at startup:
`JARVIS_CONVERSATION_RETENTION_DAYS` defaults to 90 days and
`JARVIS_AUDIT_RETENTION_DAYS` defaults to 30 days (each accepts 1–3650 days).
Cleanup deletes old conversation roots and their dependent messages/turn
events, and expired audit events. It does not purge durable memory facts,
jobs, actions, approvals, or uncertain outcomes.
