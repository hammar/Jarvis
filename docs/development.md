# Development

## Home Assistant configuration

Home tools are registered when an HA endpoint is configured; production chat
tool selection and approval UI remain M2-04 scope. Simulator/E2E uses the
managed HA endpoint, synthetic credentials and explicit fixture catalog.
Direct controlled Web hosts without that resource register no home tools.
Local/Hybrid never substitute fixture entities for a missing live catalog.

Live configuration (trusted administrator settings, never model arguments):

| Setting | Meaning/default |
| --- | --- |
| `JARVIS_HOME_ASSISTANT_BASE_URL` | Explicit HTTP(S) root; no embedded credentials, query or fragment |
| `JARVIS_HOME_ASSISTANT_SECRET_REFERENCE` | Name of a protected environment secret resolved server-side |
| `JARVIS_HOME_ENTITY_CATALOG` | Strict JSON array of explicit approved entity definitions |
| `JARVIS_HOME_THERMOSTAT_ENABLED` | False; when true, thermostat always requires exact approval |
| `JARVIS_HOME_AUTOMATIC_LIGHTS_ENABLED` | False live; enable only after spec section 14 model evaluation |
| `JARVIS_HOME_REQUEST_TIMEOUT_SECONDS` | 5 (1-60); shared across at most two safe-read attempts |
| `JARVIS_HOME_VERIFICATION_SECONDS` | 5 (1-60); observation only |
| `JARVIS_HOME_POLL_MILLISECONDS` | 250 (10-1000); bounded observation interval |

Catalog example (configuration JSON uses these exact property names):

```json
[
  {
    "EntityId": "light.study",
    "FriendlyName": "study light",
    "Area": "Study",
    "Unit": null,
    "Aliases": ["desk light"],
    "AllowWrite": true,
    "MinimumBrightness": 10,
    "MaximumBrightness": 200,
    "MaximumAgeSeconds": 300
  }
]
```

Allowed domains are `light`, `climate` and explicitly selected `sensor`.
`AllowWrite` defaults false; sensor writes are rejected. `climate` requires
`Unit` C or F and `MinimumTemperature`/`MaximumTemperature` bounds (defaults
16/28, in the configured unit). Configure the actual HA thermostat unit;
the adapter does not query household configuration or silently convert units.
Brightness uses native HA integer 1-255 units, not percent; optional brightness
is rejected for off. ID wildcards, paths, unsupported domains and collisions
between entity IDs and other aliases fail configuration. Duplicate descriptive
aliases are permitted but require clarification before an entity is selected.
Live endpoint/reference/catalog must be provided together.

Pending approvals bind the normalized endpoint, named reference and complete
catalog/options hash. Changing the installation or reference requires fresh
consent; resolved token values never enter that identity.

The existing environment resolver reads the variable named by the reference.
Set its value through a protected local launch mechanism, not chat, committed
files, command history, dashboard links or logs. Environment variables are not
an encrypted secret store; the reference mechanism does not imply granular HA
token permissions. Use a dedicated HA account/token and a trusted network/TLS
where feasible. Preserve reference names across updates.

Exact automatic-light grammar, case-insensitive and whole-request only:
`turn on <unique ID/name/alias>`, `turn off <unique ID/name/alias>`, or
`turn on <unique ID/name/alias> at brightness <native integer>`.
Every target/value must match the proposal; extra clauses, negation, missing
brightness intent or other phrasing require approval. This is conservative
recognition, not unrestricted natural-language intent certification.

Freshness uses source `last_updated` age against `MaximumAgeSeconds`.
Unchanged HA values may not update that timestamp; stale is an age heuristic,
not proof that a device failed to measure. Answers expose the timestamp and
availability rather than inventing a fresh reading.

## Toolchain and validation commands

Install the exact .NET SDK in `global.json` (`10.0.401`) and use the solution
lock files. On macOS and Linux, run `tools/validate.sh`. PowerShell is not
required or used.

