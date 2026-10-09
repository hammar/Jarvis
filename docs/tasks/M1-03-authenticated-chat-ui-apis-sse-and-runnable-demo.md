# M1-03: Authenticated chat UI, APIs, SSE, and runnable demo

## Objective

Complete M1 with a local-first, authenticated, durable chat experience and a
credential-free Simulator demo built on the M1-02 coordinator.

## Specification and scenarios

- Project spec sections 12 (host API/UI), 14 (acceptance), 16 (M1 demo), 18
  (Aspire profiles), 19 (architecture/documentation), and 20 (tests/coverage).
- Exercise owner bootstrap and login; reject unauthenticated and CSRF
  mutations without state changes; enforce owner identity for every object
  lookup; deduplicate repeated client request IDs; resume ordered turn events
  without duplicating terminal output; persist conversation history across
  restart; report cancellation and provider/readiness failure truthfully.

## Scope

- One-time bootstrap token and owner passphrase setup; recurring passphrase
  login/logout with protected HttpOnly/SameSite cookie sessions and CSRF
  protection. Persist only the passphrase hash in SQLite and persist
  ASP.NET Data Protection keys in the owner-private application data directory.
- Authenticated conversation/turn APIs, cancellation, resumable bounded SSE,
  health endpoint protection, safe structured errors/correlation IDs, and
  request/event limits aligned with the coordinator (8,000 text characters and
  128 request-ID characters by default). Kestrel enforces the 64 KiB body limit
  even for chunked requests.
- Razor Chat, Settings, and Activity pages. Show route/provider/model and
  local readiness/connection references, never resolved secret values.
- Retention controls validate 1–3650 days, default to 90 conversation days and
  30 audit days, and persist in SQLite across restarts and application updates.
  Trusted .NET configuration may supply initial defaults; do not add a custom
  writable configuration provider. Apply eligible cleanup at startup, after a
  settings update, and hourly using owner settings read in the same immediate
  SQLite transaction as deletion. Running, approval-waiting, and interrupted
  turns retain their conversation/recovery state until explicitly resolved.
- Use production composition with deterministic isolated Simulator/E2E
  endpoints, and document opt-in native Ollama operation.

## Exclusions

No cloud routing/consent, Home Assistant actions, memory, approvals, reminders,
scheduler, M2/M3 UI/API, public listener, or real cloud/household operations.
Do not present simulator output as live model evidence.

## Owned files and boundaries

- `src/PersonalAgent.Web/`: composition, auth, API/SSE handlers, Razor pages,
  static assets, and Web README.
- `src/PersonalAgent.Infrastructure/Persistence/`: versioned SQLite migration,
  owner credential/settings storage, and retention integration.
- `src/PersonalAgent.Application/`: only add use-case contracts where needed
  to preserve Application ownership and prevent Web handlers from using raw
  infrastructure clients.
- `tests/PersonalAgent.IntegrationTests/` and
  `tests/PersonalAgent.E2ETests/`: authenticated API, persistence, Aspire, and
  Playwright behavior tests.
- `docs/testing.md`, `docs/development.md`, `docs/architecture.md`, and
  `README.md`: scenario mappings and user/developer behavior.

Infrastructure references remain confined to Web composition/bootstrap.
Browser handlers call Application use cases, not SQLite/provider clients.

## Dependencies and assumptions

- M1-02/#12 is complete; the SQLite conversation store owns durable ordered
  turn events and stable request-ID semantics, and the local turn coordinator
  owns submit/read/cancel behavior.
- The owner-selected passphrase verifier and retention settings persist in the
  existing owner-private data directory; E2E uses a separate isolated database.
- Loopback remains the default bind. Liveness is public; readiness is
  authenticated as required by the issue/spec.
- M2-04 owns household/memory/approval surfaces; M3-03 owns cloud/reminder
  surfaces.

## Tests and documentation

- Integration: bootstrap once, repeated login, persisted credential/settings,
  schema upgrade, CSRF rejection, owner authorization, safe errors, request
  idempotency, and readiness behavior.
- SSE: monotonic event IDs, resume via `Last-Event-ID`, draining terminal
  histories across bounded pages, cancellation, disconnect, bounds/backpressure,
  terminal-commit interleavings, and no duplicate final message. The browser
  consumes application `TextDelta` events incrementally, keeps one active-turn
  conversation selected, and recovers authoritative history at completion.
- Aspire/Playwright: bootstrap/login, create a conversation, stream a
  controlled response, synchronize with a fixture-signaled delayed stream
  before cancelling, report a controlled provider failure without exposing
  provider details, restart and reopen persisted history, and display
  provider/readiness failures without secret values. Simulator controls are
  enabled only for E2E and cannot be triggered through model input.
- Update the scenario matrix and setup/profile guidance; run the mandatory
  applicable build, architecture, unit, integration, SDK-contract, coverage,
  Aspire E2E, browser E2E, and docs checks.

## Completion criteria

The M1 demo runs through Aspire Simulator with no cloud or household
credentials; account and conversations survive restarts; authentication,
object authorization, CSRF, idempotency and SSE recovery scenarios pass; the
M1 checks and docs pass; an independent review has findings with explicit
dispositions and the handoff records actual commands, results, limitations,
and unexercised live behavior.

## Independent review finding ledger

