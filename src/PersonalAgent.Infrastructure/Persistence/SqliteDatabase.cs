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

        var appliedVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
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
        var temporary = GetTemporaryPath(destination);
        try
        {
            await using (var source = await OpenConnectionAsync(cancellationToken))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                await target.OpenAsync(cancellationToken);
                await VerifyDatabaseAsync(source, cancellationToken);
                source.BackupDatabase(target);
                await VerifyDatabaseAsync(target, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDatabaseFile(temporary, destination);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
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
        var temporary = GetTemporaryPath(DatabasePath);
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await target.OpenAsync(cancellationToken);
                await VerifyDatabaseAsync(source, cancellationToken);
                source.BackupDatabase(target);
                await VerifyDatabaseAsync(target, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDatabaseFile(temporary, DatabasePath);
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

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite integrity check failed: {result}");
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
        long version;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
            version = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The file is not an initialized PersonalAgent database.", exception);
        }

        if (version < 1 || version > SchemaMigrations.All[^1].Version)
        {
            throw new InvalidDataException($"Unsupported database schema version {version}.");
        }
    }

    private static string GetTemporaryPath(string path) =>
        $"{path}.{Guid.NewGuid():N}.tmp";

    private static void ReplaceDatabaseFile(string temporary, string destination)
    {
        DeleteIfExists($"{destination}-wal");
        DeleteIfExists($"{destination}-shm");
        File.Move(temporary, destination, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureDifferentFiles(string first, string second)
    {
        if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup and database paths must refer to different files.");
        }
    }
}
