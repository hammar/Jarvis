using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class SqlitePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EmptyDatabaseMigratesWithForeignKeysAndWalEnabled()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);

        await database.MigrateAsync();
        await database.MigrateAsync();

        await using var connection = await OpenAsync(file.DatabasePath);
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(connection, "SELECT MAX(Version) FROM SchemaMigrations;")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('Messages');")));
        var tableCount = Convert.ToInt64(await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN (
                'Conversations', 'Messages', 'Turns', 'TurnEvents', 'MemoryFacts',
                'MemoryProposals', 'ApprovalRequests', 'Actions', 'Jobs', 'JobRuns',
                'Notifications', 'CloudConsents', 'AuditEvents', 'MemoryFactsSearch');
            """));
        Assert.Equal(14L, tableCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConversationAndMemoryPersistAfterDatabaseReopenAndMemoryIsSearchable()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var conversationId = ConversationId.New();
        var message = new ConversationMessage(
            Guid.NewGuid(), conversationId, "user", "Remember that my workshop is in Seattle.", Now);
        var memoryId = MemoryFactId.New();
        var clock = new ControlledClock(Now);
        var conversations = new SqliteConversationStore(database);
        var memory = new SqliteMemoryStore(database, clock);

        await conversations.AppendMessageAsync(message, CancellationToken.None);
        var fact = await memory.SaveAsync(
            new MemoryFact(memoryId, "owner", "workshop", "Seattle", message.MessageId.ToString("D"), "Private", 0),
            expectedVersion: 0,
            CancellationToken.None);

        var reopened = new SqliteDatabase(file.DatabasePath);
        var history = await new SqliteConversationStore(reopened)
            .ReadRecentAsync(conversationId, 10, CancellationToken.None);
        var found = await new SqliteMemoryStore(reopened, clock)
            .SearchAsync("Seattle", 10, CancellationToken.None);

        Assert.Equal([message], history);
        Assert.Contains(found, result => result.Id == memoryId && result.Version == fact.Version);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MemoryCompareAndSwapRejectsAStaleVersionWithoutLosingTheNewValue()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var store = new SqliteMemoryStore(database, new ControlledClock(Now));
        var fact = new MemoryFact(MemoryFactId.New(), "owner", "location", "Seattle", "message-1", "Private", 0);
        await store.SaveAsync(fact, 0, CancellationToken.None);

        var updated = await store.SaveAsync(fact with { Value = "Portland" }, 1, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.SaveAsync(fact with { Value = "Boston" }, 1, CancellationToken.None));
        var persisted = await store.GetAsync(fact.Id, CancellationToken.None);

        Assert.Contains("memory fact", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Portland", persisted!.Value);
        Assert.Equal(2, updated.Version);
        Assert.Empty(await store.SearchAsync("Seattle", 5, CancellationToken.None));
        Assert.Single(await store.SearchAsync("Portland", 5, CancellationToken.None));
        await store.DeleteAsync(fact.Id, 2, CancellationToken.None);
        Assert.Empty(await store.SearchAsync("Portland", 5, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.DeleteAsync(fact.Id, 2, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentMemoryCompareAndSwapAllowsOnlyOneWriter()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var clock = new ControlledClock(Now);
        var firstStore = new SqliteMemoryStore(database, clock);
        var secondStore = new SqliteMemoryStore(new SqliteDatabase(file.DatabasePath), clock);
        var initial = new MemoryFact(
            MemoryFactId.New(), "owner", "location", "Seattle", "source", "Private", 0);
        await firstStore.SaveAsync(initial, 0, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> AttemptAsync(SqliteMemoryStore store, string value)
        {
            await start.Task;
            try
            {
                await store.SaveAsync(initial with { Value = value }, 1, CancellationToken.None);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var attempts = new[]
        {
            AttemptAsync(firstStore, "Portland"),
            AttemptAsync(secondStore, "Boston")
        };
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        var winner = await firstStore.GetAsync(initial.Id, CancellationToken.None);

        Assert.Single(results, result => result is null);
        Assert.IsType<PersistenceConcurrencyException>(Assert.Single(results, result => result is not null));
        Assert.Equal(2, winner!.Version);
        Assert.Contains(winner.Value, new[] { "Portland", "Boston" });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OlderSchemaUpgradeAddsSearchWithoutChangingExistingFacts()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var factId = MemoryFactId.New();
        await new SqliteMemoryStore(database, new ControlledClock(Now)).SaveAsync(
            new MemoryFact(factId, "owner", "vehicle", "blue", "source", "Private", 0),
            0,
            CancellationToken.None);

        await using (var connection = await OpenAsync(file.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER TR_ApprovalRequests_Owner_Insert;
                DROP TRIGGER TR_ApprovalRequests_Owner_Update;
                DROP INDEX UX_Actions_Id_OwnerId;
                DROP TRIGGER TR_MemoryFactsSearch_Insert;
                DROP TRIGGER TR_MemoryFactsSearch_Delete;
                DROP TRIGGER TR_MemoryFactsSearch_Update;
                DROP TABLE MemoryFactsSearch;
                DELETE FROM SchemaMigrations WHERE Version > 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var oldSchemaBackup = Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "old-schema-backup.db");
        await database.BackupAsync(oldSchemaBackup);
        var restoredPath = Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "restored-old-schema.db");
        var restoredDatabase = new SqliteDatabase(restoredPath);
        await restoredDatabase.MigrateAsync();
        await restoredDatabase.RestoreAsync(oldSchemaBackup);
        await restoredDatabase.MigrateAsync();

        var results = await new SqliteMemoryStore(restoredDatabase, new ControlledClock(Now))
            .SearchAsync("blue", 5, CancellationToken.None);
        Assert.Contains(results, fact => fact.Id == factId && fact.Value == "blue");
        Assert.Equal(3L, Convert.ToInt64(await ScalarAtAsync(
            restoredPath, "SELECT MAX(Version) FROM SchemaMigrations;")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MigrationRejectsFutureSchemasAndRollsBackFailedMigrations()
    {
        using var file = IsolatedDatabaseFile.Create();
        var futurePath = Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "future.db");
        await using (var connection = await OpenAsync(futurePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                INSERT INTO SchemaMigrations VALUES(99, 'future', '2026-01-01');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteDatabase(futurePath).MigrateAsync());

        var conflictingPath = Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "conflicting.db");
        await using (var connection = await OpenAsync(conflictingPath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                CREATE TABLE Conversations(Id TEXT PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            new SqliteDatabase(conflictingPath).MigrateAsync());
        Assert.Equal(0L, Convert.ToInt64(await ScalarAtAsync(
            conflictingPath, "SELECT COUNT(*) FROM SchemaMigrations;")));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAtAsync(
            conflictingPath, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Messages';")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OnlineBackupRestoresDataAfterTheOriginalFileIsCorrupted()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var conversationId = ConversationId.New();
        await new SqliteConversationStore(database).AppendMessageAsync(
            new ConversationMessage(Guid.NewGuid(), conversationId, "assistant", "Still here.", Now),
            CancellationToken.None);
        var backupPath = Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "backup.db");

        await database.BackupAsync(backupPath);
        await File.WriteAllTextAsync(file.DatabasePath, "not a database");
        await database.RestoreAsync(backupPath);

        var messages = await new SqliteConversationStore(new SqliteDatabase(file.DatabasePath))
            .ReadRecentAsync(conversationId, 5, CancellationToken.None);
        Assert.Equal("Still here.", Assert.Single(messages).Content);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestoreRejectsUnversionedFutureAndForeignKeyInvalidDatabases()
    {
        using var file = IsolatedDatabaseFile.Create();
        var directory = Path.GetDirectoryName(file.DatabasePath)!;
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var original = new ConversationMessage(Guid.NewGuid(), ConversationId.New(), "user", "preserve", Now);
        await new SqliteConversationStore(database).AppendMessageAsync(original, CancellationToken.None);
        var unversioned = Path.Combine(directory, "unversioned.db");
        await using (await OpenAsync(unversioned))
        {
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreAsync(unversioned));

        var future = Path.Combine(directory, "future-backup.db");
        await using (var connection = await OpenAsync(future))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                INSERT INTO SchemaMigrations VALUES(99, 'future', '2026-01-01');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreAsync(future));

        var incompleteHistory = Path.Combine(directory, "incomplete-history.db");
        await using (var connection = await OpenAsync(incompleteHistory))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                INSERT INTO SchemaMigrations VALUES(3, 'approval action owner binding', '2026-01-01');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreAsync(incompleteHistory));

        var missingSchema = Path.Combine(directory, "missing-schema.db");
        await using (var connection = await OpenAsync(missingSchema))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                INSERT INTO SchemaMigrations VALUES(1, 'initial durable state', '2026-01-01');
                INSERT INTO SchemaMigrations VALUES(2, 'memory full-text search', '2026-01-02');
                INSERT INTO SchemaMigrations VALUES(3, 'approval action owner binding', '2026-01-03');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreAsync(missingSchema));

        var broken = Path.Combine(directory, "foreign-key-invalid.db");
        await using (var connection = await OpenAsync(broken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT, AppliedAtUtc TEXT);
                INSERT INTO SchemaMigrations VALUES(1, 'old', '2026-01-01');
                CREATE TABLE Parent(Id INTEGER PRIMARY KEY);
                CREATE TABLE Child(ParentId INTEGER REFERENCES Parent(Id));
                INSERT INTO Child VALUES(404);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.RestoreAsync(broken));
        var stillThere = await new SqliteConversationStore(database)
            .ReadRecentAsync(original.ConversationId, 5, CancellationToken.None);
        Assert.Equal(original, Assert.Single(stillThere));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackupAndRestoreRejectSymlinkedPathAliasesWithoutChangingData()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var message = new ConversationMessage(Guid.NewGuid(), ConversationId.New(), "user", "unchanged", Now);
        await new SqliteConversationStore(database).AppendMessageAsync(message, CancellationToken.None);
        var directory = Path.GetDirectoryName(file.DatabasePath)!;
        var alias = Path.Combine(directory, "directory-alias");
        Directory.CreateSymbolicLink(alias, directory);
        var aliasedDatabase = Path.Combine(alias, Path.GetFileName(file.DatabasePath));

        await Assert.ThrowsAsync<ArgumentException>(() => database.BackupAsync(aliasedDatabase));
        await Assert.ThrowsAsync<ArgumentException>(() => database.RestoreAsync(aliasedDatabase));
        var result = await new SqliteConversationStore(database)
            .ReadRecentAsync(message.ConversationId, 5, CancellationToken.None);
        Assert.Equal(message, Assert.Single(result));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DefaultRetentionExpiresOldHistoryAndAuditButPreservesFactsJobsAndUnknownActions()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var conversations = new SqliteConversationStore(database);
        var memory = new SqliteMemoryStore(database, new ControlledClock(Now));
        var journal = new SqliteJournalStore(database);
        var jobs = new SqliteJobStore(database, new ControlledClock(Now));
        var oldConversation = ConversationId.New();
        var recentConversation = ConversationId.New();
        var oldMessage = Message(oldConversation, Now.AddDays(-91));
        await conversations.AppendMessageAsync(oldMessage, CancellationToken.None);
        await conversations.AppendMessageAsync(Message(recentConversation, Now.AddDays(-89)), CancellationToken.None);
        var pendingConversation = ConversationId.New();
        await conversations.AppendMessageAsync(Message(pendingConversation, Now.AddDays(-91)), CancellationToken.None);
        await using (var connection = await OpenAsync(file.DatabasePath))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Turns(TurnId, ConversationId, Status, CreatedAtUtc, Version)
                VALUES ($turnId, $conversationId, 'WaitingForApproval', $createdAt, 1);
                """;
            command.Parameters.AddWithValue("$turnId", TurnId.New().Value.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", pendingConversation.Value.ToString("D"));
            command.Parameters.AddWithValue("$createdAt", Now.AddDays(-91).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var fact = new MemoryFact(
            MemoryFactId.New(), "owner", "preference", "tea", oldMessage.MessageId.ToString("D"), "Private", 0);
        await memory.SaveAsync(fact, 0, CancellationToken.None);
        await journal.AppendAuditEventAsync(Audit("old", Now.AddDays(-31)), CancellationToken.None);
        await journal.AppendAuditEventAsync(Audit("recent", Now.AddDays(-29)), CancellationToken.None);
        var unknownAction = new SqliteActionRecord(
            ActionId.New(), "owner", "home.set_light", """{"entity":"light.office"}""",
            "request-hash", "Unknown", Now.AddDays(-120), 1);
        await journal.SaveActionAsync(unknownAction, CancellationToken.None);
        var jobId = JobId.New();
        await jobs.ScheduleAsync(new SqliteScheduledJob(
            jobId, "owner", "reminder", 1, """{"message":"due"}""", "UTC",
            Now, "NotifyLate", null), CancellationToken.None);
        var retention = new SqliteRetention(database);

        var first = await retention.ExpireAsync(new ControlledClock(Now), CancellationToken.None);
        var second = await retention.ExpireAsync(new ControlledClock(Now), CancellationToken.None);

        Assert.Equal(1, first.Conversations);
        Assert.Equal(1, first.AuditEvents);
        Assert.Equal(new RetentionResult(0, 0), second);
        Assert.Empty(await conversations.ReadRecentAsync(oldConversation, 5, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(recentConversation, 5, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(pendingConversation, 5, CancellationToken.None));
        Assert.Equal("redacted:conversation-retention", (await memory.GetAsync(fact.Id, CancellationToken.None))!.SourceId);
        Assert.Equal("Unknown", await TextAsync(
            file.DatabasePath, "SELECT Status FROM Actions WHERE Id = $id;", ("$id", unknownAction.Id.Value.ToString("D"))));
        Assert.Equal(jobId.Value.ToString("D"), await TextAsync(
            file.DatabasePath, "SELECT Id FROM Jobs WHERE Id = $id;", ("$id", jobId.Value.ToString("D"))));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerRetentionOverridesExpireOnlyRecordsOutsideTheirWindows()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var store = new SqliteConversationStore(database);
        var expiredConversation = ConversationId.New();
        var retainedConversation = ConversationId.New();
        await store.AppendMessageAsync(Message(expiredConversation, Now.AddDays(-8)), CancellationToken.None);
        await store.AppendMessageAsync(Message(retainedConversation, Now.AddDays(-6)), CancellationToken.None);
        var journal = new SqliteJournalStore(database);
        await journal.AppendAuditEventAsync(Audit("expired", Now.AddDays(-4)), CancellationToken.None);
        await journal.AppendAuditEventAsync(Audit("retained", Now.AddDays(-2)), CancellationToken.None);

        var result = await new SqliteRetention(
                database, TimeSpan.FromDays(7), TimeSpan.FromDays(3))
            .ExpireAsync(new ControlledClock(Now), CancellationToken.None);

        Assert.Equal(new RetentionResult(1, 1), result);
        Assert.Empty(await store.ReadRecentAsync(expiredConversation, 5, CancellationToken.None));
        Assert.Single(await store.ReadRecentAsync(retainedConversation, 5, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task JobLeaseIsDurableAndCannotBeCompletedByAStaleWorker()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var clock = new ControlledClock(Now);
        var store = new SqliteJobStore(database, clock);
        var jobId = JobId.New();
        await store.ScheduleAsync(new SqliteScheduledJob(
            jobId, "owner", "reminder", 1, """{"message":"hello"}""", "UTC", Now,
            "NotifyLate", "request-1"), CancellationToken.None);
        var lease = await new SqliteJobStore(new SqliteDatabase(file.DatabasePath), clock)
            .ClaimDueAsync("worker-1", Now, TimeSpan.FromMinutes(1), CancellationToken.None);

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.CompleteAsync(lease! with { LeaseOwner = "worker-2" }, "Succeeded", CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(2);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.CompleteAsync(lease!, "Succeeded", CancellationToken.None));
        Assert.Equal(0L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT COUNT(*) FROM JobRuns WHERE JobId = $id;", ("$id", jobId.Value.ToString("D")))));
        Assert.Equal(1L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT Enabled FROM Jobs WHERE Id = $id;", ("$id", jobId.Value.ToString("D")))));

        var reclaimed = await store.ClaimDueAsync(
            "worker-2", clock.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.CompleteAsync(lease!, "Succeeded", CancellationToken.None));
        await store.CompleteAsync(reclaimed!, "Retryable", CancellationToken.None);
        Assert.Equal(1L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT Enabled FROM Jobs WHERE Id = $id;", ("$id", jobId.Value.ToString("D")))));
        Assert.Equal("Retryable", await TextAsync(
            file.DatabasePath, "SELECT Status FROM JobRuns WHERE JobId = $id;", ("$id", jobId.Value.ToString("D"))));

        clock.UtcNow = Now.AddMinutes(3);
        var retry = await store.ClaimDueAsync("worker-3", clock.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.CompleteAsync(retry!, "Unknown", CancellationToken.None);
        var duplicateId = await store.ScheduleAsync(new SqliteScheduledJob(
            JobId.New(), "owner", "reminder", 1, """{"message":"different"}""", "UTC", Now,
            "NotifyLate", "request-1"), CancellationToken.None);

        Assert.Equal(jobId, duplicateId);
        Assert.Equal("Unknown", await TextAsync(
            file.DatabasePath, "SELECT Status FROM JobRuns WHERE JobId = $id;", ("$id", jobId.Value.ToString("D"))));
        Assert.Equal(1L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT COUNT(*) FROM JobRuns WHERE JobId = $id;", ("$id", jobId.Value.ToString("D")))));
        Assert.Null(await store.ClaimDueAsync("worker-4", Now.AddMinutes(5), TimeSpan.FromMinutes(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CompleteAsync(retry!, "unexpected", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task JournalUpdatesAreOwnerScopedVersionedAndForeignKeyBound()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();
        var journal = new SqliteJournalStore(database);
        var action = new SqliteActionRecord(
            ActionId.New(), "owner", "home.set_light", """{"entity":"light.office"}""",
            "hash", "Prepared", Now, 1);
        await journal.SaveActionAsync(action, CancellationToken.None);
        await journal.UpdateActionAsync(action.Id, 1, "Unknown", Now, CancellationToken.None);
        var approval = new SqliteApprovalRecord(
            ApprovalId.New(), action.Id, "owner", Now.AddMinutes(5), Now, "Pending", 1);
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await journal.SaveApprovalAsync(approval with { Id = ApprovalId.New(), OwnerId = "different-owner" }, CancellationToken.None));
        await journal.SaveApprovalAsync(approval, CancellationToken.None);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await journal.UpdateApprovalAsync(approval.Id, "different-owner", 1, "Approved", CancellationToken.None));
        Assert.Equal("Pending", await TextAsync(
            file.DatabasePath, "SELECT Status FROM ApprovalRequests WHERE Id = $id;", ("$id", approval.Id.Value.ToString("D"))));
        Assert.Equal(1L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT Version FROM ApprovalRequests WHERE Id = $id;", ("$id", approval.Id.Value.ToString("D")))));
        await journal.UpdateApprovalAsync(approval.Id, "owner", 1, "Approved", CancellationToken.None);

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await journal.UpdateApprovalAsync(approval.Id, "owner", 2, "Rejected", CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await journal.SaveApprovalAsync(
                approval with { Id = ApprovalId.New(), ActionId = ActionId.New() }, CancellationToken.None));

        Assert.Equal("Unknown", await TextAsync(
            file.DatabasePath, "SELECT Status FROM Actions WHERE Id = $id;", ("$id", action.Id.Value.ToString("D"))));
        Assert.Equal("Approved", await TextAsync(
            file.DatabasePath, "SELECT Status FROM ApprovalRequests WHERE Id = $id;", ("$id", approval.Id.Value.ToString("D"))));
        Assert.Equal(2L, Convert.ToInt64(await TextAsync(
            file.DatabasePath, "SELECT Version FROM ApprovalRequests WHERE Id = $id;", ("$id", approval.Id.Value.ToString("D")))));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await journal.UpdateActionAsync(action.Id, 1, "Succeeded", Now, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvalidRetentionWindowsAndBackupPathsAreRejected()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.DatabasePath);
        await database.MigrateAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SqliteRetention(database, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentException>(() => database.BackupAsync(file.DatabasePath));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            database.RestoreAsync(Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "missing.db")));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new SqliteDatabase(Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "uninitialized.db"))
                .BackupAsync(Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "uninitialized-backup.db")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new SqliteConversationStore(database).ReadRecentAsync(ConversationId.New(), 0, CancellationToken.None));
        var memory = new SqliteMemoryStore(database, new ControlledClock(Now));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await memory.SearchAsync("  ", 5, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await memory.SearchAsync("word", 101, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await memory.SaveAsync(
                new MemoryFact(MemoryFactId.New(), "subject", "key", "value", "source", "Private", 0),
                -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await memory.DeleteAsync(MemoryFactId.New(), 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new SqliteJobStore(database, new ControlledClock(Now))
                .ClaimDueAsync("worker", Now, TimeSpan.Zero, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteDatabase(Path.Combine(Path.GetDirectoryName(file.DatabasePath)!, "cancelled.db"))
                .MigrateAsync(cancelled.Token));
    }

    private static ConversationMessage Message(ConversationId conversationId, DateTimeOffset at) =>
        new(Guid.NewGuid(), conversationId, "user", "retention fixture", at);

    private static SqliteAuditEvent Audit(string id, DateTimeOffset at) =>
        new(Guid.NewGuid(), "owner", id, null, "{}", at);

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = false,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<object?> ScalarAtAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        return await ScalarAsync(connection, sql);
    }

    private static async Task<string?> TextAsync(string path, string sql, (string Name, object Value) parameter)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ControlledClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
