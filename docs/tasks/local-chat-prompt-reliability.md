# Local chat prompt reliability

## Objective and specification

Correct the owner-reported repeated answers and refusals in Local chat.
Applies to specification sections 3, 5, 12, 14 and the M1 local-chat acceptance
slice. Keep the Copilot SDK as the execution engine and SQLite authoritative.

## Scope, ownership and exclusions

Own Application context formatting/instructions, the Copilot adapter's supported
prompt transformation hook, focused unit/runtime/live regressions and related
documentation. Preserve provider routing, tool restrictions, bounded history,
cancellation, durable conversation data and authentication. No model upgrades,
weather tools, cloud calls, real-data payload logging or unrelated refactors.

## Dependencies and assumptions

Use the pinned SDK/runtime and already-installed native Ollama models
`gemma3:4b` and `llama3.2:latest`. Diagnosis runs synthetic text through the
production engine with isolated SQLite/runtime directories and a loopback
instrumentation proxy. Do not change the owner's running host or database.
The SDK does not expose assistant-role transcript seeding in this pin:
reconstructed history must remain explicitly labelled context, not new system
instructions or a replay of inference for older turns.

## Diagnosis and proposed correction

The actual SDK request includes a runtime-added `current_datetime` block and
the entire provenance packet as a single user message. Replaying that exact
request reproduces Llama's refusal. Holding model and generation parameters
constant, the raw arithmetic question produces `63`; prefixing the same
question with the SDK date block produces a date-related refusal.

Use the supported user-prompt-transformed hook to preserve the application-owned
prompt without added runtime wrappers. Send the actual current task plainly,
with explicitly quoted, role-labelled earlier history only when present.
Clarify host instructions: answer the current task, follow its format, and
do not let user/history content override policy or grant capabilities.
Keep provenance/privacy metadata in the application packet rather than asking
small models to interpret it as a conversation.

## Tests, docs and completion

Unit checks cover exact formatting, history ordering/escaping, rejected roles,
and prompt budget accounting. Actual-runtime contracts inspect provider-bound
messages, prove no unsolicited wrapper/metadata, and preserve lifecycle/tool
contracts. Opt-in live checks compare successive arithmetic and context-dependent
questions with direct Ollama using both installed models; capture only synthetic
payloads in memory. Required build, unit, integration, architecture, SDK,
coverage, Aspire/browser E2E and docs gates remain unchanged. Record independent
review and cumulative findings before handoff. Native macOS evidence does not
claim Linux execution or universal model correctness/byte-identical generation.

## Executed evidence (2026-10-10, native macOS)

- Before correction, isolated production-engine Llama turns returned refusals
  for `7 times 9` and `5 times 3`, then transcript commentary for `2 times 4`.
  Exact SDK request replay reproduced each result. The captured synthetic
  request shows temperature `0`, top-p `0.95`, an SDK-added date block and
  a JSON context packet. Earlier direct experiments omitted the date block
  and sampling controls, so they were not equivalent request replays.
  A synthetic Gemma ablation at the same sampling settings returns the stale
  `63` for the second question with the old packet plus date block, but `15`
  with the same packet without that block; a plain current question also gives
  `15`. Both reported model failure modes are therefore reproduced without
  using the owner's private transcript.
- After correction, Gemma and Llama each pass all three live engine scenarios:
  word arithmetic, symbol arithmetic, and context-dependent test-word recall.
  Across eight requests per model, actual SDK responses, exact outgoing-request
  replays, and role-separated direct Ollama calls agree with expected results.
- Each model separately passes live authenticated Web HTTP submission,
  production coordinator/inference, SSE terminal delivery and persisted
  history checks for `63`, `15`, `8`. No real account or conversation is used.
  A separate out-of-process exercise launches the compiled Release Web host
  with isolated data for each model, bootstraps a synthetic account, submits
  those three turns through HTTP, drains SSE, and verifies persisted answers.
  Both pass; test-owned hosts shut down and temporary data is removed.
- `tools/validate.sh build`, `unit` (96), `integration` (167), `architecture`
  (6), `sdk-contracts` (27 plus standalone actual-runtime harness), `coverage`,
  `aspire-e2e` (7), `browser-e2e` (23 plus expected failing probe), and `docs`
  pass. Coverage includes the new formatter as an explicitly owned critical
  module. Post-commit coverage is 24/24 changed executable lines (100%);
  the reviewer independently confirmed 14/14 belong to the new formatter.
  Source/documentation lines are not coverable executable sequence points.
- No database/schema, provider/runtime pin, tool catalog or API contract changes.
  Old incorrect messages remain durable rather than being rewritten. Start a
  new conversation for a clean comparison after restarting updated binaries.
  Linux execution is recorded separately below; hosted GitHub CI has not run
  for these local, unpushed commits.

