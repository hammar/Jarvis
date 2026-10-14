# PersonalAgent.Application

Owns the frozen turn, route, context, approval, tool, conversation, memory,
job, secret-reference, clock, and Home Assistant contracts. Depends only on
Domain and contains no provider, ASP.NET Core, Aspire, or persistence types.

The M1 `Routing/LocalOnlyModelRouter` applies deterministic local-only
decisions, `Context/ConversationContextBuilder` builds bounded,
provenance-labelled context from `IConversationStore`, and
`TurnCoordination/LocalTurnCoordinator` owns accepted local-turn lifecycle,
queue limits, cancellation, shutdown, and interrupted-turn recovery. The
coordinator relies on Application-owned persistence/engine contracts and never
depends on SQLite, Copilot SDK or ASP.NET Core. It uses SQLite as the durable
authority through those contracts; queued model work is never replayed after a
restart. Context estimates count
instructions, task text, the serialized tool catalog, selected history and
model-facing prompt framing, then reserve 20% margin using a labelled estimate of four
UTF-16 characters per token. These estimates are not provider tokenizer
counts. Fixed M1 limits are 8,000 UTF-16 characters each for task text and
host instructions; at most 8 tools; and 12 recent history messages of at most
8,000 characters each. Tool names are capped at 128 characters and schemas at
4,096. The complete estimated prompt, including its 20% reserve, must fit
within 8,192 estimated tokens. One additional history row indicates whether
older messages were omitted. `ContextEstimate.MinimumOmittedHistoryMessages`
is a known lower bound, not a total count: the builder reads only the latest
13 rows to cap work. `HasMoreHistory` indicates whether the extra sentinel
row proves that additional older history exists.

`Tools/ToolRegistry` exposes only typed, host-authored registrations.
`ToolAllowlistPolicy` requires an exact tool/action/operation/entity rule and
derives a policy identity from the full rule set. `ToolDispatcher` stores a
canonical action envelope before execution and uses the original current-turn
request only as in-memory evidence for a handler's strict explicit-intent
check; the journal stores a request hash, not the prompt text. Low-risk writes
without that handler evidence require approval. `Approvals/ApprovalService`
binds a five-minute, single-use approval to one owner and one journaled action.
Execution rechecks policy and the canonical request. Only verified results
are `Succeeded`; sent-but-unconfirmed actions remain `Unknown` and are never
replayed. If restart finds an action still `Executing`, the dispatcher only
performs bounded verification and records the observed result or uncertainty;
it does not call the execution handler again. The current Web composition
registers no tools or allowlist entries until their feature implementation
arrives.

The dispatcher rechecks policy/approval expiry after claiming execution and
reports the recorded action state when a concurrent execution defeats a stale
expiry transition. Equivalent reads and known failed writes have a two-failure
limit per turn. An uncertain write blocks any further equivalent send in that
turn, including fresh call IDs, queued attempts, and exceptions after dispatch.
Equivalent attempts serialize to enforce these limits under concurrency.
Concurrent approval settlement accepts an already-matching terminal action
status, while other conflicts/errors propagate. A stale expired Pending read
uses the winning persisted decision and current action, not a second resolution.
Pending snapshots are also refreshed before expiry so they cannot mask a winning
rejection or execution or ask for another decision on an already-approved action.
Creation also returns an existing exact approval binding after execution starts
or completes, without renewing the decision or relaxing its owner binding.
Observation-only recovery shares the proposal gate and updates distinct-action
failure accounting only after journaling. A known recovered outcome clears
only its own write uncertainty; other actions' uncertainty and unsuccessful
observation/journaling remain blocked.
`IToolDispatcher.ReleaseTurn` releases only ephemeral failure accounting once
all turn callbacks have stopped; durable action identity remains authoritative.
T03 supports status-only replay, not replay of the original result JSON/reason.

`Context/LocalChatPrompt` supplies shared host chat instructions and formats
the current task plainly after quoted, role-labelled history. Packet provenance
and privacy metadata stay application-owned. The estimate counts the same
formatted text that the Copilot adapter sends.

Entry points: `PersonalAgent.Application.Contracts`,
`PersonalAgent.Application.TurnOutcomeContracts`,
`PersonalAgent.Application.Routing`, `PersonalAgent.Application.Context`,
`PersonalAgent.Application.TurnCoordination`,
`PersonalAgent.Application.Tools`, and `PersonalAgent.Application.Approvals`.
Tests: `dotnet test tests/PersonalAgent.UnitTests` and
`dotnet test tests/PersonalAgent.IntegrationTests --filter FullyQualifiedName~ToolDispatcherTests`.
