using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Assigns ordered per-turn sequence numbers and permits a single terminal event.</summary>
internal sealed class TurnEventSequencer(IClock clock)
{
    private readonly object _gate = new();
    private long _sequenceNumber;
    private bool _terminalEmitted;

    public AgentEvent? Emit(AgentEvent item)
    {
        lock (_gate)
        {
            if (_terminalEmitted)
            {
                return null;
            }

            var sequenced = item with
            {
                SequenceNumber = checked(++_sequenceNumber),
                OccurredAtUtc = clock.UtcNow,
            };
            if (sequenced is TurnCompleted or TurnFailed or TurnCancelled or TurnInterrupted)
            {
                _terminalEmitted = true;
            }

            return sequenced;
        }
    }
}
