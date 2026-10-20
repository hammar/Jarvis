using PersonalAgent.Domain;

namespace PersonalAgent.Application.Approvals;

/// <summary>Creates and resolves short-lived, single-use approvals for exact journaled actions.</summary>
public sealed class ApprovalService : IApprovalService
{
    private static readonly TimeSpan DefaultApprovalLifetime = TimeSpan.FromMinutes(5);
    private readonly IApprovalStore approvals;
    private readonly IActionJournalStore actions;
    private readonly IClock clock;

    /// <summary>Creates an approval service over durable stores and the host clock.</summary>
    /// <param name="approvals">Durable owner/action-bound approval store.</param>
    /// <param name="actions">Durable action journal.</param>
    /// <param name="clock">Host-controlled UTC clock.</param>
    public ApprovalService(IApprovalStore approvals, IActionJournalStore actions, IClock clock)
    {
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest> CreateAsync(
        ActionId actionId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ValidateOwner(ownerId);
        var action = await actions.GetAsync(actionId, cancellationToken)
            ?? throw new KeyNotFoundException("The approval action does not exist.");
        var approvalId = new ApprovalId(actionId.Value);
        var existing = await approvals.GetAsync(approvalId, cancellationToken);
        if (existing is not null)
        {
            EnsureSameBinding(existing, actionId, ownerId);
            return ToRequest(existing);
        }

        if (!string.Equals(action.Status, "AwaitingApproval", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only an action awaiting approval can receive an approval request.");
        }

        var createdAtUtc = clock.UtcNow.ToUniversalTime();
        var record = new ApprovalStorageRecord(
            approvalId,
            actionId,
            ownerId,
            "Pending",
            createdAtUtc,
            createdAtUtc.Add(DefaultApprovalLifetime),
            null,
            1);
        var persisted = await approvals.CreateAsync(record, cancellationToken);
        EnsureSameBinding(persisted, actionId, ownerId);
        return ToRequest(persisted);
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest?> GetAsync(
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var record = await approvals.GetAsync(approvalId, cancellationToken);
        return record is null ? null : ToRequest(record);
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest> ResolveAsync(
        ApprovalId approvalId,
        string ownerId,
        long expectedVersion,
        bool approved,
        CancellationToken cancellationToken)
    {
        ValidateOwner(ownerId);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        var current = await approvals.GetAsync(approvalId, cancellationToken)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)
            || current.Version != expectedVersion
            || current.Status != "Pending")
        {
            throw new PersistenceConcurrencyException(
                "The approval is not pending for this owner or its version has changed.");
        }

        var action = await actions.GetAsync(current.ActionId, cancellationToken)
            ?? throw new PersistenceConcurrencyException("The approved action no longer exists.");
        if (action.Status != "AwaitingApproval")
        {
            throw new PersistenceConcurrencyException("The action is no longer awaiting approval.");
        }

        var nowUtc = clock.UtcNow.ToUniversalTime();
        if (nowUtc >= current.ExpiresAtUtc)
        {
            var expired = await approvals.ResolveAsync(
                approvalId,
                ownerId,
                "Expired",
                expectedVersion,
                nowUtc,
                cancellationToken);
            await SettleActionAsync(action, "Expired", nowUtc, cancellationToken);
            return ToRequest(expired);
        }

        var status = approved ? "Approved" : "Rejected";
        var resolved = await approvals.ResolveAsync(
            approvalId,
            ownerId,
            status,
            expectedVersion,
            nowUtc,
            cancellationToken);
        if (!approved)
        {
            await SettleActionAsync(action, "Rejected", nowUtc, cancellationToken);
        }

        return ToRequest(resolved);
    }

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry> SettleExpiredAsync(
        ApprovalId approvalId,
        string ownerId,
        long expectedVersion,
        CancellationToken cancellationToken)
        => await SettleResolvedActionAsync(
            approvalId, ownerId, expectedVersion, "Expired", "Expired", "approval_expired", cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry> SettleRejectedAsync(
        ApprovalId approvalId,
        string ownerId,
        long expectedVersion,
        CancellationToken cancellationToken)
        => await SettleResolvedActionAsync(
            approvalId, ownerId, expectedVersion, "Rejected", "Rejected", "approval_rejected", cancellationToken);

    private async ValueTask<ActionJournalEntry> SettleResolvedActionAsync(
        ApprovalId approvalId,
        string ownerId,
        long expectedVersion,
        string approvalStatus,
        string actionStatus,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ValidateOwner(ownerId);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        var approval = await approvals.GetAsync(approvalId, cancellationToken)
            ?? throw new KeyNotFoundException("The approval request does not exist.");
        if (!string.Equals(approval.OwnerId, ownerId, StringComparison.Ordinal)
            || approval.Version != expectedVersion
            || approval.Status != approvalStatus)
        {
            throw new PersistenceConcurrencyException($"The approval is not the expected {approvalStatus} version.");
        }

        var action = await actions.GetAsync(approval.ActionId, cancellationToken)
            ?? throw new PersistenceConcurrencyException("The resolved action no longer exists.");
        if (action.Status != "AwaitingApproval")
        {
            return action;
        }

        var settledAtUtc = approval.ResolvedAtUtc ?? approval.ExpiresAtUtc;
        try
        {
            return await actions.UpdateStatusAsync(
                action.Id,
                actionStatus,
                action.Version,
                settledAtUtc,
                cancellationToken,
                new(actionStatus, null, reasonCode, action.Id, approvalId));
        }
        catch (PersistenceConcurrencyException)
        {
            var current = await actions.GetAsync(action.Id, cancellationToken);
            if (current is null || current.Status == "AwaitingApproval")
            {
                throw;
            }

            return current;
        }
    }

    private async ValueTask SettleActionAsync(
        ActionJournalEntry action,
        string status,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await actions.UpdateStatusAsync(action.Id, status, action.Version, nowUtc, cancellationToken,
                new(status, null, $"approval_{status.ToLowerInvariant()}", action.Id, new ApprovalId(action.Id.Value)));
        }
        catch (PersistenceConcurrencyException)
        {
            var current = await actions.GetAsync(action.Id, cancellationToken);
            if (current?.Status != status)
            {
                throw;
            }
        }
    }

    private static ApprovalRequest ToRequest(ApprovalStorageRecord record) =>
        new(
            record.Id,
            record.ActionId,
            record.OwnerId,
            record.ExpiresAtUtc,
            record.Version,
            record.Status);

    private static void EnsureSameBinding(ApprovalStorageRecord record, ActionId actionId, string ownerId)
    {
        if (record.ActionId != actionId || !string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal))
        {
            throw new PersistenceConcurrencyException("The approval identifier is bound to a different owner or action.");
        }
    }

    private static void ValidateOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128)
        {
            throw new ArgumentException("An authenticated owner identifier of at most 128 characters is required.", nameof(ownerId));
        }
    }
}
