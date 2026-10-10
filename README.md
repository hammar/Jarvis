# Jarvis

Jarvis is a local-first personal assistant built on ASP.NET Core, SQLite,
Ollama, approved Home Assistant tools, and the GitHub Copilot SDK behind an
application-owned adapter. The host owns privacy policy, approvals, durable
state and execution; model output is untrusted.

## Experimental personal project

Jarvis is a personal experiment in specification-driven agentic software
development. Implementation and independent review are performed by AI agents,
with owner decisions based on specifications, automated evidence and disclosed
limitations. This process does not guarantee correctness or security.

The project is incomplete and intended for controlled experimentation, not
safety-critical use. Planned capabilities are not necessarily implemented.
No support, release schedule or compatibility commitment is provided. Do not
expose the application or development dashboard to the public internet.

Unsolicited external pull requests are not accepted; see the
[contribution policy](CONTRIBUTING.md). This repository is source-visible,
not open source: no general license to use, modify or redistribute the
first-party code or documentation is granted. GitHub's terms still permit
viewing and forking public repositories. See the [rights notice](RIGHTS.md).
The software is provided "as is," without warranty to the extent permitted
by applicable law.

Report security concerns using the [security reporting policy](SECURITY.md),
not by publishing sensitive details in an issue.

## Delivery milestones

Complete milestones in order: M0 SDK Validation, M1 Local Chat, M2 Home
Assistant & Memory, M3 Cloud Consent & Reminders, M4 Release Readiness.
Every task belongs to one milestone and closes within it; every milestone
ends with a cumulative runnable demo and its required checks. See the
[ordered task backlog](docs/Personal-Agent-Project-Spec.md#15-agent-ready-implementation-backlog)
and [demo/exit criteria](docs/Personal-Agent-Project-Spec.md#16-milestones-and-release-boundaries).
T01-T04 retain their completed historical IDs; remaining work uses
milestone-local IDs such as M1-01. This plan is not a claim that later demos
are implemented today.

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

On first startup, copy the one-time bootstrap token from the Web host's local
console into the setup page and choose an owner passphrase. The passphrase
verifier, session-protection keys, conversations, and owner retention settings
remain in the private data directory across restarts and application updates.
See the [Web guide](src/PersonalAgent.Web/README.md) for session, API, and
retention details.

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
