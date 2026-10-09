# PersonalAgent.Web

Owns the ASP.NET Core/Razor host, owner authentication, authenticated chat APIs,
SSE delivery, health endpoints, and composition root. Request handlers use
Application-owned contracts. Infrastructure references are confined to
`Program.cs`; ServiceDefaults supplies service discovery and
privacy-filtered telemetry.

## Owner setup and sessions

At first startup, the host prints a one-time bootstrap token to its local
console. Keep the token local and enter it in the setup form; it is not placed
in a URL. Choose a passphrase of at least 12 characters. Jarvis stores only an
ASP.NET Identity adaptive salted password hash in SQLite. The browser receives
an HttpOnly, SameSite=Strict session cookie, protected by persisted ASP.NET
Data Protection keys in the private `JARVIS_DATA_DIR`. Sign-in sessions expire
after 12 hours and may slide while active. Sign out or clear browser cookies
to end the session. Login/bootstrap attempts share a 20-attempt/minute limit
per client address; rejection returns HTTP 429. Status/CSRF reads and logout
do not consume password attempts. All
cookie-authenticated mutations require an antiforgery token in the
`X-CSRF-TOKEN` header.

Successful sign-in upgrades an outdated adaptive verifier with a conditional
SQLite update. Authentication expiry refreshes anonymous antiforgery state
so reauthentication does not require reloading the page.
Successful bootstrap switches the form to sign-in mode; submissions are
serialized through post-login initialization, and successful authentication
immediately clears the passphrase and setup-token inputs. Failed owner-status
reads show an error instead of guessing an account state.
Wrong-passphrase login responses retain their sign-in failure message rather
than presenting an expired-session notice.

The account and conversations survive application upgrades as long as the
owner keeps the same data directory. A lost passphrase has no recovery path in
this M1 slice; preserve the data directory and use the configured OS account's
local access controls.
The deterministic bootstrap token is injected only by the isolated E2E
profile; normal Simulator, Local, and Hybrid hosts generate a fresh token.

## API and streaming

- `GET /api/auth/status`, `GET /api/auth/csrf`, `POST /api/auth/bootstrap`,
  `POST /api/auth/login`, and authenticated `POST /api/auth/logout`.
- Authenticated `GET/POST /api/conversations` and
  `GET /api/conversations/{id}`. `POST /api/conversations/{id}/turns`
  accepts `{ "requestId": "...", "text": "..." }`; retries with the same
  conversation/request ID and same content return the original turn.
- `GET /api/turns/{id}/events` streams stored ordered events using SSE and
  honors the standard `Last-Event-ID` sequence header. Disconnecting only
  stops delivery; it does not cancel inference. `POST /api/turns/{id}/cancel`
  performs explicit bounded cancellation.
- `GET /api/settings`, `PUT /api/settings/retention`,
  authenticated `GET /health/ready`, and public `GET /health/live`.

Before sending a turn, the browser temporarily stores its text, conversation,
and request ID in tab-scoped `sessionStorage`. This lets an owner safely retry
with the same idempotency key after a lost response or page reload. The record
is augmented with the accepted turn ID so reload restores the SSE stream,
navigation lock, and cancellation target. It is cleared after a terminal
event or a definitive non-retryable client error; it remains available after
an ambiguous failure.
This is browser-local recovery state, not a telemetry or logging channel.
Recovered running turns expose cancellation before startup status/history
reads, so a failed ancillary read does not hide the cancellation or SSE output
surface. Conversation creation is guarded until selection and list refresh
complete to prevent duplicate requests from repeated clicks.

Conversation and turn lookups are owner-scoped in persistence before data is
returned or cancellation is requested. API errors use safe problem details;
responses include `X-Correlation-ID`. Unhandled request failures return
generic correlated problem details without
exposing exception text; requests whose response has already started are
aborted, and request cancellation is not converted into a server error.
JSON request bodies are capped at 64 KiB, turn text at the Application
boundary's 8,000-character limit, and client request IDs at 128 characters by
default. Kestrel also enforces the body cap for requests without
`Content-Length`.
For HTTP/1.1 chunked requests Kestrel counts chunk framing toward its byte
limit, so the maximum usable JSON payload may be slightly smaller than 64 KiB.
SSE reads persisted events in pages of up to 100, drains terminal backlogs
before closing, and does not hold a worker while waiting for
inference.

The root page has Chat, Settings, and Activity surfaces. Settings show only
provider/model and connection configuration status, never secret values.
Only the latest selected surface is revealed after asynchronous navigation;
stale Settings/Activity/conversation reads cannot replace it. Retention
notices and save confirmations use an accessible status region.
Retention periods default to 90 conversation days and 30 audit days; the owner
may save 1–3650 days. Saved values are held in SQLite and survive restarts.
They override the trusted host defaults from
`JARVIS_CONVERSATION_RETENTION_DAYS` and `JARVIS_AUDIT_RETENTION_DAYS`.
Conversation/audit retention settings are read together in one SQLite
snapshot, matching their atomic updates.
Lowering a period removes newly expired eligible conversation/audit history
when saved; conversations with unresolved turns are retained.
The hourly maintenance worker logs failure type (not private exception text),
marks `history-cleanup` readiness degraded, and retries at the next hourly
pass without stopping the Web host. A successful pass clears degradation;
shutdown cancellation still stops cleanup. Degraded readiness remains HTTP
200 under the host's existing readiness policy, with degradation explicit in
the JSON report.

The E2E-only bootstrap token setting is rejected unless
`JARVIS_PROFILE=E2E`. Do not configure test controls in Local, Simulator, or
Hybrid profiles.

Entry point: `Program.cs`; API and SSE routes: `ChatApi.cs`. Tests:
`dotnet test tests/PersonalAgent.IntegrationTests` and
`dotnet test tests/PersonalAgent.E2ETests`.
