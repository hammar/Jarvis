using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Tools;

/// <summary>Dispatches only registered, validated, allowlisted actions and records every execution outcome.</summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private const int MaximumToolNameLength = 64;
    private const int MaximumCallIdLength = 128;
    private readonly ToolRegistry registry;
    private readonly IToolAuthorizationPolicy policy;
    private readonly IActionJournalStore journal;
    private readonly IApprovalService approvals;
    private readonly IClock clock;
    private readonly TimeSpan verificationTimeout;
    private readonly ConcurrentDictionary<ActionId, byte> activeExecutions = new();

    /// <summary>Creates the host tool dispatcher.</summary>
    /// <param name="registry">Immutable host-registered tools.</param>
    /// <param name="policy">Current exact operation/entity allowlist.</param>
    /// <param name="journal">Durable action journal.</param>
    /// <param name="approvals">Owner-bound exact approval service.</param>
    /// <param name="clock">Host UTC clock.</param>
    /// <param name="verificationTimeout">Maximum time spent observing an uncertain or completed action.</param>
    public ToolDispatcher(
        ToolRegistry registry,
        IToolAuthorizationPolicy policy,
        IActionJournalStore journal,
        IApprovalService approvals,
        IClock clock,
        TimeSpan verificationTimeout)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (verificationTimeout <= TimeSpan.Zero || verificationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(verificationTimeout));
        }

        this.verificationTimeout = verificationTimeout;
    }

    /// <inheritdoc />
    public async ValueTask<ToolDispatchResult> DispatchAsync(
        ToolDispatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidRequest(request))
        {
            return Rejected("invalid_tool_request");
        }

        if (!registry.TryGetRuntime(request.ToolName, out var registration) || registration is null)
        {
            return Rejected("unregistered_tool");
        }

        var preparation = await registration.PrepareAsync(request, cancellationToken);
        if (preparation.Result.Status == ToolPreparationStatus.ClarificationRequired)
        {
            return new("ClarificationRequired", null, preparation.Result.ReasonCode);
        }

        if (preparation.Result.Status == ToolPreparationStatus.Rejected)
        {
            return Rejected(preparation.Result.ReasonCode ?? "invalid_tool_arguments");
        }

        var draft = preparation.Result.Action
            ?? throw new InvalidOperationException("A ready tool registration returned no action.");
        if (!IsValidDraft(draft))
        {
            return Rejected("invalid_tool_action");
        }

        var authorization = policy.Evaluate(request.ToolName, draft, registration.RiskClass);
        var canonicalArguments = CanonicalizeObjectJson(draft.CanonicalArguments);
        if (canonicalArguments is null)
        {
            return Rejected("invalid_tool_action");
        }

        if (string.IsNullOrWhiteSpace(request.OwnerId) || request.OwnerId.Length > 128)
        {
            return Rejected("authenticated_owner_required");
        }

        var effectiveRisk = authorization.EffectiveRisk == ToolRiskClass.LowRiskWrite
            && !draft.ExplicitlyRequestedByOwner
                ? ToolRiskClass.ApprovalRequired
                : authorization.EffectiveRisk;
        var envelope = new CanonicalActionEnvelope(
            1,
            request.TurnId.Value.ToString("D"),
            request.CallId,
            request.OwnerId,
            request.ToolName,
            draft.ActionType,
            draft.Operation,
            draft.ResolvedEntityId,
            authorization.PolicyVersion,
            effectiveRisk,
            HashText(request.CurrentUserRequest),
            canonicalArguments);
        var serializedEnvelope = JsonSerializer.Serialize(envelope);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedEnvelope)));
        var actionId = CreateActionId(request.TurnId, request.CallId);
        var action = await GetOrCreateActionAsync(
            actionId,
            draft.ActionType,
            serializedEnvelope,
            requestHash,
            cancellationToken);

        if (!string.Equals(action.RequestHash, requestHash, StringComparison.Ordinal))
        {
            if (IsTerminal(action.Status) && SameProposalIgnoringPolicy(action.CanonicalArguments, envelope))
            {
                return Recorded(action);
            }

            if (action.Status is "Prepared" or "AwaitingApproval")
            {
                action = await TransitionAsync(action, "Rejected", cancellationToken);
            }

            return new("Rejected", null, "action_identity_reused", action.Id);
        }

        if (IsTerminal(action.Status))
        {
            return Recorded(action);
        }

        if (action.Status == "Executing")
        {
            return await ReconcileInterruptedExecutionAsync(
                action,
                preparation.Arguments!,
                registration,
                draft,
                cancellationToken);
        }

        if (!authorization.Allowed)
        {
            if (action.Status == "Prepared")
            {
                action = await TransitionAsync(action, "Rejected", cancellationToken);
            }

            return new(action.Status, null, authorization.ReasonCode, action.Id);
        }

        if (effectiveRisk == ToolRiskClass.ApprovalRequired
            || action.Status == "AwaitingApproval")
        {
            return await DispatchApprovalRequiredAsync(
                action,
                actionId,
                request,
                preparation.Arguments!,
                registration,
                draft,
                cancellationToken);
        }

        if (effectiveRisk == ToolRiskClass.Unsupported)
        {
            action = await TransitionAsync(action, "Rejected", cancellationToken);
            return new(action.Status, null, "unsupported_action", action.Id);
        }

        return await ExecuteOnceAsync(
            action,
            preparation.Arguments!,
            registration,
            draft,
            cancellationToken);
    }

    private async ValueTask<ActionJournalEntry> GetOrCreateActionAsync(
        ActionId actionId,
        string actionType,
        string canonicalEnvelope,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await journal.GetAsync(actionId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var nowUtc = clock.UtcNow.ToUniversalTime();
        return await journal.CreateAsync(
            new ActionJournalEntry(
                actionId,
                actionType,
                canonicalEnvelope,
                requestHash,
                "Prepared",
                nowUtc,
                nowUtc,
                1),
            cancellationToken);
    }

    private async ValueTask<ToolDispatchResult> DispatchApprovalRequiredAsync(
        ActionJournalEntry action,
        ActionId actionId,
        ToolDispatchRequest request,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        if (action.Status == "Prepared")
        {
            action = await TransitionAsync(action, "AwaitingApproval", cancellationToken);
        }

        if (action.Status != "AwaitingApproval")
        {
            return Recorded(action);
        }

        var approvalId = new ApprovalId(actionId.Value);
        var approval = await approvals.GetAsync(approvalId, cancellationToken)
            ?? await approvals.CreateAsync(actionId, request.OwnerId!, cancellationToken);
        if (!string.Equals(approval.OwnerId, request.OwnerId, StringComparison.Ordinal))
        {
            return new("Rejected", null, "approval_owner_mismatch", actionId, approvalId);
        }

        if (approval.Status == "Pending" && clock.UtcNow.ToUniversalTime() >= approval.ExpiresAtUtc)
        {
            approval = await approvals.ResolveAsync(
                approval.Id,
                request.OwnerId!,
                approval.ExpectedVersion,
                approved: false,
                cancellationToken);
        }

        if (approval.Status == "Rejected" || approval.Status == "Expired")
        {
            var currentAction = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared while resolving its approval.");
            if (currentAction.Status != approval.Status)
            {
                action = await TransitionAsync(currentAction, approval.Status, cancellationToken);
            }
            else
            {
                action = currentAction;
            }

            return new(action.Status, null, $"approval_{approval.Status.ToLowerInvariant()}", action.Id, approvalId);
        }

        if (approval.Status == "Pending")
        {
            return new(
                "AwaitingApproval",
                null,
                "owner_approval_required",
                action.Id,
                approval.Id,
                approval.ExpiresAtUtc);
        }

        if (approval.Status != "Approved")
        {
            return new("Rejected", null, "invalid_approval_state", action.Id, approvalId);
        }

        if (clock.UtcNow.ToUniversalTime() >= approval.ExpiresAtUtc)
        {
            action = await TransitionAsync(action, "Expired", cancellationToken);
            return new("Expired", null, "approval_expired_before_execution", action.Id, approvalId);
        }

        return await ExecuteOnceAsync(action, arguments, registration, draft, cancellationToken);
    }

    private async ValueTask<ToolDispatchResult> ExecuteOnceAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        if (action.Status is not ("Prepared" or "AwaitingApproval"))
        {
            return Recorded(action);
        }

        if (!activeExecutions.TryAdd(action.Id, 0))
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during execution arbitration.");
            return Recorded(current);
        }

        try
        {
            return await ExecuteClaimedOnceAsync(action, arguments, registration, draft, cancellationToken);
        }
        finally
        {
            activeExecutions.TryRemove(action.Id, out _);
        }
    }

    private async ValueTask<ToolDispatchResult> ExecuteClaimedOnceAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        try
        {
            action = await journal.UpdateStatusAsync(
                action.Id,
                "Executing",
                action.Version,
                clock.UtcNow.ToUniversalTime(),
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            action = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during execution arbitration.");
            return Recorded(action);
        }

        if (action.Status != "Executing")
        {
            return Recorded(action);
        }

        var executionEntryVersion = action.Version;
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelledBeforeSend = await journal.UpdateStatusAsync(
                action.Id,
                "Failed",
                executionEntryVersion,
                clock.UtcNow.ToUniversalTime(),
                CancellationToken.None);
            return new("Failed", null, "cancelled_before_send", cancelledBeforeSend.Id);
        }

        ToolExecutionOutcome execution;
        try
        {
            execution = await registration.ExecuteAsync(arguments, draft, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var unknown = await journal.UpdateStatusAsync(
                action.Id,
                "Unknown",
                executionEntryVersion,
                clock.UtcNow.ToUniversalTime(),
                CancellationToken.None);
            return new("Unknown", null, "execution_cancelled_after_dispatch", unknown.Id);
        }
        catch (Exception executionException)
        {
            try
            {
                await journal.UpdateStatusAsync(
                    action.Id,
                    "Unknown",
                    executionEntryVersion,
                    clock.UtcNow.ToUniversalTime(),
                    CancellationToken.None);
            }
            catch (Exception journalException)
            {
                throw new AggregateException(
                    "Tool execution failed and its uncertain outcome could not be journaled.",
                    executionException,
                    journalException);
            }

            throw;
        }

        if (!Enum.IsDefined(execution.Status))
        {
            await JournalUnknownBeforeThrowAsync(
                action.Id,
                executionEntryVersion,
                "A tool registration returned an unknown execution status, so its outcome is uncertain.");
            throw new InvalidOperationException("A tool registration returned an unknown execution status.");
        }

        if (execution.Status == ToolExecutionStatus.Failed)
        {
            var failed = await journal.UpdateStatusAsync(
                action.Id,
                "Failed",
                executionEntryVersion,
                clock.UtcNow.ToUniversalTime(),
                CancellationToken.None);
            return new("Failed", execution.ResultJson, execution.ReasonCode ?? "tool_execution_failed", failed.Id);
        }

        ToolVerificationOutcome verification;
        using (var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            verificationCancellation.CancelAfter(verificationTimeout);
            try
            {
                verification = await registration.VerifyAsync(
                    arguments,
                    draft,
                    execution,
                    verificationCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return await CompleteUnknownAsync(action, execution.ResultJson, "verification_interrupted");
            }
            catch (Exception verificationException)
            {
                try
                {
                    await journal.UpdateStatusAsync(
                        action.Id,
                        "Unknown",
                        executionEntryVersion,
                        clock.UtcNow.ToUniversalTime(),
                        CancellationToken.None);
                }
                catch (Exception journalException)
                {
                    throw new AggregateException(
                        "Tool verification failed and its uncertain outcome could not be journaled.",
                        verificationException,
                        journalException);
                }

                throw;
            }
        }

        if (!Enum.IsDefined(verification.Status))
        {
            await JournalUnknownBeforeThrowAsync(
                action.Id,
                executionEntryVersion,
                "A tool registration returned an unknown verification status, so its outcome is uncertain.");
            throw new InvalidOperationException("A tool registration returned an unknown verification status.");
        }

        var finalStatus = verification.Status switch
        {
            ToolVerificationStatus.Verified => "Succeeded",
            ToolVerificationStatus.NotApplied when execution.Status == ToolExecutionStatus.Completed => "Failed",
            _ => "Unknown"
        };
        var reasonCode = finalStatus switch
        {
            "Succeeded" => null,
            "Failed" => execution.ReasonCode ?? "action_not_applied",
            _ => verification.ReasonCode ?? execution.ReasonCode ?? "physical_outcome_uncertain"
        };
        var completed = await journal.UpdateStatusAsync(
            action.Id,
            finalStatus,
            executionEntryVersion,
            clock.UtcNow.ToUniversalTime(),
            CancellationToken.None);
        return new(finalStatus, execution.ResultJson, reasonCode, completed.Id);
    }

    private async ValueTask<ToolDispatchResult> ReconcileInterruptedExecutionAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        if (!activeExecutions.TryAdd(action.Id, 0))
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during interrupted-execution recovery.");
            return Recorded(current);
        }

        try
        {
            ToolVerificationOutcome verification;
            using (var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                verificationCancellation.CancelAfter(verificationTimeout);
                try
                {
                    verification = await registration.VerifyAsync(
                        arguments,
                        draft,
                        new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "execution_interrupted"),
                        verificationCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return await CompleteUnknownAsync(action, null, "interrupted_execution_observation_interrupted");
                }
                catch (Exception verificationException)
                {
                    try
                    {
                        await journal.UpdateStatusAsync(
                            action.Id,
                            "Unknown",
                            action.Version,
                            clock.UtcNow.ToUniversalTime(),
                            CancellationToken.None);
                    }
                    catch (Exception journalException)
                    {
                        throw new AggregateException(
                            "Interrupted action verification failed and its uncertain outcome could not be journaled.",
                            verificationException,
                            journalException);
                    }

                    throw;
                }
            }

            if (!Enum.IsDefined(verification.Status))
            {
                await JournalUnknownBeforeThrowAsync(
                    action.Id,
                    action.Version,
                    "Interrupted action verification returned an unknown status, so its outcome remains uncertain.");
                throw new InvalidOperationException("A tool registration returned an unknown verification status.");
            }

            var finalStatus = verification.Status switch
            {
                ToolVerificationStatus.Verified => "Succeeded",
                ToolVerificationStatus.NotApplied => "Failed",
                _ => "Unknown"
            };
            var reasonCode = finalStatus switch
            {
                "Succeeded" => null,
                "Failed" => "interrupted_execution_not_applied",
                _ => verification.ReasonCode ?? "interrupted_execution_outcome_unknown"
            };
            ActionJournalEntry completed;
            try
            {
                completed = await journal.UpdateStatusAsync(
                    action.Id,
                    finalStatus,
                    action.Version,
                    clock.UtcNow.ToUniversalTime(),
                    CancellationToken.None);
            }
            catch (PersistenceConcurrencyException)
            {
                completed = await journal.GetAsync(action.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The action disappeared during interrupted-execution recovery.");
                return Recorded(completed);
            }

            return new(finalStatus, null, reasonCode, completed.Id);
        }
        finally
        {
            activeExecutions.TryRemove(action.Id, out _);
        }
    }

    private async ValueTask<ToolDispatchResult> CompleteUnknownAsync(
        ActionJournalEntry action,
        string? resultJson,
        string reasonCode)
    {
        var unknown = await journal.UpdateStatusAsync(
            action.Id,
            "Unknown",
            action.Version,
            clock.UtcNow.ToUniversalTime(),
            CancellationToken.None);
        return new("Unknown", resultJson, reasonCode, unknown.Id);
    }

    private async ValueTask JournalUnknownBeforeThrowAsync(
        ActionId actionId,
        long expectedVersion,
        string message)
    {
        try
        {
            await journal.UpdateStatusAsync(
                actionId,
                "Unknown",
                expectedVersion,
                clock.UtcNow.ToUniversalTime(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(message, exception);
        }
    }

    private async ValueTask<ActionJournalEntry> TransitionAsync(
        ActionJournalEntry action,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            return await journal.UpdateStatusAsync(
                action.Id,
                status,
                action.Version,
                clock.UtcNow.ToUniversalTime(),
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during a concurrent journal transition.");
        }
    }

    private static ActionId CreateActionId(TurnId turnId, string callId)
    {
        var material = Encoding.UTF8.GetBytes($"{turnId.Value:D}\n{callId}");
        var digest = SHA256.HashData(material);
        return new ActionId(new Guid(digest.AsSpan(0, 16)));
    }

    private static string HashText(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static bool IsValidRequest(ToolDispatchRequest request) =>
        request.TurnId.Value != Guid.Empty
        && !string.IsNullOrWhiteSpace(request.CallId)
        && request.CallId.Length <= MaximumCallIdLength
        && !request.CallId.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(request.ToolName)
        && request.ToolName.Length <= MaximumToolNameLength
        && !string.IsNullOrWhiteSpace(request.ArgumentsJson);

    private static bool IsValidDraft(ToolActionDraft action) =>
        !string.IsNullOrWhiteSpace(action.ActionType)
        && action.ActionType.Length <= 96
        && !string.IsNullOrWhiteSpace(action.Operation)
        && action.Operation.Length <= 64
        && action.ResolvedEntityId is not { Length: 0 or > 256 };

    private static string? CanonicalizeObjectJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
            {
                return null;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Canonical action JSON contains an unsupported token.");
        }
    }

    private static bool SameProposalIgnoringPolicy(string storedEnvelope, CanonicalActionEnvelope current)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<CanonicalActionEnvelope>(storedEnvelope);
            return stored is not null
                && stored.FormatVersion == current.FormatVersion
                && stored.TurnId == current.TurnId
                && stored.CallId == current.CallId
                && stored.OwnerId == current.OwnerId
                && stored.ToolName == current.ToolName
                && stored.ActionType == current.ActionType
                && stored.Operation == current.Operation
                && stored.EntityId == current.EntityId
                && stored.CurrentRequestHash == current.CurrentRequestHash
                && stored.CanonicalArguments == current.CanonicalArguments;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTerminal(string status) =>
        status is "Succeeded" or "Failed" or "Unknown" or "Rejected" or "Expired";

    private static ToolDispatchResult Recorded(ActionJournalEntry action) =>
        new(action.Status, null, "recorded_action_outcome", action.Id);

    private static ToolDispatchResult Rejected(string reasonCode) =>
        new("Rejected", null, reasonCode);

    private sealed record CanonicalActionEnvelope(
        int FormatVersion,
        string TurnId,
        string CallId,
        string OwnerId,
        string ToolName,
        string ActionType,
        string Operation,
        string? EntityId,
        string PolicyVersion,
        ToolRiskClass RiskClass,
        string CurrentRequestHash,
        string CanonicalArguments);
}
