using System.Text.Json;
using PersonalAgent.Application.Tools.Memory;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Memory;

/// <summary>Provides an owner-reviewable exact change and its current conflict evidence.</summary>
/// <param name="Command">The immutable proposed content; modifying it requires a new action/approval.</param>
/// <param name="Conflicts">Current matching active local facts.</param>
/// <param name="Approval">Current owner-bound approval state.</param>
public sealed record MemoryProposalPreview(
    ConfirmedMemoryCommand Command, IReadOnlyList<MemoryFact> Conflicts, ApprovalRequest Approval);

/// <summary>Allows the trusted host to review/resolve memory proposals without a live model callback.</summary>
public sealed class MemoryProposalService(
    IConfirmedMemoryStore memories, IActionJournalStore journal, IApprovalService approvals, IToolDispatcher dispatcher)
{
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
}
