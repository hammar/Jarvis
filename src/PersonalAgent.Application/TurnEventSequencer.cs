namespace PersonalAgent.Application;

/// <summary>Assigns ordered per-turn sequence numbers and supports cursor-based replay.</summary>
internal sealed class TurnEventSequencer(IClock clock)
{
    private readonly object _gate = new();
    private readonly List<AgentEvent> _history = [];
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

            _history.Add(sequenced);
            return sequenced;
        }
    }

    public IReadOnlyList<AgentEvent> ReadAfter(long sequenceNumber)
    {
        lock (_gate)
        {
            return _history.Where(item => item.SequenceNumber > sequenceNumber).ToArray();
        }
    }
}
