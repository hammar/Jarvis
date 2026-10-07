using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Runs bounded application turns in disposable, explicitly configured Copilot runtimes.</summary>
/// <remarks>
/// Each turn receives a fresh SDK client, runtime directory, session, provider configuration, and exact
/// host-registered tool catalog. Runtime state is replaceable; turn status and ordered events are persisted
/// through application-owned contracts. The native runtime is a trusted dependency per ADR 0001; this class
/// does not claim OS-enforced network containment.
/// </remarks>
public sealed class CopilotAgentEngine : IAgentEngine
{
    private const int EventChannelCapacity = 128;
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TerminalPersistenceTimeout = TimeSpan.FromSeconds(5);
    private readonly IConversationStore conversations;
    private readonly IToolDispatcher dispatcher;
    private readonly IClock clock;
    private readonly CopilotAgentEngineOptions options;
    private readonly ISecretResolver? secretResolver;
    private readonly CopilotTurnStateMachine stateMachine;
    private readonly ConcurrentDictionary<TurnId, CopilotActiveTurn> activeTurns = new();

    /// <summary>Creates an engine using durable application event storage and explicit provider settings.</summary>
    /// <param name="conversations">Authoritative application turn and ordered-event store.</param>
    /// <param name="dispatcher">Host policy boundary for every model-proposed tool call.</param>
    /// <param name="clock">UTC clock for event timestamps.</param>
    /// <param name="options">Trusted runtime and provider configuration.</param>
    /// <param name="secretResolver">Optional server-side resolver for configured provider key references.</param>
    public CopilotAgentEngine(
        IConversationStore conversations,
        IToolDispatcher dispatcher,
        IClock clock,
        CopilotAgentEngineOptions options,
        ISecretResolver? secretResolver = null)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
        if (conversations is not IAtomicTurnOutcomeStore terminalOutcomes)
        {
            throw new ArgumentException(
                "The conversation store must atomically persist terminal turn outcomes.",
                nameof(conversations));
        }

        stateMachine = new CopilotTurnStateMachine(conversations, terminalOutcomes, clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeDirectory);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var active = new CopilotActiveTurn(request.MaximumToolCalls);
        if (!activeTurns.TryAdd(request.TurnId, active))
        {
            throw new InvalidOperationException("An engine turn with this identifier is already active.");
        }

        var events = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(EventChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var consumerStopped = new CancellationTokenSource();
        var execution = ExecuteTurnAsync(
            request,
            active,
            events.Writer,
            cancellationToken,
            consumerStopped.Token);
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            if (!execution.IsCompleted)
            {
                consumerStopped.Cancel();
                active.CancelByCaller();
            }

            try
            {
                await execution;
            }
            finally
            {
                activeTurns.TryRemove(new KeyValuePair<TurnId, CopilotActiveTurn>(request.TurnId, active));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask CancelAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!activeTurns.TryGetValue(turnId, out var active))
        {
            return;
        }

        if (!active.CancelByHost())
        {
            return;
        }

        await active.AbortAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!activeTurns.TryGetValue(turnId, out var active))
        {
            return;
        }

        if (!active.StopForHostShutdown())
        {
            return;
        }

