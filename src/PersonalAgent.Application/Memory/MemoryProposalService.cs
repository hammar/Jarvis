using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Application.Tools.Memory;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Memory;

/// <summary>Provides an owner-reviewable exact change and its current conflict evidence.</summary>
/// <param name="Command">The immutable proposed content; modifying it requires a new action/approval.</param>
/// <param name="Conflicts">Current matching active local facts.</param>
/// <param name="Approval">Current owner-bound approval state.</param>
public sealed record MemoryProposalPreview(
    ConfirmedMemoryCommand Command, IReadOnlyList<MemoryFact> Conflicts, ApprovalRequest Approval);

/// <summary>Reports the durable conversation and exact action created for an owner-entered memory proposal.</summary>
/// <param name="ConversationId">The owner-scoped conversation retaining the source message.</param>
/// <param name="Result">The dispatcher outcome and exact action/approval identifiers.</param>
public sealed record MemoryProposalSubmission(ConversationId ConversationId, ToolDispatchResult Result);

/// <summary>Allows the trusted host to review/resolve memory proposals without a live model callback.</summary>
public sealed class MemoryProposalService(
    IConfirmedMemoryStore memories,
    IActionJournalStore journal,
    IApprovalService approvals,
    IToolDispatcher dispatcher,
    IConversationStore conversations,
    IClock clock)
{
    /// <summary>Reads the current bounded conflict set for an owner-entered proposal preview.</summary>
    /// <param name="ownerId">Authenticated local owner identity.</param>
    /// <param name="subject">Proposed fact subject, up to 256 characters.</param>
    /// <param name="key">Proposed fact key, up to 128 characters.</param>
    /// <param name="excludeId">Optional current target to omit from an edit's conflict set.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The exact current active conflicts, bounded by memory policy.</returns>
    public async ValueTask<IReadOnlyList<MemoryFact>> ReadConflictsAsync(
        string ownerId, string subject, string key, MemoryFactId? excludeId, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (subject.Length > 256 || key.Length > 128)
        {
            throw new ArgumentException("Memory conflict search exceeds the supported size.");
        }
        var conflicts = await memories.FindConflictsAsync(ownerId, subject, key, cancellationToken);
        return excludeId is null ? conflicts : conflicts.Where(fact => fact.Id != excludeId.Value).ToArray();
    }

    /// <summary>Creates an exact, owner-attributed approval proposal without invoking model inference.</summary>
    /// <param name="ownerId">Authenticated local owner identity, never a request-body claim.</param>
    /// <param name="conversationId">Owner conversation to retain the durable source message.</param>
    /// <param name="requestId">Stable client request identity, reused for safe exact replay.</param>
    /// <param name="change">Validated typed mutation and exact reviewed versions.</param>
    /// <param name="cancellationToken">Token that cancels before durable proposal execution.</param>
    /// <returns>The conversation and dispatch outcome, normally awaiting exact approval.</returns>
    public async ValueTask<MemoryProposalSubmission> ProposeAsync(
        string ownerId,
        ConversationId conversationId,
        Guid requestId,
        MemoryChange change,
        CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A stable proposal request ID is required.", nameof(requestId));
        }
        MemoryPolicy.Validate(change);

        if (await conversations.GetConversationAsync(conversationId, ownerId, cancellationToken) is null)
        {
            throw new KeyNotFoundException("The conversation is unavailable.");
        }

        var turnId = TurnId.New();
        var sourceText = SourceText(change);
        var requestKey = requestId.ToString("N");
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText))).ToLowerInvariant();
        var submission = await conversations.FindSubmittedTurnAsync(
            conversationId, requestKey, requestFingerprint, cancellationToken, ownerId);
        if (submission is null)
        {
            if (change.Operation is "update" or "delete")
            {
                var target = await memories.GetAsync(ownerId, change.FactId, cancellationToken);
                if (target is null || target.Version != change.ExpectedVersion)
                {
                    throw new PersistenceConcurrencyException("The memory fact changed or is no longer available.");
                }
            }

            if (change.Operation != "delete")
            {
                var conflicts = await memories.FindConflictsAsync(
                    ownerId, change.Subject!, change.Key!, cancellationToken);
                MemoryPolicy.ValidateConflicts(change, conflicts);
            }

            var createdAt = clock.UtcNow;
            submission = await conversations.SubmitTurnAsync(new(
                turnId,
                conversationId,
                requestKey,
                requestFingerprint,
                Guid.NewGuid(),
                sourceText,
                createdAt,
                ownerId), cancellationToken);
        }
        try
        {
            var result = await dispatcher.DispatchAsync(new(
                submission.Turn.Id,
                requestId.ToString("N"),
                "memory.propose_change",
                JsonSerializer.Serialize(MemoryToolRegistrations.Arguments(change)),
                ownerId,
                sourceText), cancellationToken);
            if (!submission.IsDuplicate)
            {
                var turn = await conversations.GetTurnAsync(submission.Turn.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted memory proposal turn is unavailable.");
                await conversations.UpdateTurnStatusAsync(
                    submission.Turn.Id,
                    result.Status is "Failed" or "Rejected" ? TurnStatus.Failed : TurnStatus.Completed,
                    turn.Version,
                    clock.UtcNow,
                    cancellationToken);
            }
            return new(conversationId, result);
        }
        finally
        {
            dispatcher.ReleaseTurn(submission.Turn.Id);
        }
    }

    /// <summary>Reads a pending exact proposal only for its authenticated owner.</summary>
    public async ValueTask<MemoryProposalPreview> PreviewAsync(
        string ownerId, ActionId actionId, CancellationToken cancellationToken)
    {
        var (_, command) = await ReadAsync(ownerId, actionId, cancellationToken);
        var conflicts = command.Change.Operation == "delete" ? Array.Empty<MemoryFact>()
            : await memories.FindConflictsAsync(ownerId, command.Change.Subject!, command.Change.Key!, cancellationToken);
        var approval = await approvals.GetAsync(new(actionId.Value), cancellationToken)
            ?? throw new KeyNotFoundException("The memory approval is unavailable.");
        return new(command, conflicts, approval);
    }

    /// <summary>Records an exact Approve/Reject decision and completes approved work from durable host provenance.</summary>
    public async ValueTask<ToolDispatchResult> ResolveAsync(
        string ownerId, ActionId actionId, long expectedApprovalVersion, bool approved, CancellationToken cancellationToken)
    {
        await ReadAsync(ownerId, actionId, cancellationToken);
        var resolution = await approvals.ResolveAsync(new(actionId.Value), ownerId, expectedApprovalVersion, approved, cancellationToken);
        if (resolution.Status != "Approved")
        {
            return new(resolution.Status, null, $"approval_{resolution.Status.ToLowerInvariant()}", actionId, new(actionId.Value));
        }
        return await CompleteAsync(ownerId, actionId, cancellationToken);
    }

    /// <summary>Completes an already-approved durable change after restart, without asking a model to recreate it.</summary>
    public async ValueTask<ToolDispatchResult> CompleteAsync(
        string ownerId, ActionId actionId, CancellationToken cancellationToken)
    {
        var (entry, command) = await ReadAsync(ownerId, actionId, cancellationToken);
        using var envelope = JsonDocument.Parse(entry.CanonicalArguments);
        var turnId = new TurnId(Guid.Parse(envelope.RootElement.GetProperty("TurnId").GetString()!));
        var source = await memories.ReadSourceAsync(ownerId, turnId, cancellationToken);
        if (source is null && entry.Status is not ("Executing" or "Succeeded" or "Failed" or "Rejected" or "Expired" or "Unknown"))
        {
            throw new PersistenceConcurrencyException("The original request is no longer available.");
        }
        return await dispatcher.DispatchAsync(new(turnId,
            envelope.RootElement.GetProperty("CallId").GetString()!, "memory.propose_change",
            JsonSerializer.Serialize(MemoryToolRegistrations.Arguments(command.Change)), ownerId, source?.Content)
        {
            ReplayCurrentRequestHash = source is null
                ? envelope.RootElement.GetProperty("CurrentRequestHash").GetString() : null
        }, cancellationToken);
    }

    private async ValueTask<(ActionJournalEntry, ConfirmedMemoryCommand)> ReadAsync(
        string ownerId, ActionId actionId, CancellationToken token)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        var entry = await journal.GetAsync(actionId, token);
        if (entry is null || entry.MemoryOwnerId != ownerId || entry.ActionType != "memory.propose_change")
        {
            throw new KeyNotFoundException("The memory proposal is unavailable.");
        }
        if (entry.MemoryInvalidated)
        {
            throw new PersistenceConcurrencyException("The memory proposal was invalidated.");
        }
        using var envelope = JsonDocument.Parse(entry.CanonicalArguments);
        var command = JsonSerializer.Deserialize<ConfirmedMemoryCommand>(
            envelope.RootElement.GetProperty("CanonicalArguments").GetString()!)
            ?? throw new InvalidDataException("Missing memory proposal.");
        return (entry, command);
    }

    private static string SourceText(MemoryChange change) => JsonSerializer.Serialize(new
    {
        kind = "owner-entered-memory-proposal",
        change.Operation,
        factId = change.FactId.Value,
        change.ExpectedVersion,
        change.Subject,
        change.Key,
        change.Value,
        conflicts = change.Conflicts.Select(conflict => new
        {
            id = conflict.Id.Value,
            conflict.Version
        }),
        change.DistinguishingContext
    });
}
