using PersonalAgent.Domain;

namespace PersonalAgent.Application;

/// <summary>Identifies the explicitly selected inference destination for a turn.</summary>
public enum ProviderKind
{
    /// <summary>Use the configured local inference provider only.</summary>
    Local,
    /// <summary>Use the configured cloud provider after host policy and consent checks.</summary>
    Cloud
}

/// <summary>Represents the owner's configured model-routing policy.</summary>
public enum RouteMode
{
    /// <summary>Keep all inference local and never escalate to a cloud provider.</summary>
    LocalOnly,
    /// <summary>Require review and exact consent before sending selected context to cloud.</summary>
    AskBeforeCloud,
    /// <summary>Permit configured cloud routing within host-enforced policy.</summary>
    CloudAllowed
}

/// <summary>Describes a host-classified task category used by deterministic routing.</summary>
public enum RoutingTaskKind
{
    /// <summary>A conversational request that can be handled as local text chat.</summary>
    TextConversation,
    /// <summary>A task already recognized by the host as a supported local workflow.</summary>
    RecognizedLocalWorkflow,
    /// <summary>A task that requires cloud inference to meet its stated requirements.</summary>
    RequiresCloud,
    /// <summary>A task whose supported workflow cannot be determined safely.</summary>
    Ambiguous,
    /// <summary>A task category for which the host has no supported capability.</summary>
    Unsupported
}

/// <summary>Identifies the typed result of a host routing decision.</summary>
public enum RouteDisposition
{
    /// <summary>Proceed using the local provider.</summary>
    Local,
    /// <summary>Ask the owner for information before starting a turn.</summary>
    Clarify,
    /// <summary>Do not start a turn because the requested capability is unavailable.</summary>
    Unsupported
}

/// <summary>Describes a host-registered tool made available to one agent turn.</summary>
/// <param name="Name">Stable tool name visible to the engine.</param>
/// <param name="InputSchema">JSON Schema for the validated tool input.</param>
/// <param name="IsReadOnly">Whether the host classifies the operation as read-only.</param>
public sealed record AgentToolDefinition(string Name, string InputSchema, bool IsReadOnly);

/// <summary>Contains explicit, bounded inputs for one engine turn.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="Provider">Provider selected by host routing; never inferred by the engine.</param>
/// <param name="Instructions">Host-supplied instructions for this turn.</param>
/// <param name="Context">The selected context packet; it excludes unapproved private content.</param>
/// <param name="Tools">The exact host-registered tool catalog for this turn.</param>
/// <param name="Deadline">Maximum elapsed turn time enforced by the application host.</param>
/// <param name="MaximumToolCalls">Maximum number of tool calls allowed during this turn.</param>
public sealed record AgentTurnRequest(
    TurnId TurnId,
    ProviderKind Provider,
    string Instructions,
    ContextPacket Context,
    IReadOnlyList<AgentToolDefinition> Tools,
    TimeSpan Deadline,
    int MaximumToolCalls);

/// <summary>Reports a terminal engine outcome without treating interruption as success.</summary>
/// <param name="Status">Terminal application turn status.</param>
/// <param name="Message">Optional final assistant text.</param>
/// <param name="FailureCode">Stable reason code when the turn did not complete.</param>
public sealed record AgentTurnResult(TurnStatus Status, string? Message, string? FailureCode);

/// <summary>Supplies one bounded stream of typed engine events and explicit cancellation.</summary>
public interface IAgentEngine
{
    /// <summary>Runs the requested turn and yields events until a terminal event or cancellation.</summary>
    /// <param name="request">Host-authorized provider, context, tools, and turn limits.</param>
    /// <param name="cancellationToken">Token that stops this turn and its cooperative callbacks.</param>
    /// <returns>Events in the order observed by the host.</returns>
    IAsyncEnumerable<AgentEvent> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    /// <summary>Requests cancellation of an active turn.</summary>
    /// <param name="turnId">Application-owned turn to cancel.</param>
    /// <param name="cancellationToken">Token that bounds the cancellation request.</param>
    /// <returns>A task that completes when the cancellation request is processed.</returns>
    ValueTask CancelAsync(TurnId turnId, CancellationToken cancellationToken);
}

/// <summary>Describes an item selected for an inspectable context packet.</summary>
/// <param name="SourceId">Stable source identifier for provenance.</param>
/// <param name="Text">Selected text after host policy filtering.</param>
/// <param name="PrivacyClass">Privacy classification assigned by the host.</param>
/// <param name="Role">Message role applied by the host, such as user or assistant.</param>
public sealed record ContextItem(string SourceId, string Text, string PrivacyClass, string Role);

