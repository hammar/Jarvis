# M1-05: Distinct conversation titles, manual renaming, and creation timestamps

## Objective and acceptance

Implement issue #40 on the M1 Local Chat baseline. Empty conversations must be
distinguishable before a model responds; the first durably accepted user
message supplies a deterministic display title; and an owner's manual title
must persist and never be overwritten by automatic naming.

**Specification:** §§9, 11–12, 14, 16, 19–21. **Prerequisites:** M1-03/#11;
coordinate shared conversation metadata with M1-04/#38. **Scenarios:** M1-05
cases below plus the existing M1 authentication, idempotency, durable history,
and browser recovery scenarios.

## Scope and exclusions

- Add an application-owned title policy and contracts for automatic and manual
  titles.
- Persist explicit manual-title provenance and apply automatic naming in the
  same SQLite transaction as first-message acceptance. Rename is an atomic
  owner-scoped update. A retry with the same request ID never derives or applies
  a second title.
- Add a forward-only migration; preserve nonempty existing titles and leave
  blank titles eligible for first-message naming. Creation instants remain
  UTC.
- Add authenticated, CSRF-protected manual rename and accessible UI list/header
  rendering with an unambiguous local timestamp. Treat titles as plain text.
- Excludes model/LLM naming, cloud disclosure, repeated auto-retitling, title
  uniqueness, SDK history changes and unrelated UI redesign.

## Owned files and dependencies

- `src/PersonalAgent.Application/`: `ConversationTitlePolicy`, conversation
  metadata/store contracts and turn submission metadata.
- `src/PersonalAgent.Infrastructure/Persistence/`: v5 migration,
  registration and `SqliteConversationStore`.
- `src/PersonalAgent.Web/`: `ChatApi`, chat Razor page and JavaScript/CSS.
- `tests/PersonalAgent.UnitTests/`, `tests/PersonalAgent.IntegrationTests/`,
  `tests/PersonalAgent.E2ETests/`.
- `docs/Personal-Agent-Project-Spec.md`, `docs/testing.md`, and directly
  affected project/architecture/development guidance.

## Behavior and assumptions

- Reuse the existing 200 UTF-16 code-unit manual-title bound. Manual titles are
  trimmed, nonempty, and reject control characters.
- Automatic titles normalize Unicode to NFC, collapse all whitespace to one
  space, preserve visible punctuation, and cap at 80 Unicode scalar values
  (using an ellipsis when shortened). Control-only messages use the deterministic
  nonempty title "Message without displayable text" so later turns cannot name
  the conversation instead.
- The automatic title is metadata derived from the first accepted user message,
  not model output or prompt instructions. Manual renaming at any time,
  including before the first message, sets durable precedence.
- Existing nonempty titles are preserved verbatim and remain automatic only
  where provenance cannot be inferred; blank legacy titles remain eligible for
  automatic naming. Existing timestamps are not rewritten.
- Conversation list timestamps use the browser's local time zone, with the
  resolved time-zone name shown for clarity.

## Tests and documentation

- Unit-test normalization, Unicode scalar truncation and punctuation/whitespace
  edge cases.
- Real SQLite tests cover first submission/duplicate semantics, manual override,
  owner scoping, restart persistence and migration preserving old titles.
- Authenticated API integration tests cover rename bounds, empty/control input,
  CSRF and cross-owner/missing-resource rejection.
- Playwright covers multiple empty conversations, visible timestamps, automatic
  naming without model availability, manual rename and safe text rendering
  across reload.
- Add M1-05 scenario mappings and update directly affected docs; run applicable
  mandatory repository gates and independently review the exact revision.

## Review finding ledger

