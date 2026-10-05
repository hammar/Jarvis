using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists conversation messages and assigns a durable per-conversation sequence.</summary>
public sealed class SqliteConversationStore : IConversationStore, IAtomicTurnOutcomeStore
{
    private readonly SqliteDatabase database;
    private readonly IClock clock;

    /// <summary>Creates a conversation store over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    /// <param name="clock">UTC clock used to maintain conversation update times.</param>
    public SqliteConversationStore(SqliteDatabase database, IClock clock)
    {
        this.database = database;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask<ConversationMessage> AppendMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Content);
        if (message.Role is not ("user" or "assistant" or "tool" or "system"))
        {
            throw new ArgumentException("Message role must be user, assistant, tool, or system.", nameof(message));
        }

        var createdAtUtc = message.CreatedAtUtc.ToUniversalTime();
        var createdAt = SqliteValue.Utc(createdAtUtc);
        var updatedAt = SqliteValue.Utc(clock.UtcNow < createdAtUtc ? createdAtUtc : clock.UtcNow);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                INSERT INTO conversations (id, owner_id, created_at_utc, updated_at_utc)
                VALUES ($id, 'owner', $created, $updated)
                ON CONFLICT(id) DO UPDATE SET updated_at_utc =
                    CASE WHEN excluded.updated_at_utc > conversations.updated_at_utc
                         THEN excluded.updated_at_utc ELSE conversations.updated_at_utc END;
                """;
            conversation.Parameters.AddWithValue("$id", SqliteValue.Guid(message.ConversationId.Value));
            conversation.Parameters.AddWithValue("$created", createdAt);
            conversation.Parameters.AddWithValue("$updated", updatedAt);
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        long sequence;
        await using (var nextSequence = connection.CreateCommand())
        {
            nextSequence.Transaction = transaction;
            nextSequence.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM messages WHERE conversation_id = $id;";
            nextSequence.Parameters.AddWithValue("$id", SqliteValue.Guid(message.ConversationId.Value));
            sequence = Convert.ToInt64(await nextSequence.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc)
                VALUES ($message_id, $conversation_id, $sequence, $role, $content, $created_at);
                """;
            insert.Parameters.AddWithValue("$message_id", SqliteValue.Guid(message.MessageId));
            insert.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(message.ConversationId.Value));
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$created_at", createdAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
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
            throw new ArgumentOutOfRangeException(nameof(maximumMessages), "The message limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, conversation_id, role, content, created_at_utc
            FROM (
                SELECT message_id, conversation_id, role, content, created_at_utc, sequence
                FROM messages
                WHERE conversation_id = $conversation_id
                ORDER BY sequence DESC
                LIMIT $maximum
            )
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(conversationId.Value));
        command.Parameters.AddWithValue("$maximum", maximumMessages);

        var messages = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ConversationMessage(
                System.Guid.Parse(reader.GetString(0)),
                new ConversationId(System.Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                reader.GetString(3),
                SqliteValue.DateTimeOffset(reader.GetString(4))));
        }

        return messages;
    }

    /// <inheritdoc />
    public async ValueTask<ConversationTurn> CreateTurnAsync(
        TurnId turnId,
        ConversationId conversationId,
        TurnStatus status,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var created = SqliteValue.Utc(createdAtUtc);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                INSERT INTO conversations (id, owner_id, created_at_utc, updated_at_utc)
                VALUES ($id, 'owner', $created, $created)
                ON CONFLICT(id) DO UPDATE SET updated_at_utc =
                    CASE WHEN excluded.updated_at_utc > conversations.updated_at_utc
                         THEN excluded.updated_at_utc ELSE conversations.updated_at_utc END;
                """;
            conversation.Parameters.AddWithValue("$id", SqliteValue.Guid(conversationId.Value));
            conversation.Parameters.AddWithValue("$created", created);
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO turns (id, conversation_id, status, created_at_utc, updated_at_utc, version)
                VALUES ($id, $conversation_id, $status, $created, $created, 1);
                """;
            insert.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            insert.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(conversationId.Value));
            insert.Parameters.AddWithValue("$status", status.ToString());
            insert.Parameters.AddWithValue("$created", created);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ConversationTurn(turnId, conversationId, status, createdAtUtc.ToUniversalTime(), createdAtUtc.ToUniversalTime(), 1);
    }

    /// <inheritdoc />
    public async ValueTask<ConversationTurn?> GetTurnAsync(
        TurnId turnId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, status, created_at_utc, updated_at_utc, version
            FROM turns WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadTurn(reader)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConversationTurn>> ReadNonterminalTurnsAsync(
        int maximumTurns,
        CancellationToken cancellationToken)
    {
        if (maximumTurns is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTurns), "The turn limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, status, created_at_utc, updated_at_utc, version
            FROM turns
            WHERE status NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
            ORDER BY updated_at_utc, id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximumTurns);
        var turns = new List<ConversationTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(ReadTurn(reader));
        }

        return turns;
    }

    /// <inheritdoc />
    public async ValueTask<ConversationTurn> UpdateTurnStatusAsync(
        TurnId turnId,
        TurnStatus status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE turns SET status = $status,
                updated_at_utc = CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END,
                version = version + 1
            WHERE id = $id AND version = $version
                AND status NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted');
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException("The conversation turn changed or no longer exists.");
        }

        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                UPDATE conversations SET updated_at_utc =
                    CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END
                WHERE id = (SELECT conversation_id FROM turns WHERE id = $id);
                """;
            conversation.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
            conversation.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        ConversationTurn result;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT conversation_id, status, created_at_utc, updated_at_utc, version
                FROM turns WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !Enum.TryParse<TurnStatus>(reader.GetString(1), ignoreCase: false, out var savedStatus))
            {
                throw new InvalidDataException("The updated turn state could not be read.");
            }

            result = new ConversationTurn(
                turnId,
                new ConversationId(System.Guid.Parse(reader.GetString(0))),
                savedStatus,
                SqliteValue.DateTimeOffset(reader.GetString(2)),
                SqliteValue.DateTimeOffset(reader.GetString(3)),
                reader.GetInt64(4));
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
        TurnId turnId,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateTurnEvent(eventType, payloadJson);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var persisted = await AppendTurnEventAsync(
            connection,
            transaction,
            turnId,
            eventType,
            payloadJson,
            occurredAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    /// <inheritdoc />
    public async ValueTask<PersistedTurnEvent> UpdateTurnStatusAndAppendEventAsync(
        TurnId turnId,
        TurnStatus status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (status is not (TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A terminal turn status is required.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        ValidateTurnEvent(eventType, payloadJson);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE turns SET status = $status,
                    updated_at_utc = CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END,
                    version = version + 1
                WHERE id = $id AND version = $version
                    AND status NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted');
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
            update.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            update.Parameters.AddWithValue("$version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PersistenceConcurrencyException("The conversation turn changed or no longer exists.");
            }
        }

        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                UPDATE conversations SET updated_at_utc =
                    CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END
                WHERE id = (SELECT conversation_id FROM turns WHERE id = $id);
                """;
            conversation.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
            conversation.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        var persisted = await AppendTurnEventAsync(
            connection,
            transaction,
            turnId,
            eventType,
            payloadJson,
            occurredAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    private static void ValidateTurnEvent(string eventType, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        using var payload = JsonDocument.Parse(payloadJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Turn event payload must be a JSON object.", nameof(payloadJson));
        }
    }

    private static async ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnId turnId,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        long sequence;
        await using (var nextSequence = connection.CreateCommand())
        {
            nextSequence.Transaction = transaction;
            nextSequence.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM turn_events WHERE turn_id = $id;";
            nextSequence.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            sequence = Convert.ToInt64(await nextSequence.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        var eventId = System.Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO turn_events (event_id, turn_id, sequence, event_type, payload_json, occurred_at_utc)
                VALUES ($event_id, $turn_id, $sequence, $type, $payload, $occurred);
                """;
            insert.Parameters.AddWithValue("$event_id", SqliteValue.Guid(eventId));
            insert.Parameters.AddWithValue("$turn_id", SqliteValue.Guid(turnId.Value));
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$type", eventType);
            insert.Parameters.AddWithValue("$payload", payloadJson);
            insert.Parameters.AddWithValue("$occurred", SqliteValue.Utc(occurredAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE turns SET updated_at_utc =
                    CASE WHEN updated_at_utc < $occurred THEN $occurred ELSE updated_at_utc END
                WHERE id = $turn_id;
                UPDATE conversations SET updated_at_utc =
                    CASE WHEN updated_at_utc < $occurred THEN $occurred ELSE updated_at_utc END
                WHERE id = (SELECT conversation_id FROM turns WHERE id = $turn_id);
                """;
            update.Parameters.AddWithValue("$occurred", SqliteValue.Utc(occurredAtUtc));
            update.Parameters.AddWithValue("$turn_id", SqliteValue.Guid(turnId.Value));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return new PersistedTurnEvent(
            eventId,
            turnId,
            sequence,
            eventType,
            payloadJson,
            occurredAtUtc.ToUniversalTime());
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PersistedTurnEvent>> ReadTurnEventsAfterAsync(
        TurnId turnId,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        if (maximumEvents is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents), "The event limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, sequence, event_type, payload_json, occurred_at_utc
            FROM turn_events
            WHERE turn_id = $turn_id AND sequence > $after
            ORDER BY sequence
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$turn_id", SqliteValue.Guid(turnId.Value));
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$maximum", maximumEvents);

        var events = new List<PersistedTurnEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new PersistedTurnEvent(
                System.Guid.Parse(reader.GetString(0)),
                turnId,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                SqliteValue.DateTimeOffset(reader.GetString(4))));
        }

        return events;
    }

    private static ConversationTurn ReadTurn(SqliteDataReader reader) =>
        new(
            new TurnId(System.Guid.Parse(reader.GetString(0))),
            new ConversationId(System.Guid.Parse(reader.GetString(1))),
            Enum.Parse<TurnStatus>(reader.GetString(2), ignoreCase: false),
            SqliteValue.DateTimeOffset(reader.GetString(3)),
            SqliteValue.DateTimeOffset(reader.GetString(4)),
            reader.GetInt64(5));
}
