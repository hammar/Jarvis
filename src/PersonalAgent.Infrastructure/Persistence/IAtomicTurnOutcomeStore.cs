using PersonalAgent.Application;
using PersonalAgent.Domain;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Persists a terminal turn status and its terminal event in one storage transaction.</summary>
public interface IAtomicTurnOutcomeStore
{
    /// <summary>
    /// Updates a turn with optimistic concurrency and appends its terminal event atomically.
    /// </summary>
    /// <param name="turnId">Application turn whose outcome is being committed.</param>
    /// <param name="status">Terminal status to persist.</param>
    /// <param name="expectedVersion">Current turn version required for the update.</param>
    /// <param name="updatedAtUtc">UTC time assigned to the status update.</param>
    /// <param name="eventType">Stable terminal event type.</param>
    /// <param name="payloadJson">Structured terminal event payload.</param>
    /// <param name="occurredAtUtc">UTC time the terminal outcome occurred.</param>
    /// <param name="cancellationToken">Token that cancels the operation before commit.</param>
    /// <returns>The durably sequenced terminal event.</returns>
    /// <exception cref="PersistenceConcurrencyException">
    /// The turn no longer has the expected version or is missing.
    /// </exception>
    ValueTask<PersistedTurnEvent> UpdateTurnStatusAndAppendEventAsync(
        TurnId turnId,
        TurnStatus status,
        long expectedVersion,
        DateTimeOffset updatedAtUtc,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}
