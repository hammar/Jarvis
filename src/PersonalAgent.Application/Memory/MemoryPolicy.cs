using System.Text;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Memory;

/// <summary>Identifies the exact reviewed version of a memory dependency.</summary>
/// <param name="Id">Stable fact ID.</param>
/// <param name="Version">Reviewed positive version; zero denotes an as-yet uncreated fact.</param>
public sealed record MemoryReference(MemoryFactId Id, long Version);

/// <summary>Describes an exact owner-reviewable mutation, never an authorization.</summary>
/// <param name="Operation">create, update, delete, supersede, or retain_both.</param>
/// <param name="FactId">Target ID, or deterministic new fact ID.</param>
/// <param name="ExpectedVersion">Zero for a new fact; current target version otherwise.</param>
/// <param name="Subject">Structured subject, required except for delete.</param>
/// <param name="Key">Structured key, required except for delete.</param>
/// <param name="Value">Proposed explicit fact, required except for delete.</param>
/// <param name="Conflicts">Complete reviewed active subject/key conflict set excluding the target.</param>
/// <param name="DistinguishingContext">Required owner-reviewed qualifier when retaining both.</param>
public sealed record MemoryChange(
    string Operation, MemoryFactId FactId, long ExpectedVersion,
    string? Subject, string? Key, string? Value,
    IReadOnlyList<MemoryReference> Conflicts, string? DistinguishingContext = null);

/// <summary>Contains a host-bound immutable proposed memory change.</summary>
/// <param name="ActionId">Exact journal/approval identity.</param>
/// <param name="OwnerId">Authenticated owner, not model input.</param>
/// <param name="SourceMessageId">Verified original owner-message provenance.</param>
/// <param name="Change">Validated reviewed content.</param>
public sealed record ConfirmedMemoryCommand(
    ActionId ActionId, string OwnerId, Guid SourceMessageId, MemoryChange Change);

/// <summary>Returns content-free durable evidence of a committed memory change.</summary>
/// <param name="ActionId">Unique committing action.</param>
/// <param name="FactId">Affected fact.</param>
/// <param name="Version">New version, including deletion version.</param>
/// <param name="Deleted">Whether the operation deleted the fact.</param>
public sealed record MemoryReceipt(ActionId ActionId, MemoryFactId FactId, long Version, bool Deleted);

/// <summary>Provides owner-scoped local memory and atomic approved commits.</summary>
public interface IConfirmedMemoryStore
{
    /// <summary>Reads the originating owner message for a trusted turn, or null when unavailable or not owned.</summary>
    ValueTask<ConversationMessage?> ReadSourceAsync(string ownerId, TurnId turnId, CancellationToken cancellationToken);
    /// <summary>Reads a bounded page of active local facts for the authenticated owner.</summary>
    /// <param name="ownerId">Authenticated owner identity.</param>
    /// <param name="maximumFacts">Maximum number of facts, from 1 through 100.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>Active local facts in most-recently-updated order.</returns>
    ValueTask<IReadOnlyList<MemoryFact>> ReadActiveAsync(
        string ownerId,
        int maximumFacts,
        CancellationToken cancellationToken);
    /// <summary>Searches active permitted local facts; query is at most 512 characters and limit 1-100.</summary>
    ValueTask<IReadOnlyList<MemoryFact>> SearchAsync(string ownerId, string query, int limit, CancellationToken cancellationToken);
    /// <summary>Reads one permitted local fact, or null without revealing another owner's existence.</summary>
    ValueTask<MemoryFact?> GetAsync(string ownerId, MemoryFactId id, CancellationToken cancellationToken);
    /// <summary>Returns the complete bounded active subject/key set for explicit conflict review.</summary>
    ValueTask<IReadOnlyList<MemoryFact>> FindConflictsAsync(string ownerId, string subject, string key, CancellationToken cancellationToken);
    /// <summary>Commits only an exact executing action with an approved, unexpired owner-bound approval; rechecks conflicts atomically.</summary>
    ValueTask<MemoryReceipt> CommitAsync(ConfirmedMemoryCommand command, CancellationToken cancellationToken);
    /// <summary>Observes a durable commit receipt without repeating a mutation.</summary>
    ValueTask<MemoryReceipt?> GetReceiptAsync(string ownerId, ActionId actionId, CancellationToken cancellationToken);
}

