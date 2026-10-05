using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

internal enum CopilotTurnSignal
{
    Completed,
    Cancelled,
    ToolBudgetExceeded,
    DeadlineExceeded,
    RuntimeCleanupFailed,
    Failed
}

internal sealed class CopilotTurnStateMachine(
    IConversationStore conversations,
    IAtomicTurnOutcomeStore terminalOutcomes,
    IClock clock)
{
    public async Task EnsureRunningAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var turn = await conversations.GetTurnAsync(turnId, cancellationToken)
            ?? throw new InvalidOperationException("The application turn does not exist.");
        if (IsTerminal(turn.Status))
        {
            throw new PersistenceConcurrencyException("The application turn is already terminal.");
        }

        if (turn.Status != TurnStatus.Running)
        {
            await conversations.UpdateTurnStatusAsync(
                turnId,
                TurnStatus.Running,
                turn.Version,
                clock.UtcNow,
                cancellationToken);
        }
    }

    public async Task SetTerminalOutcomeAsync(
        TurnId turnId,
        TurnStatus status,
        AgentEvent terminalEvent,
        CancellationToken cancellationToken)
    {
        if (!IsTerminal(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A terminal turn status is required.");
        }

        ArgumentNullException.ThrowIfNull(terminalEvent);
        if (terminalEvent.TurnId != turnId)
        {
            throw new ArgumentException("The terminal event must belong to the updated turn.", nameof(terminalEvent));
        }

        var turn = await conversations.GetTurnAsync(turnId, cancellationToken)
            ?? throw new InvalidOperationException("The application turn does not exist.");
        if (IsTerminal(turn.Status))
        {
            throw new PersistenceConcurrencyException("The application turn is already terminal.");
        }

        await terminalOutcomes.UpdateTurnStatusAndAppendEventAsync(
            turnId,
            status,
            turn.Version,
            clock.UtcNow,
            terminalEvent.GetType().Name,
            JsonSerializer.Serialize(terminalEvent, terminalEvent.GetType()),
            terminalEvent.OccurredAtUtc,
            cancellationToken);
    }

    public static (TurnStatus Status, AgentEvent Event) CreateTerminalOutcome(
        TurnId turnId,
        IClock clock,
        CopilotTurnSignal signal) =>
        signal switch
        {
            CopilotTurnSignal.Completed =>
                (TurnStatus.Completed, new TurnCompleted(turnId, clock.UtcNow)),
            CopilotTurnSignal.Cancelled =>
                (TurnStatus.Cancelled, new TurnCancelled(turnId, clock.UtcNow)),
            CopilotTurnSignal.ToolBudgetExceeded =>
                (TurnStatus.Failed, new TurnFailed(turnId, clock.UtcNow, "tool_budget_exceeded")),
            CopilotTurnSignal.DeadlineExceeded =>
                (TurnStatus.Interrupted, new TurnInterrupted(turnId, clock.UtcNow, "engine_deadline_exceeded")),
            CopilotTurnSignal.RuntimeCleanupFailed =>
                (TurnStatus.Interrupted, new TurnInterrupted(turnId, clock.UtcNow, "runtime_cleanup_failed")),
            CopilotTurnSignal.Failed =>
                (TurnStatus.Failed, new TurnFailed(turnId, clock.UtcNow, "engine_failure")),
            _ => throw new ArgumentOutOfRangeException(nameof(signal), "The engine terminal signal is not supported.")
        };

    private static bool IsTerminal(TurnStatus status) =>
        status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled or TurnStatus.Interrupted;
}
