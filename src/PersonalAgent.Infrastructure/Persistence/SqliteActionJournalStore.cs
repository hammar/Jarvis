using Microsoft.Data.Sqlite;
using System.Text.Json;
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
        if (entry.Status != "Prepared" || entry.Version != 1 || entry.Outcome is not null)
        {
            throw new ArgumentException("A new action journal entry must be Prepared at version 1.", nameof(entry));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var ownership = ReadActionOwnership(entry.CanonicalArguments);
        command.CommandText = """
            INSERT INTO actions
                (id, action_type, canonical_arguments, request_hash, status, created_at_utc, updated_at_utc, version,
                 memory_owner_id, action_owner_id, action_turn_id)
            VALUES ($id, $type, $arguments, $hash, $status, $created, $updated, 1,
                 $memoryOwner, $actionOwner, $actionTurn)
            ON CONFLICT(id) DO NOTHING;
            """;
        var memoryOwner = SqliteMemoryDependencies.ReadOwner(entry);
        command.Parameters.AddWithValue("$memoryOwner", (object?)memoryOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("$actionOwner", (object?)ownership?.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actionTurn", (object?)ownership?.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(entry.Id.Value));
        command.Parameters.AddWithValue("$type", entry.ActionType);
        command.Parameters.AddWithValue("$arguments", entry.CanonicalArguments);
        command.Parameters.AddWithValue("$hash", entry.RequestHash);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$created", SqliteValue.Utc(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SqliteValue.Utc(entry.UpdatedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            if (memoryOwner is not null)
            {
                await SqliteMemoryDependencies.RegisterAsync(connection, transaction, entry.Id, memoryOwner,
                    SqliteMemoryDependencies.ReadReferences(entry.CanonicalArguments, "MemoryReferences"), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(entry.Id, cancellationToken)
                ?? throw new InvalidDataException("The created action disappeared.");
        }

        await transaction.CommitAsync(cancellationToken);
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
                   created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
            FROM actions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(
        string ownerId, int maximumActions, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (ownerId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        }
        if (maximumActions is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActions));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_type, canonical_arguments, request_hash, status,
                   created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
            FROM actions
            WHERE action_owner_id = $owner OR memory_owner_id = $owner
            ORDER BY CASE WHEN status IN ('Prepared', 'AwaitingApproval', 'Executing')
                          THEN 0
                          WHEN status = 'Unknown' THEN 1 ELSE 2 END,
                     updated_at_utc DESC, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$limit", maximumActions);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<ActionJournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader));
        }
        return entries;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ActionJournalEntry>> ReadForTurnAsync(
        string ownerId, TurnId turnId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (ownerId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var integrityCheck = connection.CreateCommand())
        {
            integrityCheck.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM actions
                    WHERE status IN ('Unknown', 'Executing')
                      AND memory_invalidated = 0
                      AND (action_owner_id IS NULL OR action_turn_id IS NULL)
                );
                """;
            if (Convert.ToInt32(await integrityCheck.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                throw new InvalidDataException(
                    "An unresolved action has no durable owner and turn attribution; refusing action dispatch.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action_type, canonical_arguments, request_hash, status,
                   created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
            FROM actions
            WHERE action_owner_id = $owner AND action_turn_id = $turn
            ORDER BY created_at_utc, id
            LIMIT 9;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$turn", turnId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<ActionJournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader));
        }
        if (entries.Count > 8)
        {
            throw new InvalidDataException("The action journal exceeds the configured per-turn tool-call limit.");
        }

        return entries;
    }

    private static ActionOwnership? ReadActionOwnership(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("OwnerId", out var ownerElement)
                || ownerElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("TurnId", out var turnElement)
                || turnElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var owner = ownerElement.GetString();
            var turn = turnElement.GetString();
            if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128
                || !Guid.TryParseExact(turn, "D", out var turnId))
            {
                return null;
            }

            return new(owner, turnId.ToString("D"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ActionOwnership(string OwnerId, string TurnId);

    /// <inheritdoc />
    public async ValueTask<ActionJournalEntry> UpdateStatusAsync(
        ActionId id,
        string status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken,
        ToolDispatchResult? outcome = null)
    {
        ValidateStatus(status);
        ValidateOutcome(id, status, outcome);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var currentCommand = connection.CreateCommand())
        {
            currentCommand.Transaction = transaction;
            currentCommand.CommandText = """
                SELECT id, action_type, canonical_arguments, request_hash, status,
                       created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
                FROM actions WHERE id = $id;
                """;
            currentCommand.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
            ActionJournalEntry? current;
            await using (var reader = await currentCommand.ExecuteReaderAsync(cancellationToken))
            {
                current = await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
            }
            if (current is { MemoryInvalidated: true })
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            if (current is { ActionType: "memory.propose_change", Status: "Executing", MemoryOwnerId: { } receiptOwner }
                && status is "Succeeded" or "Failed" or "Unknown")
            {
                // Serialize settlement with mutation authorization: a stale missing-receipt
                // observation cannot report failure after the fact transaction commits.
                var receipt = await SqliteConfirmedMemoryStore.ReadReceiptAsync(
                    connection, transaction, receiptOwner, id, cancellationToken);
                if (receipt is not null)
                {
                    status = "Succeeded";
                    outcome = new(status, JsonSerializer.Serialize(receipt), null, id);
                }
            }
            if (current?.MemoryOwnerId is { } owner && outcome?.ResultJson is { } resultJson)
            {
                var references = SqliteMemoryDependencies.ReadReferences(resultJson, "MemoryReferences");
                if (references.Count > 0)
                {
                    await SqliteMemoryDependencies.RegisterAsync(connection, transaction, id, owner, references, cancellationToken);
                }
            }
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE actions SET status = $status, updated_at_utc = $updated,
                outcome_json = $outcome, version = version + 1
            WHERE id = $id AND version = $version AND memory_invalidated = 0;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        command.Parameters.AddWithValue("$version", expectedVersion);
        command.Parameters.AddWithValue("$outcome",
            outcome is null ? DBNull.Value : JsonSerializer.Serialize(outcome));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await using var invalidated = connection.CreateCommand();
            invalidated.Transaction = transaction;
            invalidated.CommandText = """
                SELECT id, action_type, canonical_arguments, request_hash, status,
                       created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
                FROM actions WHERE id = $id AND memory_invalidated = 1;
                """;
            invalidated.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
            ActionJournalEntry? winner;
            await using (var reader = await invalidated.ExecuteReaderAsync(cancellationToken))
            {
                winner = await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
            }
            if (winner is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return winner;
            }
            throw new PersistenceConcurrencyException("The action journal entry changed or no longer exists.");
        }

        ActionJournalEntry result;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, action_type, canonical_arguments, request_hash, status,
                       created_at_utc, updated_at_utc, version, outcome_json, memory_invalidated, memory_owner_id
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

    private static ActionJournalEntry ReadEntry(SqliteDataReader reader)
    {
        var entry = new ActionJournalEntry(new ActionId(System.Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            SqliteValue.DateTimeOffset(reader.GetString(5)),
            SqliteValue.DateTimeOffset(reader.GetString(6)),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<ToolDispatchResult>(reader.GetString(8))
                ?? throw new InvalidDataException("The persisted action response is null."))
        {
            MemoryInvalidated = reader.GetBoolean(9),
            MemoryOwnerId = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
        if (entry.MemoryInvalidated)
        {
            entry = entry with { Outcome = new(entry.Status, null, "memory_result_invalidated", entry.Id) };
        }
        ValidateOutcome(entry.Id, entry.Status, entry.Outcome);
        return entry;
    }

    private static void ValidateOutcome(ActionId id, string status, ToolDispatchResult? outcome)
    {
        if (outcome is not null
            && (status is not ("Succeeded" or "Failed" or "Unknown" or "Rejected" or "Expired")
                || outcome.Status != status || outcome.ActionId != id
                || outcome.ResultJson is { Length: > 1_000_000 }
                || outcome.ReasonCode is { Length: > 256 }))
        {
            throw new ArgumentException("An action response must be bounded, terminal, and match its action identity/status.",
                nameof(outcome));
        }
    }

    private static void ValidateStatus(string status)
    {
        if (!AllowedStatuses.Contains(status))
        {
            throw new ArgumentException("Action status is not one of the persisted journal states.", nameof(status));
        }
    }
}
