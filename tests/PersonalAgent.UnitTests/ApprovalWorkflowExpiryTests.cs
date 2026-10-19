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
        await Assert.ThrowsAsync<InvalidDataException>(() => memoryAction.ExpireAsync());

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

    private sealed class Fixture
    {
        internal readonly ApprovalRequest Approval;
        internal readonly Journal Journal;
        internal readonly Dispatcher Dispatcher = new();
        internal readonly Clock Clock;
        internal readonly ApprovalWorkflowService Workflow;

        internal Fixture(
            string approvalOwner = "owner",
            string envelopeOwner = "owner",
            string approvalStatus = "Approved",
            string actionStatus = "AwaitingApproval",
            string toolName = "home.set_light",
            DateTimeOffset? expiry = null)
        {
            var actionId = new ActionId(Guid.NewGuid());
            Approval = new(new(actionId.Value), actionId, approvalOwner, expiry ?? Now.AddMinutes(-1), 7, approvalStatus);
            Journal = new(CreateEntry(actionId, envelopeOwner, toolName, actionStatus));
            Clock = new(Now);
            var approvals = new Approvals(Approval);
            Workflow = new(
                new ApprovalStore(),
                Journal,
                approvals,
                Dispatcher,
                new MemoryStore(),
                new MemoryProposalService(null!, null!, null!, null!),
                Clock);
        }

        internal Task<ToolDispatchResult> ExpireAsync() =>
            Workflow.ExpireApprovedUnclaimedAsync("owner", Approval.Id, Approval.ExpectedVersion, default).AsTask();

        private static ActionJournalEntry CreateEntry(ActionId id, string owner, string tool, string status)
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
            return new(id, tool, canonical, "hash", status, Now, Now, 3);
        }
    }

    private sealed class ApprovalStore : IApprovalStore
    {
        public ValueTask<ApprovalStorageRecord> CreateAsync(ApprovalStorageRecord approval, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<IReadOnlyList<ApprovalStorageRecord>> ReadRecentForOwnerAsync(string ownerId, int maximumRequests, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ApprovalStorageRecord> ResolveAsync(ApprovalId id, string ownerId, string status, long expectedVersion,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class Approvals(ApprovalRequest approval) : IApprovalService
    {
        public ValueTask<ApprovalRequest> CreateAsync(ActionId actionId, string ownerId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ApprovalRequest?> GetAsync(ApprovalId approvalId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ApprovalRequest?>(approvalId == approval.Id ? approval : null);
        public ValueTask<ApprovalRequest> ResolveAsync(ApprovalId approvalId, string ownerId, long expectedVersion, bool approved,
            CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class Journal(ActionJournalEntry entry) : IActionJournalStore
    {
        internal ActionJournalEntry Entry { get; set; } = entry;
        internal Func<string, ActionJournalEntry>? OnUpdate { get; set; }

        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry value, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActionJournalEntry?>(id == Entry.Id ? Entry : null);
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(string ownerId, int maximumActions, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
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
        internal int Dispatches { get; private set; }
        public void ReleaseTurn(TurnId turnId) { }
        public void ReleaseApproval(ActionId actionId) => ReleasedApprovals.Add(actionId);
        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken)
        {
            Dispatches++;
            return ValueTask.FromResult(new ToolDispatchResult("Succeeded", null, null));
        }
    }

    private sealed class MemoryStore : IConfirmedMemoryStore
    {
        public ValueTask<ConversationMessage?> ReadSourceAsync(string ownerId, TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
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
