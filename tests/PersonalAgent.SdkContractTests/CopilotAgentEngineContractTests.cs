using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.AgentEngine.Copilot;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.SdkContractTests;

[Collection("CopilotRuntimeContracts")]
public sealed class CopilotAgentEngineContractTests
{
    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task AdapterStreamsExplicitRoutesAndDispatchesOnlyTheRegisteredTool()
    {
        await using var localProvider = await ControlledOpenAiProvider.StartAsync();
        await using var cloudProvider = await ControlledOpenAiProvider.StartAsync();
        using var directory = IsolatedDirectory.Create();
        var dispatcher = new CapturingDispatcher((request, _) =>
        {
            Assert.Equal("lookup", request.ToolName);
            using var arguments = JsonDocument.Parse(request.ArgumentsJson);
            Assert.Equal("fixture-key", arguments.RootElement.GetProperty("key").GetString());
            return ValueTask.FromResult(new ToolDispatchResult("Succeeded", """{"value":"fixture-result"}""", null));
        });
        var secrets = new FixtureSecretResolver();
        await using var engine = CreateEngine(directory.Path, localProvider, cloudProvider, dispatcher, secrets);
        var request = CreateRequest(
            ProviderKind.Local,
            [new AgentToolDefinition(
                "lookup",
                """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}""",
                true)],
            "local-context-marker");

        var localEvents = await CollectAsync(engine.RunTurnAsync(request, CancellationToken.None));
        var cloudEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(ProviderKind.Cloud, [], "cloud-context-marker"),
            CancellationToken.None));

        Assert.Single(dispatcher.Calls);
        Assert.Equal(request.TurnId, dispatcher.Calls.Single().TurnId);
        Assert.Single(localEvents.OfType<TurnCompleted>());
        Assert.Single(cloudEvents.OfType<TurnCompleted>());
        Assert.Equal("streamed answer", string.Concat(localEvents.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Equal("streamed answer", string.Concat(cloudEvents.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Equal(
            Enumerable.Range(1, localEvents.Count).Select(value => (long)value),
            localEvents.Select(item => item.SequenceNumber));
        Assert.Equal(
            Enumerable.Range(1, cloudEvents.Count).Select(value => (long)value),
            cloudEvents.Select(item => item.SequenceNumber));

        var localBodies = localProvider.CapturedRequests.Select(item => item.Body).ToArray();
        var cloudBodies = cloudProvider.CapturedRequests.Select(item => item.Body).ToArray();
        Assert.Equal(2, localBodies.Length);
        Assert.Single(cloudBodies);
        Assert.All(localBodies, body => Assert.Contains("local-context-marker", body, StringComparison.Ordinal));
        Assert.All(localBodies, body => Assert.DoesNotContain("cloud-context-marker", body, StringComparison.Ordinal));
        Assert.Contains("lookup", localBodies[0], StringComparison.Ordinal);
        Assert.All(cloudBodies, body => Assert.Contains("cloud-context-marker", body, StringComparison.Ordinal));
        Assert.All(cloudBodies, body => Assert.DoesNotContain("local-context-marker", body, StringComparison.Ordinal));
        Assert.Contains("fixture-cloud-key", cloudProvider.CapturedRequests.Single().Authorization);
        Assert.Single(secrets.ResolvedReferences);
        Assert.Equal("cloud-api-key", secrets.ResolvedReferences.Single().Name);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
        await engine.CancelAsync(TurnId.New(), CancellationToken.None);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task AdapterValidatesProviderAndTurnInputsBeforeInference()
    {
        using var directory = IsolatedDirectory.Create();
        var dispatcher = new CapturingDispatcher((_, _) =>
            ValueTask.FromResult(new ToolDispatchResult("Succeeded", null, null)));
        var secrets = new FixtureSecretResolver();
        var local = new CopilotProviderSettings(new Uri("http://127.0.0.1:11434/v1"), "fixture-model");
        var cloud = new CopilotProviderSettings(
            new Uri("https://provider.example/v1"),
            "fixture-model",
            new SecretReference("cloud-api-key"));
        var unsafeOptions = new[]
        {
            new CopilotAgentEngineOptions(" ", local, cloud),
            new CopilotAgentEngineOptions(directory.Path, null!, cloud),
            new CopilotAgentEngineOptions(directory.Path, new CopilotProviderSettings(new Uri("http://192.0.2.3/v1"), "fixture-model"), cloud),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("http://provider.example/v1"), "fixture-model", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("https://provider.example/v1"), "fixture-model")),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("https://fixture-user@provider.example/v1"), "fixture-model", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("https://provider.example/v1?key=secret"), "fixture-model", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("ftp://provider.example/v1"), "fixture-model", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("/relative"), "fixture-model", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("https://provider.example/v1"), " ", new SecretReference("cloud-api-key"))),
            new CopilotAgentEngineOptions(directory.Path, local, new CopilotProviderSettings(new Uri("https://provider.example/v1"), "fixture-model", new SecretReference(" "))),
        };
        foreach (var options in unsafeOptions)
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                new CopilotAgentEngine(dispatcher, secrets, new FixtureClock(), options));
        }

        await using var provider = await ControlledOpenAiProvider.StartAsync();
        var validOptions = new CopilotAgentEngineOptions(
            Path.Combine(directory.Path, "runtime"),
            new CopilotProviderSettings(new Uri(provider.BaseUrl), "fixture-model"),
            new CopilotProviderSettings(new Uri(provider.BaseUrl), "fixture-model", new SecretReference("cloud-api-key")));
        await using var engine = new CopilotAgentEngine(dispatcher, secrets, new FixtureClock(), validOptions);
        var valid = CreateRequest(ProviderKind.Local, [], "validation-marker");
        var invalidRequests = new[]
        {
            valid with { TurnId = default },
            valid with { Instructions = " " },
            valid with { Deadline = TimeSpan.Zero },
            valid with { MaximumToolCalls = -1 },
            valid with { Provider = (ProviderKind)99 },
            valid with { Context = null! },
            valid with { Context = valid.Context with { Items = null! } },
            valid with { Tools = null! },
            valid with { Tools = [new AgentToolDefinition(" ", "{}", true)] },
            valid with { Tools = [new AgentToolDefinition("lookup", " ", true)] },
            valid with
            {
                Tools =
                [
                    new AgentToolDefinition("lookup", "{}", true),
                    new AgentToolDefinition("lookup", "{}", false),
                ],
            },
            valid with { Tools = [new AgentToolDefinition("lookup", "[]", true)] },
            valid with { Context = valid.Context with { Items = [new ContextItem("source", new string('x', 100_000), "Personal")] } },
        };
        foreach (var invalidRequest in invalidRequests)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await CollectAsync(engine.RunTurnAsync(invalidRequest, CancellationToken.None)));
        }

        await Assert.ThrowsAnyAsync<JsonException>(async () =>
            await CollectAsync(engine.RunTurnAsync(
                valid with { Tools = [new AgentToolDefinition("lookup", "not-json", true)] },
                CancellationToken.None)));
        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await CollectAsync(engine.RunTurnAsync(valid, CancellationToken.None)));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task ProviderFailureAndNonStreamingFinalTextProduceCleanOutcomes()
    {
        await using var provider = await ControlledOpenAiProvider.StartAsync();
        using var directory = IsolatedDirectory.Create();
        provider.StreamResponses = false;
        var dispatcher = new CapturingDispatcher((_, _) =>
            ValueTask.FromResult(new ToolDispatchResult("Succeeded", null, null)));
        await using var engine = CreateEngine(
            directory.Path,
            provider,
            provider,
            dispatcher,
            new FixtureSecretResolver());

        var success = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(ProviderKind.Local, [], "non-streaming-marker"),
            CancellationToken.None));
        provider.FailInference = true;
        var failure = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(ProviderKind.Local, [], "provider-failure-marker"),
            CancellationToken.None));

        Assert.Equal("non-streamed answer", string.Concat(success.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Single(success.OfType<TurnCompleted>());
        Assert.Equal("engine_failure", Assert.Single(failure.OfType<TurnFailed>()).ReasonCode);
        Assert.Equal(1, failure.Count(item =>
            item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task DisposingTheEngineCancelsAndReleasesAnActiveTurn()
    {
        await using var provider = await ControlledOpenAiProvider.StartAsync();
        using var directory = IsolatedDirectory.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CapturingDispatcher(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolDispatchResult("Succeeded", null, null);
        });
        await using var engine = CreateEngine(
            directory.Path,
            provider,
            provider,
            dispatcher,
            new FixtureSecretResolver());
        var events = new ConcurrentQueue<AgentEvent>();
        var activeTurn = ReadEventsAsync(
            engine.RunTurnAsync(
                CreateRequest(
                    ProviderKind.Local,
                    [new AgentToolDefinition("lookup", """{"type":"object","properties":{"key":{"type":"string"}}}""", true)],
                    "dispose-marker"),
                CancellationToken.None),
            events);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await engine.DisposeAsync();
        await activeTurn.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(events.OfType<TurnCancelled>());
        Assert.Equal(1, events.Count(item =>
            item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
        await engine.DisposeAsync();
    }

    [CollectionDefinition("CopilotRuntimeContracts", DisableParallelization = true)]
    public sealed class CopilotRuntimeContractCollection
    {
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task CancellationStopsTheRuntimeAndANewTurnStartsWithoutDuplicateTerminalEvents()
    {
        await using var provider = await ControlledOpenAiProvider.StartAsync();
        using var directory = IsolatedDirectory.Create();
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CapturingDispatcher(async (_, cancellationToken) =>
        {
            dispatchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolDispatchResult("Succeeded", null, null);
        });
        await using var engine = CreateEngine(
            directory.Path,
            provider,
            provider,
            dispatcher,
            new FixtureSecretResolver());
        var cancelledTurnId = TurnId.New();
        var cancelledRequest = CreateRequest(
            ProviderKind.Local,
            [new AgentToolDefinition("lookup", """{"type":"object","properties":{"key":{"type":"string"}}}""", true)],
            "cancel-marker",
            cancelledTurnId);
        var canceledEvents = new ConcurrentQueue<AgentEvent>();
        var streaming = ReadEventsAsync(
            engine.RunTurnAsync(cancelledRequest, CancellationToken.None),
            canceledEvents);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(engine.RunTurnAsync(cancelledRequest, CancellationToken.None)));
        await engine.CancelAsync(cancelledTurnId, CancellationToken.None);
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        var restartedEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(ProviderKind.Local, [], "restart-marker"),
            CancellationToken.None));

        Assert.Single(canceledEvents.OfType<TurnCancelled>());
        Assert.Empty(canceledEvents.OfType<TurnCompleted>());
        Assert.Single(restartedEvents.OfType<TurnCompleted>());
        Assert.Equal(1, canceledEvents.Count(item =>
            item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted));
        Assert.Equal("streamed answer", string.Concat(restartedEvents.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Single(dispatcher.Calls);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task RuntimeProcessCrashIsInterruptedAndTheNextTurnUsesAFreshRuntime()
    {
        await using var provider = await ControlledOpenAiProvider.StartAsync();
        using var directory = IsolatedDirectory.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new CapturingDispatcher(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolDispatchResult("Succeeded", null, null);
        });
        await using var engine = CreateEngine(
            directory.Path,
            provider,
            provider,
            dispatcher,
            new FixtureSecretResolver());
        var existingProcessIds = Process.GetProcesses().Select(process =>
        {
            var id = process.Id;
            process.Dispose();
            return id;
        }).ToHashSet();
        var crashedEvents = new ConcurrentQueue<AgentEvent>();
        var request = CreateRequest(
            ProviderKind.Local,
            [new AgentToolDefinition("lookup", """{"type":"object","properties":{"key":{"type":"string"}}}""", true)],
            "crash-marker") with
        {
            Deadline = TimeSpan.FromSeconds(5),
        };
        var crashedTurn = ReadEventsAsync(engine.RunTurnAsync(request, CancellationToken.None), crashedEvents);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var runtimeId = await FindOwnedRuntimeProcessAsync(existingProcessIds);
        using (var runtime = Process.GetProcessById(runtimeId))
        {
            runtime.Kill();
            await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        await crashedTurn.WaitAsync(TimeSpan.FromSeconds(10));
        var recoveredEvents = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(ProviderKind.Local, [], "after-crash-marker"),
            CancellationToken.None));

        Assert.Single(crashedEvents.OfType<TurnInterrupted>());
        Assert.Equal(1, crashedEvents.Count(item =>
            item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted));
        Assert.Single(recoveredEvents.OfType<TurnCompleted>());
        Assert.Equal("streamed answer", string.Concat(recoveredEvents.OfType<TextDelta>().Select(item => item.Text)));
        Assert.Single(dispatcher.Calls);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
    }

    [Fact]
    [Trait("Category", "SdkContract")]
    [Trait("Category", "Integration")]
    public async Task HostToolBudgetStopsTheRuntimeBeforeAnExcessDispatch()
    {
        await using var provider = await ControlledOpenAiProvider.StartAsync();
        provider.ToolCallsBeforeCompletion = 2;
        using var directory = IsolatedDirectory.Create();
        var dispatcher = new CapturingDispatcher((_, _) =>
            ValueTask.FromResult(new ToolDispatchResult("Succeeded", """{"value":"fixture"}""", null)));
        await using var engine = CreateEngine(
            directory.Path,
            provider,
            provider,
            dispatcher,
            new FixtureSecretResolver());

        var events = await CollectAsync(engine.RunTurnAsync(
            CreateRequest(
                ProviderKind.Local,
                [new AgentToolDefinition("lookup", """{"type":"object","properties":{"key":{"type":"string"}}}""", true)],
                "budget-marker"),
            CancellationToken.None));

        var failure = Assert.Single(events.OfType<TurnFailed>());
        Assert.Equal("tool_budget_exceeded", failure.ReasonCode);
        Assert.Single(dispatcher.Calls);
        Assert.Equal(1, events.Count(item =>
            item is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "runtime")));
    }

    private static CopilotAgentEngine CreateEngine(
        string root,
        ControlledOpenAiProvider localProvider,
        ControlledOpenAiProvider cloudProvider,
        IToolDispatcher dispatcher,
        ISecretResolver secrets) =>
        new(
            dispatcher,
            secrets,
            new FixtureClock(),
            new CopilotAgentEngineOptions(
                Path.Combine(root, "runtime"),
                new CopilotProviderSettings(new Uri(localProvider.BaseUrl), "fixture-model"),
                new CopilotProviderSettings(
                    new Uri(cloudProvider.BaseUrl),
                    "fixture-model",
                    new SecretReference("cloud-api-key"))));

    private static AgentTurnRequest CreateRequest(
        ProviderKind provider,
        IReadOnlyList<AgentToolDefinition> tools,
        string context,
        TurnId? turnId = null) =>
        new(
            turnId ?? TurnId.New(),
            provider,
            $"Follow host instructions for {context}.",
            new ContextPacket(
                $"packet-{context}",
                "test-policy",
                [new ContextItem($"source-{context}", context, "Personal")]),
            tools,
            TimeSpan.FromSeconds(30),
            MaximumToolCalls: 1);

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var item in events)
        {
            collected.Add(item);
        }

        return collected;
    }

    private static async Task ReadEventsAsync(
        IAsyncEnumerable<AgentEvent> events,
        ConcurrentQueue<AgentEvent> collected)
    {
        await foreach (var item in events)
        {
            collected.Enqueue(item);
        }
    }

    private static async Task<int> FindOwnedRuntimeProcessAsync(HashSet<int> existingProcessIds)
    {
        using var process = Process.Start(new ProcessStartInfo("ps")
        {
            ArgumentList = { "-axo", "pid=,ppid=,comm=" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not inspect the Copilot runtime process.");
        var listing = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var owned = listing.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 &&
                            parts[1] == Environment.ProcessId.ToString() &&
                            int.TryParse(parts[0], out var id) &&
                            !existingProcessIds.Contains(id) &&
                            (parts[2].Contains("copilot", StringComparison.OrdinalIgnoreCase) ||
                             parts[2].EndsWith("runtime.node", StringComparison.Ordinal)))
            .Select(parts => int.Parse(parts[0]))
            .ToArray();
        return Assert.Single(owned);
    }

    private sealed class CapturingDispatcher(
        Func<ToolDispatchRequest, CancellationToken, ValueTask<ToolDispatchResult>> dispatch) : IToolDispatcher
    {
        public ConcurrentQueue<ToolDispatchRequest> Calls { get; } = new();

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Enqueue(request);
            return dispatch(request, cancellationToken);
        }
    }

    private sealed class FixtureSecretResolver : ISecretResolver
    {
        public ConcurrentQueue<SecretReference> ResolvedReferences { get; } = new();

        public ValueTask<ReadOnlyMemory<char>> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedReferences.Enqueue(reference);
            return ValueTask.FromResult<ReadOnlyMemory<char>>("fixture-cloud-key".ToCharArray());
        }
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