| Ref | Finding | Disposition and evidence |
| --- | --- | --- |
| F1 | Terminal SSE replay stopped after its first 100-event page. | Fixed in `ChatApi.StreamEventsAsync`; integration test covers 102 replayed events across pages. |
| F2 | Oversized turn text/request IDs could reach coordinator exceptions. | Fixed with API bounds and 400 mapping; integration tests cover over-limit inputs. |
| F3 | Retention cleanup needed periodic execution. | Fixed with hourly cleanup; `HostedRetentionCleanupAppliesExpiredHistoryDuringUptime` verifies cleanup while the host remains active. |
| F4 | Development `/health` exposed readiness anonymously. | Fixed to expose liveness only; integration coverage verifies readiness remains authenticated. |
| F5 | `Content-Length` checks did not bound chunked bodies. | Fixed with Kestrel's body limit; Aspire E2E verifies oversized chunked input is rejected. |
| F6 | Cancellation E2E could cancel before the delayed provider stream began. | Fixed by waiting on an E2E-only fixture signal before cancellation in `BrowserSmokeTests`. |
| F7 | A cleanup worker could apply an old retention-settings snapshot after a longer period was saved. | Fixed by acquiring an immediate SQLite transaction before reading durable settings and deleting; `PeriodicCleanupUsesCurrentDurableRetentionAfterOwnerChangesSettings` verifies persisted values override stale defaults. |
| F8 | Terminal provider failures/interruption were not visible in the chat UI. | Fixed with safe, explicit `TurnFailed`/`TurnInterrupted` notices; Playwright verifies a controlled provider failure is reported without provider details. |
| F9 | Interrupted turns were treated as unresolved by retention and could prevent conversation deletion indefinitely. | Not applicable: accepted ADR 0003 explicitly requires interrupted conversation/reconnect state to remain until explicitly resolved. Both cleanup predicates intentionally preserve it; the retention integration test verifies this policy. |
| F10 | Retention regression coverage did not prove that Completed, Failed, and Cancelled turns allow expired conversation deletion. | Fixed by adding expired conversations with each terminal state and asserting cascaded deletion; `RetentionUsesDefaultAndOverrideWindowsAndKeepsDurableState` verifies all three. |
| F11 | Browser listened for a non-existent assistant delta event instead of application `TextDelta`, so it displayed answers only after terminal history reload. | Fixed by consuming and accumulating `TextDelta`; Playwright holds the simulator after its first delta and asserts partial text is visible before the second delta/completion, then verifies one final answer. |
| F12 | Conversation navigation could leave the visible conversation and active cancellation target out of sync. | Fixed by disabling navigation/new-conversation controls while a turn is active and rejecting attempts to open another conversation; Playwright verifies those controls are disabled during a delayed turn and usable after cancellation. |
| F13 | SSE could close when terminal status was observed after a stale event-page read, omitting the atomically committed terminal event. | Fixed by requiring a follow-up bounded event read after first observing terminal status and closing only after a terminal read returns no events; `SseDrainsTerminalEventCommittedBetweenEventReadAndStatusRead` uses a barrier to commit completion at that interleaving. |
| F14 | Navigation and duplicate sends remained possible before the turn-submission response arrived, and the active conversation ID was read after the request. | Fixed by locking navigation and submission before awaiting the API, capturing the submitted conversation ID, and guarding `openConversation`; `PlaywrightSignsInAndStreamsASimulatorChat` holds the accepted response and verifies locked controls, rejected switching, and a single POST. |
| F15 | Retention regression coverage omitted the `Received`, `Routing`, and `ContextReview` nonterminal states. | Fixed by adding an expired conversation for each state and asserting both conversation and turn survive cleanup in `RetentionUsesDefaultAndOverrideWindowsAndKeepsDurableState`. |
| F16 | Settings/Activity navigation could hide the only cancel control during an active turn. | Fixed by locking Settings, Activity, and sign-out alongside conversation navigation while submitting/running; the browser test verifies these controls remain disabled until cancellation/completion. |
| F17 | A conversation read already in flight before submission could finish afterward and change the visible conversation. | Fixed with a selection generation invalidated at submission; `PlaywrightSignsInAndStreamsASimulatorChat` holds an earlier GET until after the submitted turn completes, then verifies the late result cannot change the selected conversation. |
| F18 | If the server accepted a turn but its HTTP response was lost, resubmitting created a new request ID and could execute the prompt twice. | Fixed by retaining the request payload and ID in tab-scoped session storage before POST, restoring the conversation and prompt after reload, and retrying unchanged submissions with the same ID; the browser regression drops an accepted response, reloads, and verifies the ID is reused and the stored terminal result is returned. Definitive non-retryable 4xx responses clear the pending request. |
| F19 | The review ledger omitted F14/F15 and therefore did not maintain a complete cumulative disposition record. | Fixed by recording F14–F18 with regression evidence in this ledger; the final handoff records the final independent review and exact-tree validation below. |
| F20 | Settings/Activity navigation could hide the only cancel control during an active turn. | Fixed by disabling Settings, Activity, sign-out, and conversation navigation until completion/cancellation; the browser regression verifies these controls remain locked after the conversation list refresh. |
| F21 | A conversation read started before submission could overwrite the selected conversation even after the turn completed and unlocked navigation. | Fixed by invalidating pending conversation reads with a selection generation when submission starts and applying responses only while their generation remains current; the browser test releases the read only after the terminal response. |
| F22 | An accepted request's idempotency identity was lost on page reload before the owner could retry an ambiguous response. | Fixed by storing the unaccepted request identity and payload in same-tab session storage before sending; the browser test reloads the page after an accepted response is dropped and verifies a safe retry with the same identity. The browser-held payload remains in tab-scoped storage until acceptance or a definitive non-retryable 4xx response. |
| F23 | The cumulative finding ledger did not include all follow-up review findings. | Fixed by appending F20–F22 while retaining all prior findings and dispositions in this task brief. |

## Final handoff

F61–F63 recovery/SSE follow-up: objective is to release stale browser recovery
locks only on a definitive missing-conversation response and prove slow or
disconnected SSE consumers do not stall durable turn completion. Scope owns
chat JavaScript, browser recovery regressions, production-composed SSE
integration regressions and related testing/Web documentation. No schema,
contract or dependency changes. Temporary 500/network failures retain
recovery; 408/429 remain retryable, while definitive 409 rejects the submitted
idempotency payload and permits a new request. Required evidence includes
accepted/unaccepted stale recovery, conflict retry, blocked/disconnected
consumer completion, bounded event pages, existing recovery regressions,
applicable gates and independent review.

F61–F65 evidence (October 9, 2026): `tools/validate.sh build && integration
&& architecture && coverage && aspire-e2e && browser-e2e && docs` passed
for `ed89456`: integration 139/139, architecture 6/6, Aspire 7/7, browser
4/4 plus expected failing probe; all coverage/report/discovery thresholds
passed with changed lines 98.2% (805/820), Web 98.4% lines (685/696) and
80.4% branches (111/138). Coverage reused the preceding unit 91/91 report;
unit and SDK gates were not rerun for this JavaScript/test-only follow-up.
Independent review reproduced F64 against exact `ed89456` using controlled
JavaScriptCore fetch/DOM execution and confirmed accepted recovery 404
clearing and 500 reconnection in that harness. Following F64/F65 fixes,
build/browser/docs passed again, browser 5/5 plus expected failing probe.
Docs was rerun after this final evidence update.

SSE evidence uses production Web/coordinator composition, real SQLite and
a deliberate deterministic engine adapter with a controlled response stream.
It proves Write/Flush are actually blocked, 100-event page requests do not
read ahead while blocked, and terminal status/event/answer persist within
deadlines independently. Disconnect cases prove RequestAborted interrupts
blocked I/O and request completion before engine release; slow cases verify
ordered replay after release. It is not an actual TCP slow-reader/memory
benchmark or live provider/SDK validation. Initial targeted cases failed on
fixture CSRF identity then TestServer disconnect synchronization; corrected
without weakening assertions, then passed 4/4 twice and in integration139.
The formatting gate initially failed on browser whitespace, corrected with
dotnet format. The F65 browser failure is retained, not counted as a pass.
No schema/contracts/dependencies changed; new-head CI remains separate.

Independent follow-up reviewed the F64 correction over `ed89456` (committed
as `2b19726`), held-404 browser regression, isolated fixture lifecycle and
ledger. Five focused checks executing current JavaScript under JavaScriptCore
passed: newer selection/replacement recovery preservation, accepted 404
clearing without SSE, and accepted 500/network recovery with locks/reconnect.
It found no remaining significant issues and substantiated the four SSE
theory cases' bounded-page, blocked-I/O, durable outcome and disconnect
assertions. It did not independently run full suites; browser stubs and
TestServer do not establish TCP socket-buffer bounds, load behavior or live
provider compatibility. All F1–F65 dispositions remain retained.