```sh
dotnet restore Jarvis.sln --locked-mode
tools/validate.sh build
tools/validate.sh unit
tools/validate.sh integration
tools/validate.sh architecture
tools/validate.sh sdk-contracts
tools/validate.sh coverage
tools/validate.sh aspire-e2e
tools/validate.sh browser-e2e
tools/validate.sh docs
```

CI and local development invoke the same validation operations. The selected
test stack is xUnit 2.9.3 + `Microsoft.NET.Test.Sdk` 18.10.1 +
`xunit.runner.visualstudio` 4.0.0 + Coverlet collector 10.1.0 (VSTest).
Aspire packages and AppHost SDK are pinned to 13.6.0. Do not pass
Microsoft.Testing.Platform-only switches to VSTest.

### Validation environment troubleshooting

Linux browser validation installs Chromium and its system libraries. Ensure
the container filesystem has room for NuGet packages, build outputs, browsers,
and OS package installation. Docker disk exhaustion can present as invalid
APT signatures before installation reports insufficient space. Use an isolated
host-backed build directory or enlarge Docker storage; do not disable signature
verification or prune unrelated containers, images, or household-data volumes.

Keep macOS and Linux `bin`/`obj` outputs separate. If native coverage collection
stalls, inspect the generated test output directory for numbered duplicate
assemblies and generated NuGet imports. Recreate only the affected generated
outputs, then rerun the unchanged validation command. Never delete durable
application data or exclude production assemblies to make the collector pass.

## Aspire profiles

The normal development entry point defaults to Simulator:

```sh
dotnet run --project src/PersonalAgent.AppHost
```

Choose the profile through trusted process configuration (`JARVIS_PROFILE`),
never through a model or request payload.

| Profile | Configuration and effect |
| --- | --- |
| Simulator | No credentials or real endpoints. AppHost supplies Web with a discovered deterministic OpenAI-compatible model fixture and fake Home Assistant process; external cloud and Home Assistant credentials are not forwarded. Simulator data is persistent and separate from Local data. |
| Local | Requires `JARVIS_OLLAMA_BASE_URL` and `JARVIS_OLLAMA_MODEL`; cloud is disabled. Optional HA uses `JARVIS_HOME_ASSISTANT_BASE_URL` and a secret reference. |
| Hybrid | Requires explicit Ollama endpoint/model and cloud HTTP(S) endpoints plus `JARVIS_CLOUD_SECRET_REFERENCE`. Configuring Hybrid does not bypass packet review or consent. |
| E2E | Test-only; requires unique `JARVIS_E2E_DATA_DIR`, launches controlled model/Home Assistant fixtures, and configures Web to use the discovered model fixture instead of external inference. Its model fixture exposes a one-shot delayed-stream control only to E2E tests for deterministic cancellation checks; model input cannot enable it. An optional `JARVIS_E2E_BOOTSTRAP_TOKEN` is test-only and rejected in all other profiles. |

Endpoint settings must be absolute HTTP(S) URIs without embedded user info or
query data. Supply tokens through a named protected secret reference, not an
endpoint URL. The AppHost does not resolve or print secret values. macOS uses
native Ollama as an external endpoint; no Apple GPU/container assumption is
made.

The owner UI offers Approvals, Memory, Household, and Activity alongside
Chat and Settings. Simulator displays only its controlled Home Assistant
allowlist and marks the connection probe `not-run`; it does not require or
forward household credentials. The Memory surface can create, edit, delete,
and explicitly resolve conflicts through typed owner-entered proposals without
model inference; chat may also propose changes. Facts remain unchanged until
the exact owner-reviewed proposal is approved. The Simulator model recognizes
only the documented demo commands `list household entities`, `read
temperature`, `turn on simulator lamp`, `turn off simulator lamp`, `search
memory: <terms>`, and `remember: <subject> | <key> | <value>`. This behavior is
limited to the managed Simulator/E2E model fixture; live profiles never launch
or configure it. A remembered fact is still only proposed until explicit
approval.

