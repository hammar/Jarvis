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