F58 follow-up owns authentication cookie registration in `ChatApi.cs`,
the shared-cookie SQLite/HTTP regression, and development/testing/Web docs.
Objective: isolate Local and Simulator browser sessions when their separate
data directories share a hostname on different ports, while preserving
same-path restart sessions under the existing M1-03 authentication criteria.
No schema, Application contract, dependency or profile changes. Cookie names
use the normalized absolute data path, not transient port/profile settings.
The first upgrade from fixed names requires sign-in again; credentials and
history are retained. Moving the directory or changing its symlink alias
also requires sign-in. Completion requires coexistence/sign-out/CSRF/restart
regression, applicable validation and independent review; results follow.

F58–F60 final evidence (October 9, 2026): production revision `3e40728`
passed `tools/validate.sh build`, followed by the serial `unit &&
integration && architecture && sdk-contracts && coverage && aspire-e2e &&
browser-e2e && docs` selectors. Results: unit 91/91, integration 135/135,
architecture 6/6, SDK 25/25 plus the actual pinned runtime harness, Aspire
API 7/7, browser 2/2 plus the expected failing probe, all coverage/report/
discovery fixtures and internal Markdown links. The initial unit collection
attempt was stopped without a result after finding 710 numbered generated
DLL/PDB duplicates; rebuilding only unit output restored execution, without
altering collector settings or gates. A preliminary focused regression
first failed on an empty Cookie header in its fixture, then exposed F59;
both were corrected before the successful gates.

Independent review of exact `3e40728` against `abaf1e9` examined instructions,
applicable spec/ADRs, complete F1–F59 ledger, all changed production/tests/docs
and surrounding auth/composition/persistence. It found no significant
production defect and identified the F60 evidence gap, now corrected.
After that test/docs-only correction, `build && integration && coverage &&
docs` passed again: integration 135/135; changed lines 98.2% (805/820);
Web 98.4% lines (685/696), 80.4% branches (111/138); all thresholds passed.
Docs was rerun after this handoff update. The reviewer ran read-only Git
inspection/diff checks, not the runtime suites.

The shared-cookie regression uses production HTTP composition and real
SQLite with a browser-style cookie container, not two real Kestrel sockets
or an actual browser for coexistence. Existing browser/Aspire gates cover
real-process restart authentication. Windows path case normalization and
fixed-name-cookie upgrade were not directly exercised; Linux new-head CI
remains separate. Namespacing avoids collisions, not cookie disclosure to
other services on the same hostname. Path normalization is lexical, not
symlink canonicalization. All F1–F60 dispositions remain retained.

Latest summary-review follow-up (F51–F57) owns the browser navigation/login
script, settings live-status markup, atomic Infrastructure retention read,
real SQLite/browser regressions, and related architecture/testing/Web docs.
It adds no schema, contract or dependency change. Independent review of
`2054b76..82d9fd8` read the instructions, applicable spec/ADR, complete
F1–F56 ledger and changed production/tests/docs and reported F57's stale-save
error gap. The guarded error path and held-save browser regression resolve
it; the reviewer did not independently execute the suites. Status-region
semantics are machine-verified; assistive-technology speech remains
unexercised, not a claim of a live screen-reader run.

F51–F57 validation on October 9, 2026: the first serial sequence passed
build and unit (91/91), then stalled in coverage collection before
integration results were published. That attempt and one unchanged rerun
were stopped, not counted as passes. Process sampling showed file-copy
activity against 2,715 numbered DLL/PDB duplicates in the generated
integration output directory. Rebuilding only that directory restored
normal execution; the origin of the duplicate files was not established.
No test, collector configuration, coverage exclusion or threshold changed.

After the F57 production/test correction, the unchanged script selectors
`integration && architecture && sdk-contracts && coverage && aspire-e2e &&
browser-e2e && build && docs` all passed serially: integration 134/134,
architecture 6/6, SDK 25/25 plus the actual pinned runtime contract harness,
Aspire API 7/7, browser 2/2 and the expected failing browser probe, and
Release formatting/analyzers/build and internal Markdown links. Coverage
used the current unit 91/91 report and new integration report; changed
executable lines are 98.2% (801/816), Infrastructure is 92.5% lines
(2484/2684) and 75.6% branches (575/761), and Web is 98.6% lines (682/692)
and 80.9% branches (110/136). All thresholds and invalid-report/discovery
fixtures passed. The final build ran after every executable edit; docs was
rerun after this evidence update. The cumulative F1–F57 dispositions remain
retained. Native macOS evidence does not replace required new-head Linux
CI, and no merge is authorized.

Latest CI follow-up: on `256ef93`, all reported checks except Aspire E2E
passed, including native macOS SDK contracts. The Linux Aspire failure was
the invalid JSON size fixture recorded as F50, not a bypassed size limit.
Only the E2E fixture and directly related docs changed: build and
`tools/validate.sh aspire-e2e` (7/7) and docs passed on macOS after the fix,
and `git diff --check` passed. The valid payload regressions retain mandatory
HTTP 413 for oversized bodies while adding a positive processing boundary.
Kestrel's observed chunk-framing accounting was verified against its
`Http1ChunkedEncodingMessageBody` implementation. The preceding full-suite,
independent-review and production evidence below is unchanged; Linux CI on
the new revision remains required and is not inferred from this native run.

### PR review follow-up

