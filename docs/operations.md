# Operations

## Memory deletion and recovery

M2-03 registers local memory use cases/tools, but memory UI, HTTP operations
and chat tool-catalog wiring arrive in M2-04. A proposal is not a saved fact:
the exact change needs the owner's Approve/Reject decision. Editing it or
changing its conflicting versions requires new review. Host code can complete
an already-approved proposal after restart without an active model callback.

Migration 007 is additive. Durable facts survive transcript/audit retention
and disposable SDK loss; removed source messages are explicitly unavailable.
New facts are LocalOnly. Existing Private facts remain local-readable, while
unknown classifications fail closed.

Deleting a fact removes active content and FTS entries, redacts dependent
proposal/journal payloads and retains content-free version/action evidence.
Old calls report `memory_result_invalidated`; do not reconstruct their old
arguments or retry them to recover a payload. Changes also invalidate stale
derived evidence. Unrelated action outcomes retain exact replay.

Original user/assistant messages remain under conversation retention, and
backups can contain older facts until removed under the backup policy.
Logical deletion does not securely erase SQLite/WAL pages or already
delivered answers. Offline restore can restore older facts/deletion state:
restore a deliberately selected matched backup, not a presumed privacy purge.
See [ADR 0006](adr/0006-confirmed-memory.md).

## Host operation

Aspire AppHost is a development orchestrator, not a production supervisor or
always-on macOS service manager. Keep its dashboard local and authenticated.
Use Simulator for credential-free development. Local and Hybrid require
explicit trusted local endpoint/model configuration; use protected named
secret references rather than embedding credentials in URIs or source control.
AppHost does not own external Ollama, Home Assistant, or personal data.

