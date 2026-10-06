# PersonalAgent.Application

Owns the frozen turn, route, context, approval, tool, conversation, memory,
job, secret-reference, clock, and Home Assistant contracts. Depends only on
Domain and contains no provider, ASP.NET Core, Aspire, or persistence types.

The M1 `Routing/LocalOnlyModelRouter` applies deterministic local-only
decisions, and `Context/ConversationContextBuilder` builds bounded,
provenance-labelled context from `IConversationStore`. Context estimates count
instructions, task text, the serialized tool catalog, selected history and
packet framing, then reserve 20% margin using a labelled estimate of four
UTF-16 characters per token. These estimates are not provider tokenizer
counts. Fixed M1 limits are 8,000 UTF-16 characters each for task text and
host instructions; at most 8 tools; and 12 recent history messages of at most
8,000 characters each. Tool names are capped at 128 characters and schemas at
4,096. The complete estimated prompt, including its 20% reserve, must fit
within 8,192 estimated tokens. One additional history row indicates whether
older messages were omitted.

Entry points: `PersonalAgent.Application.Contracts`,
`PersonalAgent.Application.Routing`, and `PersonalAgent.Application.Context`.
Tests:
`dotnet test tests/PersonalAgent.UnitTests`.