| Ref | Finding | Disposition and evidence |
| --- | --- | --- |
| F24 | Authorization/rate limiting could short-circuit before correlation headers were registered. | Fixed by registering correlation/body-limit middleware before those components; integration assertions verify correlation IDs on anonymous API and readiness responses. |
| F25 | Empty successful cancellation responses were parsed as JSON and reported as failures. | Fixed by reading successful response text and returning null for an empty body; browser cancellation asserts no error alert. |
| F26 | Terminal history refresh unlocked navigation before its asynchronous operations finished. | Fixed by retaining the active-turn lock through refresh and releasing it in finally with explicit error reporting; browser holds the terminal list refresh and verifies navigation/send stay disabled. |
| F27 | Browser waited for an already-existing list item instead of completion of third-conversation creation. | Fixed by waiting for the selected ID to change and the list count to reach three before continuing. |
| F28 | Accepted turns lost their SSE subscription, cancellation target, and navigation lock after reload. | Fixed by retaining accepted turn IDs in tab recovery state until terminal delivery and restoring the stream and controls on startup; browser reloads a running delayed turn and cancels the same ID. |
| F29 | Previously missed: Simulator status omitted the configured model. | Fixed by reporting the composed simulator model for Simulator/E2E; browser verifies the visible model label. |
| F30 | Previously missed: five invalid bootstrap attempts permanently disabled setup until restart. | Fixed by removing the permanent counter and retaining the existing bounded HTTP rate limiter; integration makes five invalid service attempts and then successfully bootstraps using the correct token. |
| F31 | Previously missed: startup treated non-authentication API failures as sign-out. | Fixed by retaining explicit 401 status on authentication errors and showing other runtime failures without sign-in UI; browser injects a status 500 and verifies its alert and hidden authentication form. |
| F32 | Accepted-turn startup exposed enabled controls before reading recovery state. | Fixed by restoring the turn and navigation lock synchronously before revealing chat or awaiting status/list requests; browser holds startup status after reload and verifies controls and turn identity. |
| F33 | Previously missed: successful authentication never upgraded outdated password verifiers. | Fixed with an Application-owned conditional verifier replacement and re-verification if another update wins; SQLite integration verifies actual Identity rehash, wrong-passphrase preservation, and stale-write rejection. |
| F34 | Previously missed: uncaught API exceptions did not return safe structured errors. | Fixed with correlation-scoped exception handling that emits generic problem details without logging private exception messages; aborted requests propagate cancellation and started responses are aborted. Integration verifies a private persistence failure produces safe correlated 500 details. |
| F35 | Previously missed: expired-cookie 401 retained identity-bound antiforgery state. | Fixed by refreshing anonymous CSRF state before presenting the authentication error; browser signs out through HTTP and signs back in without reload. |
| F36 | Previously missed: concurrent New conversation clicks created multiple durable roots. | Fixed with a creation-in-flight guard and locked controls until creation/selection/list refresh finishes; browser holds creation and dispatches a second click, verifying only one POST. |
| F37 | Regression validation: repeated reload/CSRF reads exhausted the shared authentication limiter and blocked accepted-turn startup with 503. | Fixed by limiting password-bearing bootstrap/login attempts, not status/CSRF reads or logout; rejected attempts return 429. Integration verifies reads cannot exhaust the 20-attempt/minute limit and attempts remain bounded. |
| F38 | Independent review of `ad5e79f`: a failed startup status/list read reconnected SSE but left the conversation ancestor hidden, hiding cancellation and streamed output. Medium. | Fixed by restoring the selected conversation and visible recovery/cancellation surface before startup reads; browser holds then fails startup status for a saved running turn, verifies visible cancellation and SSE, and successfully cancels the same turn. |
| F39 | Hourly cleanup exceptions stopped the Web host. High. | Fixed with privacy-safe failure logging, degraded `history-cleanup` readiness and next-scheduled-pass retry; shutdown cancellation propagates. Hosted integration holds cleanup after failure, asserts host remains running/degraded, then verifies successful recovery and cancellation. |
| F40 | Failed owner-status startup responses were consumed as an unconfigured boolean default. High. | Fixed by using common API error handling and validating the status shape; a first-run browser regression injects owner-status 500, asserts both forms stay hidden with an explicit error, then reloads and completes actual bootstrap. |
| F41 | PR validation claim described a final tree despite later executable edits and omitted reruns. Low. | Completion evidence and PR description are updated with the actual final sequence/counts and limitations, rather than retaining the original-tree claim. |
| F42 | Previously missed: bootstrap form mode persisted into reload-free reauthentication. Medium. | Successful authentication now switches to explicit sign-in mode; browser performs bootstrap, sign-out/401 and login without reload, asserting the setup input stays hidden and no second bootstrap occurs. |
| F43 | Previously missed: concurrent authentication submissions raced account creation and initialization. Medium. | Fixed with an in-flight guard and disabled submit until initialization finishes; browser holds a real accepted bootstrap response, dispatches another submit and verifies one request with no misleading error. |
| F44 | Previously missed: plaintext passphrase/setup token stayed in hidden inputs after authentication. Medium. | Both values are cleared immediately on successful authentication before CSRF/status initialization. Browser verifies empty inputs after actual bootstrap and sign-in. |
| F45 | Previously missed: browser restart fallback login masked failed persistent-cookie validation. Medium. | Removed fallback login; browser must reach persisted conversation controls with the authentication form hidden after host restart using the existing cookie. |
| F46 | First-run regression exposed author CSS overriding hidden attributes on setup inputs/labels. | Fixed with a standard explicit `[hidden]` rule so sign-in mode actually conceals setup controls; the bootstrap/401 regression asserts their rendered visibility. |
| F47 | Independent test review of `cd3f0e9`: store cancellation signal alone did not distinguish shutdown propagation from maintenance-error handling. Medium. | Added post-stop assertions that worker health remains healthy and its execution task is cancelled, so swallowing shutdown as a failed pass fails the regression. |
| F48 | Independent test review of `cd3f0e9`: direct worker probe and NullLogger did not verify Web readiness composition or privacy-safe failure logging. Medium. | Added WebApplicationFactory regression using production health registration/HTTP writer and controlled cleanup; asserts authenticated readiness HTTP 200 with `history-cleanup: Degraded`, continued Web lifetime, failure type/interval logging and no private exception message/exception object. Aspire `ReadinessReportsDatabaseUnavailableAfterStartup` covers unhealthy readiness HTTP 503. |
| F49 | Independent test review of `cd3f0e9`: hidden-label and reload-free reauthentication claims needed observable assertions. Low. | Browser now asserts the setup label stays hidden alongside its input and a window marker survives the sign-out/401/login sequence. |
| F50 | Linux CI on `256ef93`: the chunked-body size fixture used NUL bytes, allowing JSON parsing to reject an early chunk with HTTP 400 before reaching Kestrel's byte cap. | Replaced invalid JSON with valid padded JSON. The theory requires authentication rejection (401) at the 65,536-byte Content-Length cap and 413 one byte over, plus below-cap and oversized chunked cases with correlation headers. Kestrel includes chunk framing in its observed-byte cap, so a 65,536-byte chunked payload is not an accepted boundary; the initial exact-payload chunked assertion exposed this distinction. No production limit or oversized assertion was relaxed. |
| F51 | Previously missed: loaded/saved retention confirmation lacked a live status region. Medium. | Added `role="status"` to the existing settings notice; browser verifies its role and the saved confirmation through that accessible region. Screen-reader speech itself is not exercised. |
| F52 | Previously missed: selecting/creating a conversation left Settings or Activity visible alongside it. Medium. | Conversation activation now hides both ancillary surfaces; browser checks activity-to-conversation selection and settings-to-new-conversation transitions. |
| F53 | Previously missed: slow Settings/Activity responses could override newer view choices. Medium. | Extended the existing selection generation across conversation, Settings and Activity opening, creation/turn submission and authentication transitions. Both ancillary handlers reject stale success/errors; browser holds Settings then chooses Activity, and holds Activity then chooses a conversation, awaiting both stale handlers before visibility assertions. |
| F54 | Previously missed: retention architecture wording implied Interrupted conversations were deletable. Low. | Documentation now names only Completed/Failed/Cancelled and explicitly preserves Interrupted recovery state under ADR 0003. No retention semantics changed. |
| F55 | Previously missed: two autocommit reads could return a retention pair never saved during concurrent update. Medium. | Read both values/defaults in one SQLite statement/snapshot. Real SQLite concurrent readers/writers assert only committed pairs and preserve independent default handling for a missing setting. |
| F56 | Previously missed: wrong passphrases displayed session-expiry text instead of the login failure. Low. | Excluded login from protected-request expiry handling, preserving its `Sign-in failed.` problem title. First-run/re-login browser scenario checks a failed passphrase then succeeds using the same form. |
| F57 | Independent review of `82d9fd8`: stale retention-save failures could show an obsolete alert after navigation even though stale successful saves were ignored. Medium. | Applied the same captured-generation guard to save errors. Browser holds a save, navigates to Activity, returns a controlled 500, awaits the stale handler and asserts the newer view remains with no obsolete alert. |
| F58 | Previously missed: fixed session and antiforgery cookie names collide between loopback instances because browser cookie scope excludes TCP ports. Medium. | Namespaced both cookies with the same stable hash of the normalized absolute data-directory path. Shared browser-cookie regression checks distinct names, authenticated CSRF mutations on both instances, sign-out isolation and same-path restart with preserved session/token. Existing fixed-name sessions require one sign-in after upgrade. |
| F59 | F58 regression exposed retention PUT binding registered default settings from DI rather than the request JSON, returning success without applying the owner's submitted values. High. | Explicit body binding restores intended mutation semantics. Shared-cookie regression asserts the exact saved response and durable 12/34-day pair after restart, and rejects a submitted out-of-range period with HTTP 400. Earlier status-only/default-value assertions did not establish this behavior. |
| F60 | Independent review of `3e40728`: restarted regression saved the pair again before reading it, so that GET alone did not prove pre-restart persistence. Low evidence gap. | Moved the restarted GET/exact pair assertion before any restarted mutation; then separately verifies the preserved CSRF token still authorizes a save. Existing real SQLite reopen coverage remains retained. |
| F61 | Previously missed: slow/disconnected SSE-consumer completion and boundedness lacked direct acceptance evidence. Medium. | Production-composed blocked-response/disconnect regression added; final execution evidence recorded below. |
| F62 | Previously missed: a missing recovered conversation opened a permanently retrying missing SSE turn and retained the navigation lock. Medium. | Only a recovered conversation GET 404 clears tab recovery and unlocks/hides obsolete controls; 500/network failures still reconnect. Browser covers accepted and ambiguous-send records referencing missing conversations and asserts no missing-turn EventSource request. |
| F63 | Review summary: retained 409 conflicts required unchanged retries that could only conflict again. Medium. | Definitive 409 now clears pending submission, retaining 408/429/transport recovery. Browser verifies a changed message submits with a fresh request ID after conflict. |
| F64 | Independent review of `ed89456`: late ambiguous-recovery 404 cleared/hid a newer conversation selected while recovery was pending. Medium. | Clear only the matching pending record and guard stream/selection/control resets with the captured navigation generation. Held-recovery browser regression creates a newer conversation before releasing 404, then asserts it remains visible and send-enabled. |
| F65 | F64 browser run exposed cross-test fixture contamination: a new default-titled conversation made an existing locator ambiguous. | New recovery tests now own isolated host/data fixtures, preserving the existing scenario rather than weakening its assertions or depending on execution order. |
| F66 | Inline review: malformed/obsolete sessionStorage recovery records returned before revealing auth/chat and survived every reload. Medium. | Remove invalid records with a visible notice and continue initialization; removal failure remains an explicit blocking error. Browser covers malformed JSON, invalid shape and null with injected removal failure, anonymous sign-in and authenticated chat, then verifies usable controls and clean subsequent reload. |
| F67 | Independent review minor observation: browser JSON parse error text can include a short stored-text snippet. | Not applicable as a telemetry/privacy disclosure defect: notice is rendered via textContent only in the same owner's tab, with no logging or transmission. It explains the locally corrupt record; no new data recipient or execution surface is introduced. |
| F68 | Previously missed: retention can delete a root after the Web ownership check, then submission recreates it without its history/title. Medium. | Fixed a93ba39/ba2403e: required existing owner checked inside immediate durable submission transaction, including duplicate acceptance; trusted owner-less implicit-root callers preserved. Release integration 145/145 covers deterministic deletion and wrong-owner/no-write behavior. |
| F69 | Previously missed: recent-conversation clicks discard the open promise, hiding 404/500/network errors. Medium. | Fixed a93ba39: catch click errors only for the current selection generation. Targeted browser 3/3 and full browser 12/12 cover current errors and held stale failures after Settings navigation, with no unhandled rejection. |
| F70 | Previously missed: recovery string-only validation accepts malformed/empty identifiers and locks chat around an invalid SSE URL. Medium. | Fixed a93ba39: nonempty GUID conversation/request/optional turn IDs and nonblank text within 8,000 characters required before restoring state. Targeted and full browser 12/12 pass invalid values with a real root, cleared record, unlocked controls and no event requests. |
| F71 | Independent review of a93ba39: terminal duplicate pre-read can return an expired root's turn directly; retention after the read deletes it before 202, leaving a missing accepted SSE target. Medium. | Fixed ba2403e: owner-scoped duplicate acceptance revalidates/touches activity in the immediate transaction, full queue does not reject duplicates, owner-less duplicates remain read-only. Release integration 145/145 and unit 93/93 cover cleanup before/after acceptance, original ID/readable terminal SSE, monotonic activity and capacity. Independent follow-up substantiated correction. |
| F72 | Inline review: dark-scheme error red fails 4.5:1 contrast on common dark canvases. Medium. | Fixed 8cf22fd: explicit light/dark canvas and foreground, preserve light error red and use lighter dark red. Full browser 16/16 computes actual foreground/canvas contrast and requires at least 4.5:1 in both schemes. Independent calculation: 4.572873 light, 7.980275 dark. |
| F73 | Inline review: failed pending-record removal nulls memory, hides recovery and overwrites the storage error with a missing-conversation notice. Medium. | Fixed 8cf22fd: memory cleared only after removal succeeds; boolean failure stops missing recovery reset/reconnect and preserves error/state. Terminal/rejection callers honor failure. Full browser 16/16 covers accepted/unaccepted blocked removal across reload and successful cleanup; independent actual-script probes cover all callers. |
| F74 | Previously missed: expected errors omit structured correlated Problem Details (404, cursor 400, cookie 401/403, limiter 429 and ad hoc conflict). Medium. | Fixed 44d5039: shared customization/status handling with Accept-independent fallback; Release integration165/165 including20 new exact HTTP response regressions, coverage pass. |
| F75 | Previously missed: retention after conversation/stream authorization can delete terminal history, ending SSE without terminal output and permanently locking recovery. Medium. | Fixed 44d5039/6d99f6e/1f37040: verify owned history on stream error, failure-aware404 cleanup, connecting reconnect versus CLOSED reload guidance, stale source/turn/generation guards. Full browser23/23 covers missing/empty stream, failed storage, transient lookup and late lookup after newer selection. |
| F76 | Previously missed: authenticated chat displays sign-in guidance in the visible profile region. Low. | Fixed 44d5039/6d99f6e/1f37040: neutral checking-session then signed-in on success;401 preserves bootstrap or restores sign-in. Browser23/23 covers login/reload/status500/401 transitions. |
| F77 | Independent 44d5039 review: unconditional 401 sign-in mode reset hides first-run bootstrap; signed-in profile set before authenticated call succeeds. High. | Fixed6d99f6e/1f37040: preserve bootstrap on401, signed-in only after authenticated status success. Full browser23/23 verifies first-run bootstrap fields/profile and normal authentication. |
| F78 | Independent review: transient stream-check error persists after delivered events/completion. Low. | Corrected: source-owned recovery message cleared on next event only when it still matches the alert; unrelated errors untouched. Browser exercises delivered event clearing. |
| F79 | Independent review speculative: fallback Problem Details may omit framework traceId present in normal JSON. Low. | Not applicable to the accepted response contract: required server correlationId/status/title/content-type are enforced for all Accept modes; optional framework traceId is not a promised field. No privacy defect or required-field inconsistency established. |
| F80 | Independent review: non-200 SSE can permanently close EventSource even when owned history remains; automatic-reconnect docs overclaim. Low. | Corrected: CLOSED source receives explicit reload-to-resume notice, preserving request state; connecting source keeps automatic cursor reconnect. Browser adds500 stream/200 history case; docs distinguish closed/connecting states. |
| F81 | Follow-up review: CLOSED stream plus failed root lookup lacks reload guidance. Low. | Corrected: non404 lookup error appends reload guidance whenever source is CLOSED. Browser failed-lookup asserts guidance; connecting cases retain reconnect. |
| F82 | Follow-up review: status500 leaves visible authenticated chat with sign-in profile guidance. Low. | Corrected: neutral checking-session status before authenticated status lookup, signed-in only after success. Browser asserts neutral chat during500 and signed-in after recovered reload. |
| F83 | Final bounded review: F74–F77 ledger dispositions still pending before validation completes. Low/process. | Fixed final evidence revision: F74–F77 now record fixing commits and passing checks; all F1–F83 retained with explicit dispositions. |

