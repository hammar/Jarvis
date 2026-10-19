using System.Reflection;
using System.Text.Json.Nodes;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Memory;
using PersonalAgent.Application.Tools;
using PersonalAgent.Domain;
using Xunit;

namespace PersonalAgent.UnitTests;

[Trait("Category", "Unit")]
public sealed class ApprovalWorkflowExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpiredApprovedUnclaimedActionIsSettledWithoutDispatch()
    {
        var fixture = new Fixture();

        var result = await fixture.Workflow.ExpireApprovedUnclaimedAsync(
            "owner", fixture.Approval.Id, fixture.Approval.ExpectedVersion, default);

        Assert.Equal("Expired", result.Status);
        Assert.Equal("Expired", fixture.Journal.Entry.Status);
        Assert.Equal("approval_expired_before_execution", result.ReasonCode);
        Assert.Equal(fixture.Clock.UtcNow, fixture.Journal.Entry.UpdatedAtUtc);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task ExpiryFinalizationRequiresExactApprovedOwnerVersionAndExpiredUnclaimedHomeAction()
    {
        var wrongOwner = new Fixture(approvalOwner: "someone-else");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => wrongOwner.ExpireAsync());

        var pending = new Fixture(approvalStatus: "Pending");
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => pending.Workflow.ExpireApprovedUnclaimedAsync("owner", pending.Approval.Id, 1, default).AsTask());

        var staleVersion = new Fixture();
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => staleVersion.Workflow.ExpireApprovedUnclaimedAsync("owner", staleVersion.Approval.Id, 99, default).AsTask());

        var notExpired = new Fixture(expiry: Now.AddSeconds(1));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() => notExpired.ExpireAsync());

        var memoryAction = new Fixture(toolName: "memory.propose_change");
        Assert.Equal("Expired", (await memoryAction.ExpireAsync()).Status);
        var wrongMemoryOwner = new Fixture(toolName: "memory.propose_change", memoryOwner: "someone-else");
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongMemoryOwner.ExpireAsync());

        var wrongOwnerEnvelope = new Fixture(envelopeOwner: "someone-else");
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongOwnerEnvelope.ExpireAsync());
    }

    [Fact]
    public async Task ExpiryFinalizationDoesNotChangeAlreadySettledActions()
    {
        var terminal = new Fixture(actionStatus: "Unknown");
        var recorded = new ToolDispatchResult("Unknown", null, "ambiguous", terminal.Journal.Entry.Id);
        terminal.Journal.Entry = terminal.Journal.Entry with { Outcome = recorded };

        Assert.Equal(recorded, await terminal.ExpireAsync());
        Assert.Empty(terminal.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, terminal.Dispatcher.Dispatches);

        var legacyTerminal = new Fixture(actionStatus: "Failed");
        Assert.Equal("Failed", (await legacyTerminal.ExpireAsync()).Status);
    }

    [Fact]
    public async Task ExpiryFinalizationReleasesInvalidatedMemoryAndSettlesConcurrentTerminalTransition()
    {
        var invalidated = new Fixture();
        invalidated.Journal.Entry = invalidated.Journal.Entry with { MemoryInvalidated = true };
        var invalidatedResult = await invalidated.ExpireAsync();
        Assert.Equal("Rejected", invalidatedResult.Status);
        Assert.Equal("memory_result_invalidated", invalidatedResult.ReasonCode);
        Assert.Single(invalidated.Dispatcher.ReleasedApprovals);

        var concurrent = new Fixture();
        concurrent.Journal.OnUpdate = _ =>
        {
            concurrent.Journal.Entry = concurrent.Journal.Entry with { Status = "Expired" };
            throw new PersistenceConcurrencyException("Concurrent settlement.");
        };
        Assert.Equal("Expired", (await concurrent.ExpireAsync()).Status);
        Assert.Single(concurrent.Dispatcher.ReleasedApprovals);

        var stillPending = new Fixture();
        stillPending.Journal.OnUpdate = _ => throw new PersistenceConcurrencyException("Concurrent settlement.");
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() => stillPending.ExpireAsync());
    }

    [Fact]
    public async Task RecentApprovalReadReturnsOnlyValidOwnerBoundSupportedActions()
    {
        var empty = new Fixture();
        Assert.Empty(await empty.Workflow.ReadRecentAsync("owner", 10, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await empty.Workflow.ReadRecentAsync("owner", 0, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await empty.Workflow.ReadRecentAsync("", 10, default));

        var fixture = new Fixture();
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Pending",
                Now, Now.AddMinutes(5), null, fixture.Approval.ExpectedVersion)
        ];
        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));
        Assert.Equal("home.set_light", summary.ToolName);
        Assert.Equal("AwaitingApproval", summary.ActionStatus);

        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "someone-else", "Pending",
                Now, fixture.Approval.ExpiresAtUtc, null, fixture.Approval.ExpectedVersion)
        ];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Workflow.ReadRecentAsync("owner", 10, default));
    }

    [Fact]
    public async Task RecentApprovalReadExpiresStalePendingApprovalAndReleasesItsBudget()
    {
        var fixture = new Fixture(approvalStatus: "Pending");
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Pending",
                Now, Now.AddMinutes(-1), null, fixture.Approval.ExpectedVersion)
        ];

        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Equal("Expired", summary.Approval.Status);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task RecentApprovalReadSettlesPreviouslyExpiredApprovalWhoseActionIsStillPending()
    {
        var fixture = new Fixture(approvalStatus: "Expired", actionStatus: "AwaitingApproval");
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Expired",
                Now.AddMinutes(-5), Now.AddMinutes(-1), Now, fixture.Approval.ExpectedVersion)
        ];

        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Equal("Expired", summary.Approval.Status);
        Assert.Equal("Expired", summary.ActionStatus);
        Assert.Equal("Expired", fixture.Journal.Entry.Status);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task RecentApprovalReadMarksInterruptedExecutionUnknownWhenSourceIsUnavailable()
    {
        var fixture = new Fixture(actionStatus: "Executing", expiry: Now.AddMinutes(5));
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Approved",
                Now, fixture.Approval.ExpiresAtUtc, Now, fixture.Approval.ExpectedVersion)
        ];

        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Equal("Unknown", summary.ActionStatus);
        Assert.Equal("interrupted_execution_source_unavailable", fixture.Journal.Entry.Outcome?.ReasonCode);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task RecentApprovalReadDoesNotReleaseTurnBudgetWhileActionIsStillExecuting()
    {
        var fixture = new Fixture(actionStatus: "Executing", expiry: Now.AddMinutes(5));
        var approval = fixture.Approval;
        fixture.ApprovalStore.Records =
        [
            new(approval.Id, approval.ActionId, approval.OwnerId, "Approved",
                Now, approval.ExpiresAtUtc, Now, approval.ExpectedVersion)
        ];
        fixture.Memories.Source = new(
            Guid.NewGuid(), ConversationId.New(), "user", "turn on light.test", Now);
        fixture.Dispatcher.DispatchStatus = "Executing";

        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Equal("Executing", summary.ActionStatus);
        Assert.Equal("Executing", fixture.Journal.Entry.Status);
        Assert.Empty(fixture.Dispatcher.ReleasedTurns);
    }

    [Fact]
    public async Task RecentApprovalReadAcceptsExpirySettledConcurrently()
    {
        var fixture = new Fixture(approvalStatus: "Pending");
        fixture.Approvals.ThrowOnResolve = true;
        fixture.Approvals.Current = fixture.Approval with { Status = "Expired" };
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Pending",
                Now, Now.AddMinutes(-1), null, fixture.Approval.ExpectedVersion)
        ];

        var summary = Assert.Single(await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Equal("Expired", summary.Approval.Status);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task RecentApprovalReadFailsWhenConcurrentExpiryLeavesApprovalPending()
    {
        var fixture = new Fixture(approvalStatus: "Pending");
        fixture.Approvals.ThrowOnResolve = true;
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Pending",
                Now, Now.AddMinutes(-1), null, fixture.Approval.ExpectedVersion)
        ];

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task UnapprovedExecutingRecoveryMarksActionUnknownWhenOriginalSourceIsUnavailable()
    {
        var fixture = new Fixture(actionStatus: "Executing");
        fixture.Journal.RecentActions = [fixture.Journal.Entry];

        await fixture.Workflow.RecoverUnapprovedExecutingActionsAsync("owner", default);

        Assert.Equal("Unknown", fixture.Journal.Entry.Status);
        Assert.Equal("interrupted_execution_source_unavailable", fixture.Journal.Entry.Outcome?.ReasonCode);
        Assert.Equal([fixture.Journal.Entry.Id], fixture.Dispatcher.ReleasedApprovals);
        Assert.Single(fixture.Dispatcher.ReleasedTurns);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task UnapprovedExecutingRecoveryRejectsAnActionBoundToAnotherOwner()
    {
        var fixture = new Fixture(actionStatus: "Executing", envelopeOwner: "someone-else");
        fixture.Journal.RecentActions = [fixture.Journal.Entry];

        var issues = await fixture.Workflow.RecoverUnapprovedExecutingActionsAsync("owner", default);

        Assert.Equal("Executing", fixture.Journal.Entry.Status);
        Assert.Equal([new ActionRecoveryIssue(fixture.Journal.Entry.Id, "invalid_action_journal")], issues);
        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
        Assert.Empty(fixture.Dispatcher.ReleasedTurns);
    }

    [Theory]
    [InlineData("FormatVersion", "2")]
    [InlineData("TurnId", null)]
    [InlineData("CallId", "\"\"")]
    [InlineData("OwnerId", "\"\"")]
    [InlineData("ToolName", "\"\"")]
    public async Task UnapprovedExecutingRecoveryReportsIncompleteActionEnvelope(
        string property,
        string? replacementJson)
    {
        var fixture = new Fixture(actionStatus: "Executing");
        var envelope = JsonNode.Parse(fixture.Journal.Entry.CanonicalArguments)?.AsObject()
            ?? throw new InvalidOperationException("The action envelope was not valid JSON.");
        if (replacementJson is null)
        {
            envelope.Remove(property);
        }
        else
        {
            envelope[property] = JsonNode.Parse(replacementJson);
        }
        fixture.Journal.Entry = fixture.Journal.Entry with { CanonicalArguments = envelope.ToJsonString() };
        fixture.Journal.RecentActions = [fixture.Journal.Entry];

        var issues = await fixture.Workflow.RecoverUnapprovedExecutingActionsAsync("owner", default);

        Assert.Equal([new ActionRecoveryIssue(fixture.Journal.Entry.Id, "invalid_action_journal")], issues);
        Assert.Equal("Executing", fixture.Journal.Entry.Status);
        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
        Assert.Empty(fixture.Dispatcher.ReleasedTurns);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task UnapprovedExecutingRecoveryNeverCompletesMemoryProposals()
    {
        var fixture = new Fixture(toolName: "memory.propose_change", actionStatus: "Executing");
        fixture.Journal.RecentActions = [fixture.Journal.Entry];

        await fixture.Workflow.RecoverUnapprovedExecutingActionsAsync("owner", default);

        Assert.Equal("Executing", fixture.Journal.Entry.Status);
        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
        Assert.Empty(fixture.Dispatcher.ReleasedTurns);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task ResolveRejectsInvalidatedMemoryApprovalWhenItsOwnerBindingChanged()
    {
        var fixture = new Fixture(toolName: "memory.propose_change");
        fixture.Journal.Entry = fixture.Journal.Entry with
        {
            MemoryInvalidated = true,
            MemoryOwnerId = "someone-else"
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Workflow.ResolveAsync(
            "owner", fixture.Approval.Id, fixture.Approval.ExpectedVersion, approved: true, default).AsTask());

        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
    }

    [Fact]
    public async Task ResolveReturnsRecordedOutcomeForAlreadySettledApprovedMemoryAction()
    {
        var fixture = new Fixture(toolName: "memory.propose_change", actionStatus: "Unknown");
        var recorded = new ToolDispatchResult("Unknown", null, "ambiguous", fixture.Journal.Entry.Id, fixture.Approval.Id);
        fixture.Journal.Entry = fixture.Journal.Entry with { Outcome = recorded };

        var result = await fixture.Workflow.ResolveAsync(
            "owner", fixture.Approval.Id, fixture.Approval.ExpectedVersion, approved: true, default);

        Assert.Same(recorded, result);
        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
    }

    [Fact]
    public async Task RecentApprovalReadFailsWhenExpiredActionDisappearsDuringSettlement()
    {
        var fixture = new Fixture(approvalStatus: "Pending");
        fixture.ApprovalStore.Records =
        [
            new(fixture.Approval.Id, fixture.Approval.ActionId, "owner", "Pending",
                Now, Now.AddMinutes(-1), null, fixture.Approval.ExpectedVersion)
        ];
        fixture.Journal.MissingOnGetCall = 2;

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Workflow.ReadRecentAsync("owner", 10, default));

        Assert.Empty(fixture.Dispatcher.ReleasedApprovals);
        Assert.Equal(0, fixture.Dispatcher.Dispatches);
    }

    private sealed class Fixture
    {
        internal readonly ApprovalRequest Approval;
        internal readonly Journal Journal;
        internal readonly Dispatcher Dispatcher = new();
        internal readonly MemoryStore Memories = new();
        internal readonly Clock Clock;
        internal readonly ApprovalStore ApprovalStore = new();
        internal readonly Approvals Approvals;
        internal readonly ApprovalWorkflowService Workflow;

        internal Fixture(
            string approvalOwner = "owner",
            string envelopeOwner = "owner",
            string approvalStatus = "Approved",
            string actionStatus = "AwaitingApproval",
            string toolName = "home.set_light",
            DateTimeOffset? expiry = null,
            string? memoryOwner = null)
        {
            var actionId = new ActionId(Guid.NewGuid());
            Approval = new(new(actionId.Value), actionId, approvalOwner, expiry ?? Now.AddMinutes(-1), 7, approvalStatus);
            Journal = new(CreateEntry(actionId, envelopeOwner, toolName, actionStatus, memoryOwner));
            Clock = new(Now);
            Approvals = new Approvals(Approval, Journal);
            Workflow = new(
                ApprovalStore,
                Journal,
                Approvals,
                Dispatcher,
                Memories,
                new MemoryProposalService(
                    null!, null!, null!, null!, TerminalConversationStore.Create(), Clock),
                Clock);
        }

        internal Task<ToolDispatchResult> ExpireAsync() =>
            Workflow.ExpireApprovedUnclaimedAsync("owner", Approval.Id, Approval.ExpectedVersion, default).AsTask();

        private static ActionJournalEntry CreateEntry(
            ActionId id, string owner, string tool, string status, string? memoryOwner)
        {
            var canonical = $$"""
                {
                  "FormatVersion": 1,
                  "TurnId": "{{Guid.NewGuid():D}}",
                  "CallId": "call",
                  "OwnerId": "{{owner}}",
                  "ToolName": "{{tool}}",
                  "ActionType": "{{tool}}",
                  "Operation": "set",
                  "EntityId": "light.test",
                  "PolicyVersion": "policy",
                  "RiskClass": 1,
                  "CurrentRequestHash": "hash",
                  "CanonicalArguments": "{}"
                }
                """;
            return new ActionJournalEntry(id, tool, canonical, "hash", status, Now, Now, 3)
            {
                MemoryOwnerId = tool == "memory.propose_change" ? memoryOwner ?? "owner" : null
            };
        }
    }

    public class TerminalConversationStore : DispatchProxy
    {
        public static IConversationStore Create() =>
            Create<IConversationStore, TerminalConversationStore>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IConversationStore.GetTurnForOwnerAsync))
            {
                var turnId = (TurnId)args![0]!;
                return ValueTask.FromResult<ConversationTurn?>(new(
                    turnId,
                    ConversationId.New(),
                    TurnStatus.Interrupted,
                    Now,
                    Now,
                    1));
            }

            throw new NotSupportedException($"Unexpected conversation-store call: {targetMethod?.Name}.");
        }
    }

    private sealed class ApprovalStore : IApprovalStore
    {
        internal IReadOnlyList<ApprovalStorageRecord> Records { get; set; } = [];
        public ValueTask<ApprovalStorageRecord> CreateAsync(ApprovalStorageRecord approval, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Records.FirstOrDefault(record => record.Id == id));
        public ValueTask<IReadOnlyList<ApprovalStorageRecord>> ReadRecentForOwnerAsync(string ownerId, int maximumRequests, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ApprovalStorageRecord>>(Records.Take(maximumRequests).ToArray());
        public ValueTask<ApprovalStorageRecord> ResolveAsync(ApprovalId id, string ownerId, string status, long expectedVersion,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class Approvals : IApprovalService
    {
        private readonly ApprovalRequest approval;
        private readonly Journal journal;

        internal Approvals(ApprovalRequest approval, Journal journal)
        {
            this.approval = approval;
            this.journal = journal;
            Current = approval;
        }

        internal ApprovalRequest Current { get; set; }
        internal bool ThrowOnResolve { get; set; }

        public ValueTask<ApprovalRequest> CreateAsync(ActionId actionId, string ownerId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ApprovalRequest?> GetAsync(ApprovalId approvalId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ApprovalRequest?>(approvalId == approval.Id ? Current : null);
        public ValueTask<ApprovalRequest> ResolveAsync(ApprovalId approvalId, string ownerId, long expectedVersion, bool approved,
            CancellationToken cancellationToken)
        {
            if (ThrowOnResolve)
            {
                throw new PersistenceConcurrencyException("Concurrent approval settlement.");
            }

            Current = approval with { Status = approved ? "Approved" : "Expired" };
            return ValueTask.FromResult(Current);
        }

        public async ValueTask<ActionJournalEntry> SettleExpiredAsync(
            ApprovalId approvalId,
            string ownerId,
            long expectedVersion,
            CancellationToken cancellationToken)
            => await SettleResolvedAsync(approvalId, ownerId, expectedVersion, "Expired", cancellationToken);

        public async ValueTask<ActionJournalEntry> SettleRejectedAsync(
            ApprovalId approvalId,
            string ownerId,
            long expectedVersion,
            CancellationToken cancellationToken)
            => await SettleResolvedAsync(approvalId, ownerId, expectedVersion, "Rejected", cancellationToken);

        private async ValueTask<ActionJournalEntry> SettleResolvedAsync(
            ApprovalId approvalId,
            string ownerId,
            long expectedVersion,
            string status,
            CancellationToken cancellationToken)
        {
            if (Current.Id != approvalId
                || Current.OwnerId != ownerId
                || Current.ExpectedVersion != expectedVersion
                || Current.Status != status)
            {
                throw new PersistenceConcurrencyException("The approval is not the expected resolved version.");
            }

            var entry = await journal.GetAsync(Current.ActionId, cancellationToken)
                ?? throw new PersistenceConcurrencyException("The resolved action disappeared.");
            if (entry.Status == "AwaitingApproval")
            {
                journal.Entry = entry with
                {
                    Status = status,
                    Outcome = new(status, null, $"approval_{status.ToLowerInvariant()}", entry.Id, approvalId)
                };
            }

            return journal.Entry;
        }
    }

    private sealed class Journal(ActionJournalEntry entry) : IActionJournalStore
    {
        internal ActionJournalEntry Entry { get; set; } = entry;
        internal IReadOnlyList<ActionJournalEntry> RecentActions { get; set; } = [entry];
        internal Func<string, ActionJournalEntry>? OnUpdate { get; set; }
        internal int GetCalls { get; private set; }
        internal int MissingOnGetCall { get; set; }

        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry value, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActionJournalEntry?>(++GetCalls == MissingOnGetCall || id != Entry.Id ? null : Entry);
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(string ownerId, int maximumActions, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ActionJournalEntry>>(RecentActions.Take(maximumActions).ToArray());
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadForTurnAsync(string ownerId, TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ActionJournalEntry> UpdateStatusAsync(ActionId id, string status, long expectedVersion, DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken, ToolDispatchResult? outcome = null)
        {
            if (OnUpdate is { } onUpdate)
            {
                var result = onUpdate(status);
                return ValueTask.FromResult(result);
            }

            Entry = Entry with { Status = status, Version = expectedVersion + 1, UpdatedAtUtc = updatedAtUtc, Outcome = outcome };
            return ValueTask.FromResult(Entry);
        }
    }

    private sealed class Dispatcher : IToolDispatcher
    {
        internal List<ActionId> ReleasedApprovals { get; } = [];
        internal List<TurnId> ReleasedTurns { get; } = [];
        internal int Dispatches { get; private set; }
        internal string DispatchStatus { get; set; } = "Succeeded";
        public void ReleaseTurn(TurnId turnId) => ReleasedTurns.Add(turnId);
        public void ReleaseApproval(ActionId actionId) => ReleasedApprovals.Add(actionId);
        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken)
        {
            Dispatches++;
            return ValueTask.FromResult(new ToolDispatchResult(DispatchStatus, null, null));
        }
        public ValueTask<ActionJournalEntry> MarkUnavailableExecutingActionUnknownAsync(
            ActionJournalEntry action,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryStore : IConfirmedMemoryStore
    {
        internal ConversationMessage? Source { get; set; }
        public ValueTask<ConversationMessage?> ReadSourceAsync(string ownerId, TurnId turnId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Source);
        public ValueTask<IReadOnlyList<MemoryFact>> ReadActiveAsync(string ownerId, int maximumFacts, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(string ownerId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<MemoryFact?> GetAsync(string ownerId, MemoryFactId id, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<IReadOnlyList<MemoryFact>> FindConflictsAsync(string ownerId, string subject, string key, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<MemoryReceipt> CommitAsync(ConfirmedMemoryCommand command, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<MemoryReceipt?> GetReceiptAsync(string ownerId, ActionId actionId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
