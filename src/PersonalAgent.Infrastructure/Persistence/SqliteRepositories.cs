using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Signals that a persisted value changed after its expected version was read.</summary>
public sealed class PersistenceConcurrencyException : InvalidOperationException
{
    /// <summary>Creates a concurrency error for one persisted entity.</summary>
    /// <param name="entity">Non-sensitive entity type and identifier.</param>
    public PersistenceConcurrencyException(string entity)
        : base($"The persisted {entity} was missing or changed before the update committed.")
    {
    }
}

/// <summary>Persists application-owned conversation messages in chronological order.</summary>
public sealed class SqliteConversationStore(SqliteDatabase database) : IConversationStore
{
    /// <inheritdoc />
    public async ValueTask<ConversationMessage> AppendMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = (SqliteTransaction)transaction;
            conversation.CommandText = """
                INSERT INTO Conversations(Id, CreatedAtUtc) VALUES ($id, $created)
                ON CONFLICT(Id) DO NOTHING;
                """;
            conversation.Parameters.AddWithValue("$id", message.ConversationId.Value.ToString("D"));
            conversation.Parameters.AddWithValue("$created", SqliteDatabase.FormatUtc(message.CreatedAtUtc));
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Messages(MessageId, ConversationId, Role, Content, CreatedAtUtc)
                VALUES ($messageId, $conversationId, $role, $content, $created);
                """;
            command.Parameters.AddWithValue("$messageId", message.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", message.ConversationId.Value.ToString("D"));
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$created", SqliteDatabase.FormatUtc(message.CreatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return message;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConversationMessage>> ReadRecentAsync(
        ConversationId conversationId,
        int maximumMessages,
        CancellationToken cancellationToken)
    {
        if (maximumMessages is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessages), "Message limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MessageId, ConversationId, Role, Content, CreatedAtUtc
            FROM (
                SELECT rowid AS MessageRowId, MessageId, ConversationId, Role, Content, CreatedAtUtc
                FROM Messages
                WHERE ConversationId = $conversationId
                ORDER BY CreatedAtUtc DESC, rowid DESC
                LIMIT $maximum
            )
            ORDER BY CreatedAtUtc, MessageRowId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$maximum", maximumMessages);
        var messages = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ConversationMessage(
                Guid.Parse(reader.GetString(0)),
                new ConversationId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                reader.GetString(3),
                SqliteDatabase.ParseUtc(reader.GetString(4))));
        }

        return messages;
    }
}

/// <summary>Stores confirmed facts with FTS5 search and compare-and-swap updates.</summary>
public sealed class SqliteMemoryStore(SqliteDatabase database, IClock clock) : IMemoryStore
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be from 1 through 100.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Id, f.Subject, f.FactKey, f.Value, f.SourceId, f.PrivacyClass, f.Version
            FROM MemoryFactsSearch s
            JOIN MemoryFacts f ON f.rowid = s.rowid
            WHERE MemoryFactsSearch MATCH $query AND f.ValidityStatus = 'Active'
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", QuoteFtsQuery(query));
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
            SELECT Id, Subject, FactKey, Value, SourceId, PrivacyClass, Version
            FROM MemoryFacts WHERE Id = $id AND ValidityStatus = 'Active';
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
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
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        var now = SqliteDatabase.FormatUtc(clock.UtcNow);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        if (expectedVersion == 0)
        {
            command.CommandText = """
                INSERT INTO MemoryFacts(
                    Id, Subject, FactKey, Value, SourceId, PrivacyClass, CreatedAtUtc, UpdatedAtUtc, Version)
                VALUES ($id, $subject, $key, $value, $source, $privacy, $now, $now, 1);
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE MemoryFacts
                SET Subject = $subject, FactKey = $key, Value = $value, SourceId = $source,
                    PrivacyClass = $privacy, UpdatedAtUtc = $now, Version = Version + 1
                WHERE Id = $id AND Version = $expectedVersion AND ValidityStatus = 'Active';
                """;
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        }

        AddFactParameters(command, fact, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException($"memory fact {fact.Id.Value:D}");
        }

        await transaction.CommitAsync(cancellationToken);
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
        command.CommandText = """
            DELETE FROM MemoryFacts
            WHERE Id = $id AND Version = $expectedVersion AND ValidityStatus = 'Active';
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException($"memory fact {id.Value:D}");
        }
    }

    private static string QuoteFtsQuery(string query) => $"\"{query.Trim().Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static MemoryFact ReadFact(SqliteDataReader reader) =>
        new(
            new MemoryFactId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));

    private static void AddFactParameters(SqliteCommand command, MemoryFact fact, string now)
    {
        command.Parameters.AddWithValue("$id", fact.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$subject", fact.Subject);
        command.Parameters.AddWithValue("$key", fact.Key);
        command.Parameters.AddWithValue("$value", fact.Value);
        command.Parameters.AddWithValue("$source", fact.SourceId);
        command.Parameters.AddWithValue("$privacy", fact.PrivacyClass);
        command.Parameters.AddWithValue("$now", now);
    }
}

