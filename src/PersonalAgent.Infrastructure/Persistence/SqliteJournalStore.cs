using Microsoft.Data.Sqlite;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Describes a prepared or completed action recorded for audit and recovery.</summary>
/// <param name="Id">Stable action identifier.</param>
/// <param name="OwnerId">Authenticated owner associated with the action.</param>
/// <param name="ActionType">Registered action type.</param>
/// <param name="CanonicalArguments">Canonical serialized arguments, not model-authored instructions.</param>
/// <param name="RequestHash">Hash of the exact canonical request.</param>
/// <param name="Status">Storage state such as Prepared, Succeeded, or Unknown.</param>
/// <param name="CreatedAtUtc">UTC creation instant.</param>
/// <param name="Version">Positive compare-and-swap version.</param>
public sealed record SqliteActionRecord(
    ActionId Id,
    string OwnerId,
    string ActionType,
    string CanonicalArguments,
    string RequestHash,
    string Status,
    DateTimeOffset CreatedAtUtc,
    long Version);

/// <summary>Describes a durable approval request bound to one action.</summary>
/// <param name="Id">Stable approval identifier.</param>
/// <param name="ActionId">Action whose exact arguments are being considered.</param>
/// <param name="OwnerId">Owner allowed to make the decision.</param>
/// <param name="ExpiresAtUtc">UTC instant after which approval is no longer valid.</param>
/// <param name="CreatedAtUtc">UTC creation instant.</param>
/// <param name="Status">Storage state, initially Pending.</param>
/// <param name="Version">Positive compare-and-swap version.</param>
public sealed record SqliteApprovalRecord(
    ApprovalId Id,
    ActionId ActionId,
    string OwnerId,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    string Status,
    long Version);

/// <summary>Describes one privacy-filtered audit event.</summary>
/// <param name="Id">Stable event identifier.</param>
/// <param name="OwnerId">Owner associated with the event.</param>
/// <param name="EventType">Stable event type code.</param>
/// <param name="SubjectId">Optional related application identifier.</param>
/// <param name="DetailsJson">Structured details with secrets and raw prompts excluded by the caller.</param>
/// <param name="OccurredAtUtc">UTC observation instant.</param>
public sealed record SqliteAuditEvent(
    Guid Id,
    string OwnerId,
    string EventType,
    string? SubjectId,
    string DetailsJson,
    DateTimeOffset OccurredAtUtc);

/// <summary>Provides durable storage-only operations for action, approval, and audit journals.</summary>
public sealed class SqliteJournalStore(SqliteDatabase database)
{
    /// <summary>Creates an action journal row before external execution.</summary>
    /// <param name="action">Canonical action request and initial status.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    public async ValueTask SaveActionAsync(SqliteActionRecord action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Actions(
                Id, OwnerId, ActionType, CanonicalArguments, RequestHash, Status,
                CreatedAtUtc, UpdatedAtUtc, Version)
            VALUES ($id, $owner, $type, $arguments, $hash, $status, $created, $created, $version);
            """;
        command.Parameters.AddWithValue("$id", action.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$owner", action.OwnerId);
        command.Parameters.AddWithValue("$type", action.ActionType);
        command.Parameters.AddWithValue("$arguments", action.CanonicalArguments);
        command.Parameters.AddWithValue("$hash", action.RequestHash);
        command.Parameters.AddWithValue("$status", action.Status);
        command.Parameters.AddWithValue("$created", SqliteDatabase.FormatUtc(action.CreatedAtUtc));
        command.Parameters.AddWithValue("$version", action.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Compare-and-swap updates an action without deleting uncertain outcomes.</summary>
    /// <param name="id">Action to update.</param>
    /// <param name="expectedVersion">Version previously read by the caller.</param>
    /// <param name="status">Supported action state, including Unknown.</param>
    /// <param name="updatedAtUtc">UTC update instant.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    public async ValueTask UpdateActionAsync(
        ActionId id,
        long expectedVersion,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Actions SET Status = $status, UpdatedAtUtc = $updated, Version = Version + 1
            WHERE Id = $id AND Version = $version;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", SqliteDatabase.FormatUtc(updatedAtUtc));
        command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException($"action {id.Value:D}");
        }
    }

    /// <summary>Creates a pending approval row tied to an already persisted action.</summary>
    /// <param name="approval">Exact action, owner, expiry, and initial version.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    public async ValueTask SaveApprovalAsync(
        SqliteApprovalRecord approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApprovalRequests(
                Id, ActionId, OwnerId, ExpiresAtUtc, Status, CreatedAtUtc, Version)
            VALUES ($id, $actionId, $owner, $expires, $status, $created, $version);
            """;
        command.Parameters.AddWithValue("$id", approval.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$actionId", approval.ActionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$owner", approval.OwnerId);
        command.Parameters.AddWithValue("$expires", SqliteDatabase.FormatUtc(approval.ExpiresAtUtc));
        command.Parameters.AddWithValue("$status", approval.Status);
        command.Parameters.AddWithValue("$created", SqliteDatabase.FormatUtc(approval.CreatedAtUtc));
        command.Parameters.AddWithValue("$version", approval.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Transitions a pending approval with owner-scoped compare-and-swap semantics.</summary>
    /// <param name="id">Approval identifier.</param>
    /// <param name="ownerId">Authenticated owner whose approval row is being updated.</param>
    /// <param name="expectedVersion">Version previously read by the caller.</param>
    /// <param name="status">Decision state selected by the application use case.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    public async ValueTask UpdateApprovalAsync(
        ApprovalId id,
        string ownerId,
        long expectedVersion,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApprovalRequests SET Status = $status, Version = Version + 1
            WHERE Id = $id AND OwnerId = $owner AND Version = $version AND Status = 'Pending';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException($"approval {id.Value:D}");
        }
    }

    /// <summary>Appends a privacy-filtered audit record without updating or deleting prior events.</summary>
    /// <param name="auditEvent">Structured event data; callers must omit secrets and raw prompts.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    public async ValueTask AppendAuditEventAsync(
        SqliteAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AuditEvents(Id, OwnerId, EventType, SubjectId, DetailsJson, OccurredAtUtc)
            VALUES ($id, $owner, $type, $subject, $details, $occurredAt);
            """;
        command.Parameters.AddWithValue("$id", auditEvent.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", auditEvent.OwnerId);
        command.Parameters.AddWithValue("$type", auditEvent.EventType);
        command.Parameters.AddWithValue("$subject", (object?)auditEvent.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", auditEvent.DetailsJson);
        command.Parameters.AddWithValue("$occurredAt", SqliteDatabase.FormatUtc(auditEvent.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
