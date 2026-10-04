using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalAgent.Infrastructure.Persistence.Migrations;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Owns initialization and consistent backup/restore operations for one local SQLite file.</summary>
public sealed class SqliteDatabase
{
    private readonly string connectionString;

    /// <summary>Creates a database owner for the specified persistent file path.</summary>
    /// <param name="databasePath">Path to the SQLite file, resolved to an absolute path.</param>
    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString();
    }

    /// <summary>Gets the absolute path to the authoritative database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Applies all pending migrations and enables foreign keys and WAL journaling.</summary>
    /// <param name="cancellationToken">Token checked before each migration.</param>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            await journal.ExecuteScalarAsync(cancellationToken);
        }

        await using (var ledger = connection.CreateCommand())
        {
            ledger.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Version INTEGER PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
                """;
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }

        var appliedVersion = await ValidateMigrationHistoryAsync(connection, cancellationToken);
        if (appliedVersion > 0)
        {
            await ValidateSchemaAsync(connection, appliedVersion, cancellationToken);
        }

        if (appliedVersion > SchemaMigrations.All[^1].Version)
        {
            throw new InvalidOperationException(
                $"Database schema version {appliedVersion} is newer than this application supports.");
        }

        foreach (var migration in SchemaMigrations.All.Where(migration => migration.Version > appliedVersion))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = """
                        INSERT INTO SchemaMigrations(Version, Name, AppliedAtUtc)
                        VALUES ($version, $name, $appliedAt);
                        """;
                    command.Parameters.AddWithValue("$version", migration.Version);
                    command.Parameters.AddWithValue("$name", migration.Name);
                    command.Parameters.AddWithValue("$appliedAt", FormatUtc(DateTimeOffset.UtcNow));
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        await ValidateSchemaAsync(connection, SchemaMigrations.All[^1].Version, cancellationToken);
    }

    /// <summary>Creates a consistent online SQLite backup at a destination path.</summary>
    /// <param name="backupPath">Destination file, which is replaced only after integrity validation.</param>
    /// <param name="cancellationToken">Token checked before and after SQLite's backup operation.</param>
    public async Task BackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException("The SQLite database file does not exist.", DatabasePath);
        }

        var destination = Path.GetFullPath(backupPath);
        EnsureDifferentFiles(DatabasePath, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = await OpenConnectionAsync(cancellationToken);
        await VerifyDatabaseAsync(source, cancellationToken);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        await VerifyDatabaseAsync(target, cancellationToken);
    }

    /// <summary>Restores a SQLite backup after validating it, atomically replacing the database file.</summary>
    /// <param name="backupPath">Existing consistent SQLite backup.</param>
    /// <param name="cancellationToken">Token checked before and after SQLite's backup operation.</param>
    /// <remarks>Stop application workers and close all connections to the target database before restoring.</remarks>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(backupPath);
        EnsureDifferentFiles(sourcePath, DatabasePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The SQLite backup file does not exist.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var temporary = $"{DatabasePath}.{Guid.NewGuid():N}.restore";
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 10
            }.ToString()))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 10
            }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await VerifyDatabaseAsync(source, cancellationToken);
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
                await VerifyDatabaseAsync(target, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await CheckpointBeforeReplacementAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists($"{DatabasePath}-wal");
            DeleteIfExists($"{DatabasePath}-shm");
            File.Move(temporary, DatabasePath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static async Task<int> ValidateMigrationHistoryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name FROM SchemaMigrations ORDER BY Version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var version = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            version++;
            if (version > SchemaMigrations.All.Length
                || reader.GetInt32(0) != version
                || !string.Equals(reader.GetString(1), SchemaMigrations.All[version - 1].Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SQLite migration history is incomplete, duplicated, or unrecognized.");
            }
        }

        return version;
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            await using var integrityReader = await integrity.ExecuteReaderAsync(cancellationToken);
            while (await integrityReader.ReadAsync(cancellationToken))
            {
                var result = integrityReader.GetString(0);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SQLite integrity check failed: {result}");
                }
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("SQLite foreign key validation failed.");
        }
    }

    private static async Task VerifyDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await VerifyIntegrityAsync(connection, cancellationToken);
        int version;
        try
        {
            version = await ValidateMigrationHistoryAsync(connection, cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            throw new InvalidDataException("The file does not contain a valid PersonalAgent migration history.", exception);
        }

        if (version < 1)
        {
            throw new InvalidDataException("The file is not an initialized PersonalAgent database.");
        }

        await ValidateSchemaAsync(connection, version, cancellationToken);
    }

    private async Task CheckpointBeforeReplacementAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return;
        }

        try
        {
            await using var current = new SqliteConnection(connectionString);
            await current.OpenAsync(cancellationToken);
            await using var checkpoint = current.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("SQLite did not report a WAL checkpoint result.");
            }

            var busy = reader.GetInt32(0);
            var logFrames = reader.GetInt32(1);
            var checkpointedFrames = reader.GetInt32(2);
            if (busy != 0 || (logFrames >= 0 && checkpointedFrames < logFrames))
            {
                throw new IOException("The database WAL could not be fully checkpointed; restore was not applied.");
            }
        }
        catch (SqliteException exception)
        {
            if (File.Exists($"{DatabasePath}-wal") && new FileInfo($"{DatabasePath}-wal").Length > 0)
            {
                throw new InvalidDataException(
                    "The current database is unreadable and has a WAL that cannot be safely discarded.",
                    exception);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        var required = new Dictionary<string, string[]>
        {
            ["SchemaMigrations"] = ["Version", "Name", "AppliedAtUtc"],
            ["Conversations"] = ["Id", "CreatedAtUtc"],
            ["Messages"] = ["MessageId", "ConversationId", "Role", "Content", "CreatedAtUtc"],
            ["Turns"] = ["TurnId", "ConversationId", "Status", "CreatedAtUtc", "Version"],
            ["TurnEvents"] = ["EventId", "TurnId", "Sequence", "EventType", "Payload", "OccurredAtUtc"],
            ["MemoryFacts"] = ["Id", "Subject", "FactKey", "Value", "SourceId", "PrivacyClass", "Version", "ValidityStatus"],
            ["MemoryProposals"] = ["Id", "OwnerId", "ProposalJson", "Status", "Version"],
            ["Actions"] = ["Id", "OwnerId", "ActionType", "CanonicalArguments", "RequestHash", "Status", "Version"],
            ["ApprovalRequests"] = ["Id", "ActionId", "OwnerId", "ExpiresAtUtc", "Status", "Version"],
            ["Jobs"] = ["Id", "OwnerId", "Kind", "PayloadVersion", "Payload", "TimeZoneId", "DueAtUtc", "Enabled", "LeaseOwner", "LeaseExpiresAtUtc"],
            ["JobRuns"] = ["Id", "JobId", "ScheduledOccurrenceUtc", "Status"],
            ["Notifications"] = ["Id", "OwnerId", "Message", "CreatedAtUtc"],
            ["CloudConsents"] = ["Id", "OwnerId", "Provider", "PacketHash", "ExpiresAtUtc"],
            ["AuditEvents"] = ["Id", "OwnerId", "EventType", "DetailsJson", "OccurredAtUtc"]
        };

        foreach (var (table, requiredColumns) in required)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken))
            {
                actualColumns.Add(reader.GetString(1));
            }

            if (requiredColumns.Any(column => !actualColumns.Contains(column)))
            {
                throw new InvalidDataException($"Database schema is missing required columns from {table}.");
            }
        }

        var requiredObjects = new List<string>();
        if (version >= 2)
        {
            requiredObjects.AddRange(
                ["MemoryFactsSearch", "TR_MemoryFactsSearch_Insert", "TR_MemoryFactsSearch_Delete", "TR_MemoryFactsSearch_Update"]);
        }

        if (version >= 3)
        {
            requiredObjects.AddRange(
                ["UX_Actions_Id_OwnerId", "TR_ApprovalRequests_Owner_Insert", "TR_ApprovalRequests_Owner_Update"]);
        }

        foreach (var name in requiredObjects)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException($"Database schema is missing required object {name}.");
            }
        }
    }

    private static void EnsureDifferentFiles(string first, string second)
    {
        var firstPath = ResolvePathAliases(first);
        var secondPath = ResolvePathAliases(second);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(firstPath, secondPath, comparison))
        {
            throw new ArgumentException("Backup and database paths must refer to different files.");
        }
    }

    private static string ResolvePathAliases(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var components = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        return Path.GetFullPath(current);
    }
}
