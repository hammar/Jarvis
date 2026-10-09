# PersonalAgent.ServiceDefaults

Provides shared health checks, service discovery, and local OpenTelemetry
instrumentation with formatted log messages/scopes disabled. It does not
enable automatic retries or hedging and has no application-layer dependencies.
Development `/health` and `/alive` endpoints are liveness-only; hosts map any
readiness endpoint separately with their intended access controls.

Entry point: `Extensions.AddServiceDefaults`. Tests run through
`dotnet test tests/PersonalAgent.IntegrationTests` and
`dotnet test tests/PersonalAgent.E2ETests`.