        await active.AbortAsync(cancellationToken);
    }

    private async Task ExecuteTurnAsync(
        AgentTurnRequest request,
        CopilotActiveTurn active,
        ChannelWriter<AgentEvent> output,
        CancellationToken callerToken,
        CancellationToken consumerStoppedToken)
    {
        using var deadline = new CancellationTokenSource(request.Deadline);
        using var turnCancellation = new CancellationTokenSource();
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            active.EventCancellationToken);
        var cancellationSync = new object();
        var cancellationObserversInitializing = true;
        var pendingCancellationSignals = 0;
        var cancellationSignal = (int)CopilotTurnSignal.Completed;
        void RecordCancellation(CopilotTurnSignal signal)
        {
            lock (cancellationSync)
            {
                if (cancellationObserversInitializing)
                {
                    pendingCancellationSignals |= 1 << (int)signal;
                    return;
                }
                else if (cancellationSignal == (int)CopilotTurnSignal.Completed)
                {
                    Volatile.Write(ref cancellationSignal, (int)signal);
                }
            }

            // Publish the cause before waking execution; independent linked-token callbacks can run first.
            turnCancellation.Cancel();
            if (active.FailureCode != "tool_budget_exceeded")
            {
                eventCancellation.Cancel();
            }
        }

        CopilotTurnSignal GetCancellationSignal() =>
            (CopilotTurnSignal)Volatile.Read(ref cancellationSignal);
        bool IsCancellationRequested() =>
            Volatile.Read(ref cancellationSignal) != (int)CopilotTurnSignal.Completed;

        // Cancellation sources already signaled during observer setup are ambiguous; system interruption wins.
        using var callerCancellation = callerToken.Register(() => RecordCancellation(CopilotTurnSignal.Cancelled));
        using var hostCancellation = request.HostShutdownToken.Register(
            () => RecordCancellation(CopilotTurnSignal.HostShutdown));
        using var activeCancellation = active.CancellationToken.Register(
            () => RecordCancellation(active.StoppedForHostShutdown
                ? CopilotTurnSignal.HostShutdown
                : CopilotTurnSignal.Cancelled));
        using var acceptedDeadlineCancellation = request.DeadlineCancellationToken.Register(
            () => RecordCancellation(CopilotTurnSignal.DeadlineExceeded));
        using var engineDeadlineCancellation = deadline.Token.Register(
            () => RecordCancellation(request.ResolveDeadlineCancellation is { } resolve
                ? resolve() switch
                {
                    CancellationCause.Owner => CopilotTurnSignal.Cancelled,
                    CancellationCause.Shutdown => CopilotTurnSignal.HostShutdown,
                    CancellationCause.Deadline => CopilotTurnSignal.DeadlineExceeded,
                    _ => throw new InvalidOperationException("The host deadline arbiter must select a cancellation cause.")
                }
                : CopilotTurnSignal.DeadlineExceeded));
        lock (cancellationSync)
        {
            var hostShutdownMask = 1 << (int)CopilotTurnSignal.HostShutdown;
            var deadlineMask = 1 << (int)CopilotTurnSignal.DeadlineExceeded;
            var callerCancellationMask = 1 << (int)CopilotTurnSignal.Cancelled;
            cancellationSignal = (pendingCancellationSignals & hostShutdownMask) != 0
                ? (int)CopilotTurnSignal.HostShutdown
                : (pendingCancellationSignals & deadlineMask) != 0
                    ? (int)CopilotTurnSignal.DeadlineExceeded
                    : (pendingCancellationSignals & callerCancellationMask) != 0
                        ? (int)CopilotTurnSignal.Cancelled
                        : (int)CopilotTurnSignal.Completed;
            cancellationObserversInitializing = false;
        }

        if (IsCancellationRequested())
        {
            turnCancellation.Cancel();
            eventCancellation.Cancel();
        }

        var claimCandidate = await stateMachine.GetClaimCandidateAsync(request.TurnId);
        try
        {
            await stateMachine.EnsureRunningAsync(claimCandidate, turnCancellation.Token);
        }
        catch (OperationCanceledException) when (
            IsCancellationRequested())
        {
            try
            {
                active.MarkTerminal();
                var (status, claimCancellationEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                    request.TurnId,
                    clock,
                    GetCancellationSignal());
                using var terminalPersistence = new CancellationTokenSource(TerminalPersistenceTimeout);
                await stateMachine.SetTerminalOutcomeAsync(
                    request.TurnId,
                    status,
                    claimCancellationEvent,
                    terminalPersistence.Token,
                    claimCandidate.Version);
                output.TryWrite(claimCancellationEvent);
            }
            finally
            {
                output.TryComplete();
            }

            return;
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch
        {
            output.TryComplete();
            throw;
        }

        var observed = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(EventChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var eventPump = PumpEventsAsync(request.TurnId, observed.Reader, output, active, eventCancellation.Token);
        TurnStatus terminalStatus;
        AgentEvent terminalEvent;
        string? finalMessage = null;

        try
        {
            await WriteObservedEventAsync(observed.Writer, active, new TurnStarted(request.TurnId, clock.UtcNow), turnCancellation.Token);
            await WriteObservedEventAsync(observed.Writer, active, new RouteSelected(
                request.TurnId,
                clock.UtcNow,
                request.Provider,
                request.RouteReasonCode), turnCancellation.Token);

            var provider = GetProvider(request.Provider);
            var apiKey = await ResolveApiKeyAsync(provider, turnCancellation.Token);
            finalMessage = await RunSdkSessionAsync(
                request,
                provider,
                apiKey,
                active,
                observed.Writer,
                turnCancellation.Token);
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                CopilotTurnSignal.Completed);
        }
        catch (OperationCanceledException) when (active.FailureCode is not null)
        {
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                active.GetFailureSignal());
        }
        catch (OperationCanceledException) when (IsCancellationRequested())
        {
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                GetCancellationSignal());
        }
        catch (OperationCanceledException) when (IsDeadlineCancellationRequested(request, deadline))
        {
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                CopilotTurnSignal.DeadlineExceeded);
        }
        catch (TimeoutException) when (IsDeadlineCancellationRequested(request, deadline))
        {
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                CopilotTurnSignal.DeadlineExceeded);
        }
        catch (Exception) when (IsDeadlineCancellationRequested(request, deadline))
        {
            terminalStatus = TurnStatus.Interrupted;
            terminalEvent = new TurnInterrupted(request.TurnId, clock.UtcNow, "runtime_cleanup_failed");
        }
        catch (Exception) when (callerToken.IsCancellationRequested || active.CancelledByHost)
        {
            terminalStatus = TurnStatus.Interrupted;
            terminalEvent = new TurnInterrupted(request.TurnId, clock.UtcNow, "runtime_cleanup_failed");
        }
        catch (Exception exception)
        {
            (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                request.TurnId,
                clock,
                GetFailureSignal(exception));
        }

        try
        {
            observed.Writer.TryComplete();
            var eventPumpFailure = await eventPump;
            if (eventPumpFailure is OperationCanceledException && turnCancellation.IsCancellationRequested)
            {
                var signal = active.FailureCode is not null
                    ? active.GetFailureSignal()
                    : GetCancellationSignal();
                (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                    request.TurnId,
                    clock,
                    signal);
            }
            else if (eventPumpFailure is not null)
            {
                terminalStatus = TurnStatus.Interrupted;
                terminalEvent = new TurnInterrupted(
                    request.TurnId,
                    clock.UtcNow,
                    "event_persistence_failed");
            }

            active.MarkTerminal();
            if (terminalStatus == TurnStatus.Completed)
            {
                var signal = GetCancellationSignal();
                if (active.FailureCode is not null)
                {
                    signal = active.GetFailureSignal();
                }

                (terminalStatus, terminalEvent) = CopilotTurnStateMachine.CreateTerminalOutcome(
                    request.TurnId,
                    clock,
                    signal);
            }

            using var terminalPersistence = new CancellationTokenSource(TerminalPersistenceTimeout);
            await stateMachine.SetTerminalOutcomeAsync(
                request.TurnId,
                terminalStatus,
                terminalEvent,
                terminalPersistence.Token,
                finalAssistantMessage: terminalStatus == TurnStatus.Completed && finalMessage is not null
                    ? new ConversationMessage(
                        Guid.NewGuid(),
                        claimCandidate.ConversationId,
                        "assistant",
                        finalMessage,
                        clock.UtcNow)
                    : null);
            using var deliveryTimeout = new CancellationTokenSource(TerminalPersistenceTimeout);
            using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                consumerStoppedToken,
                deliveryTimeout.Token);
            try
            {
                await output.WriteAsync(terminalEvent, deliveryCancellation.Token);
            }
            catch (OperationCanceledException) when (consumerStoppedToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (deliveryTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The terminal outcome was persisted, but bounded event-stream delivery timed out.");
            }
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task<string> RunSdkSessionAsync(
        AgentTurnRequest request,
        CopilotProviderOptions provider,
        string? apiKey,
        CopilotActiveTurn active,
        ChannelWriter<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        var turnDirectory = Path.Combine(options.RuntimeDirectory, $"turn-{request.TurnId.Value:N}");
        var runtimeDirectory = Path.Combine(turnDirectory, "runtime");
        var workingDirectory = Path.Combine(turnDirectory, "workspace");
        CopilotClient? client = null;
        CopilotSession? session = null;
        IDisposable? subscription = null;
        try
        {
            EnsurePrivateDirectory(turnDirectory);
            EnsurePrivateDirectory(runtimeDirectory);
            EnsurePrivateDirectory(workingDirectory);

            client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = runtimeDirectory,
                WorkingDirectory = workingDirectory,
                UseLoggedInUser = false,
                LogLevel = CopilotLogLevel.None,
                Environment = CreateRuntimeEnvironment(runtimeDirectory)
            });
            active.SetClient(client);
            await client.StartAsync().WaitAsync(cancellationToken);
            var tools = request.Tools.Select(tool =>
                new CopilotToolFunction(tool, dispatcher, request.TurnId, events, active, clock)).ToArray();
            var sessionConfig = new SessionConfig
            {
                Model = provider.Model,
                Provider = new ProviderConfig
                {
                    Type = "openai",
                    BaseUrl = provider.BaseUrl.AbsoluteUri,
                    WireApi = provider.WireApi,
                    ApiKey = apiKey
                },
                AvailableTools = tools.Select(tool => $"custom:{tool.Name}").ToArray(),
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                Streaming = true,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = request.Instructions
                },
                OnPermissionRequest = (_, _) =>
                    Task.FromResult(PermissionDecision.Reject("Only host-registered tools are permitted.")),
                Tools = tools
            };
            session = await client.CreateSessionAsync(sessionConfig).WaitAsync(cancellationToken);
            active.SetSession(session);
            subscription = session.On<SessionEvent>(item =>
            {
                if (item is AssistantMessageDeltaEvent delta)
                {
                    var textDelta = new TextDelta(request.TurnId, clock.UtcNow, delta.Data.DeltaContent);
                    if (!active.TryAcceptEvent(textDelta) || !events.TryWrite(textDelta))
                    {
                        active.FailForEventBudget();
                    }
                }
            });

            var prompt = JsonSerializer.Serialize(new
            {
                packetId = request.Context.PacketId,
                policyVersion = request.Context.PolicyVersion,
                items = request.Context.Items
            });
            var turn = session.SendAndWaitAsync(new MessageOptions { Prompt = prompt });
            var response = await turn.WaitAsync(cancellationToken);
            var content = response?.Data.Content;
            if (string.IsNullOrWhiteSpace(content) || content.Length > 1_000_000)
            {
                throw new InvalidOperationException("The engine returned no bounded final assistant response.");
            }

            return content;
        }
        catch (OperationCanceledException)
        {
            await active.AbortAsync();
            throw;
        }
        catch (TimeoutException)
        {
            await active.AbortAsync();
            throw;
        }
        finally
        {
            await CleanupRuntimeAsync(
                subscription is null ? null : subscription.Dispose,
                session is null ? null : () => active.DisposeSessionAsync(session),
                () => active.StopClientAsync(),
                () =>
                {
                    if (!active.CanDeleteRuntimeDirectory)
                    {
                        throw new InvalidOperationException("The Copilot runtime may still be using its turn directory.");
                    }

                    DeleteTurnDirectory(turnDirectory);
                });
        }
    }

    private static void DeleteTurnDirectory(string turnDirectory)
    {
        if (Directory.Exists(turnDirectory))
        {
            Directory.Delete(turnDirectory, recursive: true);
        }
    }

    internal static async Task CleanupRuntimeAsync(
        Action? disposeSubscription,
        Func<Task>? disposeSession,
        Func<Task> stopClient,
        Action? cleanupRuntimeDirectory = null)
    {
        Exception? cleanupFailure = null;
        try
        {
            disposeSubscription?.Invoke();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            if (disposeSession is not null)
            {
                await disposeSession();
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        try
        {
            await stopClient();
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        try
        {
            cleanupRuntimeDirectory?.Invoke();
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            throw new RuntimeCleanupException(cleanupFailure);
        }
    }

    internal static CopilotTurnSignal GetFailureSignal(Exception exception) =>
        exception is RuntimeCleanupException
            ? CopilotTurnSignal.RuntimeCleanupFailed
            : CopilotTurnSignal.Failed;

    private static bool IsDeadlineCancellationRequested(
        AgentTurnRequest request,
        CancellationTokenSource engineDeadline) =>
        engineDeadline.IsCancellationRequested || request.DeadlineCancellationToken.IsCancellationRequested;

    private async Task<Exception?> PumpEventsAsync(
        TurnId turnId,
        ChannelReader<AgentEvent> events,
        ChannelWriter<AgentEvent> output,
        CopilotActiveTurn active,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in events.ReadAllAsync(cancellationToken))
            {
                await PersistAndPublishAsync(item, output, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            active.CancelByHost();
            try
            {
                await active.AbortAsync();
            }
            catch (Exception cleanupException)
            {
                return new AggregateException(exception, cleanupException);
            }

            return exception;
        }

        return null;
    }

    private static async ValueTask WriteObservedEventAsync(
        ChannelWriter<AgentEvent> events,
        CopilotActiveTurn active,
        AgentEvent item,
        CancellationToken cancellationToken)
    {
        if (!active.TryAcceptEvent(item))
        {
            throw new OperationCanceledException(active.CancellationToken);
        }

        await events.WriteAsync(item, cancellationToken);
    }

    private async Task PersistAndPublishAsync(
        AgentEvent item,
        ChannelWriter<AgentEvent> output,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(item, item.GetType());
        await conversations.AppendTurnEventAsync(
            item.TurnId,
            item.GetType().Name,
            payload,
            item.OccurredAtUtc,
            cancellationToken);
        await output.WriteAsync(item, cancellationToken);
    }

    private CopilotProviderOptions GetProvider(ProviderKind provider) =>
        (provider switch
        {
            ProviderKind.Local => options.LocalProvider,
            ProviderKind.Cloud => options.CloudProvider,
            _ => null
        }) is { } selected
            ? ValidateProvider(provider, selected)
            : throw new InvalidOperationException("The explicitly selected provider is not configured.");

    private static CopilotProviderOptions ValidateProvider(ProviderKind kind, CopilotProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Model) ||
            !provider.BaseUrl.IsAbsoluteUri ||
            provider.BaseUrl.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The selected provider configuration is invalid.");
        }

        if (kind == ProviderKind.Local && !provider.BaseUrl.IsLoopback)
        {
            throw new InvalidOperationException("The local inference endpoint must use a loopback address.");
        }

        if (kind == ProviderKind.Cloud &&
            provider.BaseUrl.Scheme != Uri.UriSchemeHttps &&
            !provider.BaseUrl.IsLoopback)
        {
            throw new InvalidOperationException("A non-loopback cloud inference endpoint must use HTTPS.");
        }

        return provider;
    }

    private async ValueTask<string?> ResolveApiKeyAsync(
        CopilotProviderOptions provider,
        CancellationToken cancellationToken)
    {
        if (provider.ApiKeyReference is null)
        {
            return null;
        }

        if (secretResolver is null)
        {
            throw new InvalidOperationException("The provider secret resolver is not configured.");
        }

        var secret = await secretResolver.ResolveAsync(provider.ApiKeyReference, cancellationToken);
        return secret.IsEmpty ? null : new string(secret.Span);
    }

    private void ValidateRequest(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Deadline <= TimeSpan.Zero ||
            request.Deadline == Timeout.InfiniteTimeSpan ||
            request.Deadline > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The turn deadline must be a positive finite duration.");
        }

        if (request.MaximumToolCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The tool-call budget cannot be negative.");
        }

        if (request.Tools is null || request.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Count)
        {
            throw new ArgumentException("The turn tool catalog must contain unique tool names.", nameof(request));
        }

        foreach (var tool in request.Tools)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
            using var schema = JsonDocument.Parse(tool.InputSchema);
            if (schema.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Each registered tool input schema must be a JSON object.", nameof(request));
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instructions);
        ArgumentNullException.ThrowIfNull(request.Context);
    }

    private static IReadOnlyDictionary<string, string> CreateRuntimeEnvironment(string runtimeDirectory)
    {
        var temporaryDirectory = Path.Combine(runtimeDirectory, "tmp");
        EnsurePrivateDirectory(temporaryDirectory);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = runtimeDirectory,
            ["USERPROFILE"] = runtimeDirectory,
            ["TMPDIR"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["TEMP"] = temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(runtimeDirectory, "config"),
            ["XDG_CACHE_HOME"] = Path.Combine(runtimeDirectory, "cache")
        };
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static async Task DisposeBoundedAsync(Task disposal)
    {
        await disposal.WaitAsync(CleanupTimeout);
    }

    private sealed class CopilotToolFunction : AIFunction
    {
        private readonly AgentToolDefinition definition;
        private readonly IToolDispatcher dispatcher;
        private readonly TurnId turnId;
        private readonly ChannelWriter<AgentEvent> events;
        private readonly CopilotActiveTurn active;
        private readonly IClock clock;
        private readonly JsonElement schema;

        public CopilotToolFunction(
            AgentToolDefinition definition,
            IToolDispatcher dispatcher,
            TurnId turnId,
            ChannelWriter<AgentEvent> events,
            CopilotActiveTurn active,
            IClock clock)
        {
            this.definition = definition;
            this.dispatcher = dispatcher;
            this.turnId = turnId;
            this.events = events;
            this.active = active;
            this.clock = clock;
            schema = JsonDocument.Parse(definition.InputSchema).RootElement.Clone();
        }

        /// <inheritdoc />
        public override string Name => definition.Name;

        /// <inheritdoc />
        public override string Description => "Host-registered capability; inputs are untrusted and validated by host policy.";

        /// <inheritdoc />
        public override JsonElement JsonSchema => schema;

        /// <inheritdoc />
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["skip_permission"] = true
            };

        /// <inheritdoc />
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var callId = Guid.NewGuid().ToString("N");
            await WriteObservedEventAsync(
                events,
                active,
                new ToolProposed(turnId, clock.UtcNow, callId, definition.Name),
                cancellationToken);
            var budgetCount = active.IncrementToolCalls();
            if (budgetCount > active.MaximumToolCalls)
            {
                active.FailForToolBudget();
                const string reason = "tool_budget_exceeded";
                await WriteObservedEventAsync(
                    events,
                    active,
                    new ToolCompleted(turnId, clock.UtcNow, callId, "Rejected"),
                    cancellationToken);
                return JsonSerializer.Serialize(new ToolDispatchResult("Rejected", null, reason));
            }

            var serializedArguments = JsonSerializer.Serialize(arguments);
            var result = await dispatcher.DispatchAsync(
                new ToolDispatchRequest(turnId, callId, definition.Name, serializedArguments),
                cancellationToken);
            await WriteObservedEventAsync(
                events,
                active,
                new ToolCompleted(turnId, clock.UtcNow, callId, result.Status),
                cancellationToken);
            return JsonSerializer.Serialize(result);
        }
    }
}
