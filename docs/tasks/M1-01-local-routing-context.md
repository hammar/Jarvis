# M1-01: Local-only routing and bounded conversation context

## Objective and acceptance

Implement issue #7 as the M1 routing/context slice. The Application layer
selects a local provider only, or returns an explicit clarification or
unsupported decision. It builds an inspectable, bounded context from durable
conversation history, with provenance/privacy labels and a complete prompt
envelope estimate. Application state remains authoritative; user/model text
cannot change route policy.

**Specification:** §§5–6, 9, 13–14; architecture, test and handoff
requirements §§18–21. **Acceptance scenario:** “LocalOnly request tries to
escalate”; include unsupported/ambiguous task handling, bounded context and
privacy/provenance behavior. Depends on T02 contracts and completed T03 (#4)
and T04 (#6). The next implementation task is M1-02 (#12).

## Scope

- Provide typed `Local`, `Clarify` and `Unsupported` route outcomes with stable
  reason codes and policy version. M1 supports LocalOnly only.
- Reject unsupported route modes and malformed inputs explicitly. Never
  downgrade a cloud selection to local, invoke cloud, or call a cloud
  helper/summary path.
- Use explicit host-classified task categories; unclassified or ambiguous
  work is clarified rather than inferred from untrusted text, and unsupported
  work is reported rather than claiming unimplemented capabilities.
- Build context from the existing Application conversation-store port.
  Include a bounded number of recent messages in order, each with a stable
  source reference and LocalOnly privacy classification. Retrieved text is
  untrusted data, not policy or instruction.
- Bound task, instruction, tool-schema, history and aggregate context inputs
  using documented fixed defaults and hard validation.
- Report component-level context estimates, including host instructions,
  current task, registered tool schemas and selected history. If exact
  tokenizer counts are unavailable, label the estimate and reserve explicit
  margin. Fail explicitly if mandatory inputs exceed the hard envelope.
- Register the production implementation at the Web composition root without
  adding provider, SQLite or ASP.NET Core dependencies to Application.

## Exclusions

No cloud packets, consent/hash binding, spending budgets, cloud
reclassification or provider helper; no automatic summary; no tools or
approvals; no memory features; no UI, turn workflow or new database schema.
These belong to later issues/milestones as scoped by issue #7.

## Owned files

- `docs/tasks/M1-01-local-routing-context.md`
- `src/PersonalAgent.Application/Contracts.cs` and new Application routing /
  context implementation files
- `src/PersonalAgent.Web/Program.cs` for production composition only
- `tests/PersonalAgent.UnitTests/` and
  `tests/PersonalAgent.IntegrationTests/`
- `src/PersonalAgent.Application/README.md`
- `tools/PersonalAgent.Validation/Program.cs` for critical-module ownership
- `docs/architecture.md` and `docs/testing.md`

## Decisions and assumptions

- Use the owner-selected documented conservative fixed defaults with hard
  validation; do not add environment/configuration knobs in this slice.
- Require a host-classified task kind; an omitted category defaults to
  clarification, not a local workflow.
- Keep route/context policy in Application. Read persisted history through
  `IConversationStore`; Infrastructure remains the SQLite adapter.
- Preserve existing Application/Domain dependency rules and avoid migrations.
- Context estimates are explicitly estimates unless a validated tokenizer is
  available. Include and report the reserved margin; do not present estimates
  as exact provider accounting.
- Maintain separate route outcomes: a request for cloud is not an implicit
  local fallback, and missing/ambiguous functionality is not success.

## Tests and documentation

Unit tests cover LocalOnly routing, unsupported/clarify outcomes, deterministic
reason/version values, hostile text that attempts to alter policy, invalid
inputs, default bounds and complete estimate accounting. Integration tests use
an isolated real SQLite store to verify recent ordered history, bounded
selection, provenance/privacy labels, and all estimate components and margin.
Unit tests assert explicit failure when mandatory prompt components exceed
limits. Add the LocalOnly escalation and context cases to the scenario matrix.

Maintain the Application unit-only coverage gate (90% lines / 85% branches),
critical context/policy gate (95% / 90%), combined runtime and changed-line
gates, architecture checks, and all applicable repository validation. Update
the Application project README and architecture/privacy guidance. No live
model, cloud, credential or device evidence is required by this issue.

## Completion criteria

- Production composition resolves the real application-owned router and
  context builder.
- Every M1 decision is local, clarification, or unsupported; cloud and
  helper paths are not reachable from LocalOnly.
- Context is bounded, ordered, inspectable, classified and traceable to
  SQLite source messages; all prompt components and estimate limitations are
  visible, and invalid/over-budget inputs fail explicitly.
- Required tests, coverage, architecture and documentation checks pass.
  Report exact commands/results, changed files and contract impact, independent
  review findings/dispositions, and any unverified behavior without overstating
  the evidence.

## Validation and review evidence

- `./tools/validate.sh build` — passed Release formatting and build with zero
  warnings/errors.
- `./tools/validate.sh unit` — 21 discovered, 21 passed.
- `./tools/validate.sh integration` — 81 discovered, 81 passed, including the
  isolated real-SQLite context selection and complete estimate assertions.
- `./tools/validate.sh coverage artifacts/coverage/unit/20261006T063357Z-94130 artifacts/coverage/integration/20261006T064357Z-97774`
  — passed required thresholds: Application 99.7% lines / 98.4% branches,
  Infrastructure 92.4% / 77.8%, and changed executable lines 99.6%.
  Critical router/context 95% / 90% module gates also passed.
- `./tools/validate.sh architecture` — 6 passed.
- `./tools/validate.sh docs` — all internal Markdown links resolve.
- `./tools/validate.sh sdk-contracts` — actual pinned Copilot runtime
  validation and 17 SDK contract tests passed. Runtime validation used
  controlled loopback fixtures, not live cloud or household services.
- `./tools/validate.sh aspire-e2e` — 2 passed.
- `./tools/validate.sh browser-e2e` — 1 browser test passed; the deliberately
  failing browser-gate probe was discovered and rejected as expected.

An independent read-only code review of the worktree diff against base commit
`3b3d18f` reported no significant issues and no actionable findings. The
initial finding ledger had no issues. A subsequent PR review identified and
the implementation addressed:

| Reference | Finding | Disposition and evidence |
| --- | --- | --- |
| F-001 | Capped history read exposed a lower-bound omitted-message value as an exact total. | Fixed in `5670ea8` by renaming to `MinimumOmittedHistoryMessages`, documenting its lower-bound semantics and retaining the `HasMoreHistory` sentinel. Verified with a 100-message real-SQLite fixture and bounded 13-row read; `./tools/validate.sh integration` passed 81/81, and the coverage gate passed. |

No schema/migration changes were made. Live local-model, cloud, credential,
and physical-device behavior was not exercised and is not required for issue
#7. End-to-end route invocation remains with the later turn-workflow task;
this issue verifies the application router’s explicit decisions and provides
no cloud invocation/helper path of its own.
