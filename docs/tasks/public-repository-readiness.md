# Public repository readiness

## Objective and acceptance

Prepare owner-approved publication notices and review disclosures before
changing repository visibility. The owner explicitly chose not to grant a
general reuse license and approved an experimental agentic-development
notice and a closed external-contribution policy.

**Specification:** sections 20-21 (privacy, evidence, independent review and
owner-controlled integration). This is a documentation/publication task,
not a product scenario or new runtime capability.

Publication remains an owner decision. No visibility, access, workflow,
protection or security setting is changed by this task.

## Scope, exclusions and owned files

- Add project-status and source-visible notices to `README.md`.
- Add `RIGHTS.md`, `CONTRIBUTING.md` and `SECURITY.md`.
- Maintain this task record with redacted publication evidence and findings.
- Read Git history, GitHub issue/PR discussions, Actions archives and
  repository settings; never publish raw audit data or secret values.
- Exclude history rewriting, deletion of GitHub content, accepting external
  contributions, changing license permissions and runtime changes.

## Dependencies and assumptions

- Baseline: `origin/main` at `aa67e3b`; branch begins at that revision.
- Existing accepted ADRs and product scope remain unchanged.
- Rights notices are not legal advice or a guarantee of enforceability or
  ownership of every generated artifact. Third-party rights remain intact.
- Private-to-public conversion can expose historical content, not only HEAD.
- Public copies and forks cannot reliably be recalled.

## Automated disclosure review

Audit date: 2026-10-07 UTC. Raw API responses, review scripts and redacted
location-only output remain in the private session artifact directory,
outside this repository.

Commands/methods: `git fetch origin`; fetch GitHub PR head refs into local
`refs/audit/pr-heads/`; `git rev-list --objects` and `git cat-file --batch`
over fetched remote branches, tags, PR heads and HEAD; paginated `gh api`
retrieval of issues, comments, PRs, reviews, Actions runs and artifacts.
ZIP archives are inspected in memory, without executing or extracting their
contents. Pattern scanning covers recognizable provider credentials, private
keys, JWTs, credential-bearing URLs, quoted secret assignments, personal paths,
email addresses, private-network candidates and selected service endpoints.

Initial snapshot examined 80 commits, 516 unique historical blobs and 135
tracked working files; 35 issue/PR entries, 9 issue comments, 167 inline review
comments, 14 PR descriptions and 119 submitted reviews; 99 Actions log archives
and 88 unexpired artifact archives (about 116 MB of scanned content overall).
PR descriptions also occur in the issues API and are not independent evidence.
Artifact contents are coverage XML/JSON and XML test-result (`.trx`) files.

No credential-pattern matches or committed sensitive-file-name candidates
were found. Network matches in documentation include a Docker diagnostic
destination, not a demonstrated household endpoint. Runner paths, SDK
version numbers matching an IP pattern and GitHub Copilot service endpoints
are not evidence of private household state.
Archive metadata detections were subsequently classified across every
affected archive: paths belong to hosted runners, Dependabot or redaction
markers; service-host detections belong to GitHub Copilot; private-address
log detections belong to Copilot's hosted Docker bridge/firewall setup.
No owner-local path or household endpoint was identified by those patterns.

Ten completed runs returned HTTP 404 for logs; subsequent jobs API queries
returned zero jobs for every one of those runs. The two initially active
runs subsequently completed and their log archives were scanned, together
with one additional artifact archive, without credential-pattern matches.
The refreshed inventory contained 111 runs (101 reviewed log archives and
10 jobless runs), 89 reviewed artifact archives and no active runs.
No assertion is made about inaccessible logs or later output.
Pattern scanning cannot establish that all secrets or
private prose are absent; it is not a provenance/license audit of every
dependency or generated line. Unreachable Git objects, external attachments,
private integrations not exposed by the APIs and future changes are outside
the verified scope. No issue/PR attachment links were detected by the scan.

## Repository settings snapshot

- Repository private; Issues enabled; Discussions and wiki disabled.
- Main protection requires all nine named CI checks, an up-to-date branch
  and resolved conversations, including for administrators. Force pushes
  and deletion are disabled. No approving-review count is required.
- Auto-merge disabled; merge, squash and rebase methods enabled.
- Default Actions token permissions are read-only; workflow PR approvals
  disabled. Checked-in workflows use pinned actions and read-only tokens,
  with no `pull_request_target` trigger.
- All Actions are allowed; repository-wide SHA pinning is not enforced.
  Existing workflow pins are therefore a convention, not an account gate.
- No self-hosted runners, repository Actions secrets/variables, webhooks or
  release assets were returned. The `copilot` environment has no secrets or
  variables and no protection rules; these are not evidence of credentials.