Follow-up validation on October 9, 2026: `tools/validate.sh build`,
`integration` (126/126), `browser-e2e` (1/1 plus expected failing probe),
`coverage` (all thresholds passed), and `docs` passed serially after F24–F27.
The base was fetched and remains unchanged from the implementation base.
GitHub checks on the preceding head were passing; new-head CI is separate.

The same targeted gate sequence passed again after F28–F31: build,
integration 126/126, browser E2E 1/1 with expected failure probe, coverage,
and docs. Changed executable lines reached 97.9% (731/747).
Accepted-turn recovery now extends the earlier F18/F22 behavior: records
remain until terminal event delivery, rather than being cleared on acceptance.
All eleven reported GitHub checks passed on the preceding `282de20` head;
required-check configuration was not readable by the app, and new-head CI
must be evaluated separately.
On the subsequent October 9 follow-up, authenticated REST retrieval of
`branches/main/protection` succeeded: strict/up-to-date checks require
`build-and-analyzers`, `architecture`, `unit-tests`, `integration-tests`,
`sdk-contracts`, `coverage`, `aspire-e2e`, `browser-e2e`, and `docs`.
Conversation resolution is required; force pushes/deletions are disabled;
no approving-review-count requirement is configured. This supersedes the
earlier configuration-access limitation, but is configuration evidence, not
a negative branch-protection enforcement demonstration.