| Ref | Finding | Disposition and evidence |
|---|---|---|
| R1 | Automatic-title control characters could make an otherwise valid turn fail SQLite title validation. | Fixed and independently re-reviewed: automatic-title policy replaces Unicode control runes before whitespace normalization. `AutomaticTitleReplacesControlCharactersBeforeWhitespaceNormalization` and `TurnWithControlCharactersIsAcceptedAndReturnsSanitizedAutomaticTitle` cover policy and accepted API submission. |
| R2 | The active conversation header stayed at its previous title after turn acceptance until a terminal event. | Fixed and independently re-reviewed: the accepted response includes persisted conversation metadata and the browser applies it immediately when the submitted conversation remains selected. `ConversationTitlesAndCreationTimesRemainDistinctAndManualNamesPersist` holds simulator inference after its first frame and checks the active title while the turn is still active. |
| R3 | A pending older conversation read could overwrite a successful manual rename. | Fixed and independently re-reviewed: successful rename invalidates pending selection reads, and conversation-list requests discard superseded responses. The browser scenario delays a pre-rename conversation read, completes the rename, then releases the stale response and verifies the title remains renamed. |
| R4 | Control-only accepted first messages left the title blank and allowed later turns to supply it. | Fixed and independently reviewed: the policy supplies a deterministic nonempty fallback and the API regression checks a later turn cannot replace it. |
| R5 | Conversation timestamps were not associated with conversation buttons for assistive technology. | Fixed and independently reviewed: each timestamp has a conversation-scoped ID referenced by its button's `aria-describedby`; the browser scenario checks unique, nonempty description targets. |
| R6 | A stored title equal to "New conversation" was mistaken for an empty-title placeholder in the rename form. | Fixed and independently reviewed: the browser tracks the stored title independently from its display label. The passing browser scenario covers blank, manual and automatic literal-placeholder titles and reopening after cancelling an unsaved edit. |
| R7 | Follow-up browser regression could reload while the prior submission was still pending, then reject its next submission and falsely treat rejection as idle completion. | Fixed and independently reviewed: wait for durable request cleanup before reload and explicitly assert HTTP 202 for the literal-title submission before waiting for completion. |

The independent reviewer found no new significant issues in the complete
current diff. Review was against the current uncommitted worktree on macOS.

## Validation evidence

- `tools/validate.sh build`: passed, 0 warnings and 0 errors.
- `tools/validate.sh unit`: 105 passed.
- `tools/validate.sh integration`: 212 passed.
- `tools/validate.sh architecture`: 6 passed.
- `tools/validate.sh sdk-contracts`: 55 passed; actual SDK runtime checks passed.
- `tools/validate.sh aspire-e2e`: 7 passed.
- `tools/validate.sh browser-e2e`: 24 passed; the deliberate failure probe
  failed as expected and the validation gate recognized it.
- `tools/validate.sh coverage`: thresholds and negative report/discovery
  fixtures passed; changed executable lines were 132/137 (96.4%).
- `tools/validate.sh docs`: passed.
- `git diff --check`: passed.

PR #43 was opened at `fc8817c`. All GitHub CI checks passed after targeted
reruns in run `38148917311`. The docs failure was a transient GitHub HTTP 503
for an unchanged SDK reference; the same URL subsequently returned HTTP 200.
The Ubuntu SDK failure came from `FakeOpenAiProvider` writing to a disconnected
HTTP client during cleanup in the automatic-compaction deadline test. The
fixture and test logic are unchanged by M1-05; all 55 SDK contract tests passed
on rerun, along with Ubuntu Aspire and loopback containment checks. The
underlying intermittent fixture write/disposal race remains a disclosed risk;
rerun success does not establish a root-cause fix. No gate was weakened.

Follow-up review fixes passed native macOS build, unit (107), integration
(213), browser E2E (24), coverage (134/139 changed executable lines, 96.4%),
and docs checks. An intermediate full browser run failed the unchanged
provider-failure recovery smoke with a timeout; the following full run passed.
No sleeps, test retries or gate exclusions were added. R4-R6 were independently
reviewed against the follow-up diff on `fc8817c`; R7's synchronization
correction received a separate narrow review with no new findings.

## Completion criteria

The acceptance behaviors above pass deterministic unit, real-SQLite/API and
browser checks; the migration preserves existing records; documentation and
scenario mapping agree with behavior; required checks and independent review
have evidence and all finding dispositions; the handoff states actual
commands/results and any unexercised behavior. Do not merge or enable
auto-merge.
