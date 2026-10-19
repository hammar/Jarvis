using System.Text.Json;
using PersonalAgent.Application.Memory;
using PersonalAgent.Application.Tools;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Approvals;

/// <summary>Provides an owner-facing, redacted summary of one journaled approval.</summary>
/// <param name="Approval">Owner-bound approval and its expiry/version state.</param>
/// <param name="ActionId">Stable journaled action identifier.</param>
/// <param name="ToolName">Registered host capability name.</param>
/// <param name="ActionType">Stable application action category.</param>
/// <param name="Operation">Narrow host-validated operation.</param>
/// <param name="EntityId">Exact resolved household entity, when applicable.</param>
/// <param name="ActionStatus">Current durable action state.</param>
/// <param name="ArgumentsJson">Validated display arguments without owner, source, or secret fields.</param>
public sealed record ApprovalActionSummary(
    ApprovalRequest Approval,
    ActionId ActionId,
    string ToolName,
    string ActionType,
    string Operation,
    string? EntityId,
    string ActionStatus,
    string ArgumentsJson);

/// <summary>Coordinates trusted approval decisions and owner-initiated resumption of exact approved actions.</summary>
public sealed class ApprovalWorkflowService
{
    private readonly IApprovalStore approvalStore;
    private readonly IActionJournalStore journal;
    private readonly IApprovalService approvals;
    private readonly IToolDispatcher dispatcher;
    private readonly IConfirmedMemoryStore memories;
    private readonly MemoryProposalService memoryProposals;

    /// <summary>Creates approval list, preview, and exact-action decision use cases.</summary>
    public ApprovalWorkflowService(
        IApprovalStore approvalStore,
        IActionJournalStore journal,
        IApprovalService approvals,
        IToolDispatcher dispatcher,
        IConfirmedMemoryStore memories,
        MemoryProposalService memoryProposals)
    {
        this.approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.memories = memories ?? throw new ArgumentNullException(nameof(memories));
        this.memoryProposals = memoryProposals ?? throw new ArgumentNullException(nameof(memoryProposals));
    }

