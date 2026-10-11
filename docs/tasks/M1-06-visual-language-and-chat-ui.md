# M1-06: Visual language and existing chat UI polish

## Objective

Apply the owner-selected visual language to the existing local chat,
authentication, conversation navigation, settings, and activity surfaces, and
document its reusable rules. The two isolated alternatives remain available
for future reference; Soft editorial is the primary production direction.

## Specification and scenarios

- Issue #42; project specification §§12 (Host API and UI), 14 (Acceptance and
  evaluation), 16 (milestones), 19 (architecture/documentation), 20 (test and
  coverage gates), and 21 (agentic development and review).
- Existing M1-03/#11 browser and API behavior is preserved.
- Conversation title, excerpt, rename, and creation-time presentation integrates
  M1-05/#40's accepted behavior from merged PR #43, including its schema-v5
  title provenance and race-safe browser updates.

## Scope

- Keep both alternatives in `docs/design-previews/m1-06-visual-directions.html`
  as isolated reference material. The owner selected **A — Soft editorial** as
  the primary direction; preserve the option to revisit the alternatives later.
- Establish shared CSS tokens and focused reusable styling in the existing
  Razor/minimal-JavaScript UI. The approved direction uses warm paper, charcoal
  text, a restrained terracotta accent, generous reading space, and subtle
  system-serif headings/message prose, with crisp sans-serif navigation and
  controls. No external runtime fonts/assets.
- The message composer sends on Enter and inserts a newline on Shift+Enter.
  Prevent accidental send during IME composition and modified Enter chords.
- Polish chat/auth/navigation/settings and activity without adding future
  product pages or changing server behavior.
- Preserve visible truthful route/provider/model, readiness, errors, streaming,
  cancellation, reconnect, and disabled states. Incorporate #40 naming,
  timestamp, excerpt, and manual-rename presentation when its accepted
  implementation is available.
- Support narrow mobile, tablet, and desktop widths; long titles/messages;
  keyboard interaction and visible/unobscured focus; readable contrast; zoom
  and reflow; reduced motion; and non-color-only status. Avoid disruptive
  repeated assistive announcements during streaming.

## Exclusions

No new assistant capabilities, cloud/device operations, settings behavior,
theme engine, SPA/frontend framework rewrite, title persistence or generation,
engine/API/schema redesign, speculative component platform, unrelated
refactors, or unapproved material interaction changes. Preview alternatives do
not alter the live product experience.

## Owned files and boundaries

- `src/PersonalAgent.Web/Pages/Index.cshtml`,
  `src/PersonalAgent.Web/wwwroot/css/site.css`,
  `src/PersonalAgent.Web/wwwroot/js/chat.js`, and `src/PersonalAgent.Web/README.md`.
- Focused scenarios in `tests/PersonalAgent.E2ETests/BrowserSmokeTests.cs`.
- `docs/testing.md`, `docs/Personal-Agent-Project-Spec.md`, project
  documentation/scenario mapping, and this task brief.
- Any preview-only files are isolated documentation artifacts; do not expose
  preview/test controls in production profiles.

Web continues to call Application use cases. Do not introduce presentation
changes that bypass authentication, CSRF, owner authorization, SSE ordering,
idempotency, or cancellation semantics. No durable schema or application
contract change is expected from M1-06 itself.

## Dependencies and assumptions

- M1-03/#11 is the baseline; existing Razor markup, CSS/JavaScript, and
  Playwright/Aspire composition are the implementation surface.
- M1-05/#40 was merged in PR #43 (`91aaa9a`). Retain its schema migration,
  accepted-turn metadata, manual-title precedence, timestamp descriptions,
  and stale-read/rename protections. Style its controls without replacing its
  application or persistence logic.
- The primary production visual direction is approved, but the owner may
  change their preference later; retain both previews for reference.
- Preserve the pre-existing light/dark system color-scheme behavior with
  matching Soft editorial palettes; this is not a configurable theme engine.

## Tests and documentation

- Keep existing authenticated chat, navigation, settings/activity, streaming,
  cancellation, reconnect, provider-failure, and recovery behavior covered.
- Verify Enter-to-send and Shift+Enter newline handling through Playwright,
  including exactly one request per Enter and no submission for the newline.
- Add Playwright assertions for desktop/tablet/mobile layout; representative
  authentication/chat/settings/activity states; long messages and titles;
  visible keyboard focus and keyboard operation; and supported zoom/reflow.
- Use measurable layout and contrast assertions where practical; avoid brittle
  whole-page snapshots. Do not claim complete WCAG conformance from automation
  alone. Use controlled fixtures and production Simulator/E2E composition.
- Document approved typography/fallbacks, color/semantic states, spacing,
  reading width, control treatment, responsive layout, accessibility choices,
  and known gaps. Update the scenario mapping and affected Web/testing guidance.

## Completion criteria

- Two representative alternatives have the same controlled content and
  functionality; the owner explicitly selects the direction before broad UI
  changes.
- Implemented surfaces consistently follow the approved brief and retain the
  existing M1-03 behavior, plus #40 naming presentation when available.
- The applicable required build, architecture, integration/browser E2E,
  coverage, and documentation checks pass. Report only checks actually run.
- Independent agentic review findings have explicit dispositions; handoff
  states exact revision/check evidence, platform/runtime limitations,
  unexercised behavior, and residual risks.

## Main integration and review

The owner's requested main integration uses a merge rather than a rebase:
`f73bad3` checkpoints the previously uncommitted M1-06 work, and `b25b55e`
merges main's `91aaa9a` (PR #43) without rewriting either history.
Four textual conflicts in the specification, testing map, Razor page, and
JavaScript were reconciled by preserving both task entries and combining
M1-05's metadata/rename behavior with the approved presentation.

The schema-v5 migration and backend naming implementation are imported
unchanged. Header/list titles retain manual precedence, accepted-turn metadata
updates, timestamp descriptions, and stale-list/read protections. Rename and
timestamp styles use the existing design tokens. The conversation container
uses flex layout so showing the optional rename form does not displace the
message/composer grid tracks.

Independent read-only agentic review examined `b25b55e` against both parents
and the M1-05 cumulative ledger. It did not execute tests or browser rendering.
Its scope was merge-specific correctness, not a full M1-06 design review.

| Ref | Finding | Disposition and evidence |
|---|---|---|
| MI-01 | Medium: M1-05's timestamp sibling could be clipped on mobile by M1-06's full-height conversation button within a vertically clipped horizontal list. | Fixed: remove the mobile button's `height: 100%`. Extend `SoftEditorialChatReflowsWithoutOverflowAndKeepsLongMessagesReadable` to assert timestamp bounds lie within the list, not merely DOM visibility, at 1440/768/390/320px. |
