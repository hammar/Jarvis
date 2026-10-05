using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Owns a single SQLite database file, its connections, and forward-only schema migrations.</summary>
public sealed class SqliteDatabase
{
    private const UnixFileMode OwnerPrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerPrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode NonOwnerPermissions =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static readonly (int Version, string ResourceName)[] Migrations =
    [
        (1, "PersonalAgent.Infrastructure.Persistence.Migrations.001-initial.sql"),
        (2, "PersonalAgent.Infrastructure.Persistence.Migrations.002-durable-state.sql")
    ];

    private readonly string connectionString;

    /// <summary>Initializes a database owner for the specified durable file path.</summary>
    /// <param name="databasePath">Full or relative path to the SQLite file, outside the deployment directory.</param>
    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10,
            ForeignKeys = true
        }.ToString();
    }

    /// <summary>Gets the absolute path of the owned database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Acquires the per-database cross-process recovery lock, completes interrupted restore recovery,
    /// then enables WAL and applies pending migrations before SQLite is opened by the host.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels initialization between database operations.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The SQLite database path must have a parent directory.");
        EnsurePrivateDataDirectory(directory);
        await using var recoveryLock = await SqliteBackupRestoreService.AcquireRestoreLockAsync(
            DatabasePath,
            cancellationToken);
        await SqliteBackupRestoreService.RecoverInterruptedRestoreUnderLockAsync(
            DatabasePath,
            cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = (string?)await journalMode.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite did not enable WAL mode for '{DatabasePath}'.");
            }
        }

        await ApplyMigrationsAsync(connection, cancellationToken);
        await EnsureForeignKeysAreValidAsync(connection, cancellationToken);
        RestrictFilePermissions(DatabasePath);
        RestrictFilePermissions(DatabasePath + "-wal");
        RestrictFilePermissions(DatabasePath + "-shm");
        RestrictFilePermissions(SqliteBackupRestoreService.RestoreLockPath(DatabasePath));
    }

    /// <summary>Opens a configured connection with foreign-key enforcement and a bounded busy timeout.</summary>
    /// <param name="cancellationToken">Token that cancels opening the connection.</param>
    /// <returns>An open connection owned by the caller.</returns>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsurePrivateDataDirectory(Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The SQLite database path must have a parent directory."));
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            RestrictFilePermissions(DatabasePath);
            RestrictFilePermissions(DatabasePath + "-wal");
            RestrictFilePermissions(DatabasePath + "-shm");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static void EnsurePrivateDataDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory, OwnerPrivateDirectory);
            }

            var resolvedDirectory = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(directory);
            var permissions = File.GetUnixFileMode(resolvedDirectory);
            if ((permissions & NonOwnerPermissions) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"SQLite data directory '{Path.GetFullPath(directory)}' must be private to the current user (mode 700 or stricter).");
            }

            return;
        }

        if (Directory.Exists(directory))
        {
            ValidatePrivateWindowsDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var userSid = CurrentWindowsUserSid();
        security.SetOwner(userSid);
        foreach (var sid in AllowedWindowsSids(userSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
        ValidatePrivateWindowsDirectory(directory);
    }

    internal static void RestrictFilePermissions(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var userSid = CurrentWindowsUserSid();
            security.SetOwner(userSid);
            foreach (var sid in AllowedWindowsSids(userSid))
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, OwnerPrivateFile);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidatePrivateWindowsDirectory(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var userSid = CurrentWindowsUserSid();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(userSid))
        {
            throw new UnauthorizedAccessException(
                $"SQLite data directory '{Path.GetFullPath(directory)}' must be owned by the current user.");
        }

        var allowedSids = AllowedWindowsSids(userSid);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.IdentityReference is not SecurityIdentifier identity
                    || !allowedSids.Any(allowedSid => allowedSid.Equals(identity))))
            {
                throw new UnauthorizedAccessException(
                    $"SQLite data directory '{Path.GetFullPath(directory)}' grants access to an unapproved Windows identity.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] AllowedWindowsSids(SecurityIdentifier userSid) =>
    [
        userSid,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new UnauthorizedAccessException("Could not determine the current Windows user SID.");
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ReadSchemaVersionAsync(connection, cancellationToken);
        var latestVersion = Migrations[^1].Version;
        if (version > latestVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this application supports ({latestVersion}).");
        }

        foreach (var migration in Migrations.Where(migration => migration.Version > version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var currentVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
                if (currentVersion >= migration.Version)
                {
                    await transaction.CommitAsync(cancellationToken);
                    version = currentVersion;
                    continue;
                }

                if (currentVersion != migration.Version - 1)
                {
                    throw new InvalidDataException(
                        $"SQLite schema version {currentVersion} cannot apply migration {migration.Version}.");
                }

                var sql = ReadMigration(migration.ResourceName);
                await using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = sql;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);

                await using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = $"PRAGMA user_version = {migration.Version};";
                await versionCommand.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            version = migration.Version;
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadMigration(string resourceName)
    {
        using var stream = typeof(SqliteDatabase).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQLite migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task EnsureForeignKeysAreValidAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                $"SQLite foreign-key validation failed for table '{reader.GetString(0)}'.");
        }
    }
}
