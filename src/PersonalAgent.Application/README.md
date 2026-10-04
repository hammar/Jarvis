# PersonalAgent.Application

Owns the frozen turn, route, context, approval, tool, conversation, memory,
job, secret-reference, clock, and Home Assistant contracts. Depends only on
Domain and contains no provider, ASP.NET Core, Aspire, or persistence types.

Entry point: `PersonalAgent.Application.Contracts`. Tests:
`dotnet test tests/PersonalAgent.UnitTests`.
