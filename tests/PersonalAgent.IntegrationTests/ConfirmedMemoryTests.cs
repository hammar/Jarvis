using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Memory;
using PersonalAgent.Application.Tools;
using PersonalAgent.Application.Tools.Memory;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ConfirmedMemoryTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalWorkflowValidatesDependenciesOwnerAndPageBounds()
    {
        using var fixture = await Fixture.CreateAsync();
        var valid = fixture.ApprovalWorkflow();

        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            null!, fixture.Journal, fixture.Approvals, fixture.Dispatcher, fixture.Memory, fixture.Proposals, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, null!, fixture.Approvals, fixture.Dispatcher, fixture.Memory, fixture.Proposals, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, fixture.Journal, null!, fixture.Dispatcher, fixture.Memory, fixture.Proposals, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, fixture.Journal, fixture.Approvals, null!, fixture.Memory, fixture.Proposals, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, fixture.Journal, fixture.Approvals, fixture.Dispatcher, null!, fixture.Proposals, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, fixture.Journal, fixture.Approvals, fixture.Dispatcher, fixture.Memory, null!, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() => new ApprovalWorkflowService(
            fixture.ApprovalStore, fixture.Journal, fixture.Approvals, fixture.Dispatcher, fixture.Memory, fixture.Proposals, null!));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await valid.ReadRecentAsync(" ", 10, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await valid.ReadRecentAsync("owner", 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await valid.ReadRecentAsync("owner", 101, default));
    }

    [Fact]
    public async Task ExplicitSensitiveFactRequiresApprovalAndSurvivesRuntimeAndStoreLoss()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "household", Key: "explicit medical note", Value: "synthetic private note"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "synthetic", 10, default));
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        Assert.Equal("synthetic private note", preview.Command.Change.Value);
        var result = await fixture.Proposals.ResolveAsync("owner", pending.ActionId.Value, preview.Approval.ExpectedVersion, true, default);
        Assert.Equal("Succeeded", result.Status);
        var receipt = JsonSerializer.Deserialize<MemoryReceipt>(result.ResultJson!)!;
        var reopened = new SqliteConfirmedMemoryStore(new SqliteDatabase(fixture.Database.DatabasePath), fixture.Clock);
        var fact = Assert.Single(await reopened.SearchAsync("owner", "synthetic", 10, default));
        Assert.Equal(receipt.FactId, fact.Id);
        Assert.Equal("LocalOnly", fact.PrivacyClass);
        Assert.Equal("OwnerMessage", fact.SourceType);
        Assert.True(fact.SourceAvailable);
        Assert.NotNull(fact.SourceMessageId);
        Assert.Equal(fixture.Clock.UtcNow, fact.CreatedAtUtc);
        Assert.Equal(fixture.Clock.UtcNow, fact.UpdatedAtUtc);
        Assert.Equal(result, await fixture.RestartedDispatcher().DispatchAsync(request, default));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Proposals.ResolveAsync("owner", pending.ActionId.Value, preview.Approval.ExpectedVersion, true, default));
    }

    [Fact]
    public async Task OwnerWorkflowListsRedactedMemoryApprovalAndCompletesTheExactProposal()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(
            new("create", Subject: "synthetic room", Key: "lamp", Value: "blue"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var workflow = fixture.ApprovalWorkflow();

        var summaries = await workflow.ReadRecentAsync("owner", 10, default);
        var summary = Assert.Single(summaries);
        Assert.Equal("memory.propose_change", summary.ToolName);
        Assert.Equal("AwaitingApproval", summary.ActionStatus);
        Assert.DoesNotContain("OwnerId", summary.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Explicit synthetic owner request", summary.ArgumentsJson, StringComparison.Ordinal);
        Assert.Empty(await workflow.ReadRecentAsync("another-owner", 10, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await workflow.ResolveAsync("another-owner", pending.ApprovalId!.Value, 1, true, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await workflow.ReadRecentAsync("owner", 0, default));

        var result = await workflow.ResolveAsync(
            "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, true, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.Single(await fixture.Memory.SearchAsync("owner", "synthetic room lamp", 10, default));
        Assert.Single(await fixture.Memory.ReadActiveAsync("owner", 10, default));
        Assert.Equal("Succeeded", Assert.Single(await workflow.ReadRecentAsync("owner", 10, default)).ActionStatus);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await workflow.ResolveAsync("owner", pending.ApprovalId.Value, summary.Approval.ExpectedVersion, true, default));
    }

    [Fact]
    public async Task ConflictsRequireExplicitResolutionAndRetainedFactsNeedDistinctContext()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("Example Blue");
        var conflict = await fixture.RequestAsync(new("create", Subject: " BATHROOM ", Key: " PAINT ", Value: "Example Red"));
        Assert.Equal("Failed", (await fixture.ApproveAsync(conflict)).Status);
        Assert.Equal("Example Blue", (await fixture.Memory.GetAsync("owner", first.Id, default))!.Value);
        var found = await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default);
        var references = found.Select(fact => new MemoryConflictArguments(fact.Id.Value, fact.Version)).ToArray();
        var retain = await fixture.RequestAsync(new("retain_both", Subject: "Bathroom", Key: "paint",
            Value: "Example Red", Conflicts: references, DistinguishingContext: "ceiling"));
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(retain)).Status);
        var both = await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default);
        Assert.Equal(2, both.Count);
        var sameContext = await fixture.RequestAsync(new("retain_both", Subject: "Bathroom", Key: "paint",
            Value: "Example Green", Conflicts: both.Select(f => new MemoryConflictArguments(f.Id.Value, f.Version)).ToArray(),
            DistinguishingContext: "CEILING"));
        Assert.Equal("Failed", (await fixture.ApproveAsync(sameContext)).Status);
        var supersede = await fixture.RequestAsync(new("supersede", Subject: "Bathroom", Key: "paint",
            Value: "Example White", Conflicts: both.Select(f => new MemoryConflictArguments(f.Id.Value, f.Version)).ToArray()));
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(supersede)).Status);
        var active = Assert.Single(await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default));
        Assert.Equal("Example White", active.Value);
        Assert.NotNull(active.SupersedesId);
        Assert.Null(await fixture.Memory.GetAsync("owner", first.Id, default));
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "Blue", 10, default));
    }

    [Fact]
    public async Task ChangedConflictSetCannotSlipBetweenReviewAndCommit()
    {
        using var fixture = await Fixture.CreateAsync();
        var left = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "left"));
        var right = await fixture.RequestAsync(new("create", Subject: " BATHROOM", Key: "paint ", Value: "right"));
        var leftPending = await fixture.Dispatcher.DispatchAsync(left, default);
        var rightPending = await fixture.Dispatcher.DispatchAsync(right, default);
        await fixture.Approvals.ResolveAsync(leftPending.ApprovalId!.Value, "owner", 1, true, default);
        await fixture.Approvals.ResolveAsync(rightPending.ApprovalId!.Value, "owner", 1, true, default);
        var results = await Task.WhenAll(
            fixture.Dispatcher.DispatchAsync(left, default).AsTask(),
            fixture.RestartedDispatcher().DispatchAsync(right, default).AsTask());
        Assert.Single(results, result => result.Status == "Succeeded");
        Assert.Single(results, result => result.Status == "Failed");
        Assert.Single(await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default));
    }

    [Fact]
    public async Task RetainedFactCanBeEditedInPlaceWithExactSiblingSnapshotAndDistinctContext()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("walls");
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(
            new("retain_both", Subject: "bathroom", Key: "paint", Value: "ceiling",
                Conflicts: [new(first.Id.Value, first.Version)], DistinguishingContext: "ceiling")))).Status);
        var retained = (await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default))
            .Single(fact => fact.Id != first.Id);
        var update = new MemoryChangeArguments("update", retained.Id.Value, retained.Version,
            "bathroom", "paint", "new ceiling", [new(first.Id.Value, first.Version)], "ceiling");
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(update))).Status);
        var edited = (await fixture.Memory.GetAsync("owner", retained.Id, default))!;
        Assert.Equal(2, edited.Version);
        Assert.Equal("new ceiling", edited.Value);
        Assert.Equal("ceiling", edited.DistinguishingContext);
        Assert.Equal(first, await fixture.Memory.GetAsync("owner", first.Id, default));
        Assert.Equal("Failed", (await fixture.ApproveAsync(await fixture.RequestAsync(
            update with { ExpectedVersion = 2, Conflicts = [] }))).Status);
    }

    [Fact]
    public async Task FullConflictGroupRejectsGrowthButAllowsInPlaceUpdatesAndSupersession()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("walls");
        await fixture.SqlAsync("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < 99)
            INSERT INTO memory_facts (id, subject, fact_key, value, source_id, privacy_class,
                created_at_utc, updated_at_utc, version, distinguishing_context)
            SELECT printf('00000000-0000-0000-0000-%012x', n.x), f.subject, f.fact_key, f.value, f.source_id,
                f.privacy_class, f.created_at_utc, f.updated_at_utc, f.version, 'part ' || n.x
            FROM n CROSS JOIN memory_facts f;
            """);
        var full = await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default);
        Assert.Equal(100, full.Count);
        var references = full.Select(fact => new MemoryConflictArguments(fact.Id.Value, fact.Version)).ToArray();
        Assert.Equal("Failed", (await fixture.ApproveAsync(await fixture.RequestAsync(new("retain_both",
            Subject: "bathroom", Key: "paint", Value: "too many", Conflicts: references, DistinguishingContext: "new part")))).Status);
        var other = await fixture.CreateFactAsync("kitchen", "kitchen");
        Assert.Equal("Failed", (await fixture.ApproveAsync(await fixture.RequestAsync(new("update", other.Id.Value,
            other.Version, "bathroom", "paint", "too many", references, "another part")))).Status);
        Assert.Equal(other, await fixture.Memory.GetAsync("owner", other.Id, default));
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(new("update", first.Id.Value,
            first.Version, "bathroom", "paint", "updated walls",
            references.Where(reference => reference.Id != first.Id.Value).ToArray(), "walls")))).Status);
        full = await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default);
        Assert.Equal(100, full.Count);
        Assert.Equal(2, full.Single(fact => fact.Id == first.Id).Version);
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(new("supersede",
            Subject: "bathroom", Key: "paint", Value: "replacement",
            Conflicts: full.Select(fact => new MemoryConflictArguments(fact.Id.Value, fact.Version)).ToArray())))).Status);
        Assert.Single(await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default));
    }

    [Fact]
    public async Task QualifiersAreSearchableAndTheirOnlyUpdatesAndDeletionSynchronizeFts()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("red");
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(new("retain_both",
            Subject: "bathroom", Key: "paint", Value: "red",
            Conflicts: [new(first.Id.Value, first.Version)], DistinguishingContext: "ceiling")))).Status);
        var retained = Assert.Single(await fixture.Memory.SearchAsync("owner", "ceiling", 10, default));
        Assert.Equal(retained.Id, Assert.Single(await fixture.Memory.SearchAsync("owner", "paint ceiling", 10, default)).Id);
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(new("update",
            retained.Id.Value, retained.Version, retained.Subject, retained.Key, retained.Value,
            [new(first.Id.Value, first.Version)], "vaulted")))).Status);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "ceiling", 10, default));
        var updated = Assert.Single(await fixture.Memory.SearchAsync("owner", "paint vaulted", 10, default));
        Assert.Equal(retained.Id, updated.Id);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(
            new("delete", updated.Id.Value, updated.Version)))).Status);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "vaulted", 10, default));
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM memory_facts_fts WHERE memory_facts_fts MATCH 'vaulted';";
        Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
        Assert.NotNull(await fixture.Memory.GetAsync("owner", first.Id, default));
    }

    [Fact]
    public async Task EscapedSearchEvidenceIsSizeBoundedWithMatchingCitationsAndExplicitTruncation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CreateFactAsync(new string('\\', 8192));
        await fixture.SqlAsync("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < 99)
            INSERT INTO memory_facts (id, subject, fact_key, value, source_id, privacy_class,
                created_at_utc, updated_at_utc, version)
            SELECT printf('00000000-0000-0000-0000-%012x', n.x), f.subject, f.fact_key, f.value, f.source_id, f.privacy_class,
                f.created_at_utc, f.updated_at_utc, f.version FROM n CROSS JOIN memory_facts f;
            """);
        var request = await fixture.ReadRequestAsync("memory.search", new MemorySearchArguments("bathroom", 100));
        var result = await fixture.Dispatcher.DispatchAsync(request, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.InRange(result.ResultJson!.Length, 1, 900_000);
        var evidence = JsonSerializer.Deserialize<MemoryEvidence>(result.ResultJson)!;
        Assert.True(evidence.Truncated);
        Assert.InRange(evidence.Facts.Count, 1, 99);
        Assert.Equal(evidence.Facts.Select(fact => new MemoryReference(fact.Id, fact.Version)), evidence.MemoryReferences);
        Assert.Equal(result, await fixture.RestartedDispatcher().DispatchAsync(request, default));
        var fact = evidence.Facts[0];
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(await fixture.RequestAsync(
            new("delete", fact.Id.Value, fact.Version)))).Status);
        Assert.Equal("memory_result_invalidated", (await fixture.Dispatcher.DispatchAsync(request, default)).ReasonCode);
    }

    [Fact]
    public async Task StaleMissingReceiptObservationCannotOverrideConcurrentCommitAcrossDispatchers()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "once"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var claimed = new AsyncGate();
        var committed = new AsyncGate();
        var staleObservation = new AsyncGate();
        var originalJournal = new InterleavingJournal(fixture.Journal)
        {
            AfterStatus = status => status == "Executing" ? claimed.PauseAsync() : Task.CompletedTask,
            BeforeStatus = status => status == "Succeeded" ? committed.PauseAsync() : Task.CompletedTask
        };
        var recoveryJournal = new InterleavingJournal(fixture.Journal)
        {
            BeforeStatus = status => status == "Failed" ? staleObservation.PauseAsync() : Task.CompletedTask
        };
        var original = fixture.RestartedDispatcher(originalJournal).DispatchAsync(request, default).AsTask();
        await claimed.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var recovering = fixture.RestartedDispatcher(recoveryJournal).DispatchAsync(request, default).AsTask();
        await staleObservation.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        claimed.Release.SetResult();
        await committed.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        staleObservation.Release.SetResult();
        var recovered = await recovering.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Succeeded", recovered.Status);
        committed.Release.SetResult();
        Assert.Equal(recovered, await original.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotNull(JsonSerializer.Deserialize<MemoryReceipt>(recovered.ResultJson!));
        Assert.Equal(1, Assert.Single(await fixture.Memory.SearchAsync("owner", "once", 10, default)).Version);
    }

    [Fact]
    public async Task InvalidationDuringPreparationReturnsContentFreeOutcomeInsteadOfParsingRedactedEnvelope()
    {
        using var fixture = await Fixture.CreateAsync();
        var fact = await fixture.CreateFactAsync("secret");
        var request = await fixture.RequestAsync(new("update", fact.Id.Value, fact.Version, "bathroom", "paint", "updated"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId!.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        var deletion = await fixture.RequestAsync(new("delete", fact.Id.Value, fact.Version));
        var journal = new InterleavingJournal(fixture.Journal)
        {
            AfterFirstRead = async () => Assert.Equal("Succeeded", (await fixture.ApproveAsync(deletion)).Status)
        };
        var result = await fixture.RestartedDispatcher(journal).DispatchAsync(request, default);
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("memory_result_invalidated", result.ReasonCode);
        Assert.Null(result.ResultJson);
        Assert.Null(await fixture.Memory.GetAsync("owner", fact.Id, default));
    }

    [Fact]
    public async Task SettlementBeforeCommitPreventsALateMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "not applied"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        Assert.Equal("Failed", (await fixture.RestartedDispatcher().DispatchAsync(request, default)).Status);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Memory.CommitAsync(preview.Command, default));
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "paint", 10, default));
        Assert.Null(await fixture.Memory.GetReceiptAsync("owner", entry.Id, default));
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task EquivalentMemoryFailuresShareTheTurnBudgetAcrossFreshCallIds(string operation, bool hostCompletion)
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("walls");
        await fixture.ApproveAsync(await fixture.RequestAsync(new("retain_both", Subject: "bathroom", Key: "paint",
            Value: "ceiling", Conflicts: [new(first.Id.Value, first.Version)], DistinguishingContext: "ceiling")));
        var args = new MemoryChangeArguments(operation, operation == "update" ? first.Id.Value : null,
            operation == "update" ? first.Version : 0, "bathroom", "paint", "not applied",
            DistinguishingContext: "walls");
        var template = await fixture.RequestAsync(args);
        async Task<ToolDispatchResult> ApproveAsync(ToolDispatchRequest request)
        {
            if (!hostCompletion)
            {
                return await fixture.ApproveAsync(request);
            }
            var pending = await fixture.Dispatcher.DispatchAsync(request, default);
            return pending.Status == "AwaitingApproval"
                ? await fixture.Proposals.ResolveAsync("owner", pending.ActionId!.Value, 1, true, default)
                : pending;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var request = template with { CallId = Guid.NewGuid().ToString() };
            var result = await ApproveAsync(request);
            Assert.Equal(attempt < 2 ? "Failed" : "Rejected", result.Status);
            Assert.Equal(attempt < 2 ? "memory_changed_review_again" : "equivalent_failure_limit", result.ReasonCode);
            Assert.Equal(result, await fixture.Dispatcher.DispatchAsync(request, default));
        }
        var distinct = template with
        {
            CallId = Guid.NewGuid().ToString(),
            ArgumentsJson = JsonSerializer.Serialize(args with { Value = "a different proposal" })
        };
        Assert.Equal("Failed", (await ApproveAsync(distinct)).Status);
        fixture.Dispatcher.ReleaseTurn(template.TurnId);
        Assert.Equal("Failed", (await ApproveAsync(template with { CallId = Guid.NewGuid().ToString() })).Status);
        Assert.Equal(2, (await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default)).Count);
        Assert.Equal(first, await fixture.Memory.GetAsync("owner", first.Id, default));
    }

    [Fact]
    public async Task DeleteRedactsDerivedCopiesAndOldCallsCannotRestoreThem()
    {
        using var fixture = await Fixture.CreateAsync();
        var created = await fixture.CreateFactAsync("private_blue_sentinel");
        var get = await fixture.ReadRequestAsync("memory.get", new MemoryGetArguments(created.Id.Value));
        var read = await fixture.Dispatcher.DispatchAsync(get, default);
        Assert.Contains("private_blue_sentinel", read.ResultJson!, StringComparison.Ordinal);
        var search = await fixture.ReadRequestAsync("memory.search", new MemorySearchArguments("private_blue_sentinel"));
        Assert.Contains("private_blue_sentinel", (await fixture.Dispatcher.DispatchAsync(search, default)).ResultJson!, StringComparison.Ordinal);
        var pendingUpdate = await fixture.RequestAsync(new("update", created.Id.Value, created.Version,
            "bathroom", "paint", "private_blue_sentinel updated"));
        var pending = await fixture.Dispatcher.DispatchAsync(pendingUpdate, default);
        var deletion = await fixture.RequestAsync(new("delete", created.Id.Value, created.Version));
        var deleted = await fixture.ApproveAsync(deletion);
        Assert.Equal("Succeeded", deleted.Status);
        Assert.Null(await fixture.Memory.GetAsync("owner", created.Id, default));
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "private_blue_sentinel", 10, default));
        foreach (var request in new[] { get, search, pendingUpdate })
        {
            var replay = await fixture.RestartedDispatcher().DispatchAsync(request, default);
            Assert.Equal("memory_result_invalidated", replay.ReasonCode);
            Assert.Null(replay.ResultJson);
        }
        var invalidatedApproval = (await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 20, default))
            .Single(summary => summary.ActionId == pending.ActionId);
        Assert.Equal("memory.propose_change", invalidatedApproval.ToolName);
        Assert.Equal("invalidated", invalidatedApproval.Operation);
        Assert.Equal("Invalidated", invalidatedApproval.ActionStatus);
        Assert.Equal("""{"invalidated":true}""", invalidatedApproval.ArgumentsJson);
        Assert.DoesNotContain("private_blue_sentinel", invalidatedApproval.ArgumentsJson, StringComparison.Ordinal);
        var invalidatedDecision = await fixture.ApprovalWorkflow().ResolveAsync(
            "owner", pending.ApprovalId!.Value, invalidatedApproval.Approval.ExpectedVersion, true, default);
        Assert.Equal("Rejected", invalidatedDecision.Status);
        Assert.Equal("memory_result_invalidated", invalidatedDecision.ReasonCode);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default));
        await fixture.SqlAsync(
            $"UPDATE actions SET memory_owner_id = 'another-owner' WHERE id = '{pending.ActionId!.Value.Value:D}';");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 20, default));
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM actions WHERE canonical_arguments LIKE '%private_blue_sentinel%'
                OR outcome_json LIKE '%private_blue_sentinel%';
            """;
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM memory_tombstones;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM memory_facts_fts WHERE memory_facts_fts MATCH 'private_blue_sentinel';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        Assert.Equal(deleted, await fixture.RestartedDispatcher().DispatchAsync(deletion, default));
    }

    [Fact]
    public async Task OwnerWorkflowExecutesOnlyTheConfirmedMemoryProposal()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "blue"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, default));
        Assert.Equal("memory.propose_change", summary.ToolName);

        var result = await workflow.ResolveAsync(
            "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, true, default);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("blue", Assert.Single(await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default)).Value);

        var rejectedRequest = await fixture.RequestAsync(
            new("create", Subject: "kitchen", Key: "paint", Value: "red"));
        var rejectedPending = await fixture.Dispatcher.DispatchAsync(rejectedRequest, default);
        var rejectedSummary = (await workflow.ReadRecentAsync("owner", 10, default))
            .Single(item => item.ActionId == rejectedPending.ActionId);
        var rejected = await workflow.ResolveAsync(
            "owner", rejectedPending.ApprovalId!.Value, rejectedSummary.Approval.ExpectedVersion, false, default);
        Assert.Equal("Rejected", rejected.Status);
    }

    [Fact]
    public async Task DelayedReadPublicationCannotReintroduceDeletedContent()
    {
        using var fixture = await Fixture.CreateAsync();
        var fact = await fixture.CreateFactAsync("late_result_secret");
        var request = await fixture.ReadRequestAsync("memory.get", new MemoryGetArguments(fact.Id.Value));
        var result = await fixture.Dispatcher.DispatchAsync(request, default);
        var old = (await fixture.Journal.GetAsync(result.ActionId!.Value, default))!;
        var deletion = await fixture.RequestAsync(new("delete", fact.Id.Value, fact.Version));
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(deletion)).Status);
        var late = await fixture.Journal.UpdateStatusAsync(old.Id, "Succeeded", old.Version,
            fixture.Clock.UtcNow, default, result);
        Assert.True(late.MemoryInvalidated);
        Assert.Null(late.Outcome!.ResultJson);

        // Exercise first publication after deletion, when no result dependency was yet registered.
        var another = await fixture.ReadRequestAsync("memory.get", new MemoryGetArguments(fact.Id.Value));
        var raw = new ActionJournalEntry(ToolDispatcher.CreateActionId(another.TurnId, another.CallId),
            "memory.get", JsonSerializer.Serialize(new { OwnerId = "owner" }), "synthetic-hash", "Prepared",
            fixture.Clock.UtcNow, fixture.Clock.UtcNow, 1);
        var prepared = await fixture.Journal.CreateAsync(raw, default);
        var claimed = await fixture.Journal.UpdateStatusAsync(prepared.Id, "Executing", prepared.Version, fixture.Clock.UtcNow, default);
        var staleResult = result with { ActionId = claimed.Id };
        var invalidated = await fixture.Journal.UpdateStatusAsync(claimed.Id, "Succeeded", claimed.Version,
            fixture.Clock.UtcNow, default, staleResult);
        Assert.True(invalidated.MemoryInvalidated);
        Assert.Equal("memory_result_invalidated", invalidated.Outcome!.ReasonCode);
        Assert.Null(invalidated.Outcome.ResultJson);
    }

    [Fact]
    public async Task EditInvalidatesOldEvidenceAndRejectsStaleVersionsWithoutLosingOtherFacts()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateFactAsync("original");
        var other = await fixture.CreateFactAsync("unrelated", "kitchen");
        var get = await fixture.ReadRequestAsync("memory.get", new MemoryGetArguments(first.Id.Value));
        await fixture.Dispatcher.DispatchAsync(get, default);
        var update = await fixture.RequestAsync(new("update", first.Id.Value, first.Version, "bathroom", "paint", "changed"));
        Assert.Equal("Succeeded", (await fixture.ApproveAsync(update)).Status);
        var current = (await fixture.Memory.GetAsync("owner", first.Id, default))!;
        Assert.Equal(2, current.Version);
        Assert.Equal("changed", current.Value);
        Assert.Equal("memory_result_invalidated", (await fixture.Dispatcher.DispatchAsync(get, default)).ReasonCode);
        var stale = await fixture.RequestAsync(new("delete", first.Id.Value, 1));
        Assert.Equal("memory_result_invalidated", (await fixture.Dispatcher.DispatchAsync(stale, default)).ReasonCode);
        Assert.NotNull(await fixture.Memory.GetAsync("owner", other.Id, default));
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "original", 10, default));
    }

    [Fact]
    public async Task RejectionExpiryAndForgedSourceCannotCommit()
    {
        using var fixture = await Fixture.CreateAsync();
        var rejected = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "not saved"));
        var pending = await fixture.Dispatcher.DispatchAsync(rejected, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        Assert.Equal("Rejected", (await fixture.Proposals.ResolveAsync("owner", pending.ActionId.Value,
            preview.Approval.ExpectedVersion, false, default)).Status);
        var expired = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "expired"));
        pending = await fixture.Dispatcher.DispatchAsync(expired, default);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(5);
        Assert.Equal("Expired", (await fixture.Proposals.ResolveAsync("owner", pending.ActionId!.Value, 1, true, default)).Status);
        Assert.Equal("memory_source_unavailable", (await fixture.Dispatcher.DispatchAsync(expired with
        {
            CallId = "forged",
            CurrentUserRequest = "different request"
        }, default)).ReasonCode);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "paint", 10, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Proposals.PreviewAsync("another-owner", pending.ActionId.Value, default));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Memory.CommitAsync(preview.Command, default));
    }

    [Fact]
    public async Task ReceiptRecoversInterruptedSettlementWithoutRepeatingTheMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "once"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        var receipt = await fixture.Memory.CommitAsync(preview.Command, default);
        Assert.Equal(receipt, await fixture.Memory.CommitAsync(preview.Command, default));
        var recovered = await fixture.RestartedDispatcher().DispatchAsync(request, default);
        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal(1, Assert.Single(await fixture.Memory.SearchAsync("owner", "once", 10, default)).Version);
        Assert.Null(await fixture.Memory.GetReceiptAsync("other", receipt.ActionId, default));
    }

    [Fact]
    public async Task TranscriptRemovalPreservesExactCompletedReplayButCannotAuthorizeNewWork()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "once"));
        var result = await fixture.ApproveAsync(request);
        await fixture.SqlAsync("DELETE FROM conversations;");
        Assert.Equal(result, await fixture.RestartedDispatcher().DispatchAsync(request, default));
        Assert.Equal(result, await fixture.Proposals.CompleteAsync("owner", result.ActionId!.Value, default));
        Assert.Equal("action_identity_reused", (await fixture.Dispatcher.DispatchAsync(request with
        {
            CurrentUserRequest = "different request"
        }, default)).ReasonCode);
        Assert.Equal("action_identity_reused", (await fixture.Dispatcher.DispatchAsync(request with
        {
            ArgumentsJson = JsonSerializer.Serialize(new MemoryChangeArguments("create", Subject: "bathroom", Key: "paint", Value: "changed"))
        }, default)).ReasonCode);
        Assert.Equal("memory_source_unavailable", (await fixture.Dispatcher.DispatchAsync(request with { CallId = "new-call" }, default)).ReasonCode);
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Proposals.CompleteAsync("other", result.ActionId.Value, default));
        Assert.Equal(1, Assert.Single(await fixture.Memory.SearchAsync("owner", "once", 10, default)).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptRemovalStillAllowsReceiptRecoveryWithoutAnotherMutation(bool hostCompletion)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "once"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        await fixture.Memory.CommitAsync(preview.Command, default);
        await fixture.SqlAsync("DELETE FROM conversations;");
        var recovered = hostCompletion
            ? await fixture.Proposals.CompleteAsync("owner", entry.Id, default)
            : await fixture.RestartedDispatcher().DispatchAsync(request, default);
        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal(1, Assert.Single(await fixture.Memory.SearchAsync("owner", "once", 10, default)).Version);
        Assert.Equal(recovered, await fixture.Proposals.CompleteAsync("owner", entry.Id, default));
    }

    [Fact]
    public async Task TranscriptRemovalCannotCompleteAnUncommittedApproval()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "bathroom", Key: "paint", Value: "not saved"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        await fixture.SqlAsync("DELETE FROM conversations;");
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Proposals.CompleteAsync("owner", pending.ActionId!.Value, default));
        Assert.Equal("memory_source_unavailable", (await fixture.Dispatcher.DispatchAsync(request, default)).ReasonCode);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "saved", 10, default));
    }

    [Fact]
    public async Task LocalReadsFilterOwnerPrivacyAndFtsSyntaxBeforeApplyingLimit()
    {
        using var fixture = await Fixture.CreateAsync();
        var allowed = await fixture.CreateFactAsync("bounded");
        await fixture.SqlAsync("""
            INSERT INTO memory_facts (id, owner_id, subject, fact_key, value, source_id, privacy_class,
                created_at_utc, updated_at_utc, version)
            SELECT lower(hex(randomblob(16))), 'other', subject, fact_key, value, source_id, privacy_class,
                created_at_utc, updated_at_utc, version FROM memory_facts;
            """);
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "bounded OR *", 1, default));
        Assert.Single(await fixture.Memory.SearchAsync("owner", "bounded", 1, default));
        Assert.Null(await fixture.Memory.GetAsync("other", allowed.Id, default));
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "!!!", 1, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Memory.SearchAsync(" ", "x", 1, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Memory.SearchAsync("owner", "", 1, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await fixture.Memory.SearchAsync("owner", "x", 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await fixture.Memory.SearchAsync("owner", new string('x', 513), 1, default));
        await fixture.SqlAsync("UPDATE memory_facts SET privacy_class = 'Unknown';");
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "bounded", 10, default));
        Assert.Null(await fixture.Memory.GetAsync("owner", allowed.Id, default));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default));
    }

    [Fact]
    public async Task TranscriptRemovalMakesSourceExplicitlyUnavailableWithoutDeletingFacts()
    {
        using var fixture = await Fixture.CreateAsync();
        var fact = await fixture.CreateFactAsync("durable");
        await fixture.SqlAsync("DELETE FROM conversations;");
        var surviving = (await fixture.Memory.GetAsync("owner", fact.Id, default))!;
        Assert.False(surviving.SourceAvailable);
        Assert.Null(surviving.SourceMessageId);
        Assert.Equal("OwnerMessage", surviving.SourceType);
        Assert.Equal("durable", surviving.Value);
        Assert.Null(await fixture.Memory.ReadSourceAsync("owner", new TurnId(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task ApprovedCommitRejectsChangedContentMissingSourceExpiryAndStaleFact()
    {
        using var fixture = await Fixture.CreateAsync();
        var fact = await fixture.CreateFactAsync("original");
        var request = await fixture.RequestAsync(new("update", fact.Id.Value, fact.Version, "bathroom", "paint", "replacement"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(
            preview.Command with { Change = preview.Command.Change with { Value = "not approved" } }, default));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(
            preview.Command with { OwnerId = "other" }, default));
        var originalTime = fixture.Clock.UtcNow;
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(5);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(preview.Command, default));
        fixture.Clock.UtcNow = originalTime;
        await fixture.SqlAsync("UPDATE memory_facts SET version = version + 1;");
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(preview.Command, default));
        await fixture.SqlAsync("DELETE FROM messages;");
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(preview.Command, default));
        Assert.Equal("original", (await fixture.Memory.GetAsync("owner", fact.Id, default))!.Value);
    }

    [Fact]
    public async Task VersionSixUpgradePreservesLegacyFactsAndHonestProvenance()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await using (var connection = await database.OpenConnectionAsync())
        {
            foreach (var resource in typeof(SqliteDatabase).Assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)
                    && !name.Contains(".007-", StringComparison.Ordinal)
                    && !name.Contains(".008-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                await using var stream = typeof(SqliteDatabase).Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                await using var apply = connection.CreateCommand();
                apply.CommandText = await reader.ReadToEndAsync();
                await apply.ExecuteNonQueryAsync();
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO memory_facts (id, subject, fact_key, value, source_id, privacy_class,
                    created_at_utc, updated_at_utc, version)
                VALUES ($id, 'legacy room', 'paint', 'preserved', 'message-legacy', 'Private', $now, $now, 4);
                PRAGMA user_version = 6;
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$now", new Clock().UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        await database.InitializeAsync();
        var store = new SqliteConfirmedMemoryStore(database, new Clock());
        var fact = Assert.Single(await store.SearchAsync("owner", "preserved", 10, default));
        Assert.Equal("Legacy", fact.SourceType);
        Assert.False(fact.SourceAvailable);
        Assert.Equal("message-legacy", fact.SourceId);
        Assert.Equal(4, fact.Version);
    }

    [Fact]
    public async Task OverlargeConflictSetFailsExplicitlyRatherThanApprovingAPartialSnapshot()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CreateFactAsync("first");
        await fixture.SqlAsync("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < 100)
            INSERT INTO memory_facts (id, subject, fact_key, value, source_id, privacy_class,
                created_at_utc, updated_at_utc, version)
            SELECT lower(hex(randomblob(16))), f.subject, f.fact_key, f.value, f.source_id, f.privacy_class,
                f.created_at_utc, f.updated_at_utc, f.version FROM n CROSS JOIN memory_facts f;
            """);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Memory.FindConflictsAsync("owner", "bathroom", "paint", default));
    }

    [Theory]
    [InlineData("""{"OwnerId":null}""")]
    [InlineData("""{"OwnerId":"owner","MemoryReferences":null}""")]
    public async Task MalformedMemoryJournalMetadataFailsWithoutLeavingAPartialAction(string envelope)
    {
        using var fixture = await Fixture.CreateAsync();
        var actionId = ActionId.New();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Journal.CreateAsync(
            new(actionId, "memory.get", envelope, "hash", "Prepared", fixture.Clock.UtcNow, fixture.Clock.UtcNow, 1), default));
        Assert.Null(await fixture.Journal.GetAsync(actionId, default));
    }

    [Theory]
    [InlineData("OwnerId")]
    [InlineData("ToolName")]
    [InlineData("CanonicalArguments")]
    public async Task CorruptedApprovedEnvelopeCannotAuthorizeMemory(string property)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(new("create", Subject: "room", Key: "paint", Value: "not committed"));
        var pending = await fixture.Dispatcher.DispatchAsync(request, default);
        var preview = await fixture.Proposals.PreviewAsync("owner", pending.ActionId!.Value, default);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
        var entry = (await fixture.Journal.GetAsync(pending.ActionId.Value, default))!;
        await fixture.Journal.UpdateStatusAsync(entry.Id, "Executing", entry.Version, fixture.Clock.UtcNow, default);
        var json = System.Text.Json.Nodes.JsonNode.Parse(entry.CanonicalArguments)!;
        json[property] = property == "CanonicalArguments" ? "null" : "not-the-approved-binding";
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE actions SET canonical_arguments = $value WHERE id = $id;";
        corrupt.Parameters.AddWithValue("$value", json.ToJsonString());
        corrupt.Parameters.AddWithValue("$id", entry.Id.Value.ToString("D"));
        await corrupt.ExecuteNonQueryAsync();
        if (property == "CanonicalArguments")
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Memory.CommitAsync(preview.Command, default));
        }
        else
        {
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () => await fixture.Memory.CommitAsync(preview.Command, default));
        }
        Assert.Empty(await fixture.Memory.SearchAsync("owner", "committed", 10, default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IsolatedDatabaseFile file = IsolatedDatabaseFile.Create();
        internal SqliteDatabase Database { get; private set; } = null!;
        internal Clock Clock { get; } = new();
        internal SqliteConfirmedMemoryStore Memory { get; private set; } = null!;
        internal SqliteActionJournalStore Journal { get; private set; } = null!;
        internal SqliteApprovalStore ApprovalStore { get; private set; } = null!;
        internal ApprovalService Approvals { get; private set; } = null!;
        internal ToolDispatcher Dispatcher { get; private set; } = null!;
        internal MemoryProposalService Proposals => new(Memory, Journal, Approvals, Dispatcher);

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Database = new(fixture.file.Path);
            await fixture.Database.InitializeAsync();
            fixture.Memory = new(fixture.Database, fixture.Clock);
            fixture.Journal = new(fixture.Database);
            fixture.ApprovalStore = new(fixture.Database);
            fixture.Approvals = new(fixture.ApprovalStore, fixture.Journal, fixture.Clock);
            fixture.Dispatcher = fixture.RestartedDispatcher();
            return fixture;
        }

        internal ApprovalWorkflowService ApprovalWorkflow() =>
            new(ApprovalStore, Journal, Approvals, Dispatcher, Memory, Proposals, Clock);

        internal ToolDispatcher RestartedDispatcher(IActionJournalStore? journal = null) => new(
            new ToolRegistry(MemoryToolRegistrations.Create(Memory)),
            new ToolAllowlistPolicy("memory-v1", MemoryToolRegistrations.Permissions()),
            journal ?? Journal, Approvals, Clock, TimeSpan.FromSeconds(5));

        internal async Task<ToolDispatchRequest> RequestAsync(MemoryChangeArguments args) =>
            await ReadRequestAsync("memory.propose_change", args);

        internal async Task<ToolDispatchRequest> ReadRequestAsync<T>(string tool, T args)
        {
            var conversations = new SqliteConversationStore(Database, Clock);
            var conversation = ConversationId.New();
            var turn = TurnId.New();
            var text = "Explicit synthetic owner request";
            await conversations.SubmitTurnAsync(new(turn, conversation, Guid.NewGuid().ToString(),
                new string('a', 64), Guid.NewGuid(), text, Clock.UtcNow), default);
            return new(turn, Guid.NewGuid().ToString(), tool, JsonSerializer.Serialize(args), "owner", text);
        }

        internal async Task<ToolDispatchResult> ApproveAsync(ToolDispatchRequest request)
        {
            var pending = await Dispatcher.DispatchAsync(request, default);
            if (pending.Status != "AwaitingApproval")
            {
                return pending;
            }
            await Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, default);
            return await Dispatcher.DispatchAsync(request, default);
        }

        internal async Task<MemoryFact> CreateFactAsync(string value, string subject = "bathroom")
        {
            var request = await RequestAsync(new("create", Subject: subject, Key: "paint", Value: value));
            var result = await ApproveAsync(request);
            Assert.Equal("Succeeded", result.Status);
            return (await Memory.GetAsync("owner", JsonSerializer.Deserialize<MemoryReceipt>(result.ResultJson!)!.FactId, default))!;
        }

        internal async Task SqlAsync(string sql)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose() => file.Dispose();
    }

    private sealed class AsyncGate
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task PauseAsync()
        {
            Reached.SetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class InterleavingJournal(IActionJournalStore inner) : IActionJournalStore
    {
        internal Func<Task>? AfterFirstRead { get; set; }
        internal Func<string, Task>? BeforeStatus { get; init; }
        internal Func<string, Task>? AfterStatus { get; init; }
        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken token) =>
            inner.CreateAsync(entry, token);
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(string ownerId, int maximumActions, CancellationToken token) =>
            inner.ReadRecentForOwnerAsync(ownerId, maximumActions, token);
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadForTurnAsync(string ownerId, TurnId turnId, CancellationToken token) =>
            inner.ReadForTurnAsync(ownerId, turnId, token);
        public async ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken token)
        {
            var result = await inner.GetAsync(id, token);
            var callback = AfterFirstRead;
            AfterFirstRead = null;
            if (callback is not null)
            {
                await callback();
            }
            return result;
        }
        public async ValueTask<ActionJournalEntry> UpdateStatusAsync(ActionId id, string status, long expectedVersion,
            DateTimeOffset updatedAtUtc, CancellationToken cancellationToken, ToolDispatchResult? outcome = null)
        {
            if (BeforeStatus is not null)
            {
                await BeforeStatus(status);
            }
            var result = await inner.UpdateStatusAsync(id, status, expectedVersion, updatedAtUtc, cancellationToken, outcome);
            if (AfterStatus is not null)
            {
                await AfterStatus(status);
            }
            return result;
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    }
}