After F39–F49, the complete serial command
`tools/validate.sh build && tools/validate.sh unit &&
tools/validate.sh integration && tools/validate.sh architecture &&
tools/validate.sh sdk-contracts && tools/validate.sh coverage &&
tools/validate.sh aspire-e2e && tools/validate.sh browser-e2e &&
tools/validate.sh docs` passed against the final production revision
`cd3f0e9`. Results: build/formatting/analyzers passed, unit 91/91,
integration 132/132, architecture 6/6, SDK contracts 25/25 plus the actual
pinned runtime contract harness, Aspire API E2E 4/4, browser 2/2 plus the
expected failing probe, coverage/report/discovery negative fixtures, and docs.
Independent-review follow-up changed tests/docs only; build/integration
(133/133)/coverage/docs were rerun successfully. The complete run's browser
stage included the strengthened hidden-label/page-marker assertions.
Final changed-line coverage is 98.2% (809/824); Web is 98.6% lines (682/692)
and 80.9% branches (110/136). No gate was disabled, and out-of-process E2E
is not included in the in-process coverage totals.

Independent static production review of `70dc9da..cd3f0e9` found no significant
production defects and substantiated the cleanup/health/auth/CSS/fixture
changes, but explicitly omitted test bodies and task/spec/ledger context.
A narrowly scoped independent acceptance-test/documentation review then
verified the first-run, duplicate-submit, secret-clearing, cookie-restart and
cleanup-recovery assertions and reported F47–F49 evidence gaps.
Their verifying test changes and passing runs are recorded above; neither
review independently ran the long suites or exercised live providers.
All F1–F49 findings have explicit retained dispositions. The earlier browser
failure exposed F46 and was fixed at the CSS root, not by weakening tests.

Follow-up owned files: Web cleanup worker/composition, authentication browser
script and hidden-state CSS; integration cleanup/health/logging regressions;
browser first-run/restart tests and isolated fixture; Web README, operations,
testing matrix and this brief. No new schema or Application contract changes
in F39–F49. The PR validation table is updated to these current results;
new-head CI remains a separate gate before merge, and no merge is authorized.

After F32–F37, `tools/validate.sh build`, `integration` (131/131),
`architecture` (6/6), `browser-e2e` (1/1 plus expected failing probe),
`coverage`, and `docs` passed. The final error-handler/concurrent-verifier
regressions were followed by another build/integration/coverage/docs run.
Changed executable lines reached 98.1% (788/803); Web reached 98.5% lines
(661/671) and 80.6% branches (108/134). The latest unit report (91/91) was
reused by coverage; SDK and Aspire API gates were not rerun for this
request/authentication follow-up, and new-head CI remains a separate gate.
Two intermediate browser runs exposed F37; the final run passed after its
root cause was fixed without changing thresholds or test assertions.
The Application account contract now has a conditional verifier-upgrade
operation; no further schema migration is required.
The preceding CI docs failure was a remote GitHub link-check 503/504 on
unchanged SDK documentation URLs; its failed jobs were rerun without
removing links or weakening the check. Local docs validates internal links,
not availability of those remote URLs.

Independent follow-up review inspected `2f8e065..ad5e79f` against the complete
F1–F37 ledger and task/spec/ADR requirements. It reported the medium-severity
F38 recovery-surface defect and exercised it with the actual JavaScript in a
focused harness; it did not rerun the long suites or credentialed providers.
F38 is dispositioned above and verified by the final browser regression with
a saved running turn and failed startup status. After that correction,
build, browser E2E (1/1 plus expected failing probe), coverage, and docs passed
again; integration/architecture results remain those recorded above.
All F1–F38 dispositions remain in the cumulative ledger. These automated
checks and focused independent review do not certify unexercised live behavior.

