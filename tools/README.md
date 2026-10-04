# Validation tools

`validate.sh` is the canonical macOS/Linux entry point used by developers and
CI. It needs the pinned .NET SDK and a POSIX-compatible shell, not PowerShell.

`PersonalAgent.Validation` validates test discovery and coverage output and
provides a PowerShell-free launcher for the Playwright browser installer.
`coverage.runsettings` selects Cobertura output from the VSTest Coverlet
collector. `PersonalAgent.SimulatorEndpoints` is a credential-free managed
Aspire fixture process, not a production service or data store.
