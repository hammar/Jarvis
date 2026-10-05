using Microsoft.Data.Sqlite;
using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Contains validated retention periods for conversations and audit records.</summary>
public sealed record SqliteRetentionOptions(int ConversationRetentionDays = 90, int AuditRetentionDays = 30)
{
    /// <summary>Validates configured retention windows against supported bounds.</summary>
    public void Validate()
    {
        if (ConversationRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(ConversationRetentionDays), "Conversation retention must be from 1 through 3650 days.");
        }

        if (AuditRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(AuditRetentionDays), "Audit retention must be from 1 through 3650 days.");
        }
    }
}

/// <summary>Reports deterministic retention cleanup counts.</summary>
/// <param name="ConversationsDeleted">Number of expired conversation roots removed with their messages and turns.</param>
/// <param name="AuditEventsDeleted">Number of expired audit events removed.</param>
public sealed record SqliteRetentionResult(int ConversationsDeleted, int AuditEventsDeleted);

/// <summary>Deletes only expired ordinary conversation history and audit events.</summary>
public sealed class SqliteRetentionService
{
    private readonly SqliteDatabase database;
    private readonly IClock clock;
    private readonly SqliteRetentionOptions options;

    /// <summary>Creates a retention service using validated owner-configured periods.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    /// <param name="clock">UTC clock used to calculate deterministic expiry cutoffs.</param>
    /// <param name="options">Validated conversation and audit retention windows.</param>
    public SqliteRetentionService(SqliteDatabase database, IClock clock, SqliteRetentionOptions options)
    {
        this.database = database;
        this.clock = clock;
        this.options = options;
        options.Validate();
    }

    /// <summary>Deletes records strictly older than their configured UTC retention cutoff.</summary>
    /// <param name="cancellationToken">Token that cancels cleanup before commit.</param>
    /// <returns>Counts of deleted conversations and audit events.</returns>
    public async ValueTask<SqliteRetentionResult> CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var conversationCutoff = SqliteValue.Utc(now.AddDays(-options.ConversationRetentionDays));
        var auditCutoff = SqliteValue.Utc(now.AddDays(-options.AuditRetentionDays));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        int conversations;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM conversations WHERE updated_at_utc < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", conversationCutoff);
            conversations = await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        int auditEvents;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM audit_events WHERE occurred_at_utc < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", auditCutoff);
            auditEvents = await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqliteRetentionResult(conversations, auditEvents);
    }
}
