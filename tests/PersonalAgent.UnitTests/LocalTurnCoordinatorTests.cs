using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Routing;
using PersonalAgent.Application.TurnCoordination;
using PersonalAgent.Domain;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class LocalTurnCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcceptedRequestIsPersistedExecutedAndDeduplicated()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var request = Request();
        var accepted = await scope.Coordinator.SubmitAsync(request, CancellationToken.None);
        await engine.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retry = await scope.Coordinator.SubmitAsync(request, CancellationToken.None);

        Assert.False(accepted.IsDuplicate);
        Assert.True(retry.IsDuplicate);
        Assert.Equal(accepted.Turn.Id, retry.Turn.Id);
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(
            [nameof(TurnReceived), nameof(TurnRouting), nameof(TurnCompleted)],
            (await scope.Coordinator.ReadEventsAfterAsync(accepted.Turn.Id, 0, 20, CancellationToken.None))
                .Select(item => item.EventType));
        Assert.Single(store.Messages, message => message.Role == "assistant");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AlreadyTerminalTurnIsNotRoutedOrExecuted()
    {
        var store = new InMemoryConversationStore { CompleteNextTurnOnRead = true };
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalEventAsync(store, submitted.Turn.Id);
        await scope.Coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingPostEngineReadPreservesAlreadyPersistedCompletion()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, hideOutcomeRead: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SubmissionRejectsInvalidInputAndUnavailableCoordinator()
    {
        var store = new InMemoryConversationStore();
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));
        await scope.Coordinator.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None));

        await scope.Coordinator.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Coordinator.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await scope.Coordinator.SubmitAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(conversationId: new ConversationId(Guid.Empty)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: " "), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: new string('x', 129)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(text: " "), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(text: new string('x', 8_001)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(deadline: TimeSpan.FromSeconds(301)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(taskKind: (RoutingTaskKind)999), CancellationToken.None));

        await scope.Coordinator.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CoordinatorRejectsNullDependenciesAndInvalidLimits()
    {
        var store = new InMemoryConversationStore();
        var router = new ControlledRouter(RouteDecision.Local("local", "policy"));
        var context = new ControlledContextBuilder();
        var engine = new ControlledEngine(store);
        var clock = new FixedClock();
        var options = new LocalTurnCoordinatorOptions(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300));

        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(null!, store, router, context, engine, clock, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, null!, router, context, engine, clock, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, store, null!, context, engine, clock, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, store, router, null!, engine, clock, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, store, router, context, null!, clock, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, store, router, context, engine, null!, options, "instructions"));
        Assert.Throws<ArgumentNullException>(() => new LocalTurnCoordinator(store, store, router, context, engine, clock, null!, "instructions"));
        Assert.Throws<ArgumentException>(() => new LocalTurnCoordinator(store, store, router, context, engine, clock, options, " "));

        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalTurnCoordinatorOptions(TimeSpan.Zero, TimeSpan.FromSeconds(120)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalTurnCoordinatorOptions(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(119)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalTurnCoordinatorOptions(TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(uint.MaxValue)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { MaximumActiveTurns = 3 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { MaximumQueuedTurns = 19 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { MaximumRequestIdCharacters = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { MaximumRequestIdCharacters = 257 }).Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConflictingRequestIdentityIsRejectedAndRouteRejectionIsTerminal()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store);
        var router = new ControlledRouter(RouteDecision.Unsupported("unsupported", "policy", "Not available."));
        await using var scope = CreateCoordinator(store, engine, router);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var request = Request();
        var accepted = await scope.Coordinator.SubmitAsync(request, CancellationToken.None);
        await Assert.ThrowsAsync<TurnRequestConflictException>(async () =>
            await scope.Coordinator.SubmitAsync(request with { Text = "different" }, CancellationToken.None));
        await WaitForTerminalAsync(store, accepted.Turn.Id);

        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(
            await store.ReadTurnEventsAfterAsync(accepted.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnFailed));
        Assert.Contains(store.Messages, message => message.Role == "assistant" && message.Content == "Not available.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ClarifyingRoutePersistsPromptWithoutInvokingEngine()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store);
        var router = new ControlledRouter(RouteDecision.Clarify(
            "task_category_ambiguous",
            "policy",
            "Please clarify the supported local task."));
        await using var scope = CreateCoordinator(store, engine, router);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);
        var events = await scope.Coordinator.ReadEventsAfterAsync(submitted.Turn.Id, 0, 20, CancellationToken.None);

        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(events, item => item.EventType == nameof(TurnClarificationRequired));
        Assert.Contains(store.Messages, message =>
            message.Role == "assistant" && message.Content == "Please clarify the supported local task.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AtomicSubmissionRaceReturnsDurableDuplicateWithoutQueueingItAgain()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var request = Request(requestId: "racing-retry");
        var first = await scope.Coordinator.SubmitAsync(request, CancellationToken.None);
        store.HideExistingRequests = true;

        var retry = await scope.Coordinator.SubmitAsync(request, CancellationToken.None);

        Assert.True(retry.IsDuplicate);
        Assert.Equal(first.Turn.Id, retry.Turn.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedPersistenceReleasesReservedQueueCapacity()
    {
        var store = new InMemoryConversationStore { FailNextSubmission = true };
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));
        await scope.Coordinator.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: "fails-once"), CancellationToken.None));
        var accepted = await scope.Coordinator.SubmitAsync(Request(requestId: "after-failure"), CancellationToken.None);

        Assert.False(accepted.IsDuplicate);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentStartupReservesRecoveryAndMaintainsFourWorkers()
    {
        var store = new InMemoryConversationStore { BlockNextRecovery = true };
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        var starting = scope.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await store.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(scope.Coordinator.IsReady);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Coordinator.StartAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None));
            Assert.Equal(1, store.RecoveryReads);
        }
        finally
        {
            store.ContinueRecovery.TrySetResult();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        var requests = new List<TurnId>();
        for (var index = 0; index < 4; index++)
        {
            var accepted = await scope.Coordinator.SubmitAsync(Request(requestId: $"startup-{index}"), CancellationToken.None);
            requests.Add(accepted.Turn.Id);
            await engine.WaitForTurnStartedAsync(accepted.Turn.Id);
        }
        var pending = await scope.Coordinator.SubmitAsync(Request(requestId: "fifth"), CancellationToken.None);
        await scope.Coordinator.CancelAsync(pending.Turn.Id, CancellationToken.None);
        Assert.Equal(4, engine.ExecutionCount);
        await scope.Coordinator.StopAsync(CancellationToken.None);
        foreach (var id in requests)
        {
            Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(id, CancellationToken.None))!.Status);
        }
        Assert.Equal(1, store.RecoveryReads);
        var workers = (Task[])typeof(LocalTurnCoordinator)
            .GetField("workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Coordinator)!;
        Assert.Equal(4, workers.Length);
        Assert.All(workers, worker => Assert.True(worker.IsCompletedSuccessfully));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    public async Task FailedOrCancelledRecoveryReleasesStartupReservation(bool cancel)
    {
        var store = new InMemoryConversationStore { BlockNextRecovery = true, FailNextRecovery = !cancel };
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));
        using var caller = new CancellationTokenSource();
        var starting = scope.Coordinator.StartAsync(caller.Token);
        await store.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancel)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        }
        else
        {
            store.ContinueRecovery.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => starting);
        }
        Assert.False(scope.Coordinator.IsReady);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        Assert.True(scope.Coordinator.IsReady);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopDuringRecoveryPreventsWorkerLaunchAndAdmission()
    {
        var store = new InMemoryConversationStore { BlockNextRecovery = true };
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));
        var starting = scope.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await store.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await scope.Coordinator.StopAsync(CancellationToken.None);
        }
        finally
        {
            store.ContinueRecovery.TrySetResult();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => starting);
        Assert.False(scope.Coordinator.IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Coordinator.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestartRecoveryInterruptsTurnsAndDoesNotReplay()
    {
        var store = new InMemoryConversationStore();
        var recovered = await store.CreateTurnAsync(
            TurnId.New(),
            ConversationId.New(),
            TurnStatus.Running,
            Now,
            CancellationToken.None);
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);

        await scope.Coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(recovered.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Contains(
            await scope.Coordinator.ReadEventsAfterAsync(recovered.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartupFailsWhenRecoveryCannotPersistInterruption()
    {
        var store = new InMemoryConversationStore { LeaveNextTerminalOutcomeUnchanged = true };
        var turn = await store.CreateTurnAsync(
            TurnId.New(),
            ConversationId.New(),
            TurnStatus.Running,
            Now,
            CancellationToken.None);
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Coordinator.StartAsync(CancellationToken.None));

        Assert.Equal(TurnStatus.Running, (await store.GetTurnAsync(turn.Id, CancellationToken.None))!.Status);
        Assert.False(scope.Coordinator.IsReady);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueuedCancellationPersistsCancelledOutcome()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var activeRequest = Request(requestId: "active");
        var active = await scope.Coordinator.SubmitAsync(activeRequest, CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = await scope.Coordinator.SubmitAsync(
            Request(activeRequest.ConversationId, "queued"),
            CancellationToken.None);
        await scope.Coordinator.CancelAsync(queued.Turn.Id, CancellationToken.None);
        await WaitForTerminalAsync(store, queued.Turn.Id);

        Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(queued.Turn.Id, CancellationToken.None))!.Status);
        await scope.Coordinator.CancelAsync(active.Turn.Id, CancellationToken.None);
        await WaitForTerminalAsync(store, active.Turn.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentCompletionWinsOverCancellationCompareAndSwap()
    {
        var store = new InMemoryConversationStore { CompleteWhenCancellationRaces = true };
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scope.Coordinator.CancelAsync(submitted.Turn.Id, CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationRetriesTerminalCompareAndSwapWhenTurnRemainsNonterminal()
    {
        var store = new InMemoryConversationStore { ConflictNextCancellationOutcome = true };
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scope.Coordinator.CancelAsync(submitted.Turn.Id, CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationSurfacesRepeatedTerminalCompareAndSwapConflicts()
    {
        var store = new InMemoryConversationStore { CancellationOutcomeConflictsRemaining = 2 };
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        for (var index = 0; index < 4; index++)
        {
            await scope.Coordinator.SubmitAsync(
                Request(requestId: $"cancel-conflict-active-{index}"),
                CancellationToken.None);
        }

        await engine.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = await scope.Coordinator.SubmitAsync(
            Request(requestId: "cancel-conflict-queued"),
            CancellationToken.None);

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            scope.Coordinator.CancelAsync(queued.Turn.Id, CancellationToken.None).AsTask());
        Assert.Equal(TurnStatus.Received, (await store.GetTurnAsync(queued.Turn.Id, CancellationToken.None))!.Status);

        Assert.False(scope.Coordinator.IsReady);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            scope.Coordinator.StopAsync(CancellationToken.None));
        Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(queued.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SameConversationWaitersDoNotStarveOtherConversations()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversationId = ConversationId.New();
        var first = await scope.Coordinator.SubmitAsync(Request(conversationId, "gate-first"), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sameConversationTurns = new List<TurnId>();
        for (var index = 0; index < 3; index++)
        {
            var waiting = await scope.Coordinator.SubmitAsync(
                Request(conversationId, $"same-conversation-{index}"),
                CancellationToken.None);
            sameConversationTurns.Add(waiting.Turn.Id);
        }

        var otherConversation = await scope.Coordinator.SubmitAsync(
            Request(ConversationId.New(), "other-conversation"),
            CancellationToken.None);
        var secondStartedConversation = await engine.SecondConversationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(otherConversation.Turn.ConversationId, secondStartedConversation);
        engine.ReleaseTurn(first.Turn.Id);
        await engine.WaitForTurnStartedAsync(sameConversationTurns[0]);
        await scope.Coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(first.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(otherConversation.Turn.Id, CancellationToken.None))!.Status);
        foreach (var turnId in sameConversationTurns)
        {
            Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeferredTurnsRunInSubmissionOrderUntilConversationQueueIsEmpty()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversationId = ConversationId.New();
        var first = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deferred-first"),
            CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deferred-second"),
            CancellationToken.None);
        var third = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deferred-third"),
            CancellationToken.None);

        engine.ReleaseTurn(first.Turn.Id);
        await engine.WaitForTurnStartedAsync(second.Turn.Id);
        engine.ReleaseTurn(second.Turn.Id);
        await engine.WaitForTurnStartedAsync(third.Turn.Id);
        engine.ReleaseTurn(third.Turn.Id);
        await WaitForTerminalAsync(store, third.Turn.Id);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(first.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(second.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(third.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(3, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeferredTurnPrecedesNewerChannelTurnWhenAllOtherWorkersAreBusy()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversation = ConversationId.New();
        var first = await scope.Coordinator.SubmitAsync(Request(conversation, "first"), CancellationToken.None);
        await engine.WaitForTurnStartedAsync(first.Turn.Id);
        var second = await scope.Coordinator.SubmitAsync(Request(conversation, "second"), CancellationToken.None);
        for (var index = 0; index < 3; index++)
        {
            var other = await scope.Coordinator.SubmitAsync(Request(requestId: $"other-{index}"), CancellationToken.None);
            await engine.WaitForTurnStartedAsync(other.Turn.Id);
        }

        var third = await scope.Coordinator.SubmitAsync(Request(conversation, "third"), CancellationToken.None);
        engine.ReleaseTurn(first.Turn.Id);
        await engine.WaitForTurnStartedAsync(second.Turn.Id);
        Assert.Equal(5, engine.ExecutionCount);
        engine.ReleaseTurn(second.Turn.Id);
        await engine.WaitForTurnStartedAsync(third.Turn.Id);
        Assert.Equal(6, engine.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    public async Task TerminalPendingTurnsImmediatelyReleaseCapacity(bool channelResident, bool expire)
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversation = ConversationId.New();
        var first = await scope.Coordinator.SubmitAsync(Request(conversation, "active"), CancellationToken.None);
        await engine.WaitForTurnStartedAsync(first.Turn.Id);
        if (channelResident)
        {
            for (var index = 0; index < 3; index++)
            {
                var other = await scope.Coordinator.SubmitAsync(Request(requestId: $"other-{index}"), CancellationToken.None);
                await engine.WaitForTurnStartedAsync(other.Turn.Id);
            }
        }

        var pending = new List<TurnId>();
        for (var index = 0; index < 20; index++)
        {
            if (!channelResident && index == 19)
            {
                var witness = await scope.Coordinator.SubmitAsync(
                    Request(requestId: "deferred-witness"), CancellationToken.None);
                await engine.WaitForTurnStartedAsync(witness.Turn.Id);
                // Unrelated execution proves blocked work does not consume execution workers.
            }

            var submission = await scope.Coordinator.SubmitAsync(
                Request(conversation, $"pending-{index}", deadline: expire ? TimeSpan.FromMilliseconds(200) : null),
                CancellationToken.None);
            pending.Add(submission.Turn.Id);
        }

        if (!channelResident)
        {
            await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
                await scope.Coordinator.SubmitAsync(Request(requestId: "overflow"), CancellationToken.None));
        }

        foreach (var turn in pending)
        {
            if (expire)
            {
                await WaitForTerminalAsync(store, turn);
            }
            else
            {
                await scope.Coordinator.CancelAsync(turn, CancellationToken.None);
            }
        }

        var replacement = await scope.Coordinator.SubmitAsync(Request(requestId: "replacement"), CancellationToken.None);
        Assert.False(replacement.IsDuplicate);
        if (!channelResident)
        {
            await engine.WaitForTurnStartedAsync(replacement.Turn.Id);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepeatedPendingCancellationReclaimsRequestResourcesWhileAllWorkersRemainOccupied()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        for (var index = 0; index < 4; index++)
        {
            var turn = await scope.Coordinator.SubmitAsync(Request(requestId: $"active-{index}"), CancellationToken.None);
            await engine.WaitForTurnStartedAsync(turn.Turn.Id);
        }

        var retained = new List<WeakReference>();
        for (var index = 0; index < 100; index++)
        {
            retained.Add(await SubmitCancelAndCaptureAsync(scope.Coordinator, index));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        while (retained.Any(reference => reference.IsAlive));

        Assert.Equal(4, engine.ExecutionCount);
        Assert.True(scope.Coordinator.IsReady);
        var pending = new List<TurnId>();
        for (var index = 0; index < 20; index++)
        {
            pending.Add((await scope.Coordinator.SubmitAsync(
                Request(requestId: $"still-pending-{index}"), CancellationToken.None)).Turn.Id);
        }

        await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: "overflow"), CancellationToken.None));
        foreach (var turn in pending)
        {
            await scope.Coordinator.CancelAsync(turn, CancellationToken.None);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SubmitCancelAndCaptureAsync(ILocalTurnCoordinator coordinator, int index)
    {
        var turn = await coordinator.SubmitAsync(
            Request(requestId: $"cancel-{index}", text: new string('x', 1000)), CancellationToken.None);
        var pending = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField("queued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        var item = pending[turn.Turn.Id]!;
        var reference = new WeakReference(item);
        var monitor = (TaskCompletionSource)item.GetType().GetProperty("DeadlineMonitor")!.GetValue(item)!;
        await coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None);
        await monitor.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return reference;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task ActiveEngineOnlyReceivesWinningCauseWhileCancellationPublicationIsPaused(bool deadlineFirst)
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, persistCancellationOutcome: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var turn = await scope.Coordinator.SubmitAsync(
            Request(deadline: TimeSpan.FromMilliseconds(300)), CancellationToken.None);
        await engine.WaitForTurnStartedAsync(turn.Turn.Id);
        using var release = new ManualResetEventSlim();
        var publicationPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // LIFO callback order pauses delivery before the engine's winning-signal observer.
        using var pause = (deadlineFirst ? engine.EngineDeadlineToken : engine.EngineOwnerToken).Register(() =>
        {
            publicationPaused.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Cancellation publication was not released.");
            }
        });
        Task? cancelling = null;
        try
        {
            if (!deadlineFirst)
            {
                cancelling = Task.Run(async () =>
                    await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None));
            }

            await publicationPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (deadlineFirst)
            {
                await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None);
            }
            else
            {
                var work = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
                    .GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Coordinator)!;
                var item = work[turn.Turn.Id]!.GetType().GetProperty("Item")!.GetValue(work[turn.Turn.Id])!;
                // Exercise the losing deadline signal while the owner callback holds publication.
                item.GetType().GetMethod("ExpireDeadline")!.Invoke(item, null);
            }

            Assert.False(engine.CancellationOutcome.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            engine.ContinueCancellation.TrySetResult();
        }

        if (cancelling is not null)
        {
            await cancelling.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await WaitForTerminalAsync(store, turn.Turn.Id);
        Assert.Equal(deadlineFirst ? TurnStatus.Interrupted : TurnStatus.Cancelled,
            (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(deadlineFirst ? 0 : 1, engine.CancelCallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueuedTurnThatExceedsItsAcceptedDeadlineIsInterruptedWithoutExecution()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversationId = ConversationId.New();
        var active = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deadline-active"),
            CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deadline-queued", deadline: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        await WaitForTerminalAsync(store, queued.Turn.Id);
        Assert.Equal(TurnStatus.Running, (await store.GetTurnAsync(active.Turn.Id, CancellationToken.None))!.Status);
        engine.ReleaseTurn(active.Turn.Id);
        await WaitForTerminalAsync(store, active.Turn.Id);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(queued.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(
            await scope.Coordinator.ReadEventsAfterAsync(queued.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted) &&
                item.PayloadJson.Contains("interactive_deadline_exceeded", StringComparison.Ordinal));
        Assert.Equal(1, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExpiredChannelQueuedTurnIsPersistedBeforeItCanEnterInference()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var active = new List<SubmittedConversationTurn>();
        for (var index = 0; index < 4; index++)
        {
            active.Add(await scope.Coordinator.SubmitAsync(
                Request(requestId: $"deadline-channel-active-{index}"),
                CancellationToken.None));
        }

        await engine.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var expired = await scope.Coordinator.SubmitAsync(
            Request(requestId: "deadline-channel-queued", deadline: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        await WaitForTerminalAsync(store, expired.Turn.Id);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(expired.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(4, engine.ExecutionCount);

        engine.ReleaseTurn(active[0].Turn.Id);
        await WaitForTerminalAsync(store, active[0].Turn.Id);
        await scope.Coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(4, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeadlinePersistenceFailureFailsReadinessAndShutdown()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversationId = ConversationId.New();
        var active = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deadline-persistence-active"),
            CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        store.FailNextTerminalOutcome = true;
        var expired = await scope.Coordinator.SubmitAsync(
            Request(conversationId, "deadline-persistence-queued", deadline: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        using var waitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (scope.Coordinator.IsReady)
        {
            waitDeadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Equal(TurnStatus.Received, (await store.GetTurnAsync(expired.Turn.Id, CancellationToken.None))!.Status);
        var messageCount = store.Messages.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: "after-monitor-failure"), CancellationToken.None));
        Assert.Equal(messageCount, store.Messages.Count);
        await Assert.ThrowsAsync<IOException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(active.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TerminalPersistenceTimeoutCancelsWorkerAndRejectsFurtherSubmissions()
    {
        var store = new InMemoryConversationStore { TimeoutNextTerminalOutcome = true };
        var engine = new ControlledEngine(store, fail: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (scope.Coordinator.IsReady)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var messageCount = store.Messages.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: "after-worker-timeout"), CancellationToken.None));
        Assert.Equal(messageCount, store.Messages.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task DelayedContextPreservesFirstDeadlineOrOwnerCancellation(bool deadlineFirst)
    {
        var store = new InMemoryConversationStore();
        var context = new ControlledContextBuilder(block: true, ignoreCancellation: true);
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine, contextBuilder: context);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var turn = await scope.Coordinator.SubmitAsync(
            Request(deadline: TimeSpan.FromMilliseconds(300)), CancellationToken.None);
        await context.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (deadlineFirst)
        {
            await context.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None);
        }
        else
        {
            await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None);
            await context.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var deadlineWitness = await scope.Coordinator.SubmitAsync(
                Request(turn.Turn.ConversationId, "deadline-witness", deadline: TimeSpan.FromMilliseconds(300)),
                CancellationToken.None);
            await WaitForTerminalAsync(store, deadlineWitness.Turn.Id);
        }

        context.Continue.TrySetResult();
        await WaitForTerminalAsync(store, turn.Turn.Id);
        Assert.Equal(deadlineFirst ? TurnStatus.Interrupted : TurnStatus.Cancelled,
            (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InteractiveDeadlineCancelsBlockedContextConstruction()
    {
        var store = new InMemoryConversationStore();
        var contextBuilder = new ControlledContextBuilder(block: true);
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine, contextBuilder: contextBuilder);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(
            Request(deadline: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);
        await contextBuilder.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Contains(
            await scope.Coordinator.ReadEventsAfterAsync(submitted.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted) &&
                item.PayloadJson.Contains("interactive_deadline_exceeded", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InteractiveDeadlineIsCheckedAfterUncooperativeContextBuilderReturns()
    {
        var store = new InMemoryConversationStore();
        var contextBuilder = new ControlledContextBuilder(block: true, ignoreCancellation: true);
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine, contextBuilder: contextBuilder);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(
            Request(deadline: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);
        await contextBuilder.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await contextBuilder.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        contextBuilder.Continue.TrySetResult();

        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Contains(
            await scope.Coordinator.ReadEventsAfterAsync(submitted.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted) &&
                item.PayloadJson.Contains("interactive_deadline_exceeded", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InteractiveDeadlineInterruptsActiveEngineTurn()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true, honorDeadline: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(
            Request(deadline: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(
            await scope.Coordinator.ReadEventsAfterAsync(submitted.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted) &&
                item.PayloadJson.Contains("interactive_deadline_exceeded", StringComparison.Ordinal));
        Assert.Equal(1, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SameConversationDeferredTurnsRemainInsideQueueLimit()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var conversationId = ConversationId.New();
        await scope.Coordinator.SubmitAsync(Request(conversationId, "queue-active"), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var accepted = new List<TurnId>();
        for (var index = 0; index < 20; index++)
        {
            var submitted = await scope.Coordinator.SubmitAsync(
                Request(conversationId, $"same-conversation-queued-{index}"),
                CancellationToken.None);
            accepted.Add(submitted.Turn.Id);
        }

        await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
            await scope.Coordinator.SubmitAsync(
                Request(conversationId, "same-conversation-overflow"),
                CancellationToken.None));
        await scope.Coordinator.StopAsync(CancellationToken.None);

        foreach (var turnId in accepted)
        {
            Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentTerminalOutcomeWinsAfterCancellationRetryConflict()
    {
        var store = new InMemoryConversationStore
        {
            ConflictNextCancellationOutcome = true,
            CompleteCancellationOnRetryConflict = true
        };
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scope.Coordinator.CancelAsync(submitted.Turn.Id, CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShutdownInterruptsAcceptedQueuedTurnsAndIsIdempotent()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var first = await scope.Coordinator.SubmitAsync(Request(requestId: "shutdown-active"), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = await scope.Coordinator.SubmitAsync(
            Request(ConversationId.New(), "shutdown-queued"),
            CancellationToken.None);

        await scope.Coordinator.StopAsync(CancellationToken.None);
        await scope.Coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(first.Turn.Id, CancellationToken.None))!.Status);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(queued.Turn.Id, CancellationToken.None))!.Status);
        await scope.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShutdownDuringContextBuildThatIgnoresCancellationInterruptsBeforeEngineStarts()
    {
        var store = new InMemoryConversationStore();
        var contextBuilder = new ControlledContextBuilder(block: true, ignoreCancellation: true);
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine, contextBuilder: contextBuilder);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await contextBuilder.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = scope.Coordinator.StopAsync(CancellationToken.None);
        contextBuilder.Continue.TrySetResult();
        await stopping;

        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShutdownDuringAtomicSubmissionPersistsInterruption()
    {
        var store = new InMemoryConversationStore { BlockNextSubmission = true };
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submission = scope.Coordinator.SubmitAsync(Request(), CancellationToken.None).AsTask();
        await store.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scope.Coordinator.StopAsync(CancellationToken.None);
        store.ContinueSubmission.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await submission);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(store.LastSubmittedTurnId, CancellationToken.None))!.Status);
        Assert.Equal(0, engine.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShutdownSurfacesEngineStopFailureAfterResolvingActiveTurn()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true, stopFails: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Coordinator.StopAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Coordinator.StopAsync(CancellationToken.None));
        Assert.Equal(1, engine.StopCallCount);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CallerCancellationBoundsShutdownWhileInterruptionPersistenceContinues()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Coordinator.StopAsync(cancelled.Token));

        await WaitForTerminalAsync(store, submitted.Turn.Id);
        await scope.Coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(1, engine.StopCallCount);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShutdownAggregatesEngineAndTerminalPersistenceFailures()
    {
        var store = new InMemoryConversationStore { FailNextTerminalOutcome = true };
        var engine = new ControlledEngine(store, block: true, stopFails: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            scope.Coordinator.StopAsync(CancellationToken.None));

        Assert.Equal(2, failure.InnerExceptions.Count);
        var repeated = await Assert.ThrowsAsync<AggregateException>(() =>
            scope.Coordinator.StopAsync(CancellationToken.None));
        Assert.Same(failure, repeated);
        Assert.Equal(1, engine.StopCallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SubmissionRacingWithShutdownIsDurablyInterruptedAndRejected()
    {
        var store = new InMemoryConversationStore { BlockNextSubmission = true };
        await using var scope = CreateCoordinator(store, new ControlledEngine(store));
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var submitting = scope.Coordinator.SubmitAsync(Request(), CancellationToken.None).AsTask();
        await store.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scope.Coordinator.StopAsync(CancellationToken.None);
        store.ContinueSubmission.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => submitting);

        Assert.Equal(
            TurnStatus.Interrupted,
            (await store.GetTurnAsync(store.LastSubmittedTurnId, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EngineFailuresPersistSafeTerminalOutcomes()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, fail: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var request = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalAsync(store, request.Turn.Id);

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(request.Turn.Id, CancellationToken.None))!.Status);
        var terminal = Assert.Single(
            await scope.Coordinator.ReadEventsAfterAsync(request.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted));
        Assert.DoesNotContain("private", terminal.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EngineEndingWithoutTerminalOutcomeInterruptsTurn()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, returnWithoutOutcome: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);

        var submitted = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        var turn = await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None);
        Assert.Equal(TurnStatus.Interrupted, turn!.Status);
        var terminal = Assert.Single(
            await scope.Coordinator.ReadEventsAfterAsync(submitted.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted));
        Assert.Contains("engine_ended_without_terminal_outcome", terminal.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueueLimitRejectsOverflowAndShutdownResolvesAcceptedWork()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        for (var index = 0; index < 4; index++)
        {
            await scope.Coordinator.SubmitAsync(
                Request(requestId: $"active-{index}"),
                CancellationToken.None);
        }

        await engine.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = new List<TurnId>();
        for (var index = 0; index < 20; index++)
        {
            var accepted = await scope.Coordinator.SubmitAsync(
                Request(requestId: $"queued-{index}"),
                CancellationToken.None);
            queued.Add(accepted.Turn.Id);
        }

        await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(requestId: "overflow"), CancellationToken.None));
        await scope.Coordinator.StopAsync(CancellationToken.None);

        foreach (var turnId in queued)
        {
            Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [Trait("Category", "Unit")]
    public async Task HeldPublicationDoesNotBlockReadinessAndShutdownOrCallerBounds(
        bool shutdown, bool cancelCaller, bool pending)
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        if (pending)
        {
            for (var index = 0; index < 4; index++)
            {
                var running = await scope.Coordinator.SubmitAsync(Request(requestId: $"held-active-{index}"), CancellationToken.None);
                await engine.WaitForTurnStartedAsync(running.Turn.Id);
            }
        }

        var turn = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        if (!pending)
        {
            await engine.WaitForTurnStartedAsync(turn.Turn.Id);
        }
        var active = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField(pending ? "queued" : "active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Coordinator)!;
        var work = active[turn.Turn.Id]!;
        var item = pending ? work : work.GetType().GetProperty("Item")!.GetValue(work)!;
        var source = (CancellationTokenSource)item.GetType()
            .GetProperty(shutdown ? "ShutdownCancellation" : "Cancellation")!.GetValue(item)!;
        using var release = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var barrier = source.Token.Register(() =>
        {
            paused.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Test publication was not released.");
            }
        });
        using var caller = new CancellationTokenSource();
        var operation = shutdown
            ? scope.Coordinator.StopAsync(caller.Token)
            : scope.Coordinator.CancelAsync(turn.Turn.Id, caller.Token).AsTask();
        Task? repeatedStop = null;
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(!shutdown,
                await Task.Run(() => scope.Coordinator.IsReady).WaitAsync(TimeSpan.FromSeconds(2)));
            if (shutdown)
            {
                repeatedStop = scope.Coordinator.StopAsync(CancellationToken.None);
                Assert.False(repeatedStop.IsCompleted);
                using var repeatedCaller = new CancellationTokenSource();
                var cancelledWait = scope.Coordinator.StopAsync(repeatedCaller.Token);
                repeatedCaller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
                Assert.False(repeatedStop.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await scope.Coordinator.SubmitAsync(Request(requestId: "after-stop"), CancellationToken.None));
            }

            if (cancelCaller)
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                // Exercise the production 15-second shutdown bound while the callback stays held.
                await Assert.ThrowsAsync<TimeoutException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(18)));
                await Assert.ThrowsAsync<TimeoutException>(async () =>
                    await repeatedStop!.WaitAsync(TimeSpan.FromSeconds(2)));
                await Assert.ThrowsAsync<TimeoutException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
            }

            Assert.True(source.Token.IsCancellationRequested);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
        }

        var publication = (Task)item.GetType().GetProperty("CancellationPublication")!.GetValue(item)!;
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTerminalAsync(store, turn.Turn.Id);
        Assert.Equal(shutdown ? TurnStatus.Interrupted : TurnStatus.Cancelled,
            (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        if (pending)
        {
            var monitor = (TaskCompletionSource)item.GetType().GetProperty("DeadlineMonitor")!.GetValue(item)!;
            await monitor.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, engine.ExecutionCount);
        }
        if (shutdown)
        {
            var workers = (Task[])typeof(LocalTurnCoordinator)
                .GetField("workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Coordinator)!;
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                await repeatedStop!.WaitAsync(TimeSpan.FromSeconds(5));
                await scope.Coordinator.StopAsync(CancellationToken.None);
                Assert.Equal(1, engine.StopCallCount);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    public async Task PublicationCallbackFailuresSurfaceWithoutStrandingPendingCleanup(bool failExecutionToo)
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        for (var index = 0; index < 4; index++)
        {
            var running = await scope.Coordinator.SubmitAsync(Request(requestId: $"failure-active-{index}"), CancellationToken.None);
            await engine.WaitForTurnStartedAsync(running.Turn.Id);
        }

        var turn = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        var queued = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField("queued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Coordinator)!;
        var item = queued[turn.Turn.Id]!;
        var owner = (CancellationTokenSource)item.GetType().GetProperty("Cancellation")!.GetValue(item)!;
        var execution = (CancellationTokenSource)item.GetType().GetProperty("ExecutionCancellation")!.GetValue(item)!;
        var monitor = (TaskCompletionSource)item.GetType().GetProperty("DeadlineMonitor")!.GetValue(item)!;
        using var ownerFailure = owner.Token.Register(() => throw new InvalidOperationException("engine callback failed"));
        using var executionFailure = execution.Token.Register(() =>
        {
            if (failExecutionToo)
            {
                throw new InvalidOperationException("execution callback failed");
            }
        });

        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
            await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None));
        Assert.Contains(error.Flatten().InnerExceptions, exception => exception.Message == "engine callback failed");
        Assert.Equal(failExecutionToo ? 2 : 1, error.Flatten().InnerExceptions.Count);
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await monitor.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(scope.Coordinator.IsReady);
        Assert.False(queued.Contains(turn.Turn.Id));
        Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        await Assert.ThrowsAsync<AggregateException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AggregateException>(() => scope.Coordinator.StopAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0, RoutingTaskKind.Ambiguous)]
    [InlineData(0, RoutingTaskKind.Unsupported)]
    [InlineData(0, RoutingTaskKind.TextConversation)]
    [InlineData(1, RoutingTaskKind.Ambiguous)]
    [InlineData(1, RoutingTaskKind.Unsupported)]
    [InlineData(1, RoutingTaskKind.TextConversation)]
    [InlineData(2, RoutingTaskKind.TextConversation)]
    [Trait("Category", "Unit")]
    public async Task SelectedOwnerCausePreventsRoutingOutcomeOrInferenceBeforePublication(
        int stage, RoutingTaskKind taskKind)
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        var router = new PausedRouter();
        var context = new ControlledContextBuilder(block: stage == 2);
        await using var scope = CreateCoordinator(store, engine, router, context);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var running = new List<TurnId>();
        if (stage == 0)
        {
            for (var index = 0; index < 4; index++)
            {
                var activeTurn = await scope.Coordinator.SubmitAsync(Request(requestId: $"active-{index}"), CancellationToken.None);
                running.Add(activeTurn.Turn.Id);
                await engine.WaitForTurnStartedAsync(activeTurn.Turn.Id);
            }
        }

        router.BlockNextRoute = stage == 1;
        var turn = await scope.Coordinator.SubmitAsync(Request(taskKind: taskKind), CancellationToken.None);
        if (stage == 1)
        {
            await router.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else if (stage == 2)
        {
            await context.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var collection = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField(stage == 0 ? "queued" : "active", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope.Coordinator)!;
        var entry = collection[turn.Turn.Id]!;
        var item = stage == 0 ? entry : entry.GetType().GetProperty("Item")!.GetValue(entry)!;
        var owner = (CancellationTokenSource)item.GetType().GetProperty("Cancellation")!.GetValue(item)!;
        var publicationPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = owner.Token.Register(() =>
        {
            publicationPaused.TrySetResult();
            release.Wait();
        });
        var cancellation = scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None).AsTask();
        try
        {
            await publicationPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (stage == 0)
            {
                engine.ReleaseTurn(running[0]);
            }
            else if (stage == 1)
            {
                router.Continue.TrySetResult();
            }
            else
            {
                context.Continue.TrySetResult();
            }

            await WaitForTerminalEventAsync(store, turn.Turn.Id);
            Assert.False(cancellation.IsCompleted);
            Assert.Equal(stage == 0 ? 4 : 0, engine.ExecutionCount);
            Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
            var events = await scope.Coordinator.ReadEventsAfterAsync(turn.Turn.Id, 0, 20, CancellationToken.None);
            Assert.Single(events, item => item.EventType == nameof(TurnCancelled));
            Assert.DoesNotContain(events, item => item.EventType is nameof(TurnFailed) or nameof(TurnClarificationRequired));
            if (stage == 0)
            {
                Assert.DoesNotContain(events, item => item.EventType == nameof(TurnRouting));
            }
            Assert.DoesNotContain(store.Messages,
                message => message.ConversationId == turn.Turn.ConversationId && message.Role == "assistant");
            Assert.True(scope.Coordinator.IsReady);
        }
        finally
        {
            release.Set();
            router.Continue.TrySetResult();
            context.Continue.TrySetResult();
        }

        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.Coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WorkerClaimDuringPendingCancellationPersistenceReclaimsExactlyOnce()
    {
        var store = new InMemoryConversationStore();
        var engine = new ControlledEngine(store, block: true);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var running = new List<TurnId>();
        for (var index = 0; index < 4; index++)
        {
            var turn = await scope.Coordinator.SubmitAsync(Request(requestId: $"active-{index}"), CancellationToken.None);
            running.Add(turn.Turn.Id);
            await engine.WaitForTurnStartedAsync(turn.Turn.Id);
        }

        var pending = await scope.Coordinator.SubmitAsync(Request(requestId: "pending"), CancellationToken.None);
        store.BlockNextCancellationOutcome = true;
        var cancellation = scope.Coordinator.CancelAsync(pending.Turn.Id, CancellationToken.None).AsTask();
        try
        {
            await store.CancellationPersistenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            engine.ReleaseTurn(running[0]);
            await WaitForTerminalEventAsync(store, pending.Turn.Id);
            Assert.False(cancellation.IsCompleted);
            Assert.Equal(4, engine.ExecutionCount);
            Assert.Equal(0, engine.CancelCallCount);
        }
        finally
        {
            store.ContinueCancellationPersistence.TrySetResult();
        }

        await cancellation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(await scope.Coordinator.ReadEventsAfterAsync(pending.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnCancelled));
        Assert.True(scope.Coordinator.IsReady);
        var conversation = (await store.GetTurnAsync(running[1], CancellationToken.None))!.ConversationId;
        for (var index = 0; index < 20; index++)
        {
            await scope.Coordinator.SubmitAsync(Request(conversation, $"remaining-{index}"), CancellationToken.None);
        }

        await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
            await scope.Coordinator.SubmitAsync(Request(conversation, "overflow"), CancellationToken.None));
        await scope.Coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingAcceptedTurnReadSurfacesInterruptionWithoutCallingEngine()
    {
        var store = new InMemoryConversationStore { HideNextTurnRead = true };
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var turn = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalEventAsync(store, turn.Turn.Id);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        Assert.Contains(await scope.Coordinator.ReadEventsAfterAsync(turn.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnInterrupted)
                && item.PayloadJson.Contains("turn_execution_interrupted_InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentDurableCompletionPreventsInferenceAndLaterCancellationIsIdempotent()
    {
        var store = new InMemoryConversationStore { CompleteNextTurnOnRead = true };
        var engine = new ControlledEngine(store);
        await using var scope = CreateCoordinator(store, engine);
        await scope.Coordinator.StartAsync(CancellationToken.None);
        var turn = await scope.Coordinator.SubmitAsync(Request(), CancellationToken.None);
        await WaitForTerminalEventAsync(store, turn.Turn.Id);
        await scope.Coordinator.StopAsync(CancellationToken.None);
        await scope.Coordinator.CancelAsync(turn.Turn.Id, CancellationToken.None);
        await scope.Coordinator.CancelAsync(TurnId.New(), CancellationToken.None);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(turn.Turn.Id, CancellationToken.None))!.Status);
        Assert.Single(await scope.Coordinator.ReadEventsAfterAsync(turn.Turn.Id, 0, 20, CancellationToken.None),
            item => item.EventType == nameof(TurnCompleted));
    }

    private static Scope CreateCoordinator(
        InMemoryConversationStore store,
        ControlledEngine engine,
        IModelRouter? router = null,
        ControlledContextBuilder? contextBuilder = null,
        FixedClock? clock = null) =>
        new(new LocalTurnCoordinator(
            store,
            store,
            router ?? new ControlledRouter(RouteDecision.Local("local", "policy")),
            contextBuilder ?? new ControlledContextBuilder(),
            engine,
            clock ?? new FixedClock(),
            new LocalTurnCoordinatorOptions(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)),
            "Host instructions."));

    private static LocalTurnRequest Request(
        ConversationId? conversationId = null,
        string requestId = "client-request",
        string text = "Hello",
        TimeSpan? deadline = null,
        RoutingTaskKind taskKind = RoutingTaskKind.TextConversation) =>
        new(conversationId ?? ConversationId.New(), requestId, text, taskKind, deadline);

    private static async Task WaitForTerminalEventAsync(InMemoryConversationStore store, TurnId turnId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var events = await store.ReadTurnEventsAfterAsync(turnId, 0, 20, deadline.Token);
            if (events.Any(item => item.EventType is nameof(TurnCompleted) or nameof(TurnFailed)
                or nameof(TurnCancelled) or nameof(TurnInterrupted)))
            {
                return;
            }

            await Task.Yield();
        }
    }

    private static async Task WaitForTerminalAsync(InMemoryConversationStore store, TurnId turnId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var turn = await store.GetTurnAsync(turnId, deadline.Token);
            if (turn is not null && turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private sealed class Scope(ILocalTurnCoordinator coordinator) : IAsyncDisposable
    {
        public ILocalTurnCoordinator Coordinator { get; } = coordinator;

        public async ValueTask DisposeAsync()
        {
            if (Coordinator.IsReady)
            {
                await Coordinator.StopAsync(CancellationToken.None);
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PausedRouter : IModelRouter
    {
        private readonly LocalOnlyModelRouter router = new();
        public bool BlockNextRoute { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RouteDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken)
        {
            var decision = await router.RouteAsync(request, cancellationToken);
            if (BlockNextRoute)
            {
                BlockNextRoute = false;
                Started.TrySetResult();
                await Continue.Task;
            }

            return decision;
        }
    }

    private sealed class ControlledRouter(RouteDecision decision) : IModelRouter
    {
        public ValueTask<RouteDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ControlledContextBuilder(bool block = false, bool ignoreCancellation = false) : IContextBuilder
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ContextPacket> BuildAsync(ContextBuildRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block)
            {
                Started.TrySetResult();
                if (ignoreCancellation)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult();
                    }

                    await Continue.Task;
                }
                else
                {
                    await Continue.Task.WaitAsync(cancellationToken);
                }
            }

            return new ContextPacket(
                "packet",
                "policy",
                [new ContextItem("task", request.TaskText, "LocalOnly", "user")],
                new ContextEstimate(0, 0, 0, 0, 0, 0, 0, 0, 8192, 0, false, false, "unit fixture"));
        }
    }

    private sealed class ControlledEngine(
        InMemoryConversationStore store,
        bool block = false,
        bool fail = false,
        bool returnWithoutOutcome = false,
        bool stopFails = false,
        bool honorDeadline = false,
        bool hideOutcomeRead = false,
        bool persistCancellationOutcome = false) : IAgentEngine
    {
        private int executionCount;
        private int cancelCallCount;
        private int stopCallCount;
        public int CancelCallCount => Volatile.Read(ref cancelCallCount);
        public int StopCallCount => Volatile.Read(ref stopCallCount);
        public TaskCompletionSource<TurnStatus> CancellationOutcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken EngineOwnerToken { get; private set; }
        public CancellationToken EngineDeadlineToken { get; private set; }
        private readonly ConcurrentDictionary<TurnId, TaskCompletionSource> startedTurns = new();
        private readonly ConcurrentDictionary<TurnId, TaskCompletionSource> releaseTurns = new();
        public int ExecutionCount => Volatile.Read(ref executionCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ConversationId> SecondConversationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForTurnStartedAsync(TurnId turnId) =>
            startedTurns.GetOrAdd(turnId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void ReleaseTurn(TurnId turnId) =>
            releaseTurns.GetOrAdd(turnId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var execution = Interlocked.Increment(ref executionCount);
            if (execution >= 4)
            {
                FourStarted.TrySetResult();
            }

            Started.TrySetResult();
            var turn = await store.GetTurnAsync(request.TurnId, cancellationToken)
                ?? throw new InvalidOperationException("Turn was not persisted.");
            if (execution == 2)
            {
                SecondConversationStarted.TrySetResult(turn.ConversationId);
            }

            await store.UpdateTurnStatusAsync(
                request.TurnId,
                TurnStatus.Running,
                turn.Version,
                Now,
                cancellationToken);

            if (persistCancellationOutcome)
            {
                EngineOwnerToken = cancellationToken;
                EngineDeadlineToken = request.DeadlineCancellationToken;
                using var owner = cancellationToken.Register(() => CancellationOutcome.TrySetResult(TurnStatus.Cancelled));
                using var deadline = request.DeadlineCancellationToken.Register(() => CancellationOutcome.TrySetResult(TurnStatus.Interrupted));
                using var shutdown = request.HostShutdownToken.Register(() => CancellationOutcome.TrySetResult(TurnStatus.Interrupted));
                startedTurns.GetOrAdd(request.TurnId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
                var outcome = await CancellationOutcome.Task;
                await ContinueCancellation.Task;
                var cancelledTurn = (await store.GetTurnAsync(request.TurnId, CancellationToken.None))!;
                AgentEvent terminal = outcome == TurnStatus.Cancelled
                    ? new TurnCancelled(request.TurnId, Now)
                    : new TurnInterrupted(request.TurnId, Now, "interactive_deadline_exceeded");
                await store.UpdateTurnStatusAndAppendEventAsync(
                    request.TurnId, outcome, cancelledTurn.Version, Now, terminal.GetType().Name,
                    JsonSerializer.Serialize(terminal, terminal.GetType()), Now, CancellationToken.None);
                yield return terminal;
                yield break;
            }

            if (block)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    request.HostShutdownToken,
                    request.DeadlineCancellationToken);
                if (honorDeadline)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                }
                else
                {
                    startedTurns.GetOrAdd(
                        request.TurnId,
                        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                    await releaseTurns.GetOrAdd(
                        request.TurnId,
                        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                        .Task.WaitAsync(linked.Token);
                }
            }

            if (fail)
            {
                throw new InvalidOperationException("private provider details");
            }

            if (returnWithoutOutcome)
            {
                yield break;
            }

            var running = await store.GetTurnAsync(request.TurnId, cancellationToken)
                ?? throw new InvalidOperationException("Turn was not persisted.");
            var completed = new TurnCompleted(request.TurnId, Now);
            await store.UpdateTurnStatusAndAppendEventAsync(
                request.TurnId,
                TurnStatus.Completed,
                running.Version,
                Now,
                nameof(TurnCompleted),
                JsonSerializer.Serialize(completed),
                Now,
                cancellationToken,
                new ConversationMessage(Guid.NewGuid(), running.ConversationId, "assistant", "Done.", Now));
            if (hideOutcomeRead)
            {
                store.HideNextTurnRead = true;
            }

            Completed.TrySetResult();
            yield return completed;
        }

        public ValueTask CancelAsync(TurnId turnId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref cancelCallCount);
            if (persistCancellationOutcome)
            {
                CancellationOutcome.TrySetResult(TurnStatus.Cancelled);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(TurnId turnId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref stopCallCount);
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : stopFails
                ? ValueTask.FromException(new InvalidOperationException("engine stop failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryConversationStore : IConversationStore, IAtomicTurnOutcomeStore
    {
        private readonly object sync = new();
        private readonly Dictionary<TurnId, ConversationTurn> turns = [];
        private readonly Dictionary<(ConversationId ConversationId, string RequestId), (string Fingerprint, TurnId TurnId, Guid MessageId)> requests = [];
        private readonly Dictionary<TurnId, List<PersistedTurnEvent>> events = [];
        private readonly List<ConversationMessage> messages = [];
        private TurnId? watchedTurnRead;
        private TaskCompletionSource? watchedTurnReadCompletion;
        public bool CompleteWhenCancellationRaces { get; init; }
        public bool BlockNextRecovery { get; set; }
        public bool FailNextRecovery { get; set; }
        public int RecoveryReads { get; private set; }
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ConflictNextCancellationOutcome { get; set; }
        public int CancellationOutcomeConflictsRemaining { get; set; }
        public bool CompleteCancellationOnRetryConflict { get; set; }
        public bool LeaveNextTerminalOutcomeUnchanged { get; set; }
        public bool FailNextTerminalOutcome { get; set; }
        public bool TimeoutNextTerminalOutcome { get; set; }
        public bool BlockNextCancellationOutcome { get; set; }
        public TaskCompletionSource CancellationPersistenceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueCancellationPersistence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompleteNextTurnOnRead { get; set; }
        public bool HideNextTurnRead { get; set; }
        public bool HideExistingRequests { get; set; }
        public bool BlockNextSubmission { get; init; }
        public bool FailNextSubmission { get; set; }
        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TurnId LastSubmittedTurnId { get; private set; }

        public TaskCompletionSource WatchNextRead(TurnId turnId)
        {
            lock (sync)
            {
                watchedTurnRead = turnId;
                watchedTurnReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return watchedTurnReadCompletion;
            }
        }

        public IReadOnlyList<ConversationMessage> Messages
        {
            get
            {
                lock (sync)
                {
                    return messages.ToArray();
                }
            }
        }

        public ValueTask<ConversationMessage> AppendMessageAsync(
            ConversationMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                messages.Add(message);
                return ValueTask.FromResult(message);
            }
        }

        public ValueTask<IReadOnlyList<ConversationMessage>> ReadRecentAsync(
            ConversationId conversationId,
            int maximumMessages,
            CancellationToken cancellationToken,
            Guid? currentTaskMessageId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                IReadOnlyList<ConversationMessage> result = messages
                    .Where(message => message.ConversationId == conversationId)
                    .TakeLast(maximumMessages)
                    .ToArray();
                return ValueTask.FromResult(result);
            }
        }

        public async ValueTask<SubmittedConversationTurn> SubmitTurnAsync(
            ConversationTurnSubmission submission,
            CancellationToken cancellationToken)
        {
            if (FailNextSubmission)
            {
                FailNextSubmission = false;
                throw new IOException("in-memory submission failed");
            }

            if (BlockNextSubmission)
            {
                SubmissionStarted.TrySetResult();
                await ContinueSubmission.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                LastSubmittedTurnId = submission.TurnId;
                var key = (submission.ConversationId, submission.ClientRequestId);
                if (requests.TryGetValue(key, out var prior))
                {
                    if (prior.Fingerprint != submission.RequestFingerprint)
                    {
                        throw new TurnRequestConflictException();
                    }

                    return new SubmittedConversationTurn(
                        turns[prior.TurnId],
                        prior.MessageId,
                        true);
                }

                var turn = new ConversationTurn(
                    submission.TurnId,
                    submission.ConversationId,
                    TurnStatus.Received,
                    submission.CreatedAtUtc,
                    submission.CreatedAtUtc,
                    1);
                turns.Add(turn.Id, turn);
                requests.Add(key, (submission.RequestFingerprint, turn.Id, submission.UserMessageId));
                messages.Add(new ConversationMessage(
                    submission.UserMessageId,
                    submission.ConversationId,
                    "user",
                    submission.Text,
                    submission.CreatedAtUtc));
                AppendEvent(turn.Id, nameof(TurnReceived), JsonSerializer.Serialize(new TurnReceived(turn.Id, submission.CreatedAtUtc)), submission.CreatedAtUtc);
                return new SubmittedConversationTurn(turn, submission.UserMessageId, false);
            }
        }

        public ValueTask<SubmittedConversationTurn?> FindSubmittedTurnAsync(
            ConversationId conversationId,
            string clientRequestId,
            string requestFingerprint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (HideExistingRequests)
                {
                    return ValueTask.FromResult<SubmittedConversationTurn?>(null);
                }

                if (!requests.TryGetValue((conversationId, clientRequestId), out var prior))
                {
                    return ValueTask.FromResult<SubmittedConversationTurn?>(null);
                }

                if (prior.Fingerprint != requestFingerprint)
                {
                    throw new TurnRequestConflictException();
                }

                return ValueTask.FromResult<SubmittedConversationTurn?>(
                    new SubmittedConversationTurn(turns[prior.TurnId], prior.MessageId, true));
            }
        }

        public ValueTask<ConversationTurn> CreateTurnAsync(
            TurnId turnId,
            ConversationId conversationId,
            TurnStatus status,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                var turn = new ConversationTurn(turnId, conversationId, status, createdAtUtc, createdAtUtc, 1);
                turns.Add(turnId, turn);
                events.Add(turnId, []);
                return ValueTask.FromResult(turn);
            }
        }

        public ValueTask<ConversationTurn?> GetTurnAsync(TurnId turnId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (HideNextTurnRead)
                {
                    HideNextTurnRead = false;
                    return ValueTask.FromResult<ConversationTurn?>(null);
                }

                turns.TryGetValue(turnId, out var turn);
                if (watchedTurnRead == turnId)
                {
                    watchedTurnRead = null;
                    watchedTurnReadCompletion!.TrySetResult();
                    watchedTurnReadCompletion = null;
                }

                if (CompleteNextTurnOnRead && turn is not null)
                {
                    CompleteNextTurnOnRead = false;
                    turn = turn with
                    {
                        Status = TurnStatus.Completed,
                        UpdatedAtUtc = Now,
                        Version = turn.Version + 1
                    };
                    turns[turnId] = turn;
                    AppendEvent(turnId, nameof(TurnCompleted), "{}", Now);
                }

                return ValueTask.FromResult(turn);
            }
        }

        public async ValueTask<IReadOnlyList<ConversationTurn>> ReadNonterminalTurnsAsync(
            int maximumTurns,
            CancellationToken cancellationToken)
        {
            RecoveryReads++;
            if (BlockNextRecovery)
            {
                BlockNextRecovery = false;
                RecoveryStarted.TrySetResult();
                await ContinueRecovery.Task.WaitAsync(cancellationToken);
            }
            if (FailNextRecovery)
            {
                FailNextRecovery = false;
                throw new IOException("Controlled recovery failure.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                IReadOnlyList<ConversationTurn> result = turns.Values
                    .Where(turn => turn.Status is not (TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted))
                    .Take(maximumTurns)
                    .ToArray();
                return result;
            }
        }

        public ValueTask<ConversationTurn> UpdateTurnStatusAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                var updated = UpdateTurn(turnId, status, expectedVersion, updatedAtUtc);
                return ValueTask.FromResult(updated);
            }
        }

        public ValueTask<PersistedTurnEvent> TransitionTurnAndAppendEventAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                UpdateTurn(turnId, status, expectedVersion, updatedAtUtc);
                return ValueTask.FromResult(AppendEvent(turnId, eventType, payloadJson, occurredAtUtc));
            }
        }

        public ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
            TurnId turnId,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                return ValueTask.FromResult(AppendEvent(turnId, eventType, payloadJson, occurredAtUtc));
            }
        }

        public ValueTask<IReadOnlyList<PersistedTurnEvent>> ReadTurnEventsAfterAsync(
            TurnId turnId,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                IReadOnlyList<PersistedTurnEvent> result = events.GetValueOrDefault(turnId, [])
                    .Where(item => item.Sequence > afterSequence)
                    .Take(maximumEvents)
                    .ToArray();
                return ValueTask.FromResult(result);
            }
        }

        public ValueTask<PersistedTurnEvent> UpdateTurnStatusAndAppendEventAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken,
            ConversationMessage? finalAssistantMessage = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TimeoutNextTerminalOutcome)
            {
                TimeoutNextTerminalOutcome = false;
                return new ValueTask<PersistedTurnEvent>(WaitForTerminalPersistenceTimeoutAsync(cancellationToken));
            }

            lock (sync)
            {
                if (status == TurnStatus.Cancelled && BlockNextCancellationOutcome)
                {
                    BlockNextCancellationOutcome = false;
                    CancellationPersistenceStarted.TrySetResult();
                    return new ValueTask<PersistedTurnEvent>(ContinueCancellationOutcomeAsync(
                        turnId, status, expectedVersion, updatedAtUtc, eventType,
                        payloadJson, occurredAtUtc, cancellationToken, finalAssistantMessage));
                }

                if (FailNextTerminalOutcome)
                {
                    FailNextTerminalOutcome = false;
                    throw new IOException("in-memory terminal persistence failed");
                }

                if (LeaveNextTerminalOutcomeUnchanged)
                {
                    LeaveNextTerminalOutcomeUnchanged = false;
                    return ValueTask.FromResult(AppendEvent(turnId, eventType, payloadJson, occurredAtUtc));
                }

                if (status == TurnStatus.Cancelled && ConflictNextCancellationOutcome)
                {
                    ConflictNextCancellationOutcome = false;
                    throw new PersistenceConcurrencyException("The turn changed before cancellation.");
                }

                if (status == TurnStatus.Cancelled && CancellationOutcomeConflictsRemaining > 0)
                {
                    CancellationOutcomeConflictsRemaining--;
                    throw new PersistenceConcurrencyException("The turn changed before cancellation.");
                }

                if (status == TurnStatus.Cancelled && CompleteCancellationOnRetryConflict)
                {
                    CompleteCancellationOnRetryConflict = false;
                    var current = turns[turnId];
                    turns[turnId] = current with
                    {
                        Status = TurnStatus.Completed,
                        UpdatedAtUtc = updatedAtUtc,
                        Version = current.Version + 1
                    };
                    AppendEvent(turnId, nameof(TurnCompleted), "{}", occurredAtUtc);
                    throw new PersistenceConcurrencyException("The concurrent completion won the retry.");
                }

                if (status == TurnStatus.Cancelled && CompleteWhenCancellationRaces)
                {
                    var current = turns[turnId];
                    var completed = current with
                    {
                        Status = TurnStatus.Completed,
                        UpdatedAtUtc = updatedAtUtc,
                        Version = current.Version + 1
                    };
                    turns[turnId] = completed;
                    AppendEvent(turnId, nameof(TurnCompleted), "{}", occurredAtUtc);
                    throw new PersistenceConcurrencyException("The concurrent completion won.");
                }

                UpdateTurn(turnId, status, expectedVersion, updatedAtUtc);
                if (finalAssistantMessage is not null)
                {
                    messages.Add(finalAssistantMessage);
                }

                return ValueTask.FromResult(AppendEvent(turnId, eventType, payloadJson, occurredAtUtc));
            }
        }

        private async Task<PersistedTurnEvent> ContinueCancellationOutcomeAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken,
            ConversationMessage? finalAssistantMessage)
        {
            await ContinueCancellationPersistence.Task.WaitAsync(cancellationToken);
            return await UpdateTurnStatusAndAppendEventAsync(
                turnId, status, expectedVersion, updatedAtUtc, eventType,
                payloadJson, occurredAtUtc, cancellationToken, finalAssistantMessage);
        }

        private static async Task<PersistedTurnEvent> WaitForTerminalPersistenceTimeoutAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Persistence was expected to time out.");
        }

        private ConversationTurn UpdateTurn(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc)
        {
            var current = turns[turnId];
            if (current.Version != expectedVersion
                || current.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted)
            {
                throw new PersistenceConcurrencyException("The in-memory turn changed.");
            }

            var updated = current with { Status = status, UpdatedAtUtc = updatedAtUtc, Version = current.Version + 1 };
            turns[turnId] = updated;
            return updated;
        }

        private PersistedTurnEvent AppendEvent(
            TurnId turnId,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc)
        {
            if (!events.TryGetValue(turnId, out var items))
            {
                items = [];
                events.Add(turnId, items);
            }

            var persisted = new PersistedTurnEvent(
                Guid.NewGuid(),
                turnId,
                items.Count + 1,
                eventType,
                payloadJson,
                occurredAtUtc);
            items.Add(persisted);
            return persisted;
        }
    }
}
