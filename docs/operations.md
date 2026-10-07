# Operations

Aspire AppHost is a development orchestrator, not a production supervisor or
always-on macOS service manager. Keep its dashboard local and authenticated.
Use Simulator for credential-free development. Local and Hybrid require
explicit trusted local endpoint/model configuration; use protected named
secret references rather than embedding credentials in URIs or source control.
AppHost does not own external Ollama, Home Assistant, or personal data.

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

During graceful shutdown the coordinator stops accepting work, interrupts
queued turns, and asks the engine to stop active turns within a bounded
deadline. Persistence or shutdown timeout failures are surfaced and make
readiness unavailable; shutdown never reports uncertain work as completed.
`/health/ready` reports migration/policy/runtime readiness and local provider
configuration separately from model connectivity, which is not probed.
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
