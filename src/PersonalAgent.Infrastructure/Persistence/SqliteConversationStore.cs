using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists conversation messages and assigns a durable per-conversation sequence.</summary>
public sealed class SqliteConversationStore : IConversationStore, IAtomicTurnOutcomeStore
{
    private const string RecentHistorySql = """
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
    public async ValueTask<ConversationRecord> CreateConversationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Id.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation identifier must be nonempty.", nameof(conversation));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(conversation.OwnerId);
        ArgumentNullException.ThrowIfNull(conversation.Title);
        if (conversation.OwnerId.Length > 128 || conversation.Title.Length > 200)
        {
            throw new ArgumentException("Conversation owner or title exceeds its supported length.", nameof(conversation));
        }

        var createdAt = SqliteValue.Utc(conversation.CreatedAtUtc);
        var updatedAt = SqliteValue.Utc(conversation.UpdatedAtUtc);
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations (id, owner_id, title, created_at_utc, updated_at_utc)
            VALUES ($id, $owner, $title, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(conversation.Id.Value));
        command.Parameters.AddWithValue("$owner", conversation.OwnerId);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$updated", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return conversation with
        {
            CreatedAtUtc = conversation.CreatedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = conversation.UpdatedAtUtc.ToUniversalTime()
        };
    }

    /// <inheritdoc />
    public async ValueTask<ConversationRecord?> GetConversationAsync(
        ConversationId conversationId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_id, title, created_at_utc, updated_at_utc
            FROM conversations WHERE id = $id AND owner_id = $owner;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(conversationId.Value));
        command.Parameters.AddWithValue("$owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConversationRecord>> ReadRecentConversationsAsync(
        string ownerId,
        int maximumConversations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (maximumConversations is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConversations), "The conversation limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_id, title, created_at_utc, updated_at_utc
            FROM conversations WHERE owner_id = $owner
            ORDER BY updated_at_utc DESC, id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$maximum", maximumConversations);
        var conversations = new List<ConversationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conversations.Add(ReadConversation(reader));
        }

        return conversations;
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
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);

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
        CancellationToken cancellationToken,
        Guid? currentTaskMessageId = null)
    {
        if (maximumMessages is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessages), "The message limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        long? boundarySequence = null;
        if (currentTaskMessageId is { } messageId)
        {
            await using var boundary = connection.CreateCommand();
            boundary.CommandText = """
                SELECT sequence FROM messages
                WHERE message_id = $message AND conversation_id = $conversation AND role = 'user';
                """;
            boundary.Parameters.AddWithValue("$message", SqliteValue.Guid(messageId));
            boundary.Parameters.AddWithValue("$conversation", SqliteValue.Guid(conversationId.Value));
            var value = await boundary.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The current task message does not exist in the conversation.");
            boundarySequence = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = boundarySequence is null ? RecentHistorySql : """
            SELECT message_id, conversation_id, role, content, created_at_utc
            FROM (
                SELECT m.message_id, m.conversation_id, m.role, m.content, m.created_at_utc, m.sequence,
                    COALESCE(task.sequence, m.sequence) AS history_order
                FROM messages m
                LEFT JOIN messages task ON task.turn_id = m.turn_id AND task.role = 'user'
                    AND task.conversation_id = m.conversation_id
                WHERE m.conversation_id = $conversation_id
                    AND COALESCE(task.sequence, m.sequence) < $boundary
                ORDER BY history_order DESC, m.sequence DESC
                LIMIT $maximum
            )
            ORDER BY history_order, sequence;
            """;
        command.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(conversationId.Value));
        command.Parameters.AddWithValue("$maximum", maximumMessages);
        command.Parameters.AddWithValue("$boundary", boundarySequence is { } sequence ? sequence : DBNull.Value);

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
    public async ValueTask<SubmittedConversationTurn> SubmitTurnAsync(
        ConversationTurnSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.TurnId.Value == Guid.Empty
            || submission.ConversationId.Value == Guid.Empty
            || submission.UserMessageId == Guid.Empty)
        {
            throw new ArgumentException("Turn, conversation, and message identifiers must be nonempty.", nameof(submission));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(submission.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.RequestFingerprint);
        if (submission.ClientRequestId.Length > 256
            || submission.RequestFingerprint.Length != 64
            || !submission.RequestFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The request identity or SHA-256 fingerprint is invalid.", nameof(submission));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(submission.Text);
        if (submission.Text.Length > 8_000)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "Turn text exceeds the fixed local input limit.");
        }

        var createdAtUtc = submission.CreatedAtUtc.ToUniversalTime();
        var createdAt = SqliteValue.Utc(createdAtUtc);
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT t.id, t.conversation_id, t.status, t.created_at_utc, t.updated_at_utc,
                    t.version, t.request_fingerprint, m.message_id
                FROM turns t
                LEFT JOIN messages m ON m.turn_id = t.id AND m.role = 'user'
                WHERE t.conversation_id = $conversation_id AND t.client_request_id = $request_id;
                """;
            existing.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(submission.ConversationId.Value));
            existing.Parameters.AddWithValue("$request_id", submission.ClientRequestId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var fingerprint = reader.IsDBNull(6) ? null : reader.GetString(6);
                if (!string.Equals(fingerprint, submission.RequestFingerprint, StringComparison.Ordinal))
                {
                    throw new TurnRequestConflictException();
                }

                if (reader.IsDBNull(7)
                    || !Enum.TryParse<TurnStatus>(reader.GetString(2), ignoreCase: false, out var existingStatus))
                {
                    throw new InvalidDataException("The durable request identity has no valid user message or turn status.");
                }

                var result = new SubmittedConversationTurn(
                    new ConversationTurn(
                        new TurnId(Guid.Parse(reader.GetString(0))),
                        new ConversationId(Guid.Parse(reader.GetString(1))),
                        existingStatus,
                        SqliteValue.DateTimeOffset(reader.GetString(3)),
                        SqliteValue.DateTimeOffset(reader.GetString(4)),
                        reader.GetInt64(5)),
                    Guid.Parse(reader.GetString(7)),
                    IsDuplicate: true);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
        }

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
            conversation.Parameters.AddWithValue("$id", SqliteValue.Guid(submission.ConversationId.Value));
            conversation.Parameters.AddWithValue("$created", createdAt);
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertTurn = connection.CreateCommand())
        {
            insertTurn.Transaction = transaction;
            insertTurn.CommandText = """
                INSERT INTO turns (
                    id, conversation_id, status, created_at_utc, updated_at_utc, version,
                    client_request_id, request_fingerprint)
                VALUES ($id, $conversation_id, 'Received', $created, $created, 1, $request_id, $fingerprint);
                """;
            insertTurn.Parameters.AddWithValue("$id", SqliteValue.Guid(submission.TurnId.Value));
            insertTurn.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(submission.ConversationId.Value));
            insertTurn.Parameters.AddWithValue("$created", createdAt);
            insertTurn.Parameters.AddWithValue("$request_id", submission.ClientRequestId);
            insertTurn.Parameters.AddWithValue("$fingerprint", submission.RequestFingerprint);
            await insertTurn.ExecuteNonQueryAsync(cancellationToken);
        }

        long messageSequence;
        await using (var nextSequence = connection.CreateCommand())
        {
            nextSequence.Transaction = transaction;
            nextSequence.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM messages WHERE conversation_id = $id;";
            nextSequence.Parameters.AddWithValue("$id", SqliteValue.Guid(submission.ConversationId.Value));
            messageSequence = Convert.ToInt64(
                await nextSequence.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var insertMessage = connection.CreateCommand())
        {
            insertMessage.Transaction = transaction;
            insertMessage.CommandText = """
                INSERT INTO messages (message_id, conversation_id, turn_id, sequence, role, content, created_at_utc)
                VALUES ($message_id, $conversation_id, $turn_id, $sequence, 'user', $content, $created);
                """;
            insertMessage.Parameters.AddWithValue("$message_id", SqliteValue.Guid(submission.UserMessageId));
            insertMessage.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(submission.ConversationId.Value));
            insertMessage.Parameters.AddWithValue("$turn_id", SqliteValue.Guid(submission.TurnId.Value));
            insertMessage.Parameters.AddWithValue("$sequence", messageSequence);
            insertMessage.Parameters.AddWithValue("$content", submission.Text);
            insertMessage.Parameters.AddWithValue("$created", createdAt);
            await insertMessage.ExecuteNonQueryAsync(cancellationToken);
        }

        var received = new TurnReceived(submission.TurnId, createdAtUtc);
        await AppendTurnEventAsync(
            connection,
            transaction,
            submission.TurnId,
            nameof(TurnReceived),
            JsonSerializer.Serialize(received),
            createdAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SubmittedConversationTurn(
            new ConversationTurn(
                submission.TurnId,
                submission.ConversationId,
                TurnStatus.Received,
                createdAtUtc,
                createdAtUtc,
                1),
            submission.UserMessageId,
            IsDuplicate: false);
    }

    /// <inheritdoc />
    public async ValueTask<SubmittedConversationTurn?> FindSubmittedTurnAsync(
        ConversationId conversationId,
        string clientRequestId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A nonempty conversation identifier is required.", nameof(conversationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        if (clientRequestId.Length > 256
            || requestFingerprint.Length != 64
            || !requestFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The request identity or SHA-256 fingerprint is invalid.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.conversation_id, t.status, t.created_at_utc, t.updated_at_utc,
                t.version, t.request_fingerprint, m.message_id
            FROM turns t
            LEFT JOIN messages m ON m.turn_id = t.id AND m.role = 'user'
            WHERE t.conversation_id = $conversation_id AND t.client_request_id = $request_id;
            """;
        command.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(conversationId.Value));
        command.Parameters.AddWithValue("$request_id", clientRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(6), requestFingerprint, StringComparison.Ordinal))
        {
            throw new TurnRequestConflictException();
        }

        if (reader.IsDBNull(7)
            || !Enum.TryParse<TurnStatus>(reader.GetString(2), ignoreCase: false, out var status))
        {
            throw new InvalidDataException("The durable request identity has no valid user message or turn status.");
        }

        return new SubmittedConversationTurn(
            new ConversationTurn(
                new TurnId(Guid.Parse(reader.GetString(0))),
                new ConversationId(Guid.Parse(reader.GetString(1))),
                status,
                SqliteValue.DateTimeOffset(reader.GetString(3)),
                SqliteValue.DateTimeOffset(reader.GetString(4)),
                reader.GetInt64(5)),
            Guid.Parse(reader.GetString(7)),
            IsDuplicate: true);
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
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
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
    public async ValueTask<ConversationTurn?> GetTurnForOwnerAsync(
        TurnId turnId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turns.id, turns.conversation_id, turns.status,
                turns.created_at_utc, turns.updated_at_utc, turns.version
            FROM turns
            INNER JOIN conversations ON conversations.id = turns.conversation_id
            WHERE turns.id = $id AND conversations.owner_id = $owner;
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
        command.Parameters.AddWithValue("$owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTurn(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ConversationTurn>> ReadRecentTurnsForOwnerAsync(
        string ownerId,
        int maximumTurns,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (maximumTurns is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTurns), "The turn limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turns.id, turns.conversation_id, turns.status,
                turns.created_at_utc, turns.updated_at_utc, turns.version
            FROM turns
            INNER JOIN conversations ON conversations.id = turns.conversation_id
            WHERE conversations.owner_id = $owner
            ORDER BY turns.updated_at_utc DESC, turns.id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
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

        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
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
    public async ValueTask<PersistedTurnEvent> TransitionTurnAndAppendEventAsync(
        TurnId turnId,
        TurnStatus status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (status is not (TurnStatus.Routing or TurnStatus.ContextReview or TurnStatus.Running or TurnStatus.WaitingForApproval))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A nonterminal turn lifecycle state is required.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        ValidateTurnEvent(eventType, payloadJson);
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        TurnStatus currentStatus;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status FROM turns WHERE id = $id AND version = $version;";
            read.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            read.Parameters.AddWithValue("$version", expectedVersion);
            var value = (string?)await read.ExecuteScalarAsync(cancellationToken);
            if (!Enum.TryParse(value, ignoreCase: false, out currentStatus))
            {
                throw new PersistenceConcurrencyException("The conversation turn changed or no longer exists.");
            }
        }

        if (!CanTransition(currentStatus, status))
        {
            throw new PersistenceConcurrencyException("The requested conversation turn state transition is not allowed.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE turns SET status = $status,
                    updated_at_utc = CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END,
                    version = version + 1
                WHERE id = $id AND version = $version AND status = $current;
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$updated", SqliteValue.Utc(updatedAtUtc));
            update.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            update.Parameters.AddWithValue("$version", expectedVersion);
            update.Parameters.AddWithValue("$current", currentStatus.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PersistenceConcurrencyException("The conversation turn changed or no longer exists.");
            }
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

    /// <inheritdoc />
    public async ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
        TurnId turnId,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateTurnEvent(eventType, payloadJson);
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
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
        CancellationToken cancellationToken,
        ConversationMessage? finalAssistantMessage = null)
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
        if (finalAssistantMessage is not null
            && (status is not (TurnStatus.Completed or TurnStatus.Failed)
                || finalAssistantMessage.Role != "assistant"
                || finalAssistantMessage.MessageId == Guid.Empty
                || string.IsNullOrWhiteSpace(finalAssistantMessage.Content)
                || finalAssistantMessage.Content.Length > 1_000_000))
        {
            throw new ArgumentException(
                "A completed or failed turn may persist one bounded nonempty assistant message.",
                nameof(finalAssistantMessage));
        }

        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
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
        if (finalAssistantMessage is not null)
        {
            await AppendFinalAssistantMessageAsync(
                connection,
                transaction,
                turnId,
                finalAssistantMessage,
                cancellationToken);
        }

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

    private static async ValueTask AppendFinalAssistantMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnId turnId,
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        Guid conversationId;
        await using (var turn = connection.CreateCommand())
        {
            turn.Transaction = transaction;
            turn.CommandText = "SELECT conversation_id FROM turns WHERE id = $id;";
            turn.Parameters.AddWithValue("$id", SqliteValue.Guid(turnId.Value));
            var value = (string?)await turn.ExecuteScalarAsync(cancellationToken);
            if (!Guid.TryParse(value, out conversationId) || conversationId != message.ConversationId.Value)
            {
                throw new ArgumentException("The final assistant message must belong to the completed turn's conversation.");
            }
        }

        long sequence;
        await using (var nextSequence = connection.CreateCommand())
        {
            nextSequence.Transaction = transaction;
            nextSequence.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM messages WHERE conversation_id = $id;";
            nextSequence.Parameters.AddWithValue("$id", SqliteValue.Guid(conversationId));
            sequence = Convert.ToInt64(
                await nextSequence.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var createdAt = SqliteValue.Utc(message.CreatedAtUtc);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (message_id, conversation_id, turn_id, sequence, role, content, created_at_utc)
                VALUES ($message_id, $conversation_id, $turn_id, $sequence, 'assistant', $content, $created_at);
                """;
            insert.Parameters.AddWithValue("$message_id", SqliteValue.Guid(message.MessageId));
            insert.Parameters.AddWithValue("$conversation_id", SqliteValue.Guid(conversationId));
            insert.Parameters.AddWithValue("$turn_id", SqliteValue.Guid(turnId.Value));
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$created_at", createdAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var updateConversation = connection.CreateCommand();
        updateConversation.Transaction = transaction;
        updateConversation.CommandText = """
            UPDATE conversations SET updated_at_utc =
                CASE WHEN updated_at_utc < $updated THEN $updated ELSE updated_at_utc END
            WHERE id = $id;
            """;
        updateConversation.Parameters.AddWithValue("$updated", createdAt);
        updateConversation.Parameters.AddWithValue("$id", SqliteValue.Guid(conversationId));
        await updateConversation.ExecuteNonQueryAsync(cancellationToken);
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

    private static ConversationRecord ReadConversation(SqliteDataReader reader) =>
        new(
            new ConversationId(System.Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            SqliteValue.DateTimeOffset(reader.GetString(3)),
            SqliteValue.DateTimeOffset(reader.GetString(4)));

    private static bool CanTransition(TurnStatus current, TurnStatus next) =>
        (current, next) switch
        {
            (TurnStatus.Received, TurnStatus.Routing) => true,
            (TurnStatus.Routing, TurnStatus.ContextReview or TurnStatus.Running) => true,
            (TurnStatus.ContextReview, TurnStatus.Routing or TurnStatus.Running) => true,
            (TurnStatus.Running, TurnStatus.WaitingForApproval) => true,
            (TurnStatus.WaitingForApproval, TurnStatus.Running) => true,
            _ => false
        };
}
