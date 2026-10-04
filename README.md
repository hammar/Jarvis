# Jarvis

Jarvis is a local-first personal assistant built on ASP.NET Core, SQLite,
Ollama, approved Home Assistant tools, and the GitHub Copilot SDK behind an
application-owned adapter. The host owns privacy policy, approvals, durable
state and execution; model output is untrusted.

## Prerequisites and quick start

- .NET SDK `10.0.401`, pinned in [`global.json`](global.json). Historical T01
  evidence may identify the earlier `10.0.100` SDK; it is not a prerequisite.
- Docker is not required for the Simulator profile. Aspire launches the Web
  host and deterministic fake model/Home Assistant endpoints as local
  processes.

```sh
dotnet restore Jarvis.sln --locked-mode
dotnet run --project src/PersonalAgent.AppHost
```

The default Simulator profile uses no cloud or household credentials. The
Aspire dashboard is for local development; do not expose it publicly. The
direct Web host command and the Local/Hybrid profile configuration are in
[development guidance](docs/development.md).

## Validation

Use `tools/validate.sh` on macOS/Linux. It relies on the pinned .NET SDK and
ordinary shell tools; PowerShell is not required.

```sh
tools/validate.sh restore
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

The SDK validation harness runs real pinned Copilot runtime contracts, not a
fake adapter:

```sh
dotnet run --project tools/sdk-validation/SdkValidation.csproj -c Release -- --contracts
```

See [architecture](docs/architecture.md), [development](docs/development.md),
[testing](docs/testing.md), [operations](docs/operations.md), and
[SDK validation evidence](docs/sdk-validation.md).
