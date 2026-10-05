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

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            machine.SetTerminalOutcomeAsync(
                turnId,
                TurnStatus.Failed,
                new TurnFailed(turnId, Now, "engine_failure"),
                cancelled.Token));
        Assert.Equal(TurnStatus.Received, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);

        await machine.EnsureRunningAsync(turnId, CancellationToken.None);
        await machine.EnsureRunningAsync(turnId, CancellationToken.None);
        await machine.SetTerminalOutcomeAsync(
            turnId,
            TurnStatus.Completed,
            new TurnCompleted(turnId, Now),
            CancellationToken.None);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var terminalEvents = await store.ReadTurnEventsAfterAsync(turnId, 0, 10, CancellationToken.None);
        Assert.Equal("TurnCompleted", Assert.Single(terminalEvents).EventType);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => machine.EnsureRunningAsync(turnId, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(
            () => machine.SetTerminalOutcomeAsync(
                turnId,
                TurnStatus.Failed,
                new TurnFailed(turnId, Now, "engine_failure"),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => machine.SetTerminalOutcomeAsync(
                turnId,
                TurnStatus.Running,
                new TurnCompleted(turnId, Now),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.EnsureRunningAsync(TurnId.New(), CancellationToken.None));
        var missingTurnId = TurnId.New();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.SetTerminalOutcomeAsync(
                missingTurnId,
                TurnStatus.Failed,
                new TurnFailed(missingTurnId, Now, "engine_failure"),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => machine.SetTerminalOutcomeAsync(
                turnId,
                TurnStatus.Failed,
                new TurnFailed(TurnId.New(), Now, "engine_failure"),
                CancellationToken.None));

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
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await store.UpdateTurnStatusAndAppendEventAsync(
                turnId,
                TurnStatus.Failed,
                savedTurn!.Version,
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
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.RuntimeCleanupFailed),
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, CopilotTurnSignal.Failed)
        };

        Assert.Equal(
            [
                TurnStatus.Completed,
                TurnStatus.Cancelled,
                TurnStatus.Failed,
                TurnStatus.Interrupted,
                TurnStatus.Interrupted,
                TurnStatus.Failed
            ],
            outcomes.Select(item => item.Status));
        Assert.Equal(
            [
                typeof(TurnCompleted),
                typeof(TurnCancelled),
                typeof(TurnFailed),
                typeof(TurnInterrupted),
                typeof(TurnInterrupted),
                typeof(TurnFailed)
            ],
            outcomes.Select(item => item.Event.GetType()));
        Assert.Equal("tool_budget_exceeded", Assert.IsType<TurnFailed>(outcomes[2].Event).ReasonCode);
        Assert.Equal("engine_deadline_exceeded", Assert.IsType<TurnInterrupted>(outcomes[3].Event).ReasonCode);
        Assert.Equal("runtime_cleanup_failed", Assert.IsType<TurnInterrupted>(outcomes[4].Event).ReasonCode);
        Assert.Equal("engine_failure", Assert.IsType<TurnFailed>(outcomes[5].Event).ReasonCode);
        Assert.All(outcomes, item => Assert.Equal(turnId, item.Event.TurnId));
        Assert.All(outcomes, item => Assert.Equal(Now, item.Event.OccurredAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CopilotTurnStateMachine.CreateTerminalOutcome(turnId, clock, (CopilotTurnSignal)int.MaxValue));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RuntimeCleanupAttemptsEveryResourceAndReportsCleanupFailure()
    {
        var attempts = new List<string>();

        var exception = await Assert.ThrowsAsync<RuntimeCleanupException>(() =>
            CopilotAgentEngine.CleanupRuntimeAsync(
                () =>
                {
                    attempts.Add("subscription");
                    throw new InvalidOperationException("subscription cleanup");
                },
                () =>
                {
                    attempts.Add("session");
                    return Task.FromException(new InvalidOperationException("session cleanup"));
                },
                () =>
                {
                    attempts.Add("client");
                    return Task.FromException(new InvalidOperationException("client cleanup"));
                },
                () =>
                {
                    attempts.Add("runtime-directory");
                    throw new IOException("directory cleanup");
                }));

        Assert.Equal(["subscription", "session", "client", "runtime-directory"], attempts);
        var failures = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(4, failures.Flatten().InnerExceptions.Count);
        Assert.Equal(CopilotTurnSignal.RuntimeCleanupFailed, CopilotAgentEngine.GetFailureSignal(exception));
        Assert.Equal(
            CopilotTurnSignal.Failed,
            CopilotAgentEngine.GetFailureSignal(new InvalidOperationException("provider failure")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RuntimeCleanupDeletesTurnDirectoryAfterStoppingRuntime()
    {
        using var directory = IsolatedDatabaseFile.Create();
        var turnDirectory = Path.Combine(Path.GetDirectoryName(directory.Path)!, "turn-runtime");
        Directory.CreateDirectory(Path.Combine(turnDirectory, "runtime"));
        var attempts = new List<string>();

        await CopilotAgentEngine.CleanupRuntimeAsync(
            () => attempts.Add("subscription"),
            () =>
            {
                attempts.Add("session");
                return Task.CompletedTask;
            },
            () =>
            {
                attempts.Add("client");
                return Task.CompletedTask;
            },
            () =>
            {
                attempts.Add("directory");
                Directory.Delete(turnDirectory, recursive: true);
            });

        Assert.Equal(["subscription", "session", "client", "directory"], attempts);
        Assert.False(Directory.Exists(turnDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ActiveTurnCancellationAndBudgetTransitionsAreHostControlled()
    {
        var active = new CopilotActiveTurn(2);
        Assert.False(active.CancelledByHost);
        Assert.Null(active.FailureCode);
        Assert.Equal(2, active.MaximumToolCalls);
        Assert.Equal(1, active.IncrementToolCalls());
        Assert.Equal(2, active.IncrementToolCalls());
        await active.AbortAsync();

        Assert.True(active.CancelByHost());
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.True(active.CancelledByHost);
        Assert.True(active.MarkTerminal());
        Assert.False(active.CancelByHost());

        var callerCancelled = new CopilotActiveTurn(0);
        callerCancelled.CancelByCaller();
        Assert.True(callerCancelled.CancellationToken.IsCancellationRequested);
        Assert.True(callerCancelled.MarkTerminal());

        var budgetExceeded = new CopilotActiveTurn(0);
        budgetExceeded.FailForToolBudget();
        budgetExceeded.FailForToolBudget();
        Assert.Equal("tool_budget_exceeded", budgetExceeded.FailureCode);
        Assert.True(budgetExceeded.CancellationToken.IsCancellationRequested);
        Assert.False(budgetExceeded.MarkTerminal());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ActiveTurnSelectsTerminalOutcomeByBudgetCancellationDeadlinePrecedence()
    {
        Assert.Equal(
            CopilotTurnSignal.Completed,
            CopilotActiveTurn.SelectCompletedSignal(false, false, false));
        Assert.Equal(
            CopilotTurnSignal.DeadlineExceeded,
            CopilotActiveTurn.SelectCompletedSignal(false, false, true));
        Assert.Equal(
            CopilotTurnSignal.Cancelled,
            CopilotActiveTurn.SelectCompletedSignal(false, true, false));
        Assert.Equal(
            CopilotTurnSignal.Cancelled,
            CopilotActiveTurn.SelectCompletedSignal(false, true, true));
        Assert.Equal(
            CopilotTurnSignal.ToolBudgetExceeded,
            CopilotActiveTurn.SelectCompletedSignal(true, false, false));
        Assert.Equal(
            CopilotTurnSignal.ToolBudgetExceeded,
            CopilotActiveTurn.SelectCompletedSignal(true, true, true));
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
