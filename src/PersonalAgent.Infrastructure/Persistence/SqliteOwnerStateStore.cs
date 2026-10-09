using Microsoft.Data.Sqlite;
using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists the single owner credential verifier and owner-selected history retention.</summary>
public sealed class SqliteOwnerStateStore(SqliteDatabase database) : IOwnerAccountStore, IOwnerSettingsService
{
    /// <inheritdoc />
    public async ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM owner_account WHERE owner_id = 'owner');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryCreateOwnerAsync(
        string passwordHash,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (passwordHash.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(passwordHash));
        }

        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO owner_account (owner_id, password_hash, created_at_utc)
            VALUES ('owner', $password_hash, $created_at);
            """;
        command.Parameters.AddWithValue("$password_hash", passwordHash);
        command.Parameters.AddWithValue("$created_at", SqliteValue.Utc(createdAtUtc));
        var created = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetPasswordHashAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM owner_account WHERE owner_id = 'owner';";
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<RetentionSettings> GetRetentionAsync(
        RetentionSettings defaults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        defaults.Validate();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var conversationDays = await ReadSettingAsync(
            connection,
            "conversation_retention_days",
            defaults.ConversationDays,
            cancellationToken);
        var auditDays = await ReadSettingAsync(
            connection,
            "audit_retention_days",
            defaults.AuditDays,
            cancellationToken);
        return new RetentionSettings(conversationDays, auditDays);
    }

    /// <inheritdoc />
    public async ValueTask<RetentionSettings> UpdateRetentionAsync(
        RetentionSettings settings,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var now = nowUtc.ToUniversalTime();
        var conversationCutoff = SqliteValue.Utc(now.AddDays(-settings.ConversationDays));
        var auditCutoff = SqliteValue.Utc(now.AddDays(-settings.AuditDays));

        await using var connection = await database.OpenConnectionWithDefaultTimeoutAsync(1, cancellationToken);
        await using var transaction = await SqliteDatabase.BeginImmediateTransactionAsync(connection, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "conversation_retention_days", settings.ConversationDays, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "audit_retention_days", settings.AuditDays, cancellationToken);

        await using (var conversations = connection.CreateCommand())
        {
            conversations.Transaction = transaction;
            conversations.CommandText = """
                DELETE FROM conversations
                WHERE updated_at_utc < $cutoff
                  AND NOT EXISTS (
                      SELECT 1 FROM turns
                      WHERE turns.conversation_id = conversations.id
                        AND turns.status NOT IN ('Completed', 'Failed', 'Cancelled')
                  );
                """;
            conversations.Parameters.AddWithValue("$cutoff", conversationCutoff);
            await conversations.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "DELETE FROM audit_events WHERE occurred_at_utc < $cutoff;";
            audit.Parameters.AddWithValue("$cutoff", auditCutoff);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return settings;
    }

    private static async Task<int> ReadSettingAsync(
        SqliteConnection connection,
        string key,
        int fallback,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM owner_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? fallback
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        int value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO owner_settings (setting_key, setting_value)
            VALUES ($key, $value)
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