- Acceptance: M1-03 scope and scenarios listed above, including the conditional
  owner-verifier upgrade follow-up; no additional schema change.
- Original implementation validation (October 9, 2026): ran
  `./tools/validate.sh build && ./tools/validate.sh unit &&
  ./tools/validate.sh integration && ./tools/validate.sh architecture &&
  ./tools/validate.sh sdk-contracts && ./tools/validate.sh coverage &&
  ./tools/validate.sh aspire-e2e && ./tools/validate.sh browser-e2e &&
  ./tools/validate.sh docs`; all gates exited successfully. Results: Release
  build and formatting passed; unit 91/91; integration 126/126; architecture
  6/6; actual pinned Copilot SDK runtime contract executed its read-only tool
  successfully and SDK contracts 25/25; coverage/report/discovery negative
  fixtures rejected invalid inputs and coverage thresholds passed; Aspire E2E
  4/4; browser E2E 1/1 and deliberately failing browser-gate probe failed as
  expected; docs validation passed. Earlier intermediate browser runs failed
  while stabilizing the new deterministic regression fixture; the final
  complete validation run includes the corrected fixture and passed.
- Independent review: final review round inspected the F1–F23 ledger and
  current behavior. The earlier F17/F21 test setup finding was corrected by
  gating a conversation GET before submission and releasing it only after
  terminal completion; the final browser E2E passed. The reviewer requested
  exact-tree validation results and a complete ledger reference; this section
  records both. The earlier F1–F19 findings and subsequent F20–F23 findings
  remain individually dispositioned above, including F9's ADR-based
  not-applicable rationale. No independent reviewer commit exists because
  this review was performed against the uncommitted worktree.
- Limitations: simulator/E2E fixtures establish deterministic composition and
  UI behavior only. Native Ollama/provider and real Home Assistant/device
  behavior are excluded from this milestone and were not exercised.
F66 follow-up scope: chat recovery parsing/removal, isolated browser
regressions and Web/testing documentation. Objective is to restore access
after malformed or obsolete tab records without silently ignoring storage
errors. Invalid JSON/shape records are removed with an explicit notice;
failed removal stops initialization with an error and retains the record.
No schema, contract or dependency change. Completion requires anonymous and
authenticated recovery, failed-removal retention, subsequent reload access,
build/browser/docs gates and independent review.

F66 validation: `tools/validate.sh build && browser-e2e && docs` passed on
`e24d765`: Release formatting/analyzers/build, browser 8/8 plus expected
failing probe, discovery and internal Markdown links. The first browser run
was 7/8: the removal-failure fixture seeded data before initial anonymous
startup finished, allowing that startup to remove it. Waiting for the auth
surface before seeding fixes the fixture race; no assertion was weakened.
No C# production changes; unit/integration/architecture/SDK/coverage/Aspire
were not rerun for this JavaScript/browser-only follow-up, and earlier results
remain separately recorded. New-head CI remains required.

Independent review of exact `e24d765` against `ca30eca` inspected every
changed code/test/doc hunk and surrounding recovery/start/auth control flow.
It found no significant defects and substantiated invalid-record clearing,
blocking failed-removal behavior, anonymous/authenticated test paths and the
fixture startup wait. It did not run builds/browser suites independently.
Its minor observation about local parse-error text is dispositioned as F67.
All F1–F67 remain retained. Docs reran after final evidence; no schema,
contract/dependency change, live providers or household credentials.

F68–F70 follow-up scope (owner authorized all fixes): preserve owner-scoped
submission under concurrent retention and expose only current browser
navigation failures; reject corrupt recovery values before locking chat.
Applies specification sections 12, 14, 19 and 20 and the same M1-03
ownership, durable history, safe error and recovery scenarios. Owned files:
Application turn request/submission contracts and coordinator, SQLite
conversation store, Web API/chat script, corresponding integration/unit and
browser tests, Web/testing documentation and this ledger. No schema,
dependency, cloud, device or product-scope expansion. Existing M1-02
coordinator/persistence contracts are prerequisites. Assumption: non-Web
callers may intentionally create roots implicitly; browser requests must not.
Completion requires deterministic deletion/submission ordering, wrong-owner
rejection, duplicate behavior, current/stale navigation failure and invalid
value recovery regressions; relevant build/runtime/coverage/E2E/docs gates,
independent exact-revision review and complete finding dispositions.

Initial F68–F70 validation: targeted new browser tests 4/4, build and unit
92/92 passed. Integration collector stalled while processing 942 numbered
duplicate files in generated integration output; stopped before a result and
rebuilt only `tests/PersonalAgent.IntegrationTests/bin/Release/net10.0`.
No collector configuration, thresholds or validation gate changed. Full
remaining gates pending. Independent a93ba39 review substantiated F68–F70
and identified F71; no independent suite execution.

F71 correction ba2403e extends activity/recent-list ordering only for
authenticated duplicate retries; it does not re-execute inference or alter
trusted owner-less duplicates. Optional required-owner fields on Application
request/submission contracts and lookup are the contract impact; no schema or
dependency change. Two integration race cases initially failed before
startup because the plain store proxy did not satisfy the default engine's
atomic-outcome interface. The fixture now uses the existing deliberate
PausedDurableEngine adapter (production coordinator and real SQLite retained);
targeted Debug integration 6/6 and unit 2/2 passed, followed by Release build,
integration 145/145 and coverage gate. No assertion/gate weakened.

Current gates: build passes; unit 93/93; integration 145/145; architecture
6/6; SDK 25/25 plus actual pinned runtime controlled-loopback harness; Aspire
7/7; merged coverage and negative report/discovery fixtures pass.
Changed executable lines 870/886 (98.2%); Application 947/961 lines and
304/323 branches; Infrastructure 2515/2716 lines and 594/781 branches;
Web 690/701 lines and 111/138 branches. Browser 12/12 plus expected failing
probe, discovery and docs passed; browser/Aspire not counted as in-process
coverage.

Independent follow-up review of ba2403e against a93ba39 plus the test-only
engine injection found no additional significant issue. It traced immediate
transaction owner-before-key validation, monotonic duplicate activity commit,
cleanup-winning 404 versus acceptance-winning retained SSE, full-queue
duplicate slot release and defensive missing-key terminal rejection, trusted
owner-less semantics, and inspected deterministic API/store/unit assertions.
It did not independently execute suites. Deterministic ordering is not
multi-process stress/load evidence; no provider/device/household credentials
used. Main refreshed again: zero commits behind.