- No rulesets returned. Pages and private vulnerability reporting queries
  returned HTTP 404; availability/enabled state is not established.
- Fork-contributor approval query returned HTTP 422 because the repository
  is private; its public-repository configuration cannot yet be verified.

## Cumulative findings and dispositions

| ID | Finding | Disposition and evidence |
|---|---|---|
| PUB-01 | Commit author/committer metadata contains the owner's name and two personal email addresses, including metadata in squash-message trailers. | Accepted as a residual privacy risk: the owner explicitly selected "Accept publication of the existing identity metadata" on 2026-10-07 UTC. History has not been rewritten. |
| PUB-02 | Fork-contributor approval cannot be checked while private. A contribution policy does not prevent PR submission or workflow execution. | Pending post-publication configuration: require approval for all outside collaborators, then verify the API/UI result. Do not weaken required CI. |
| PUB-03 | Private vulnerability reporting is not verified active. | `SECURITY.md` provides a detail-free fallback. Enable and verify private reporting when available after publication. |
| PUB-04 | Ten unavailable log archives and two initially active runs limited the first snapshot. | Two completed runs and the new artifact were subsequently scanned, with no credential-pattern matches. All ten HTTP 404 runs have zero jobs according to the jobs API; not applicable as evidence of an unreviewed executed job. Unavailable output is not claimed to have passed. Refresh again immediately before publication. |
| PUB-05 | Repository-wide action allowlisting/SHA enforcement is permissive. | Preventive observation, not an observed exploit. Current workflow pins and read-only permissions retained; no account gate changed without owner approval. |
| PUB-R1 | The validation status described the initial no-push/no-PR handoff as current after the owner requested a PR. | Fixed by explicitly separating the initial audit handoff from the subsequent branch push and draft PR creation below; documentation links and whitespace checks pass. |

## Tests, documentation and completion criteria

- Run `sh tools/validate.sh docs` and `git diff --check`.
- Independently review the exact documentation revision, including all
  findings above; record concrete findings and dispositions.
- No executable, contract, schema, migration or CI changes; runtime suites
  are not applicable to this documentation-only diff.
- Documentation readiness requires passing documentation checks and
  dispositioned independent review.
- Publication readiness additionally requires the owner's privacy decision,
  refreshed disclosure evidence and verified public-only security settings.
- Do not describe the repository as open source or promise reuse rights,
  support, security guarantees or already implemented planned capabilities.

## Validation and review status

`sh tools/validate.sh docs` passed (all internal Markdown file links resolve);
`git diff --check` passed. No runtime tests were run for this documentation-only
change. Repository remains private.

Independent documentation review was requested against commit
`3cb4863bc440c25568edfc2cf9df855f26e0ae9f`. The review agent returned only
"No significant issues found in the reviewed changes," without scope,
checks or supporting reasoning. The initial security-review agent likewise
returned only a no-vulnerabilities verdict. Neither unsupported verdict is
treated as substantive review evidence; disclosure evidence above comes
from the recorded direct audit. Detailed independent review remains pending
before a PR can be presented as ready. At the initial audit handoff, no PR had
been opened and no commits had been pushed.

The owner subsequently requested PR creation. The branch was pushed and
[draft PR #36](https://github.com/hammar/Jarvis/pull/36) was opened on
2026-10-07 UTC. GitHub CI run `37580647407` passed all nine required checks
against `dea3d206c26b56fe8da35976bdf03c81704da394`, including runtime suites
not run locally for this documentation-only change. Independent Copilot
review against that revision identified PUB-R1 (the stale handoff wording);
the finding and its disposition are retained above. The documentation
correction requires fresh CI/review evidence before readiness is claimed.
Repository visibility and security settings remain unchanged by this task.

## Owner-controlled publication steps

Merge the documentation only after independent review and required CI gates.
Immediately before changing visibility, refresh remote refs, discussions and
Actions output so later changes are not implicitly covered by this audit.
Confirm that the disclosure and rights decisions still hold.

When the owner changes visibility to public:

1. In Settings, Actions, General, configure approval for **all outside
   collaborators** before allowing external workflows to execute. Re-query
   the fork-contributor approval API; do not assume the private setting
   carries over.
2. Enable private vulnerability reporting under the repository security
   settings and verify the Security tab's reporting control. Do not promise
   a reporting route based only on a successful settings request.
3. Re-query main protection and confirm the same nine checks, strict
   up-to-date requirement, admin enforcement, conversation resolution and
   disabled force pushes/deletion. Keep auto-merge disabled.
4. Confirm read-only workflow tokens and disabled workflow PR approvals,
   no household-connected self-hosted runners, and no newly added secrets.
   Review action allowlisting/SHA enforcement without silently changing gates.

These public-only checks remain pending; this task does not authorize an
agent to publish the repository, merge a PR or change account settings.
