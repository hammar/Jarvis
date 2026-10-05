using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists atomic job lease claims and terminal run outcomes without performing job work.</summary>
public sealed class SqliteJobStore : IJobStore
{
    private readonly SqliteDatabase database;
    private readonly IClock clock;

    /// <summary>Creates a job store over the configured SQLite database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    /// <param name="clock">UTC clock used to reject expired lease completions.</param>
    public SqliteJobStore(SqliteDatabase database, IClock clock)
    {
        this.database = database;
        this.clock = clock;
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

        var now = SqliteValue.Utc(nowUtc);
        var expiry = SqliteValue.Utc(nowUtc.Add(leaseDuration));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        string? id = null;
        var payloadVersion = 0;
        await using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = """
                SELECT id, payload_version
                FROM jobs AS due_job
                WHERE enabled = 1 AND due_at_utc <= $now
                  AND (lease_owner IS NULL OR lease_expires_at_utc <= $now)
                  AND NOT EXISTS (
                      SELECT 1 FROM job_runs
                      WHERE job_id = due_job.id
                        AND scheduled_occurrence_utc = due_job.due_at_utc
                        AND completed_at_utc IS NOT NULL
                  )
                ORDER BY due_at_utc, id
                LIMIT 1;
                """;
            candidate.Parameters.AddWithValue("$now", now);
            await using var reader = await candidate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetString(0);
                payloadVersion = reader.GetInt32(1);
            }
        }

        if (id is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE jobs
                SET lease_owner = $owner, lease_expires_at_utc = $expires,
                    attempt_count = attempt_count + 1, version = version + 1
                WHERE id = $id AND enabled = 1 AND due_at_utc <= $now
                  AND (lease_owner IS NULL OR lease_expires_at_utc <= $now)
                  AND NOT EXISTS (
                      SELECT 1 FROM job_runs
                      WHERE job_id = jobs.id
                        AND scheduled_occurrence_utc = jobs.due_at_utc
                        AND completed_at_utc IS NOT NULL
                  );
                """;
            claim.Parameters.AddWithValue("$owner", workerId);
            claim.Parameters.AddWithValue("$expires", expiry);
            claim.Parameters.AddWithValue("$id", id);
            claim.Parameters.AddWithValue("$now", now);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PersistenceConcurrencyException("The due job was claimed by another worker.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new JobLease(new JobId(System.Guid.Parse(id)), workerId, nowUtc.Add(leaseDuration), payloadVersion);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(
        JobLease lease,
        string outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var now = SqliteValue.Utc(clock.UtcNow);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string occurrence;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT due_at_utc FROM jobs
                WHERE id = $id AND lease_owner = $owner
                  AND lease_expires_at_utc = $expires AND lease_expires_at_utc > $now;
                """;
            lookup.Parameters.AddWithValue("$id", SqliteValue.Guid(lease.Id.Value));
            lookup.Parameters.AddWithValue("$owner", lease.LeaseOwner);
            lookup.Parameters.AddWithValue("$expires", SqliteValue.Utc(lease.LeaseExpiresAtUtc));
            lookup.Parameters.AddWithValue("$now", now);
            occurrence = (string?)await lookup.ExecuteScalarAsync(cancellationToken)
                ?? throw new PersistenceConcurrencyException("The job lease expired or is no longer owned by this worker.");
        }

        await using (var run = connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = """
                INSERT INTO job_runs
                    (id, job_id, scheduled_occurrence_utc, status, started_at_utc, completed_at_utc)
                VALUES ($id, $job_id, $occurrence, $status, NULL, $now)
                ON CONFLICT(job_id, scheduled_occurrence_utc) DO UPDATE SET
                    status = excluded.status, started_at_utc = NULL,
                    completed_at_utc = excluded.completed_at_utc;
                """;
            run.Parameters.AddWithValue("$id", SqliteValue.Guid(System.Guid.NewGuid()));
            run.Parameters.AddWithValue("$job_id", SqliteValue.Guid(lease.Id.Value));
            run.Parameters.AddWithValue("$occurrence", occurrence);
            run.Parameters.AddWithValue("$status", outcome);
            run.Parameters.AddWithValue("$now", now);
            await run.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET outcome = $outcome, lease_owner = NULL, lease_expires_at_utc = NULL, version = version + 1
                WHERE id = $id AND lease_owner = $owner
                  AND lease_expires_at_utc = $expires AND lease_expires_at_utc > $now;
                """;
            update.Parameters.AddWithValue("$outcome", outcome);
            update.Parameters.AddWithValue("$id", SqliteValue.Guid(lease.Id.Value));
            update.Parameters.AddWithValue("$owner", lease.LeaseOwner);
            update.Parameters.AddWithValue("$expires", SqliteValue.Utc(lease.LeaseExpiresAtUtc));
            update.Parameters.AddWithValue("$now", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PersistenceConcurrencyException("The job lease expired before its outcome could be committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
