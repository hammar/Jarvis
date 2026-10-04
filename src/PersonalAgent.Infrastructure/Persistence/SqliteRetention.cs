using Microsoft.Data.Sqlite;
using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Reports the number of top-level records expired by one retention pass.</summary>
/// <param name="Conversations">Conversation roots deleted; related messages, turns, and events cascade.</param>
/// <param name="AuditEvents">Audit events deleted.</param>
public sealed record RetentionResult(int Conversations, int AuditEvents);

/// <summary>Deterministically expires ordinary conversations and audit events without deleting durable state.</summary>
public sealed class SqliteRetention
{
    /// <summary>Default retention window for inactive ordinary conversations.</summary>
    public static readonly TimeSpan DefaultConversationRetention = TimeSpan.FromDays(90);

    /// <summary>Default retention window for audit events.</summary>
    public static readonly TimeSpan DefaultAuditRetention = TimeSpan.FromDays(30);

    private readonly SqliteDatabase database;
    private readonly TimeSpan conversationRetention;
    private readonly TimeSpan auditRetention;

    /// <summary>Creates retention policy with positive, bounded owner-configurable windows.</summary>
    /// <param name="database">Database to clean.</param>
    /// <param name="conversationRetention">Inactive conversation age, defaulting to 90 days.</param>
    /// <param name="auditRetention">Audit event age, defaulting to 30 days.</param>
    public SqliteRetention(
        SqliteDatabase database,
        TimeSpan? conversationRetention = null,
        TimeSpan? auditRetention = null)
    {
        this.database = database;
        this.conversationRetention = ValidateWindow(conversationRetention ?? DefaultConversationRetention, nameof(conversationRetention));
        this.auditRetention = ValidateWindow(auditRetention ?? DefaultAuditRetention, nameof(auditRetention));
    }

    /// <summary>Removes expired records using the supplied clock and one SQLite transaction.</summary>
    /// <param name="clock">Authoritative application UTC clock.</param>
    /// <param name="cancellationToken">Token that cancels before commit.</param>
    /// <returns>Counts of deleted conversations and audit events.</returns>
    public async ValueTask<RetentionResult> ExpireAsync(IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var conversationCutoff = SqliteDatabase.FormatUtc(clock.UtcNow - conversationRetention);
        var auditCutoff = SqliteDatabase.FormatUtc(clock.UtcNow - auditRetention);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int conversations;
        int auditEvents;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE MemoryFacts
                SET SourceId = 'redacted:conversation-retention',
                    UpdatedAtUtc = $now,
                    Version = Version + 1
                WHERE SourceId IN (
                    SELECT Messages.MessageId
                    FROM Messages
                    JOIN Conversations ON Conversations.Id = Messages.ConversationId
                    WHERE COALESCE(
                        (SELECT MAX(Recent.CreatedAtUtc)
                         FROM Messages AS Recent
                         WHERE Recent.ConversationId = Conversations.Id),
                        Conversations.CreatedAtUtc) < $cutoff
                      AND NOT EXISTS (
                        SELECT 1 FROM Turns
                        WHERE Turns.ConversationId = Conversations.Id
                          AND Turns.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
                      )
                );
                """;
            command.Parameters.AddWithValue("$cutoff", conversationCutoff);
            command.Parameters.AddWithValue("$now", SqliteDatabase.FormatUtc(clock.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM Conversations
                WHERE COALESCE(
                    (SELECT MAX(Messages.CreatedAtUtc) FROM Messages WHERE Messages.ConversationId = Conversations.Id),
                    Conversations.CreatedAtUtc) < $cutoff
                  AND NOT EXISTS (
                    SELECT 1 FROM Turns
                    WHERE Turns.ConversationId = Conversations.Id
                      AND Turns.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
                  );
                """;
            command.Parameters.AddWithValue("$cutoff", conversationCutoff);
            conversations = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM AuditEvents WHERE OccurredAtUtc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", auditCutoff);
            auditEvents = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RetentionResult(conversations, auditEvents);
    }

    private static TimeSpan ValidateWindow(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(36500))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Retention must be positive and no longer than 100 years.");
        }

        return value;
    }
}
