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
- After correction, Gemma and Llama each pass all three live engine scenarios:
  word arithmetic, symbol arithmetic, and context-dependent test-word recall.
  Across eight requests per model, actual SDK responses, exact outgoing-request
  replays, and role-separated direct Ollama calls agree with expected results.
- Each model separately passes live authenticated Web HTTP submission,
  production coordinator/inference, SSE terminal delivery and persisted
  history checks for `63`, `15`, `8`. No real account or conversation is used.
- `tools/validate.sh build`, `unit` (96), `integration` (167), `architecture`
  (6), `sdk-contracts` (27 plus standalone actual-runtime harness), `coverage`,
  `aspire-e2e` (7), `browser-e2e` (23 plus expected failing probe), and `docs`
  pass. Coverage includes the new formatter as an explicitly owned critical
  module; final changed-line totals are recorded after committing new files.
- No database/schema, provider/runtime pin, tool catalog or API contract changes.
  Old incorrect messages remain durable rather than being rewritten. Start a
  new conversation for a clean comparison after restarting updated binaries.
  Required Linux CI has not been executed by this native session.

## Independent review finding ledger

Review findings and dispositions are recorded here against the implementation
commit before final handoff.
