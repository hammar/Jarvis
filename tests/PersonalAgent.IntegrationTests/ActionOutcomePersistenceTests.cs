using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class ActionOutcomePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OutcomeAndStatusCommitTogetherAndRejectStaleOrCancelledReplacements()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteActionJournalStore(database);
        var prepared = await store.CreateAsync(Prepared(), CancellationToken.None);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_outcome BEFORE UPDATE OF outcome_json ON actions
            BEGIN SELECT RAISE(ABORT, 'controlled outcome failure'); END;
            """;
        await command.ExecuteNonQueryAsync();
        var outcome = new ToolDispatchResult("Succeeded", """{"value":"stored-once"}""", null,
            prepared.Id, new ApprovalId(Guid.NewGuid()), Now.AddMinutes(5));
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await store.UpdateStatusAsync(prepared.Id, outcome.Status, prepared.Version, Now, CancellationToken.None, outcome));
        Assert.Equal(prepared, await store.GetAsync(prepared.Id, CancellationToken.None));
        command.CommandText = "DROP TRIGGER reject_outcome;";
        await command.ExecuteNonQueryAsync();
        var completed = await store.UpdateStatusAsync(prepared.Id, outcome.Status, prepared.Version, Now, CancellationToken.None, outcome);
        Assert.Equal(outcome, completed.Outcome);
        Assert.Equal(outcome, (await new SqliteActionJournalStore(new SqliteDatabase(file.Path))
            .GetAsync(prepared.Id, CancellationToken.None))!.Outcome);
        var loser = outcome with { Status = "Failed", ReasonCode = "losing_result" };
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.UpdateStatusAsync(prepared.Id, loser.Status, prepared.Version, Now, CancellationToken.None, loser));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.UpdateStatusAsync(prepared.Id, loser.Status, completed.Version, Now, cancellation.Token, loser));
        Assert.Equal(completed, await store.GetAsync(prepared.Id, CancellationToken.None));
        var executing = await store.UpdateStatusAsync(prepared.Id, "Executing", completed.Version, Now, CancellationToken.None);
        Assert.Null(executing.Outcome);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing-identity")]
    [InlineData("status")]
    [InlineData("nonterminal")]
    [InlineData("result-size")]
    [InlineData("reason-size")]
    [Trait("Category", "Integration")]
    public async Task InvalidOrOversizedOutcomeCannotChangeJournalState(string invalid)
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteActionJournalStore(database);
        var prepared = await store.CreateAsync(Prepared(), CancellationToken.None);
        var outcome = new ToolDispatchResult("Succeeded", "{}", null, prepared.Id);
        outcome = invalid switch
        {
            "identity" => outcome with { ActionId = new ActionId(Guid.NewGuid()) },
            "missing-identity" => outcome with { ActionId = null },
            "status" => outcome with { Status = "Failed" },
            "nonterminal" => outcome with { Status = "Executing" },
            "result-size" => outcome with { ResultJson = new string('x', 1_000_001) },
            _ => outcome with { ReasonCode = new string('x', 257) }
        };
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.UpdateStatusAsync(prepared.Id, invalid == "nonterminal" ? "Executing" : "Succeeded",
                prepared.Version, Now, CancellationToken.None, outcome));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CreateAsync(Prepared() with { Outcome = outcome }, CancellationToken.None));
        Assert.Equal(prepared, await store.GetAsync(prepared.Id, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExactSizeLimitsRoundTripWithoutTruncatingOrCopyingIntoMessagesOrEvents()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var store = new SqliteActionJournalStore(database);
        var prepared = await store.CreateAsync(Prepared(), CancellationToken.None);
        var outcome = new ToolDispatchResult("Unknown", "\"" + new string('x', 999_998) + "\"",
            new string('r', 256), prepared.Id);
        await store.UpdateStatusAsync(prepared.Id, "Unknown", prepared.Version, Now, CancellationToken.None, outcome);
        Assert.Equal(outcome, (await store.GetAsync(prepared.Id, CancellationToken.None))!.Outcome);
        await using var connection = await database.OpenConnectionAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT (SELECT COUNT(*) FROM messages) + (SELECT COUNT(*) FROM turn_events);";
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VersionFiveUpgradePreservesLegacyUnknownAndConversationWithoutInventingResponse()
    {
        using var file = IsolatedDatabaseFile.Create();
        var legacy = Prepared() with { Status = "Unknown" };
        await using (var connection = new SqliteConnection($"Data Source={file.Path}"))
        {
            await connection.OpenAsync();
            var assembly = typeof(SqliteDatabase).Assembly;
            foreach (var name in assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Persistence.Migrations.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(5))
            {
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(resource);
                await using var migration = connection.CreateCommand();
                migration.CommandText = await reader.ReadToEndAsync();
                await migration.ExecuteNonQueryAsync();
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                PRAGMA user_version = 5;
                INSERT INTO conversations(id,owner_id,created_at_utc,updated_at_utc)
                VALUES ('conversation','owner',$now,$now);
                INSERT INTO messages(message_id,conversation_id,sequence,role,content,created_at_utc)
                VALUES ('message','conversation',1,'assistant','preserved answer',$now);
                INSERT INTO actions(id,action_type,canonical_arguments,request_hash,status,created_at_utc,updated_at_utc,version)
                VALUES ($id,'fixture','{}','hash','Unknown',$now,$now,1);
                """;
            insert.Parameters.AddWithValue("$now", Now.ToString("O"));
            insert.Parameters.AddWithValue("$id", legacy.Id.Value.ToString("D"));
            await insert.ExecuteNonQueryAsync();
        }
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        await database.InitializeAsync();
        Assert.Equal(legacy, await new SqliteActionJournalStore(database).GetAsync(legacy.Id, CancellationToken.None));
        await using var verify = await database.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT content FROM messages;";
        Assert.Equal("preserved answer", (string?)await command.ExecuteScalarAsync());
        command.CommandText = "UPDATE actions SET outcome_json = '{\"Status\":\"Succeeded\"}';";
        await Assert.ThrowsAsync<SqliteException>(async () => await command.ExecuteNonQueryAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VersionSevenUpgradeBackfillsDurableActionOwnerAndTurnAttribution()
    {
        using var file = IsolatedDatabaseFile.Create();
        var actionId = new ActionId(Guid.NewGuid());
        var malformedActionId = new ActionId(Guid.NewGuid());
        var turnId = TurnId.New();
        await using (var connection = new SqliteConnection($"Data Source={file.Path}"))
        {
            await connection.OpenAsync();
            var assembly = typeof(SqliteDatabase).Assembly;
            foreach (var name in assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Persistence.Migrations.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(7))
            {
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(resource);
                await using var migration = connection.CreateCommand();
                migration.CommandText = await reader.ReadToEndAsync();
                await migration.ExecuteNonQueryAsync();
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                PRAGMA user_version = 7;
                INSERT INTO actions(id,action_type,canonical_arguments,request_hash,status,created_at_utc,updated_at_utc,version)
                VALUES ($id,'home.set_light',$arguments,'hash','Unknown',$now,$now,1);
                INSERT INTO actions(id,action_type,canonical_arguments,request_hash,status,created_at_utc,updated_at_utc,version)
                VALUES ($malformedId,'home.set_light',$malformedArguments,'malformed-hash','Unknown',$now,$now,1);
                """;
            insert.Parameters.AddWithValue("$id", actionId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$arguments", $$"""{"OwnerId":"owner","TurnId":"{{turnId.Value:D}}"}""");
            insert.Parameters.AddWithValue("$malformedId", malformedActionId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$malformedArguments", """{"OwnerId":"owner","TurnId":"not-a-turn"}""");
            insert.Parameters.AddWithValue("$now", Now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var journal = new SqliteActionJournalStore(database);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT action_owner_id, action_turn_id
                FROM actions WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", actionId.Value.ToString("D"));
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("owner", reader.GetString(0));
                Assert.Equal(turnId.Value.ToString("D"), reader.GetString(1));
            }
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", malformedActionId.Value.ToString("D"));
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(await reader.IsDBNullAsync(0));
                Assert.True(await reader.IsDBNullAsync(1));
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await journal.ReadForTurnAsync("owner", turnId, CancellationToken.None));
    }

    private static ActionJournalEntry Prepared() =>
        new(new ActionId(Guid.NewGuid()), "fixture", "{}", "hash", "Prepared", Now, Now, 1);
}
