using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Explicitly denies every tool proposal in M1, where no tools are registered.</summary>
public sealed class RejectingToolDispatcher : IToolDispatcher
{
    /// <inheritdoc />
    public void ReleaseTurn(TurnId turnId)
    {
    }

    /// <inheritdoc />
    public ValueTask<ToolDispatchResult> DispatchAsync(
        ToolDispatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ToolDispatchResult("Rejected", null, "tools_not_available_in_m1"));
    }
}
