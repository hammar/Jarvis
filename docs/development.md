# Development

## Toolchain and validation commands

Install the exact .NET SDK in `global.json` (`10.0.401`) and use the solution
lock files. On macOS and Linux, run `tools/validate.sh`. PowerShell is not
required or used.

```sh
dotnet restore Jarvis.sln --locked-mode
tools/validate.sh build
tools/validate.sh unit
tools/validate.sh integration
tools/validate.sh architecture
tools/validate.sh sdk-contracts
tools/validate.sh coverage
tools/validate.sh aspire-e2e
tools/validate.sh browser-e2e
tools/validate.sh docs
```

CI and local development invoke the same validation operations. The selected
test stack is xUnit 2.9.3 + `Microsoft.NET.Test.Sdk` 18.10.1 +
`xunit.runner.visualstudio` 4.0.0 + Coverlet collector 10.1.0 (VSTest).
Aspire packages and AppHost SDK are pinned to 13.6.0. Do not pass
Microsoft.Testing.Platform-only switches to VSTest.

## Aspire profiles

The normal development entry point defaults to Simulator:

```sh
dotnet run --project src/PersonalAgent.AppHost
```

Choose the profile through trusted process configuration (`JARVIS_PROFILE`),
never through a model or request payload.

| Profile | Configuration and effect |
| --- | --- |
| Simulator | No credentials or real endpoints. External model, cloud, and Home Assistant settings are cleared for Web. Runs deterministic fake model/Home Assistant processes. Simulator data is persistent and separate from Local data. |
| Local | Requires `JARVIS_OLLAMA_BASE_URL`; cloud is disabled. Optional HA uses `JARVIS_HOME_ASSISTANT_BASE_URL` and a secret reference. |
| Hybrid | Requires explicit Ollama and cloud HTTP(S) endpoints plus `JARVIS_CLOUD_SECRET_REFERENCE`. Configuring Hybrid does not bypass packet review or consent. |
| E2E | Test-only; requires unique `JARVIS_E2E_DATA_DIR`, launches controlled fixture endpoints and clears external model, cloud, and Home Assistant settings for Web. |

Endpoint settings must be absolute HTTP(S) URIs without embedded user info or
query data. Supply tokens through a named protected secret reference, not an
endpoint URL. The AppHost does not resolve or print secret values. macOS uses
native Ollama as an external endpoint; no Apple GPU/container assumption is
made.

## Direct Web host and resources

The Aspire AppHost is the canonical development orchestrator, not a production
supervisor. For a direct Web process, set `JARVIS_PROFILE`, `JARVIS_DATA_DIR`,
and any required external endpoint/secret-reference settings. Direct Simulator
and E2E startup requires an explicitly isolated `JARVIS_DATA_DIR`; those
profiles never fall back to the Local per-user path. Then run:

```sh
dotnet run --project src/PersonalAgent.Web
```

AppHost does not own external Ollama/Home Assistant lifecycles or personal
data. Keep the dashboard local and authenticated; local telemetry exports
only when an OTLP endpoint is explicitly configured. No automatic HTTP
retry/hedging policy is enabled by ServiceDefaults; writes and provider turns
need explicit future semantics.

## Development environment requirements

- .NET SDK `10.0.401`; locked restore.
- Chromium for Playwright browser E2E. Install it without PowerShell using
  `tools/validate.sh playwright-install` (Linux installs system dependencies).
- A Docker CLI and reachable Docker Engine are required by the Aspire runtime
  for local AppHost and Aspire E2E startup. The Simulator/E2E profiles do not
  launch Docker-managed application resources; on macOS, keep Docker Desktop
  running while using Aspire.
- No cloud API keys or household credentials for required checks.

## Copilot cloud agent environment

`.github/workflows/copilot-setup-steps.yml` prepares GitHub-hosted Copilot
cloud agent sessions: it installs the SDK from `global.json`, performs locked
restores and installs Playwright Chromium through `tools/validate.sh`. It
receives no secrets. Local agent sessions use the developer's own toolchain and
do not run it. The workflow also runs when the file itself changes, so edits are
validated before cloud agents depend on them.
