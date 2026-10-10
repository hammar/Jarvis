using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.Web;
using PersonalAgent.TestSupport;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class SqlitePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SuccessfulLoginUpgradesOutdatedVerifierWithoutOverwritingNewerVerifier()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var accounts = new SqliteOwnerStateStore(database);
        var identity = new OwnerIdentity("owner");
        var oldHasher = new PasswordHasher<OwnerIdentity>(Options.Create(new PasswordHasherOptions
        {
            IterationCount = 1000
        }));
        var passphrase = "a-long-owner-passphrase";
        var oldHash = oldHasher.HashPassword(identity, passphrase);
        await accounts.TryCreateOwnerAsync(oldHash, Now, CancellationToken.None);
        var currentHasher = new PasswordHasher<OwnerIdentity>();
        var authentication = new OwnerAuthenticationService(accounts, currentHasher, new MutableClock(Now));

        Assert.False(await authentication.VerifyAsync("wrong-passphrase", CancellationToken.None));
        Assert.Equal(oldHash, await accounts.GetPasswordHashAsync(CancellationToken.None));
        Assert.True(await authentication.VerifyAsync(passphrase, CancellationToken.None));
        var upgraded = await accounts.GetPasswordHashAsync(CancellationToken.None);
        Assert.NotEqual(oldHash, upgraded);
        Assert.Equal(PasswordVerificationResult.Success, currentHasher.VerifyHashedPassword(identity, upgraded!, passphrase));
        Assert.False(await accounts.TryUpgradePasswordHashAsync(oldHash, "stale-replacement", CancellationToken.None));
        Assert.Equal(upgraded, await accounts.GetPasswordHashAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await accounts.TryUpgradePasswordHashAsync(upgraded!, new string('x', 4097), CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public async Task VerifierUpgradeRaceRechecksTheWinningPassword(bool samePassword)
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var accounts = new SqliteOwnerStateStore(database);
        var identity = new OwnerIdentity("owner");
        var oldHasher = new PasswordHasher<OwnerIdentity>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        var hasher = new PasswordHasher<OwnerIdentity>();
        const string passphrase = "a-long-owner-passphrase";
        var oldHash = oldHasher.HashPassword(identity, passphrase);
        await accounts.TryCreateOwnerAsync(oldHash, Now, CancellationToken.None);
        var winningHash = hasher.HashPassword(identity, samePassword ? passphrase : "a-different-owner-passphrase");
        var competing = new CompetingVerifierStore(accounts, winningHash);
        var authentication = new OwnerAuthenticationService(competing, hasher, new MutableClock(Now));

        Assert.Equal(samePassword, await authentication.VerifyAsync(passphrase, CancellationToken.None));
        Assert.Equal(winningHash, await accounts.GetPasswordHashAsync(CancellationToken.None));
    }

    private sealed class CompetingVerifierStore(IOwnerAccountStore accounts, string winningHash) : IOwnerAccountStore
    {
        public ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
            accounts.IsConfiguredAsync(cancellationToken);

        public ValueTask<bool> TryCreateOwnerAsync(string passwordHash, DateTimeOffset createdAtUtc, CancellationToken cancellationToken) =>
            accounts.TryCreateOwnerAsync(passwordHash, createdAtUtc, cancellationToken);

        public ValueTask<string?> GetPasswordHashAsync(CancellationToken cancellationToken) =>
            accounts.GetPasswordHashAsync(cancellationToken);

        public async ValueTask<bool> TryUpgradePasswordHashAsync(
            string expectedHash, string replacementHash, CancellationToken cancellationToken)
        {
            Assert.True(await accounts.TryUpgradePasswordHashAsync(expectedHash, winningHash, cancellationToken));
            return await accounts.TryUpgradePasswordHashAsync(expectedHash, replacementHash, cancellationToken);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DatabaseReadinessChecksSchemaWithoutCreatingMissingDatabase()
    {
        using var directory = IsolatedDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "readiness.db");
        var missing = new SqliteDatabase(databasePath);

        Assert.False(await missing.CheckReadinessAsync());
        Assert.False(File.Exists(databasePath));

        await missing.InitializeAsync();

        Assert.True(await missing.CheckReadinessAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetentionReadsReturnOnlyCommittedPairsDuringConcurrentUpdates()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteOwnerStateStore(database);
        var first = new RetentionSettings(111, 222);
        var second = new RetentionSettings(333, 444);
        await store.UpdateRetentionAsync(first, Now, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 100; i++)
            {
                await store.UpdateRetentionAsync(i % 2 == 0 ? second : first, Now, CancellationToken.None);
                await Task.Yield();
            }
        });
        var reader = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 200; i++)
            {
                var settings = await store.GetRetentionAsync(new RetentionSettings(90, 30), CancellationToken.None);
                Assert.True(settings == first || settings == second, $"Read an uncommitted retention pair: {settings}");
                await Task.Yield();
            }
        });
        start.SetResult();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));

        await using var connection = await database.OpenConnectionAsync();
        await using var remove = connection.CreateCommand();
        remove.CommandText = "DELETE FROM owner_settings WHERE setting_key = 'audit_retention_days';";
        await remove.ExecuteNonQueryAsync();
        Assert.Equal(new RetentionSettings(first.ConversationDays, 30),
            await store.GetRetentionAsync(new RetentionSettings(90, 30), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerVerifierAndRetentionSettingsSurviveDatabaseReopen()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var owner = new SqliteOwnerStateStore(database);

        Assert.False(await owner.IsConfiguredAsync(CancellationToken.None));
        Assert.True(await owner.TryCreateOwnerAsync("adaptive-verifier", Now, CancellationToken.None));
        Assert.False(await owner.TryCreateOwnerAsync("replacement-verifier", Now, CancellationToken.None));
        Assert.Equal("adaptive-verifier", await owner.GetPasswordHashAsync(CancellationToken.None));

        var defaults = new RetentionSettings(90, 30);
        Assert.Equal(defaults, await owner.GetRetentionAsync(defaults, CancellationToken.None));
        var saved = new RetentionSettings(42, 21);
        await owner.UpdateRetentionAsync(saved, Now, CancellationToken.None);

        var reopened = new SqliteOwnerStateStore(new SqliteDatabase(file.Path));
        Assert.Equal(saved, await reopened.GetRetentionAsync(defaults, CancellationToken.None));
        Assert.True(await reopened.IsConfiguredAsync(CancellationToken.None));
        await using var connection = await new SqliteDatabase(file.Path).OpenConnectionAsync();
        Assert.Equal(4, await UserVersionAsync(connection));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConversationQueriesEnforceOwnerIdentity()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new MutableClock(Now));
        var conversation = new ConversationRecord(
            ConversationId.New(),
            "owner",
            "Local chat",
            Now,
            Now);
        await store.CreateConversationAsync(conversation, CancellationToken.None);

        Assert.Equal(conversation, await store.GetConversationAsync(
            conversation.Id,
            "owner",
            CancellationToken.None));
        Assert.Null(await store.GetConversationAsync(conversation.Id, "different-owner", CancellationToken.None));
        Assert.Equal(
            [conversation],
            await store.ReadRecentConversationsAsync("owner", 10, CancellationToken.None));
        Assert.Empty(await store.ReadRecentConversationsAsync("different-owner", 10, CancellationToken.None));
        var turn = await store.CreateTurnAsync(TurnId.New(), conversation.Id, TurnStatus.Received, Now, CancellationToken.None);
        Assert.Equal(turn, await store.GetTurnForOwnerAsync(turn.Id, "owner", CancellationToken.None));
        Assert.Null(await store.GetTurnForOwnerAsync(turn.Id, "different-owner", CancellationToken.None));
        Assert.Equal([turn], await store.ReadRecentTurnsForOwnerAsync("owner", 10, CancellationToken.None));
        Assert.Empty(await store.ReadRecentTurnsForOwnerAsync("different-owner", 10, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DataDirectoryUsesConfiguredPathOrValidatedPerUserFallback()
    {
        Assert.Equal("/owner/data", SqliteDataDirectory.Resolve("/owner/data", "/user/local"));
        Assert.Equal(
            System.IO.Path.Combine("/user/local", "Jarvis"),
            SqliteDataDirectory.Resolve(null, "/user/local"));
        Assert.Equal(
            System.IO.Path.Combine("/user/local", "Jarvis"),
            SqliteDataDirectory.Resolve("  ", "/user/local"));
        Assert.Throws<InvalidOperationException>(() => SqliteDataDirectory.Resolve(null, " "));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DataDirectoryPreservesLegacyAppHostStateAndRejectsAmbiguousDefaults()
    {
        using var directory = IsolatedDirectory.Create();
        var previousAppHostDirectory = Path.Combine(directory.Path, "PersonalAgent");
        Directory.CreateDirectory(previousAppHostDirectory);
        File.WriteAllText(Path.Combine(previousAppHostDirectory, "jarvis.db"), "legacy");
        Assert.Equal(
            previousAppHostDirectory,
            SqliteDataDirectory.Resolve(null, directory.Path));

        var currentDirectory = Path.Combine(directory.Path, "Jarvis");
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(Path.Combine(currentDirectory, "jarvis.db"), "current");
        Assert.Throws<InvalidOperationException>(
            () => SqliteDataDirectory.Resolve(null, directory.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MigrationsEnableWalAndReopenedDatabaseRetainsMessages()
    {
        using var file = IsolatedDatabaseFile.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();

        var store = new SqliteConversationStore(database, clock);
        var conversationId = ConversationId.New();
        var message = Message(conversationId, "First durable message.");
        await store.AppendMessageAsync(message, CancellationToken.None);

        var reopened = new SqliteDatabase(file.Path);
        await reopened.InitializeAsync();
        var read = await new SqliteConversationStore(reopened, clock)
            .ReadRecentAsync(conversationId, 10, CancellationToken.None);

        Assert.Equal([message], read);
        await using var connection = await reopened.OpenConnectionAsync();
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await mode.ExecuteScalarAsync());
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, (long)(await foreignKeys.ExecuteScalarAsync())!);
        Assert.Equal(4, await UserVersionAsync(connection));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MigrationFromOlderSchemaPreservesExistingConversationData()
    {
        using var file = IsolatedDatabaseFile.Create();
        var conversationId = ConversationId.New();
        var messageId = Guid.NewGuid();
        await CreateVersionOneFixtureAsync(file.Path, conversationId, messageId);

        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var messages = await new SqliteConversationStore(database, new MutableClock(Now))
            .ReadRecentAsync(conversationId, 10, CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal(messageId, messages[0].MessageId);
        Assert.Equal("before upgrade", messages[0].Content);
        await using var migrated = await database.OpenConnectionAsync();
        Assert.Equal(4, await UserVersionAsync(migrated));
        await using var table = migrated.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'cloud_consents';";
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpgradedVersionOneSchemaSupportsWritesConstraintsAndRetention()
    {
        using var file = IsolatedDatabaseFile.Create();
        var conversationId = ConversationId.New();
        await CreateVersionOneFixtureAsync(file.Path, conversationId, Guid.NewGuid());
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var store = new SqliteConversationStore(database, clock);
        var appended = Message(conversationId, "after upgrade");
        await store.AppendMessageAsync(appended, CancellationToken.None);
        var turnId = TurnId.New();
        var turn = await store.CreateTurnAsync(
            turnId, conversationId, TurnStatus.Received, Now, CancellationToken.None);
        await store.AppendTurnEventAsync(turnId, "turn.started", "{}", Now, CancellationToken.None);
        await store.UpdateTurnStatusAsync(
            turnId, TurnStatus.Completed, turn.Version, Now, CancellationToken.None);

        await using (var connection = await database.OpenConnectionAsync())
        await using (var invalid = connection.CreateCommand())
        {
            invalid.CommandText = """
                INSERT INTO messages
                    (message_id, conversation_id, sequence, role, content, created_at_utc)
                VALUES ($id, $conversation, 1, 'user', 'duplicate sequence', $now);
                """;
            invalid.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            invalid.Parameters.AddWithValue("$conversation", conversationId.Value.ToString("D"));
            invalid.Parameters.AddWithValue("$now", Now.ToString("O"));
            var duplicate = await Assert.ThrowsAsync<SqliteException>(
                async () => await invalid.ExecuteNonQueryAsync());
            Assert.Equal(19, duplicate.SqliteErrorCode);
            invalid.CommandText = "UPDATE turns SET version = 0 WHERE id = $id;";
            invalid.Parameters["$id"].Value = turnId.Value.ToString("D");
            var invalidVersion = await Assert.ThrowsAsync<SqliteException>(
                async () => await invalid.ExecuteNonQueryAsync());
            Assert.Equal(19, invalidVersion.SqliteErrorCode);
            invalid.CommandText = "UPDATE messages SET role = 'invalid' WHERE message_id = $id;";
            invalid.Parameters["$id"].Value = appended.MessageId.ToString("D");
            var invalidRole = await Assert.ThrowsAsync<SqliteException>(
                async () => await invalid.ExecuteNonQueryAsync());
            Assert.Equal(19, invalidRole.SqliteErrorCode);
        }

        Assert.Equal(2, (await store.ReadRecentAsync(conversationId, 10, CancellationToken.None)).Count);
        clock.UtcNow = Now.AddDays(91);
        var deleted = await new SqliteRetentionService(database, clock, new SqliteRetentionOptions())
            .CleanupExpiredAsync(CancellationToken.None);
        Assert.Equal(1, deleted.ConversationsDeleted);
        Assert.Empty(await store.ReadRecentAsync(conversationId, 10, CancellationToken.None));
        Assert.Null(await store.GetTurnAsync(turnId, CancellationToken.None));
        await using var verify = await database.OpenConnectionAsync();
        await using var events = verify.CreateCommand();
        events.CommandText = "SELECT COUNT(*) FROM turn_events;";
        Assert.Equal(0L, (long)(await events.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ValidateUpgradeOnCopyAppliesPendingMigrationWithoutChangingLiveDatabase()
    {
        using var file = IsolatedDatabaseFile.Create();
        var conversationId = ConversationId.New();
        var messageId = Guid.NewGuid();
        await CreateVersionOneFixtureAsync(file.Path, conversationId, messageId);

        await new SqliteBackupRestoreService(new SqliteDatabase(file.Path)).ValidateUpgradeOnCopyAsync();

        await using var live = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file.Path
        }.ToString());
        await live.OpenAsync();
        Assert.Equal(1, await UserVersionAsync(live));
        await using var preservedData = live.CreateCommand();
        preservedData.CommandText = "SELECT content FROM messages WHERE message_id = $id;";
        preservedData.Parameters.AddWithValue("$id", messageId.ToString("D"));
        Assert.Equal("before upgrade", (string?)await preservedData.ExecuteScalarAsync());
        await using var unchangedSchema = live.CreateCommand();
        unchangedSchema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'cloud_consents';";
        Assert.Equal(0L, (long)(await unchangedSchema.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ValidateUpgradeOnCopyRejectsConflictingSchemaWithoutChangingOriginal()
    {
        using var file = IsolatedDatabaseFile.Create();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file.Path
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var createConflict = connection.CreateCommand();
            createConflict.CommandText = """
                CREATE TABLE conversations (id TEXT PRIMARY KEY);
                INSERT INTO conversations VALUES ('original');
                PRAGMA user_version = 0;
                """;
            await createConflict.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(
            () => new SqliteBackupRestoreService(new SqliteDatabase(file.Path)).ValidateUpgradeOnCopyAsync());

        await using var original = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file.Path
        }.ToString());
        await original.OpenAsync();
        Assert.Equal(0, await UserVersionAsync(original));
        await using var rows = original.CreateCommand();
        rows.CommandText = "SELECT id FROM conversations;";
        Assert.Equal("original", (string?)await rows.ExecuteScalarAsync());
        await using var absent = original.CreateCommand();
        absent.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'memory_facts';";
        Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConversationTurnsAndEventsPersistWithOrderedReconnectCursors()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new MutableClock(Now));
        var conversationId = ConversationId.New();
        var turnId = TurnId.New();
        var turn = await store.CreateTurnAsync(turnId, conversationId, TurnStatus.Received, Now, CancellationToken.None);
        var first = await store.AppendTurnEventAsync(turnId, "turn.started", """{"status":"Received"}""", Now, CancellationToken.None);
        var second = await store.AppendTurnEventAsync(turnId, "turn.completed", """{"status":"Completed"}""", Now.AddSeconds(1), CancellationToken.None);
        var running = await store.UpdateTurnStatusAsync(turnId, TurnStatus.Completed, turn.Version, Now.AddSeconds(1), CancellationToken.None);

        var reopened = new SqliteConversationStore(new SqliteDatabase(file.Path), new MutableClock(Now));
        var page = await reopened.ReadTurnEventsAfterAsync(turnId, first.Sequence, 10, CancellationToken.None);
        var beginning = await reopened.ReadTurnEventsAfterAsync(turnId, 0, 1, CancellationToken.None);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(TurnStatus.Completed, running.Status);
        Assert.Equal(2, running.Version);
        Assert.Equal([second], page);
        Assert.Equal([first], beginning);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await reopened.UpdateTurnStatusAsync(turnId, TurnStatus.Failed, turn.Version, Now, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TurnSubmissionIsAtomicAndDeduplicatedAcrossStoreReopen()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var conversationId = ConversationId.New();
        var requestId = "client-turn-001";
        var text = "What time is it?";
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var firstStore = new SqliteConversationStore(database, new MutableClock(Now));
        var first = await firstStore.SubmitTurnAsync(
            new ConversationTurnSubmission(
                TurnId.New(),
                conversationId,
                requestId,
                fingerprint,
                Guid.NewGuid(),
                text,
                Now),
            CancellationToken.None);

        var reopened = new SqliteConversationStore(new SqliteDatabase(file.Path), new MutableClock(Now));
        var retry = await reopened.SubmitTurnAsync(
            new ConversationTurnSubmission(
                TurnId.New(),
                conversationId,
                requestId,
                fingerprint,
                Guid.NewGuid(),
                text,
                Now.AddSeconds(1)),
            CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(retry.IsDuplicate);
        Assert.Equal(first.Turn, retry.Turn);
        Assert.Equal(first.UserMessageId, retry.UserMessageId);
        Assert.Single(await reopened.ReadRecentAsync(conversationId, 10, CancellationToken.None));
        Assert.Single(await reopened.ReadTurnEventsAfterAsync(first.Turn.Id, 0, 10, CancellationToken.None));
        await Assert.ThrowsAsync<TurnRequestConflictException>(async () =>
            await reopened.SubmitTurnAsync(
                new ConversationTurnSubmission(
                    TurnId.New(),
                    conversationId,
                    requestId,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("different request"))),
                    Guid.NewGuid(),
                    "different request",
                    Now.AddSeconds(2)),
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerScopedSubmissionRequiresTheExistingOwnedConversationAtomically()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var store = new SqliteConversationStore(database, clock);
        var retention = new SqliteRetentionService(database, clock, new SqliteRetentionOptions());
        const string text = "Owner scoped request";
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        ConversationTurnSubmission Submission(ConversationId id, string requestId, string? owner) =>
            new(TurnId.New(), id, requestId, fingerprint, Guid.NewGuid(), text, Now, owner);

        var missing = ConversationId.New();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SubmitTurnAsync(Submission(missing, "invalid", " "), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.FindSubmittedTurnAsync(missing, "invalid", fingerprint, CancellationToken.None, new string('o', 129)));
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.SubmitTurnAsync(Submission(missing, "missing", "owner"), CancellationToken.None));
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.FindSubmittedTurnAsync(missing, "missing", fingerprint, CancellationToken.None, "owner"));
        Assert.Null(await store.GetConversationAsync(missing, "owner", CancellationToken.None));

        var owned = await store.CreateConversationAsync(
            new ConversationRecord(ConversationId.New(), "owner", "Owned", Now.AddDays(-400), Now.AddDays(-400)),
            CancellationToken.None);
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.SubmitTurnAsync(Submission(owned.Id, "foreign", "intruder"), CancellationToken.None));
        Assert.Empty(await store.ReadRecentAsync(owned.Id, 10, CancellationToken.None));
        Assert.Equal(owned.UpdatedAtUtc, (await store.GetConversationAsync(owned.Id, "owner", CancellationToken.None))!.UpdatedAtUtc);

        var accepted = await store.SubmitTurnAsync(Submission(owned.Id, "accepted", "owner"), CancellationToken.None);
        Assert.False(accepted.IsDuplicate);
        var duplicate = await store.SubmitTurnAsync(Submission(owned.Id, "accepted", "owner"), CancellationToken.None);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(accepted.Turn, duplicate.Turn);
        Assert.Equal(accepted.Turn, (await store.FindSubmittedTurnAsync(
            owned.Id, "accepted", fingerprint, CancellationToken.None, "owner"))!.Turn);
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.FindSubmittedTurnAsync(owned.Id, "accepted", fingerprint, CancellationToken.None, "intruder"));
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.SubmitTurnAsync(Submission(owned.Id, "accepted", "intruder"), CancellationToken.None));

        // The accepted nonterminal turn and refreshed update time keep cleanup from removing the conversation.
        clock.UtcNow = Now.AddDays(3700);
        Assert.Equal(new SqliteRetentionResult(0, 0), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.NotNull(await store.GetConversationAsync(owned.Id, "owner", CancellationToken.None));

        var expired = await store.CreateConversationAsync(
            new ConversationRecord(ConversationId.New(), "owner", "Expired", Now.AddDays(-400), Now.AddDays(-400)),
            CancellationToken.None);
        Assert.Equal(new SqliteRetentionResult(1, 0), await retention.CleanupExpiredAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConversationNotFoundException>(async () =>
            await store.SubmitTurnAsync(Submission(expired.Id, "after-cleanup", "owner"), CancellationToken.None));
        await using (var connection = await database.OpenConnectionAsync())
        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = """
                SELECT (SELECT COUNT(*) FROM conversations WHERE id = $id)
                    + (SELECT COUNT(*) FROM turns WHERE conversation_id = $id)
                    + (SELECT COUNT(*) FROM messages WHERE conversation_id = $id);
                """;
            rows.Parameters.AddWithValue("$id", expired.Id.Value.ToString("D"));
            Assert.Equal(0L, (long)(await rows.ExecuteScalarAsync())!);
        }

        var implicitRoot = ConversationId.New();
        var trusted = await store.SubmitTurnAsync(Submission(implicitRoot, "implicit", null), CancellationToken.None);
        Assert.False(trusted.IsDuplicate);
        Assert.NotNull(await store.GetConversationAsync(implicitRoot, "owner", CancellationToken.None));
        Assert.Null(await store.FindSubmittedTurnAsync(implicitRoot, "unused", fingerprint, CancellationToken.None, "owner"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerScopedDuplicateRefreshesRetentionActivityButTrustedDuplicateDoesNot()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var past = Now.AddDays(-400);
        var clock = new MutableClock(past);
        var store = new SqliteConversationStore(database, clock);
        var retention = new SqliteRetentionService(database, clock, new SqliteRetentionOptions());
        const string text = "Completed request";
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

        async Task<(ConversationId Id, SubmittedConversationTurn First)> CompletedConversationAsync()
        {
            var conversation = await store.CreateConversationAsync(
                new ConversationRecord(ConversationId.New(), "owner", "Old", past, past), CancellationToken.None);
            var first = await store.SubmitTurnAsync(
                new ConversationTurnSubmission(TurnId.New(), conversation.Id, "retry", fingerprint, Guid.NewGuid(), text, past, "owner"),
                CancellationToken.None);
            await store.UpdateTurnStatusAsync(first.Turn.Id, TurnStatus.Completed, first.Turn.Version, past, CancellationToken.None);
            return (conversation.Id, first);
        }

        var ownerScoped = await CompletedConversationAsync();
        var trusted = await CompletedConversationAsync();
        clock.UtcNow = Now;
        var ownerRetry = await store.SubmitTurnAsync(
            new ConversationTurnSubmission(TurnId.New(), ownerScoped.Id, "retry", fingerprint, Guid.NewGuid(), text, Now, "owner"),
            CancellationToken.None);
        var trustedRetry = await store.SubmitTurnAsync(
            new ConversationTurnSubmission(TurnId.New(), trusted.Id, "retry", fingerprint, Guid.NewGuid(), text, Now),
            CancellationToken.None);

        Assert.True(ownerRetry.IsDuplicate);
        Assert.Equal(ownerScoped.First.Turn.Id, ownerRetry.Turn.Id);
        Assert.True(trustedRetry.IsDuplicate);
        Assert.Equal(Now, (await store.GetConversationAsync(ownerScoped.Id, "owner", CancellationToken.None))!.UpdatedAtUtc);
        Assert.Equal(past, (await store.GetConversationAsync(trusted.Id, "owner", CancellationToken.None))!.UpdatedAtUtc);

        // Cleanup immediately after acceptance keeps the re-accepted root; the untouched trusted root expires.
        Assert.Equal(new SqliteRetentionResult(1, 0), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(ownerRetry.Turn.Id, CancellationToken.None))!.Status);
        Assert.Null(await store.GetConversationAsync(trusted.Id, "owner", CancellationToken.None));

        // An older duplicate timestamp never moves the activity time backwards.
        var stale = await store.SubmitTurnAsync(
            new ConversationTurnSubmission(TurnId.New(), ownerScoped.Id, "retry", fingerprint, Guid.NewGuid(), text, past, "owner"),
            CancellationToken.None);
        Assert.True(stale.IsDuplicate);
        Assert.Equal(Now, (await store.GetConversationAsync(ownerScoped.Id, "owner", CancellationToken.None))!.UpdatedAtUtc);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TerminalCompletionPersistsItsFinalAssistantMessageAtomicallyOnce()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new MutableClock(Now));
        var conversationId = ConversationId.New();
        var submission = await store.SubmitTurnAsync(
            new ConversationTurnSubmission(
                TurnId.New(),
                conversationId,
                "final-message",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("question"))),
                Guid.NewGuid(),
                "question",
                Now),
            CancellationToken.None);
        await store.TransitionTurnAndAppendEventAsync(
            submission.Turn.Id,
            TurnStatus.Routing,
            submission.Turn.Version,
            Now,
            nameof(TurnRouting),
            System.Text.Json.JsonSerializer.Serialize(new TurnRouting(submission.Turn.Id, Now)),
            Now,
            CancellationToken.None);
        var running = await store.GetTurnAsync(submission.Turn.Id, CancellationToken.None);
        var assistant = new ConversationMessage(
            Guid.NewGuid(),
            conversationId,
            "assistant",
            "It is noon.",
            Now.AddSeconds(1));

        await store.UpdateTurnStatusAndAppendEventAsync(
            submission.Turn.Id,
            TurnStatus.Completed,
            running!.Version,
            Now.AddSeconds(1),
            nameof(TurnCompleted),
            System.Text.Json.JsonSerializer.Serialize(new TurnCompleted(submission.Turn.Id, Now.AddSeconds(1))),
            Now.AddSeconds(1),
            CancellationToken.None,
            assistant);

        var messages = await store.ReadRecentAsync(conversationId, 10, CancellationToken.None);
        Assert.Collection(
            messages,
            user => Assert.Equal("user", user.Role),
            answer =>
            {
                Assert.Equal("assistant", answer.Role);
                Assert.Equal("It is noon.", answer.Content);
            });
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(submission.Turn.Id, CancellationToken.None))!.Status);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.UpdateTurnStatusAndAppendEventAsync(
                submission.Turn.Id,
                TurnStatus.Completed,
                running.Version,
                Now.AddSeconds(2),
                nameof(TurnCompleted),
                System.Text.Json.JsonSerializer.Serialize(new TurnCompleted(submission.Turn.Id, Now.AddSeconds(2))),
                Now.AddSeconds(2),
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedClarificationPersistsItsOwnerFacingMessageAtomically()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new MutableClock(Now));
        var conversationId = ConversationId.New();
        var submission = await store.SubmitTurnAsync(
            new ConversationTurnSubmission(
                TurnId.New(),
                conversationId,
                "clarification",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("ambiguous task"))),
                Guid.NewGuid(),
                "ambiguous task",
                Now),
            CancellationToken.None);
        await store.TransitionTurnAndAppendEventAsync(
            submission.Turn.Id,
            TurnStatus.Routing,
            submission.Turn.Version,
            Now,
            nameof(TurnRouting),
            System.Text.Json.JsonSerializer.Serialize(new TurnRouting(submission.Turn.Id, Now)),
            Now,
            CancellationToken.None);
        var routing = await store.GetTurnAsync(submission.Turn.Id, CancellationToken.None);
        var clarification = new TurnClarificationRequired(
            submission.Turn.Id,
            Now.AddSeconds(1),
            "task_category_ambiguous",
            "Please clarify the supported local task.");
        var assistant = new ConversationMessage(
            Guid.NewGuid(),
            conversationId,
            "assistant",
            clarification.UserMessage,
            Now.AddSeconds(1));

        await store.UpdateTurnStatusAndAppendEventAsync(
            submission.Turn.Id,
            TurnStatus.Failed,
            routing!.Version,
            Now.AddSeconds(1),
            nameof(TurnClarificationRequired),
            System.Text.Json.JsonSerializer.Serialize(clarification),
            Now.AddSeconds(1),
            CancellationToken.None,
            assistant);

        var messages = await store.ReadRecentAsync(conversationId, 10, CancellationToken.None);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(submission.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(messages, message =>
            message.Role == "assistant" && message.Content == clarification.UserMessage);
        Assert.Contains(
            await store.ReadTurnEventsAfterAsync(submission.Turn.Id, 0, 10, CancellationToken.None),
            item => item.EventType == nameof(TurnClarificationRequired));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NonterminalTurnStateAndVersionCanBeRecoveredAfterRestart()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new MutableClock(Now));
        var turnId = TurnId.New();
        var conversationId = ConversationId.New();
        var created = await store.CreateTurnAsync(
            turnId,
            conversationId,
            TurnStatus.Received,
            Now,
            CancellationToken.None);
        await store.AppendTurnEventAsync(
            turnId,
            "turn.progress",
            """{"state":"observed"}""",
            Now.AddSeconds(10),
            CancellationToken.None);
        var updated = await store.UpdateTurnStatusAsync(
            turnId,
            TurnStatus.Routing,
            created.Version,
            Now.AddSeconds(5),
            CancellationToken.None);

        var reopened = new SqliteConversationStore(new SqliteDatabase(file.Path), new MutableClock(Now));
        var recovered = await reopened.GetTurnAsync(turnId, CancellationToken.None);
        var nonterminal = await reopened.ReadNonterminalTurnsAsync(10, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(TurnStatus.Routing, recovered.Status);
        Assert.Equal(2, recovered.Version);
        Assert.Equal(Now.AddSeconds(10), recovered.UpdatedAtUtc);
        Assert.Equal(updated, recovered);
        Assert.Equal([recovered], nonterminal);
        Assert.Null(await reopened.GetTurnAsync(TurnId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await reopened.ReadNonterminalTurnsAsync(0, CancellationToken.None));

        await reopened.UpdateTurnStatusAsync(
            turnId,
            TurnStatus.Completed,
            recovered.Version,
            Now.AddSeconds(20),
            CancellationToken.None);
        Assert.Empty(await reopened.ReadNonterminalTurnsAsync(10, CancellationToken.None));

        var interruptedTurnId = TurnId.New();
        await reopened.CreateTurnAsync(
            interruptedTurnId,
            recovered.ConversationId,
            TurnStatus.Received,
            Now.AddSeconds(30),
            CancellationToken.None);
        var interrupted = await reopened.GetTurnAsync(interruptedTurnId, CancellationToken.None);
        await reopened.UpdateTurnStatusAsync(
            interruptedTurnId,
            TurnStatus.Interrupted,
            interrupted!.Version,
            Now.AddSeconds(31),
            CancellationToken.None);
        Assert.Empty(await reopened.ReadNonterminalTurnsAsync(10, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MemoryUpdatesUseCompareAndSwapAndMaintainFtsIndex()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteMemoryStore(database, new MutableClock(Now));
        var fact = new MemoryFact(
            MemoryFactId.New(), "home", "thermostat", "smart thermostat in hallway", "message-1", "Private", 0);

        var created = await store.SaveAsync(fact, 0, CancellationToken.None);
        Assert.Equal(1, created.Version);
        Assert.Single(await store.SearchAsync("thermostat hallway", 10, CancellationToken.None));

        var updated = await store.SaveAsync(created with { Value = "hallway thermostat" }, created.Version, CancellationToken.None);
        Assert.Equal(2, updated.Version);
        Assert.Empty(await store.SearchAsync("smart thermostat", 10, CancellationToken.None));
        Assert.Single(await store.SearchAsync("hallway thermostat", 10, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.SaveAsync(created, created.Version, CancellationToken.None));

        await store.DeleteAsync(updated.Id, updated.Version, CancellationToken.None);
        Assert.Null(await store.GetAsync(updated.Id, CancellationToken.None));
        Assert.Empty(await store.SearchAsync("hallway thermostat", 10, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.DeleteAsync(updated.Id, updated.Version, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PersistenceStoresRejectInvalidBoundsStatesAndMissingRows()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var memory = new SqliteMemoryStore(database, clock);
        await Assert.ThrowsAsync<ArgumentException>(async () => await memory.SearchAsync("", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await memory.SearchAsync("lamp", 0, CancellationToken.None));
        Assert.Empty(await memory.SearchAsync("!!!", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await memory.SaveAsync(
            new MemoryFact(MemoryFactId.New(), "home", "x", "y", "source", "Private", 0),
            -1,
            CancellationToken.None));
        Assert.Null(await memory.GetAsync(MemoryFactId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await memory.DeleteAsync(MemoryFactId.New(), 1, CancellationToken.None));

        var conversations = new SqliteConversationStore(database, clock);
        await Assert.ThrowsAsync<ArgumentException>(async () => await conversations.AppendMessageAsync(
            new ConversationMessage(Guid.NewGuid(), ConversationId.New(), "invalid", "text", Now),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.ReadRecentAsync(
            ConversationId.New(), 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.CreateTurnAsync(
            TurnId.New(), ConversationId.New(), (TurnStatus)int.MaxValue, Now, CancellationToken.None));
        var turnId = TurnId.New();
        var turn = await conversations.CreateTurnAsync(turnId, ConversationId.New(), TurnStatus.Received, Now, CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(async () => await conversations.CreateTurnAsync(
            turnId, turn.ConversationId, TurnStatus.Received, Now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.UpdateTurnStatusAsync(
            turnId, (TurnStatus)int.MaxValue, 1, Now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.UpdateTurnStatusAsync(
            turnId, TurnStatus.Completed, 0, Now, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await conversations.UpdateTurnStatusAsync(
            TurnId.New(), TurnStatus.Completed, 1, Now, CancellationToken.None));
        Assert.Empty(await conversations.ReadTurnEventsAfterAsync(turnId, 0, 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.ReadTurnEventsAfterAsync(
            turnId, -1, 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await conversations.ReadTurnEventsAfterAsync(
            turnId, 0, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await conversations.AppendTurnEventAsync(
            turnId, "event", "[]", Now, CancellationToken.None));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () => await conversations.AppendTurnEventAsync(
            turnId, "event", "{", Now, CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(async () => await conversations.AppendTurnEventAsync(
            TurnId.New(), "event", "{}", Now, CancellationToken.None));

        var actions = new SqliteActionJournalStore(database);
        var prepared = NewAction(ActionId.New(), "Prepared", Now);
        await Assert.ThrowsAsync<ArgumentException>(async () => await actions.CreateAsync(
            prepared with { Status = "Unknown" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await actions.CreateAsync(
            prepared with { Version = 2 }, CancellationToken.None));
        Assert.Null(await actions.GetAsync(ActionId.New(), CancellationToken.None));
        var inserted = await actions.CreateAsync(prepared, CancellationToken.None);
        Assert.Equal(inserted, await actions.CreateAsync(prepared, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await actions.CreateAsync(
            prepared with { ActionType = "different.action" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await actions.CreateAsync(
            prepared with { RequestHash = "different-hash" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await actions.CreateAsync(
            prepared with { CanonicalArguments = """{"different":true}""" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await actions.UpdateStatusAsync(
            prepared.Id, "Invalid", 1, Now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await actions.UpdateStatusAsync(
            prepared.Id, "Failed", 0, Now, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await actions.UpdateStatusAsync(
            ActionId.New(), "Failed", 1, Now, CancellationToken.None));

        var approvals = new SqliteApprovalStore(database);
        var pending = new ApprovalStorageRecord(
            ApprovalId.New(), prepared.Id, "owner", "Pending", Now, Now.AddMinutes(1), null, 1);
        await Assert.ThrowsAsync<ArgumentException>(async () => await approvals.CreateAsync(
            pending with { Status = "Approved" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await approvals.CreateAsync(
            pending with { ExpiresAtUtc = Now }, CancellationToken.None));
        Assert.Null(await approvals.GetAsync(ApprovalId.New(), CancellationToken.None));
        await approvals.CreateAsync(pending, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(async () => await approvals.ResolveAsync(
            pending.Id, "owner", "Pending", 1, Now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await approvals.ResolveAsync(
            pending.Id, "owner", "Approved", 0, Now, CancellationToken.None));

        var audit = new SqliteAuditStore(database);
        await Assert.ThrowsAsync<ArgumentException>(async () => await audit.AppendAsync(
            NewAudit(Now) with { PayloadJson = "[]" }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () => await audit.AppendAsync(
            NewAudit(Now) with { PayloadJson = "{" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await audit.ReadSinceAsync(
            Now, 0, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentMemoryWritersAllowOnlyOneExpectedVersionUpdate()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteMemoryStore(database, new MutableClock(Now));
        var created = await store.SaveAsync(
            new MemoryFact(MemoryFactId.New(), "home", "light", "on", "message-2", "Private", 0),
            0,
            CancellationToken.None);

        var outcomes = await RunConcurrentlyAsync(
            () => TryUpdateAsync(store, created with { Value = "dim" }, created.Version),
            () => TryUpdateAsync(store, created with { Value = "bright" }, created.Version));

        Assert.Equal(1, outcomes.Count(success => success));
        Assert.Equal(1, outcomes.Count(success => !success));
        Assert.Equal(2, (await store.GetAsync(created.Id, CancellationToken.None))!.Version);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalStorageBindsOwnerExpiryAndSingleUseResolution()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var actionStore = new SqliteActionJournalStore(database);
        var action = NewAction(ActionId.New(), "Prepared", Now);
        await actionStore.CreateAsync(action, CancellationToken.None);
        var store = new SqliteApprovalStore(database);
        var approval = new ApprovalStorageRecord(
            ApprovalId.New(), action.Id, "owner-1", "Pending", Now, Now.AddMinutes(5), null, 1);
        await store.CreateAsync(approval, CancellationToken.None);

        var approved = await store.ResolveAsync(
            approval.Id, "owner-1", "Approved", 1, Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal("Approved", approved.Status);
        Assert.Equal(2, approved.Version);
        Assert.Equal(Now.AddMinutes(1), approved.ResolvedAtUtc);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.ResolveAsync(
                approval.Id, "owner-1", "Rejected", 1, Now.AddMinutes(2), CancellationToken.None));

        var expired = approval with { Id = ApprovalId.New(), ExpiresAtUtc = Now.AddMinutes(1) };
        await store.CreateAsync(expired, CancellationToken.None);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.ResolveAsync(
                expired.Id, "owner-1", "Approved", 1, Now.AddMinutes(1), CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.ResolveAsync(
                expired.Id, "other-owner", "Approved", 1, Now, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExpiredJobLeaseCanBeReclaimedButStaleWorkerCannotCompleteIt()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var jobId = JobId.New();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO jobs
                    (id, owner_id, kind, payload_version, payload, time_zone_id, due_at_utc, enabled, misfire_policy)
                VALUES ($id, 'owner', 'reminder', 1, '{}', 'America/Los_Angeles', $due, 1, 'Delayed');
                """;
            command.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
            command.Parameters.AddWithValue("$due", SqliteValueForTest(Now.AddMinutes(-1)));
            await command.ExecuteNonQueryAsync();
        }

        var clock = new MutableClock(Now);
        var store = new SqliteJobStore(database, clock);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ClaimDueAsync(
            " ", Now, TimeSpan.FromMinutes(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ClaimDueAsync(
            "worker", Now, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.CompleteAsync(
            new JobLease(jobId, "worker", Now.AddMinutes(1), 1), " ", CancellationToken.None));
        var firstLease = await store.ClaimDueAsync("worker-1", Now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(firstLease);
        Assert.Null(await store.ClaimDueAsync("worker-2", Now, TimeSpan.FromMinutes(1), CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(2);
        var secondLease = await store.ClaimDueAsync("worker-2", clock.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(secondLease);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.CompleteAsync(firstLease!, "Succeeded", CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(2).AddSeconds(30);
        await store.CompleteAsync(secondLease!, "Unknown", CancellationToken.None);
        await using var verify = await database.OpenConnectionAsync();
        await using var outcome = verify.CreateCommand();
        outcome.CommandText = "SELECT outcome FROM jobs WHERE id = $id;";
        outcome.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
        Assert.Equal("Unknown", (string?)await outcome.ExecuteScalarAsync());
        await using var runs = verify.CreateCommand();
        runs.CommandText = "SELECT status FROM job_runs WHERE job_id = $id;";
        runs.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
        Assert.Equal("Unknown", (string?)await runs.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CleanupFailureDegradesReadinessWithoutStoppingHostAndRecoversOnNextPass()
    {
        var store = new ControlledCleanupStore();
        var worker = new RetentionCleanupService(
            store, new MutableClock(Now), new RetentionSettings(1, 1),
            TimeSpan.FromMilliseconds(20), NullLogger<RetentionCleanupService>.Instance);
        using var host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(worker)).Build();
        await host.StartAsync();
        try
        {
            await store.SecondPass.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.Equal(HealthStatus.Degraded, (await worker.CheckHealthAsync(new HealthCheckContext())).Status);
            store.AllowSuccess.TrySetResult();
            await store.ThirdPass.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HealthStatus.Healthy, (await worker.CheckHealthAsync(new HealthCheckContext())).Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                worker.CheckHealthAsync(new HealthCheckContext(), new CancellationToken(true)));
        }
        finally
        {
            await host.StopAsync();
        }
        Assert.True(store.ShutdownCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(HealthStatus.Healthy, (await worker.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.True(worker.ExecuteTask!.IsCanceled);
    }

    private sealed class ControlledCleanupStore : IHistoryRetentionStore
    {
        public TaskCompletionSource SecondPass { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSuccess { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdPass { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int passes;

        public async ValueTask CleanupExpiredAsync(
            RetentionSettings defaults, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            var pass = Interlocked.Increment(ref passes);
            if (pass == 1) throw new InvalidOperationException("private-maintenance-detail");
            if (pass == 2)
            {
                SecondPass.TrySetResult();
                await AllowSuccess.Task.WaitAsync(cancellationToken);
                return;
            }
            ThirdPass.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ShutdownCancelled.TrySetResult();
                throw;
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HostedRetentionCleanupAppliesExpiredHistoryDuringUptime()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var conversations = new SqliteConversationStore(database, clock);
        var expired = new ConversationRecord(
            ConversationId.New(),
            "owner",
            "Expired",
            Now.AddDays(-2),
            Now.AddDays(-2));
        await conversations.CreateConversationAsync(expired, CancellationToken.None);

        var cleanup = new RetentionCleanupService(
            new SqliteRetentionService(database, clock, new SqliteRetentionOptions()),
            clock,
            new RetentionSettings(1, 1),
            TimeSpan.FromMilliseconds(20),
            NullLogger<RetentionCleanupService>.Instance);
        await cleanup.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitForConversationDeletionAsync(conversations, expired.Id, deadline.Token);
        }
        finally
        {
            await cleanup.StopAsync(CancellationToken.None);
        }

        Assert.Null(await conversations.GetConversationAsync(expired.Id, "owner", CancellationToken.None));
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Unknown")]
    [Trait("Category", "Integration")]
    public async Task CompletedJobOccurrenceCannotBeClaimedAgain(string outcome)
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var jobId = JobId.New();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO jobs
                    (id, owner_id, kind, payload_version, payload, time_zone_id, due_at_utc, enabled, misfire_policy)
                VALUES ($id, 'owner', 'reminder', 1, '{}', 'America/Los_Angeles', $due, 1, 'Delayed');
                """;
            command.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
            command.Parameters.AddWithValue("$due", SqliteValueForTest(Now.AddMinutes(-1)));
            await command.ExecuteNonQueryAsync();
        }

        var clock = new MutableClock(Now);
        var store = new SqliteJobStore(database, clock);
        var lease = await store.ClaimDueAsync("worker", Now, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(lease);
        clock.UtcNow = Now.AddMinutes(1);
        await store.CompleteAsync(lease!, outcome, CancellationToken.None);

        Assert.Null(await store.ClaimDueAsync("worker-retry", clock.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.CompleteAsync(lease!, "Succeeded", CancellationToken.None));
        await using var verify = await database.OpenConnectionAsync();
        await using var run = verify.CreateCommand();
        run.CommandText = "SELECT status, started_at_utc, completed_at_utc FROM job_runs WHERE job_id = $id;";
        run.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
        await using var reader = await run.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(outcome, reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(Now.AddMinutes(1).ToString("O"), reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CompletionReadsExpiryClockOnlyAfterAcquiringTheWriteTransaction()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var jobId = JobId.New();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO jobs
                    (id, owner_id, kind, payload_version, payload, time_zone_id, due_at_utc, enabled, misfire_policy)
                VALUES ($id, 'owner', 'reminder', 1, '{}', 'UTC', $due, 1, 'Delayed');
                """;
            insert.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$due", Now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var clockReadUnderWriteLock = false;
        var clock = new CallbackClock(() =>
        {
            // A second writer distinguishes a clock read inside the transaction from a stale pre-lock read.
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file.Path,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE;";
            try
            {
                command.ExecuteNonQuery();
                command.CommandText = "ROLLBACK;";
                command.ExecuteNonQuery();
                return Now;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5)
            {
                clockReadUnderWriteLock = true;
                return Now.AddMinutes(2);
            }
        });
        var store = new SqliteJobStore(database, clock);
        var lease = await new SqliteJobStore(database, new MutableClock(Now))
            .ClaimDueAsync("worker", Now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(lease);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            async () => await store.CompleteAsync(lease, "Succeeded", CancellationToken.None));
        Assert.True(clockReadUnderWriteLock);
        await using var verify = await database.OpenConnectionAsync();
        await using var outcome = verify.CreateCommand();
        outcome.CommandText = "SELECT outcome FROM jobs WHERE id = $id;";
        outcome.Parameters.AddWithValue("$id", jobId.Value.ToString("D"));
        Assert.Equal(DBNull.Value, await outcome.ExecuteScalarAsync());
        outcome.CommandText = "SELECT COUNT(*) FROM job_runs;";
        Assert.Equal(0L, (long)(await outcome.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ContendedClaimRefreshesItsDeadlineAfterTheWriterReleasesTheLock()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO jobs
                    (id, owner_id, kind, payload_version, payload, time_zone_id, due_at_utc, enabled, misfire_policy)
                VALUES ($id, 'owner', 'reminder', 1, '{}', 'UTC', $due, 1, 'Delayed');
                """;
            insert.Parameters.AddWithValue("$id", JobId.New().Value.ToString("D"));
            insert.Parameters.AddWithValue("$due", Now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var observedClock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new CallbackClock(() =>
        {
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file.Path,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE;";
            var busy = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Equal(5, busy.SqliteErrorCode);
            observedClock.TrySetResult();
            return Now.AddSeconds(2);
        });
        var store = new SqliteJobStore(database, clock);
        await using var blocker = await database.OpenConnectionAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = Task.Run(async () =>
        {
            started.TrySetResult();
            return await store.ClaimDueAsync("worker", Now, TimeSpan.FromSeconds(1), CancellationToken.None);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await transaction.CommitAsync();
            var lease = await claim.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(lease);
            Assert.True(observedClock.Task.IsCompletedSuccessfully);
            Assert.Equal(Now.AddSeconds(3), lease.LeaseExpiresAtUtc);
            Assert.Null(await store.ClaimDueAsync(
                "other-worker", Now.AddSeconds(2), TimeSpan.FromSeconds(1), CancellationToken.None));
            await store.CompleteAsync(lease, "Succeeded", CancellationToken.None);
        }
        finally
        {
            if (!claim.IsCompleted)
            {
                await transaction.DisposeAsync();
                await claim.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetentionUsesDefaultAndOverrideWindowsAndKeepsDurableState()
    {
        Assert.Equal(new SqliteRetentionOptions(), new SqliteRetentionOptions(90, 30));
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var conversations = new SqliteConversationStore(database, clock);
        var oldConversation = ConversationId.New();
        var retainedConversation = ConversationId.New();
        var runningConversation = ConversationId.New();
        var approvalConversation = ConversationId.New();
        var interruptedConversation = ConversationId.New();
        var receivedConversation = ConversationId.New();
        var routingConversation = ConversationId.New();
        var contextReviewConversation = ConversationId.New();
        var completedConversation = ConversationId.New();
        var failedConversation = ConversationId.New();
        var cancelledConversation = ConversationId.New();
        await conversations.AppendMessageAsync(Message(oldConversation, "expired"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(retainedConversation, "retained"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(runningConversation, "running turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(approvalConversation, "approval turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(interruptedConversation, "interrupted turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(receivedConversation, "received turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(routingConversation, "routing turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(contextReviewConversation, "context review turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(completedConversation, "completed turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(failedConversation, "failed turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(cancelledConversation, "cancelled turn"), CancellationToken.None);
        var runningTurn = TurnId.New();
        var approvalTurn = TurnId.New();
        var interruptedTurn = TurnId.New();
        var receivedTurn = TurnId.New();
        var routingTurn = TurnId.New();
        var contextReviewTurn = TurnId.New();
        await conversations.CreateTurnAsync(
            runningTurn,
            runningConversation,
            TurnStatus.Running,
            Now,
            CancellationToken.None);
        await conversations.CreateTurnAsync(
            approvalTurn,
            approvalConversation,
            TurnStatus.WaitingForApproval,
            Now,
            CancellationToken.None);
        await conversations.CreateTurnAsync(
            interruptedTurn,
            interruptedConversation,
            TurnStatus.Interrupted,
            Now,
            CancellationToken.None);
        await conversations.CreateTurnAsync(
            receivedTurn,
            receivedConversation,
            TurnStatus.Received,
            Now,
            CancellationToken.None);
        await conversations.CreateTurnAsync(
            routingTurn,
            routingConversation,
            TurnStatus.Routing,
            Now,
            CancellationToken.None);
        await conversations.CreateTurnAsync(
            contextReviewTurn,
            contextReviewConversation,
            TurnStatus.ContextReview,
            Now,
            CancellationToken.None);
        async Task<TurnId> CreateTerminalTurnAsync(ConversationId id, TurnStatus status)
        {
            var created = await conversations.CreateTurnAsync(
                TurnId.New(),
                id,
                TurnStatus.Received,
                Now,
                CancellationToken.None);
            var routing = await conversations.UpdateTurnStatusAsync(
                created.Id,
                TurnStatus.Routing,
                created.Version,
                Now,
                CancellationToken.None);
            var running = await conversations.UpdateTurnStatusAsync(
                created.Id,
                TurnStatus.Running,
                routing.Version,
                Now,
                CancellationToken.None);
            await conversations.UpdateTurnStatusAsync(
                created.Id,
                status,
                running.Version,
                Now,
                CancellationToken.None);
            return created.Id;
        }

        var completedTurn = await CreateTerminalTurnAsync(completedConversation, TurnStatus.Completed);
        var failedTurn = await CreateTerminalTurnAsync(failedConversation, TurnStatus.Failed);
        var cancelledTurn = await CreateTerminalTurnAsync(cancelledConversation, TurnStatus.Cancelled);

        var audit = new SqliteAuditStore(database);
        await audit.AppendAsync(NewAudit(Now.AddDays(-31)), CancellationToken.None);
        await audit.AppendAsync(NewAudit(Now.AddDays(-29)), CancellationToken.None);
        var memory = new SqliteMemoryStore(database, clock);
        var fact = await memory.SaveAsync(
            new MemoryFact(MemoryFactId.New(), "owner", "preference", "quiet", "source-1", "Private", 0),
            0,
            CancellationToken.None);
        var actions = new SqliteActionJournalStore(database);
        var unknownAction = NewAction(ActionId.New(), "Prepared", Now);
        await actions.CreateAsync(unknownAction, CancellationToken.None);
        var unknownResult = await actions.UpdateStatusAsync(unknownAction.Id, "Unknown", 1, Now, CancellationToken.None);
        Assert.Equal("Unknown", unknownResult.Status);
        Assert.Equal(2, unknownResult.Version);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var insertJob = connection.CreateCommand())
        {
            insertJob.CommandText = """
                INSERT INTO jobs
                    (id, owner_id, kind, payload_version, payload, time_zone_id, due_at_utc, enabled, misfire_policy)
                VALUES ($id, 'owner', 'reminder', 1, '{}', 'America/Los_Angeles', $due, 1, 'Delayed');
                """;
            insertJob.Parameters.AddWithValue("$id", JobId.New().Value.ToString("D"));
            insertJob.Parameters.AddWithValue("$due", SqliteValueForTest(Now.AddDays(-100)));
            await insertJob.ExecuteNonQueryAsync();
        }

        await using (var connection = await database.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE conversations SET updated_at_utc = $old
                WHERE id = $old_conversation;
                UPDATE conversations SET updated_at_utc = $recent
                WHERE id = $recent_conversation;
                UPDATE conversations SET updated_at_utc = $old
                WHERE id IN (
                    $running_conversation, $approval_conversation, $interrupted_conversation,
                    $received_conversation, $routing_conversation, $context_review_conversation,
                    $completed_conversation, $failed_conversation, $cancelled_conversation);
                """;
            update.Parameters.AddWithValue("$old", SqliteValueForTest(Now.AddDays(-91)));
            update.Parameters.AddWithValue("$recent", SqliteValueForTest(Now.AddDays(-89)));
            update.Parameters.AddWithValue("$old_conversation", oldConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$recent_conversation", retainedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$running_conversation", runningConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$approval_conversation", approvalConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$interrupted_conversation", interruptedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$received_conversation", receivedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$routing_conversation", routingConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$context_review_conversation", contextReviewConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$completed_conversation", completedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$failed_conversation", failedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$cancelled_conversation", cancelledConversation.Value.ToString("D"));
            await update.ExecuteNonQueryAsync();
        }

        var retention = new SqliteRetentionService(database, clock, new SqliteRetentionOptions());
        Assert.Equal(new SqliteRetentionResult(4, 1), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Equal(new SqliteRetentionResult(0, 0), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Empty(await conversations.ReadRecentAsync(oldConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(retainedConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(runningConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(approvalConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(interruptedConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(receivedConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(routingConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(contextReviewConversation, 10, CancellationToken.None));
        Assert.Empty(await conversations.ReadRecentAsync(completedConversation, 10, CancellationToken.None));
        Assert.Empty(await conversations.ReadRecentAsync(failedConversation, 10, CancellationToken.None));
        Assert.Empty(await conversations.ReadRecentAsync(cancelledConversation, 10, CancellationToken.None));
        Assert.Null(await conversations.GetTurnAsync(completedTurn, CancellationToken.None));
        Assert.Null(await conversations.GetTurnAsync(failedTurn, CancellationToken.None));
        Assert.Null(await conversations.GetTurnAsync(cancelledTurn, CancellationToken.None));
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var turns = connection.CreateCommand();
            turns.CommandText = """
                SELECT id, status FROM turns
                WHERE id IN ($running, $approval, $interrupted, $received, $routing, $context_review)
                ORDER BY id;
                """;
            turns.Parameters.AddWithValue("$running", runningTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$approval", approvalTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$interrupted", interruptedTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$received", receivedTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$routing", routingTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$context_review", contextReviewTurn.Value.ToString("D"));
            await using var reader = await turns.ExecuteReaderAsync();
            var retainedTurns = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                retainedTurns.Add(reader.GetString(0), reader.GetString(1));
            }

            Assert.Equal(6, retainedTurns.Count);
            Assert.Equal(TurnStatus.Running.ToString(), retainedTurns[runningTurn.Value.ToString("D")]);
            Assert.Equal(
                TurnStatus.WaitingForApproval.ToString(),
                retainedTurns[approvalTurn.Value.ToString("D")]);
            Assert.Equal(TurnStatus.Interrupted.ToString(), retainedTurns[interruptedTurn.Value.ToString("D")]);
            Assert.Equal(TurnStatus.Received.ToString(), retainedTurns[receivedTurn.Value.ToString("D")]);
            Assert.Equal(TurnStatus.Routing.ToString(), retainedTurns[routingTurn.Value.ToString("D")]);
            Assert.Equal(
                TurnStatus.ContextReview.ToString(),
                retainedTurns[contextReviewTurn.Value.ToString("D")]);
        }
        Assert.NotNull(await memory.GetAsync(fact.Id, CancellationToken.None));
        Assert.Equal("Unknown", (await actions.GetAsync(unknownAction.Id, CancellationToken.None))!.Status);
        Assert.Single(await audit.ReadSinceAsync(Now.AddDays(-90), 10, CancellationToken.None));
        await using (var connection = await database.OpenConnectionAsync())
        await using (var jobs = connection.CreateCommand())
        {
            jobs.CommandText = "SELECT COUNT(*) FROM jobs;";
            Assert.Equal(1L, (long)(await jobs.ExecuteScalarAsync())!);
        }

        using var overrideFile = IsolatedDatabaseFile.Create();
        var overrideDatabase = new SqliteDatabase(overrideFile.Path);
        await overrideDatabase.InitializeAsync();
        var overrideStore = new SqliteConversationStore(overrideDatabase, clock);
        var overrideOld = ConversationId.New();
        var overrideRecent = ConversationId.New();
        await overrideStore.AppendMessageAsync(Message(overrideOld, "old override"), CancellationToken.None);
        await overrideStore.AppendMessageAsync(Message(overrideRecent, "recent override"), CancellationToken.None);
        await using (var connection = await overrideDatabase.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE conversations SET updated_at_utc = $old WHERE id = $old_id;
                UPDATE conversations SET updated_at_utc = $recent WHERE id = $recent_id;
                """;
            update.Parameters.AddWithValue("$old", SqliteValueForTest(Now.AddDays(-3)));
            update.Parameters.AddWithValue("$recent", SqliteValueForTest(Now.AddDays(-1)));
            update.Parameters.AddWithValue("$old_id", overrideOld.Value.ToString("D"));
            update.Parameters.AddWithValue("$recent_id", overrideRecent.Value.ToString("D"));
            await update.ExecuteNonQueryAsync();
        }

        var customRetention = new SqliteRetentionService(
            overrideDatabase, clock, new SqliteRetentionOptions(ConversationRetentionDays: 2, AuditRetentionDays: 1));
        Assert.Equal(new SqliteRetentionResult(1, 0), await customRetention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Single(await overrideStore.ReadRecentAsync(overrideRecent, 10, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteRetentionService(overrideDatabase, clock, new SqliteRetentionOptions(0, 30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteRetentionService(overrideDatabase, clock, new SqliteRetentionOptions(3651, 30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteRetentionService(overrideDatabase, clock, new SqliteRetentionOptions(90, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteRetentionService(overrideDatabase, clock, new SqliteRetentionOptions(90, 3651)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackupRestoreRehearsalRestoresIntegrityAndUnknownActionState()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var fact = await new SqliteMemoryStore(database, clock).SaveAsync(
            new MemoryFact(MemoryFactId.New(), "owner", "name", "Jarvis", "message-3", "Private", 0),
            0,
            CancellationToken.None);
        var actions = new SqliteActionJournalStore(database);
        var action = NewAction(ActionId.New(), "Prepared", Now);
        await actions.CreateAsync(action, CancellationToken.None);
        await actions.UpdateStatusAsync(action.Id, "Unknown", 1, Now, CancellationToken.None);
        var backupPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!, "backup.db");
        var backups = new SqliteBackupRestoreService(database);
        await backups.CreateBackupAsync(backupPath);
        await backups.ValidateUpgradeOnCopyAsync();

        await File.WriteAllTextAsync(file.Path, "intentionally corrupted database");
        await File.WriteAllTextAsync(file.Path + "-wal", "stale wal");
        await File.WriteAllTextAsync(file.Path + "-shm", "stale shared memory");
        await backups.RestoreAsync(backupPath);

        var reopened = new SqliteDatabase(file.Path);
        await reopened.InitializeAsync();
        Assert.Equal("Jarvis", (await new SqliteMemoryStore(reopened, clock).GetAsync(fact.Id, CancellationToken.None))!.Value);
        Assert.Equal("Unknown", (await new SqliteActionJournalStore(reopened).GetAsync(action.Id, CancellationToken.None))!.Status);
        await Assert.ThrowsAsync<IOException>(() => backups.CreateBackupAsync(backupPath));
        Assert.True(File.Exists(backupPath));
        await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateBackupAsync(file.Path));
        await Assert.ThrowsAsync<FileNotFoundException>(() => backups.RestoreAsync(file.Path + ".missing"));
        await Assert.ThrowsAsync<ArgumentException>(() => backups.RestoreAsync(file.Path));
        var corruptBackup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!, "corrupt-backup.db");
        await File.WriteAllTextAsync(corruptBackup, "not a SQLite backup");
        await Assert.ThrowsAsync<SqliteException>(() => backups.RestoreAsync(corruptBackup));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackupRejectsMissingLiveDatabaseWithoutCreatingFiles()
    {
        using var directory = IsolatedDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "missing.db");
        var backupPath = Path.Combine(directory.Path, "backup.db");
        var backups = new SqliteBackupRestoreService(new SqliteDatabase(databasePath));

        await Assert.ThrowsAsync<SqliteException>(() => backups.CreateBackupAsync(backupPath));

        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + "-wal"));
        Assert.False(File.Exists(backupPath + "-shm"));
    }

    [Theory]
    [InlineData("-wal", false)]
    [InlineData("-shm", false)]
    [InlineData("-journal", false)]
    [InlineData("-wal", true)]
    [InlineData("-shm", true)]
    [InlineData("-journal", true)]
    [Trait("Category", "Integration")]
    public async Task BackupRejectsPreexistingDestinationSidecarsWithoutChangingBytes(string suffix, bool sourceExists)
    {
        using var directory = IsolatedDirectory.Create();
        var source = new SqliteDatabase(Path.Combine(directory.Path, "source.db"));
        if (sourceExists)
        {
            await source.InitializeAsync();
        }

        var destination = Path.Combine(directory.Path, "backup.db");
        var original = new byte[] { 1, 5, 9, 2 };
        await File.WriteAllBytesAsync(destination + suffix, original);
        await Assert.ThrowsAsync<IOException>(
            () => new SqliteBackupRestoreService(source).CreateBackupAsync(destination));
        Assert.Equal(original, await File.ReadAllBytesAsync(destination + suffix));
        Assert.False(File.Exists(destination));
        Assert.Equal(sourceExists, File.Exists(source.DatabasePath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackupAndRestoreRejectRestoreArtifactsWithoutDeletingSourceBackup()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var backups = new SqliteBackupRestoreService(database);
        var backupPath = Path.Combine(Path.GetDirectoryName(file.Path)!, "artifact-source.db");
        await backups.CreateBackupAsync(backupPath);

        var artifactPaths = new[]
        {
            SqliteBackupRestoreService.RestoreStagedPath(file.Path),
            SqliteBackupRestoreService.RestoreStagedPath(file.Path) + "-wal",
            SqliteBackupRestoreService.RestoreStagedPath(file.Path) + "-shm",
            SqliteBackupRestoreService.RestoreStagedPath(file.Path) + "-journal",
            file.Path + ".restore-staged.restore-state",
            file.Path + ".restore-staged.restore-state.tmp",
            file.Path + ".restore-staged.restore-lock",
            file.Path + ".restore-staged.restore-rollback",
            file.Path + ".restore-staged.restore-original-wal",
            file.Path + ".restore-staged.restore-original-shm",
            file.Path + ".restore-staged.restore-original-journal",
            file.Path + ".restore-staged.restore-retired-database",
            file.Path + ".restore-staged.restore-retired-wal",
            file.Path + ".restore-staged.restore-retired-shm",
            file.Path + ".restore-staged.restore-retired-journal",
            SqliteBackupRestoreService.RestoreRollbackPath(file.Path),
            file.Path + ".restore-original-wal",
            file.Path + ".restore-original-shm",
            file.Path + ".restore-original-journal",
            file.Path + ".restore-retired-database",
            file.Path + ".restore-retired-wal",
            file.Path + ".restore-retired-shm",
            file.Path + ".restore-retired-journal",
            SqliteBackupRestoreService.RestoreMarkerPath(file.Path) + ".tmp",
            SqliteBackupRestoreService.RestoreMarkerPath(file.Path),
            file.Path + "-wal",
            file.Path + "-shm",
            file.Path + "-journal"
        };
        foreach (var artifactPath in artifactPaths)
        {
            File.Copy(backupPath, artifactPath, overwrite: true);
            var originalBackup = await File.ReadAllBytesAsync(artifactPath);

            await Assert.ThrowsAsync<ArgumentException>(() => backups.RestoreAsync(artifactPath));

            Assert.Equal(originalBackup, await File.ReadAllBytesAsync(artifactPath));
            await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateBackupAsync(artifactPath));
            Assert.Equal(originalBackup, await File.ReadAllBytesAsync(artifactPath));

            var caseVariant = TogglePathCase(artifactPath);
            if (!string.Equals(caseVariant, artifactPath, StringComparison.Ordinal)
                && File.Exists(caseVariant))
            {
                await Assert.ThrowsAsync<ArgumentException>(() => backups.RestoreAsync(caseVariant));
                Assert.Equal(originalBackup, await File.ReadAllBytesAsync(artifactPath));
                await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateBackupAsync(caseVariant));
                Assert.Equal(originalBackup, await File.ReadAllBytesAsync(artifactPath));
            }

            File.Delete(artifactPath);
        }

        var lockPath = SqliteBackupRestoreService.RestoreLockPath(file.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => backups.RestoreAsync(lockPath));
        await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateBackupAsync(lockPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BackupAndRestoreRejectArtifactPathsReachedThroughDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var backups = new SqliteBackupRestoreService(database);
        var backupPath = Path.Combine(Path.GetDirectoryName(file.Path)!, "symlink-source.db");
        await backups.CreateBackupAsync(backupPath);

        var directory = Path.GetDirectoryName(file.Path)!;
        var alias = Path.Combine(directory, "data-link");
        Directory.CreateSymbolicLink(alias, directory);
        var aliasedRollback = Path.Combine(
            alias,
            Path.GetFileName(SqliteBackupRestoreService.RestoreRollbackPath(file.Path)));
        File.Copy(backupPath, aliasedRollback);
        var originalBackup = await File.ReadAllBytesAsync(aliasedRollback);

        await Assert.ThrowsAsync<ArgumentException>(() => backups.RestoreAsync(aliasedRollback));
        Assert.Equal(originalBackup, await File.ReadAllBytesAsync(aliasedRollback));
        await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateBackupAsync(aliasedRollback));
        Assert.Equal(originalBackup, await File.ReadAllBytesAsync(aliasedRollback));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DatabaseRejectsSharedDataDirectoryAndRestrictsDatabasePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = IsolatedDirectory.Create();
        var privatePermissions =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(
            directory.Path,
            privatePermissions | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var databasePath = Path.Combine(directory.Path, "jarvis.db");
        var database = new SqliteDatabase(databasePath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.InitializeAsync());

        File.SetUnixFileMode(directory.Path, privatePermissions);
        await database.InitializeAsync();
        var databasePermissions = File.GetUnixFileMode(databasePath);
        var lockPermissions = File.GetUnixFileMode(SqliteBackupRestoreService.RestoreLockPath(databasePath));
        var nonOwnerPermissions =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal((UnixFileMode)0, databasePermissions & nonOwnerPermissions);
        Assert.Equal((UnixFileMode)0, lockPermissions & nonOwnerPermissions);

        await using var connection = await database.OpenConnectionAsync();
        var walPermissions = databasePath + "-wal";
        if (File.Exists(walPermissions))
        {
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(walPermissions) & nonOwnerPermissions);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PeriodicCleanupUsesCurrentDurableRetentionAfterOwnerChangesSettings()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new MutableClock(Now);
        var owner = new SqliteOwnerStateStore(database);
        var startupDefaults = new RetentionSettings(30, 30);
        await owner.UpdateRetentionAsync(startupDefaults, Now, CancellationToken.None);

        var conversations = new SqliteConversationStore(database, clock);
        var conversation = new ConversationRecord(
            ConversationId.New(),
            "owner",
            "Keep using updated retention",
            Now.AddDays(-40),
            Now.AddDays(-40));
        await conversations.CreateConversationAsync(conversation, CancellationToken.None);
        var settingsObservedBeforeUpdate = await owner.GetRetentionAsync(
            new RetentionSettings(90, 30),
            CancellationToken.None);

        await owner.UpdateRetentionAsync(new RetentionSettings(90, 30), Now, CancellationToken.None);
        var retention = new SqliteRetentionService(
            database,
            clock,
            new SqliteRetentionOptions(ConversationRetentionDays: 1, AuditRetentionDays: 1));
        await ((IHistoryRetentionStore)retention).CleanupExpiredAsync(
            settingsObservedBeforeUpdate,
            Now,
            CancellationToken.None);

        Assert.NotNull(await conversations.GetConversationAsync(conversation.Id, "owner", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WindowsDatabaseRejectsDirectoriesGrantingAccessToOtherUsers()
    {
        if (OperatingSystem.IsWindows())
        {
            await VerifyWindowsDatabaseAclAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyWindowsDatabaseAclAsync()
    {
        using var directory = IsolatedDirectory.Create();
        var privatePath = Path.Combine(directory.Path, "private");
        var database = new SqliteDatabase(Path.Combine(privatePath, "jarvis.db"));
        await database.InitializeAsync();
        var currentUserSid = WindowsIdentity.GetCurrent().User!;
        var allowedSids = new[]
        {
            currentUserSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var privateAcl = new DirectoryInfo(privatePath).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.Equal(currentUserSid, privateAcl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(
            privateAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow),
            rule => Assert.Contains(
                allowedSids,
                allowedSid => allowedSid.Equals(rule.IdentityReference)));

        var sharedPath = Path.Combine(directory.Path, "shared");
        Directory.CreateDirectory(sharedPath);
        var sharedAcl = new DirectoryInfo(sharedPath).GetAccessControl();
        sharedAcl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new DirectoryInfo(sharedPath).SetAccessControl(sharedAcl);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new SqliteDatabase(Path.Combine(sharedPath, "jarvis.db")).InitializeAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentStartupSerializesPreparedRestoreRecovery()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var conversationId = ConversationId.New();
        var message = Message(conversationId, "survives concurrent startup");
        await new SqliteConversationStore(database, new MutableClock(Now))
            .AppendMessageAsync(message, CancellationToken.None);

        var rollbackPath = SqliteBackupRestoreService.RestoreRollbackPath(file.Path);
        File.Copy(file.Path, rollbackPath);
        CopyTestSidecar(file.Path + "-wal", file.Path + ".restore-original-wal");
        CopyTestSidecar(file.Path + "-shm", file.Path + ".restore-original-shm");
        await File.WriteAllTextAsync(file.Path, "interrupted replacement");
        DeleteTestSidecar(file.Path + "-wal");
        DeleteTestSidecar(file.Path + "-shm");
        await File.WriteAllTextAsync(SqliteBackupRestoreService.RestoreMarkerPath(file.Path), "prepared");

        var lockPath = SqliteBackupRestoreService.RestoreLockPath(file.Path);
        Task startup;
        await using (var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            startup = Task.WhenAll(
                new SqliteDatabase(file.Path).InitializeAsync(),
                new SqliteDatabase(file.Path).InitializeAsync());
            var firstCompletion = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(startup, firstCompletion);
        }

        await startup.WaitAsync(TimeSpan.FromSeconds(10));

        var recovered = new SqliteDatabase(file.Path);
        await using var verify = await recovered.OpenConnectionAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT content FROM messages WHERE message_id = $id;";
        read.Parameters.AddWithValue("$id", message.MessageId.ToString("D"));
        Assert.Equal(message.Content, (string?)await read.ExecuteScalarAsync());
        Assert.False(File.Exists(SqliteBackupRestoreService.RestoreMarkerPath(file.Path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task FailedRestorePreservesCommittedWalDataAndStartupRecoversPreparedReplacement(bool useSymlink)
    {
        if (useSymlink && OperatingSystem.IsWindows())
        {
            return;
        }

        using var file = IsolatedDatabaseFile.Create();
        var sourcePath = Path.Combine(Path.GetDirectoryName(file.Path)!, "wal-source.db");
        var database = new SqliteDatabase(sourcePath);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE recovery_fixture (value TEXT NOT NULL);
                INSERT INTO recovery_fixture VALUES ('original');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var backupPath = Path.Combine(Path.GetDirectoryName(file.Path)!, "restore-source.db");
        await new SqliteBackupRestoreService(database).CreateBackupAsync(backupPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Pooling = false
        }.ToString();
        await using var pinnedReader = new SqliteConnection(connectionString);
        await pinnedReader.OpenAsync();
        await using var disableCheckpoint = pinnedReader.CreateCommand();
        disableCheckpoint.CommandText = "PRAGMA wal_autocheckpoint = 0;";
        await disableCheckpoint.ExecuteNonQueryAsync();
        await using var snapshot = pinnedReader.BeginTransaction(deferred: true);
        await using (var read = pinnedReader.CreateCommand())
        {
            read.Transaction = snapshot;
            read.CommandText = "SELECT value FROM recovery_fixture;";
            Assert.Equal("original", (string?)await read.ExecuteScalarAsync());
        }

        await using (var writer = await database.OpenConnectionAsync())
        await using (var insert = writer.CreateCommand())
        {
            insert.CommandText = "INSERT INTO recovery_fixture VALUES ('committed in wal');";
            await insert.ExecuteNonQueryAsync();
        }

        Assert.True(File.Exists(sourcePath + "-wal"));
        Assert.True(new FileInfo(sourcePath + "-wal").Length > 32);
        File.Copy(sourcePath, file.Path, overwrite: true);
        File.Copy(sourcePath + "-wal", file.Path + "-wal", overwrite: true);

        var livePath = file.Path;
        if (useSymlink)
        {
            livePath = Path.Combine(Path.GetDirectoryName(file.Path)!, "live-alias.db");
            File.CreateSymbolicLink(livePath, file.Path);
        }

        var liveDatabase = new SqliteDatabase(livePath);
        Assert.Equal(new SqliteDatabase(file.Path).DatabasePath, liveDatabase.DatabasePath);
        var failingRestore = new SqliteBackupRestoreService(liveDatabase, () => throw new IOException("replacement failed"));
        await Assert.ThrowsAsync<IOException>(() => failingRestore.RestoreAsync(backupPath));
        if (useSymlink)
        {
            Assert.NotNull(new FileInfo(livePath).LinkTarget);
        }
        await snapshot.DisposeAsync();
        await pinnedReader.DisposeAsync();

        var recovered = new SqliteDatabase(file.Path);
        await recovered.InitializeAsync();
        await using (var connection = await recovered.OpenConnectionAsync())
        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = "SELECT value FROM recovery_fixture ORDER BY rowid;";
            await using var reader = await rows.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("original", reader.GetString(0));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("committed in wal", reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }

        var rollbackPath = SqliteBackupRestoreService.RestoreRollbackPath(file.Path);
        await using (var source = await recovered.OpenConnectionAsync())
        await using (var rollback = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = rollbackPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await rollback.OpenAsync();
            source.BackupDatabase(rollback);
        }

        await File.WriteAllTextAsync(file.Path, "interrupted replacement");
        await File.WriteAllTextAsync(file.Path + "-wal", "stale replacement wal");
        await File.WriteAllTextAsync(SqliteBackupRestoreService.RestoreMarkerPath(file.Path), "prepared");
        await new SqliteDatabase(file.Path).InitializeAsync();

        await using var afterRestart = await new SqliteDatabase(file.Path).OpenConnectionAsync();
        await using var verify = afterRestart.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM recovery_fixture;";
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
        Assert.False(File.Exists(SqliteBackupRestoreService.RestoreMarkerPath(file.Path)));
        Assert.False(File.Exists(rollbackPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task RestoreRetiresHotJournalAndFailedReplacementRecoversOriginal(bool failReplacement)
    {
        using var directory = IsolatedDirectory.Create();
        var sourcePath = Path.Combine(directory.Path, "source.db");
        var source = new SqliteDatabase(sourcePath);
        await source.InitializeAsync();
        await using (var connection = await source.OpenConnectionAsync())
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE hot_journal_fixture (id INTEGER PRIMARY KEY, value TEXT NOT NULL, padding BLOB);
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 100)
                INSERT INTO hot_journal_fixture SELECT n, 'backup', zeroblob(4000) FROM numbers;
                """;
            await create.ExecuteNonQueryAsync();
        }
        var backupPath = Path.Combine(directory.Path, "backup.db");
        await new SqliteBackupRestoreService(source).CreateBackupAsync(backupPath);
        var livePath = Path.Combine(directory.Path, "live.db");
        await using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Pooling = false
        }.ToString()))
        {
            await writer.OpenAsync();
            await using var configure = writer.CreateCommand();
            configure.CommandText = """
                PRAGMA journal_mode = PERSIST;
                PRAGMA synchronous = FULL;
                PRAGMA cache_size = 1;
                PRAGMA cache_spill = ON;
                UPDATE hot_journal_fixture SET value = 'original';
                """;
            await configure.ExecuteNonQueryAsync();
            await using var transaction = writer.BeginTransaction();
            await using var dirty = writer.CreateCommand();
            dirty.Transaction = transaction;
            dirty.CommandText = "UPDATE hot_journal_fixture SET value = 'uncommitted';";
            await dirty.ExecuteNonQueryAsync();
            var journal = await File.ReadAllBytesAsync(sourcePath + "-journal");
            Assert.Equal(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }, journal[..8]);
            await transaction.CommitAsync();
            var persistedJournal = await File.ReadAllBytesAsync(sourcePath + "-journal");
            // PERSIST flushes the final records then zeros the first header on commit.
            // Restore SQLite's captured header to model interruption before that zeroing.
            journal[..28].CopyTo(persistedJournal, 0);
            File.Copy(sourcePath, livePath);
            await File.WriteAllBytesAsync(livePath + "-journal", persistedJournal);
        }

        var restore = new SqliteBackupRestoreService(
            new SqliteDatabase(livePath),
            failReplacement ? () => throw new IOException("replacement failed") : null);
        if (failReplacement)
        {
            await Assert.ThrowsAsync<IOException>(() => restore.RestoreAsync(backupPath));
            Assert.True(File.Exists(livePath + "-journal"));
        }
        else
        {
            await restore.RestoreAsync(backupPath);
            Assert.False(File.Exists(livePath + "-journal"));
        }

        var reopened = new SqliteDatabase(livePath);
        await reopened.InitializeAsync();
        await using var verify = await reopened.OpenConnectionAsync();
        await using var rows = verify.CreateCommand();
        rows.CommandText = "SELECT DISTINCT value FROM hot_journal_fixture;";
        await using (var reader = await rows.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(failReplacement ? "original" : "backup", reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }
        rows.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await rows.ExecuteScalarAsync());
        Assert.False(File.Exists(livePath + ".restore-original-journal"));
        Assert.False(File.Exists(livePath + ".restore-retired-journal"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedRestoreWithoutOriginalKeepsDatabaseAbsent()
    {
        using var directory = IsolatedDirectory.Create();
        var sourcePath = Path.Combine(directory.Path, "source.db");
        var source = new SqliteDatabase(sourcePath);
        await source.InitializeAsync();
        var backupPath = Path.Combine(directory.Path, "backup.db");
        await new SqliteBackupRestoreService(source).CreateBackupAsync(backupPath);
        var livePath = Path.Combine(directory.Path, "not-yet-created.db");
        var restore = new SqliteBackupRestoreService(
            new SqliteDatabase(livePath),
            () => throw new IOException("replacement failed"));

        await Assert.ThrowsAsync<IOException>(() => restore.RestoreAsync(backupPath));

        Assert.False(File.Exists(livePath));
        Assert.False(File.Exists(SqliteBackupRestoreService.RestoreMarkerPath(livePath)));
        Assert.False(File.Exists(SqliteBackupRestoreService.RestoreRollbackPath(livePath)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartupFinishesCleanupAfterCommittedRestore()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE restore_fixture (value TEXT NOT NULL); INSERT INTO restore_fixture VALUES ('restored');";
            await command.ExecuteNonQueryAsync();
        }

        var rollbackPath = SqliteBackupRestoreService.RestoreRollbackPath(file.Path);
        var stagedPath = SqliteBackupRestoreService.RestoreStagedPath(file.Path);
        await File.WriteAllTextAsync(rollbackPath, "old database snapshot");
        await File.WriteAllTextAsync(stagedPath, "stale staged database");
        await File.WriteAllTextAsync(SqliteBackupRestoreService.RestoreMarkerPath(file.Path), "committed");

        await new SqliteDatabase(file.Path).InitializeAsync();

        await using var verify = await database.OpenConnectionAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT value FROM restore_fixture;";
        Assert.Equal("restored", (string?)await read.ExecuteScalarAsync());
        Assert.False(File.Exists(SqliteBackupRestoreService.RestoreMarkerPath(file.Path)));
        Assert.False(File.Exists(rollbackPath));
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartupRejectsNewerOrPartiallyConflictingSchemasAndForeignKeyDamage()
    {
        using var newerFile = IsolatedDatabaseFile.Create();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = newerFile.Path
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new SqliteDatabase(newerFile.Path).InitializeAsync());

        using var conflictFile = IsolatedDatabaseFile.Create();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = conflictFile.Path
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE conversations (id TEXT PRIMARY KEY); PRAGMA user_version = 0;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(
            async () => await new SqliteDatabase(conflictFile.Path).InitializeAsync());
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = conflictFile.Path
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, (long)(await version.ExecuteScalarAsync())!);
        }

        using var damagedFile = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(damagedFile.Path);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var damage = connection.CreateCommand())
        {
            damage.CommandText = """
                PRAGMA foreign_keys = OFF;
                INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc)
                VALUES ('orphan', 'missing-conversation', 1, 'user', 'orphan', $now);
                """;
            damage.Parameters.AddWithValue("$now", SqliteValueForTest(Now));
            await damage.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await database.InitializeAsync());

        var missingDirectoryDatabase = new SqliteDatabase(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(damagedFile.Path)!, "missing", "database.db"));
        await using var createdInPrivateDirectory = await missingDirectoryDatabase.OpenConnectionAsync();
        Assert.True(Directory.Exists(System.IO.Path.GetDirectoryName(missingDirectoryDatabase.DatabasePath)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SimultaneousStartupAppliesEachMigrationOnlyOnce()
    {
        using var file = IsolatedDatabaseFile.Create();
        var first = new SqliteDatabase(file.Path);
        var second = new SqliteDatabase(file.Path);
        await RunConcurrentlyAsync(
            async () =>
            {
                await first.InitializeAsync();
                return true;
            },
            async () =>
            {
                await second.InitializeAsync();
                return true;
            });
        await using var connection = await first.OpenConnectionAsync();
        Assert.Equal(4, await UserVersionAsync(connection));
    }

    private static async Task<T[]> RunConcurrentlyAsync<T>(params Func<Task<T>>[] operations)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = operations.Length;
        var tasks = operations.Select(operation => Task.Run(async () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                ready.TrySetResult();
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await operation();
        })).ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static ConversationMessage Message(ConversationId id, string content) =>
        new(Guid.NewGuid(), id, "user", content, Now);

    private static string TogglePathCase(string path) => new(path.Select(character =>
        char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)).ToArray());

    private static void CopyTestSidecar(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void DeleteTestSidecar(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static ActionJournalEntry NewAction(ActionId id, string status, DateTimeOffset at) =>
        new(id, "home.set_light", """{"entity_id":"light.test","state":"on"}""", "request-hash", status, at, at, 1);

    private static AuditEventRecord NewAudit(DateTimeOffset at) =>
        new(Guid.NewGuid(), "turn.completed", "turn-1", """{"status":"Completed"}""", at);

    private static async Task CreateVersionOneFixtureAsync(
        string databasePath,
        ConversationId conversationId,
        Guid messageId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        using var schemaStream = typeof(SqlitePersistenceTests).Assembly.GetManifestResourceStream(
            "PersonalAgent.IntegrationTests.Fixtures.001-initial-v1.sql")
            ?? throw new InvalidOperationException("The frozen version-1 schema fixture is missing.");
        using var schemaReader = new StreamReader(schemaStream);
        await using var schema = connection.CreateCommand();
        schema.CommandText = await schemaReader.ReadToEndAsync();
        await schema.ExecuteNonQueryAsync();
        await using var createLegacy = connection.CreateCommand();
        createLegacy.CommandText = """
            INSERT INTO conversations VALUES
                ($conversation, 'owner', '', '2026-01-01T00:00:00.0000000+00:00',
                 '2026-01-02T00:00:00.0000000+00:00', 1);
            INSERT INTO messages VALUES
                ($message, $conversation, NULL, 1, 'user', 'before upgrade',
                 '2026-01-02T00:00:00.0000000+00:00');
            PRAGMA user_version = 1;
            """;
        createLegacy.Parameters.AddWithValue("$conversation", conversationId.Value.ToString("D"));
        createLegacy.Parameters.AddWithValue("$message", messageId.ToString("D"));
        await createLegacy.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TryUpdateAsync(SqliteMemoryStore store, MemoryFact fact, long expectedVersion)
    {
        try
        {
            await store.SaveAsync(fact, expectedVersion, CancellationToken.None);
            return true;
        }
        catch (PersistenceConcurrencyException)
        {
            return false;
        }
    }

    private static async Task WaitForConversationDeletionAsync(
        IConversationStore conversations,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await conversations.GetConversationAsync(conversationId, "owner", cancellationToken) is not null)
        {
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    private static string SqliteValueForTest(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static async Task<int> UserVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class CallbackClock(Func<DateTimeOffset> read) : IClock
    {
        public DateTimeOffset UtcNow => read();
    }
}
