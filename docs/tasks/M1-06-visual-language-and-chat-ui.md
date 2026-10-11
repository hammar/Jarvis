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
  M1-05/#40's accepted behavior. Issue #40 is open; this task must coordinate
  with its resulting API/UI contract rather than duplicate its persistence or
  title-generation work.

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
- M1-05/#40 is open and specifies persistent first-message titles, manual
  renaming, and secondary creation timestamps. Integrate its agreed surface
  when available; if it has not merged, do not invent a competing contract or
  claim naming-presentation acceptance complete.
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
