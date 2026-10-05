using GitHub.Copilot;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

internal sealed class CopilotActiveTurn
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim abortGate = new(1, 1);
    private readonly SemaphoreSlim clientStopGate = new(1, 1);
    private readonly object sync = new();
    private CopilotClient? client;
    private CopilotSession? session;
    private int toolCalls;
    private bool clientStopped;
    private bool cancelledByHost;
    private bool terminal;
    private string? failureCode;

    public CopilotActiveTurn(int maximumToolCalls) => MaximumToolCalls = maximumToolCalls;

    public int MaximumToolCalls { get; }
    public CancellationToken CancellationToken => stop.Token;
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

    public void SetClient(CopilotClient value) => Volatile.Write(ref client, value);

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
            if (currentSession is not null)
            {
                try
                {
                    await currentSession.AbortAsync().WaitAsync(
                        CopilotAgentEngine.CleanupTimeout,
                        cancellationToken);
                }
                catch (TimeoutException)
                {
                    await StopClientAsync(cancellationToken);
                }
            }
            else
            {
                await StopClientAsync(cancellationToken);
            }
        }
        finally
        {
            abortGate.Release();
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
        await clientStopGate.WaitAsync(cancellationToken);
        try
        {
            var currentClient = Volatile.Read(ref client);
            if (currentClient is null || clientStopped)
            {
                return;
            }

            try
            {
                await currentClient.ForceStopAsync().WaitAsync(
                    CopilotAgentEngine.CleanupTimeout,
                    cancellationToken);
            }
            finally
            {
                await CopilotAgentEngine.DisposeBoundedAsync(currentClient.DisposeAsync().AsTask());
                clientStopped = true;
            }
        }
        finally
        {
            clientStopGate.Release();
        }
    }
}

internal sealed class RuntimeCleanupException(Exception innerException)
    : Exception("The Copilot runtime could not be cleanly stopped or disposed.", innerException);
