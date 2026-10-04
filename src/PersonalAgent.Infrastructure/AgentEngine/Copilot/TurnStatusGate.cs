using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Provides atomic in-process state transitions for one non-durable engine turn.</summary>
internal sealed class TurnStatusGate
{
    private int _status = (int)TurnStatus.Received;

    public TurnStatus Status => (TurnStatus)Volatile.Read(ref _status);

    public bool TryTransition(TurnStatus expected, TurnStatus next) =>
        Interlocked.CompareExchange(ref _status, (int)next, (int)expected) == (int)expected;

    public bool TryTerminate(TurnStatus terminal) =>
        terminal is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted &&
        TryTransition(TurnStatus.Running, terminal);
}