Bare loopback Ollama origins (with no path or `/`) are normalized to the
OpenAI-compatible `/v1` API base. Explicit API paths, such as `/v1` or a
custom proxy prefix, are preserved. Controlled integration tests execute the
actual Copilot runtime through production Local/Hybrid composition against
strict completion paths; startup-only checks are not inference evidence.

Local chat preserves the application-owned current question instead of the
SDK's default date/XML wrapper on create and resume. Tool-free Local turns reuse
native SDK history only when it matches the host-selected SQLite context.
Reconstruction quotes/labels earlier history instead of sending an opaque
provenance packet. Each turn still launches its own runtime. Working copies are
private, bounded and replaceable. The SDK owns automatic compaction for tool-free
Local sessions using registered-model input/output limits of 8,192/2,048 tokens.
Configure Ollama's actual context allocation for at least their 10,240-token sum;
Jarvis does not modify the native server or assume automatic discovery.
The host's character estimate still bounds selected input/reconstruction, not
SDK summary text. See the accepted reliance and upstream limitation in
[ADR 0004](adr/0004-local-copilot-working-state.md). Validate a model
with the opt-in [Local chat comparison](testing.md#local-chat-model-comparison);
passing controlled-provider tests alone does not establish live model quality.
Restart the host after updating binaries. Existing accounts/history remain;
use a new conversation for clean comparisons rather than retaining earlier
incorrect model responses.

The M1 local coordinator uses a 120-second end-to-end interactive turn
deadline by default, starting at durable acceptance and including queue wait,
routing, context construction, and inference. The engine receives only the
remaining budget plus a separate deadline-cancellation token, preserving the
distinction from owner cancellation in terminal outcomes.
`JARVIS_LOCAL_TURN_MAX_DEADLINE_SECONDS` is a trusted host setting
that bounds explicit provider/model overrides from 120 through 1200 seconds;
invalid values fail startup. The coordinator permits four executing turns and
20 queued turns. Same-conversation work stays in the counted queue while it
waits and does not occupy an execution worker, so unrelated conversations can
use available capacity. A stable client request ID is
deduplicated per conversation in SQLite; conflicting reuse is rejected.
Shutdown stops acceptance, persists queued work as interrupted, requests
bounded engine shutdown, and surfaces timeout/persistence failures. On restart,
persisted nonterminal turns are marked interrupted and are never automatically
replayed. Readiness probes SQLite reachability and the applied schema version, along
with local-policy availability,
coordinator recovery, and provider configuration separately from model
connectivity; it does not probe or log the model endpoint.

### Owner authentication and local chat

The first Web startup prints a one-time owner bootstrap token to the local
host console. Use it once in the setup form to create the owner passphrase.
Only an adaptive salted password hash is stored in SQLite. The session is an
HttpOnly, SameSite=Strict cookie with a 12-hour lifetime; Data Protection keys
persist below the owner-private data directory so restarts preserve cookie
validation. Mutations require the antiforgery request token in
`X-CSRF-TOKEN`. Login and bootstrap are rate-limited. If the passphrase is
lost, this M1 version has no reset flow; retain the protected data directory.

Session and antiforgery cookie names share a namespace derived from the
normalized absolute data-directory path. Separate Local/Simulator data
directories can therefore use the same browser and loopback hostname on
different ports without overwriting each other's cookies. Keep that path
stable across restarts; moving it or using a different symlink alias requires
sign-in again. Upgrading from the earlier fixed-cookie names also requires
one sign-in; account credentials and durable history are unchanged.

Conversation titles are durable display metadata: empty conversations show
“New conversation” and their creation instant in the browser's local time zone.
The displayed numeric UTC offset distinguishes repeated daylight-saving times.
The first accepted user message supplies a normalized excerpt of at most 80
Unicode scalar values; no model call is involved. Owners can rename through the
authenticated chat UI, and the manual choice is retained across restarts and
never replaced by automatic naming. The authenticated
`PATCH /api/conversations/{id}` mutation requires CSRF validation and accepts a
trimmed title of at most 200 characters without control characters and with
displayable text with a visible base rune. Format/mark-only titles (including
variation selectors) and default-ignorable Hangul fillers are rejected; format
characters and marks alongside visible text, including emoji joiners and
variation selectors, are preserved. The same validation
applies to nonblank titles supplied when creating a conversation.

The root page provides Chat, Settings, and Activity. Chat submits bounded
requests with stable IDs and reconnects through the SSE `Last-Event-ID`
sequence. Closing the browser stream does not stop inference; use Cancel for
owner cancellation. Settings persist conversation/audit retention windows in
SQLite (1–3650 days; defaults are 90/30). The configuration keys
`JARVIS_CONVERSATION_RETENTION_DAYS` and `JARVIS_AUDIT_RETENTION_DAYS` are
initial defaults only and are read if no owner setting exists. Editing those
settings uses .NET options/configuration for startup defaults and SQLite for
the authoritative mutable owner state; it intentionally does not add a
SQLite-backed .NET configuration provider. Expired eligible history is
cleaned at startup, immediately when settings are saved, and hourly from the
current persisted retention values while the host remains running.

`GET /health/live` is public for the Aspire process health probe.
`GET /health/ready` is authenticated and reports database, recovery, and
provider-configuration state to the owner. Development-only `/health` and
`/alive` aliases are liveness-only; they do not expose readiness details. A
healthy process probe does not mean that the model is reachable.

## Direct Web host and resources

The Aspire AppHost is the canonical development orchestrator, not a production
supervisor. For a direct Web process, set `JARVIS_PROFILE`, `JARVIS_DATA_DIR`,
and any required external endpoint/secret-reference settings. Direct Simulator
and E2E startup requires an explicitly isolated `JARVIS_DATA_DIR`; those
profiles never fall back to the Local per-user path. Then run:

```sh
dotnet run --project src/PersonalAgent.Web
```

When Local or Hybrid has no `JARVIS_DATA_DIR`, Web uses
`<LocalApplicationData>/Jarvis`, regardless of whether AppHost or the Web
project is launched directly. If the prior AppHost
`<LocalApplicationData>/PersonalAgent/jarvis.db` is the only default database,
it remains selected for compatibility. If both locations contain a database,
set `JARVIS_DATA_DIR` explicitly; startup refuses to choose and hide either
store. Direct Web startup also rejects profile names other than `Simulator`,
`Local`, `Hybrid`, and `E2E`.

AppHost does not own external Ollama/Home Assistant lifecycles or personal
data. Keep the dashboard local and authenticated; local telemetry exports
only when an OTLP endpoint is explicitly configured. No automatic HTTP
retry/hedging policy is enabled by ServiceDefaults; writes and provider turns
need explicit future semantics.

## Development environment requirements

- .NET SDK `10.0.401`; locked restore.
- Chromium for Playwright browser E2E. Install it without PowerShell using
  `tools/validate.sh playwright-install` (Linux installs system dependencies).
- A Docker CLI and reachable Docker Engine are required by the Aspire runtime
  for local AppHost and Aspire E2E startup. The Simulator/E2E profiles do not
  launch Docker-managed application resources; on macOS, keep Docker Desktop
  running while using Aspire.
- No cloud API keys or household credentials for required checks.

## Copilot cloud agent environment

`.github/workflows/copilot-setup-steps.yml` prepares GitHub-hosted Copilot
cloud agent sessions: it installs the SDK from `global.json`, performs locked
restores and installs Playwright Chromium through `tools/validate.sh`. It
receives no secrets. Local agent sessions use the developer's own toolchain and
do not run it. The workflow also runs when the file itself changes, so edits are
validated before cloud agents depend on them.