/// <summary>Describes bounded, explicitly approximate prompt-size accounting.</summary>
/// <param name="SystemInstructionsCharacters">UTF-16 code units in host instructions.</param>
/// <param name="CurrentTaskCharacters">UTF-16 code units in the current user task.</param>
/// <param name="ToolCatalogCharacters">UTF-16 code units in the serialized host tool catalog.</param>
/// <param name="ConversationHistoryCharacters">UTF-16 code units in selected history message text.</param>
/// <param name="SerializationOverheadCharacters">Packet framing and JSON escaping code units.</param>
/// <param name="EstimatedInputTokens">Input token estimate before reserve margin.</param>
/// <param name="ReservedMarginTokens">Additional tokens reserved for tokenizer/provider variance.</param>
/// <param name="EstimatedInputTokensWithMargin">Estimate including the reserve margin.</param>
/// <param name="MaximumEstimatedInputTokens">Hard maximum estimate accepted by the builder.</param>
/// <param name="MinimumOmittedHistoryMessages">Known lower bound for messages not selected; the bounded history read cannot establish the full count.</param>
/// <param name="HasMoreHistory">Whether the extra sentinel row proves older history exists beyond the selected-message limit.</param>
/// <param name="IsExact">Whether a provider tokenizer produced an exact count; false for M1.</param>
/// <param name="EstimationMethod">Human-readable description of the estimate and margin.</param>
public sealed record ContextEstimate(
    int SystemInstructionsCharacters,
    int CurrentTaskCharacters,
    int ToolCatalogCharacters,
    int ConversationHistoryCharacters,
    int SerializationOverheadCharacters,
    int EstimatedInputTokens,
    int ReservedMarginTokens,
    int EstimatedInputTokensWithMargin,
    int MaximumEstimatedInputTokens,
    int MinimumOmittedHistoryMessages,
    bool HasMoreHistory,
    bool IsExact,
    string EstimationMethod);

/// <summary>Contains selected evidence and provenance for one inference request.</summary>
/// <param name="PacketId">Stable identifier for review and consent binding.</param>
/// <param name="PolicyVersion">Policy version used to select the content.</param>
/// <param name="Items">Ordered, bounded context items.</param>
/// <param name="Estimate">Approximate size accounting for the complete prompt envelope.</param>
public sealed record ContextPacket(
    string PacketId,
    string PolicyVersion,
    IReadOnlyList<ContextItem> Items,
    ContextEstimate Estimate);

/// <summary>Contains host-owned inputs used to build bounded local context.</summary>
/// <param name="ConversationId">Conversation whose durable history may be considered.</param>
/// <param name="Provider">Inference destination; M1 accepts only <see cref="ProviderKind.Local"/>.</param>
/// <param name="TaskText">Current user task, treated as untrusted text.</param>
/// <param name="HostInstructions">Host-authored instructions, never derived from model output.</param>
/// <param name="Tools">Exact host-registered tool definitions for the turn.</param>
public sealed record ContextBuildRequest(
    ConversationId ConversationId,
    ProviderKind Provider,
    string TaskText,
    string HostInstructions,
    IReadOnlyList<AgentToolDefinition> Tools);

/// <summary>Documents fixed M1 input bounds used by routing and context construction.</summary>
public static class LocalContextLimits
{
    /// <summary>Maximum UTF-16 code units accepted for the current task.</summary>
    public const int MaximumTaskTextCharacters = 8_000;

    /// <summary>Maximum UTF-16 code units accepted for host instructions.</summary>
    public const int MaximumInstructionCharacters = 8_000;

    /// <summary>Maximum registered tool definitions included in one estimate.</summary>
    public const int MaximumToolCount = 8;

    /// <summary>Maximum UTF-16 code units in a registered tool name.</summary>
    public const int MaximumToolNameCharacters = 128;

    /// <summary>Maximum UTF-16 code units in one registered tool schema.</summary>
    public const int MaximumToolSchemaCharacters = 4_096;

    /// <summary>Maximum recent messages read when constructing history context.</summary>
    public const int MaximumHistoryMessages = 12;

    /// <summary>Maximum UTF-16 code units selected from any one history message.</summary>
    public const int MaximumHistoryMessageCharacters = 8_000;

    /// <summary>Maximum approximate input tokens, including the reserved margin.</summary>
    public const int MaximumEstimatedInputTokens = 8_192;

    /// <summary>Character-to-token ratio used when no exact tokenizer is available.</summary>
    public const int EstimatedCharactersPerToken = 4;

    /// <summary>Percentage of estimated tokens held as a safety margin.</summary>
    public const int ReservedTokenMarginPercent = 20;
}

