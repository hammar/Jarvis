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
        TimeSpan? deadline = null) =>
        new(
            turnId,
            provider,
            "Use only the selected context and registered tools.",
            new ContextPacket($"packet-{marker}", "policy-test", [new ContextItem("fixture", marker, "Personal")]),
            tools ?? [],
            deadline ?? TimeSpan.FromSeconds(60),
            1);

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
