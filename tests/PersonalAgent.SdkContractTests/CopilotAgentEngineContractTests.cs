using System.Diagnostics;
using System.Net;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.SdkContractTests;

[Trait("Category", "Integration")]
[Collection("Copilot runtime process isolation")]
public sealed class CopilotAgentEngineContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private const string ToolName = "read_only_lookup";
    private const string ToolSchema = """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""";

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ActualRuntimeStreamsEventsUsesExplicitProviderAndPersistsOrderedTerminalOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var database = await CreateDatabaseAsync(databaseFile.Path);
        var store = new SqliteConversationStore(database, new TestClock());
        await using var localProvider = await FakeOpenAiProvider.StartAsync();
        await using var cloudProvider = await FakeOpenAiProvider.StartAsync();
        var runtimeDirectory = Path.Combine(data.Path, "runtime");
        var engine = CreateEngine(store, localProvider.BaseUrl, cloudProvider.BaseUrl, runtimeDirectory);
        var localTurn = await CreateTurnAsync(store);
        var cloudTurn = await CreateTurnAsync(store);
        var hostileWorkspace = Path.Combine(runtimeDirectory, $"turn-{localTurn.Value:N}", "workspace");
        Directory.CreateDirectory(Path.Combine(hostileWorkspace, ".github"));
        await File.WriteAllTextAsync(Path.Combine(hostileWorkspace, "AGENTS.md"), "JARVIS_HOSTILE_WORKSPACE_SENTINEL");
        await File.WriteAllTextAsync(
            Path.Combine(hostileWorkspace, ".github", "copilot-instructions.md"),
            "JARVIS_HOSTILE_WORKSPACE_SENTINEL");

        var localEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(localTurn, ProviderKind.Local, "LOCAL_PACKET_MARKER"),
            CancellationToken.None));
        var cloudEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(cloudTurn, ProviderKind.Cloud, "CLOUD_PACKET_MARKER"),
            CancellationToken.None));

        Assert.Contains(localEvents, item => item is TextDelta { Text: "streamed " });
        Assert.Contains(localEvents, item => item is TurnCompleted);
        Assert.Contains(cloudEvents, item => item is TurnCompleted);
        Assert.Single(localEvents.OfType<TurnCompleted>());
        Assert.Single(cloudEvents.OfType<TurnCompleted>());
        Assert.Contains("LOCAL_PACKET_MARKER", Assert.Single(localProvider.Requests).Body, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOUD_PACKET_MARKER", Assert.Single(localProvider.Requests).Body, StringComparison.Ordinal);
        Assert.Contains("CLOUD_PACKET_MARKER", Assert.Single(cloudProvider.Requests).Body, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL_PACKET_MARKER", Assert.Single(cloudProvider.Requests).Body, StringComparison.Ordinal);
        Assert.DoesNotContain("JARVIS_HOSTILE_WORKSPACE_SENTINEL", Assert.Single(localProvider.Requests).Body, StringComparison.Ordinal);
        Assert.Empty(localProvider.Requests.SelectMany(item => item.ToolNames));
        Assert.Empty(cloudProvider.Requests.SelectMany(item => item.ToolNames));

        var savedTurn = await store.GetTurnAsync(localTurn, CancellationToken.None);
        Assert.Equal(TurnStatus.Completed, savedTurn?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(localTurn, 0, 1000, CancellationToken.None);
        Assert.Equal(Enumerable.Range(1, persisted.Count).Select(value => (long)value), persisted.Select(item => item.Sequence));
        Assert.Equal(localEvents.Select(item => item.GetType().Name), persisted.Select(item => item.EventType));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task RejectedDuplicateClaimDoesNotTerminalizeTheActiveTurn()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);
        var turn = await store.GetTurnAsync(turnId, CancellationToken.None);
        await store.UpdateTurnStatusAsync(
            turnId,
            TurnStatus.Running,
            turn!.Version,
            Now,
            CancellationToken.None);

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            CollectAsync(engine.RunTurnAsync(
                CreateRequest(turnId, ProviderKind.Local, "duplicate claim"),
                CancellationToken.None)));

        Assert.Equal(TurnStatus.Running, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        Assert.Empty(await store.ReadTurnEventsAfterAsync(turnId, 0, 100, CancellationToken.None));
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task CancellationDuringDurableClaimPersistsCancelledOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var database = await CreateDatabaseAsync(databaseFile.Path);
        var innerStore = new SqliteConversationStore(database, new TestClock());
        var store = new ControlledTurnEventStore(innerStore)
        {
            TurnStatusUpdateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(innerStore);
        await using var blocker = await database.OpenConnectionAsync();
        await using var heldTransaction = blocker.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var run = CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "cancel during claim"),
            cancellation.Token));

        await store.TurnStatusUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await heldTransaction.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(TurnStatus.Cancelled, (await innerStore.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var savedEvents = await innerStore.ReadTurnEventsAfterAsync(turnId, 0, 100, CancellationToken.None);
        Assert.Equal(nameof(TurnCancelled), Assert.Single(savedEvents).EventType);
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task CancellationClaimCannotTerminalizeAConcurrentOwnersTurn()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var database = await CreateDatabaseAsync(databaseFile.Path);
        var innerStore = new SqliteConversationStore(database, new TestClock());
        var store = new ControlledTurnEventStore(innerStore)
        {
            TurnStatusUpdateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ClaimCancellationOutcomeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowClaimCancellationOutcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(innerStore);
        await using var blocker = await database.OpenConnectionAsync();
        await using var heldTransaction = blocker.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var run = CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "competing claim"),
            cancellation.Token));

        await store.TurnStatusUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await heldTransaction.DisposeAsync();
        await store.ClaimCancellationOutcomeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competingClaim = await innerStore.GetTurnAsync(turnId, CancellationToken.None);
        await innerStore.UpdateTurnStatusAsync(
            turnId,
            TurnStatus.Running,
            competingClaim!.Version,
            Now,
            CancellationToken.None);
        store.AllowClaimCancellationOutcome.TrySetResult();

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TurnStatus.Running, (await innerStore.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        Assert.Empty(await innerStore.ReadTurnEventsAfterAsync(turnId, 0, 100, CancellationToken.None));
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task DisposingConsumerDoesNotHangOnFullBoundedEventOutput()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.StreamingDeltaCount = 512;
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);
        var iterator = engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "bounded output shutdown"),
            CancellationToken.None).GetAsyncEnumerator();

        try
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            await provider.StreamingResponseWritten.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await iterator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await iterator.DisposeAsync();
        }

        var finalStatus = (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status;
        Assert.True(finalStatus is TurnStatus.Cancelled or TurnStatus.Failed or TurnStatus.Interrupted);
        var persistedEvents = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        Assert.Single(persistedEvents, item =>
            item.EventType is nameof(TurnCancelled) or nameof(TurnFailed) or nameof(TurnInterrupted));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ExcessiveStreamingTextFailsTurnWithinHostEventBudget()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.StreamingContent = new string('x', 1_000_001);
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "event budget"),
            CancellationToken.None));

        Assert.Single(events.OfType<TurnFailed>(), item => item.ReasonCode == "event_budget_exceeded");
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        Assert.DoesNotContain(events, item => item is TurnCompleted);
    }

    private static async Task AssertPreCancelledOutcomeAsync(
        CopilotAgentEngine engine,
        SqliteConversationStore store,
        AgentTurnRequest request,
        CancellationToken callerToken,
        string expectedReason)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(engine.RunTurnAsync(request, callerToken)));

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(request.TurnId, CancellationToken.None))?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(request.TurnId, 0, 1000, CancellationToken.None);
        var interrupted = Assert.Single(persisted, item => item.EventType == nameof(TurnInterrupted));
        Assert.Contains(expectedReason, interrupted.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(persisted, item => item.EventType == nameof(TurnCancelled));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ActualRuntimeInvokesOnlyRegisteredToolAndForwardsHostOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var expectedResult = new ToolDispatchResult("Succeeded", """{"value":"host-result"}""", null);
        provider.ExpectedToolResult = System.Text.Json.JsonSerializer.Serialize(expectedResult);
        var dispatcher = new RecordingDispatcher(expectedResult);
        var engine = CreateEngine(
            store,
            provider.BaseUrl,
            provider.BaseUrl,
            Path.Combine(data.Path, "runtime"),
            dispatcher);
        var turnId = await CreateTurnAsync(store);
        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "tool contract", [new AgentToolDefinition(ToolName, ToolSchema, true)]),
            CancellationToken.None));
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        var disconnectedPrefix = persisted.Take(1).ToArray();
        var reconnectedSuffix = await store.ReadTurnEventsAfterAsync(
            turnId,
            disconnectedPrefix[^1].Sequence,
            1000,
            CancellationToken.None);

        Assert.True(dispatcher.Calls == 1,
            $"Expected one tool dispatch; got {dispatcher.Calls}. Exposed tools: {string.Join("|", provider.Requests.SelectMany(item => item.ToolNames))}. Events: {string.Join("|", events.Select(item => item.GetType().Name))}");
        Assert.Single(events.OfType<ToolProposed>());
        Assert.Single(events.OfType<ToolCompleted>());
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal(ToolName, Assert.Single(request.ToolNames)));
        using var args = System.Text.Json.JsonDocument.Parse(dispatcher.LastArguments!);
        Assert.Equal("fixture-key", args.RootElement.GetProperty("key").GetString());
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        Assert.Equal(
            persisted.Select(item => item.Sequence),
            disconnectedPrefix.Select(item => item.Sequence).Concat(reconnectedSuffix.Select(item => item.Sequence)));
        Assert.Equal(
            persisted.Select(item => item.EventType),
            disconnectedPrefix.Select(item => item.EventType).Concat(reconnectedSuffix.Select(item => item.EventType)));
        Assert.Single(
            disconnectedPrefix.Concat(reconnectedSuffix),
            item => item.EventType == nameof(TurnCompleted));
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ActualRuntimeToolLoopCannotExceedHostBudget()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.RepeatTool = true;
        var dispatcher = new RecordingDispatcher(new ToolDispatchResult("Succeeded", """{"ok":true}""", null));
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"), dispatcher);
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "budget contract", [new AgentToolDefinition(ToolName, ToolSchema, true)]),
            CancellationToken.None));

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, events.OfType<ToolProposed>().Count());
        Assert.Equal(2, events.OfType<ToolCompleted>().Count());
        Assert.Equal("Rejected", events.OfType<ToolCompleted>().Last().Outcome);
        Assert.Equal("tool_budget_exceeded", Assert.Single(events.OfType<TurnFailed>()).ReasonCode);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("caller")]
    [InlineData("shutdown")]
    [Trait("Category", "SdkContract")]
    public async Task ExternalCancellationBoundsBlockedToolBudgetRejectionPersistence(string cause)
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        var blockingStore = new ControlledTurnEventStore(store, blockBudgetRejectionUntilCancelled: true);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.RepeatTool = true;
        var dispatcher = new RecordingDispatcher(new ToolDispatchResult("Succeeded", """{"ok":true}""", null));
        var engine = CreateEngine(blockingStore, provider.BaseUrl, provider.BaseUrl,
            Path.Combine(data.Path, "runtime"), dispatcher);
        var turnId = await CreateTurnAsync(store);
        using var caller = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        var request = CreateRequest(turnId, ProviderKind.Local, "blocked budget rejection",
            [new AgentToolDefinition(ToolName, ToolSchema, true)], deadline: TimeSpan.FromSeconds(5)) with
        {
            HostShutdownToken = shutdown.Token
        };
        var collecting = CollectAsync(engine.RunTurnAsync(request, caller.Token));
        await blockingStore.BudgetRejectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(blockingStore.BudgetRejectionCancelled.Task.IsCompleted);
        Assert.False(collecting.IsCompleted);
        if (cause == "caller")
        {
            caller.Cancel();
        }
        else if (cause == "shutdown")
        {
            shutdown.Cancel();
        }

        await blockingStore.BudgetRejectionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(7));
        if (cause == "caller")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await collecting.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            Assert.Equal("tool_budget_exceeded",
                Assert.Single((await collecting.WaitAsync(TimeSpan.FromSeconds(10))).OfType<TurnFailed>()).ReasonCode);
        }
        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
        var events = await store.ReadTurnEventsAfterAsync(turnId, 0, 100, CancellationToken.None);
        var terminal = Assert.Single(events, item => item.EventType == nameof(TurnFailed));
        Assert.Contains("tool_budget_exceeded", terminal.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item.EventType == nameof(TurnCompleted));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ProviderFailureEmitsSafeFailureCodeWithoutLeakingProviderMessage()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.FailInference = true;
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "provider failure"),
            CancellationToken.None));

        Assert.Equal("engine_failure", Assert.Single(events.OfType<TurnFailed>()).ReasonCode);
        Assert.DoesNotContain(events.OfType<TurnFailed>(), item => item.ReasonCode.Contains("Controlled provider refusal", StringComparison.Ordinal));
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task EventPersistenceFailureInterruptsRuntimeAndPersistsTerminalOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        var failingStore = new ControlledTurnEventStore(store);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(
            failingStore,
            provider.BaseUrl,
            provider.BaseUrl,
            Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "event persistence failure"),
            CancellationToken.None));

        var interrupted = Assert.Single(events.OfType<TurnInterrupted>());
        Assert.Equal("event_persistence_failed", interrupted.ReasonCode);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        Assert.Equal(nameof(TurnInterrupted), Assert.Single(persisted).EventType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SdkContract")]
    public async Task DeadlineBoundsBlockedEventPersistenceWithoutMisclassifyingCancellation(bool hostArbitration)
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        var blockingStore = new ControlledTurnEventStore(store, blockFirstAppendUntilCancelled: true);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(
            blockingStore,
            provider.BaseUrl,
            provider.BaseUrl,
            Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "event persistence deadline", deadline: TimeSpan.FromSeconds(2)) with
            {
                ResolveDeadlineCancellation = hostArbitration ? () => CancellationCause.Deadline : null
            },
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("engine_deadline_exceeded", Assert.Single(events.OfType<TurnInterrupted>()).ReasonCode);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        Assert.Equal(
            nameof(TurnInterrupted),
            Assert.Single(persisted, item => item.EventType == nameof(TurnInterrupted)).EventType);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task AcceptanceDeadlineBeforeCallerCancellationPersistsInterruptedOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.StallInference = true;
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);
        using var acceptedDeadline = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var eventsTask = CollectAsync(engine.RunTurnAsync(
                CreateRequest(
                    turnId,
                    ProviderKind.Local,
                    "acceptance deadline",
                    deadline: TimeSpan.FromSeconds(20),
                    deadlineCancellationToken: acceptedDeadline.Token),
                callerCancellation.Token));

        await provider.InferenceStalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        acceptedDeadline.Cancel();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await eventsTask.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        Assert.Single(persisted, item => item.EventType == nameof(TurnInterrupted));
        Assert.DoesNotContain(persisted, item => item.EventType == nameof(TurnCancelled));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task PreCancelledSystemSignalsTakePrecedenceOverCallerCancellation()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));

        using var deadline = new CancellationTokenSource();
        using var deadlineCaller = new CancellationTokenSource();
        deadline.Cancel();
        deadlineCaller.Cancel();
        var deadlineTurn = await CreateTurnAsync(store);
        await AssertPreCancelledOutcomeAsync(
            engine,
            store,
            CreateRequest(
                deadlineTurn,
                ProviderKind.Local,
                "pre-cancelled deadline",
                deadlineCancellationToken: deadline.Token),
            deadlineCaller.Token,
            "engine_deadline_exceeded");

        using var hostShutdown = new CancellationTokenSource();
        using var hostCaller = new CancellationTokenSource();
        hostShutdown.Cancel();
        hostCaller.Cancel();
        var shutdownTurn = await CreateTurnAsync(store);
        var shutdownRequest = CreateRequest(shutdownTurn, ProviderKind.Local, "pre-cancelled shutdown") with
        {
            HostShutdownToken = hostShutdown.Token
        };
        await AssertPreCancelledOutcomeAsync(
            engine,
            store,
            shutdownRequest,
            hostCaller.Token,
            "host_shutdown");
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task DeadlineCancelsStalledProviderAndPersistsOneInterruptedOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.StallInference = true;
        var runtimeDirectory = Path.Combine(data.Path, "runtime");
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, runtimeDirectory);
        var turnId = await CreateTurnAsync(store);
        var elapsed = Stopwatch.StartNew();

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "deadline contract", deadline: TimeSpan.FromSeconds(2)),
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));

        await provider.InferenceStalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("engine_deadline_exceeded", Assert.Single(events.OfType<TurnInterrupted>()).ReasonCode);
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);
        Assert.Equal(
            nameof(TurnInterrupted),
            Assert.Single(persisted, item => item.EventType == nameof(TurnInterrupted)).EventType);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Deadline cleanup took {elapsed.Elapsed}.");
        Assert.Empty(Directory.Exists(runtimeDirectory)
            ? Directory.EnumerateFileSystemEntries(runtimeDirectory)
            : []);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task ApiKeyIsResolvedFromHostReferenceAndNeverPersistedInTurnEvents()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        const string secret = "fixture-private-provider-token";
        var reference = new SecretReference("provider-token");
        var resolver = new RecordingSecretResolver(secret);
        var providerOptions = new CopilotProviderOptions("fixture-model", new Uri(provider.BaseUrl), ApiKeyReference: reference);
        var engine = new CopilotAgentEngine(
            store,
            new RecordingDispatcher(new ToolDispatchResult("Succeeded", """{"ok":true}""", null)),
            new TestClock(),
            new CopilotAgentEngineOptions(
                Path.Combine(data.Path, "runtime"),
                providerOptions,
                new CopilotProviderOptions("fixture-model", new Uri(provider.BaseUrl))),
            resolver);
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "secret contract"),
            CancellationToken.None));
        var persisted = await store.ReadTurnEventsAfterAsync(turnId, 0, 1000, CancellationToken.None);

        Assert.Equal(reference, resolver.ResolvedReference);
        Assert.Equal($"Bearer {secret}", Assert.Single(provider.Requests).Authorization);
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.DoesNotContain(secret, string.Join("|", persisted.Select(item => item.PayloadJson)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task LocalProviderRejectsNonLoopbackEndpointBeforeStartingRuntime()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = new CopilotAgentEngine(
            store,
            new RecordingDispatcher(new ToolDispatchResult("Succeeded", """{"ok":true}""", null)),
            new TestClock(),
            new CopilotAgentEngineOptions(
                Path.Combine(data.Path, "runtime"),
                new CopilotProviderOptions("fixture-model", new Uri("https://example.com/v1")),
                new CopilotProviderOptions("fixture-model", new Uri(provider.BaseUrl))));
        var turnId = await CreateTurnAsync(store);

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "invalid endpoint contract"),
            CancellationToken.None));

        Assert.Equal("engine_failure", Assert.Single(events.OfType<TurnFailed>()).ReasonCode);
        Assert.Empty(provider.Requests);
        Assert.False(Directory.Exists(Path.Combine(data.Path, "runtime")));
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task CancelAsyncCancelsHostToolAndEmitsOnePersistedCancellationOutcome()
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var dispatcher = new BlockingDispatcher();
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"), dispatcher);
        var turnId = await CreateTurnAsync(store);
        await engine.CancelAsync(TurnId.New(), CancellationToken.None);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.CancelAsync(turnId, cancelled.Token).AsTask());
        }

        var running = CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "cancel contract", [new AgentToolDefinition(ToolName, ToolSchema, true)]),
            CancellationToken.None));

        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(engine.RunTurnAsync(
                CreateRequest(turnId, ProviderKind.Local, "duplicate turn contract"),
                CancellationToken.None)));
        await engine.CancelAsync(turnId, CancellationToken.None);
        var events = await running.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(dispatcher.Cancelled.Task.IsCompleted);
        Assert.Single(events.OfType<TurnCancelled>());
        Assert.Empty(events.OfType<TurnCompleted>());
        Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SdkContract")]
    public async Task DirectCancellationWinningTerminalLockPreventsCompletionBeforeTokenCallbacks(bool shutdown)
    {
        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.PauseInference = true;
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, Path.Combine(data.Path, "runtime"));
        var turnId = await CreateTurnAsync(store);
        var request = CreateRequest(turnId, ProviderKind.Local, "direct cancellation race");
        var execution = CollectAsync(engine.RunTurnAsync(request, CancellationToken.None));
        await provider.InferenceStalled.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var activeTurns = (System.Collections.IDictionary)typeof(CopilotAgentEngine)
            .GetField("activeTurns", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(engine)!;
        var active = activeTurns[turnId]!;
        var token = (CancellationToken)active.GetType().GetProperty("CancellationToken")!.GetValue(active)!;
        using var release = new ManualResetEventSlim();
        var publicationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdBeforeObserver = token.Register(() =>
        {
            publicationHeld.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Direct cancellation callback barrier was not released.");
            }
        });
        var cancelling = Task.Run(async () =>
        {
            if (shutdown)
            {
                await engine.StopAsync(turnId, CancellationToken.None);
            }
            else
            {
                await engine.CancelAsync(turnId, CancellationToken.None);
            }
        });

        try
        {
            await publicationHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.ContinueInference.TrySetResult();
            await provider.StreamingResponseWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var events = await execution.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.DoesNotContain(events, item => item is TurnCompleted);
            if (shutdown)
            {
                Assert.Single(events.OfType<TurnInterrupted>());
                Assert.Equal("host_shutdown", Assert.Single(events.OfType<TurnInterrupted>()).ReasonCode);
                Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
            }
            else
            {
                Assert.Single(events.OfType<TurnCancelled>());
                Assert.Equal(TurnStatus.Cancelled, (await store.GetTurnAsync(turnId, CancellationToken.None))!.Status);
            }
            Assert.DoesNotContain(
                await store.ReadRecentAsync((await store.GetTurnAsync(turnId, CancellationToken.None))!.ConversationId,
                    10, CancellationToken.None),
                message => message.Role == "assistant");
            Assert.False(cancelling.IsCompleted);
        }
        finally
        {
            provider.ContinueInference.TrySetResult();
            release.Set();
        }

        await cancelling.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    public async Task RuntimeProcessCrashBecomesInterruptedAndNextTurnStartsFreshRuntime()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var data = IsolatedDirectory.Create();
        using var databaseFile = IsolatedDatabaseFile.Create();
        var store = new SqliteConversationStore(await CreateDatabaseAsync(databaseFile.Path), new TestClock());
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var dispatcher = new BlockingDispatcher();
        var runtimeDirectory = Path.Combine(data.Path, "runtime");
        var engine = CreateEngine(store, provider.BaseUrl, provider.BaseUrl, runtimeDirectory, dispatcher);
        var turnId = await CreateTurnAsync(store);
        var priorProcesses = GetProcessIds();
        var crashedRun = CollectAsync(engine.RunTurnAsync(
            CreateRequest(turnId, ProviderKind.Local, "crash contract", [new AgentToolDefinition(ToolName, ToolSchema, true)],
                TimeSpan.FromSeconds(12)),
            CancellationToken.None));
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var runtimeProcess = await FindNewRuntimeProcessAsync(priorProcesses);
        runtimeProcess.Kill();
        await runtimeProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var events = await crashedRun.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(dispatcher.Cancelled.Task.IsCompleted);
        Assert.True(events.OfType<TurnInterrupted>().Count() == 1,
            $"Expected interruption; terminal events: {string.Join("|", events.Where(item => item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted).Select(item => $"{item.GetType().Name}:{(item as TurnFailed)?.ReasonCode ?? (item as TurnInterrupted)?.ReasonCode}"))}");
        Assert.Empty(events.OfType<TurnCompleted>());
        Assert.Equal(TurnStatus.Interrupted, (await store.GetTurnAsync(turnId, CancellationToken.None))?.Status);

        var nextTurn = await CreateTurnAsync(store);
        var nextEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(nextTurn, ProviderKind.Local, "restart contract"),
            CancellationToken.None));
        Assert.Single(nextEvents.OfType<TurnCompleted>());
        Assert.Equal(2, provider.RequestCount);
    }

    private static CopilotAgentEngine CreateEngine(
        IConversationStore store,
        string localBaseUrl,
        string cloudBaseUrl,
        string runtimeDirectory,
        IToolDispatcher? dispatcher = null) =>
        new(
            store,
            dispatcher ?? new RecordingDispatcher(new ToolDispatchResult("Succeeded", """{"ok":true}""", null)),
            new TestClock(),
            new CopilotAgentEngineOptions(
                runtimeDirectory,
                new CopilotProviderOptions("fixture-model", new Uri(localBaseUrl)),
                new CopilotProviderOptions("fixture-model", new Uri(cloudBaseUrl))));

    private static AgentTurnRequest CreateRequest(
        TurnId turnId,
        ProviderKind provider,
        string marker,
        IReadOnlyList<AgentToolDefinition>? tools = null,
        TimeSpan? deadline = null,
        CancellationToken deadlineCancellationToken = default) =>
        new(
            turnId,
            provider,
            "Use only the selected context and registered tools.",
            new ContextPacket(
                $"packet-{marker}",
                "policy-test",
                [new ContextItem("fixture", marker, "LocalOnly", "user")],
                new ContextEstimate(0, 0, 0, 0, 0, 0, 0, 0, 8192, 0, false, false, "fixture estimate")),
            tools ?? [],
            deadline ?? TimeSpan.FromSeconds(60),
            1,
            DeadlineCancellationToken: deadlineCancellationToken);

    private static async Task<SqliteDatabase> CreateDatabaseAsync(string path)
    {
        var database = new SqliteDatabase(path);
        await database.InitializeAsync();
        return database;
    }

    private static async Task<TurnId> CreateTurnAsync(IConversationStore store)
    {
        var turnId = TurnId.New();
        await store.CreateTurnAsync(turnId, ConversationId.New(), TurnStatus.Received, Now, CancellationToken.None);
        return turnId;
    }

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var item in events)
        {
            collected.Add(item);
        }

        return collected;
    }

    private static HashSet<int> GetProcessIds()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static async Task<Process> FindNewRuntimeProcessAsync(HashSet<int> before)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var listing = Process.Start(new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = "-axo pid=,ppid=,comm=",
                RedirectStandardOutput = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Could not inspect the isolated Copilot runtime process.");
            var output = await listing.StandardOutput.ReadToEndAsync();
            await listing.WaitForExitAsync();
            var owned = output.Split('\n')
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3 &&
                    parts[1] == Environment.ProcessId.ToString() &&
                    int.TryParse(parts[0], out var processId) &&
                    !before.Contains(processId) &&
                    (parts[2].Contains("copilot", StringComparison.OrdinalIgnoreCase) ||
                     parts[2].EndsWith("runtime.node", StringComparison.OrdinalIgnoreCase)))
                .Select(parts => int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            if (owned.Length == 1)
            {
                return Process.GetProcessById(owned[0]);
            }

            if (owned.Length > 1)
            {
                throw new InvalidOperationException("The isolated Copilot runtime child could not be identified uniquely.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The isolated Copilot runtime process did not appear.");
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingSecretResolver(string secret) : ISecretResolver
    {
        public SecretReference? ResolvedReference { get; private set; }

        public ValueTask<ReadOnlyMemory<char>> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedReference = reference;
            return ValueTask.FromResult<ReadOnlyMemory<char>>(secret.AsMemory());
        }
    }

    private sealed class ControlledTurnEventStore(
        IConversationStore inner,
        bool blockFirstAppendUntilCancelled = false,
        bool blockBudgetRejectionUntilCancelled = false)
        : IConversationStore, IAtomicTurnOutcomeStore
    {
        private int shouldFail = 1;
        public TaskCompletionSource BudgetRejectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BudgetRejectionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ConversationRecord> CreateConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken) =>
            inner.CreateConversationAsync(conversation, cancellationToken);

        public ValueTask<ConversationRecord?> GetConversationAsync(
            ConversationId conversationId,
            string ownerId,
            CancellationToken cancellationToken) =>
            inner.GetConversationAsync(conversationId, ownerId, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationRecord>> ReadRecentConversationsAsync(
            string ownerId,
            int maximumConversations,
            CancellationToken cancellationToken) =>
            inner.ReadRecentConversationsAsync(ownerId, maximumConversations, cancellationToken);

        public ValueTask<ConversationMessage> AppendMessageAsync(
            ConversationMessage message,
            CancellationToken cancellationToken) =>
            inner.AppendMessageAsync(message, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationMessage>> ReadRecentAsync(
            ConversationId conversationId,
            int maximumMessages,
            CancellationToken cancellationToken,
            Guid? currentTaskMessageId = null) =>
            inner.ReadRecentAsync(conversationId, maximumMessages, cancellationToken, currentTaskMessageId);

        public ValueTask<SubmittedConversationTurn> SubmitTurnAsync(
            ConversationTurnSubmission submission,
            CancellationToken cancellationToken) =>
            inner.SubmitTurnAsync(submission, cancellationToken);

        public ValueTask<SubmittedConversationTurn?> FindSubmittedTurnAsync(
            ConversationId conversationId,
            string clientRequestId,
            string requestFingerprint,
            CancellationToken cancellationToken,
            string? requiredOwnerId = null) =>
            inner.FindSubmittedTurnAsync(conversationId, clientRequestId, requestFingerprint, cancellationToken, requiredOwnerId);

        public ValueTask<ConversationTurn> CreateTurnAsync(
            TurnId turnId,
            ConversationId conversationId,
            TurnStatus status,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken) =>
            inner.CreateTurnAsync(turnId, conversationId, status, createdAtUtc, cancellationToken);

        public ValueTask<ConversationTurn?> GetTurnAsync(TurnId turnId, CancellationToken cancellationToken) =>
            inner.GetTurnAsync(turnId, cancellationToken);

        public ValueTask<ConversationTurn?> GetTurnForOwnerAsync(
            TurnId turnId,
            string ownerId,
            CancellationToken cancellationToken) =>
            inner.GetTurnForOwnerAsync(turnId, ownerId, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationTurn>> ReadRecentTurnsForOwnerAsync(
            string ownerId,
            int maximumTurns,
            CancellationToken cancellationToken) =>
            inner.ReadRecentTurnsForOwnerAsync(ownerId, maximumTurns, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationTurn>> ReadNonterminalTurnsAsync(
            int maximumTurns,
            CancellationToken cancellationToken) =>
            inner.ReadNonterminalTurnsAsync(maximumTurns, cancellationToken);

        public ValueTask<ConversationTurn> UpdateTurnStatusAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            TurnStatusUpdateStarted?.TrySetResult();
            return inner.UpdateTurnStatusAsync(turnId, status, expectedVersion, updatedAtUtc, cancellationToken);
        }

        public ValueTask<PersistedTurnEvent> TransitionTurnAndAppendEventAsync(
            TurnId turnId,
            TurnStatus status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            inner.TransitionTurnAndAppendEventAsync(
                turnId,
                status,
                expectedVersion,
                updatedAtUtc,
                eventType,
                payloadJson,
                occurredAtUtc,
                cancellationToken);

        public TaskCompletionSource? TurnStatusUpdateStarted { get; set; }
        public TaskCompletionSource? ClaimCancellationOutcomeStarted { get; set; }
        public TaskCompletionSource? AllowClaimCancellationOutcome { get; set; }

        public ValueTask<PersistedTurnEvent> AppendTurnEventAsync(
            TurnId turnId,
            string eventType,
            string payloadJson,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            if (blockBudgetRejectionUntilCancelled)
            {
                if (eventType == nameof(ToolCompleted) &&
                    System.Text.Json.JsonSerializer.Deserialize<ToolCompleted>(payloadJson)!.Outcome == "Rejected")
                {
                    BudgetRejectionStarted.TrySetResult();
                    return WaitForBudgetCancellationAsync(cancellationToken);
                }
                return inner.AppendTurnEventAsync(turnId, eventType, payloadJson, occurredAtUtc, cancellationToken);
            }
            if (Interlocked.Exchange(ref shouldFail, 0) == 1)
            {
                if (blockFirstAppendUntilCancelled)
                {
                    return WaitForCancellationAsync(cancellationToken);
                }

                throw new IOException("Injected nonterminal event persistence failure.");
            }

            return inner.AppendTurnEventAsync(turnId, eventType, payloadJson, occurredAtUtc, cancellationToken);
        }

        private async ValueTask<PersistedTurnEvent> WaitForBudgetCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await WaitForCancellationAsync(cancellationToken);
            }
            finally
            {
                BudgetRejectionCancelled.TrySetResult();
            }
        }

        private static async ValueTask<PersistedTurnEvent> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled event append was expected to be cancelled.");
        }

        public ValueTask<IReadOnlyList<PersistedTurnEvent>> ReadTurnEventsAfterAsync(
            TurnId turnId,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken) =>
            inner.ReadTurnEventsAfterAsync(turnId, afterSequence, maximumEvents, cancellationToken);

        public async ValueTask<PersistedTurnEvent> UpdateTurnStatusAndAppendEventAsync(
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
            if (eventType == nameof(TurnCancelled) &&
                ClaimCancellationOutcomeStarted is { } started &&
                AllowClaimCancellationOutcome is { } allow)
            {
                started.TrySetResult();
                await allow.Task.WaitAsync(cancellationToken);
            }

            return await ((IAtomicTurnOutcomeStore)inner).UpdateTurnStatusAndAppendEventAsync(
                turnId,
                status,
                expectedVersion,
                updatedAtUtc,
                eventType,
                payloadJson,
                occurredAtUtc,
                cancellationToken,
                finalAssistantMessage);
        }
    }

    private sealed class RecordingDispatcher(ToolDispatchResult result) : IToolDispatcher
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public string? LastArguments { get; private set; }

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            LastArguments = request.ArgumentsJson;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingDispatcher : IToolDispatcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolDispatchResult("Succeeded", "{}", null);
        }
    }
}
