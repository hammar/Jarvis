using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class CopilotTurnStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TurnStateMachineAppliesRunningAndTerminalTransitionsWithCompareAndSwap()
    {
        using var databaseFile = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(databaseFile.Path);
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, new TestClock());
        var machine = new CopilotTurnStateMachine(store, store, new TestClock());
        var turnId = TurnId.New();
        await store.CreateTurnAsync(turnId, ConversationId.New(), TurnStatus.Received, Now, CancellationToken.None);

        await machine.EnsureRunningAsync(turnId, CancellationToken.None);
        await machine.EnsureRunningAsync(turnId, CancellationToken.None);
        await machine.SetTerminalOutcomeAsync(turnId, TurnStatus.Completed, new TurnCompleted(turnId, Now));

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var terminalEvents = await store.ReadTurnEventsAfterAsync(turnId, 0, 10, CancellationToken.None);
        Assert.Equal("TurnCompleted", Assert.Single(terminalEvents).EventType);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => machine.EnsureRunningAsync(turnId, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => machine.SetTerminalOutcomeAsync(turnId, TurnStatus.Failed, new TurnFailed(turnId, Now, "engine_failure")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => machine.SetTerminalOutcomeAsync(turnId, TurnStatus.Running, new TurnCompleted(turnId, Now)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.EnsureRunningAsync(TurnId.New(), CancellationToken.None));
        var missingTurnId = TurnId.New();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.SetTerminalOutcomeAsync(
                missingTurnId,
                TurnStatus.Failed,
                new TurnFailed(missingTurnId, Now, "engine_failure")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => machine.SetTerminalOutcomeAsync(turnId, TurnStatus.Failed, new TurnFailed(TurnId.New(), Now, "engine_failure")));

        var savedTurn = await store.GetTurnAsync(turnId, CancellationToken.None);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.UpdateTurnStatusAndAppendEventAsync(
                turnId,
                TurnStatus.Failed,
                savedTurn!.Version - 1,
                Now,
                nameof(TurnFailed),
                """{"reasonCode":"engine_failure"}""",
                Now,
                CancellationToken.None));
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        Assert.Single(await store.ReadTurnEventsAfterAsync(turnId, 0, 10, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TurnStateMachineMapsEveryEngineSignalToOneTypedTerminalOutcome()
    {
        var clock = new TestClock();
        var turnId = TurnId.New();
        var outcomes = new[]
        {
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.Completed),
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.Cancelled),
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.ToolBudgetExceeded),
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.DeadlineExceeded),
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.Failed)
        };

        Assert.Equal(
            [TurnStatus.Completed, TurnStatus.Cancelled, TurnStatus.Failed, TurnStatus.Interrupted, TurnStatus.Failed],
            outcomes.Select(item => item.Status));
        Assert.Equal(
            [typeof(TurnCompleted), typeof(TurnCancelled), typeof(TurnFailed), typeof(TurnInterrupted), typeof(TurnFailed)],
            outcomes.Select(item => item.Event.GetType()));
        Assert.Equal("tool_budget_exceeded", Assert.IsType<TurnFailed>(outcomes[2].Event).ReasonCode);
        Assert.Equal("engine_deadline_exceeded", Assert.IsType<TurnInterrupted>(outcomes[3].Event).ReasonCode);
        Assert.Equal("engine_failure", Assert.IsType<TurnFailed>(outcomes[4].Event).ReasonCode);
        Assert.All(outcomes, item => Assert.Equal(turnId, item.Event.TurnId));
        Assert.All(outcomes, item => Assert.Equal(Now, item.Event.OccurredAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, (CopilotTurnSignal)int.MaxValue));
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
