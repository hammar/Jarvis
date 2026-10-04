# PersonalAgent.AppHost

Owns Aspire development resource composition and the trusted Simulator,
Local, Hybrid, and E2E profiles. It references Web and the controlled simulator
endpoint executable as resources only. It does not own SQLite data, external
Ollama/Home Assistant/cloud lifecycles, or business rules.

Entry point: `AppHost.cs`. Launch Simulator with
`dotnet run --project src/PersonalAgent.AppHost`; validate using
`tools/validate.sh aspire-e2e`.
