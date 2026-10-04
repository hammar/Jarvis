# T01 follow-up: refresh the .NET GA toolchain

## Objective

Check the repository's .NET target and SDK against the latest GA .NET release
and align development, CI, container validation, and documentation pins.

## Specification/scenario IDs

- Personal-Agent-Project-Spec.md: pinned .NET SDK, .NET 10 LTS target, M0 SDK
  compatibility, and pinned runtime/tooling requirements.
- T01: preserve the actual-runtime contract and its reproducible toolchain.

## Scope and exclusions

- Keep the product on `net10.0`; update the exact SDK pin to the latest GA SDK
  for .NET 10.
- Align `global.json`, CI, and the SDK validation container image.
- Record the official release metadata and update the SDK validation docs.
- Audit project specs, agent instructions, task briefs, and GitHub issue/PR
  bodies and comments for stale SDK requirements; clarify the authoritative pin.
- Do not change Copilot SDK/package versions, application APIs, or NuGet
  dependency versions.

## Owned files

- `global.json`
- `.github/workflows/ci.yml`
- `tools/sdk-validation/Dockerfile`
- `docs/sdk-validation.md`
- `AGENTS.md`
- `README.md`
- `docs/Personal-Agent-Project-Spec.md`
- `docs/tasks/T01-sdk-validation.md`
- `docs/tasks/T01-dotnet-ga-refresh.md`
- GitHub issues #2 and #3 (SDK pin/target assumptions only).

## Dependencies and assumptions

- .NET 10 remains the latest GA major release; .NET 11 release candidates are
  not GA.
- Microsoft's .NET 10 release metadata dated 2026-09-08 reports runtime
  `10.0.12` and SDK `10.0.401`.
- The SDK image is pinned by the multi-platform manifest digest for tag
  `10.0.401`.
- T01's prior evidence remains historically identified as run on SDK
  `10.0.100`; the runtime contract suite is rerun on the refreshed SDK.

## Tests and documentation

- Restore in locked mode, build Release with warnings as errors, and run the
  real Copilot runtime contracts using the pinned SDK.
- Check the Docker image manifest digest and build the SDK validation image
  when Docker is available.
- Update `docs/sdk-validation.md` with the current SDK/runtime pins and
  preserve the original T01 evidence context.

## Completion criteria

- All exact SDK pins agree on `10.0.401`.
- `net10.0` remains the project target.
- The credential-free contracts pass with the refreshed SDK.
- The Docker base image resolves to the pinned multi-platform digest.

## Status

Complete on 2026-10-04. Microsoft metadata confirms the current .NET 10 GA
release is runtime `10.0.12` with SDK `10.0.401`. Locked restore, Release
build, and the actual Copilot runtime contract suite passed on SDK `10.0.401`.
The refreshed Docker image built successfully and passed the loopback-only
containment and runtime contract checks. The repository intentionally remains
on `net10.0`; there is no GA major target change to make.

Follow-up collateral audit on 2026-10-04 checked repository instructions,
specifications, documentation, configuration, and all 17 issue bodies plus
issue comments and the three pull request bodies plus review comments.
No current requirement mandates `10.0.100`. Remaining old-version references
identify historical evidence. Updated the T01/T02 GitHub issue assumptions
and repository guidance to make current `global.json` authoritative rather
than inheriting the original T01 pin. No application contract/schema changes.
