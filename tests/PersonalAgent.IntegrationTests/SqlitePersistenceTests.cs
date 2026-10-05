using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class SqlitePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

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
        Assert.Equal(2, await UserVersionAsync(connection));
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
        Assert.Equal(2, await UserVersionAsync(migrated));
        await using var table = migrated.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'cloud_consents';";
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
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

        var outcomes = await Task.WhenAll(
            TryUpdateAsync(store, created with { Value = "dim" }, created.Version),
            TryUpdateAsync(store, created with { Value = "bright" }, created.Version));

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
        await conversations.AppendMessageAsync(Message(oldConversation, "expired"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(retainedConversation, "retained"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(runningConversation, "running turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(approvalConversation, "approval turn"), CancellationToken.None);
        await conversations.AppendMessageAsync(Message(interruptedConversation, "interrupted turn"), CancellationToken.None);
        var runningTurn = TurnId.New();
        var approvalTurn = TurnId.New();
        var interruptedTurn = TurnId.New();
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
                WHERE id IN ($running_conversation, $approval_conversation, $interrupted_conversation);
                """;
            update.Parameters.AddWithValue("$old", SqliteValueForTest(Now.AddDays(-91)));
            update.Parameters.AddWithValue("$recent", SqliteValueForTest(Now.AddDays(-89)));
            update.Parameters.AddWithValue("$old_conversation", oldConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$recent_conversation", retainedConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$running_conversation", runningConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$approval_conversation", approvalConversation.Value.ToString("D"));
            update.Parameters.AddWithValue("$interrupted_conversation", interruptedConversation.Value.ToString("D"));
            await update.ExecuteNonQueryAsync();
        }

        var retention = new SqliteRetentionService(database, clock, new SqliteRetentionOptions());
        Assert.Equal(new SqliteRetentionResult(1, 1), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Equal(new SqliteRetentionResult(0, 0), await retention.CleanupExpiredAsync(CancellationToken.None));
        Assert.Empty(await conversations.ReadRecentAsync(oldConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(retainedConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(runningConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(approvalConversation, 10, CancellationToken.None));
        Assert.Single(await conversations.ReadRecentAsync(interruptedConversation, 10, CancellationToken.None));
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var turns = connection.CreateCommand();
            turns.CommandText = "SELECT id, status FROM turns WHERE id IN ($running, $approval, $interrupted) ORDER BY id;";
            turns.Parameters.AddWithValue("$running", runningTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$approval", approvalTurn.Value.ToString("D"));
            turns.Parameters.AddWithValue("$interrupted", interruptedTurn.Value.ToString("D"));
            await using var reader = await turns.ExecuteReaderAsync();
            var retainedTurns = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                retainedTurns.Add(reader.GetString(0), reader.GetString(1));
            }

            Assert.Equal(3, retainedTurns.Count);
            Assert.Equal(TurnStatus.Running.ToString(), retainedTurns[runningTurn.Value.ToString("D")]);
            Assert.Equal(
                TurnStatus.WaitingForApproval.ToString(),
                retainedTurns[approvalTurn.Value.ToString("D")]);
            Assert.Equal(TurnStatus.Interrupted.ToString(), retainedTurns[interruptedTurn.Value.ToString("D")]);
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
            SqliteBackupRestoreService.RestoreRollbackPath(file.Path),
            file.Path + ".restore-original-wal",
            file.Path + ".restore-original-shm",
            SqliteBackupRestoreService.RestoreMarkerPath(file.Path) + ".tmp",
            SqliteBackupRestoreService.RestoreMarkerPath(file.Path),
            file.Path + "-wal",
            file.Path + "-shm"
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedRestorePreservesCommittedWalDataAndStartupRecoversPreparedReplacement()
    {
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

        var liveDatabase = new SqliteDatabase(file.Path);
        var failingRestore = new SqliteBackupRestoreService(liveDatabase, () => throw new IOException("replacement failed"));
        await Assert.ThrowsAsync<IOException>(() => failingRestore.RestoreAsync(backupPath));
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
        await Assert.ThrowsAsync<SqliteException>(async () => await missingDirectoryDatabase.OpenConnectionAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SimultaneousStartupAppliesEachMigrationOnlyOnce()
    {
        using var file = IsolatedDatabaseFile.Create();
        var first = new SqliteDatabase(file.Path);
        var second = new SqliteDatabase(file.Path);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        await using var connection = await first.OpenConnectionAsync();
        Assert.Equal(2, await UserVersionAsync(connection));
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
        await using var createLegacy = connection.CreateCommand();
        createLegacy.CommandText = """
            CREATE TABLE conversations (
                id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL DEFAULT '',
                created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE turns (
                id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id),
                status TEXT NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE messages (
                message_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id),
                turn_id TEXT REFERENCES turns(id), sequence INTEGER NOT NULL, role TEXT NOT NULL,
                content TEXT NOT NULL, created_at_utc TEXT NOT NULL);
            CREATE TABLE turn_events (
                event_id TEXT PRIMARY KEY, turn_id TEXT NOT NULL REFERENCES turns(id),
                sequence INTEGER NOT NULL, event_type TEXT NOT NULL, payload_json TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL);
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
}
