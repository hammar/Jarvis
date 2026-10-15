using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Application.Memory;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

// Dependency publication and invalidation run under the caller's immediate transaction.
internal static class SqliteMemoryDependencies
{
    internal static async Task RegisterAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActionId actionId,
        string ownerId, IReadOnlyList<MemoryReference> references, CancellationToken token)
    {
        foreach (var reference in references)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO memory_action_dependencies (action_id, fact_id, fact_version)
                VALUES ($action, $fact, $version)
                ON CONFLICT(action_id, fact_id) DO UPDATE SET fact_version = excluded.fact_version;
                """;
            command.Parameters.AddWithValue("$action", SqliteValue.Guid(actionId.Value));
            command.Parameters.AddWithValue("$fact", SqliteValue.Guid(reference.Id.Value));
            command.Parameters.AddWithValue("$version", reference.Version);
            await command.ExecuteNonQueryAsync(token);
        }
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM memory_action_dependencies d
                LEFT JOIN memory_facts f ON f.id = d.fact_id AND f.owner_id = $owner
                WHERE d.action_id = $action AND (
                    EXISTS (SELECT 1 FROM memory_tombstones t WHERE t.fact_id = d.fact_id)
                    OR (d.fact_version > 0 AND
                        (f.id IS NULL OR f.version <> d.fact_version))
                )
            );
            """;
        check.Parameters.AddWithValue("$action", SqliteValue.Guid(actionId.Value));
        check.Parameters.AddWithValue("$owner", ownerId);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(token)) != 0)
        {
            await InvalidateActionAsync(connection, transaction, actionId, token);
        }
    }

    internal static async Task InvalidateFactAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        MemoryFactId factId, ActionId exceptAction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT action_id FROM memory_action_dependencies WHERE fact_id = $fact AND action_id <> $except;
            """;
        command.Parameters.AddWithValue("$fact", SqliteValue.Guid(factId.Value));
        command.Parameters.AddWithValue("$except", SqliteValue.Guid(exceptAction.Value));
        var actions = new List<ActionId>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                actions.Add(new ActionId(Guid.Parse(reader.GetString(0))));
            }
        }
        foreach (var action in actions)
        {
            await InvalidateActionAsync(connection, transaction, action, token);
        }
    }

    private static async Task InvalidateActionAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActionId id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE actions SET memory_invalidated = 1, canonical_arguments = '{}',
                status = CASE WHEN status IN ('Prepared', 'AwaitingApproval') THEN 'Rejected'
                              WHEN status = 'Executing' THEN 'Unknown' ELSE status END,
                outcome_json = NULL, version = version + 1
            WHERE id = $id AND memory_invalidated = 0;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await command.ExecuteNonQueryAsync(token);
    }

    internal static IReadOnlyList<MemoryReference> ReadReferences(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value)
            ? value.Deserialize<MemoryReference[]>() ?? throw new InvalidDataException("Null memory dependencies.")
            : [];
    }

    internal static string? ReadOwner(ActionJournalEntry entry)
    {
        if (!entry.ActionType.StartsWith("memory.", StringComparison.Ordinal))
        {
            return null;
        }
        using var document = JsonDocument.Parse(entry.CanonicalArguments);
        var owner = document.RootElement.GetProperty("OwnerId").GetString()
            ?? throw new InvalidDataException("Missing memory action owner.");
        MemoryPolicy.ValidateOwner(owner);
        return owner;
    }
}
