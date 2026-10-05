using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>
/// Creates consistent SQLite backups and restores only integrity-checked, migrated copies while serializing
/// replacement and startup recovery with a per-database cross-process lock.
/// </summary>
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
    /// <remarks>
    /// The live database and backup destination directories must be private to this user.
    /// The caller must exclusively own the destination filename and its sidecar namespace during backup.
    /// Existing destination sidecars are rejected without modification.
    /// </remarks>
    /// <param name="backupPath">Destination path; an existing file is never overwritten.</param>
    /// <param name="cancellationToken">Token checked before and after SQLite's backup operation.</param>
    /// <returns>A task that completes after backup integrity has been verified.</returns>
    public async Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var destinationPath = Path.GetFullPath(backupPath);
        EnsureNotRestoreArtifactPath(database.DatabasePath, destinationPath, nameof(backupPath));
        if (PathsEqual(destinationPath, database.DatabasePath))
        {
            throw new ArgumentException("The backup destination must differ from the live database path.", nameof(backupPath));
        }

        SqliteDatabase.EnsurePrivateDataDirectory(Path.GetDirectoryName(database.DatabasePath)
            ?? throw new InvalidOperationException("The database path must have a parent directory."));
        SqliteDatabase.EnsurePrivateDataDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The backup path must have a parent directory."));
        EnsureNoDestinationSidecars(destinationPath);
        var reservedDestination = false;
        var ownsDestinationSidecars = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var reserved = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                reservedDestination = true;
                SqliteDatabase.RestrictFilePermissions(destinationPath);
            }

            await using (var source = await OpenReadOnlyConnectionAsync(database.DatabasePath, cancellationToken))
            {
                EnsureNoDestinationSidecars(destinationPath);
                ownsDestinationSidecars = true;
                await using var destination = await OpenWritableConnectionAsync(destinationPath, cancellationToken);
                source.BackupDatabase(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyDatabaseAsync(destinationPath, cancellationToken);
            SqliteDatabase.RestrictFilePermissions(destinationPath);
        }
        catch
        {
            if (reservedDestination)
            {
                if (ownsDestinationSidecars)
                {
                    DeleteDatabaseFiles(destinationPath);
                }
                else
                {
                    File.Delete(destinationPath);
                }
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
        SqliteDatabase.EnsurePrivateDataDirectory(directory);
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
    /// Restore shares the startup recovery lock and rejects the live database, sidecars, and reserved recovery files.
    /// The restore never replays jobs or changes an action's recorded Unknown outcome.
    /// </remarks>
    /// <param name="backupPath">Existing SQLite backup to restore.</param>
    /// <param name="cancellationToken">Token checked before copy, migration, validation, and file replacement.</param>
    /// <returns>A task that completes after the verified database replaces the live file.</returns>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var sourcePath = Path.GetFullPath(backupPath);
        EnsureNotRestoreArtifactPath(database.DatabasePath, sourcePath, nameof(backupPath));
        var directory = Path.GetDirectoryName(database.DatabasePath)
            ?? throw new InvalidOperationException("The database path must have a parent directory.");
        SqliteDatabase.EnsurePrivateDataDirectory(directory);
        await using var recoveryLock = await AcquireRestoreLockAsync(database.DatabasePath, cancellationToken);
        await RecoverInterruptedRestoreUnderLockAsync(database.DatabasePath, cancellationToken);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The SQLite backup file was not found.", sourcePath);
        }

        if (PathsEqual(sourcePath, database.DatabasePath))
        {
            throw new ArgumentException("The restore source must differ from the live database path.", nameof(backupPath));
        }

        await VerifyDatabaseAsync(sourcePath, cancellationToken);
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
            FlushFileContents(restorePath);
            FlushExistingSidecar(restorePath + "-wal");
            FlushExistingSidecar(restorePath + "-shm");
            cancellationToken.ThrowIfCancellationRequested();

            var originalExists = File.Exists(database.DatabasePath);
            if (originalExists)
            {
                CopyFileDurably(database.DatabasePath, rollbackPath);
                CopySidecarDurablyIfPresent(database.DatabasePath + "-wal", RestoreOriginalWalPath(database.DatabasePath));
                CopySidecarDurablyIfPresent(database.DatabasePath + "-shm", RestoreOriginalShmPath(database.DatabasePath));
                CopySidecarDurablyIfPresent(database.DatabasePath + "-journal", RestoreOriginalJournalPath(database.DatabasePath));
            }

            DurableFileSystem.FlushDirectory(directory);
            WriteRestoreMarker(
                database.DatabasePath,
                originalExists ? PreparedRestore : PreparedRestoreWithoutOriginal);
            prepared = true;
            RetireDatabaseSidecars(database.DatabasePath);
            DurableFileSystem.FlushDirectory(directory);
            beforeReplacement?.Invoke();
            DurableFileSystem.MoveFileDurably(restorePath, database.DatabasePath, overwrite: true);
            DurableFileSystem.FlushDirectory(directory);
            WriteRestoreMarker(database.DatabasePath, CommittedRestore);
            DeleteRestoreArtifacts(database.DatabasePath);
        }
        catch (Exception restoreFailure)
        {
            if (prepared)
            {
                try
                {
                    await RecoverInterruptedRestoreUnderLockAsync(database.DatabasePath, CancellationToken.None);
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

    internal static async Task<FileStream> AcquireRestoreLockAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var lockPath = RestoreLockPath(databasePath);
        var directory = Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException("The database path must have a parent directory.");
        SqliteDatabase.EnsurePrivateDataDirectory(directory);
        var timeout = TimeSpan.FromSeconds(30);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    SqliteDatabase.RestrictFilePermissions(lockPath);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception)
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    throw new IOException(
                        $"Timed out waiting for SQLite restore recovery lock '{Path.GetFileName(lockPath)}'.",
                        exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    internal static async Task RecoverInterruptedRestoreUnderLockAsync(
        string databasePath,
        CancellationToken cancellationToken)
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

            RetireDatabaseSidecars(databasePath);
            CopyFileDurably(rollbackPath, databasePath);
            var savedWalPath = RestoreOriginalWalPath(databasePath);
            if (File.Exists(savedWalPath))
            {
                CopyFileDurably(savedWalPath, databasePath + "-wal");
            }
            CopySidecarDurablyIfPresent(RestoreOriginalJournalPath(databasePath), databasePath + "-journal");
            DurableFileSystem.FlushDirectory(Path.GetDirectoryName(databasePath)!);
        }
        else if (string.Equals(state, PreparedRestoreWithoutOriginal, StringComparison.Ordinal))
        {
            RetireDatabaseFile(databasePath);
            RetireDatabaseSidecars(databasePath);
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

    internal static string RestoreLockPath(string databasePath) => databasePath + ".restore-lock";

    internal static string RestoreStagedPath(string databasePath) => databasePath + ".restore-staged";

    internal static string RestoreRollbackPath(string databasePath) => databasePath + ".restore-rollback";

    private static string RestoreOriginalWalPath(string databasePath) => databasePath + ".restore-original-wal";

    private static string RestoreOriginalShmPath(string databasePath) => databasePath + ".restore-original-shm";

    private static string RestoreOriginalJournalPath(string databasePath) => databasePath + ".restore-original-journal";

    private static string RestoreRetiredDatabasePath(string databasePath) => databasePath + ".restore-retired-database";

    private static string RestoreRetiredWalPath(string databasePath) => databasePath + ".restore-retired-wal";

    private static string RestoreRetiredShmPath(string databasePath) => databasePath + ".restore-retired-shm";

    private static string RestoreRetiredJournalPath(string databasePath) => databasePath + ".restore-retired-journal";

    private static void EnsureNotRestoreArtifactPath(string databasePath, string path, string parameterName)
    {
        var restoreArtifacts = new[]
        {
            RestoreStagedPath(databasePath),
            RestoreRollbackPath(databasePath),
            RestoreOriginalWalPath(databasePath),
            RestoreOriginalShmPath(databasePath),
            RestoreOriginalJournalPath(databasePath),
            RestoreRetiredDatabasePath(databasePath),
            RestoreRetiredWalPath(databasePath),
            RestoreRetiredShmPath(databasePath),
            RestoreRetiredJournalPath(databasePath),
            RestoreStagedPath(databasePath) + "-wal",
            RestoreStagedPath(databasePath) + "-shm",
            RestoreStagedPath(databasePath) + "-journal",
            RestoreMarkerPath(RestoreStagedPath(databasePath)),
            RestoreMarkerPath(RestoreStagedPath(databasePath)) + ".tmp",
            RestoreLockPath(RestoreStagedPath(databasePath)),
            RestoreRollbackPath(RestoreStagedPath(databasePath)),
            RestoreOriginalWalPath(RestoreStagedPath(databasePath)),
            RestoreOriginalShmPath(RestoreStagedPath(databasePath)),
            RestoreOriginalJournalPath(RestoreStagedPath(databasePath)),
            RestoreRetiredDatabasePath(RestoreStagedPath(databasePath)),
            RestoreRetiredWalPath(RestoreStagedPath(databasePath)),
            RestoreRetiredShmPath(RestoreStagedPath(databasePath)),
            RestoreRetiredJournalPath(RestoreStagedPath(databasePath)),
            RestoreMarkerPath(databasePath) + ".tmp",
            RestoreMarkerPath(databasePath),
            RestoreLockPath(databasePath),
            databasePath + "-wal",
            databasePath + "-shm",
            databasePath + "-journal"
        };
        if (restoreArtifacts.Any(artifact => PathsEqual(path, artifact)))
        {
            throw new ArgumentException("The path is reserved for SQLite restore recovery.", parameterName);
        }
    }

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

        SqliteDatabase.RestrictFilePermissions(temporaryPath);
        DurableFileSystem.MoveFileDurably(temporaryPath, markerPath, overwrite: true);
        DurableFileSystem.FlushDirectory(Path.GetDirectoryName(databasePath)!);
    }

    private static void CopySidecarDurablyIfPresent(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            CopyFileDurably(sourcePath, destinationPath);
        }
    }

    private static void CopyFileDurably(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("A durable SQLite file copy requires a parent directory.");
        var temporaryPath = Path.Combine(directory, $".jarvis-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            SqliteDatabase.RestrictFilePermissions(temporaryPath);
            DurableFileSystem.MoveFileDurably(temporaryPath, destinationPath, overwrite: true);
            DurableFileSystem.FlushDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void FlushExistingSidecar(string path)
    {
        if (File.Exists(path))
        {
            FlushFileContents(path);
        }
    }

    private static void FlushFileContents(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteRestoreArtifacts(string databasePath)
    {
        var changed = false;
        foreach (var path in new[]
                 {
                     RestoreStagedPath(databasePath),
                     RestoreRollbackPath(databasePath),
                     RestoreOriginalWalPath(databasePath),
                     RestoreOriginalShmPath(databasePath),
                     RestoreOriginalJournalPath(databasePath),
                     RestoreRetiredDatabasePath(databasePath),
                     RestoreRetiredWalPath(databasePath),
                     RestoreRetiredShmPath(databasePath),
                     RestoreRetiredJournalPath(databasePath),
                     RestoreStagedPath(databasePath) + "-wal",
                     RestoreStagedPath(databasePath) + "-shm",
                     RestoreStagedPath(databasePath) + "-journal",
                     RestoreMarkerPath(databasePath) + ".tmp",
                     RestoreMarkerPath(databasePath)
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                changed = true;
            }
        }

        if (changed)
        {
            DurableFileSystem.FlushDirectory(Path.GetDirectoryName(databasePath)!);
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            ResolvePathAliases(first),
            ResolvePathAliases(second),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string ResolvePathAliases(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DurableFileSystem.ResolvePath(path);
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Path must be fully qualified.", nameof(path));
        var current = root;
        var components = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            if (Directory.Exists(current))
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
            else if (index == components.Length - 1 && File.Exists(current))
            {
                current = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }

        return Path.GetFullPath(current);
    }

    private static void RetireDatabaseFile(string databasePath)
    {
        var retiredPath = RestoreRetiredDatabasePath(databasePath);
        if (File.Exists(databasePath))
        {
            DurableFileSystem.MoveFileDurably(databasePath, retiredPath, overwrite: true);
        }
    }

    private static void RetireDatabaseSidecars(string databasePath)
    {
        RetireSidecar(databasePath + "-wal", RestoreRetiredWalPath(databasePath));
        RetireSidecar(databasePath + "-shm", RestoreRetiredShmPath(databasePath));
        RetireSidecar(databasePath + "-journal", RestoreRetiredJournalPath(databasePath));
    }

    private static void RetireSidecar(string sidecarPath, string retiredPath)
    {
        if (File.Exists(sidecarPath))
        {
            DurableFileSystem.MoveFileDurably(sidecarPath, retiredPath, overwrite: true);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        DeleteDatabaseSidecars(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureNoDestinationSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = path + suffix;
            if (File.Exists(sidecarPath) || Directory.Exists(sidecarPath)
                || new FileInfo(sidecarPath).LinkTarget is not null)
            {
                throw new IOException("The SQLite backup destination has an existing sidecar; choose an unused destination.");
            }
        }
    }

    private static void DeleteDatabaseSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }
}