/// <summary>Builds privacy-classified context from application-owned state.</summary>
public interface IContextBuilder
{
    /// <summary>Builds a bounded local context packet and estimates its full prompt envelope.</summary>
    /// <param name="request">Host-selected task, instructions, provider, and registered tools.</param>
    /// <param name="cancellationToken">Token that cancels retrieval and packet construction.</param>
    /// <returns>An inspectable packet with source provenance, privacy classifications, and estimate.</returns>
    ValueTask<ContextPacket> BuildAsync(ContextBuildRequest request, CancellationToken cancellationToken);
}

/// <summary>Represents the user's requested route and the host's policy context.</summary>
/// <param name="RequestedMode">Owner-selected route mode.</param>
/// <param name="TaskText">Current user request, treated as untrusted input.</param>
/// <param name="HasLocalOnlyContent">Whether selected evidence contains LocalOnly material.</param>
/// <param name="TaskKind">Task category classified by the host, never by model output.</param>
public sealed record RoutingRequest(
    RouteMode RequestedMode,
    string TaskText,
    bool HasLocalOnlyContent,
    RoutingTaskKind TaskKind = RoutingTaskKind.Ambiguous);

/// <summary>Describes a deterministic routing decision made by host policy.</summary>
public sealed record RouteDecision
{
    private RouteDecision(
        RouteDisposition disposition,
        ProviderKind? provider,
        string reasonCode,
        string policyVersion,
        string? userMessage)
    {
        Disposition = disposition;
        Provider = provider;
        ReasonCode = reasonCode;
        PolicyVersion = policyVersion;
        UserMessage = userMessage;
    }

    /// <summary>Gets the typed route outcome.</summary>
    public RouteDisposition Disposition { get; }

    /// <summary>Gets the selected provider; non-null only for a local decision.</summary>
    public ProviderKind? Provider { get; }

    /// <summary>Gets the stable policy reason code.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the policy version that produced the decision.</summary>
    public string PolicyVersion { get; }

    /// <summary>Gets safe owner-facing explanation for clarification or unsupported outcomes.</summary>
    public string? UserMessage { get; }

    /// <summary>Creates a decision to proceed with the local provider.</summary>
    /// <param name="reasonCode">Stable, non-empty policy reason code.</param>
    /// <param name="policyVersion">Non-empty policy version.</param>
    /// <returns>A typed Local decision with no cloud provider alternative.</returns>
    public static RouteDecision Local(string reasonCode, string policyVersion)
    {
        ValidateReason(reasonCode, policyVersion);
        return new RouteDecision(RouteDisposition.Local, ProviderKind.Local, reasonCode, policyVersion, null);
    }

    /// <summary>Creates a decision that requires owner clarification before a turn.</summary>
    /// <param name="reasonCode">Stable, non-empty policy reason code.</param>
    /// <param name="policyVersion">Non-empty policy version.</param>
    /// <param name="userMessage">Safe clarification presented to the owner.</param>
    /// <returns>A typed Clarify decision with no provider selected.</returns>
    public static RouteDecision Clarify(string reasonCode, string policyVersion, string userMessage)
    {
        ValidateReason(reasonCode, policyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        return new RouteDecision(RouteDisposition.Clarify, null, reasonCode, policyVersion, userMessage);
    }

    /// <summary>Creates a decision that rejects an unavailable or prohibited task.</summary>
    /// <param name="reasonCode">Stable, non-empty policy reason code.</param>
    /// <param name="policyVersion">Non-empty policy version.</param>
    /// <param name="userMessage">Safe explanation presented to the owner.</param>
    /// <returns>A typed Unsupported decision with no provider selected.</returns>
    public static RouteDecision Unsupported(string reasonCode, string policyVersion, string userMessage)
    {
        ValidateReason(reasonCode, policyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        return new RouteDecision(RouteDisposition.Unsupported, null, reasonCode, policyVersion, userMessage);
    }

    private static void ValidateReason(string reasonCode, string policyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
    }
}

/// <summary>Selects a provider from explicit owner policy and classified context.</summary>
public interface IModelRouter
{
    /// <summary>Evaluates the route without allowing model output to change owner policy.</summary>
    /// <param name="request">Owner mode and host-derived privacy facts.</param>
    /// <param name="cancellationToken">Token that cancels policy evaluation.</param>
    /// <returns>A provider, clarification, or unsupported decision with a reason code.</returns>
    ValueTask<RouteDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken);
}

/// <summary>Contains one model-proposed tool call before host validation.</summary>
/// <param name="TurnId">Turn that emitted the proposal.</param>
/// <param name="CallId">Engine-local call identifier for event correlation.</param>
/// <param name="ToolName">Registered tool name requested by the model.</param>
/// <param name="ArgumentsJson">Untrusted JSON arguments to validate against the tool schema.</param>
public sealed record ToolDispatchRequest(TurnId TurnId, string CallId, string ToolName, string ArgumentsJson);

/// <summary>Reports the host's dispatch outcome, including uncertain physical results.</summary>
/// <param name="Status">Stable outcome such as Succeeded, Rejected, Failed, or Unknown.</param>
/// <param name="ResultJson">Optional typed tool result serialized for the engine.</param>
/// <param name="ReasonCode">Stable reason code for denial or failure.</param>
public sealed record ToolDispatchResult(string Status, string? ResultJson, string? ReasonCode);

/// <summary>Validates, authorizes, journals, executes, and verifies registered tool calls.</summary>
public interface IToolDispatcher
{
    /// <summary>Dispatches one untrusted proposal through host-side policy checks.</summary>
    /// <param name="request">Model-proposed call; its arguments are never trusted as authorization.</param>
    /// <param name="cancellationToken">Token that cancels work before an external side effect is sent.</param>
    /// <returns>A truthful result; ambiguous physical outcomes are reported as Unknown.</returns>
    ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken);
}

