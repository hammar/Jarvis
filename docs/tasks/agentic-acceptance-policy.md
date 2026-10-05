# Agentic implementation and acceptance policy

## Objective and acceptance

Align repository instructions with the owner's accepted model of agentic
implementation and review as a compiler from higher-abstraction specifications
to lower-level implementation artifacts. The owner accepts specifications,
automated validation evidence, independent agentic review dispositions and
disclosed residual risks, and does not manually inspect generated code.

**Specification:** §21, Agentic development, PR review and CI enforcement.
This is a documentation-only policy task, not a product backlog feature ID.

## Scope

- Replace owner manual code/diff inspection requirements with owner acceptance
  of the requirements/acceptance basis, measured validation evidence, complete
  review dispositions and disclosed residual risks.
- Require independent agentic review against specifications and meaningful
  implementation, test and documentation risks for every PR.
- Preserve every finding across rounds and require explicit disposition of all
  findings, including summary-only “Previously missed” findings.
- Clarify that automated evidence and agentic review have limits, and require
  disclosure of unexercised platforms/runtime behavior.
- Preserve owner control of requirements, material scope/tradeoffs, residual
  risk acceptance and merge authority; agents never merge, enable auto-merge
  or enqueue merges on their own initiative.

## Exclusions

No runtime behavior or privileges, source code, CI workflow, validation
threshold, branch protection, required check, conversation-resolution rule or
security/privacy gate changes. No owner code certification or manual
generated-code inspection requirement.

## Owned files

- `AGENTS.md`
- `docs/Personal-Agent-Project-Spec.md`
- `docs/testing.md`
- This task brief

## Dependencies and assumptions

- Based on the current `main` containing owner-merged PR #26.
- The owner explicitly approved this policy; it does not authorize agents to
  merge, enable auto-merge, enqueue a merge, or change product runtime
  capabilities.
- Existing CI, coverage, architecture, security/privacy and conversation
  resolution requirements remain unchanged.

## Tests and documentation

- Run `tools/validate.sh docs`.
- Search the repository instructions/specification/documentation for
  conflicting owner manual code/diff inspection mandates.
- Perform an independent, read-only agentic review of the final policy diff;
  disposition all findings and retain review evidence with the PR.

## Completion criteria

- Instructions and specification consistently define spec/evidence/review
  acceptance without owner manual code inspection or code certification.
- Review is independent and cumulative; every finding has an explicit
  disposition, including summary-only and previously missed findings.
- Owner requirement/scope/risk/merge authority and all existing technical
  gates are explicit and unchanged.
- Documentation validation passes, independent review is dispositioned, and
  a new PR is opened without modifying or reusing PR #26.
