using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Stores owner-confirmed memory facts and maintains their SQLite FTS5 index.</summary>
public sealed partial class SqliteMemoryStore : IMemoryStore
{
    private const int MaximumResults = 100;
    private readonly SqliteDatabase database;
    private readonly IClock clock;

    /// <summary>Creates a memory store over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    /// <param name="clock">UTC clock for fact timestamps.</param>
    public SqliteMemoryStore(SqliteDatabase database, IClock clock)
    {
        this.database = database;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"The result limit must be from 1 through {MaximumResults}.");
        }

        var terms = SearchTermRegex().Matches(query).Select(match => $"\"{match.Value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.subject, f.fact_key, f.value, f.source_id, f.privacy_class, f.version
            FROM memory_facts_fts
            JOIN memory_facts f ON f.rowid = memory_facts_fts.rowid
            WHERE memory_facts_fts MATCH $query AND f.validity_status = 'Active'
            ORDER BY rank, f.updated_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", string.Join(" AND ", terms));
        command.Parameters.AddWithValue("$limit", limit);

        var facts = new List<MemoryFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            facts.Add(ReadFact(reader));
        }

        return facts;
    }

    /// <inheritdoc />
    public async ValueTask<MemoryFact?> GetAsync(MemoryFactId id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, subject, fact_key, value, source_id, privacy_class, version
            FROM memory_facts
            WHERE id = $id AND validity_status = 'Active';
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFact(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<MemoryFact> SaveAsync(
        MemoryFact fact,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.PrivacyClass);
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        var now = SqliteValue.Utc(clock.UtcNow);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (expectedVersion == 0)
        {
            command.CommandText = """
                INSERT INTO memory_facts
                    (id, subject, fact_key, value, source_id, privacy_class, created_at_utc, updated_at_utc, version)
                VALUES ($id, $subject, $key, $value, $source, $privacy, $now, $now, 1)
                ON CONFLICT(id) DO NOTHING;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE memory_facts
                SET subject = $subject, fact_key = $key, value = $value, source_id = $source,
                    privacy_class = $privacy, updated_at_utc = $now, version = version + 1
                WHERE id = $id AND version = $expected AND validity_status = 'Active';
                """;
            command.Parameters.AddWithValue("$expected", expectedVersion);
        }

        command.Parameters.AddWithValue("$id", SqliteValue.Guid(fact.Id.Value));
        command.Parameters.AddWithValue("$subject", fact.Subject);
        command.Parameters.AddWithValue("$key", fact.Key);
        command.Parameters.AddWithValue("$value", fact.Value);
        command.Parameters.AddWithValue("$source", fact.SourceId);
        command.Parameters.AddWithValue("$privacy", fact.PrivacyClass);
        command.Parameters.AddWithValue("$now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException("The memory fact was created, changed, or deleted by another operation.");
        }

        return fact with { Version = expectedVersion + 1 };
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(
        MemoryFactId id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_facts WHERE id = $id AND version = $version AND validity_status = 'Active';";
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(id.Value));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException("The memory fact was changed or deleted by another operation.");
        }
    }

    private static MemoryFact ReadFact(SqliteDataReader reader) =>
        new(new MemoryFactId(System.Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}
