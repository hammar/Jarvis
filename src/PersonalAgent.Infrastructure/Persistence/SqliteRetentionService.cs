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
/// <param name="ConversationsDeleted">Number of expired conversation roots removed with their messages and terminal turns.</param>
/// <param name="AuditEventsDeleted">Number of expired audit events removed.</param>
public sealed record SqliteRetentionResult(int ConversationsDeleted, int AuditEventsDeleted);

/// <summary>Deletes expired conversation history only when no unresolved turn would be lost, plus expired audit events.</summary>
public sealed class SqliteRetentionService : IHistoryRetentionStore
{
    private readonly SqliteDatabase database;
    private readonly IClock clock;
    private readonly SqliteRetentionOptions options;

    /// <summary>Creates a retention service using validated configuration defaults beneath durable owner settings.</summary>
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

    /// <summary>Deletes records strictly older than their effective UTC retention cutoff, preserving conversations with unresolved turns.</summary>
    /// <param name="cancellationToken">Token that cancels cleanup before commit.</param>
    /// <returns>Counts of deleted conversations and audit events.</returns>
    public ValueTask<SqliteRetentionResult> CleanupExpiredAsync(CancellationToken cancellationToken) =>
        CleanupExpiredWithSettingsAsync(
            new RetentionSettings(options.ConversationRetentionDays, options.AuditRetentionDays),
            clock.UtcNow,
            cancellationToken);

    async ValueTask IHistoryRetentionStore.CleanupExpiredAsync(
        RetentionSettings defaults,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredWithSettingsAsync(defaults, nowUtc, cancellationToken);
    }

    private async ValueTask<SqliteRetentionResult> CleanupExpiredWithSettingsAsync(
        RetentionSettings defaults,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        defaults.Validate();
        var now = nowUtc.ToUniversalTime();
        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        var settings = await ReadEffectiveSettingsAsync(connection, transaction, defaults, cancellationToken);
        var conversationCutoff = SqliteValue.Utc(now.AddDays(-settings.ConversationDays));
        var auditCutoff = SqliteValue.Utc(now.AddDays(-settings.AuditDays));

        int conversations;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM conversations
                WHERE updated_at_utc < $cutoff
                  AND NOT EXISTS (
                      SELECT 1 FROM turns
                      WHERE turns.conversation_id = conversations.id
                        AND turns.status NOT IN ('Completed', 'Failed', 'Cancelled')
                  );
                """;
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

    private static async Task<RetentionSettings> ReadEffectiveSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionSettings defaults,
        CancellationToken cancellationToken)
    {
        var conversationDays = await ReadSettingAsync(
            connection,
            transaction,
            "conversation_retention_days",
            defaults.ConversationDays,
            cancellationToken);
        var auditDays = await ReadSettingAsync(
            connection,
            transaction,
            "audit_retention_days",
            defaults.AuditDays,
            cancellationToken);
        var settings = new RetentionSettings(conversationDays, auditDays);
        settings.Validate();
        return settings;
    }

    private static async Task<int> ReadSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        int fallback,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT setting_value FROM owner_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? fallback
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
