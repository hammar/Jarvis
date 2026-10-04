# PersonalAgent.Web

Owns the ASP.NET Core/Razor host, health endpoints, and composition root.
Handlers call Application use cases. Infrastructure may be referenced only
from `Program.cs` for future composition; ServiceDefaults supplies health,
discovery, and privacy-filtered telemetry.

Entry point: `Program.cs`; the current Razor page is a profile smoke surface.
Tests: `dotnet test tests/PersonalAgent.IntegrationTests` and
`dotnet test tests/PersonalAgent.E2ETests`.
