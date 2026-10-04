# ADR 0002: Architecture dependency enforcement

## Status

Accepted for the T02 scaffold.

## Context

The project boundary policy must survive namespace renames and must permit
only the narrow Web-to-Infrastructure composition-root exception. An
architecture library would add a new versioned dependency before the product
has application code.

## Decision

Use a small xUnit architecture suite with two complementary checks:

- Parse project files and compare direct `ProjectReference` edges against a
  fixed allowlist.
- Inspect compiled assembly references and reflected declared-type
  dependencies. Reject ASP.NET Core, Aspire, SQLite/EF, and Copilot framework
  references from Domain/Application, and confine the Copilot package and
  compiled SDK references to Infrastructure.

Web may reference Infrastructure at the project level for composition, but
source scanning rejects Infrastructure namespace/type use outside `Program.cs`.
AppHost references are checked as Aspire orchestration resources, not business
layer edges. Test projects have explicit per-project allowlists; production
projects do not reference tests.

Negative tests include a forbidden project edge fixture, a compiled fixture
whose public type exposes an ASP.NET Core type, and a Web source fixture using
Infrastructure outside composition. These fixtures remain under tests and are
not production references.

## Consequences and limitations

The checker itself is first-party code and runs on every PR. Its exclusions
are explicit and narrow. Compiled assembly metadata and public/member
signatures complement project-file and source checks; a namespace rename
cannot remove an assembly edge. Changes to the checker require review of the
fixture tests and allowed-edge table. The project does not add ArchUnitNET or
another architecture dependency.

The isolated `tools/sdk-validation/` executable is the pre-T02 actual-runtime
feasibility harness and intentionally references the SDK directly. It is not a
production assembly, is not project-referenced by application code, and keeps
its exact SDK version range and lock file independent of solution-level central
package management. The production confinement rule applies to `src/` and all
other test/tool projects.
