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
            VALUES ($id, $action_id, $owner, 'Pending', $created, $expires, NULL, 1);
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(approval.Id.Value));
        command.Parameters.AddWithValue("$action_id", SqliteValue.Guid(approval.ActionId.Value));
        command.Parameters.AddWithValue("$owner", approval.OwnerId);
        command.Parameters.AddWithValue("$created", SqliteValue.Utc(approval.CreatedAtUtc));
        command.Parameters.AddWithValue("$expires", SqliteValue.Utc(approval.ExpiresAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return approval;
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
    public async ValueTask<ApprovalStorageRecord> ResolveAsync(
        ApprovalId id,
        string ownerId,
        string status,
        long expectedVersion,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (status is not ("Approved" or "Rejected"))
        {
            throw new ArgumentException("An approval can only resolve to Approved or Rejected.", nameof(status));
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
              AND expires_at_utc > $resolved AND version = $version;
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