/// <summary>Describes one owner-authorized scheduled job to be stored for later processing.</summary>
/// <param name="Id">Stable application-owned job identifier.</param>
/// <param name="OwnerId">Owner identity associated with the job.</param>
/// <param name="Kind">Registered deterministic handler kind.</param>
/// <param name="PayloadVersion">Positive version of the serialized payload schema.</param>
/// <param name="Payload">Serialized job payload.</param>
/// <param name="TimeZoneId">Time-zone identifier used to display or resolve schedule intent.</param>
/// <param name="DueAtUtc">UTC instant when the job becomes eligible.</param>
/// <param name="MisfirePolicy">Stable handler-defined policy for a late job.</param>
/// <param name="ClientRequestId">Optional idempotency key scoped to the owner.</param>
public sealed record SqliteScheduledJob(
    JobId Id,
    string OwnerId,
    string Kind,
    int PayloadVersion,
    string Payload,
    string TimeZoneId,
    DateTimeOffset DueAtUtc,
    string MisfirePolicy,
    string? ClientRequestId);

/// <summary>Persists durable job payloads and atomic expiring lease transitions.</summary>
public sealed class SqliteJobStore(SqliteDatabase database, IClock clock) : IJobStore
{
    /// <summary>Persists a one-time job; a repeated owner request ID returns the original job identifier.</summary>
    /// <param name="job">Validated job storage fields.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>The stored identifier, including the original ID for a duplicate request.</returns>
    public async ValueTask<JobId> ScheduleAsync(SqliteScheduledJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Jobs(
                Id, OwnerId, Kind, PayloadVersion, Payload, TimeZoneId, DueAtUtc,
                Recurrence, Enabled, MisfirePolicy, ClientRequestId)
            VALUES (
                $id, $owner, $kind, $payloadVersion, $payload, $timeZone, $dueAt,
                NULL, 1, $misfire, $requestId)
            ON CONFLICT(OwnerId, ClientRequestId) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", job.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$owner", job.OwnerId);
        command.Parameters.AddWithValue("$kind", job.Kind);
        command.Parameters.AddWithValue("$payloadVersion", job.PayloadVersion);
        command.Parameters.AddWithValue("$payload", job.Payload);
        command.Parameters.AddWithValue("$timeZone", job.TimeZoneId);
        command.Parameters.AddWithValue("$dueAt", SqliteDatabase.FormatUtc(job.DueAtUtc));
        command.Parameters.AddWithValue("$misfire", job.MisfirePolicy);
        command.Parameters.AddWithValue("$requestId", (object?)job.ClientRequestId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (job.ClientRequestId is null)
        {
            return job.Id;
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id FROM Jobs WHERE OwnerId = $owner AND ClientRequestId = $requestId;";
        read.Parameters.AddWithValue("$owner", job.OwnerId);
        read.Parameters.AddWithValue("$requestId", job.ClientRequestId);
        var id = Convert.ToString(await read.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return new JobId(Guid.Parse(id!));
    }

    /// <inheritdoc />
    public async ValueTask<JobLease?> ClaimDueAsync(
        string workerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var now = SqliteDatabase.FormatUtc(nowUtc);
        var expires = SqliteDatabase.FormatUtc(nowUtc.Add(leaseDuration));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string? id;
        int payloadVersion;
        long version;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id, PayloadVersion, Version FROM Jobs
                WHERE Enabled = 1 AND DueAtUtc <= $now
                  AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= $now)
                ORDER BY DueAtUtc, Id LIMIT 1;
                """;
            select.Parameters.AddWithValue("$now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            id = reader.GetString(0);
            payloadVersion = reader.GetInt32(1);
            version = reader.GetInt64(2);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Jobs
                SET LeaseOwner = $worker, LeaseExpiresAtUtc = $expires,
                    AttemptCount = AttemptCount + 1, Version = Version + 1
                WHERE Id = $id AND Version = $version AND Enabled = 1
                  AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= $now);
                """;
            update.Parameters.AddWithValue("$worker", workerId);
            update.Parameters.AddWithValue("$expires", expires);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$version", version);
            update.Parameters.AddWithValue("$now", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new JobLease(new JobId(Guid.Parse(id)), workerId, SqliteDatabase.ParseUtc(expires), payloadVersion);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(JobLease lease, string outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var now = clock.UtcNow;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE Jobs
                SET Enabled = 0, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                    Outcome = $outcome, Version = Version + 1
                WHERE Id = $id AND PayloadVersion = $payloadVersion
                  AND LeaseOwner = $worker AND LeaseExpiresAtUtc = $expires AND Enabled = 1;
                """;
            update.Parameters.AddWithValue("$outcome", outcome);
            update.Parameters.AddWithValue("$id", lease.Id.Value.ToString("D"));
            update.Parameters.AddWithValue("$payloadVersion", lease.PayloadVersion);
            update.Parameters.AddWithValue("$worker", lease.LeaseOwner);
            update.Parameters.AddWithValue("$expires", SqliteDatabase.FormatUtc(lease.LeaseExpiresAtUtc));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PersistenceConcurrencyException($"job {lease.Id.Value:D} lease");
            }
        }

        await using (var run = connection.CreateCommand())
        {
            run.Transaction = (SqliteTransaction)transaction;
            run.CommandText = """
                INSERT INTO JobRuns(Id, JobId, ScheduledOccurrenceUtc, Status, CompletedAtUtc, Error)
                SELECT $runId, Id, DueAtUtc, $outcome, $completedAt,
                       CASE WHEN $outcome IN ('Succeeded', 'Failed', 'Unknown') THEN NULL ELSE $outcome END
                FROM Jobs WHERE Id = $jobId;
                """;
            run.Parameters.AddWithValue("$runId", Guid.NewGuid().ToString("D"));
            run.Parameters.AddWithValue("$outcome", outcome);
            run.Parameters.AddWithValue("$completedAt", SqliteDatabase.FormatUtc(now));
            run.Parameters.AddWithValue("$jobId", lease.Id.Value.ToString("D"));
            await run.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
