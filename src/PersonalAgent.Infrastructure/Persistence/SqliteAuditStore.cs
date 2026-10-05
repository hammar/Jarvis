using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Stores privacy-filtered audit events as append-only SQLite records.</summary>
public sealed class SqliteAuditStore : IAuditStore
{
    private readonly SqliteDatabase database;

    /// <summary>Creates an audit store over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    public SqliteAuditStore(SqliteDatabase database) => this.database = database;

    /// <inheritdoc />
    public async ValueTask AppendAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.EventType);
        using var payload = JsonDocument.Parse(auditEvent.PayloadJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Audit payload must be a JSON object.", nameof(auditEvent));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, event_type, subject_id, payload_json, occurred_at_utc)
            VALUES ($id, $type, $subject, $payload, $occurred);
            """;
        command.Parameters.AddWithValue("$id", SqliteValue.Guid(auditEvent.Id));
        command.Parameters.AddWithValue("$type", auditEvent.EventType);
        command.Parameters.AddWithValue("$subject", (object?)auditEvent.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", auditEvent.PayloadJson);
        command.Parameters.AddWithValue("$occurred", SqliteValue.Utc(auditEvent.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuditEventRecord>> ReadSinceAsync(
        DateTimeOffset fromUtc,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        if (maximumEvents is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents), "The event limit must be from 1 through 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, event_type, subject_id, payload_json, occurred_at_utc
            FROM audit_events
            WHERE occurred_at_utc >= $from
            ORDER BY occurred_at_utc, id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$from", SqliteValue.Utc(fromUtc));
        command.Parameters.AddWithValue("$maximum", maximumEvents);

        var events = new List<AuditEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AuditEventRecord(
                System.Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                SqliteValue.DateTimeOffset(reader.GetString(4))));
        }

        return events;
    }
}
