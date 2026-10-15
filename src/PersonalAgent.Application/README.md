# PersonalAgent.Application

Owns the frozen turn, route, context, approval, tool, conversation, memory,
job, secret-reference, clock, and Home Assistant contracts. Depends only on
Domain and contains no provider, ASP.NET Core, Aspire, or persistence types.

`Memory/` owns explicit local facts, deterministic normalized subject/key
conflicts and host-side proposal review/completion. `Tools/Memory/` registers
search/get and always-approved-by-owner mutations. New facts default to
LocalOnly; model arguments cannot reclassify or approve. Tool evidence carries
IDs/versions/update times, never authority. The production host uses
`IConfirmedMemoryStore`; the older unscoped `IMemoryStore` is not registered.
See [ADR 0006](../../docs/adr/0006-confirmed-memory.md).

`Tools/Home/` owns the exact immutable Home Assistant catalog, bounds, feature
gates, whole-request light intent and four typed registrations. Permissions and
approval configuration identity derive from the same catalog, including aliases
and bounds. Duplicate aliases clarify; no model-proposed service, URL or approval
is accepted. Thermostat is disabled by default and always approval-required.
Writes verify by bounded observation without replay. See
[configuration and command syntax](../../docs/development.md#home-assistant-configuration).
Run `tools/validate.sh unit` for catalog, intent and registration tests.

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
registers configured home tools and local memory tools; the chat coordinator
catalog and approval UI remain M2-04 scope.

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
Fresh-dispatcher recovery seeds uncertainty from the persisted original risk
before observation and tracks every unresolved write by action ID. Reconciling
one orphan cannot discard another, current read classification cannot erase
original write risk, and unreadable risk metadata fails closed. Original reads
remain subject to their failure budget without inventing write uncertainty.
Normal post-execution verification rechecks permission and records Unknown
without observation when revoked/Unsupported. Recovery refreshes the action
under its proposal gate and returns any winning non-Executing state without
observing again; known pre-send rejection/expiry does not invent uncertainty.
Original write risk is registered thread-safely before cancellable gate waits,
so cancelling queued recovery cannot discard an orphan's marker when another
action completes. Valid reads do not add markers; malformed metadata does.
Final send admission after the durable claim shares a short synchronous lock
with orphan registration. A winning registration rejects the unsent proposal
without clearing the orphan; already-admitted operations may finish. The lock
never encloses persistence, an external handler or an await.
Terminal replay refreshes the journal under the proposal gate and reconciles
only its own accounting, including stale uncertainty left by cancelled recovery
after completion. Failed refreshes and cancellation preserve markers; another
orphan's uncertainty cannot be cleared. Replay never executes or observes.
Persisted Unknown replay shares Executing recovery's strict original-risk
classification and registers unresolved write risk before cancellable waits.
A fresh dispatcher, lowered current risk, cancellation or refresh error cannot
permit an equivalent resend. Original reads do not add write markers; malformed
risk metadata marks conservatively then surfaces the error.
Recovery rechecks authorization after any proposal-gate wait, before observing.
Definitive `NotApplied` means known failure even after uncertain delivery and
retains only the remaining bounded attempt; an unchanged snapshot with possible
late application must remain `Unknown`, not authorize a blind retry.
`IToolDispatcher.ReleaseTurn` releases only ephemeral failure accounting once
all turn callbacks have stopped; durable action identity remains authoritative.
The owner-authorized R6 extension persists complete terminal responses atomically
with action status/version for exact duplicate/restart replay. Completion events
reference the action without copying the result; conversation messages/context
remain unchanged. Legacy rows without a response explicitly retain status-only
replay. Result JSON is bounded to 1,000,000 characters and reasons to 256.

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
