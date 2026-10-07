using System.Runtime.CompilerServices;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Context;
using PersonalAgent.Application.Routing;
using PersonalAgent.Application.TurnCoordination;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

[Collection("Copilot runtime process isolation")]
public sealed class LocalTurnCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CoordinatorPersistsOneCompletedAnswerAndReturnsDuplicateRequest()
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var engine = new ControlledAgentEngine(store, clock);
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));
        await coordinator.Coordinator.StartAsync(CancellationToken.None);

        var request = new LocalTurnRequest(
            ConversationId.New(),
            "client-request-1",
            "Hello",
            RoutingTaskKind.TextConversation);
        var accepted = await coordinator.Coordinator.SubmitAsync(request, CancellationToken.None);
        await engine.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await coordinator.Coordinator.SubmitAsync(request, CancellationToken.None);

        Assert.False(accepted.IsDuplicate);
        Assert.True(retry.IsDuplicate);
        Assert.Equal(accepted.Turn.Id, retry.Turn.Id);
        Assert.Equal(accepted.UserMessageId, retry.UserMessageId);
        var messages = await store.ReadRecentAsync(request.ConversationId, 10, CancellationToken.None);
        Assert.Equal(["user", "assistant"], messages.Select(message => message.Role));
        Assert.Equal(1, messages.Count(message => message.Role == "assistant"));
        var events = await coordinator.Coordinator.ReadEventsAfterAsync(
            accepted.Turn.Id,
            0,
            100,
            CancellationToken.None);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Sequence));
        Assert.Equal(1, events.Count(item => item.EventType == nameof(TurnCompleted)));
        Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ContextExcludesLaterSubmissionsButIncludesLateAnswerFromPrecedingTurn()
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var conversation = ConversationId.New();
        var preceding = await SubmitStoredAsync("preceding", "Earlier question");
        var current = await SubmitStoredAsync("current", "Current question");
        for (var index = 0; index < 40; index++)
        {
            await SubmitStoredAsync($"later-{index}", $"Future question {index}");
        }

        var completed = new TurnCompleted(preceding.Turn.Id, Now);
        await store.UpdateTurnStatusAndAppendEventAsync(
            preceding.Turn.Id,
            TurnStatus.Completed,
            preceding.Turn.Version,
            Now,
            nameof(TurnCompleted),
            JsonSerializer.Serialize(completed),
            Now,
            CancellationToken.None,
            new ConversationMessage(Guid.NewGuid(), conversation, "assistant", "Earlier answer appended late", Now));
        var builder = new ConversationContextBuilder(store);
        var context = await builder.BuildAsync(
            new ContextBuildRequest(conversation, ProviderKind.Local, "Current question", "Local instructions",
                [], current.UserMessageId),
            CancellationToken.None);

        Assert.Equal(["Earlier question", "Earlier answer appended late", "Current question"],
            context.Items.Select(item => item.Text));
        Assert.False(context.Estimate.HasMoreHistory);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadRecentAsync(conversation, 10, CancellationToken.None, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadRecentAsync(ConversationId.New(), 10, CancellationToken.None, current.UserMessageId));
        Assert.Equal("Earlier answer appended late",
            Assert.Single(await store.ReadRecentAsync(conversation, 1, CancellationToken.None, current.UserMessageId)).Content);
        Assert.Equal(["Future question 39", "Earlier answer appended late"],
            (await store.ReadRecentAsync(conversation, 2, CancellationToken.None)).Select(message => message.Content));

        await using var connection = await database.OpenConnectionAsync();
        await using var plan = connection.CreateCommand();
        var sql = (string)typeof(SqliteConversationStore)
            .GetField("RecentHistorySql", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        plan.CommandText = "EXPLAIN QUERY PLAN " + sql;
        plan.Parameters.AddWithValue("$conversation_id", conversation.Value.ToString("D"));
        plan.Parameters.AddWithValue("$maximum", 2);
        var details = new List<string>();
        await using (var reader = await plan.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }
        }

        Assert.Contains(details, detail => detail.Contains("USING INDEX sqlite_autoindex_messages", StringComparison.Ordinal));
        // Only the bounded outer page may sort; the indexed inner read must apply LIMIT first.
        Assert.Single(details, detail => detail.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));

        ValueTask<SubmittedConversationTurn> SubmitStoredAsync(string id, string text) =>
            store.SubmitTurnAsync(
                new ConversationTurnSubmission(TurnId.New(), conversation, id, new string('A', 64), Guid.NewGuid(), text, Now),
                CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartupInterruptsRecoveredTurnWithoutCallingInferenceAgain()
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var turn = await store.CreateTurnAsync(
            TurnId.New(),
            ConversationId.New(),
            TurnStatus.Received,
            Now,
            CancellationToken.None);
        var engine = new ControlledAgentEngine(store, clock);
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));

        await coordinator.Coordinator.StartAsync(CancellationToken.None);

        var recovered = await store.GetTurnAsync(turn.Id, CancellationToken.None);
        var events = await store.ReadTurnEventsAfterAsync(turn.Id, 0, 10, CancellationToken.None);
        Assert.Equal(TurnStatus.Interrupted, recovered!.Status);
        Assert.Equal(0, engine.ExecutionCount);
        Assert.Contains(events, item => item.EventType == nameof(TurnInterrupted));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CoordinatorRejectsTwentyFirstQueuedTurnWithoutPersistingIt()
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var engine = new ControlledAgentEngine(store, clock, block: true);
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));
        await coordinator.Coordinator.StartAsync(CancellationToken.None);

        for (var index = 0; index < 4; index++)
        {
            await coordinator.Coordinator.SubmitAsync(
                new LocalTurnRequest(ConversationId.New(), $"active-{index}", $"active {index}", RoutingTaskKind.TextConversation),
                CancellationToken.None);
        }

        var started = await Task.WhenAny(
            engine.FourExecutionsStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));
        if (started != engine.FourExecutionsStarted.Task)
        {
            var nonterminal = await store.ReadNonterminalTurnsAsync(100, CancellationToken.None);
            var details = new List<string>();
            foreach (var turn in nonterminal)
            {
                var turnEvents = await store.ReadTurnEventsAfterAsync(turn.Id, 0, 10, CancellationToken.None);
                details.Add($"{turn.Status}:{string.Join(',', turnEvents.Select(item => item.PayloadJson))}");
            }

            Assert.Fail($"Expected four blocked turns to start; observed {engine.ExecutionCount}; {string.Join(" | ", details)}");
        }

        for (var index = 0; index < 20; index++)
        {
            await coordinator.Coordinator.SubmitAsync(
                new LocalTurnRequest(ConversationId.New(), $"queued-{index}", $"queued {index}", RoutingTaskKind.TextConversation),
                CancellationToken.None);
        }

        await Assert.ThrowsAsync<TurnQueueFullException>(async () =>
            await coordinator.Coordinator.SubmitAsync(
                new LocalTurnRequest(ConversationId.New(), "overflow", "overflow", RoutingTaskKind.TextConversation),
                CancellationToken.None));
        await using (var connection = await database.OpenConnectionAsync())
        await using (var messages = connection.CreateCommand())
        {
            messages.CommandText = "SELECT COUNT(*) FROM messages;";
            Assert.Equal(24L, (long)(await messages.ExecuteScalarAsync())!);
        }

        await coordinator.Coordinator.StopAsync(CancellationToken.None);
        var remaining = await store.ReadNonterminalTurnsAsync(100, CancellationToken.None);
        Assert.Empty(remaining);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AmbiguousLocalRequestPersistsClarificationWithoutInvokingTheEngine()
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var engine = new ControlledAgentEngine(store, clock);
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));
        await coordinator.Coordinator.StartAsync(CancellationToken.None);

        var submitted = await coordinator.Coordinator.SubmitAsync(
            new LocalTurnRequest(ConversationId.New(), "ambiguous", "Do it", RoutingTaskKind.Ambiguous),
            CancellationToken.None);
        await WaitForTerminalAsync(store, submitted.Turn.Id);

        Assert.Equal(0, engine.ExecutionCount);
        Assert.Equal(TurnStatus.Failed, (await store.GetTurnAsync(submitted.Turn.Id, CancellationToken.None))!.Status);
        var events = await store.ReadTurnEventsAfterAsync(submitted.Turn.Id, 0, 10, CancellationToken.None);
        Assert.Contains(events, item => item.EventType == nameof(TurnClarificationRequired));
        var messages = await store.ReadRecentAsync(submitted.Turn.ConversationId, 10, CancellationToken.None);
        Assert.Contains(messages, message =>
            message.Role == "assistant" &&
            message.Content == "Please clarify what supported local task you want completed.");
    }

    private static LocalTurnCoordinator CreateCoordinator(
        SqliteConversationStore store,
        IAgentEngine engine,
        IClock clock) =>
        new(
            store,
            store,
            new LocalOnlyModelRouter(),
            new ConversationContextBuilder(store),
            engine,
            clock,
            new LocalTurnCoordinatorOptions(
                TimeSpan.FromSeconds(120),
                TimeSpan.FromSeconds(300)),
            "Use the local model only.");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ProductionEngineDeadlineUsesFirstCauseWhileWinningSignalPublicationIsPaused(bool hostShutdown)
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        var secrets = new BlockingSecretResolver();
        await using var provider = await FakeOpenAiProvider.StartAsync();
        var engine = new CopilotAgentEngine(store, new RejectingToolDispatcher(), clock,
            new CopilotAgentEngineOptions(Path.Combine(directory.Path, "runtime"),
                new CopilotProviderOptions("fixture-model", new Uri(provider.BaseUrl),
                    ApiKeyReference: new SecretReference("controlled-fixture")), null),
            secrets);
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));
        await coordinator.Coordinator.StartAsync(CancellationToken.None);
        var accepted = await coordinator.Coordinator.SubmitAsync(new LocalTurnRequest(
            ConversationId.New(), "owner-before-engine-deadline", "Hello", RoutingTaskKind.TextConversation,
            TimeSpan.FromSeconds(2)), CancellationToken.None);
        await secrets.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!(await store.ReadTurnEventsAfterAsync(accepted.Turn.Id, 0, 100, wait.Token))
            .Any(item => item.EventType == nameof(RouteSelected)))
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var active = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField("active", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator.Coordinator)!;
        var work = active[accepted.Turn.Id]!;
        var item = work.GetType().GetProperty("Item")!.GetValue(work)!;
        var winningSource = (CancellationTokenSource)item.GetType()
            .GetProperty(hostShutdown ? "ShutdownCancellation" : "Cancellation")!.GetValue(item)!;
        using var release = new ManualResetEventSlim();
        var publicationPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pause = winningSource.Token.Register(() =>
        {
            publicationPaused.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Winning cancellation publication was not released.");
            }
        });
        var cancelling = Task.Run(async () =>
        {
            if (hostShutdown)
            {
                await coordinator.Coordinator.StopAsync(CancellationToken.None);
            }
            else
            {
                await coordinator.Coordinator.CancelAsync(accepted.Turn.Id, CancellationToken.None);
            }
        });
        try
        {
            await publicationPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The production adapter's independent deadline must resolve through the host winner.
            await WaitForTerminalAsync(store, accepted.Turn.Id);
            Assert.Equal(hostShutdown ? TurnStatus.Interrupted : TurnStatus.Cancelled,
                (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
        }
        finally
        {
            release.Set();
        }

        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
        var events = await store.ReadTurnEventsAfterAsync(accepted.Turn.Id, 0, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == (hostShutdown ? nameof(TurnInterrupted) : nameof(TurnCancelled)));
        Assert.DoesNotContain(events, item => item.EventType == (hostShutdown ? nameof(TurnCancelled) : nameof(TurnInterrupted)));
        Assert.Equal(0, provider.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task SuccessfulActualRuntimeResponsePreservesSelectedCauseBeforePublication(bool? hostShutdown)
    {
        using var directory = IsolatedDirectory.Create();
        var clock = new MutableClock(Now);
        var database = new SqliteDatabase(Path.Combine(directory.Path, "jarvis.db"));
        await database.InitializeAsync();
        var store = new SqliteConversationStore(database, clock);
        await using var provider = await FakeOpenAiProvider.StartAsync();
        provider.PauseInference = true;
        var engine = new CopilotAgentEngine(store, new RejectingToolDispatcher(), clock,
            new CopilotAgentEngineOptions(Path.Combine(directory.Path, "runtime"),
                new CopilotProviderOptions("fixture-model", new Uri(provider.BaseUrl)), null));
        await using var coordinator = new CoordinatorScope(CreateCoordinator(store, engine, clock));
        await coordinator.Coordinator.StartAsync(CancellationToken.None);
        var conversation = ConversationId.New();
        var accepted = await coordinator.Coordinator.SubmitAsync(new LocalTurnRequest(
            conversation, "completion-before-publication", "Hello", RoutingTaskKind.TextConversation),
            CancellationToken.None);
        await provider.InferenceStalled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (hostShutdown is null)
        {
            provider.ContinueInference.TrySetResult();
            await WaitForTerminalAsync(store, accepted.Turn.Id);
            Assert.Equal(TurnStatus.Completed, (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
            Assert.Single(await store.ReadRecentAsync(conversation, 10, CancellationToken.None),
                message => message.Role == "assistant");
            Assert.Equal(1, provider.RequestCount);
            await coordinator.Coordinator.StopAsync(CancellationToken.None);
            return;
        }
        var active = (System.Collections.IDictionary)typeof(LocalTurnCoordinator)
            .GetField("active", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator.Coordinator)!;
        var work = active[accepted.Turn.Id]!;
        var item = work.GetType().GetProperty("Item")!.GetValue(work)!;
        var source = (CancellationTokenSource)item.GetType()
            .GetProperty(hostShutdown.Value ? "ShutdownCancellation" : "Cancellation")!.GetValue(item)!;
        using var release = new ManualResetEventSlim();
        var publicationPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pause = source.Token.Register(() =>
        {
            publicationPaused.TrySetResult();
            release.Wait();
        });
        var cancelling = hostShutdown.Value
            ? coordinator.Coordinator.StopAsync(CancellationToken.None)
            : coordinator.Coordinator.CancelAsync(accepted.Turn.Id, CancellationToken.None).AsTask();
        try
        {
            await publicationPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.ContinueInference.TrySetResult();
            await provider.StreamingResponseWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForTerminalAsync(store, accepted.Turn.Id);
            Assert.False(cancelling.IsCompleted);
            Assert.Equal(hostShutdown.Value ? TurnStatus.Interrupted : TurnStatus.Cancelled,
                (await store.GetTurnAsync(accepted.Turn.Id, CancellationToken.None))!.Status);
            var events = await store.ReadTurnEventsAfterAsync(accepted.Turn.Id, 0, 100, CancellationToken.None);
            var terminal = Assert.Single(events,
                item => item.EventType is nameof(TurnInterrupted) or nameof(TurnCancelled));
            if (hostShutdown.Value)
            {
                Assert.Equal("host_shutdown", JsonSerializer.Deserialize<TurnInterrupted>(terminal.PayloadJson)!.ReasonCode);
            }
            Assert.DoesNotContain(events, item => item.EventType == nameof(TurnCompleted));
            Assert.DoesNotContain(await store.ReadRecentAsync(conversation, 10, CancellationToken.None),
                message => message.Role == "assistant");
            Assert.Equal(1, provider.RequestCount);
        }
        finally
        {
            provider.ContinueInference.TrySetResult();
            release.Set();
        }
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingSecretResolver : ISecretResolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<char>> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled secret lookup must end through cancellation.");
        }
    }

    private static async Task WaitForTerminalAsync(IConversationStore store, TurnId turnId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class CoordinatorScope(ILocalTurnCoordinator coordinator) : IAsyncDisposable
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

    private sealed class ControlledAgentEngine(
        IConversationStore conversations,
        IClock clock,
        bool block = false) : IAgentEngine
    {
        private int executionCount;

        public int ExecutionCount => Volatile.Read(ref executionCount);
        public TaskCompletionSource FourExecutionsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref executionCount);
            if (count >= 4)
            {
                FourExecutionsStarted.TrySetResult();
            }

            var current = await conversations.GetTurnAsync(request.TurnId, cancellationToken)
                ?? throw new InvalidOperationException("Test turn was not persisted.");
            await conversations.UpdateTurnStatusAsync(
                request.TurnId,
                TurnStatus.Running,
                current.Version,
                clock.UtcNow,
                cancellationToken);

            if (block)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    request.HostShutdownToken,
                    request.DeadlineCancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                yield break;
            }

            var started = new TurnStarted(request.TurnId, clock.UtcNow);
            await conversations.AppendTurnEventAsync(
                request.TurnId,
                nameof(TurnStarted),
                JsonSerializer.Serialize(started),
                started.OccurredAtUtc,
                cancellationToken);
            yield return started;

            var route = new RouteSelected(
                request.TurnId,
                clock.UtcNow,
                ProviderKind.Local,
                request.RouteReasonCode);
            await conversations.AppendTurnEventAsync(
                request.TurnId,
                nameof(RouteSelected),
                JsonSerializer.Serialize(route),
                route.OccurredAtUtc,
                cancellationToken);
            yield return route;

            var completed = new TurnCompleted(request.TurnId, clock.UtcNow);
            var running = await conversations.GetTurnAsync(request.TurnId, cancellationToken)
                ?? throw new InvalidOperationException("Test turn was not persisted.");
            await ((IAtomicTurnOutcomeStore)conversations).UpdateTurnStatusAndAppendEventAsync(
                request.TurnId,
                TurnStatus.Completed,
                running.Version,
                clock.UtcNow,
                nameof(TurnCompleted),
                JsonSerializer.Serialize(completed),
                completed.OccurredAtUtc,
                cancellationToken,
                new ConversationMessage(
                    Guid.NewGuid(),
                    running.ConversationId,
                    "assistant",
                    "Hello there.",
                    clock.UtcNow));
            Completed.TrySetResult();
            yield return completed;
        }

        public ValueTask CancelAsync(TurnId turnId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(TurnId turnId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