/// <summary>Describes an exact-action approval request.</summary>
/// <param name="Id">Application-owned approval identifier.</param>
/// <param name="ActionId">Journaled action whose canonical arguments are approved.</param>
/// <param name="OwnerId">Authenticated owner identity.</param>
/// <param name="ExpiresAtUtc">UTC expiry instant after which approval is invalid.</param>
/// <param name="ExpectedVersion">Concurrency version used when resolving the request.</param>
public sealed record ApprovalRequest(
    ApprovalId Id,
    ActionId ActionId,
    string OwnerId,
    DateTimeOffset ExpiresAtUtc,
    long ExpectedVersion);

/// <summary>Creates and resolves expiring, single-use approvals bound to exact actions.</summary>
public interface IApprovalService
{
    /// <summary>Creates an approval request for a journaled action.</summary>
    /// <param name="actionId">Action with canonical arguments and current policy context.</param>
    /// <param name="ownerId">Authenticated owner who may decide the request.</param>
    /// <param name="cancellationToken">Token that cancels persistence before creation completes.</param>
    /// <returns>The persisted request with expiry and concurrency version.</returns>
    ValueTask<ApprovalRequest> CreateAsync(
        ActionId actionId,
        string ownerId,
        CancellationToken cancellationToken);

    /// <summary>Resolves an approval using compare-and-swap version semantics.</summary>
    /// <param name="approvalId">Approval request to resolve.</param>
    /// <param name="ownerId">Authenticated owner deciding the request.</param>
    /// <param name="expectedVersion">Version shown to the owner when the decision was made.</param>
    /// <param name="approved">Whether to approve the exact action or reject it.</param>
    /// <param name="cancellationToken">Token that cancels the state update.</param>
    /// <returns>The resolved request; expired, replayed, or changed requests are rejected.</returns>
    ValueTask<ApprovalRequest> ResolveAsync(
        ApprovalId approvalId,
        string ownerId,
        long expectedVersion,
        bool approved,
        CancellationToken cancellationToken);
}

/// <summary>Describes a memory fact with provenance and optimistic concurrency version.</summary>
/// <param name="Id">Stable fact identifier.</param>
/// <param name="Subject">Fact subject.</param>
/// <param name="Key">Fact key within the subject.</param>
/// <param name="Value">Owner-confirmed fact value.</param>
/// <param name="SourceId">Provenance reference.</param>
/// <param name="PrivacyClass">Host-assigned privacy classification.</param>
/// <param name="Version">Concurrency version incremented on accepted updates.</param>
public sealed record MemoryFact(
    MemoryFactId Id,
    string Subject,
    string Key,
    string Value,
    string SourceId,
    string PrivacyClass,
    long Version);

/// <summary>Stores and retrieves explicit owner-confirmed facts.</summary>
public interface IMemoryStore
{
    /// <summary>Searches permitted facts and returns provenance-bearing results.</summary>
    /// <param name="query">Bounded search text.</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="cancellationToken">Token that cancels the search.</param>
    /// <returns>Matching facts with source identifiers.</returns>
    ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Gets one fact by its stable identifier.</summary>
    /// <param name="id">Fact identifier.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The fact, or <see langword="null"/> when it is absent or deleted.</returns>
    ValueTask<MemoryFact?> GetAsync(MemoryFactId id, CancellationToken cancellationToken);