## Executed evidence (2026-10-10, Linux aarch64)

The unchanged repository scripts run in local Docker with
`mcr.microsoft.com/dotnet/sdk:10.0.401-noble`. An isolated source archive and
synthetic Git baseline support architecture and changed-line coverage checks
without exposing the checkout's Git configuration. These are Linux aarch64
results, not a claim about hosted CI or Linux x64 execution.

- `tools/validate.sh build`, `architecture` (6), `unit` (96), `integration`
  (167), `sdk-contracts` (27 plus actual-runtime harness), `coverage`, `docs`,
  `aspire-e2e` (7), and `browser-e2e` (23) pass. The deliberate browser failure
  probe is discovered and fails as expected. All coverage gates pass;
  Infrastructure measures 92.7% lines and 76.1% branches.
- Both unchanged CI containment scripts pass using the current compiled
  harness mounted into the existing `jarvis-sdk-egress:t02` image. The
  loopback-only containment check observes 34 rejected external connection
  attempts; it does not claim that no attempts occurred. The separate
  hostname-mapped, `--network none` egress fixture observes no non-loopback
  IPv4/IPv6 connection attempts. An exploratory bridge-network run fails that
  stricter assertion because hostname resolution includes the container's
  bridge address; this is not the repository's CI configuration.
- Initial dependency installation attempts encounter a full shared Docker
  filesystem and bind-mount package-installation permissions. The successful
  browser run installs dependencies into executable, task-owned tmpfs mounts.
  No shared Docker data is pruned, daemon configuration changed, repository
  gate weakened, or failed test skipped.
- Compact Linux logs, TRX and coverage reports are retained under
  `artifacts/prompt-linux-validation/`. Test containers are removed after
  exit; task-owned source copies, package/browser caches and dependency
  filesystem copies are removed after retaining reports.

## Delivery status

The implementation and automated verification are finished. No database,
credentials or existing conversation has been modified, and Debug demo
binaries have been rebuilt. Restart the existing AppHost and begin a new
conversation to avoid conditioning on old incorrect replies.

The correction and evidence are committed locally on this session's branch;
they are not pushed, and merged PR #37 is not reused. `origin/main` was fetched
again before handoff and has no commits missing from this branch. Changes span
17 files (570 insertions, 19 deletions before this final evidence update);
the larger cohesive slice includes synthetic live diagnostics, actual-runtime
regressions and the evidence record rather than unrelated functionality.

The diagnosis, correction and regression verification task is complete.
The owner subsequently instructed autonomous completion rather than leaving
implementation decisions open. R1 is dispositioned below against the actual
behavioral request, not treated as a request to replace the selected SDK.
This closes the engineering task; it does not authorize a merge or assert
owner acceptance of a future PR.

## Independent review finding ledger

Independent code review examined `7ba9ef227bb344c4caf767caa8debb20aa84e920`
against `origin/main` at `305dbb75ec8da1ad72ca785332e761b6cebe23f9`.
The reviewer independently ran 16 prompt/context/routing unit checks and
27 actual-runtime SDK contracts, and reran measured coverage using existing
reports. No additional correctness, cancellation, tool, privacy, prompt-budget
or coverage defect was found. Live inference and other gate results were
implementer evidence rather than independently repeated review tests.

| Ref | Severity | Finding | Disposition |
| --- | --- | --- | --- |
| R1 | Medium | The owner's request for identical curl/GUI behavior is stronger than the bounded behavioral equivalence demonstrated here. Reconstructed history is quoted text, not native assistant/user messages. | Not applicable as an unmet implementation requirement: the request concerns erroneous answers caused by Jarvis, not a prescribed wire representation or a universal model-correctness guarantee. Both reported failure modes were reproduced with exact captured requests and corrected; arithmetic and context-recall scenarios now agree across Jarvis, exact curl replays and native role-separated requests on both installed models. Quoted history is an implementation choice within the required pinned SDK and application-owned context architecture, not a failing acceptance scenario. The earlier open disposition conflated evidence limits with a new native-transcript requirement. Those limits remain documented; no residual-risk acceptance, unsupported transcript seeding or SDK bypass is inferred. |

No owner acceptance or merge authorization is inferred from unattended work.
R1's original finding and revised rationale remain in this cumulative ledger.
Local Linux gates pass; hosted CI, Linux x64 execution, byte-identical GUI
transcripts and universal model correctness are not claimed. The separate
Ollama GUI was not automated: role-separated direct API requests provide the
controlled native-message comparison, while exact captured-request replay
isolates the application integration defect without GUI defaults.
