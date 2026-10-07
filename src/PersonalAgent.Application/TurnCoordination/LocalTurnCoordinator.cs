using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.TurnCoordination;

/// <summary>Coordinates bounded LocalOnly turn submission, execution, cancellation, and restart recovery.</summary>
public sealed class LocalTurnCoordinator : ILocalTurnCoordinator
{
    private const int RecoveryPageSize = 1000;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TerminalPersistenceTimeout = TimeSpan.FromSeconds(5);
    private readonly IConversationStore conversations;
    private readonly IAtomicTurnOutcomeStore outcomes;
    private readonly IModelRouter router;
    private readonly IContextBuilder contextBuilder;
    private readonly IAgentEngine engine;
    private readonly IClock clock;
    private readonly LocalTurnCoordinatorOptions options;
    private readonly string hostInstructions;
    private readonly Channel<WorkItem> queue;
    private readonly SemaphoreSlim queueSlots;
    private readonly ConcurrentDictionary<TurnId, WorkItem> queued = new();
    private readonly ConcurrentDictionary<TurnId, ActiveWork> active = new();
    private readonly ConcurrentDictionary<TurnId, Task> deadlineMonitors = new();
    private readonly Dictionary<ConversationId, PriorityQueue<WorkItem, long>> deferredByConversation = [];
    private readonly SemaphoreSlim submissionGate = new(1, 1);
    private readonly object lifecycleLock = new();
    private CancellationTokenSource? shutdown;
    private Task[] workers = [];
    private long nextQueueOrder;
    private bool started;
    private bool stopping;

