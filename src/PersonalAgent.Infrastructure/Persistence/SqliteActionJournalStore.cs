using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists canonical action requests and compare-and-swapped outcomes.</summary>
public sealed class SqliteActionJournalStore : IActionJournalStore
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Prepared", "AwaitingApproval", "Executing", "Succeeded", "Failed", "Unknown", "Rejected", "Expired"
    };

    private readonly SqliteDatabase database;

    /// <summary>Creates an action journal over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    public SqliteActionJournalStore(SqliteDatabase database) => this.database = database;

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry> CreateAsync(
        ActionJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ActionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RequestHash);
        ValidateStatus(entry.Status);
        if (entry.Status != "Prepared" || entry.Version != 1)
        {
            throw new ArgumentException("A new action journal entry must be Prepared at version 1.", nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions
                (id, action_type, canonical_arguments, request_hash, status, created_at_utc, updated_at_utc, version)
            VALUES ($id, $type, $arguments, $hash, $status, $created, $updated, 1)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(entry.Id.Value));
        command.Parameters.AddWithValue("$type", entry.ActionType);
        command.Parameters.AddWithValue("$arguments", entry.CanonicalArguments);
        command.Parameters.AddWithValue("$hash", entry.RequestHash);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$created", SqliteValue.Utc(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SqliteValue.Utc(entry.UpdatedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return entry;
        }

        var existing = await GetAsync(entry.Id, cancellationToken);
        if (existing is not null
            && string.Equals(existing.RequestHash, entry.RequestHash, StringComparison.Ordinal)
            && string.Equals(existing.ActionType, entry.ActionType, StringComparison.Ordinal)
            && string.Equals(existing.CanonicalArguments, entry.CanonicalArguments, StringComparison.Ordinal))
        {
            return existing;
        }

        throw new InvalidOperationException("The action identifier is already bound to a different canonical request.");
    }

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_type, canonical_arguments, request_hash, status,
                   created_at_utc, updated_at_utc, version
            FROM actions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry> UpdateStatusAsync(
        ActionId id,
        string status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateStatus(status);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE actions SET status = $status, updated_at_utc = $updated, version = version + 1
            WHERE id = $id AND version = $version;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException("The action journal entry changed or no longer exists.");
        }

        ActionJournalEntry result;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, action_type, canonical_arguments, request_hash, status,
                       created_at_utc, updated_at_utc, version
                FROM actions WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The updated action journal entry could not be read.");
            }

            result = ReadEntry(reader);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static ActionJournalEntry ReadEntry(SqliteDataReader reader) =>
        new(new ActionId(System.Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            SqliteValue.DateTimeOffset(reader.GetString(5)),
            SqliteValue.DateTimeOffset(reader.GetString(6)),
            reader.GetInt64(7));

    private static void ValidateStatus(string status)
    {
        if (!AllowedStatuses.Contains(status))
        {
            throw new ArgumentException("Action status is not one of the persisted journal states.", nameof(status));
        }
    }
}
