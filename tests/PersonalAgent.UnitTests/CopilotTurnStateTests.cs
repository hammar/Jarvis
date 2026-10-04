using PersonalAgent.Application;
using PersonalAgent.Domain;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class CopilotTurnStateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void TerminalStatusCompareAndSwapAllowsExactlyOneConcurrentOutcome()
    {
        var status = new TurnStatusGate();
        Assert.True(status.TryTransition(TurnStatus.Received, TurnStatus.Running));
        var outcomes = new[]
        {
            TurnStatus.Completed,
            TurnStatus.Failed,
            TurnStatus.Cancelled,
            TurnStatus.Interrupted,
        };
        var winners = 0;

        Parallel.ForEach(outcomes, outcome =>
        {
            if (status.TryTerminate(outcome))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        Assert.Contains(status.Status, outcomes);
        Assert.False(status.TryTerminate(TurnStatus.Completed));

        foreach (var outcome in outcomes)
        {
            var independentStatus = new TurnStatusGate();
            Assert.True(independentStatus.TryTransition(TurnStatus.Received, TurnStatus.Running));
            Assert.True(independentStatus.TryTerminate(outcome));
            Assert.Equal(outcome, independentStatus.Status);
        }

        Assert.False(new TurnStatusGate().TryTerminate(TurnStatus.Received));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReplayingAfterDisconnectReturnsOrderedEventsWithoutRepeatingTerminalOutcome()
    {
        var turnId = TurnId.New();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero));
        var events = new TurnEventSequencer(clock);
        events.Emit(new TurnStarted(turnId, clock.UtcNow));
        events.Emit(new RouteSelected(turnId, clock.UtcNow, ProviderKind.Local, "host_selected"));
        events.Emit(new TextDelta(turnId, clock.UtcNow, "streamed result"));
        events.Emit(new TurnCompleted(turnId, clock.UtcNow));
        events.Emit(new TurnFailed(turnId, clock.UtcNow, "late_failure"));

        var replay = events.ReadAfter(sequenceNumber: 2);

        Assert.Equal([3L, 4L], replay.Select(item => item.SequenceNumber));
        Assert.IsType<TextDelta>(replay[0]);
        Assert.Single(replay.OfType<TurnCompleted>());
        Assert.DoesNotContain(replay, item => item is TurnFailed);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
