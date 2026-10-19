using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Memory;
using PersonalAgent.Application.Tools;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolDispatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly ToolDispatchRequest PorchRequest =
        new(new TurnId(Guid.Parse("35D3D0CC-3B47-45C6-84A3-3DCE5E03C34F")), "call-1", "home.set_light",
            """{"Entity":"light.porch","On":true}""", "owner", "Turn on the porch light");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownMalformedAmbiguousAndDisallowedCallsNeverExecute()
    {
        using var fixture = await Fixture.CreateAsync();

        var unknown = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ToolName = "home.run_service" },
            CancellationToken.None);
        var malformed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "malformed", ArgumentsJson = """{"Entity":1,"On":true}""" },
            CancellationToken.None);
        var duplicate = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "duplicate", ArgumentsJson = """{"Entity":"light.porch","Entity":"light.denied","On":true}""" },
            CancellationToken.None);
        var unknownProperty = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "extra", ArgumentsJson = """{"Entity":"light.porch","On":true,"Service":"dangerous"}""" },
            CancellationToken.None);
        var ambiguous = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "ambiguous", ArgumentsJson = """{"Entity":"porch","On":true}""" },
            CancellationToken.None);
        var denied = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "denied", ArgumentsJson = """{"Entity":"light.denied","On":true}""" },
            CancellationToken.None);

        Assert.Equal("unregistered_tool", unknown.ReasonCode);
        Assert.Equal("invalid_tool_arguments", malformed.ReasonCode);
        Assert.Equal("invalid_tool_arguments", duplicate.ReasonCode);
        Assert.Equal("invalid_tool_arguments", unknownProperty.ReasonCode);
        Assert.Equal("ClarificationRequired", ambiguous.Status);
        Assert.Equal("Rejected", denied.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(denied.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SameCallIdentityReturnsRecordedOutcomeButSimilarLaterCallExecutes()
    {
        using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var restarted = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);
        var later = await fixture.Dispatcher.DispatchAsync(PorchRequest with { CallId = "call-2" }, CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("Succeeded", duplicate.Status);
        Assert.Equal(first, duplicate);
        Assert.Equal(first, restarted);
        Assert.NotEqual(first.ActionId, later.ActionId);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        var persisted = await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None);
        Assert.Contains("CurrentRequestHash", persisted!.CanonicalArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn on the porch light", persisted.CanonicalArguments, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowReviewsAndExecutesOnlyTheExactApprovedHomeAction()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var conversationStore = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversationStore.CreateConversationAsync(new(
            conversationId,
            "owner",
            "Approval workflow",
            fixture.Clock.UtcNow,
            fixture.Clock.UtcNow), CancellationToken.None);
        await conversationStore.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), CancellationToken.None);

        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("AwaitingApproval", pending.Status);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, CancellationToken.None));
        Assert.Equal("home.set_light", summary.ToolName);
        Assert.Equal("light.porch", summary.EntityId);
        Assert.Equal("turn_on", summary.Operation);
        Assert.DoesNotContain("OwnerId", summary.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PorchRequest.CurrentUserRequest!, summary.ArgumentsJson, StringComparison.Ordinal);
        Assert.Empty(await workflow.ReadRecentAsync("another-owner", 10, CancellationToken.None));

        var result = await workflow.ResolveAsync(
            "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, true, CancellationToken.None);

        Assert.True(result.Status == "Succeeded", $"{result.Status}: {result.ReasonCode}");
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
        Assert.Single(await fixture.Journal.ReadRecentForOwnerAsync("owner", 10, CancellationToken.None));
        Assert.Empty(await fixture.Journal.ReadRecentForOwnerAsync("another-owner", 10, CancellationToken.None));
        Assert.Equal("Succeeded", Assert.Single(await workflow.ReadRecentAsync("owner", 10, CancellationToken.None)).ActionStatus);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await workflow.ResolveAsync(
                "owner", pending.ApprovalId.Value, summary.Approval.ExpectedVersion, true, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AlreadyApprovedUnclaimedHomeActionCanResumeAfterDispatcherRestart()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, default));
        var approval = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", summary.Approval.ExpectedVersion, true, default);
        Assert.Equal("Approved", approval.Status);
        Assert.Equal("AwaitingApproval", (await fixture.Journal.GetAsync(pending.ActionId!.Value, default))!.Status);

        var restartedWorkflow = fixture.ApprovalWorkflow(fixture.CreateRestartedDispatcher());
        var approvedSummary = Assert.Single(await restartedWorkflow.ReadRecentAsync("owner", 10, default));
        var resumed = await restartedWorkflow.ResolveAsync(
            "owner", pending.ApprovalId.Value, approvedSummary.Approval.ExpectedVersion, true, default);

        Assert.Equal("Succeeded", resumed.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
        Assert.Equal(resumed, await restartedWorkflow.ResolveAsync(
            "owner", pending.ApprovalId.Value, approvedSummary.Approval.ExpectedVersion, true, default));
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExpiredApprovedUnclaimedActionCanBeFinalizedWithoutExecution()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, default));
        var approval = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", summary.Approval.ExpectedVersion, true, default);
        var action = await fixture.Journal.GetAsync(pending.ActionId!.Value, default);

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await workflow.ExpireApprovedUnclaimedAsync("owner", approval.Id, approval.ExpectedVersion, default));
        Assert.Equal("AwaitingApproval", (await fixture.Journal.GetAsync(action!.Id, default))!.Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await workflow.ExpireApprovedUnclaimedAsync(
            "owner", approval.Id, approval.ExpectedVersion, default);

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("approval_expired_before_execution", expired.ReasonCode);
        Assert.Equal("Expired", (await fixture.Journal.GetAsync(action.Id, default))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal(expired, await workflow.ExpireApprovedUnclaimedAsync(
            "owner", approval.Id, approval.ExpectedVersion, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownApprovedWriteRemainsProtectedAcrossTurnReleaseWhileSiblingApprovalIsPending()
    {
        using var fixture = await Fixture.CreateAsync(
            ToolRiskClass.ApprovalRequired,
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        var siblingRequest = PorchRequest with { CallId = "call-2" };
        var sibling = await fixture.Dispatcher.DispatchAsync(siblingRequest, default);
        Assert.Equal("AwaitingApproval", first.Status);
        Assert.Equal("AwaitingApproval", sibling.Status);

        var workflow = fixture.ApprovalWorkflow();
        var firstSummary = (await workflow.ReadRecentAsync("owner", 10, default))
            .Single(summary => summary.ActionId == first.ActionId);
        var firstOutcome = await workflow.ResolveAsync(
            "owner", first.ApprovalId!.Value, firstSummary.Approval.ExpectedVersion, true, default);
        Assert.Equal("Unknown", firstOutcome.Status);
        Assert.Equal(1, fixture.ExecutionCount);

        fixture.Dispatcher.ReleaseTurn(PorchRequest.TurnId);

        var siblingSummary = (await workflow.ReadRecentAsync("owner", 10, default))
            .Single(summary => summary.ActionId == sibling.ActionId);
        var siblingOutcome = await workflow.ResolveAsync(
            "owner", sibling.ApprovalId!.Value, siblingSummary.Approval.ExpectedVersion, true, default);

        Assert.Equal("Rejected", siblingOutcome.Status);
        Assert.Equal("equivalent_write_outcome_unknown", siblingOutcome.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownApprovedWriteRemainsProtectedAfterDispatcherRestartWhileSiblingApprovalIsPending()
    {
        using var fixture = await Fixture.CreateAsync(
            ToolRiskClass.ApprovalRequired,
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        var siblingRequest = PorchRequest with { CallId = "call-2" };
        var sibling = await fixture.Dispatcher.DispatchAsync(siblingRequest, default);
        Assert.Equal("AwaitingApproval", first.Status);
        Assert.Equal("AwaitingApproval", sibling.Status);

        var firstWorkflow = fixture.ApprovalWorkflow();
        var firstSummary = (await firstWorkflow.ReadRecentAsync("owner", 10, default))
            .Single(summary => summary.ActionId == first.ActionId);
        var firstOutcome = await firstWorkflow.ResolveAsync(
            "owner", first.ApprovalId!.Value, firstSummary.Approval.ExpectedVersion, true, default);
        Assert.Equal("Unknown", firstOutcome.Status);
        Assert.Equal(1, fixture.ExecutionCount);

        var recentTimestamp = fixture.Clock.UtcNow.AddMinutes(1);
        for (var index = 0; index < 101; index++)
        {
            var envelope = JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                TurnId = TurnId.New().Value.ToString("D"),
                CallId = $"unrelated-{index}",
                OwnerId = "owner",
                ToolName = "home.set_light",
                ActionType = "home.set_light",
                Operation = "turn_on",
                EntityId = "light.porch",
                PolicyVersion = "policy-v1",
                RiskClass = ToolRiskClass.ReadOnly,
                CurrentRequestHash = new string('a', 64),
                CanonicalArguments = """{"Entity":"light.porch","On":true}"""
            });
            var id = new ActionId(Guid.NewGuid());
            await fixture.Journal.CreateAsync(new(
                id,
                "home.set_light",
                envelope,
                Guid.NewGuid().ToString("N"),
                "Prepared",
                fixture.Clock.UtcNow,
                recentTimestamp,
                1), default);
        }
        Assert.DoesNotContain(
            (await fixture.Journal.ReadRecentForOwnerAsync("owner", 100, default)),
            entry => entry.Id == first.ActionId);

        fixture.Dispatcher.ReleaseTurn(PorchRequest.TurnId);

        var restartedDispatcher = fixture.CreateRestartedDispatcher();
        var restartedWorkflow = fixture.ApprovalWorkflow(restartedDispatcher);
        var siblingSummary = (await restartedWorkflow.ReadRecentAsync("owner", 10, default))
            .Single(summary => summary.ActionId == sibling.ActionId);
        var siblingOutcome = await restartedWorkflow.ResolveAsync(
            "owner", sibling.ApprovalId!.Value, siblingSummary.Approval.ExpectedVersion, true, default);

        Assert.Equal("Rejected", siblingOutcome.Status);
        Assert.Equal("equivalent_write_outcome_unknown", siblingOutcome.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HistorySeedingDoesNotResurrectUncertaintyAfterMatchingExecutionSucceeds()
    {
        using var fixture = await Fixture.CreateAsync();
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = 0;
        fixture.SetExecution(async (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref execution) == 1)
            {
                executionStarted.SetResult();
                await releaseExecution.Task.WaitAsync(cancellationToken);
            }

            return new(ToolExecutionStatus.Completed, """{"ok":true}""", null);
        });

        var snapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeHistoryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseNextHistoryRead = 0;
        var journal = new PausedJournal(
            fixture.Journal,
            (_, _) => Task.CompletedTask,
            afterTurnRead: async (entries, cancellationToken) =>
            {
                if (Interlocked.Exchange(ref pauseNextHistoryRead, 0) == 1)
                {
                    Assert.Contains(entries, entry => entry.Status == "Executing");
                    snapshotTaken.SetResult();
                    await resumeHistoryRead.Task.WaitAsync(cancellationToken);
                }
            });
        var dispatcher = fixture.CreateRestartedDispatcher(journal);

        var first = dispatcher.DispatchAsync(PorchRequest with { CallId = "history-first" }, default).AsTask();
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref pauseNextHistoryRead, 1);

        var second = dispatcher.DispatchAsync(PorchRequest with { CallId = "history-second" }, default).AsTask();
        await snapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseExecution.SetResult();
        Assert.Equal("Succeeded", (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        resumeHistoryRead.SetResult();

        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Succeeded", secondResult.Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MalformedSameTurnUnknownEnvelopeFailsClosedBeforeFreshWrite()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var unknown = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        Assert.Equal("Unknown", unknown.Status);

        await fixture.SqlAsync(
            $"UPDATE actions SET canonical_arguments = 'not-json' " +
            $"WHERE id = '{unknown.ActionId!.Value.Value:D}';");
        Assert.Contains(
            await fixture.Journal.ReadRecentForOwnerAsync("owner", 10, default),
            entry => entry.Id == unknown.ActionId);
        Assert.Empty(await fixture.Journal.ReadRecentForOwnerAsync("another-owner", 10, default));

        var restarted = fixture.CreateRestartedDispatcher();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await restarted.DispatchAsync(PorchRequest with { CallId = "fresh-after-malformed-unknown" }, default));
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnattributedUnknownJournalRowFailsClosedBeforeFreshWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        var malformed = await fixture.Journal.CreateAsync(new(
            new ActionId(Guid.NewGuid()),
            "legacy.action",
            "not-json",
            Guid.NewGuid().ToString("N"),
            "Prepared",
            fixture.Clock.UtcNow,
            fixture.Clock.UtcNow,
            1), default);
        await fixture.Journal.UpdateStatusAsync(
            malformed.Id, "Unknown", malformed.Version, fixture.Clock.UtcNow, default);

        var restarted = fixture.CreateRestartedDispatcher();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await restarted.DispatchAsync(PorchRequest with { CallId = "fresh-after-unattributed-unknown" }, default));
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowRejectionSettlesApprovalWithoutExecutingAction()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await workflow.ResolveAsync(
                "another-owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, false, default));

        var result = await workflow.ResolveAsync(
            "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, false, default);

        Assert.Equal("Rejected", result.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal("Rejected", Assert.Single(await workflow.ReadRecentAsync("owner", 10, default)).Approval.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowRejectsMalformedStoredActionEnvelope()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        await fixture.SqlAsync(
            $"UPDATE actions SET canonical_arguments = 'not-json' WHERE id = '{pending.ActionId!.Value.Value:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 10, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowExpiresHomeApprovalWithoutExecutingAction()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        var workflow = fixture.ApprovalWorkflow();
        var summary = Assert.Single(await workflow.ReadRecentAsync("owner", 10, default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await workflow.ResolveAsync(
            "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, true, default);

        Assert.Equal("Expired", result.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowRequiresOriginalRequestSourceBeforeApprovingHomeAction()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var conversations = new SqliteConversationStore(fixture.Database, fixture.Clock);
        var conversationId = ConversationId.New();
        await conversations.CreateConversationAsync(new(
            conversationId, "owner", "Approval workflow", fixture.Clock.UtcNow, fixture.Clock.UtcNow), default);
        await conversations.SubmitTurnAsync(new(
            PorchRequest.TurnId,
            conversationId,
            Guid.NewGuid().ToString(),
            new string('a', 64),
            Guid.NewGuid(),
            PorchRequest.CurrentUserRequest!,
            fixture.Clock.UtcNow,
            RequiredOwnerId: "owner"), default);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, default);
        var summary = Assert.Single(await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 10, default));
        await fixture.SqlAsync("DELETE FROM conversations;");

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.ApprovalWorkflow().ResolveAsync(
                "owner", pending.ApprovalId!.Value, summary.Approval.ExpectedVersion, true, default));
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("another-owner", "home.set_light")]
    [InlineData("owner", "home.run_service")]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowRejectsUnboundOrUnsupportedStoredActions(string storedOwner, string toolName)
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        await fixture.SqlAsync(
            $"UPDATE actions SET canonical_arguments = '{{\"OwnerId\":\"{storedOwner}\",\"ToolName\":\"{toolName}\"}}' " +
            $"WHERE id = '{pending.ActionId!.Value.Value:D}';");
        var workflow = fixture.ApprovalWorkflow();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await workflow.ReadRecentAsync("owner", 10, default));
        if (storedOwner != "owner")
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
                await workflow.ResolveAsync(
                    "owner", pending.ApprovalId!.Value, 1, true, default));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await workflow.ResolveAsync(
                    "owner", pending.ApprovalId!.Value, 1, true, default));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowReportsMissingApprovalAndActionRecordsExplicitly()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var workflow = fixture.ApprovalWorkflow();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await workflow.ResolveAsync("owner", new ApprovalId(Guid.NewGuid()), 1, true, default));

        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        await fixture.SqlAsync(
            $"PRAGMA foreign_keys = OFF; DELETE FROM actions WHERE id = '{pending.ActionId!.Value.Value:D}';");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await workflow.ReadRecentAsync("owner", 10, default));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await workflow.ResolveAsync("owner", pending.ApprovalId!.Value, 1, true, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnerWorkflowRejectsNullStoredActionEnvelope()
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        await fixture.SqlAsync(
            $"UPDATE actions SET canonical_arguments = 'null' WHERE id = '{pending.ActionId!.Value.Value:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 10, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangedArgumentsCannotReuseCompletedActionIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        var changed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    [InlineData("Rejected")]
    [InlineData("Expired")]
    [Trait("Category", "Integration")]
    public async Task TerminalOutcomeReplaysExactlyFromSqliteWithoutExecutingOrObserving(string status)
    {
        using var fixture = await Fixture.CreateAsync(
            status == "Expired" ? ToolRiskClass.ApprovalRequired : ToolRiskClass.LowRiskWrite,
            execution: (_, _, _) => ValueTask.FromResult(new ToolExecutionOutcome(
                status == "Failed" ? ToolExecutionStatus.Failed : ToolExecutionStatus.Completed,
                """ {"value":"\u00e5","precision":1.00} """, "execution_detail")),
            verification: (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(
                status == "Unknown" ? ToolVerificationStatus.Unknown : ToolVerificationStatus.Verified, "observation_detail")));
        if (status == "Rejected")
        {
            fixture.Policy.Replace("denied", fixture.AllowedPermission with { EntityId = "light.other" });
        }
        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        if (status == "Expired")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
            Assert.NotNull(first.ApprovalId);
        }
        Assert.Equal(status, first.Status);
        var persisted = (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!;
        Assert.Equal(first, persisted.Outcome);
        var sends = fixture.ExecutionCount;
        var observations = fixture.VerificationCount;
        fixture.SetExecution((_, _, _) => throw new InvalidOperationException("Replay must not execute."));
        fixture.SetVerification((_, _, _, _) => throw new InvalidOperationException("Replay must not observe."));
        var replay = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(replay));
        Assert.Equal(persisted, await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None));
        Assert.Equal(sends, fixture.ExecutionCount);
        Assert.Equal(observations, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacyTerminalOutcomeRemainsExplicitlyStatusOnlyWithoutReconstructingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var current = (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(current.Id, current.Status, current.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var replay = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Null(replay.ResultJson);
        Assert.Equal("recorded_action_outcome", replay.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("lowered-risk", false)]
    [InlineData("lowered-risk", true)]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [Trait("Category", "Integration")]
    public async Task PersistedSqliteUnknownReplayPreservesOriginalRiskAcrossCancelledWaits(
        string originalRisk, bool cancelReplay)
    {
        using var fixture = await Fixture.CreateAsync(
            originalRisk == "write" ? ToolRiskClass.LowRiskWrite : ToolRiskClass.ReadOnly);
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.Replace("approval-policy", fixture.AllowedPermission with
            {
                MinimumRisk = ToolRiskClass.ApprovalRequired
            });
            var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }
        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "unconfirmed")));
        fixture.SetVerification((_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var unknown = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Unknown", unknown.Status);
        var persisted = (await fixture.Journal.GetAsync(unknown.ActionId!.Value, CancellationToken.None))!;
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.Replace("read-only-policy", fixture.AllowedPermission);
        }
        fixture.SetVerification((_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)));
        var restarted = fixture.CreateRestartedDispatcher();
        if (cancelReplay)
        {
            var replaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var journal = new PausedJournal(fixture.Journal, async (entry, token) =>
            {
                if (entry?.Id != persisted.Id)
                {
                    return;
                }

                switch (Interlocked.Increment(ref reads))
                {
                    case 2:
                        replaying.SetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                        break;
                    case 3:
                        queuedRead.SetResult();
                        break;
                }
            });
            restarted = fixture.CreateRestartedDispatcher(journal);
            var holder = restarted.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
            await replaying.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var cancellation = new CancellationTokenSource();
            var replay = restarted.DispatchAsync(PorchRequest, cancellation.Token).AsTask();
            await queuedRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(replay.IsCompleted);
            cancellation.Cancel();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
            }
            finally
            {
                release.SetResult();
                Assert.Equal("Unknown", (await holder).Status);
            }
        }
        var sends = fixture.ExecutionCount;
        var observations = fixture.VerificationCount;
        var read = originalRisk == "read";
        if (cancelReplay)
        {
            fixture.SetExecution((_, _, _) =>
                ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null)));
            var afterCancellation = await restarted.DispatchAsync(
                PorchRequest with { CallId = "after-cancelled-sqlite-unknown-replay" }, CancellationToken.None);
            Assert.Equal(read ? "Succeeded" : "Rejected", afterCancellation.Status);
            if (!read)
            {
                Assert.Equal("equivalent_write_outcome_unknown", afterCancellation.ReasonCode);
            }
            Assert.Equal(sends + (read ? 1 : 0), fixture.ExecutionCount);
            Assert.Equal(observations + (read ? 1 : 0), fixture.VerificationCount);
            observations = fixture.VerificationCount;
        }
        for (var duplicate = 0; duplicate < 2; duplicate++)
        {
            var replay = await restarted.DispatchAsync(PorchRequest, CancellationToken.None);
            Assert.Equal("Unknown", replay.Status);
            Assert.Equal(unknown, replay);
        }
        Assert.Equal(persisted, await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None));
        Assert.Equal(observations, fixture.VerificationCount);
        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure")));
        var fresh = await restarted.DispatchAsync(PorchRequest with
        {
            CallId = "fresh-after-sqlite-unknown-replay",
            ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}"""
        }, CancellationToken.None);
        Assert.Equal(read ? "Failed" : "Rejected", fresh.Status);
        Assert.Equal(read ? "known_failure" : "equivalent_write_outcome_unknown", fresh.ReasonCode);
        var stopped = await restarted.DispatchAsync(
            PorchRequest with { CallId = "past-sqlite-unknown-budget" }, CancellationToken.None);
        Assert.Equal(read ? "equivalent_failure_limit" : "equivalent_write_outcome_unknown", stopped.ReasonCode);
        var additionalReadSends = cancelReplay ? 2 : 1;
        Assert.Equal(sends + (read ? additionalReadSends : 0), fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [Trait("Category", "Integration")]
    public async Task SqliteTerminalReplayReconcilesCancelledStaleRecovery(
        bool failed, bool changedPolicy, bool anotherOrphan)
    {
        using var fixture = await Fixture.CreateAsync();
        var otherRequest = PorchRequest with { CallId = "other-sqlite-orphan" };
        ActionJournalEntry? otherOrphan = null;
        if (anotherOrphan)
        {
            var other = await fixture.Dispatcher.DispatchAsync(otherRequest, CancellationToken.None);
            otherOrphan = (await fixture.Journal.GetAsync(other.ActionId!.Value, CancellationToken.None))!;
        }
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Inline release reaches registration and the proposal wait before cancellation.
        var releaseRead = new TaskCompletionSource();
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var readDeadlineRegistration = readDeadline.Token.Register(
            () => releaseRead.TrySetCanceled(readDeadline.Token));
        var paused = 0;
        var journal = new PausedJournal(fixture.Journal, async (entry, _) =>
        {
            if (entry?.Id == fixture.ActionId(PorchRequest) && entry.Status == "Executing"
                && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task;
            }
        });
        var restarted = fixture.CreateRestartedDispatcher(journal);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(
                failed ? ToolExecutionStatus.Failed : ToolExecutionStatus.Completed, null, null);
        });
        var first = restarted.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (otherOrphan is not null)
        {
            await fixture.Journal.UpdateStatusAsync(
                otherOrphan.Id, "Executing", otherOrphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        }
        using var cancellation = new CancellationTokenSource();
        var stale = restarted.DispatchAsync(PorchRequest, cancellation.Token).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseExecution.SetResult();
        var winner = await first;
        Assert.Equal(failed ? "Failed" : "Succeeded", winner.Status);
        cancellation.Cancel();
        releaseRead.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        var sends = fixture.ExecutionCount;
        var observations = fixture.VerificationCount;
        var beforeReplay = await restarted.DispatchAsync(
            PorchRequest with { CallId = "before-sqlite-terminal-reconciliation" }, CancellationToken.None);
        Assert.Equal("equivalent_write_outcome_unknown", beforeReplay.ReasonCode);
        Assert.Equal(sends, fixture.ExecutionCount);
        var persisted = (await fixture.Journal.GetAsync(winner.ActionId!.Value, CancellationToken.None))!;
        if (changedPolicy)
        {
            fixture.Policy.Replace("new-policy", fixture.AllowedPermission);
        }
        Assert.Equal(winner.Status, (await restarted.DispatchAsync(PorchRequest, CancellationToken.None)).Status);
        Assert.Equal(persisted, await fixture.Journal.GetAsync(winner.ActionId.Value, CancellationToken.None));
        Assert.Equal(sends, fixture.ExecutionCount);
        Assert.Equal(observations, fixture.VerificationCount);
        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure")));
        if (anotherOrphan)
        {
            var blocked = await restarted.DispatchAsync(
                PorchRequest with { CallId = "other-sqlite-orphan-remains" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", blocked.ReasonCode);
            Assert.Equal(sends, fixture.ExecutionCount);
            Assert.Equal("Succeeded", (await restarted.DispatchAsync(otherRequest, CancellationToken.None)).Status);
        }
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            PorchRequest with { CallId = "fresh-after-sqlite-terminal-replay" }, CancellationToken.None)).Status);
        if (!failed)
        {
            Assert.Equal("Failed", (await restarted.DispatchAsync(
                PorchRequest with { CallId = "second-sqlite-terminal-replay-failure" }, CancellationToken.None)).Status);
        }
        var stopped = await restarted.DispatchAsync(
            PorchRequest with { CallId = "past-sqlite-terminal-replay-budget" }, CancellationToken.None);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(sends + (failed ? 1 : 2), fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.LowRiskWrite)]
    [InlineData(ToolRiskClass.ReadOnly)]
    [Trait("Category", "Integration")]
    public async Task OrphanRegisteredDuringSqliteClaimPreventsFinalSendAdmission(ToolRiskClass risk)
    {
        using var fixture = await Fixture.CreateAsync(risk);
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var orphan = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var journal = new PausedJournal(fixture.Journal, (entry, _) =>
        {
            if (entry?.Id == orphan.Id && entry.Status == "Executing")
            {
                reading.TrySetResult();
            }
            return Task.CompletedTask;
        }, async token =>
        {
            if (Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                claiming.SetResult();
                await releaseClaim.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var restarted = fixture.CreateRestartedDispatcher(journal);
        var request = PorchRequest with { CallId = "claiming-during-orphan-discovery" };
        var fresh = restarted.DispatchAsync(request, CancellationToken.None).AsTask();
        await claiming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Journal.UpdateStatusAsync(
            orphan.Id, "Executing", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var recovery = restarted.DispatchAsync(PorchRequest, cancellation.Token).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        }
        finally
        {
            releaseClaim.SetResult();
        }
        var result = await fresh;
        var read = risk == ToolRiskClass.ReadOnly;
        Assert.Equal(read ? "Succeeded" : "Rejected", result.Status);
        Assert.Equal(result.Status, (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal("Executing", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(read ? 2 : 1, fixture.ExecutionCount);
        Assert.Equal(read ? 2 : 1, fixture.VerificationCount);
        if (!read)
        {
            Assert.Equal("equivalent_write_outcome_unknown", result.ReasonCode);
            var stopped = await restarted.DispatchAsync(
                request with { CallId = "after-unsent-sqlite-claim" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
            Assert.Equal(1, fixture.ExecutionCount);
        }
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(PorchRequest, CancellationToken.None)).Status);
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(
            request with { CallId = "after-known-sqlite-orphan" }, CancellationToken.None)).Status);
        Assert.Equal(read ? 3 : 2, fixture.ExecutionCount);
        Assert.Equal(read ? 4 : 3, fixture.VerificationCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.LowRiskWrite)]
    [InlineData(ToolRiskClass.ReadOnly)]
    [Trait("Category", "Integration")]
    public async Task CancelledQueuedRecoveryPreservesSqliteOrphanRiskAfterTheGateHolderSucceeds(ToolRiskClass risk)
    {
        using var fixture = await Fixture.CreateAsync(risk);
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var orphan = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            orphan.Id, "Executing", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetVerification(async (_, _, _, token) =>
        {
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
        });
        var restarted = fixture.CreateRestartedDispatcher();
        var first = restarted.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelled = restarted.DispatchAsync(
            PorchRequest with { CallId = "cancelled-queued-sqlite-recovery" }, cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            releaseObservation.SetResult();
            Assert.Equal("Succeeded", (await first).Status);
        }
        Assert.Equal("Succeeded", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure")));
        var fresh = await restarted.DispatchAsync(PorchRequest with
        {
            CallId = "after-cancelled-sqlite-recovery",
            ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}"""
        }, CancellationToken.None);
        Assert.Equal("Failed", fresh.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(fresh.Status, (await fixture.Journal.GetAsync(fresh.ActionId!.Value, CancellationToken.None))!.Status);

        Assert.Equal("Succeeded", (await restarted.DispatchAsync(PorchRequest, CancellationToken.None)).Status);
        Assert.Equal(2, fixture.VerificationCount);
        Assert.Equal("Succeeded", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            PorchRequest with { CallId = "after-confirmed-sqlite-recovery" }, CancellationToken.None)).Status);
        Assert.Equal(3, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed, false)]
    [InlineData(ToolExecutionStatus.Unknown, false)]
    [InlineData(ToolExecutionStatus.Completed, true)]
    [InlineData(ToolExecutionStatus.Unknown, true)]
    [Trait("Category", "Integration")]
    public async Task RevocationDuringExecutionPersistsUnknownWithoutObservation(
        ToolExecutionStatus delivery, bool unsupported)
    {
        using var fixture = await Fixture.CreateAsync();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, null);
        });
        var dispatch = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Policy.Replace("revoked", fixture.AllowedPermission with
        {
            EntityId = unsupported ? "light.porch" : "light.other",
            MinimumRisk = unsupported ? ToolRiskClass.Unsupported : ToolRiskClass.ReadOnly
        });
        release.SetResult();
        var result = await dispatch;
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("execution_observation_not_permitted", result.ReasonCode);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.VerificationCount);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed)]
    [InlineData(ToolExecutionStatus.Failed)]
    [InlineData(ToolExecutionStatus.Unknown)]
    [Trait("Category", "Integration")]
    public async Task StaleExecutingSnapshotReturnsSqliteWinnerWithoutObserving(ToolExecutionStatus delivery)
    {
        using var fixture = await Fixture.CreateAsync();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, null);
        });
        if (delivery == ToolExecutionStatus.Unknown)
        {
            fixture.SetVerification((_, _, _, _) =>
                ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        }
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var journal = new PausedJournal(fixture.Journal, async (entry, token) =>
        {
            if (entry?.Status == "Executing" && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var dispatcher = fixture.CreateRestartedDispatcher(journal);
        var first = dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stale = dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseExecution.SetResult();
        var winner = await first;
        var observations = fixture.VerificationCount;
        fixture.Policy.Replace("revoked", fixture.AllowedPermission with { EntityId = "light.other" });
        releaseRead.SetResult();
        var result = await stale;
        Assert.Equal(winner.Status, result.Status);
        Assert.Equal(winner, result);
        Assert.Equal(winner.Status, (await fixture.Journal.GetAsync(winner.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(observations, fixture.VerificationCount);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task EquivalentUncertainWritesCannotResendWithFreshIds(bool concurrent, bool throws)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(execution: async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return throws
                ? throw new InvalidOperationException("controlled uncertain send")
                : new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "delivery_uncertain");
        }, verification: (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var first = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextRequest = PorchRequest with { CallId = "fresh-id" };
        var queued = concurrent
            ? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask()
            : null;
        release.SetResult();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        }
        else
        {
            Assert.Equal("Unknown", (await first).Status);
        }

        var rejected = await (queued ?? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("equivalent_write_outcome_unknown", rejected.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None))!.Status);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(rejected.ActionId!.Value, CancellationToken.None))!.Status);

        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null)));
        fixture.SetVerification((_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)));
        var later = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { TurnId = TurnId.New() }, CancellationToken.None);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExactApprovalMustBeResolvedBeforeActionExecutesAndIsSingleUse()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);

        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal(Start.AddMinutes(5), pending.ApprovalExpiresAtUtc);
        Assert.Equal(0, fixture.ExecutionCount);

        var resolved = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value,
            "owner",
            1,
            approved: true,
            CancellationToken.None);
        Assert.Equal("Approved", resolved.Status);

        var executed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Succeeded", executed.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId.Value,
                "owner",
                1,
                approved: true,
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalListRecoversInterruptedApprovedHomeActionByObservationOnly()
    {
        using var fixture = await Fixture.CreateAsync(
            riskClass: ToolRiskClass.ApprovalRequired,
            verification: (_, _, _, _) =>
                ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "not_confirmed")));
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        var approval = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", 1, approved: true, CancellationToken.None);
        var executing = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None)
            ?? throw new InvalidOperationException("The approved action was not journaled.");
        await fixture.Journal.UpdateStatusAsync(
            executing.Id, "Executing", executing.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var restartedWorkflow = fixture.ApprovalWorkflow(fixture.CreateRestartedDispatcher());

        var summary = Assert.Single(await restartedWorkflow.ReadRecentAsync("owner", 10, CancellationToken.None));

        Assert.Equal("Approved", summary.Approval.Status);
        Assert.Equal("Unknown", summary.ActionStatus);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(executing.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
        Assert.Equal(approval.ExpectedVersion, summary.Approval.ExpectedVersion);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnapprovedExecutingActionRecoveryPrioritizesOlderUnresolvedActions()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = PorchRequest with { TurnId = TurnId.New(), CallId = "older-orphan" };
        var completed = await fixture.Dispatcher.DispatchAsync(request, CancellationToken.None);
        var action = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None)
            ?? throw new InvalidOperationException("The action was not journaled.");
        await fixture.Journal.UpdateStatusAsync(
            action.Id, "Executing", action.Version, Start.AddMinutes(-1), CancellationToken.None);
        await fixture.SqlAsync($"""
            WITH RECURSIVE numbers(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 101
            )
            INSERT INTO actions
                (id, action_type, canonical_arguments, request_hash, status, created_at_utc,
                 updated_at_utc, version, action_owner_id)
            SELECT lower(substr(hex(randomblob(16)), 1, 8) || '-' ||
                         substr(hex(randomblob(16)), 9, 4) || '-' ||
                         substr(hex(randomblob(16)), 13, 4) || '-' ||
                         substr(hex(randomblob(16)), 17, 4) || '-' ||
                         substr(hex(randomblob(16)), 21, 12)),
                   action_type, canonical_arguments,
                   'terminal-seed-' || value, 'Succeeded', '2026-10-15T00:00:00+00:00',
                   '2026-10-15T00:00:00+00:00', 1, 'owner'
            FROM numbers
            CROSS JOIN actions
            WHERE actions.id = '{action.Id.Value:D}';
            """);

        await fixture.ApprovalWorkflow().RecoverUnapprovedExecutingActionsAsync("owner", CancellationToken.None);

        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(action.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalListPrioritizesOlderPendingApprovalsOverRecentResolvedRequests()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(
            PorchRequest with { TurnId = TurnId.New(), CallId = "older-pending" });
        await fixture.SqlAsync($"""
            UPDATE approval_requests
            SET created_at_utc = '2026-10-01T00:00:00+00:00'
            WHERE id = '{pending.ApprovalId!.Value.Value:D}';

            WITH RECURSIVE numbers(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 101
            )
            INSERT INTO actions
                (id, action_type, canonical_arguments, request_hash, status, created_at_utc,
                 updated_at_utc, version, action_owner_id)
            SELECT lower(substr(hex(randomblob(16)), 1, 8) || '-' ||
                         substr(hex(randomblob(16)), 9, 4) || '-' ||
                         substr(hex(randomblob(16)), 13, 4) || '-' ||
                         substr(hex(randomblob(16)), 17, 4) || '-' ||
                         substr(hex(randomblob(16)), 21, 12)),
                   action_type, canonical_arguments,
                   'terminal-seed-' || value, 'Rejected', '2026-10-15T00:00:00+00:00',
                   '2026-10-15T00:00:00+00:00', 1, 'owner'
            FROM numbers
            CROSS JOIN actions
            WHERE actions.id = '{pending.ActionId!.Value.Value:D}';

            WITH RECURSIVE numbers(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 101
            )
            INSERT INTO approval_requests
                (id, action_id, owner_id, status, created_at_utc, expires_at_utc, resolved_at_utc, version)
            SELECT lower(substr(hex(randomblob(16)), 1, 8) || '-' ||
                         substr(hex(randomblob(16)), 9, 4) || '-' ||
                         substr(hex(randomblob(16)), 13, 4) || '-' ||
                         substr(hex(randomblob(16)), 17, 4) || '-' ||
                         substr(hex(randomblob(16)), 21, 12)),
                   actions.id, 'owner', 'Rejected',
                   '2026-10-15T00:00:00+00:00', '2026-10-15T01:00:00+00:00',
                   '2026-10-15T00:00:00+00:00', 2
            FROM actions
            WHERE actions.request_hash LIKE 'terminal-seed-%';
            """);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        var summaries = await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 100, CancellationToken.None);

        var recovered = Assert.Single(summaries, summary => summary.ActionId == pending.ActionId);
        Assert.Equal("Expired", recovered.Approval.Status);
        Assert.Equal("Expired", recovered.ActionStatus);
        Assert.Equal(100, summaries.Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalListExpiresPendingActionsAndKeepsNewerApprovalPending()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var firstRequest = PorchRequest with { TurnId = TurnId.New(), CallId = "first-approval" };
        var first = await fixture.CreatePendingHomeApprovalAsync(firstRequest);
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        var secondRequest = firstRequest with { CallId = "newer-approval" };
        var second = await fixture.Dispatcher.DispatchAsync(secondRequest, CancellationToken.None);
        fixture.Dispatcher.ReleaseTurn(firstRequest.TurnId);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var summaries = await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 10, CancellationToken.None);

        var firstSummary = Assert.Single(summaries, item => item.ActionId == first.ActionId);
        var secondSummary = Assert.Single(summaries, item => item.ActionId == second.ActionId);
        Assert.Equal("Expired", firstSummary.Approval.Status);
        Assert.Equal("Expired", firstSummary.ActionStatus);
        Assert.Equal("Pending", secondSummary.Approval.Status);
        Assert.Equal("AwaitingApproval", secondSummary.ActionStatus);
        Assert.Equal("Expired", (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalListRecoversExpiredDecisionWhoseActionSettlementWasInterrupted()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.CreatePendingHomeApprovalAsync(PorchRequest);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.ApprovalStore.ResolveAsync(
            pending.ApprovalId!.Value,
            "owner",
            "Expired",
            1,
            fixture.Clock.UtcNow,
            CancellationToken.None);

        Assert.Equal(
            "AwaitingApproval",
            (await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None))!.Status);

        var summary = Assert.Single(
            await fixture.ApprovalWorkflow().ReadRecentAsync("owner", 10, CancellationToken.None));

        Assert.Equal("Expired", summary.Approval.Status);
        Assert.Equal("Expired", summary.ActionStatus);
        Assert.Equal("Expired", (await fixture.Journal.GetAsync(pending.ActionId.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LowRiskWriteWithoutVerifiedExplicitOwnerIntentRequiresApproval()
    {
        using var fixture = await Fixture.CreateAsync();

        var pending = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CurrentUserRequest = "Maybe turn on the porch light" },
            CancellationToken.None);

        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal("owner_approval_required", pending.ReasonCode);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExpiredApprovalIsJournaledExpiredWithoutExecution()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persistedApproval = await fixture.Approvals.GetAsync(pending.ApprovalId!.Value, CancellationToken.None);
        var action = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", persistedApproval!.Status);
        Assert.Equal("Expired", action!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovedActionThatExpiresBeforeExecutionCannotRun()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");
        _ = await fixture.Approvals.ResolveAsync(approvalId, "owner", 1, true, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", (await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangedProposalWhileApprovalIsPendingInvalidatesTheAction()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var changed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");

        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(approvalId, "owner", 1, true, CancellationToken.None));
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalRejectsWrongOwnerAndDoesNotExecuteAction()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(
                approvalId,
                "other-owner",
                1,
                approved: true,
                CancellationToken.None));
        var rejected = await fixture.Approvals.ResolveAsync(
            approvalId,
            "owner",
            1,
            approved: false,
            CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Rejected", replay.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PolicyVersionChangeInvalidatesPendingApproval()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);

        var changed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var action = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.Equal("Rejected", action!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task StaleSqlitePolicyInvalidationReturnsApprovalWinnerOnlyForUnchangedContent(bool expired, bool changedContent)
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var journal = new PausedJournal(fixture.Journal, async (entry, token) =>
        {
            if (entry?.Status == "AwaitingApproval" && Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var request = changedContent ? PorchRequest with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" } : PorchRequest;
        var stale = fixture.CreateRestartedDispatcher(journal).DispatchAsync(request, CancellationToken.None).AsTask();
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (expired)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            }
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None);
            var winner = (await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None))!;
            release.SetResult();
            var result = await stale.WaitAsync(TimeSpan.FromSeconds(2));
            if (changedContent)
            {
                Assert.Equal(new ToolDispatchResult("Rejected", null, "action_identity_reused", winner.Id), result);
            }
            else
            {
                Assert.Equal(winner.Outcome, result);
                Assert.Equal(pending.ApprovalId, result.ApprovalId);
            }
            Assert.Equal(winner, await fixture.Journal.GetAsync(winner.Id, CancellationToken.None));
            Assert.Equal(0, fixture.ExecutionCount);
            Assert.Equal(0, fixture.VerificationCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Rejected")]
    [Trait("Category", "Integration")]
    public async Task StaleSqliteApprovalDecisionPreservesPolicyInvalidationOutcomeAndVersion(string decision)
    {
        using var fixture = await Fixture.CreateAsync(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        if (decision == "Expired")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        await fixture.ApprovalStore.ResolveAsync(pending.ApprovalId!.Value, "owner", decision, 1,
            fixture.Clock.UtcNow, CancellationToken.None);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var journal = new PausedJournal(fixture.Journal, async (entry, token) =>
        {
            if (entry?.Status == "AwaitingApproval" && Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var stale = fixture.CreateRestartedDispatcher(journal).DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);
            var winner = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
            var persisted = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None);
            Assert.Equal("action_identity_reused", winner.ReasonCode);
            release.SetResult();
            Assert.Equal(winner, await stale.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(persisted, await fixture.Journal.GetAsync(pending.ActionId.Value, CancellationToken.None));
            Assert.Equal(0, fixture.ExecutionCount);
            Assert.Equal(0, fixture.VerificationCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownExecutionIsObservedOnceAndNeverBlindlyReplayed()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "state_unconfirmed")));

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Unknown", first.Status);
        Assert.Equal("Unknown", replay.Status);
        Assert.Equal("Unknown", persisted!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ObservationCanConfirmDesiredStateAfterUnknownDelivery()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, """{"accepted":true}""", "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)));

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("""{"accepted":true}""", result.ResultJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task FreshDispatcherKeepsOrphanedWritesUncertainAcrossEquivalentFreshIds(bool queued, bool throws)
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var action = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            action.Id, "Executing", action.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetVerification(async (_, _, outcome, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, outcome.Status);
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (throws)
            {
                throw new InvalidOperationException("controlled orphan observation failure");
            }
            return new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed");
        });
        var restarted = fixture.CreateRestartedDispatcher();
        var recovery = restarted.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshRequest = PorchRequest with { CallId = "fresh-after-orphan" };
        var fresh = queued ? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask() : null;
        releaseObservation.SetResult();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery);
        }
        else
        {
            Assert.Equal("Unknown", (await recovery).Status);
        }

        var stopped = await (fresh ?? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(action.Id, CancellationToken.None))!.Status);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(stopped.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DefinitiveNonApplicationAfterUnknownDeliveryAllowsOnlyOneFreshRetry()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "definitively_not_applied")));

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var second = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "second-definitive-failure" }, CancellationToken.None);
        var stopped = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "third-definitive-failure" }, CancellationToken.None);

        Assert.Equal("Failed", first.Status);
        Assert.Equal("Failed", replay.Status);
        Assert.Equal("Failed", second.Status);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal("Failed", (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedExecutionAndConfirmedNotAppliedVerificationRemainFailed()
    {
        using var knownFailure = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "device_rejected")));
        using var notApplied = await Fixture.CreateAsync(
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "state_unchanged")));

        var executionFailure = await knownFailure.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var verificationFailure = await notApplied.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Failed", executionFailure.Status);
        Assert.Equal("device_rejected", executionFailure.ReasonCode);
        Assert.Equal(0, knownFailure.VerificationCount);
        Assert.Equal("Failed", verificationFailure.Status);
        Assert.Equal(1, notApplied.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerificationDeadlineRecordsUnknownInsteadOfFailed()
    {
        using var fixture = await Fixture.CreateAsync(
            verification: async (_, _, _, cancellationToken) =>
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                    .Task.WaitAsync(cancellationToken);
                return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
            });

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Unknown", result.Status);
        Assert.Equal("verification_interrupted", result.ReasonCode);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationAfterExecutionBeginsRecordsUnknown()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.SetExecution((_, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, cancellation.Token);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Unknown", result.Status);
        Assert.Equal("Unknown", replay.Status);
        Assert.Equal("execution_cancelled_after_dispatch", result.ReasonCode);
        Assert.Equal("Unknown", persisted!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationBeforeDispatchCreatesNoJournalEntry()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Dispatcher.DispatchAsync(PorchRequest, cancellation.Token));

        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Null(await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentDuplicateDispatchClaimsExecutionOnlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(
            execution: async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new ToolExecutionOutcome(ToolExecutionStatus.Completed, """{"ok":true}""", null);
            });

        var firstTask = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        release.TrySetResult();
        var first = await firstTask;

        Assert.Equal("Executing", duplicate.Status);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestartedDispatcherReconcilesPersistedExecutingActionWithoutReplayingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        await fixture.Journal.UpdateStatusAsync(
            persisted!.Id,
            "Executing",
            persisted.Version,
            fixture.Clock.UtcNow,
            CancellationToken.None);
        fixture.SetVerification((_, _, execution, _) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        });

        var restartedDispatcher = fixture.CreateRestartedDispatcher();
        var recovered = await restartedDispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await restartedDispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal(recovered, replay);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        Assert.Equal("Succeeded", (await fixture.Journal.GetAsync(completed.ActionId.Value, CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public async Task RestartedExecutionHandlesChangedPolicyWithoutReplayingIt(bool allowed)
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        await fixture.Journal.UpdateStatusAsync(
            persisted!.Id, "Executing", persisted.Version, fixture.Clock.UtcNow, CancellationToken.None);
        fixture.Policy.Replace("policy-v2",
            allowed ? fixture.AllowedPermission : fixture.AllowedPermission with { EntityId = "light.other" });

        var recovered = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal(allowed ? "Succeeded" : "Unknown", recovered.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(allowed ? 2 : 1, fixture.VerificationCount);
        Assert.Equal(recovered.Status, (await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PolicyChangeDoesNotMistakeActiveExecutionForRestartRecovery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(execution: async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        });
        var first = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Executing", duplicate.Status);
        Assert.Equal(0, fixture.VerificationCount);
        release.SetResult();
        Assert.Equal("Succeeded", (await first).Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestartedDispatcherDoesNotOverwriteAConcurrentJournalOutcome()
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        var interrupted = await fixture.Journal.UpdateStatusAsync(
            persisted!.Id,
            "Executing",
            persisted.Version,
            fixture.Clock.UtcNow,
            CancellationToken.None);
        fixture.SetVerification(async (_, _, execution, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            var concurrent = await fixture.Journal.GetAsync(interrupted.Id, token)
                ?? throw new InvalidOperationException("The action was not journaled.");
            await fixture.Journal.UpdateStatusAsync(
                concurrent.Id,
                "Unknown",
                concurrent.Version,
                fixture.Clock.UtcNow,
                token);
            return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
        });

        var recovered = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Unknown", recovered.Status);
        Assert.Equal("recorded_action_outcome", recovered.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(interrupted.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnexpectedHandlerExceptionIsSurfacedAndJournaledUnknown()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => throw new InvalidOperationException("controlled handler failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None));

        var persisted = await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None);
        Assert.Equal("Unknown", persisted!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnsupportedRiskAndUnauthenticatedActionsAreDenied()
    {
        using var unsupported = await Fixture.CreateAsync(riskClass: ToolRiskClass.Unsupported);
        using var unauthenticated = await Fixture.CreateAsync();

        var unsupportedResult = await unsupported.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var missingOwner = await unauthenticated.Dispatcher.DispatchAsync(PorchRequest with { OwnerId = null }, CancellationToken.None);

        Assert.Equal("Rejected", unsupportedResult.Status);
        Assert.Equal("tool_action_not_allowlisted", unsupportedResult.ReasonCode);
        Assert.Equal("Rejected", missingOwner.Status);
        Assert.Equal("authenticated_owner_required", missingOwner.ReasonCode);
        Assert.Equal(0, unsupported.ExecutionCount);
        Assert.Equal(0, unauthenticated.ExecutionCount);
        Assert.Null(await unauthenticated.Journal.GetAsync(unauthenticated.ActionId(PorchRequest), CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IsolatedDatabaseFile file;
        private Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> execution;
        private Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> verification;
        private int executionCount;
        private int verificationCount;
        private ToolRegistry registry = null!;

        private Fixture(
            IsolatedDatabaseFile file,
            SqliteDatabase database,
            ToolRiskClass riskClass,
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>>? execution,
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>>? verification)
        {
            this.file = file;
            Database = database;
            Journal = new SqliteActionJournalStore(database);
            ApprovalStore = new SqliteApprovalStore(database);
            Clock = new MutableClock(Start);
            Approvals = new ApprovalService(ApprovalStore, Journal, Clock);
            AllowedPermission = new ToolPermission(
                "home.set_light",
                "home.set_light",
                "turn_on",
                "light.porch",
                ToolRiskClass.ReadOnly);
            Policy = new MutablePolicy("policy-v1", AllowedPermission);
            this.execution = execution ?? DefaultExecution;
            this.verification = verification ?? DefaultVerification;
            var registration = new ToolRegistration<ToolArguments>(
                "home.set_light",
                """{"type":"object","properties":{"Entity":{"type":"string"},"On":{"type":"boolean"}},"required":["Entity","On"],"additionalProperties":false}""",
                riskClass,
                Prepare,
                Execute,
                Verify);
            registry = new ToolRegistry([registration]);
            Dispatcher = new ToolDispatcher(registry, Policy, Journal, Approvals, Clock, TimeSpan.FromSeconds(1));
        }

        public SqliteActionJournalStore Journal { get; }
        public SqliteApprovalStore ApprovalStore { get; }
        public SqliteDatabase Database { get; }
        public ApprovalService Approvals { get; }
        public MutableClock Clock { get; }
        public MutablePolicy Policy { get; }
        public ToolPermission AllowedPermission { get; }
        public ToolDispatcher Dispatcher { get; }
        public int ExecutionCount => Volatile.Read(ref executionCount);
        public int VerificationCount => Volatile.Read(ref verificationCount);

        public static async Task<Fixture> CreateAsync(
            ToolRiskClass riskClass = ToolRiskClass.LowRiskWrite,
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>>? execution = null,
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>>? verification = null)
        {
            var file = IsolatedDatabaseFile.Create();
            try
            {
                var database = new SqliteDatabase(file.Path);
                await database.InitializeAsync();
                return new Fixture(file, database, riskClass, execution, verification);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public ActionId ActionId(ToolDispatchRequest request) =>
            new(new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{request.TurnId.Value:D}\n{request.CallId}")).AsSpan(0, 16)));

        public void SetExecution(
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> handler) =>
            execution = handler;

        public void SetVerification(
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> handler) =>
            verification = handler;

        public async Task<ToolDispatchResult> CreatePendingHomeApprovalAsync(ToolDispatchRequest request)
        {
            var conversations = new SqliteConversationStore(Database, Clock);
            var conversationId = ConversationId.New();
            await conversations.CreateConversationAsync(new(
                conversationId, "owner", "Approval workflow", Clock.UtcNow, Clock.UtcNow), default);
            await conversations.SubmitTurnAsync(new(
                request.TurnId,
                conversationId,
                Guid.NewGuid().ToString(),
                new string('a', 64),
                Guid.NewGuid(),
                request.CurrentUserRequest!,
                Clock.UtcNow,
                RequiredOwnerId: "owner"), default);
            return await Dispatcher.DispatchAsync(request, default);
        }

        public async Task SqlAsync(string sql)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ToolDispatcher CreateRestartedDispatcher(IActionJournalStore? journal = null) =>
            new(registry, Policy, journal ?? Journal, Approvals, Clock, TimeSpan.FromSeconds(1));

        public ApprovalWorkflowService ApprovalWorkflow(ToolDispatcher? dispatcher = null)
        {
            dispatcher ??= Dispatcher;
            var memories = new SqliteConfirmedMemoryStore(Database, Clock);
            var proposals = new MemoryProposalService(
                memories, Journal, Approvals, dispatcher, new SqliteConversationStore(Database, Clock), Clock);
            return new(ApprovalStore, Journal, Approvals, dispatcher, memories, proposals, Clock);
        }

        public void Dispose() => file.Dispose();

        private ValueTask<ToolPreparationResult> Prepare(
            ToolArguments arguments,
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Entity == "porch")
            {
                return ValueTask.FromResult(ToolPreparationResult.Clarify("entity_alias_ambiguous"));
            }

            var entityId = arguments.Entity == "porch-lamp" ? "light.porch" : arguments.Entity;
            var operation = arguments.On ? "turn_on" : "turn_off";
            var canonicalArguments = JsonSerializer.Serialize(new { Entity = entityId, On = arguments.On });
            return ValueTask.FromResult(ToolPreparationResult.Ready(
                new ToolActionDraft(
                    "home.set_light",
                    operation,
                    entityId,
                    canonicalArguments,
                    request.CurrentUserRequest == "Turn on the porch light")));
        }

        private async ValueTask<ToolExecutionOutcome> Execute(
            ToolArguments arguments,
            ToolActionDraft action,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            return await execution(arguments, action, cancellationToken);
        }

        private async ValueTask<ToolVerificationOutcome> Verify(
            ToolArguments arguments,
            ToolActionDraft action,
            ToolExecutionOutcome outcome,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref verificationCount);
            return await verification(arguments, action, outcome, cancellationToken);
        }

        private static ValueTask<ToolExecutionOutcome> DefaultExecution(
            ToolArguments _,
            ToolActionDraft __,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolExecutionOutcome(
                ToolExecutionStatus.Completed,
                """{"ok":true}""",
                null));
        }

        private static ValueTask<ToolVerificationOutcome> DefaultVerification(
            ToolArguments _,
            ToolActionDraft __,
            ToolExecutionOutcome ___,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        }
    }

    private sealed class PausedJournal(
        IActionJournalStore inner,
        Func<ActionJournalEntry?, CancellationToken, Task> afterRead,
        Func<CancellationToken, Task>? beforeExecuting = null,
        Func<IReadOnlyList<ActionJournalEntry>, CancellationToken, Task>? afterTurnRead = null) : IActionJournalStore
    {
        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken cancellationToken) =>
            inner.CreateAsync(entry, cancellationToken);

        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(
            string ownerId, int maximumActions, CancellationToken cancellationToken) =>
            inner.ReadRecentForOwnerAsync(ownerId, maximumActions, cancellationToken);

        public async ValueTask<IReadOnlyList<ActionJournalEntry>> ReadForTurnAsync(
            string ownerId, TurnId turnId, CancellationToken cancellationToken)
        {
            var entries = await inner.ReadForTurnAsync(ownerId, turnId, cancellationToken);
            if (afterTurnRead is not null)
            {
                await afterTurnRead(entries, cancellationToken);
            }
            return entries;
        }

        public async ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken)
        {
            var entry = await inner.GetAsync(id, cancellationToken);
            await afterRead(entry, cancellationToken);
            return entry;
        }

        public async ValueTask<ActionJournalEntry> UpdateStatusAsync(
            ActionId id, string status, long expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken,
            ToolDispatchResult? outcome = null)
        {
            if (status == "Executing" && beforeExecuting is not null)
            {
                await beforeExecuting(cancellationToken);
            }
            return await inner.UpdateStatusAsync(id, status, expectedVersion, updatedAtUtc, cancellationToken, outcome);
        }
    }

    private sealed record ToolArguments(string Entity, bool On);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset now = now;

        public DateTimeOffset UtcNow => now;

        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }

    private sealed class MutablePolicy(string version, ToolPermission permission) : IToolAuthorizationPolicy
    {
        private ToolAllowlistPolicy current = new(version, [permission]);

        public void Replace(string newVersion, ToolPermission newPermission) =>
            current = new ToolAllowlistPolicy(newVersion, [newPermission]);

        public ToolAuthorizationDecision Evaluate(
            string toolName,
            ToolActionDraft action,
            ToolRiskClass declaredRisk) =>
            current.Evaluate(toolName, action, declaredRisk);
    }
}