Commands actually run for final evidence: `tools/validate.sh build`, `unit`,
`integration`, `architecture`, `sdk-contracts`, `coverage`, `aspire-e2e`,
`browser-e2e`, `docs`; all passed as above after the recorded fixture fix.
Unit/architecture/SDK/Aspire/browser apply to ba2403e production revision;
the final build/integration/coverage include the test-only engine injection.
Final docs and whitespace checks rerun before push. Full F1–F71 ledger
retained with explicit dispositions. Required new-head CI remains separate
and is not represented by preceding-head green checks. No merge performed.
The larger follow-up diff is cohesive: optional owner contract plumbing
requires updating all store fakes, and most added lines are deterministic
race/browser regression fixtures, not speculative production features.

F72–F73 follow-up objective: accessible error contrast and explicit,
recoverable storage-removal failure without pretending cleanup succeeded.
Applies spec host UI/error/recovery acceptance, sections 12, 14, 19 and 20.
Owned files: Web chat script/CSS, BrowserSmokeTests, Web README, testing matrix
and this brief. Dependencies: existing M1-03 recovery/navigation and isolated
browser fixture. No C# contract/schema/dependency or provider scope change.
Completion: both color schemes meet measurable 4.5:1 contrast; missing
accepted/unaccepted records preserve state/error and avoid SSE on failed
removal; resumed storage permits cleanup/unlock; build/browser/docs and
independent review. Prior ad3216d CI now reports all eleven checks SUCCESS,
contradicting the earlier injected pending state; fresh-head CI still required.

F72–F73 validation: `tools/validate.sh build && browser-e2e && docs` passed
for 8cf22fd: Release formatting/analyzers/nullable/warnings-as-errors,
browser 16/16, discovery, expected failing probe and internal links.
No C# runtime changes; unit/integration/architecture/SDK/coverage/Aspire
not rerun for this JS/CSS/browser-only follow-up. Preceding full gate evidence
remains above; new-head CI is separate.

Independent exact 8cf22fd against ad3216d review read all six changed files,
surrounding JS/CSS/markup, spec/ADRs/task ledger and tests/docs. No significant
issues found. It substantiated all three cleanup callers, object/generation
guards, missing404 false-versus500 reconnect and computed contrast. Six
JavaScriptCore probes executing the actual script passed accepted/unaccepted
missing removal failure then success, accepted500 reconnect, terminal failure,
terminal success and definitive rejection failure then success. No independent
build/browser execution. Parent browser establishes Chromium computed-style
contrast and missing-root behavior; terminal/rejection failures have probe,
not dedicated browser, coverage. Failed-removal late-generation interleaving
was inspected, not executed; non-Chromium/forced-color/assistive-technology
behavior remains unverified. Full F1–F73 ledger retained.

F74–F76 owner-authorized scope: consistent safe expected-error payloads,
missing SSE target recovery and truthful authentication status. Spec sections
12/14/19/20; M1-03 structured errors/correlation, owner-scoped lookup and
reconnect/durable-history scenarios. Owned files: Web Program/API/script,
integration/browser regressions and affected Web/testing docs/ledger.
Dependencies: existing owner auth, persisted history and F73 failure-aware
cleanup. No schema/Application contract/provider/product expansion.
Completion: real HTTP expected status/content-type/title/correlation evidence,
SSE error missing/transient/storage/stale behavior, authentication transitions,
relevant build/integration/coverage/browser/docs gates and independent review.

F74 implementation uses shared Problem Details customization/status-code
pages within correlated exception middleware. Default JSON writer handles
normal Accept; a fallback writer preserves safe correlated JSON for pre-stream
SSE/HTML Accept errors. Existing payloads/success/started streams are untouched.
Targeted Release HTTP integration 27/27 (20 new plus seven existing) passed.
New browser targeted 5/5 plus held-stream lookup 1/1 passed.
Initial full build rejected three initializer formatting lines; repository
formatter corrected them and Release build passed. Integration collection
again encountered 585 numbered generated duplicates; stopped before result,
rebuilt only integration generated bin output without gate/settings changes.
Current rerun integration 165/165, architecture 6/6, Aspire 7/7 and merged
coverage pass. Changed executable lines 888/902 (98.4%); Web 708/717 lines,
113/140 branches. Coverage uses preceding unchanged Application/Domain unit
93/93 report; no unit/SDK rerun for Web-only changes. Full browser/docs and
independent exact44d5039 review pending.

Independent 44d5039 review substantiated middleware ordering, fallback writer,
safe correlation/title handling and recovery stale-source/object/generation
guards, but found F77–F80 above. Its actual-script stub DOM probe reproduced
the first-run regression versus4c99893. It read relevant spec/ledger portions,
not the full historical ledger/ADRs, and did not run suites. Browser run was
stopped before result while corrections were applied; not counted as a pass.
Follow-up preserves first-run mode, delays signed-in status, clears only owned
recovery alerts on events and explicitly reports CLOSED source reload recovery.

Follow-up exact6d99f6e review reproduced F77 fix, F78 matching-alert clearing
without erasing unrelated alerts, and CLOSED successful-lookup guidance using
actual-script stub DOM/EventSource probes. F79 accepted not applicable against
spec12 correlated safe-error requirement. It identified minor F81/F82, now
corrected as above. Applicable AGENTS/fullledger/spec sections read; ADR
reconnect/interruption relevance searched rather than every ADR line reread.
No independent suites. Script-dispatched TextDelta checks alert handler
behavior, not real delivery on a permanently closed stream.
An intermediate browser run failed the existing simulator scenario at a
post-status500 reload (chat hidden) before completing; the added profile
scenario now owns isolated host/data to avoid shared authentication/rate
budget interference. Cause not independently established; no gate/assertion
weakened and full rerun required.

Final bounded exact1f37040 against6d99f6e review verifies F81/F82 using
actual-script stub DOM/fetch/EventSource probes: CLOSED+500/network appends
reload guidance and preserves turn; CONNECTING+500 retains automatic
reconnect without reload text; CLOSED+200 guidance remains; matching-alert
clearing works. Status500 shows neutral checking-session text, success
signed-in, first-run401 preserves setup form/profile. No further code
findings; F83 is pending-ledger finalization. It confirmed all82 preceding
rows exist without gaps/duplicates and inspected F74–F82 dispositions;
prior fullledger read reused for earlier findings. No independent suites,
Chromium timing or fixture-failure cause investigation. Final browser result
still pending; no failing/unrun check represented as success.

Final F74–F83 evidence: Release build and full browser23/23 plus discovery,
expected failing probe and docs passed on1f37040. Earlier integration165/165,
coverage (changed888/902,98.4%), architecture6/6 and Aspire7/7 passed on
44d5039; later revisions only JS/browser/docs, so those results are retained
without claiming rerun. Unit93/93 and SDK25/25 plus actual controlled runtime
harness are preceding evidence, not rerun for these Web changes. Final docs
and whitespace rerun before push. No schema/dependency/Application contract
changes. Full cumulative F1–F83 ledger dispositioned; F79 optional traceId
not applicable to required correlated response contract. No merge performed;
fresh-head CI remains required. Closed streams with existing history require
explicit reload, preserving request identity; no arbitrary browser retry loop.
