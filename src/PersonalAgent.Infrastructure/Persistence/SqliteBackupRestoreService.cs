using Microsoft.Data.Sqlite;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Creates consistent SQLite backups and restores only integrity-checked, migrated copies.</summary>
public sealed class SqliteBackupRestoreService
{
    private const string PreparedRestore = "prepared";
    private const string PreparedRestoreWithoutOriginal = "prepared-without-original";
    private const string CommittedRestore = "committed";
    private const string RecoveredRestore = "recovered";
    private readonly SqliteDatabase database;
    private readonly Action? beforeReplacement;

    /// <summary>Creates a backup service for the specified live database.</summary>
    /// <param name="database">Database connection and migration owner.</param>
    public SqliteBackupRestoreService(SqliteDatabase database) : this(database, null)
    {
    }

    internal SqliteBackupRestoreService(SqliteDatabase database, Action? beforeReplacement)
    {
        this.database = database;
        this.beforeReplacement = beforeReplacement;
    }

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

            await using (var source = await OpenReadOnlyConnectionAsync(database.DatabasePath, cancellationToken))
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
        await RecoverInterruptedRestoreAsync(database.DatabasePath, cancellationToken);
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
        var restorePath = RestoreStagedPath(database.DatabasePath);
        var rollbackPath = RestoreRollbackPath(database.DatabasePath);
        var prepared = false;
        try
        {
            DeleteRestoreArtifacts(database.DatabasePath);
            await CopyDatabaseAsync(sourcePath, restorePath, cancellationToken);
            var restored = new SqliteDatabase(restorePath);
            await restored.InitializeAsync(cancellationToken);
            await VerifyDatabaseAsync(restorePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var originalExists = File.Exists(database.DatabasePath);
            if (originalExists)
            {
                File.Copy(database.DatabasePath, rollbackPath, overwrite: true);
                CopySidecarIfPresent(database.DatabasePath + "-wal", RestoreOriginalWalPath(database.DatabasePath));
                CopySidecarIfPresent(database.DatabasePath + "-shm", RestoreOriginalShmPath(database.DatabasePath));
            }

            WriteRestoreMarker(
                database.DatabasePath,
                originalExists ? PreparedRestore : PreparedRestoreWithoutOriginal);
            prepared = true;
            DeleteDatabaseSidecars(database.DatabasePath);
            beforeReplacement?.Invoke();
            File.Move(restorePath, database.DatabasePath, overwrite: true);
            WriteRestoreMarker(database.DatabasePath, CommittedRestore);
            DeleteRestoreArtifacts(database.DatabasePath);
        }
        catch (Exception restoreFailure)
        {
            if (prepared)
            {
                try
                {
                    await RecoverInterruptedRestoreAsync(database.DatabasePath, CancellationToken.None);
                }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException(
                        "SQLite restore failed and rollback recovery remains pending.",
                        restoreFailure,
                        recoveryFailure);
                }
            }
            else
            {
                DeleteRestoreArtifacts(database.DatabasePath);
            }

            throw;
        }
    }

    internal static async Task RecoverInterruptedRestoreAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var markerPath = RestoreMarkerPath(databasePath);
        if (!File.Exists(markerPath))
        {
            return;
        }

        var state = await File.ReadAllTextAsync(markerPath, cancellationToken);
        if (string.Equals(state, CommittedRestore, StringComparison.Ordinal)
            || string.Equals(state, RecoveredRestore, StringComparison.Ordinal))
        {
            DeleteRestoreArtifacts(databasePath);
            return;
        }

        if (string.Equals(state, PreparedRestore, StringComparison.Ordinal))
        {
            var rollbackPath = RestoreRollbackPath(databasePath);
            if (!File.Exists(rollbackPath))
            {
                throw new InvalidDataException("Interrupted SQLite restore has no recoverable original database.");
            }

            DeleteDatabaseSidecars(databasePath);
            File.Copy(rollbackPath, databasePath, overwrite: true);
            var savedWalPath = RestoreOriginalWalPath(databasePath);
            if (File.Exists(savedWalPath))
            {
                File.Copy(savedWalPath, databasePath + "-wal", overwrite: true);
            }
        }
        else if (string.Equals(state, PreparedRestoreWithoutOriginal, StringComparison.Ordinal))
        {
            DeleteDatabaseFiles(databasePath);
        }
        else
        {
            throw new InvalidDataException("Interrupted SQLite restore has an unknown recovery state.");
        }

        WriteRestoreMarker(databasePath, RecoveredRestore);
        DeleteRestoreArtifacts(databasePath);
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

    internal static string RestoreMarkerPath(string databasePath) => databasePath + ".restore-state";

    internal static string RestoreStagedPath(string databasePath) => databasePath + ".restore-staged";

    internal static string RestoreRollbackPath(string databasePath) => databasePath + ".restore-rollback";

    private static string RestoreOriginalWalPath(string databasePath) => databasePath + ".restore-original-wal";

    private static string RestoreOriginalShmPath(string databasePath) => databasePath + ".restore-original-shm";

    private static void WriteRestoreMarker(string databasePath, string state)
    {
        var markerPath = RestoreMarkerPath(databasePath);
        var temporaryPath = markerPath + ".tmp";
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(state);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, markerPath, overwrite: true);
    }

    private static void CopySidecarIfPresent(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void DeleteRestoreArtifacts(string databasePath)
    {
        foreach (var path in new[]
                 {
                     RestoreStagedPath(databasePath),
                     RestoreRollbackPath(databasePath),
                     RestoreOriginalWalPath(databasePath),
                     RestoreOriginalShmPath(databasePath),
                     RestoreMarkerPath(databasePath) + ".tmp",
                     RestoreMarkerPath(databasePath)
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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
