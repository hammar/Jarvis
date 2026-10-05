using Microsoft.Data.Sqlite;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Creates consistent SQLite backups and restores only integrity-checked, migrated copies.</summary>
public sealed class SqliteBackupRestoreService
{
    private readonly SqliteDatabase database;

    /// <summary>Creates a backup service for the specified live database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    public SqliteBackupRestoreService(SqliteDatabase database) => this.database = database;

    /// <summary>Creates a consistent SQLite backup at a new destination file.</summary>
    /// <param name="backupPath">Destination path; an existing file is never overwritten.</param>
    /// <param name="cancellationToken">Token checked before and after SQLite's backup operation.</param>
    /// <returns>A task that completes after backup integrity has been verified.</returns>
    public async Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var destinationPath = Path.GetFullPath(backupPath);
        if (PathsEqual(destinationPath, database.DatabasePath))
        {
            throw new ArgumentException("The backup destination must differ from the live database path.", nameof(backupPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The backup path must have a parent directory."));
        var reservedDestination = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var reserved = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                reservedDestination = true;
            }

            await using (var source = await database.OpenConnectionAsync(cancellationToken))
            await using (var destination = await OpenWritableConnectionAsync(destinationPath, cancellationToken))
            {
                source.BackupDatabase(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyDatabaseAsync(destinationPath, cancellationToken);
        }
        catch
        {
            if (reservedDestination)
            {
                DeleteDatabaseFiles(destinationPath);
            }

            throw;
        }
    }

    /// <summary>Validates that pending migrations can be applied to a private copy without changing the live file.</summary>
    /// <param name="cancellationToken">Token that cancels copy, migration, or integrity validation.</param>
    /// <returns>A task that completes when the migrated copy passes SQLite integrity checks.</returns>
    public async Task ValidateUpgradeOnCopyAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(database.DatabasePath)
            ?? throw new InvalidOperationException("The database path must have a parent directory.");
        var copyPath = Path.Combine(directory, $".upgrade-check-{System.Guid.NewGuid():N}.db");
        try
        {
            await CopyDatabaseAsync(database.DatabasePath, copyPath, cancellationToken);
            var copy = new SqliteDatabase(copyPath);
            await copy.InitializeAsync(cancellationToken);
            await VerifyDatabaseAsync(copyPath, cancellationToken);
        }
        finally
        {
            DeleteDatabaseFiles(copyPath);
        }
    }

    /// <summary>Restores from a verified backup after migrating a private copy of it.</summary>
    /// <remarks>
    /// The caller must stop application workers and ensure no database connections are in use before restore.
    /// The restore never replays jobs or changes an action's recorded Unknown outcome.
    /// </remarks>
    /// <param name="backupPath">Existing SQLite backup to restore.</param>
    /// <param name="cancellationToken">Token checked before copy, migration, validation, and file replacement.</param>
    /// <returns>A task that completes after the verified database replaces the live file.</returns>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var sourcePath = Path.GetFullPath(backupPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The SQLite backup file was not found.", sourcePath);
        }

        if (PathsEqual(sourcePath, database.DatabasePath))
        {
            throw new ArgumentException("The restore source must differ from the live database path.", nameof(backupPath));
        }

        await VerifyDatabaseAsync(sourcePath, cancellationToken);
        var directory = Path.GetDirectoryName(database.DatabasePath)
            ?? throw new InvalidOperationException("The database path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var restorePath = Path.Combine(directory, $".restore-{System.Guid.NewGuid():N}.db");
        try
        {
            await CopyDatabaseAsync(sourcePath, restorePath, cancellationToken);
            var restored = new SqliteDatabase(restorePath);
            await restored.InitializeAsync(cancellationToken);
            await VerifyDatabaseAsync(restorePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            DeleteDatabaseSidecars(database.DatabasePath);
            File.Move(restorePath, database.DatabasePath, overwrite: true);
        }
        finally
        {
            DeleteDatabaseFiles(restorePath);
        }
    }

    private static async Task CopyDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var source = await OpenReadOnlyConnectionAsync(sourcePath, cancellationToken))
        await using (var destination = await OpenWritableConnectionAsync(destinationPath, cancellationToken))
        {
            source.BackupDatabase(destination);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task VerifyDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(path, cancellationToken);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await integrity.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite integrity check failed for '{Path.GetFileName(path)}'.");
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException($"SQLite foreign-key check failed for '{Path.GetFileName(path)}'.");
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<SqliteConnection> OpenWritableConnectionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
            ForeignKeys = true
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.Ordinal);

    private static void DeleteDatabaseFiles(string path)
    {
        DeleteDatabaseSidecars(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDatabaseSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }
}
