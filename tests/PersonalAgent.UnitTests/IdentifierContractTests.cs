using PersonalAgent.Domain;
using PersonalAgent.Application;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class IdentifierContractTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void NewConversationIdentifiersAreDistinctAndNonEmpty()
    {
        var first = ConversationId.New();
        var second = ConversationId.New();
        var action = ActionId.New();
        var approval = ApprovalId.New();
        var memoryFact = MemoryFactId.New();
        var job = JobId.New();

        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(first, second);
        Assert.NotEqual(Guid.Empty, action.Value);
        Assert.NotEqual(Guid.Empty, approval.Value);
        Assert.NotEqual(Guid.Empty, memoryFact.Value);
        Assert.NotEqual(Guid.Empty, job.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TurnIdentifierPreservesItsValue()
    {
        var value = Guid.NewGuid();
        var turn = new TurnId(value);

        Assert.Equal(value, turn.Value);
        Assert.Equal(value, turn.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TurnEventKeepsTheApplicationTurnAndUtcObservation()
    {
        var turnId = TurnId.New();
        var observedAtUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var started = new TurnStarted(turnId, observedAtUtc);

        Assert.Equal(turnId, started.TurnId);
        Assert.Equal(observedAtUtc, started.OccurredAtUtc);
    }
}