    /// <summary>Lists bounded recent approvals belonging to one authenticated owner.</summary>
    /// <param name="ownerId">Authenticated owner identity.</param>
    /// <param name="maximumRequests">Maximum number of entries, from 1 through 100.</param>
    /// <param name="cancellationToken">Token that cancels reads.</param>
    /// <returns>Owner-bound action previews with secret and provenance fields removed.</returns>
    public async ValueTask<IReadOnlyList<ApprovalActionSummary>> ReadRecentAsync(
        string ownerId,
        int maximumRequests,
        CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        if (maximumRequests is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        }

        var records = await approvalStore.ReadRecentForOwnerAsync(ownerId, maximumRequests, cancellationToken);
        var summaries = new List<ApprovalActionSummary>(records.Count);
        foreach (var record in records)
        {
            if (!string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The approval store returned a record for another owner.");
            }

            var action = await journal.GetAsync(record.ActionId, cancellationToken)
                ?? throw new InvalidDataException("An approval is missing its durable action.");
            if (action.MemoryInvalidated)
            {
                if (!string.Equals(action.MemoryOwnerId, ownerId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("An invalidated memory approval is not bound to its owner.");
                }

                dispatcher.ReleaseApproval(action.Id);
                summaries.Add(new(
                    new(record.Id, record.ActionId, record.OwnerId, record.ExpiresAtUtc, record.Version, record.Status),
                    action.Id,
                    "memory.propose_change",
                    action.ActionType,
                    "invalidated",
                    null,
                    "Invalidated",
                    """{"invalidated":true}"""));
                continue;
            }

            var envelope = ReadEnvelope(action);
            if (!string.Equals(envelope.OwnerId, ownerId, StringComparison.Ordinal)
                || envelope.ToolName is not ("home.set_light" or "home.set_temperature" or "memory.propose_change"))
            {
                throw new InvalidDataException("An approval is not bound to an approved owner tool.");
            }

            var arguments = envelope.CanonicalArguments;
            if (envelope.ToolName == "memory.propose_change")
            {
                var proposal = await memoryProposals.PreviewAsync(ownerId, action.Id, cancellationToken);
                arguments = JsonSerializer.Serialize(new
                {
                    change = proposal.Command.Change,
                    conflicts = proposal.Conflicts
                });
            }

            summaries.Add(new(
                new(record.Id, record.ActionId, record.OwnerId, record.ExpiresAtUtc, record.Version, record.Status),
                action.Id,
                envelope.ToolName,
                envelope.ActionType,
                envelope.Operation,
                envelope.EntityId,
                action.Status,
                arguments));
        }
        return summaries;
    }

    /// <summary>
    /// Resolves an exact owner-bound approval or resumes its already-approved, unclaimed action and executes at most once.
    /// </summary>
    /// <param name="ownerId">Authenticated owner deciding the action.</param>
    /// <param name="approvalId">Approval identifier from the trusted UI.</param>
    /// <param name="expectedVersion">Version displayed to the owner.</param>
    /// <param name="approved">True to approve, false to reject the exact action.</param>
    /// <param name="cancellationToken">Token that cancels work before an external submission.</param>
    /// <returns>The durable action outcome, including an explicit Unknown status when effects are uncertain.</returns>
    public async ValueTask<ToolDispatchResult> ResolveAsync(
        string ownerId,
        ApprovalId approvalId,
        long expectedVersion,
        bool approved,
        CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        var approval = await approvals.GetAsync(approvalId, cancellationToken)
            ?? throw new KeyNotFoundException("The approval request is unavailable.");
        if (!string.Equals(approval.OwnerId, ownerId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("The approval request is unavailable.");
        }

        var action = await journal.GetAsync(approval.ActionId, cancellationToken)
            ?? throw new PersistenceConcurrencyException("The approved action is unavailable.");
        if (action.MemoryInvalidated)
        {
            if (!string.Equals(action.MemoryOwnerId, ownerId, StringComparison.Ordinal))
            {
                throw new KeyNotFoundException("The approval request is unavailable.");
            }

            dispatcher.ReleaseApproval(action.Id);
            return new("Rejected", null, "memory_result_invalidated", action.Id, approvalId);
        }

        var envelope = ReadEnvelope(action);
        if (!string.Equals(envelope.OwnerId, ownerId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("The approval request is unavailable.");
        }

        if (envelope.ToolName == "memory.propose_change")
        {
            var memoryResult = await memoryProposals.ResolveAsync(
                ownerId, action.Id, expectedVersion, approved, cancellationToken);
            if (memoryResult.Status != "AwaitingApproval")
            {
                dispatcher.ReleaseApproval(action.Id);
            }
            return memoryResult;
        }

        if (envelope.ToolName is not ("home.set_light" or "home.set_temperature"))
        {
            throw new InvalidDataException("The approval does not reference a supported owner action.");
        }

        var turnId = new TurnId(Guid.Parse(envelope.TurnId));
        ConversationMessage? source = null;
        ApprovalRequest resolved;
        if (approval.Status == "Approved")
        {
            if (!approved || approval.ExpectedVersion != expectedVersion)
            {
                throw new PersistenceConcurrencyException(
                    "The approved action cannot be changed or its version has changed.");
            }
            if (action.Status != "AwaitingApproval")
            {
                return action.Outcome ?? new(action.Status, null, "recorded_action_outcome", action.Id, approvalId);
            }

            resolved = approval;
        }
        else
        {
            source = await memories.ReadSourceAsync(ownerId, turnId, cancellationToken)
                ?? throw new PersistenceConcurrencyException("The original owner request is unavailable.");
            resolved = await approvals.ResolveAsync(
                approvalId, ownerId, expectedVersion, approved, cancellationToken);
        }
        if (resolved.Status != "Approved")
        {
            dispatcher.ReleaseApproval(action.Id);
            var settled = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidDataException("The settled action disappeared.");
            return settled.Outcome ?? new(settled.Status, null, $"approval_{resolved.Status.ToLowerInvariant()}",
                settled.Id, approvalId, resolved.ExpiresAtUtc);
        }

        var approvedSource = source ?? await memories.ReadSourceAsync(ownerId, turnId, cancellationToken)
            ?? throw new PersistenceConcurrencyException("The original owner request is unavailable.");
        var request = new ToolDispatchRequest(
            turnId,
            envelope.CallId,
            envelope.ToolName,
            envelope.CanonicalArguments,
            ownerId,
            approvedSource.Content)
        {
            ReplayCurrentRequestHash = envelope.CurrentRequestHash
        };
        return await dispatcher.DispatchAsync(request, cancellationToken);
    }

    private static CanonicalActionEnvelope ReadEnvelope(ActionJournalEntry action)
    {
        try
        {
            return JsonSerializer.Deserialize<CanonicalActionEnvelope>(action.CanonicalArguments)
                ?? throw new InvalidDataException("The action envelope is missing.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The action envelope is invalid.", error);
        }
    }

    private sealed record CanonicalActionEnvelope(
        int FormatVersion,
        string TurnId,
        string CallId,
        string OwnerId,
        string ToolName,
        string ActionType,
        string Operation,
        string? EntityId,
        string PolicyVersion,
        ToolRiskClass RiskClass,
        string CurrentRequestHash,
        string CanonicalArguments);
}
