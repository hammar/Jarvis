using GitHub.Copilot;
using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

internal sealed class CopilotActiveTurn
{
    private const int MaximumPersistedEvents = 10_000;
    private const int MaximumTextCharacters = 1_000_000;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource eventStop = new();
    private readonly SemaphoreSlim abortGate = new(1, 1);
    private readonly object sync = new();
    private CopilotClient? client;
    private CopilotSession? session;
    private CopilotClientShutdown? clientShutdown;
    private int toolCalls;
    private int emittedEvents;
    private int emittedTextCharacters;
    private bool cancelledByHost;
    private bool terminal;
    private string? failureCode;

    public CopilotActiveTurn(int maximumToolCalls) => MaximumToolCalls = maximumToolCalls;

    public int MaximumToolCalls { get; }
    public CancellationToken CancellationToken => stop.Token;
    public CancellationToken EventCancellationToken => eventStop.Token;
    public bool CancelledByHost
    {
        get
        {
            lock (sync)
            {
                return cancelledByHost;
            }
        }
    }

    public string? FailureCode => Volatile.Read(ref failureCode);

    public void SetClient(CopilotClient value)
    {
        lock (sync)
        {
            client = value;
            clientShutdown = new CopilotClientShutdown(
                value.ForceStopAsync,
                () => CopilotAgentEngine.DisposeBoundedAsync(value.DisposeAsync().AsTask()));
        }
    }

    public void SetSession(CopilotSession value) => Volatile.Write(ref session, value);

    public bool CancelByHost()
    {
        lock (sync)
        {
            if (terminal)
            {
                return false;
            }

            cancelledByHost = true;
        }

        stop.Cancel();
        eventStop.Cancel();
        return true;
    }

    public void CancelByCaller() => _ = CancelByHost();

    public bool MarkTerminal()
    {
        lock (sync)
        {
            terminal = true;
            return cancelledByHost;
        }
    }

    public void FailForToolBudget()
    {
        Interlocked.CompareExchange(ref failureCode, "tool_budget_exceeded", null);
        stop.Cancel();
    }

    public bool TryAcceptEvent(AgentEvent item)
    {
        var eventCount = Interlocked.Increment(ref emittedEvents);
        if (eventCount > MaximumPersistedEvents)
        {
            FailForEventBudget();
            return false;
        }

        if (item is TextDelta delta)
        {
            var textCharacters = Interlocked.Add(ref emittedTextCharacters, delta.Text.Length);
            if (textCharacters > MaximumTextCharacters)
            {
                FailForEventBudget();
                return false;
            }
        }

        return true;
    }

    public void FailForEventBudget()
    {
        Interlocked.CompareExchange(ref failureCode, "event_budget_exceeded", null);
        stop.Cancel();
        eventStop.Cancel();
    }

    public CopilotTurnSignal GetFailureSignal() =>
        FailureCode == "event_budget_exceeded"
            ? CopilotTurnSignal.EventBudgetExceeded
            : CopilotTurnSignal.ToolBudgetExceeded;

    public int IncrementToolCalls() => Interlocked.Increment(ref toolCalls);

    public static CopilotTurnSignal SelectCompletedSignal(
        bool toolBudgetExceeded,
        bool cancellationRequested,
        bool deadlineExceeded) =>
        toolBudgetExceeded
            ? CopilotTurnSignal.ToolBudgetExceeded
            : cancellationRequested
                ? CopilotTurnSignal.Cancelled
                : deadlineExceeded
                    ? CopilotTurnSignal.DeadlineExceeded
                    : CopilotTurnSignal.Completed;

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await abortGate.WaitAsync(cancellationToken);
        try
        {
            var currentSession = Volatile.Read(ref session);
            await AbortRuntimeAsync(
                currentSession is null
                    ? null
                    : async () => await currentSession.AbortAsync(),
                StopClientAsync,
                CopilotAgentEngine.CleanupTimeout,
                cancellationToken);
        }
        finally
        {
            abortGate.Release();
        }
    }

    internal static async Task AbortRuntimeAsync(
        Func<Task>? abortSession,
        Func<CancellationToken, Task> stopClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (abortSession is null)
        {
            await stopClient(cancellationToken);
            return;
        }

        try
        {
            await abortSession().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await stopClient(cancellationToken);
        }
    }

    public async Task DisposeSessionAsync(CopilotSession value)
    {
        await abortGate.WaitAsync();
        try
        {
            if (ReferenceEquals(Volatile.Read(ref session), value))
            {
                Volatile.Write(ref session, null);
            }

            await CopilotAgentEngine.DisposeBoundedAsync(value.DisposeAsync().AsTask());
        }
        finally
        {
            abortGate.Release();
        }
    }

    public async Task StopClientAsync(CancellationToken cancellationToken = default)
    {
        CopilotClientShutdown? shutdown;
        lock (sync)
        {
            shutdown = clientShutdown;
        }

        if (shutdown is not null)
        {
            await shutdown.StopAsync(cancellationToken);
        }
    }

    public bool CanDeleteRuntimeDirectory
    {
        get
        {
            lock (sync)
            {
                return clientShutdown is null || clientShutdown.IsCompleted;
            }
        }
    }
}

internal sealed class CopilotClientShutdown(
    Func<Task> forceStop,
    Func<Task> dispose,
    TimeSpan? waitTimeout = null)
{
    private readonly object sync = new();
    private readonly TimeSpan timeout = waitTimeout ?? CopilotAgentEngine.CleanupTimeout;
    private Task? shutdown;
    private bool isCompleted;

    public bool IsCompleted
    {
        get
        {
            lock (sync)
            {
                return isCompleted;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task current;
        lock (sync)
        {
            current = shutdown ??= StopCoreAsync();
        }

        await current.WaitAsync(timeout, cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await forceStop();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        lock (sync)
        {
            isCompleted = true;
        }
    }
}

internal sealed class RuntimeCleanupException(Exception innerException)
    : Exception("The Copilot runtime could not be cleanly stopped or disposed.", innerException);