    /// <summary>Persists an owner-confirmed fact using expected-version concurrency.</summary>
    /// <param name="fact">Proposed fact value and provenance.</param>
    /// <param name="expectedVersion">Current version, or zero when creating the fact.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The saved fact with its incremented version.</returns>
    ValueTask<MemoryFact> SaveAsync(MemoryFact fact, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Deletes a fact using expected-version concurrency.</summary>
    /// <param name="id">Fact to delete.</param>
    /// <param name="expectedVersion">Version displayed to the owner.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    ValueTask DeleteAsync(MemoryFactId id, long expectedVersion, CancellationToken cancellationToken);
}

/// <summary>Represents an application-owned conversation message.</summary>
/// <param name="MessageId">Stable message identifier.</param>
/// <param name="ConversationId">Owning conversation identifier.</param>
/// <param name="Role">Allowed role such as user, assistant, or tool.</param>
/// <param name="Content">Message body stored by the application.</param>
/// <param name="CreatedAtUtc">UTC creation instant.</param>
public sealed record ConversationMessage(
    Guid MessageId,
    ConversationId ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAtUtc);

/// <summary>Describes the durable lifecycle state and version of one application turn.</summary>
/// <param name="Id">Application-owned turn identifier.</param>
/// <param name="ConversationId">Owning conversation identifier.</param>
/// <param name="Status">Current host-owned turn lifecycle status.</param>
/// <param name="CreatedAtUtc">UTC creation instant.</param>
/// <param name="UpdatedAtUtc">UTC instant of the latest state or event change.</param>
/// <param name="Version">Positive optimistic-concurrency version.</param>
public sealed record ConversationTurn(
    TurnId Id,
    ConversationId ConversationId,
    TurnStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

/// <summary>Describes a turn event with an assigned, durable sequence number.</summary>
/// <param name="EventId">Stable event identifier.</param>
/// <param name="TurnId">Owning application turn.</param>
/// <param name="Sequence">One-based sequence within the turn.</param>
/// <param name="EventType">Stable application event type.</param>
/// <param name="PayloadJson">Structured event data, excluding secrets and unapproved private content.</param>
/// <param name="OccurredAtUtc">UTC instant the host observed the event.</param>
public sealed record PersistedTurnEvent(
    Guid EventId,
    TurnId TurnId,
    long Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);

/// <summary>Persists durable conversation messages and ordered turn events.</summary>
public interface IConversationStore
{
    /// <summary>Appends a message and assigns its durable sequence.</summary>
    /// <param name="message">Message to persist.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The persisted message.</returns>
    ValueTask<ConversationMessage> AppendMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken);

    /// <summary>Reads a bounded, ordered conversation history for context construction.</summary>
    /// <param name="conversationId">Conversation to read.</param>
    /// <param name="maximumMessages">Maximum messages to return.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>Messages in chronological order.</returns>
    ValueTask<IReadOnlyList<ConversationMessage>> ReadRecentAsync(
        ConversationId conversationId,
        int maximumMessages,
        CancellationToken cancellationToken);

    /// <summary>Creates a durable turn in a conversation with the supplied host-owned status.</summary>
    /// <param name="turnId">Stable application turn identifier.</param>
    /// <param name="conversationId">Owning conversation identifier.</param>
    /// <param name="status">Initial lifecycle status assigned by the host.</param>
    /// <param name="createdAtUtc">UTC creation instant.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The persisted turn at version 1.</returns>
    ValueTask<ConversationTurn> CreateTurnAsync(
        TurnId turnId,
        ConversationId conversationId,
        TurnStatus status,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Reads the current persisted state and optimistic-concurrency version for a turn.</summary>
    /// <param name="turnId">Turn identifier to inspect.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The persisted turn, or null when the identifier is unknown.</returns>
    ValueTask<ConversationTurn?> GetTurnAsync(
        TurnId turnId,
        CancellationToken cancellationToken);

    /// <summary>Reads a bounded, stable-ordered page of nonterminal turns for recovery inspection.</summary>
    /// <remarks>Returned state is authoritative for inspection; it does not authorize replaying interrupted work.</remarks>
    /// <param name="maximumTurns">Maximum turns to return, from 1 through 1000.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>Nonterminal turns ordered by their latest state or event timestamp.</returns>
    ValueTask<IReadOnlyList<ConversationTurn>> ReadNonterminalTurnsAsync(
        int maximumTurns,
        CancellationToken cancellationToken);

    /// <summary>Updates turn lifecycle state using expected-version concurrency.</summary>
    /// <param name="turnId">Turn to update.</param>
    /// <param name="status">New host-owned lifecycle status.</param>
    /// <param name="expectedVersion">Version previously read by the caller.</param>
    /// <param name="updatedAtUtc">UTC state-change instant.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The updated turn with an incremented version.</returns>
    ValueTask<ConversationTurn> UpdateTurnStatusAsync(
        TurnId turnId,
        TurnStatus status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Appends an event and atomically assigns its next per-turn sequence number.</summary>
    /// <param name="turnId">Turn that owns the event.</param>
    /// <param name="eventType">Stable event type.</param>
    /// <param name="payloadJson">Structured event data.</param>
    /// <param name="occurredAtUtc">UTC observation instant.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The persisted event with its assigned sequence.</returns>
    ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
        TurnId turnId,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Reads a bounded page of turn events strictly after a sequence cursor.</summary>
    /// <param name="turnId">Turn whose event stream is read.</param>
    /// <param name="afterSequence">Exclusive cursor; zero starts at the first event.</param>
    /// <param name="maximumEvents">Maximum events, from 1 through 1000.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>Events in ascending sequence order.</returns>
    ValueTask<IReadOnlyList<PersistedTurnEvent>> ReadTurnEventsAfterAsync(
        TurnId turnId,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken);
}

/// <summary>Describes a durable job lease and its expected owner.</summary>
/// <param name="Id">Stable scheduled job identifier.</param>
/// <param name="LeaseOwner">Worker identifier holding the lease.</param>
/// <param name="LeaseExpiresAtUtc">UTC lease expiry instant.</param>
/// <param name="PayloadVersion">Version of the serialized job payload.</param>
public sealed record JobLease(JobId Id, string LeaseOwner, DateTimeOffset LeaseExpiresAtUtc, int PayloadVersion);

/// <summary>Persists job schedules, claims due work, and records terminal outcomes.</summary>
public interface IJobStore
{
    /// <summary>Claims the next due job using an expiring lease.</summary>
    /// <param name="workerId">Stable identifier for this worker process.</param>
    /// <param name="nowUtc">Caller-observed UTC instant. Storage refreshes it after obtaining the write transaction, never moving it backward.</param>
    /// <param name="leaseDuration">Maximum duration of the claimed lease.</param>
    /// <param name="cancellationToken">Token that cancels before the claim is committed.</param>
    /// <returns>A lease, or <see langword="null"/> when no job is due.</returns>
    ValueTask<JobLease?> ClaimDueAsync(
        string workerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>Records one outcome without replaying an ambiguous physical action.</summary>
    /// <param name="lease">Lease that currently owns the job.</param>
    /// <param name="outcome">Terminal or recoverable outcome code.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    ValueTask CompleteAsync(JobLease lease, string outcome, CancellationToken cancellationToken);
}

/// <summary>Describes a durable journal entry for one proposed action.</summary>
/// <param name="Id">Stable application action identifier.</param>
/// <param name="ActionType">Registered action type, never an arbitrary provider operation.</param>
/// <param name="CanonicalArguments">Canonical action arguments retained for audit and idempotency.</param>
/// <param name="RequestHash">Hash of the canonical action request.</param>
/// <param name="Status">Known journal status: Prepared, AwaitingApproval, Executing, Succeeded, Failed, Unknown, Rejected, or Expired.</param>
/// <param name="CreatedAtUtc">UTC instant the action was journaled.</param>
/// <param name="UpdatedAtUtc">UTC instant of the latest journal change.</param>
/// <param name="Version">Positive optimistic-concurrency version.</param>
public sealed record ActionJournalEntry(
    ActionId Id,
    string ActionType,
    string CanonicalArguments,
    string RequestHash,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

/// <summary>Persists action intent and observed outcomes before and after external side effects.</summary>
public interface IActionJournalStore
{
    /// <summary>Persists a prepared action, rejecting reuse of its identifier for different content.</summary>
    /// <param name="entry">Prepared action with a stable ID and canonical request hash.</param>
    /// <param name="cancellationToken">Token that cancels the write before commit.</param>
    /// <returns>The durable entry, including its initial version.</returns>
    ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Gets one action entry by its stable identifier.</summary>
    /// <param name="id">Action identifier.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> when absent.</returns>
    ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken);

    /// <summary>Compare-and-swaps an action outcome without replaying the external operation.</summary>
    /// <param name="id">Action identifier.</param>
    /// <param name="status">Next journal status.</param>
    /// <param name="expectedVersion">Version previously read by the caller.</param>
    /// <param name="updatedAtUtc">UTC time at which the outcome was observed.</param>
    /// <param name="cancellationToken">Token that cancels the write before commit.</param>
    /// <returns>The entry with its incremented version.</returns>
    ValueTask<ActionJournalEntry> UpdateStatusAsync(
        ActionId id,
        string status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Describes the durable, exact-action binding of one approval request.</summary>
/// <param name="Id">Stable approval identifier.</param>
/// <param name="ActionId">Action whose canonical arguments are being approved.</param>
/// <param name="OwnerId">Owner identity to which the request is bound.</param>
/// <param name="Status">Pending, Approved, Rejected, or Expired.</param>
/// <param name="CreatedAtUtc">UTC creation instant.</param>
/// <param name="ExpiresAtUtc">UTC instant after which the request cannot be resolved.</param>
/// <param name="ResolvedAtUtc">UTC resolution instant, or null while pending.</param>
/// <param name="Version">Positive optimistic-concurrency version.</param>
public sealed record ApprovalStorageRecord(
    ApprovalId Id,
    ActionId ActionId,
    string OwnerId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    long Version);

/// <summary>Persists approval requests bound to one owner and one journaled action.</summary>
public interface IApprovalStore
{
    /// <summary>Creates a pending approval request for an existing action.</summary>
    /// <param name="approval">Pending request with an exact action and expiry.</param>
    /// <param name="cancellationToken">Token that cancels the write before commit.</param>
    /// <returns>The persisted request with its initial version.</returns>
    ValueTask<ApprovalStorageRecord> CreateAsync(ApprovalStorageRecord approval, CancellationToken cancellationToken);

    /// <summary>Gets one approval request by its stable identifier.</summary>
    /// <param name="id">Approval identifier.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The request, or <see langword="null"/> when absent.</returns>
    ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken);

    /// <summary>Resolves a still-pending, unexpired request once using owner and version checks.</summary>
    /// <param name="id">Approval identifier.</param>
    /// <param name="ownerId">Owner identity bound to the request.</param>
    /// <param name="status">Approved or Rejected decision.</param>
    /// <param name="expectedVersion">Version previously read by the caller.</param>
    /// <param name="resolvedAtUtc">UTC resolution time used for expiry validation.</param>
    /// <param name="cancellationToken">Token that cancels the write before commit.</param>
    /// <returns>The resolved request with its incremented version.</returns>
    ValueTask<ApprovalStorageRecord> ResolveAsync(
        ApprovalId id,
        string ownerId,
        string status,
        long expectedVersion,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Describes one append-only operational audit event.</summary>
/// <param name="Id">Stable event identifier.</param>
/// <param name="EventType">Stable event type, not free-form prompt or provider content.</param>
/// <param name="SubjectId">Optional application object identifier.</param>
/// <param name="PayloadJson">Structured, privacy-filtered event details.</param>
/// <param name="OccurredAtUtc">UTC time the event occurred.</param>
public sealed record AuditEventRecord(
    Guid Id,
    string EventType,
    string? SubjectId,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);

/// <summary>Appends and queries privacy-filtered operational audit events.</summary>
public interface IAuditStore
{
    /// <summary>Appends one audit event without overwriting an existing event.</summary>
    /// <param name="auditEvent">Structured event that contains no prompt, secret, or private memory by default.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    ValueTask AppendAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken);

    /// <summary>Reads a bounded, ordered page of events at or after a UTC instant.</summary>
    /// <param name="fromUtc">Inclusive UTC lower bound.</param>
    /// <param name="maximumEvents">Maximum number of events to return, from 1 through 1000.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>Audit events in occurrence order.</returns>
    ValueTask<IReadOnlyList<AuditEventRecord>> ReadSinceAsync(
        DateTimeOffset fromUtc,
        int maximumEvents,
        CancellationToken cancellationToken);
}

/// <summary>Reports a stale or conflicting optimistic-concurrency update.</summary>
public sealed class PersistenceConcurrencyException : InvalidOperationException
{
    /// <summary>Initializes an exception for a persistence operation that lost a compare-and-swap race.</summary>
    /// <param name="message">Safe description of the conflicting persistence operation.</param>
    public PersistenceConcurrencyException(string message)
        : base(message)
    {
    }
}

/// <summary>Runs due jobs through deterministic, host-owned handlers.</summary>
public interface IJobRunner
{
    /// <summary>Claims and runs at most one due job.</summary>
    /// <param name="workerId">Stable worker identifier.</param>
    /// <param name="cancellationToken">Token that stops polling and cooperative handlers.</param>
    /// <returns><see langword="true"/> when a job was claimed and processed.</returns>
    ValueTask<bool> RunOneAsync(string workerId, CancellationToken cancellationToken);
}

/// <summary>Represents an allowlisted Home Assistant entity snapshot.</summary>
/// <param name="EntityId">Canonical entity identifier.</param>
/// <param name="State">Current state string.</param>
/// <param name="ObservedAtUtc">UTC source observation instant.</param>
/// <param name="Available">Whether the source reports the entity available.</param>
public sealed record HomeAssistantEntity(string EntityId, string State, DateTimeOffset ObservedAtUtc, bool Available);

/// <summary>Provides typed reads and narrowly mapped operations for approved Home Assistant entities.</summary>
public interface IHomeAssistantClient
{
    /// <summary>Reads the current snapshot for an approved entity.</summary>
    /// <param name="entityId">Canonical entity identifier from the trusted allowlist.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>Typed state and source observation time.</returns>
    ValueTask<HomeAssistantEntity> GetStateAsync(string entityId, CancellationToken cancellationToken);
}

/// <summary>Names a protected secret reference without carrying its secret value.</summary>
/// <param name="Name">Configuration key identifying the external secret.</param>
public sealed record SecretReference(string Name);

/// <summary>Resolves named secret references for server-side adapters only.</summary>
public interface ISecretResolver
{
    /// <summary>Resolves a named secret for use by the requesting adapter.</summary>
    /// <param name="reference">Non-secret reference name from trusted configuration.</param>
    /// <param name="cancellationToken">Token that cancels resolution.</param>
    /// <returns>Secret value for server-side use; never expose it to UI or model context.</returns>
    ValueTask<ReadOnlyMemory<char>> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}

/// <summary>Provides the current UTC time for deterministic policy and scheduling behavior.</summary>
public interface IClock
{
    /// <summary>Gets the current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Represents one typed event emitted during an agent turn.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC instant when the host observed the event.</param>
public abstract record AgentEvent(TurnId TurnId, DateTimeOffset OccurredAtUtc);

/// <summary>Signals that a turn was accepted and is starting.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
public sealed record TurnStarted(TurnId TurnId, DateTimeOffset OccurredAtUtc) : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Reports the explicit route chosen by the host.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="Provider">Selected provider.</param>
/// <param name="ReasonCode">Stable route-policy reason.</param>
public sealed record RouteSelected(
    TurnId TurnId,
    DateTimeOffset OccurredAtUtc,
    ProviderKind Provider,
    string ReasonCode) : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Signals that context must be inspected or approved before inference.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="PacketId">Context packet requiring review.</param>
public sealed record ContextReviewRequired(
    TurnId TurnId,
    DateTimeOffset OccurredAtUtc,
    string PacketId) : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Contains a nonterminal assistant text fragment.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="Text">Text fragment; it is not a terminal success outcome.</param>
public sealed record TextDelta(TurnId TurnId, DateTimeOffset OccurredAtUtc, string Text)
    : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Reports a tool proposal before dispatch policy is evaluated.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="CallId">Engine-local identifier used for event correlation.</param>
/// <param name="ToolName">Proposed registered tool name.</param>
public sealed record ToolProposed(
    TurnId TurnId,
    DateTimeOffset OccurredAtUtc,
    string CallId,
    string ToolName) : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Signals that a proposed action requires owner approval.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="ApprovalId">Exact approval request identifier.</param>
public sealed record ApprovalRequired(TurnId TurnId, DateTimeOffset OccurredAtUtc, ApprovalId ApprovalId)
    : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Reports a tool's host-recorded completion or uncertain outcome.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="CallId">Engine-local identifier used for event correlation.</param>
/// <param name="Outcome">Outcome code, including Unknown for ambiguous writes.</param>
public sealed record ToolCompleted(
    TurnId TurnId,
    DateTimeOffset OccurredAtUtc,
    string CallId,
    string Outcome) : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Signals successful completion of the application-owned turn.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
public sealed record TurnCompleted(TurnId TurnId, DateTimeOffset OccurredAtUtc)
    : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Reports a failed turn with a safe, stable reason code.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="ReasonCode">Failure code without private prompts or provider payloads.</param>
public sealed record TurnFailed(TurnId TurnId, DateTimeOffset OccurredAtUtc, string ReasonCode)
    : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Signals a cooperative cancellation outcome.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
public sealed record TurnCancelled(TurnId TurnId, DateTimeOffset OccurredAtUtc)
    : AgentEvent(TurnId, OccurredAtUtc);

/// <summary>Signals termination without a confirmed engine completion event.</summary>
/// <param name="TurnId">Application-owned turn identifier.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
/// <param name="ReasonCode">Stable interruption reason code.</param>
public sealed record TurnInterrupted(TurnId TurnId, DateTimeOffset OccurredAtUtc, string ReasonCode)
    : AgentEvent(TurnId, OccurredAtUtc);
