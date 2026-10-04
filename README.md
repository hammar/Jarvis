# Jarvis

Install the .NET 10 SDK version pinned in [`global.json`](global.json)
(currently `10.0.401`). Development, CI, and container validation use that exact
pin; older SDK versions in historical evidence are not prerequisites.

The T01 GitHub Copilot SDK feasibility spike and its evidence are documented in
[the SDK validation report](docs/sdk-validation.md). Run its credential-free
runtime contracts with `dotnet run --project tools/sdk-validation -c Release -- --contracts`.
