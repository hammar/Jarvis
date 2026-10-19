using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists approvals bound to one owner and one action, with single-use expiry checks.</summary>
public sealed class SqliteApprovalStore : IApprovalStore
{
    private readonly SqliteDatabase database;

    /// <summary>Creates an approval store over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    public SqliteApprovalStore(SqliteDatabase database) => this.database = database;

    /// <inheritdoc />
    public async ValueTask<ApprovalStorageRecord> CreateAsync(
        ApprovalStorageRecord approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.OwnerId);
        if (approval.Status != "Pending" || approval.ResolvedAtUtc is not null || approval.Version != 1)
        {
            throw new ArgumentException("A new approval must be unresolved, pending, and at version 1.", nameof(approval));
        }

        if (approval.ExpiresAtUtc <= approval.CreatedAtUtc)
        {
            throw new ArgumentException("Approval expiry must be after its creation time.", nameof(approval));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO approval_requests
                (id, action_id, owner_id, status, created_at_utc, expires_at_utc, resolved_at_utc, version)
            VALUES ($id, $action_id, $owner, 'Pending', $created, $expires, NULL, 1)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(approval.Id.Value));
        command.Parameters.AddWithValue("$action_id", SqliteValue.Guid(approval.ActionId.Value));
        command.Parameters.AddWithValue("$owner", approval.OwnerId);
        command.Parameters.AddWithValue("$created", SqliteValue.Utc(approval.CreatedAtUtc));
        command.Parameters.AddWithValue("$expires", SqliteValue.Utc(approval.ExpiresAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return approval;
        }

        var existing = await GetAsync(approval.Id, cancellationToken);
        if (existing is not null
            && existing.ActionId == approval.ActionId
            && string.Equals(existing.OwnerId, approval.OwnerId, StringComparison.Ordinal))
        {
            return existing;
        }

        throw new PersistenceConcurrencyException("The approval identifier is already bound to a different owner or action.");
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_id, owner_id, status, created_at_utc, expires_at_utc, resolved_at_utc, version
            FROM approval_requests WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ApprovalStorageRecord>> ReadRecentForOwnerAsync(
        string ownerId, int maximumRequests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (ownerId.Length > 128)
        {
            throw new ArgumentException("Owner identifiers cannot exceed 128 characters.", nameof(ownerId));
        }

        if (maximumRequests is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_id, owner_id, status, created_at_utc, expires_at_utc, resolved_at_utc, version
            FROM approval_requests
            WHERE owner_id = $owner
            ORDER BY CASE WHEN status IN ('Approved', 'Rejected', 'Expired') AND EXISTS (
                              SELECT 1 FROM actions
                              WHERE actions.id = approval_requests.action_id
                                AND actions.status IN ('AwaitingApproval', 'Executing')
                                AND actions.memory_invalidated = 0
                          )
                          THEN 0
                          WHEN status = 'Pending' AND EXISTS (
                              SELECT 1 FROM actions
                              WHERE actions.id = approval_requests.action_id
                                AND actions.status = 'AwaitingApproval'
                                AND actions.memory_invalidated = 0
                          )
                          THEN 1
                          WHEN EXISTS (
                              SELECT 1 FROM actions
                              WHERE actions.id = approval_requests.action_id
                                AND actions.status IN ('AwaitingApproval', 'Executing')
                                AND actions.memory_invalidated = 0
                          )
                          THEN 2
                          WHEN status = 'Pending' THEN 3 ELSE 4 END,
                     created_at_utc DESC, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$limit", maximumRequests);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<ApprovalStorageRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }
        return records;
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalStorageRecord> ResolveAsync(
        ApprovalId id,
        string ownerId,
        string status,
        long expectedVersion,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (status is not ("Approved" or "Rejected" or "Expired"))
        {
            throw new ArgumentException("An approval can only resolve to Approved, Rejected, or Expired.", nameof(status));
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE approval_requests
            SET status = $status, resolved_at_utc = $resolved, version = version + 1
            WHERE id = $id AND owner_id = $owner AND status = 'Pending'
              AND version = $version
              AND (($status IN ('Approved', 'Rejected') AND expires_at_utc > $resolved)
                   OR ($status = 'Expired' AND expires_at_utc <= $resolved));
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$resolved", SqliteValue.Utc(resolvedAtUtc));
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException(
                "The approval is expired, already resolved, owned by another identity, or has changed.");
        }

        return await GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("The resolved approval could not be read.");
    }

    private static ApprovalStorageRecord ReadRecord(SqliteDataReader reader)
    {
        DateTimeOffset? resolvedAt = reader.IsDBNull(6) ? null : SqliteValue.DateTimeOffset(reader.GetString(6));
        return new ApprovalStorageRecord(
            new ApprovalId(System.Guid.Parse(reader.GetString(0))),
            new ActionId(System.Guid.Parse(reader.GetString(1))),
            reader.GetString(2),
            reader.GetString(3),
            SqliteValue.DateTimeOffset(reader.GetString(4)),
            SqliteValue.DateTimeOffset(reader.GetString(5)),
            resolvedAt,
            reader.GetInt64(7));
    }
}