Home Assistant configuration is opt-in and preserved independently of binaries;
see [settings](development.md#home-assistant-configuration). Live automatic light
writes and thermostat are off by default. Do not enable automatic light writes
before the spec's model-evaluation prerequisite. A service acceptance response
is not device verification; Unknown/accepted-unconfirmed outcomes are not a
reason to blindly resend. Restore preserves the existing action journal and
must never replay uncertain writes. A matching observation establishes current
state, not which request caused it. Stale readings retain their source timestamp.
Interrupted home actions also bind their original connection/catalog observation
scope. An endpoint/reference/catalog change, or a legacy action without that
scope, remains Unknown without querying a replacement installation. Retain the
original journal; configuration changes are not evidence of physical success.
Service HTTP errors (including 400) may be returned after a device effect and
are not proof of rejection without application. Jarvis observes them without
resubmission; an unchanged/unavailable state remains Unknown and blocks an
equivalent write in that turn. Correct known local configuration/credential
failures before submission rather than assuming a remote error rolled back.

Optional live HA smoke evaluation is outside required CI and has not been run
by M2-02 automation. Only after explicit owner consent, configure the endpoint,
protected reference and a minimal read-only catalog in an isolated direct host;
keep automatic lights/thermostat disabled. Check a selected sensor through the
application client against its source timestamp/unit and stop on unauthorized,
unknown, stale or transport errors. Do not add real tokens to simulator fixtures.
Physical write evaluation requires separate consent, exact target/bounds,
baseline/expected observation and owner-controlled restoration; timeout or
uncertainty means stop and observe, never automatically retry. Remove test
configuration and stop only test-owned processes afterward. This guidance is
not a claim of implemented live smoke tooling or required manual certification.

The Web host initializes SQLite migrations before accepting work. A stable
client request ID is durable per conversation; same-request retries return
the original turn, including after restart, while conflicting reuse is
rejected. The coordinator accepts at most four executing turns and 20 queued
turns, with a 120-second default end-to-end interactive deadline beginning at
durable acceptance and a validated trusted override ceiling. Queue wait,
routing, context construction, and inference consume the same time budget. It
does not automatically retry an
interrupted inference. On startup, nonterminal turns are persisted as
interrupted and remain available for review; they are not replayed.

Migration 006 preserves existing rows and adds complete terminal tool responses
to the action journal. Newly recorded responses replay exactly; old status-only
rows cannot regain missing payloads/reasons. Tool-completion events reference
the action ID, without another result copy or extra conversation message.
Outcomes are private local application data, included in SQLite backups, not
diagnostic/telemetry output. They follow existing durable action retention:
conversation/event cleanup does not purge them, and cumulative storage may grow.
Result JSON is limited to 1,000,000 characters and reason codes to 256; invalid
or oversized persistence input fails explicitly, never truncates. A failed
post-send commit remains uncertain and must not be blindly retried. Restore
requires a schema-compatible binary/backup pair; no automatic downgrade.

Tool-free Local SDK copies live under private
`conversation-<ConversationId>/local/{runtime,workspace}` roots beneath the
configured runtime directory. They are sensitive working copies, not the
authoritative conversation store or part of SQLite backups. Do not edit private
SDK transcripts. A missing/corrupt/incompatible checkpoint or changed selected
history reconstructs from bounded SQLite context; missing native state during
pre-inference resume also reconstructs and processes the current request once.
No uncertain inference or completed tool action is retried. Cloud/tool turns
start fresh. Tool-free Local sessions delegate automatic compaction to the SDK.
The configured model budget is 8,192 input + 2,048 output tokens. Ensure Ollama's
actual context allocation is at least 10,240 tokens (app/server context settings
or a model's `num_ctx`); inspect it with `ollama ps`. The 2,048 output tokens
are an SDK context reservation, not a verified wire generation cap. The OpenAI-compatible
endpoint does not set this allocation for Jarvis. No server settings are changed
automatically. A model's theoretical maximum is not its current allocation.
SDK compaction can lose detail; the observed oversized-summary/truncation defect
is explicitly accepted for upstream follow-up, not claimed fixed. SQLite history
survives. SDK-reported failures and size metrics are persisted as
`RuntimeContextCompacted`, without summary text or private paths.
See [ADR 0004](adr/0004-local-copilot-working-state.md).

The cache retains at most 32 conversation roots, with a 30-day idle limit.
Startup and hourly maintenance remove idle/orphan engine copies without
inference; an owner retention update is reflected by the next maintenance pass,
not synchronously by the settings request. Active or unresolved runtime shutdown
paths are not pruned. Startup engine-maintenance failure stops startup; hourly
failures use the existing degraded health/error reporting below. Startup failures
log only the error type and propagate a sanitized exception without private
filesystem paths or inner exception content. Removing SDK
copies after stopping the host does not delete SQLite conversations, but the next
turn must reconstruct its selected history. Ready-checkpoint persistence failure
is explicitly Interrupted (`session_checkpoint_failed`) without committing an
assistant answer, even if partial text was streamed.

During graceful shutdown the coordinator stops accepting work, interrupts
queued turns, and asks the engine to stop active turns within a bounded
deadline. Persistence or shutdown timeout failures are surfaced and make
readiness unavailable; shutdown never reports uncertain work as completed.
The shared cleanup operation starts its 15-second shutdown budget before it
awaits cancellation callback publication; synchronous cause selection and
asynchronous publication scheduling happen first. Acceptance and the queue
close; held callbacks do not hold the lifecycle lock or block readiness.
Timeout or caller cancellation ends the
shutdown wait explicitly, not the owned cleanup tasks. Resources used by a
held callback stay alive until it finishes; do not treat a returned timeout as
proof that runtime cleanup completed. Owner cancellation likewise bounds its
publication/cleanup wait with the supplied token while the selected cancellation
and pending terminal persistence continue independently.
`/health/ready` reports migration/policy/runtime readiness and local provider
configuration separately from model connectivity, which is not probed.
It also includes `history-cleanup`: hourly maintenance failures are reported
as degraded until the next successful pass. Maintenance retries once at each
scheduled hourly pass rather than stopping the host or retrying in a tight
loop. Logs contain only the failure type and interval, not private exception
messages. Degraded readiness is HTTP 200 with explicit JSON health status;
unhealthy runtime/database readiness remains HTTP 503. Persistent cleanup
degradation means history can remain beyond its configured retention window;
inspect local storage availability and permissions rather than assuming data
was deleted. Shutdown cancellation is not handled as a maintenance failure.
The database result is based on a live SQLite query and the expected migration
version, not only the startup initialization result. Simulator and E2E profiles
use the Aspire-discovered controlled model endpoint; Local and Hybrid use
explicit external local-inference configuration.

Set `JARVIS_DATA_DIR` to a private writable directory outside the deployment
folder for explicit deployment placement. SQLite backup/restore and forward
migration behavior are documented in the Infrastructure README. For
production restart policy, native service deployment, and upgrade/restore
rehearsal, use the procedures delivered by M4-02; do not run Aspire AppHost as
the service manager.
