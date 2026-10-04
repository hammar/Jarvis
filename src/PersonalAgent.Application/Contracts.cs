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
public sealed record ContextItem(string SourceId, string Text, string PrivacyClass);

/// <summary>Contains selected evidence and provenance for one inference request.</summary>
/// <param name="PacketId">Stable identifier for review and consent binding.</param>
/// <param name="PolicyVersion">Policy version used to select the content.</param>
/// <param name="Items">Ordered, bounded context items.</param>
public sealed record ContextPacket(string PacketId, string PolicyVersion, IReadOnlyList<ContextItem> Items);

/// <summary>Builds privacy-classified context from application-owned state.</summary>
public interface IContextBuilder
{
    /// <summary>Builds a bounded context packet for the specified route.</summary>
    /// <param name="conversationId">Conversation whose authorized history may be considered.</param>
    /// <param name="provider">Destination that will receive the packet.</param>
    /// <param name="cancellationToken">Token that cancels retrieval and packet construction.</param>
    /// <returns>An inspectable packet with source provenance and privacy classifications.</returns>
    ValueTask<ContextPacket> BuildAsync(
        ConversationId conversationId,
        ProviderKind provider,
        CancellationToken cancellationToken);
}

/// <summary>Represents the user's requested route and the host's policy context.</summary>
/// <param name="RequestedMode">Owner-selected route mode.</param>
/// <param name="TaskText">Current user request, treated as untrusted input.</param>
/// <param name="HasLocalOnlyContent">Whether selected evidence contains LocalOnly material.</param>
public sealed record RoutingRequest(RouteMode RequestedMode, string TaskText, bool HasLocalOnlyContent);

/// <summary>Describes a deterministic routing decision made by host policy.</summary>
/// <param name="Provider">Selected provider when routing can proceed.</param>
/// <param name="RequiresClarification">Whether the host must ask before starting a turn.</param>
/// <param name="ReasonCode">Stable policy reason explaining the decision.</param>
/// <param name="PolicyVersion">Version of the policy that produced the decision.</param>
public sealed record RouteDecision(
    ProviderKind? Provider,
    bool RequiresClarification,
    string ReasonCode,
    string PolicyVersion);

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
    /// <param name="nowUtc">Current UTC instant from the injected clock.</param>
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
public abstract record AgentEvent(TurnId TurnId, DateTimeOffset OccurredAtUtc)
{
    /// <summary>
    /// Gets the 1-based, strictly increasing sequence assigned within one turn.
    /// Persisted event stores use this value as the reconnect cursor.
    /// </summary>
    public long SequenceNumber { get; init; }
}

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
