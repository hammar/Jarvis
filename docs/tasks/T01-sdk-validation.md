# T01: GitHub Copilot SDK feasibility validation

## Objective and acceptance

Validate the pinned GitHub Copilot SDK/runtime before T02 or dependent feature work. This task covers the nine M0 demonstrations in [Personal-Agent-Project-Spec.md, section 4](../Personal-Agent-Project-Spec.md#4-sdk-validation-milestone-m0), the T01 backlog entry in section 15, and the SDK-related acceptance expectations in section 14.

## Scope

- Pin the .NET SDK, Copilot SDK package, and package dependency graph.
- Build an isolated executable harness using the real spawned Copilot runtime and deterministic loopback model fixtures.
- Verify explicit Ollama BYOK inference through an instrumented loopback proxy.
- Record evidence, limitations, and a go/no-go recommendation in `docs/sdk-validation.md`.
- Run actual-runtime contracts on Linux and macOS in the `sdk-contracts` CI check without cloud or household credentials.

## Exclusions

- No production application projects, application contracts, Home Assistant access, or real household writes.
- No required cloud credentials or live cloud inference in CI.
- No claim of air-gapped execution, full process-egress capture, or safe unattended tool execution.
- This spike does not replace T02's project structure, architecture checks, test runner, or coverage gates.

## Owned files

- `global.json`
- `.dockerignore` (exclude credentials from the validation image context)
- `.gitignore` (exclude the local evaluation credential file)
- `tools/sdk-validation/`
- `.github/workflows/ci.yml` (`sdk-contracts` and .NET SDK version alignment only)
- `docs/sdk-validation.md`
- `docs/tasks/T01-sdk-validation.md`
- `README.md`

## Dependencies and assumptions

- The T01 implementation was initially validated with .NET SDK `10.0.100`,
  with roll-forward disabled; the current repository pin is maintained by
  [the .NET GA refresh follow-up](T01-dotnet-ga-refresh.md).
- `GitHub.Copilot.SDK` exactly `1.0.16`; its bundled Copilot CLI runtime is `1.0.90`.
- Tagged SDK documentation and release metadata reviewed on 2026-10-04.
- The standard contract check can download the pinned runtime/package but does not require GitHub login, cloud keys, Ollama, or household credentials.
- The live Ollama smoke is opt-in and requires an already-running native Ollama endpoint and an installed model.
- The live cloud smoke is opt-in and requires an owner-supplied HTTPS endpoint, model, and API key.
- Cloud evaluation settings are read from the Git-ignored `.env.cloud-evaluation` file without shell execution; process environment values take precedence.

## Tests and documentation

- `dotnet restore tools/sdk-validation/SdkValidation.csproj --locked-mode`
- `dotnet build tools/sdk-validation/SdkValidation.csproj -c Release --no-restore -warnaserror -p:TreatWarningsAsErrors=true`
- `dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release --no-build --no-restore -- --contracts`
- `JARVIS_OLLAMA_MODEL=<installed-model> dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release --no-build --no-restore -- --live-ollama`
- `dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release --no-build --no-restore -- --live-cloud` (opt-in only)

## Completion criteria

- Pinned, locked restore and Release build succeed.
- Real-runtime contracts cover allowlisted host tools, hostile ambient configuration, explicit provider selection and transcript isolation, streaming, timeout/cancellation, and forced runtime stop/restart.
- The local smoke captures configured inference destinations through a loopback-only proxy and proves that each observed destination is loopback.
- The report maps all nine M0 demonstrations to evidence and explicitly records the native-runtime isolation blocker, the failed zero-external-attempt diagnostic, and the separately passing Docker containment test.
- CI runs the credential-free real-runtime contract on Linux and macOS.

## Current status

Evidence collected: the saved-key Foundry smoke, adversarial tool denial, permission
denial, provider refusal, host budgets, and active-turn crash/restart tests pass.
Follow-up baseline tracing attributed the DNS attempts to fixture hostname
resolution independent of Copilot. The strict full-suite diagnostic passes
with a hostname-to-loopback mapping; no assertion was relaxed. Docker
containment passes, including an explicit external TCP rejection probe.
Native macOS capture is permission-blocked, and native containment or an
isolated inference gateway to real native Ollama remains unproven.
The owner accepted native trusted-dependency operation in ADR 0001 on
2026-10-04, resolving that assurance caveat. Qualified go does not waive
application-owned policy or imply an air-gap. Published PR/CI delivery remains
to be verified before closing the issue.