/// <summary>Enforces deterministic memory validation, privacy and conflict resolution.</summary>
public static class MemoryPolicy
{
    /// <summary>Normalizes identity without attempting semantic similarity.</summary>
    public static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    /// <summary>Validates a host owner identity before any lookup.</summary>
    public static void ValidateOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128)
        {
            throw new ArgumentException("An authenticated owner is required.", nameof(ownerId));
        }
    }

    /// <summary>Allows supported classifications only on the local route; reclassification is not a model capability.</summary>
    public static bool CanRead(string privacyClass, ProviderKind provider) =>
        provider == ProviderKind.Local && privacyClass is "LocalOnly" or "Private";

    /// <summary>Validates sizes, operation, identifiers and exact version references.</summary>
    public static void Validate(MemoryChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.FactId.Value == Guid.Empty || change.Conflicts is null || change.Conflicts.Count > 100
            || change.Conflicts.Any(reference => reference is null || reference.Id.Value == Guid.Empty
                || reference.Version < 1 || reference.Id == change.FactId)
            || change.Conflicts.Select(reference => reference.Id).Distinct().Count() != change.Conflicts.Count
            || change.Operation is not ("create" or "update" or "delete" or "supersede" or "retain_both")
            || change.ExpectedVersion < 0
            || (change.Operation is "update" or "delete") != (change.ExpectedVersion > 0))
        {
            throw new ArgumentException("Invalid memory operation or version references.", nameof(change));
        }
        if (change.Operation == "delete")
        {
            if (change.Subject is not null || change.Key is not null || change.Value is not null
                || change.DistinguishingContext is not null || change.Conflicts.Count != 0)
            {
                throw new ArgumentException("Deletion carries only a fact ID and version.", nameof(change));
            }
            return;
        }
        RequireText(change.Subject, 256);
        RequireText(change.Key, 128);
        RequireText(change.Value, 8192);
        if (change.DistinguishingContext is not null)
        {
            RequireText(change.DistinguishingContext, 512);
        }
        if (change.Operation is "supersede" or "retain_both" && change.Conflicts.Count == 0
            || (change.Operation == "retain_both" || change.Operation == "update" && change.Conflicts.Count > 0)
                && change.DistinguishingContext is null
            || change.Operation == "create" && change.Conflicts.Count != 0)
        {
            throw new ArgumentException("Conflicts require explicit supersede or qualified retain-both.", nameof(change));
        }
    }

    /// <summary>Rejects any changed conflict set; ordinary create/update never silently overwrites.</summary>
    public static void ValidateConflicts(MemoryChange change, IReadOnlyList<MemoryFact> current)
    {
        var actual = current.Where(fact => fact.Id != change.FactId)
            .Select(fact => new MemoryReference(fact.Id, fact.Version)).OrderBy(item => item.Id.Value).ToArray();
        var expected = change.Conflicts.OrderBy(item => item.Id.Value).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new PersistenceConcurrencyException("Memory conflicts changed; review a new proposal.");
        }
        if (change.Operation is "retain_both" or "update" && actual.Length >= 100)
        {
            throw new PersistenceConcurrencyException("The active memory group is full; delete or supersede facts first.");
        }
        if (change.Operation is "retain_both" or "update"
            && current.Any(fact => fact.Id != change.FactId && string.Equals(fact.DistinguishingContext?.Trim(),
                change.DistinguishingContext?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new PersistenceConcurrencyException("Retained facts require distinct context.");
        }
    }

    private static void RequireText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
        {
            throw new ArgumentException("Memory text is empty, oversized, or contains a null character.");
        }
    }
}