    /// <summary>Creates the host-owned local turn coordinator.</summary>
    /// <param name="conversations">Durable conversation, submission, and ordered event store.</param>
    /// <param name="outcomes">Atomic terminal-status/event/final-message store.</param>
    /// <param name="router">Application LocalOnly routing policy.</param>
    /// <param name="contextBuilder">Application bounded local context builder.</param>
    /// <param name="engine">Isolated inference engine; its provider must be explicit.</param>
    /// <param name="clock">UTC clock used for durable observations.</param>
    /// <param name="options">Validated queue, request ID, and deadline limits.</param>
    /// <param name="hostInstructions">Host-authored instructions; never accepted from model output.</param>
    public LocalTurnCoordinator(
        IConversationStore conversations,
        IAtomicTurnOutcomeStore outcomes,
        IModelRouter router,
        IContextBuilder contextBuilder,
        IAgentEngine engine,
        IClock clock,
        LocalTurnCoordinatorOptions options,
        string hostInstructions)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        this.contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstructions);
        this.hostInstructions = hostInstructions;
        queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        queueSlots = new SemaphoreSlim(options.MaximumQueuedTurns, options.MaximumQueuedTurns);
    }

    /// <summary>Gets whether recovery completed and the coordinator is accepting work.</summary>
    public bool IsReady
    {
        get
        {
            lock (lifecycleLock)
            {
                return started
                    && !stopping
                    && workers.All(worker => !worker.IsCompleted)
                    && deadlineMonitors.Values.All(monitor => !monitor.IsFaulted && !monitor.IsCanceled);
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleLock)
        {
            if (started || stopping)
            {
                throw new InvalidOperationException("The local turn coordinator can only be started once.");
            }
        }

        await RecoverInterruptedTurnsAsync(cancellationToken);
        lock (lifecycleLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shutdown = new CancellationTokenSource();
            started = true;
            workers = Enumerable.Range(0, options.MaximumActiveTurns)
                .Select(_ => WorkerAsync(shutdown.Token))
                .ToArray();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveWork[] activeWork;
        Task[] workerTasks;
        Task[] deadlineTasks;
        lock (lifecycleLock)
        {
            if (!started || stopping)
            {
                return;
            }

            activeWork = active.Values.Where(work => work.InEngine).ToArray();
            stopping = true;
            shutdown!.Cancel();
            RequeueDeferredTurns();
            queue.Writer.TryComplete();
            workerTasks = workers;
            deadlineTasks = deadlineMonitors.Values.ToArray();
        }

        using var shutdownDeadline = new CancellationTokenSource(ShutdownTimeout);
        using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            shutdownDeadline.Token);
        var stopFailures = new List<Exception>();
        try
        {
            await Task.WhenAll(activeWork
                .Select(work => engine.StopAsync(work.Item.TurnId, stopDeadline.Token).AsTask()));
        }
        catch (OperationCanceledException) when (shutdownDeadline.IsCancellationRequested)
        {
            stopFailures.Add(new TimeoutException("Local turn shutdown exceeded its bounded deadline."));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            stopFailures.Add(exception);
        }
        catch (Exception exception)
        {
            stopFailures.Add(exception);
        }

        try
        {
            await Task.WhenAll(deadlineTasks).WaitAsync(shutdownDeadline.Token);
        }
        catch (OperationCanceledException) when (shutdownDeadline.IsCancellationRequested)
        {
            stopFailures.Add(new TimeoutException("Local turn shutdown exceeded its bounded deadline."));
        }
        catch (Exception exception)
        {
            stopFailures.Add(exception);
        }

        try
        {
            await Task.WhenAll(workerTasks).WaitAsync(shutdownDeadline.Token);
        }
        catch (OperationCanceledException) when (shutdownDeadline.IsCancellationRequested)
        {
            stopFailures.Add(new TimeoutException("Local turn shutdown exceeded its bounded deadline."));
        }
        catch (Exception exception)
        {
            // A worker can propagate the same deadline-monitor failure already observed above.
            if (!stopFailures.Contains(exception))
            {
                stopFailures.Add(exception);
            }
        }

        if (stopFailures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailures[0]).Throw();
        }

        if (stopFailures.Count > 1)
        {
            throw new AggregateException("Local turn shutdown encountered multiple failures.", stopFailures);
        }
    }

    /// <inheritdoc />
    public async ValueTask<SubmittedConversationTurn> SubmitAsync(
        LocalTurnRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var deadline = request.DeadlineOverride ?? options.InteractiveDeadline;
        if (deadline <= TimeSpan.Zero || deadline > options.MaximumDeadline)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The turn deadline is outside the configured finite bounds.");
        }

        await submissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (lifecycleLock)
            {
                if (!IsReady)
                {
                    throw new InvalidOperationException("The local turn coordinator is not accepting work.");
                }
            }

            var fingerprint = Fingerprint(request, deadline);
            var existing = await conversations.FindSubmittedTurnAsync(
                request.ConversationId,
                request.ClientRequestId,
                fingerprint,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            if (!queueSlots.Wait(0))
            {
                throw new TurnQueueFullException();
            }

            var slotReserved = true;
            try
            {
                var messageId = Guid.NewGuid();
                var submitted = await conversations.SubmitTurnAsync(
                    new ConversationTurnSubmission(
                        TurnId.New(),
                        request.ConversationId,
                        request.ClientRequestId,
                        fingerprint,
                        messageId,
                        request.Text,
                        clock.UtcNow),
                    cancellationToken);
                if (submitted.IsDuplicate)
                {
                    queueSlots.Release();
                    slotReserved = false;
                    return submitted;
                }

                var work = new WorkItem(
                    submitted.Turn.Id,
                    submitted.Turn.ConversationId,
                    request,
                    deadline,
                    submitted.UserMessageId,
                    Interlocked.Increment(ref nextQueueOrder));
                work.ArmDeadline(shutdown!.Token);
                var acceptedForQueue = false;
                var stoppedDuringSubmission = false;
                CancellationToken monitorStoppingToken = default;
                lock (lifecycleLock)
                {
                    stoppedDuringSubmission = stopping;
                    acceptedForQueue = !stoppedDuringSubmission
                        && queued.TryAdd(work.TurnId, work)
                        && queue.Writer.TryWrite(work);
                    if (acceptedForQueue)
                    {
                        deadlineMonitors[work.TurnId] = work.DeadlineMonitor.Task;
                        monitorStoppingToken = shutdown!.Token;
                    }

                    if (!acceptedForQueue)
                    {
                        queued.TryRemove(work.TurnId, out _);
                    }
                }

                if (acceptedForQueue)
                {
                    slotReserved = false;
                    _ = ObserveDeadlineAsync(work, monitorStoppingToken);
                    return submitted;
                }

                var status = stoppedDuringSubmission ? TurnStatus.Interrupted : TurnStatus.Failed;
                var reason = stoppedDuringSubmission ? "host_shutdown" : "turn_queue_full";
                try
                {
                    await TryResolveTerminalWithinBoundAsync(
                        work.TurnId,
                        status,
                        reason,
                        stoppedDuringSubmission ? null : "The local turn queue is full. Please try again.");
                    if (stoppedDuringSubmission)
                    {
                        throw new InvalidOperationException("The local turn coordinator stopped while the request was being accepted.");
                    }

                    throw new TurnQueueFullException();
                }
                finally
                {
                    work.Dispose();
                }
            }
            finally
            {
                if (slotReserved)
                {
                    queueSlots.Release();
                }
            }
        }
        finally
        {
            submissionGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PersistedTurnEvent>> ReadEventsAfterAsync(
        TurnId turnId,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken) =>
        conversations.ReadTurnEventsAfterAsync(turnId, afterSequence, maximumEvents, cancellationToken);

    /// <inheritdoc />
    public async ValueTask CancelAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveWork? running;
        WorkItem? waiting;
        lock (lifecycleLock)
        {
            active.TryGetValue(turnId, out running);
            waiting = running is null && queued.TryGetValue(turnId, out var pending)
                ? pending
                : null;
            running?.Item.CancelOwner();
            waiting?.CancelOwner();
        }

        if (running is not null)
        {
            await engine.CancelAsync(turnId, cancellationToken);
            return;
        }

        if (waiting is not null)
        {
            await ResolveCancellationAsync(waiting);
            ReleasePendingReservation(waiting);
        }
    }

    private async Task RecoverInterruptedTurnsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var turns = await conversations.ReadNonterminalTurnsAsync(RecoveryPageSize, cancellationToken);
            if (turns.Count == 0)
            {
                return;
            }
            var before = turns[0].Id;

            foreach (var turn in turns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryResolveTerminalAsync(
                    turn.Id,
                    TurnStatus.Interrupted,
                    "process_restart",
                    null,
                    cancellationToken);
            }

            var remaining = await conversations.GetTurnAsync(before, cancellationToken);
            if (remaining is not null && !IsTerminal(remaining.Status))
            {
                throw new InvalidOperationException("Interrupted-turn recovery could not resolve a nonterminal record.");
            }
        }
    }

    private async Task WorkerAsync(CancellationToken hostStopping)
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            var work = new ActiveWork(item);
            bool wasQueued;
            bool deferred;
            lock (lifecycleLock)
            {
                wasQueued = queued.ContainsKey(item.TurnId);
                deferred = wasQueued
                    && !stopping
                    && !item.Cancellation.IsCancellationRequested
                    && (active.Values.Any(activeWork =>
                        activeWork.Item.ConversationId == item.ConversationId)
                        || queued.Values.Any(pending =>
                            pending.ConversationId == item.ConversationId
                            && pending.QueueOrder < item.QueueOrder));
                if (deferred)
                {
                    if (!deferredByConversation.TryGetValue(item.ConversationId, out var waiting))
                    {
                        waiting = new PriorityQueue<WorkItem, long>();
                        deferredByConversation.Add(item.ConversationId, waiting);
                    }

                    waiting.Enqueue(item, item.QueueOrder);
                }
                else if (wasQueued)
                {
                    queued.TryRemove(item.TurnId, out _);
                    queueSlots.Release();
                    active[item.TurnId] = work;
                }
            }

            if (deferred)
            {
                continue;
            }

            try
            {
                if (!wasQueued)
                {
                    continue;
                }

                if (item.RemainingDeadline <= TimeSpan.Zero)
                {
                    item.ExpireDeadline();
                }

                if (item.ExecutionCancellation.IsCancellationRequested)
                {
                    await ResolveCancellationAsync(item);
                }
                else
                {
                    await ExecuteTurnAsync(work, hostStopping);
                }
            }
            finally
            {
                lock (lifecycleLock)
                {
                    active.TryRemove(item.TurnId, out _);
                    if (!stopping)
                    {
                        RequeueNextDeferredTurn(item.ConversationId);
                    }
                }

                item.StopDeadlineMonitor();
                try
                {
                    await item.DeadlineMonitor.Task;
                }
                finally
                {
                    item.Dispose();
                }
            }
        }
    }

    private async Task ExecuteTurnAsync(ActiveWork work, CancellationToken hostStopping)
    {
        var item = work.Item;
        var cancellationToken = item.Cancellation.Token;
        var remainingDeadline = item.RemainingDeadline;
        if (remainingDeadline <= TimeSpan.Zero)
        {
            item.ExpireDeadline();
        }

        var routingCancellation = item.ExecutionCancellation;
        try
        {
            var turn = await conversations.GetTurnAsync(item.TurnId, routingCancellation.Token)
                ?? throw new InvalidOperationException("The accepted application turn is missing.");
            if (IsTerminal(turn.Status))
            {
                return;
            }

            var routingEvent = new TurnRouting(item.TurnId, clock.UtcNow);
            await conversations.TransitionTurnAndAppendEventAsync(
                item.TurnId,
                TurnStatus.Routing,
                turn.Version,
                clock.UtcNow,
                nameof(TurnRouting),
                JsonSerializer.Serialize(routingEvent),
                routingEvent.OccurredAtUtc,
                routingCancellation.Token);

            var route = await router.RouteAsync(
                new RoutingRequest(RouteMode.LocalOnly, item.Request.Text, true, item.Request.TaskKind),
                routingCancellation.Token);
            if (route.Disposition != RouteDisposition.Local || route.Provider != ProviderKind.Local)
            {
                if (route.Disposition == RouteDisposition.Clarify)
                {
                    var message = route.UserMessage
                        ?? throw new InvalidOperationException("A clarification route must include an owner-facing message.");
                    var clarification = new TurnClarificationRequired(
                        item.TurnId,
                        clock.UtcNow,
                        route.ReasonCode,
                        message);
                    await TryResolveTerminalWithinBoundAsync(
                        item.TurnId,
                        TurnStatus.Failed,
                        route.ReasonCode,
                        message,
                        clarification);
                    return;
                }

                await TryResolveTerminalWithinBoundAsync(
                    item.TurnId,
                    TurnStatus.Failed,
                    route.ReasonCode,
                    route.UserMessage);
                return;
            }

            var context = await contextBuilder.BuildAsync(
                new ContextBuildRequest(
                    item.ConversationId,
                    ProviderKind.Local,
                    item.Request.Text,
                    hostInstructions,
                    Array.Empty<AgentToolDefinition>(),
                    item.UserMessageId),
                routingCancellation.Token);
            lock (lifecycleLock)
            {
                if (stopping)
                {
                    throw new OperationCanceledException(hostStopping);
                }

                remainingDeadline = item.RemainingDeadline;
                if (remainingDeadline <= TimeSpan.Zero)
                {
                    item.ExpireDeadline();
                }

                routingCancellation.Token.ThrowIfCancellationRequested();
                work.InEngine = true;
            }

            var engineRequest = new AgentTurnRequest(
                item.TurnId,
                ProviderKind.Local,
                hostInstructions,
                context,
                Array.Empty<AgentToolDefinition>(),
                remainingDeadline,
                MaximumToolCalls: 0,
                RouteReasonCode: route.ReasonCode,
                HostShutdownToken: hostStopping,
                DeadlineCancellationToken: item.DeadlineCancellation.Token);
            await foreach (var _ in engine.RunTurnAsync(engineRequest, cancellationToken))
            {
            }

            var engineOutcome = await conversations.GetTurnAsync(item.TurnId, cancellationToken)
                ?? throw new InvalidOperationException("The application turn disappeared after engine execution.");
            if (!IsTerminal(engineOutcome.Status))
            {
                await TryResolveTerminalWithinBoundAsync(
                    item.TurnId,
                    TurnStatus.Interrupted,
                    "engine_ended_without_terminal_outcome");
            }
        }
        catch (OperationCanceledException) when (routingCancellation.IsCancellationRequested)
        {
            await ResolveCancellationAsync(item);
        }
        catch (Exception exception)
        {
            await TryResolveTerminalWithinBoundAsync(
                item.TurnId,
                TurnStatus.Interrupted,
                $"turn_execution_interrupted_{exception.GetType().Name}");
        }
        finally
        {
            work.InEngine = false;
        }
    }

    private void RequeueNextDeferredTurn(ConversationId conversationId)
    {
        if (!deferredByConversation.TryGetValue(conversationId, out var waiting) || waiting.Count == 0)
        {
            return;
        }

        var next = waiting.Dequeue();

        if (waiting.Count == 0)
        {
            deferredByConversation.Remove(conversationId);
        }

        if (!queue.Writer.TryWrite(next))
        {
            throw new InvalidOperationException("A deferred local turn could not be returned to the scheduler.");
        }
    }

    private async Task ObserveDeadlineAsync(WorkItem item, CancellationToken hostStopping)
    {
        try
        {
            await ExpireQueuedTurnAsync(item, hostStopping);
            item.DeadlineMonitor.TrySetResult();
        }
        catch (Exception exception)
        {
            item.DeadlineMonitor.TrySetException(exception);
        }
        finally
        {
            if (!item.DeadlineMonitor.Task.IsFaulted)
            {
                deadlineMonitors.TryRemove(item.TurnId, out _);
            }
        }
    }

    private async Task ExpireQueuedTurnAsync(WorkItem item, CancellationToken hostStopping)
    {
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(
            item.DeadlineCancellation.Token,
            hostStopping,
            item.DeadlineMonitorCancellation.Token);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, monitor.Token);
        }
        catch (OperationCanceledException) when (
            hostStopping.IsCancellationRequested || item.DeadlineMonitorCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (item.DeadlineCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (hostStopping.IsCancellationRequested || item.DeadlineMonitorCancellation.IsCancellationRequested)
        {
            return;
        }

        lock (lifecycleLock)
        {
            if (active.ContainsKey(item.TurnId) || !queued.ContainsKey(item.TurnId))
            {
                return;
            }

        }

        await ResolveCancellationAsync(item);
        ReleasePendingReservation(item);
    }

    private Task ResolveCancellationAsync(WorkItem item) =>
        TryResolveTerminalWithinBoundAsync(
            item.TurnId,
            item.CancellationCause == CancellationCause.Owner ? TurnStatus.Cancelled : TurnStatus.Interrupted,
            item.CancellationCause switch
            {
                CancellationCause.Owner => "owner_cancelled",
                CancellationCause.Shutdown => "host_shutdown",
                _ => "interactive_deadline_exceeded"
            });

    private void ReleasePendingReservation(WorkItem item)
    {
        lock (lifecycleLock)
        {
            if (queued.TryRemove(item.TurnId, out _))
            {
                queueSlots.Release();
                item.StopDeadlineMonitor();
                // Stale channel entries only reclaim resources; deferred entries need the same cleanup path.
                if (RemoveDeferredTurn(item) && !queue.Writer.TryWrite(item))
                {
                    throw new InvalidOperationException("Terminal queued work could not be returned for cleanup.");
                }
            }
        }
    }

    private bool RemoveDeferredTurn(WorkItem item)
    {
        if (!deferredByConversation.TryGetValue(item.ConversationId, out var waiting)
            || !waiting.Remove(item, out _, out _))
        {
            return false;
        }

        if (waiting.Count == 0)
        {
            deferredByConversation.Remove(item.ConversationId);
        }

        return true;
    }

    private void RequeueDeferredTurns()
    {
        foreach (var waiting in deferredByConversation.Values)
        {
            while (waiting.TryDequeue(out var item, out _))
            {
                if (!queue.Writer.TryWrite(item))
                {
                    throw new InvalidOperationException("A deferred local turn could not be returned during shutdown.");
                }
            }
        }

        deferredByConversation.Clear();
    }

    private async Task TryResolveTerminalAsync(
        TurnId turnId,
        TurnStatus status,
        string reasonCode,
        string? finalMessage,
        CancellationToken cancellationToken,
        AgentEvent? terminalEventOverride = null)
    {
        var turn = await conversations.GetTurnAsync(turnId, cancellationToken)
            ?? throw new InvalidOperationException("The application turn does not exist.");
        if (IsTerminal(turn.Status))
        {
            return;
        }

        AgentEvent terminalEvent = terminalEventOverride ?? status switch
        {
            TurnStatus.Completed => new TurnCompleted(turnId, clock.UtcNow),
            TurnStatus.Cancelled => new TurnCancelled(turnId, clock.UtcNow),
            TurnStatus.Interrupted => new TurnInterrupted(turnId, clock.UtcNow, reasonCode),
            TurnStatus.Failed => new TurnFailed(turnId, clock.UtcNow, reasonCode),
            _ => throw new ArgumentOutOfRangeException(nameof(status), "A terminal turn status is required.")
        };
        var message = finalMessage is null
            ? null
            : new ConversationMessage(Guid.NewGuid(), turn.ConversationId, "assistant", finalMessage, clock.UtcNow);
        try
        {
            await outcomes.UpdateTurnStatusAndAppendEventAsync(
                turnId,
                status,
                turn.Version,
                clock.UtcNow,
                terminalEvent.GetType().Name,
                JsonSerializer.Serialize(terminalEvent, terminalEvent.GetType()),
                terminalEvent.OccurredAtUtc,
                cancellationToken,
                message);
        }
        catch (PersistenceConcurrencyException)
        {
            var latest = await conversations.GetTurnAsync(turnId, cancellationToken)
                ?? throw new InvalidOperationException("The application turn disappeared during terminal resolution.");
            if (IsTerminal(latest.Status))
            {
                return;
            }

            try
            {
                await outcomes.UpdateTurnStatusAndAppendEventAsync(
                    turnId,
                    status,
                    latest.Version,
                    clock.UtcNow,
                    terminalEvent.GetType().Name,
                    JsonSerializer.Serialize(terminalEvent, terminalEvent.GetType()),
                    terminalEvent.OccurredAtUtc,
                    cancellationToken,
                    message);
            }
            catch (PersistenceConcurrencyException)
            {
                var afterRetry = await conversations.GetTurnAsync(turnId, cancellationToken)
                    ?? throw new InvalidOperationException("The application turn disappeared during terminal resolution.");
                if (!IsTerminal(afterRetry.Status))
                {
                    throw;
                }
            }
        }
    }

    private async Task TryResolveTerminalWithinBoundAsync(
        TurnId turnId,
        TurnStatus status,
        string reasonCode,
        string? finalMessage = null,
        AgentEvent? terminalEventOverride = null)
    {
        using var persistenceDeadline = new CancellationTokenSource(TerminalPersistenceTimeout);
        await TryResolveTerminalAsync(
            turnId,
            status,
            reasonCode,
            finalMessage,
            persistenceDeadline.Token,
            terminalEventOverride);
    }

    private void ValidateRequest(LocalTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A nonempty conversation identifier is required.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        if (request.ClientRequestId.Length > options.MaximumRequestIdCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The client request ID exceeds its configured bound.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.Text.Length > LocalContextLimits.MaximumTaskTextCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The request exceeds the fixed local text limit.");
        }

        if (!Enum.IsDefined(request.TaskKind))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The host task category is invalid.");
        }
    }

    private static string Fingerprint(LocalTurnRequest request, TimeSpan deadline)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            conversationId = request.ConversationId.Value,
            request.Text,
            request.TaskKind,
            deadlineTicks = deadline.Ticks
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsTerminal(TurnStatus status) =>
        status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted;

    private sealed class WorkItem(
        TurnId turnId,
        ConversationId conversationId,
        LocalTurnRequest request,
        TimeSpan deadline,
        Guid userMessageId,
        long queueOrder)
    {
        private readonly long acceptedTimestamp = Stopwatch.GetTimestamp();
        private int cancellationCause;
        private CancellationTokenRegistration deadlineRegistration;
        private CancellationTokenRegistration shutdownRegistration;
        private readonly CancellationTokenSource deadlineTimer = new();

        public TurnId TurnId { get; } = turnId;
        public ConversationId ConversationId { get; } = conversationId;
        public LocalTurnRequest Request { get; } = request;
        public TimeSpan Deadline { get; } = deadline;
        public TimeSpan RemainingDeadline => Deadline - Stopwatch.GetElapsedTime(acceptedTimestamp);
        public Guid UserMessageId { get; } = userMessageId;
        public long QueueOrder { get; } = queueOrder;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationTokenSource DeadlineCancellation { get; } = new();
        public CancellationTokenSource ExecutionCancellation { get; } = new();
        public CancellationCause CancellationCause => (CancellationCause)Volatile.Read(ref cancellationCause);
        public CancellationTokenSource DeadlineMonitorCancellation { get; } = new();
        public TaskCompletionSource DeadlineMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ArmDeadline(CancellationToken hostStopping)
        {
            shutdownRegistration = hostStopping.Register(() => SignalCancellation(CancellationCause.Shutdown));
            deadlineRegistration = deadlineTimer.Token.Register(ExpireDeadline);
            deadlineTimer.CancelAfter(Deadline);
        }

        public void CancelOwner()
        {
            SignalCancellation(CancellationCause.Owner);
            Cancellation.Cancel();
        }

        public void ExpireDeadline()
        {
            SignalCancellation(CancellationCause.Deadline);
            DeadlineCancellation.Cancel();
        }

        private void SignalCancellation(CancellationCause cause)
        {
            Interlocked.CompareExchange(ref cancellationCause, (int)cause, (int)CancellationCause.None);
            ExecutionCancellation.Cancel();
        }

        public void StopDeadlineMonitor() => DeadlineMonitorCancellation.Cancel();
        public void Dispose()
        {
            deadlineRegistration.Dispose();
            shutdownRegistration.Dispose();
            deadlineTimer.Dispose();
            Cancellation.Dispose();
            DeadlineCancellation.Dispose();
            ExecutionCancellation.Dispose();
            DeadlineMonitorCancellation.Dispose();
        }
    }

    private enum CancellationCause { None, Owner, Shutdown, Deadline }

    private sealed class ActiveWork(WorkItem item)
    {
        public WorkItem Item { get; } = item;
        public volatile bool InEngine;
    }

}
