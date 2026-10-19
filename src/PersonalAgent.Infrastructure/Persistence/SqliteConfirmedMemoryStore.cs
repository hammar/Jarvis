using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Application.Memory;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Owns local owner-filtered retrieval and atomic, approval-bound memory mutations.</summary>
public sealed partial class SqliteConfirmedMemoryStore(SqliteDatabase database, IClock clock) : IConfirmedMemoryStore
{
    private const string Columns = """
        f.id, f.subject, f.fact_key, f.value, f.source_id, f.privacy_class, f.version,
        f.owner_id, f.source_type, f.source_message_id, f.created_at_utc, f.updated_at_utc,
        f.validity_status, f.supersedes_id, f.distinguishing_context
        """;

    /// <inheritdoc />
    public async ValueTask<ConversationMessage?> ReadSourceAsync(
        string ownerId, TurnId turnId, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.message_id, m.conversation_id, m.content, m.created_at_utc
            FROM messages m JOIN conversations c ON c.id = m.conversation_id
            WHERE c.owner_id = $owner AND m.turn_id = $turn AND m.role = 'user' ORDER BY m.sequence LIMIT 1;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$turn", SqliteValue.Guid(turnId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(Guid.Parse(reader.GetString(0)), new ConversationId(Guid.Parse(reader.GetString(1))),
                "user", reader.GetString(2), SqliteValue.DateTimeOffset(reader.GetString(3))) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryFact>> ReadActiveAsync(
        string ownerId, int maximumFacts, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        if (maximumFacts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFacts));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, ownerId, """
            WHERE f.owner_id = $owner AND f.validity_status = 'Active'
              AND f.privacy_class IN ('LocalOnly', 'Private')
            ORDER BY f.updated_at_utc DESC, f.id LIMIT $limit
            """, cancellationToken, ("$limit", maximumFacts));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(
        string ownerId, string query, int limit, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 512 || limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Search requires at most 512 characters and 1-100 results.");
        }
        var terms = SearchTerms().Matches(query).Select(match => $"\"{match.Value}\"").ToArray();
        if (terms.Length == 0)
        {
            return [];
        }
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, ownerId, """
            JOIN memory_facts_fts ON f.rowid = memory_facts_fts.rowid
            WHERE f.owner_id = $owner AND f.validity_status = 'Active'
              AND f.privacy_class IN ('LocalOnly', 'Private') AND memory_facts_fts MATCH $query
            ORDER BY rank, f.updated_at_utc DESC, f.id LIMIT $limit
            """, cancellationToken, ("$query", string.Join(" AND ", terms)), ("$limit", limit));
    }

    /// <inheritdoc />
    public async ValueTask<MemoryFact?> GetAsync(string ownerId, MemoryFactId id, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await ReadAsync(connection, null, ownerId, """
            WHERE f.owner_id = $owner AND f.id = $id AND f.validity_status = 'Active'
              AND f.privacy_class IN ('LocalOnly', 'Private')
            """, cancellationToken, ("$id", SqliteValue.Guid(id.Value)));
        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryFact>> FindConflictsAsync(
        string ownerId, string subject, string key, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await FindConflictsAsync(connection, null, ownerId, subject, key, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<MemoryReceipt> CommitAsync(ConfirmedMemoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        MemoryPolicy.ValidateOwner(command.OwnerId);
        MemoryPolicy.Validate(command.Change);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        var existing = await ReadReceiptAsync(connection, transaction, command.OwnerId, command.ActionId, cancellationToken);
        await AuthorizeAsync(connection, transaction, command, existing is not null, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        var change = command.Change;
        var now = SqliteValue.Utc(clock.UtcNow);
        if (change.Operation != "delete")
        {
            var conflicts = await FindConflictsAsync(connection, transaction, command.OwnerId,
                change.Subject!, change.Key!, cancellationToken);
            MemoryPolicy.ValidateConflicts(change, conflicts);
        }
        if (change.Operation == "supersede")
        {
            foreach (var conflict in change.Conflicts)
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE memory_facts SET validity_status = 'Superseded', version = version + 1, updated_at_utc = $now
                    WHERE id = $id AND owner_id = $owner AND version = $expected AND validity_status = 'Active'
                      AND privacy_class IN ('LocalOnly', 'Private');
                    """, cancellationToken, ("$id", SqliteValue.Guid(conflict.Id.Value)),
                    ("$owner", command.OwnerId), ("$expected", conflict.Version), ("$now", now));
                await SqliteMemoryDependencies.InvalidateFactAsync(
                    connection, transaction, conflict.Id, command.ActionId, cancellationToken);
            }
        }
        var version = change.ExpectedVersion + 1;
        if (change.Operation == "delete")
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM memory_facts WHERE id = $id AND owner_id = $owner AND version = $expected
                    AND privacy_class IN ('LocalOnly', 'Private');
                """, cancellationToken, ("$id", SqliteValue.Guid(change.FactId.Value)),
                ("$owner", command.OwnerId), ("$expected", change.ExpectedVersion));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO memory_tombstones (fact_id, owner_id, version, deleted_at_utc)
                VALUES ($id, $owner, $version, $now);
                """, cancellationToken, ("$id", SqliteValue.Guid(change.FactId.Value)),
                ("$owner", command.OwnerId), ("$version", version), ("$now", now));
        }
        else if (change.Operation == "update")
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE memory_facts SET subject = $subject, fact_key = $key, value = $value,
                    source_id = $source, source_type = 'OwnerMessage', source_message_id = $source,
                    updated_at_utc = $now, version = version + 1, distinguishing_context = $context
                WHERE id = $id AND owner_id = $owner AND version = $expected AND validity_status = 'Active'
                    AND privacy_class IN ('LocalOnly', 'Private');
                """, cancellationToken, Values(command, now));
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO memory_facts (id, owner_id, subject, fact_key, value, source_id, source_type,
                    source_message_id, privacy_class, created_at_utc, updated_at_utc, version,
                    supersedes_id, distinguishing_context)
                SELECT $id, $owner, $subject, $key, $value, $source, 'OwnerMessage', $source,
                    'LocalOnly', $now, $now, 1, $supersedes, $context
                WHERE NOT EXISTS (SELECT 1 FROM memory_tombstones WHERE fact_id = $id)
                  AND NOT EXISTS (SELECT 1 FROM memory_facts WHERE id = $id);
                """, cancellationToken, Values(command, now));
        }
        await SqliteMemoryDependencies.InvalidateFactAsync(
            connection, transaction, change.FactId, command.ActionId, cancellationToken);
        // The current action's historical draft remains dependent on the versions produced by its own commit.
        await using (var dependencies = connection.CreateCommand())
        {
            dependencies.Transaction = transaction;
            dependencies.CommandText = """
                UPDATE memory_action_dependencies SET fact_version =
                    COALESCE((SELECT version FROM memory_facts f WHERE f.id = fact_id), fact_version)
                WHERE action_id = $action;
                """;
            dependencies.Parameters.AddWithValue("$action", SqliteValue.Guid(command.ActionId.Value));
            await dependencies.ExecuteNonQueryAsync(cancellationToken);
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO memory_commits (action_id, owner_id, fact_id, fact_version, deleted, committed_at_utc)
            VALUES ($action, $owner, $fact, $version, $deleted, $now);
            """, cancellationToken, ("$action", SqliteValue.Guid(command.ActionId.Value)), ("$owner", command.OwnerId),
            ("$fact", SqliteValue.Guid(change.FactId.Value)), ("$version", version),
            ("$deleted", change.Operation == "delete" ? 1 : 0), ("$now", now));
        await transaction.CommitAsync(cancellationToken);
        return new(command.ActionId, change.FactId, version, change.Operation == "delete");
    }

    /// <inheritdoc />
    public async ValueTask<MemoryReceipt?> GetReceiptAsync(
        string ownerId, ActionId actionId, CancellationToken cancellationToken)
    {
        MemoryPolicy.ValidateOwner(ownerId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadReceiptAsync(connection, null, ownerId, actionId, cancellationToken);
    }

    private async Task AuthorizeAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConfirmedMemoryCommand command,
        bool alreadyCommitted, CancellationToken token)
    {
        await using var authorization = connection.CreateCommand();
        authorization.Transaction = transaction;
        authorization.CommandText = """
            SELECT a.canonical_arguments, p.owner_id, p.status, p.expires_at_utc, a.status, a.memory_invalidated
            FROM actions a JOIN approval_requests p ON p.action_id = a.id WHERE a.id = $id;
            """;
        authorization.Parameters.AddWithValue("$id", SqliteValue.Guid(command.ActionId.Value));
        await using var reader = await authorization.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(1) != command.OwnerId
            || reader.GetString(2) != "Approved" || reader.GetBoolean(5)
            || !alreadyCommitted && (reader.GetString(4) != "Executing"
                || clock.UtcNow >= SqliteValue.DateTimeOffset(reader.GetString(3))))
        {
            throw new PersistenceConcurrencyException("Memory requires an exact current owner approval.");
        }
        using var envelope = JsonDocument.Parse(reader.GetString(0));
        var root = envelope.RootElement;
        var bound = JsonSerializer.Deserialize<ConfirmedMemoryCommand>(root.GetProperty("CanonicalArguments").GetString()!)
            ?? throw new InvalidDataException("Missing memory command.");
        if (root.GetProperty("OwnerId").GetString() != command.OwnerId
            || root.GetProperty("ToolName").GetString() != "memory.propose_change"
            || JsonSerializer.Serialize(bound) != JsonSerializer.Serialize(command))
        {
            throw new PersistenceConcurrencyException("The approved memory content changed.");
        }
        if (alreadyCommitted)
        {
            return;
        }
        await using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = """
            SELECT EXISTS (SELECT 1 FROM messages m JOIN conversations c ON c.id = m.conversation_id
                WHERE m.message_id = $source AND m.role = 'user' AND c.owner_id = $owner
                  AND m.turn_id = $turn);
            """;
        source.Parameters.AddWithValue("$source", SqliteValue.Guid(command.SourceMessageId));
        source.Parameters.AddWithValue("$owner", command.OwnerId);
        source.Parameters.AddWithValue("$turn", root.GetProperty("TurnId").GetString()!);
        if (Convert.ToInt32(await source.ExecuteScalarAsync(token)) == 0)
        {
            throw new PersistenceConcurrencyException("The original owner request is unavailable.");
        }
    }

    private static (string, object?)[] Values(ConfirmedMemoryCommand command, string now) =>
    [
        ("$id", SqliteValue.Guid(command.Change.FactId.Value)), ("$owner", command.OwnerId),
        ("$subject", command.Change.Subject), ("$key", command.Change.Key), ("$value", command.Change.Value),
        ("$source", SqliteValue.Guid(command.SourceMessageId)), ("$now", now),
        ("$expected", command.Change.ExpectedVersion), ("$context", command.Change.DistinguishingContext),
        ("$supersedes", command.Change.Operation == "supersede"
            ? SqliteValue.Guid(command.Change.Conflicts[0].Id.Value) : null)
    ];

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        if (await command.ExecuteNonQueryAsync(token) != 1)
        {
            throw new PersistenceConcurrencyException("The memory fact changed, disappeared, or was previously deleted.");
        }
    }

    internal static async Task<MemoryReceipt?> ReadReceiptAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string ownerId, ActionId id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fact_id, fact_version, deleted FROM memory_commits WHERE owner_id = $owner AND action_id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(id, new MemoryFactId(Guid.Parse(reader.GetString(0))), reader.GetInt64(1), reader.GetBoolean(2)) : null;
    }

    private static async Task<IReadOnlyList<MemoryFact>> FindConflictsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string ownerId,
        string subject, string key, CancellationToken token)
    {
        connection.CreateCollation("memory_identity", (left, right) =>
            string.CompareOrdinal(MemoryPolicy.Normalize(left), MemoryPolicy.Normalize(right)));
        var facts = await ReadAsync(connection, transaction, ownerId, """
            WHERE f.owner_id = $owner AND f.validity_status = 'Active'
                AND f.subject = $subject COLLATE memory_identity AND f.fact_key = $key COLLATE memory_identity
            ORDER BY f.id LIMIT 101
            """, token, ("$subject", subject), ("$key", key));
        if (facts.Count > 100 || facts.Any(fact => !MemoryPolicy.CanRead(fact.PrivacyClass, ProviderKind.Local)))
        {
            throw new PersistenceConcurrencyException("The memory conflict set cannot be reviewed under current policy.");
        }
        return facts;
    }

    private static async Task<IReadOnlyList<MemoryFact>> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string ownerId, string suffix,
        CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM memory_facts f {suffix};";
        command.Parameters.AddWithValue("$owner", ownerId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var rows = new List<MemoryFact>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new(new MemoryFactId(Guid.Parse(reader.GetString(0))), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6))
            {
                OwnerId = reader.GetString(7),
                SourceType = reader.GetString(8),
                SourceMessageId = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                SourceAvailable = !reader.IsDBNull(9),
                CreatedAtUtc = SqliteValue.DateTimeOffset(reader.GetString(10)),
                UpdatedAtUtc = SqliteValue.DateTimeOffset(reader.GetString(11)),
                ValidityStatus = reader.GetString(12),
                SupersedesId = reader.IsDBNull(13) ? null : new MemoryFactId(Guid.Parse(reader.GetString(13))),
                DistinguishingContext = reader.IsDBNull(14) ? null : reader.GetString(14)
            });
        }
        return rows;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTerms();
}
