using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalAgent.Application.Memory;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Tools;

/// <summary>Dispatches only registered, validated, allowlisted actions and records every execution outcome.</summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private const int MaximumToolNameLength = 64;
    private const int MaximumCallIdLength = 128;
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        RespectRequiredConstructorParameters = true
    };
    private readonly ToolRegistry registry;
    private readonly IToolAuthorizationPolicy policy;
    private readonly IActionJournalStore journal;
    private readonly IApprovalService approvals;
    private readonly IClock clock;
    private readonly TimeSpan verificationTimeout;
    private readonly ConcurrentDictionary<ActionId, byte> activeExecutions = new();
    private readonly ConcurrentDictionary<(TurnId Turn, string Proposal), FailureBudget> failureBudgets = new();
    private readonly Dictionary<ActionId, (TurnId Turn, string Proposal)> approvalBudgets = [];
    private readonly Dictionary<TurnId, int> activeDispatchesByTurn = [];
    private readonly object failureBudgetLifecycleGate = new();

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
    public void ReleaseTurn(TurnId turnId)
    {
        lock (failureBudgetLifecycleGate)
        {
            foreach (var pair in failureBudgets.Where(pair => pair.Key.Turn == turnId).ToArray())
            {
                pair.Value.TurnReleased = true;
                RemoveReleasedBudgetIfIdle(pair.Key, pair.Value);
            }
        }
    }

    /// <inheritdoc />
    public void ReleaseApproval(ActionId actionId)
    {
        lock (failureBudgetLifecycleGate)
        {
            if (!approvalBudgets.Remove(actionId, out var key)
                || !failureBudgets.TryGetValue(key, out var budget))
            {
                return;
            }

            budget.PendingApprovals.Remove(actionId);
            RemoveReleasedBudgetIfIdle(key, budget);
        }
    }

    /// <summary>Records an explicit Unknown outcome when an interrupted action can no longer be observed.</summary>
    /// <param name="action">Owner-validated action still marked Executing.</param>
    /// <param name="cancellationToken">Token that cancels the journal update before it commits.</param>
    /// <returns>The durable action, preserving any concurrent execution or terminal outcome.</returns>
    public async ValueTask<ActionJournalEntry> MarkUnavailableExecutingActionUnknownAsync(
        ActionJournalEntry action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Status != "Executing")
        {
            return action;
        }
        if (!activeExecutions.TryAdd(action.Id, 0))
        {
            return await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidDataException("The action disappeared during unavailable-capability recovery.");
        }

        try
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidDataException("The action disappeared during unavailable-capability recovery.");
            if (current.Status != "Executing")
            {
                return current;
            }

            try
            {
                return await journal.UpdateStatusAsync(
                    current.Id,
                    "Unknown",
                    current.Version,
                    clock.UtcNow.ToUniversalTime(),
                    cancellationToken,
                    new("Unknown", null, "recovery_capability_unavailable", current.Id));
            }
            catch (PersistenceConcurrencyException)
            {
                current = await journal.GetAsync(current.Id, cancellationToken)
                    ?? throw new InvalidDataException("The action disappeared during unavailable-capability recovery.");
                if (current.Status is "Executing" or "Prepared" or "AwaitingApproval")
                {
                    throw;
                }

                return current;
            }
        }
        finally
        {
            activeExecutions.TryRemove(action.Id, out _);
        }
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

        BeginDispatch(request.TurnId);
        try
        {
            return await DispatchCoreAsync(request, cancellationToken);
        }
        finally
        {
            EndDispatch(request.TurnId);
        }
    }

    private async ValueTask<ToolDispatchResult> DispatchCoreAsync(
        ToolDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ToolName.StartsWith("memory.", StringComparison.Ordinal))
        {
            var previous = await journal.GetAsync(CreateActionId(request.TurnId, request.CallId), cancellationToken);
            if (previous is { MemoryInvalidated: true })
            {
                return previous.MemoryOwnerId == request.OwnerId
                    ? Recorded(previous)
                    : Rejected("authenticated_owner_required");
            }
            if (request.ToolName == "memory.propose_change"
                && previous is { ActionType: "memory.propose_change", MemoryOwnerId: not null }
                && previous.MemoryOwnerId == request.OwnerId
                && (IsTerminal(previous.Status) || previous.Status == "Executing"))
            {
                using var stored = JsonDocument.Parse(previous.CanonicalArguments);
                var command = JsonSerializer.Deserialize<ConfirmedMemoryCommand>(
                    stored.RootElement.GetProperty("CanonicalArguments").GetString()!)
                    ?? throw new InvalidDataException("Missing recorded memory command.");
                request = request with { ReplayMemorySourceId = command.SourceMessageId };
            }
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
            request.ReplayCurrentRequestHash ?? HashText(request.CurrentUserRequest),
            canonicalArguments,
            draft.ObservationScope,
            draft.MemoryReferences);
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
            var sameProposal = SameProposalIgnoringPolicy(action.CanonicalArguments, envelope);
            if (action.Status is "Prepared" or "AwaitingApproval")
            {
                action = await TransitionAsync(action, "Rejected", cancellationToken, "action_identity_reused");
            }

            if (sameProposal)
            {
                if (IsTerminal(action.Status))
                {
                    return Recorded(await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken));
                }

                if (action.Status == "Executing")
                {
                    return await ReconcileInterruptedExecutionAsync(
                        action, preparation.Arguments!, registration, draft, request,
                        cancellationToken);
                }
            }

            return new("Rejected", null, "action_identity_reused", action.Id);
        }

        if (IsTerminal(action.Status))
        {
            return Recorded(await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken));
        }

        if (action.Status == "Executing")
        {
            return await ReconcileInterruptedExecutionAsync(
                action,
                preparation.Arguments!,
                registration,
                draft,
                request,
                cancellationToken);
        }

        if (!authorization.Allowed)
        {
            if (action.Status == "Prepared")
            {
                action = await TransitionAsync(action, "Rejected", cancellationToken, authorization.ReasonCode);
            }

            return action.Outcome ?? new(action.Status, null, authorization.ReasonCode, action.Id);
        }

        await SeedPersistedWriteUncertaintyAsync(actionId, request, draft, cancellationToken);

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
                authorization.PolicyVersion,
                cancellationToken);
        }

        if (effectiveRisk == ToolRiskClass.Unsupported)
        {
            action = await TransitionAsync(action, "Rejected", cancellationToken, "unsupported_action");
            return Recorded(action);
        }

        return await ExecuteOnceAsync(
            action,
            preparation.Arguments!,
            registration,
            draft,
            request,
            authorization.PolicyVersion,
            effectiveRisk != ToolRiskClass.ReadOnly,
            null,
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
        string policyVersion,
        CancellationToken cancellationToken)
    {
        if (action.Status == "Prepared")
        {
            action = await TransitionAsync(action, "AwaitingApproval", cancellationToken);
        }

        if (action.Status != "AwaitingApproval")
        {
            var terminal = await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken);
            ReleaseApproval(action.Id);
            return Recorded(terminal);
        }

        var approvalId = new ApprovalId(actionId.Value);
        var approval = await approvals.GetAsync(approvalId, cancellationToken)
            ?? await approvals.CreateAsync(actionId, request.OwnerId!, cancellationToken);
        if (approval.Status == "Pending")
        {
            approval = await approvals.GetAsync(approvalId, cancellationToken)
                ?? throw new InvalidOperationException("The approval disappeared while refreshing its pending decision.");
            action = await journal.GetAsync(actionId, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared while refreshing its pending approval.");
        }

        if (!string.Equals(approval.OwnerId, request.OwnerId, StringComparison.Ordinal))
        {
            return new("Rejected", null, "approval_owner_mismatch", actionId, approvalId);
        }

        if (action.Status != "AwaitingApproval")
        {
            var terminal = await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken);
            ReleaseApproval(action.Id);
            return Recorded(terminal);
        }

        if (approval.Status == "Pending" && clock.UtcNow.ToUniversalTime() >= approval.ExpiresAtUtc)
        {
            try
            {
                approval = await approvals.ResolveAsync(
                    approval.Id,
                    request.OwnerId!,
                    approval.ExpectedVersion,
                    approved: false,
                    cancellationToken);
            }
            catch (PersistenceConcurrencyException)
            {
                var winner = await approvals.GetAsync(approvalId, cancellationToken);
                if (winner is null || winner.Status == "Pending")
                {
                    throw;
                }

                approval = winner;
                action = await journal.GetAsync(actionId, cancellationToken)
                    ?? throw new InvalidOperationException("The action disappeared while rereading its approval decision.");
                if (action.Status != "AwaitingApproval")
                {
                    return Recorded(await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken));
                }
            }
        }

        if (approval.Status == "Rejected" || approval.Status == "Expired")
        {
            action = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared while resolving its approval.");
            if (action.Status == "AwaitingApproval")
            {
                action = await TransitionAsync(action, approval.Status, cancellationToken,
                    $"approval_{approval.Status.ToLowerInvariant()}", approvalId);
            }

            action = await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken);
            ReleaseApproval(action.Id);
            return Recorded(action);
        }

        if (approval.Status == "Pending")
        {
            HoldApproval(actionId, request, draft);
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
            ReleaseApproval(action.Id);
            return new("Rejected", null, "invalid_approval_state", action.Id, approvalId);
        }

        if (clock.UtcNow.ToUniversalTime() >= approval.ExpiresAtUtc)
        {
            action = await TransitionAsync(action, "Expired", cancellationToken,
                "approval_expired_before_execution", approvalId);
            action = await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken);
            ReleaseApproval(action.Id);
            return Recorded(action);
        }

        var result = await ExecuteOnceAsync(
            action, arguments, registration, draft, request, policyVersion, true, approval.ExpiresAtUtc, cancellationToken);
        if (result.Status != "AwaitingApproval")
        {
            ReleaseApproval(action.Id);
        }
        return result;
    }

    private async ValueTask<ToolDispatchResult> ExecuteOnceAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        ToolDispatchRequest request,
        string policyVersion,
        bool mayWrite,
        DateTimeOffset? approvalExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (action.Status is not ("Prepared" or "AwaitingApproval"))
        {
            return Recorded(await ReconcileTerminalReplayAsync(action, request, draft, cancellationToken));
        }

        if (!activeExecutions.TryAdd(action.Id, 0))
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during execution arbitration.");
            return Recorded(await ReconcileTerminalReplayAsync(current, request, draft, cancellationToken));
        }

        try
        {
            var budget = GetFailureBudget(request, draft);
            await budget.Gate.WaitAsync(cancellationToken);
            try
            {
                if (budget.HasUncertainWrites)
                {
                    action = await TransitionAsync(action, "Rejected", cancellationToken, "equivalent_write_outcome_unknown");
                    return Recorded(action);
                }

                if (budget.FailedActions.Count >= 2)
                {
                    action = await TransitionAsync(action, "Rejected", cancellationToken, "equivalent_failure_limit");
                    return Recorded(action);
                }

                ToolDispatchResult result;
                try
                {
                    result = await ExecuteClaimedOnceAsync(
                        action, arguments, registration, draft, request, policyVersion, mayWrite,
                        approvalExpiresAtUtc, budget, cancellationToken);
                }
                catch
                {
                    budget.FailedActions.Add(action.Id);
                    throw;
                }

                if (mayWrite && result.Status == "Unknown")
                {
                    budget.RegisterUncertainWrite(action.Id);
                }

                RecordBudgetOutcome(budget, action.Id, result.Status);
                return result;
            }
            finally
            {
                budget.Gate.Release();
            }
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
        ToolDispatchRequest request,
        string policyVersion,
        bool mayWrite,
        DateTimeOffset? approvalExpiresAtUtc,
        FailureBudget budget,
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
            return await PersistOutcomeAsync(action, new("Failed", null, "cancelled_before_send", action.Id));
        }

        if (approvalExpiresAtUtc is { } expiry && clock.UtcNow.ToUniversalTime() >= expiry)
        {
            var expired = await TransitionAsync(action, "Expired", CancellationToken.None, "approval_expired_before_execution");
            return Recorded(expired);
        }

        var authorization = policy.Evaluate(request.ToolName, draft, registration.RiskClass);
        if (!authorization.Allowed
            || authorization.EffectiveRisk == ToolRiskClass.Unsupported
            || authorization.PolicyVersion != policyVersion)
        {
            var rejected = await TransitionAsync(action, "Rejected", CancellationToken.None, "policy_changed_before_execution");
            return Recorded(rejected);
        }

        if (!budget.TryAdmitExecution(action.Id, mayWrite))
        {
            var rejected = await TransitionAsync(action, "Rejected", CancellationToken.None, "equivalent_write_outcome_unknown");
            return Recorded(rejected);
        }
        ToolExecutionOutcome execution;
        try
        {
            execution = await registration.ExecuteAsync(arguments, draft, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CompleteUnknownAsync(action, null, "execution_cancelled_after_dispatch");
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
                    CancellationToken.None,
                    new("Unknown", null, "tool_execution_exception", action.Id));
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
            return await PersistOutcomeAsync(action,
                new("Failed", execution.ResultJson, execution.ReasonCode ?? "tool_execution_failed", action.Id));
        }

        var observationAuthorization = policy.Evaluate(request.ToolName, draft, registration.RiskClass);
        if (!observationAuthorization.Allowed || observationAuthorization.EffectiveRisk == ToolRiskClass.Unsupported)
        {
            return await CompleteUnknownAsync(action, execution.ResultJson, "execution_observation_not_permitted");
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
                        CancellationToken.None,
                        new("Unknown", execution.ResultJson, "tool_verification_exception", action.Id));
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
            ToolVerificationStatus.NotApplied => "Failed",
            _ => "Unknown"
        };
        var reasonCode = finalStatus switch
        {
            "Succeeded" => null,
            "Failed" => execution.ReasonCode ?? "action_not_applied",
            _ => verification.ReasonCode ?? execution.ReasonCode ?? "physical_outcome_uncertain"
        };
        return await PersistOutcomeAsync(action, new(finalStatus, execution.ResultJson, reasonCode, action.Id));
    }

    private async ValueTask<ToolDispatchResult> ReconcileInterruptedExecutionAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        ToolDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!activeExecutions.TryAdd(action.Id, 0))
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during interrupted-execution recovery.");
            return Recorded(await ReconcileTerminalReplayAsync(current, request, draft, cancellationToken));
        }

        try
        {
            var budget = GetFailureBudget(request, draft);
            RegisterPersistedWriteUncertainty(budget, action);
            await budget.Gate.WaitAsync(cancellationToken);
            try
            {
                action = await journal.GetAsync(action.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The action disappeared during interrupted-execution recovery.");
                if (action.Status != "Executing")
                {
                    RecordBudgetOutcome(budget, action.Id, action.Status);
                    return Recorded(action);
                }

                var authorization = policy.Evaluate(request.ToolName, draft, registration.RiskClass);
                var result = await ObserveInterruptedExecutionAsync(
                    action, arguments, registration, draft,
                    authorization.Allowed && authorization.EffectiveRisk != ToolRiskClass.Unsupported
                        && ObservationScopeMatches(action.CanonicalArguments, draft.ObservationScope),
                    cancellationToken);
                RecordBudgetOutcome(budget, action.Id, result.Status);
                return result;
            }
            catch
            {
                budget.FailedActions.Add(action.Id);
                throw;
            }
            finally
            {
                budget.Gate.Release();
            }
        }
        finally
        {
            activeExecutions.TryRemove(action.Id, out _);
        }
    }

    private async ValueTask<ToolDispatchResult> ObserveInterruptedExecutionAsync(
        ActionJournalEntry action,
        object arguments,
        IRuntimeToolRegistration registration,
        ToolActionDraft draft,
        bool observationAllowed,
        CancellationToken cancellationToken)
    {
        if (!observationAllowed)
        {
            return await CompleteUnknownAsync(action, null, "interrupted_execution_observation_not_permitted");
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
                        CancellationToken.None,
                        new("Unknown", null, "interrupted_execution_verification_failed", action.Id));
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
                CancellationToken.None,
                new(finalStatus, null, reasonCode, action.Id));
        }
        catch (PersistenceConcurrencyException)
        {
            completed = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during interrupted-execution recovery.");
            return Recorded(completed);
        }

        return Recorded(completed);
    }

    private async ValueTask<ActionJournalEntry> ReconcileTerminalReplayAsync(
        ActionJournalEntry action,
        ToolDispatchRequest request,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        if (action.MemoryInvalidated || !IsTerminal(action.Status))
        {
            return action;
        }

        var budget = GetFailureBudget(request, draft);
        if (action.Status == "Unknown")
        {
            RegisterPersistedWriteUncertainty(budget, action);
        }
        await budget.Gate.WaitAsync(cancellationToken);
        try
        {
            var current = await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during terminal replay reconciliation.");
            RecordBudgetOutcome(budget, current.Id, current.Status);
            return current;
        }
        finally
        {
            budget.Gate.Release();
        }
    }

    // Seed original risk before cancellable gate waits; changed authorization cannot downgrade an unresolved write.
    private static void RegisterPersistedWriteUncertainty(FailureBudget budget, ActionJournalEntry action)
    {
        ToolRiskClass originalRisk;
        try
        {
            originalRisk = JsonSerializer.Deserialize<CanonicalActionEnvelope>(
                action.CanonicalArguments, EnvelopeJsonOptions)?.RiskClass
                ?? throw new InvalidOperationException("The persisted action has no canonical envelope.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            budget.RegisterUncertainWrite(action.Id);
            throw;
        }

        if (originalRisk != ToolRiskClass.ReadOnly)
        {
            budget.RegisterUncertainWrite(action.Id);
        }
    }

    private async ValueTask SeedPersistedWriteUncertaintyAsync(
        ActionId currentActionId,
        ToolDispatchRequest request,
        ToolActionDraft draft,
        CancellationToken cancellationToken)
    {
        var budget = GetFailureBudget(request, draft);
        var currentProposal = GetProposalKey(request, draft);
        var turnActions = await journal.ReadForTurnAsync(request.OwnerId!, request.TurnId, cancellationToken);
        var candidates = new Dictionary<ActionId, ActionJournalEntry>();
        foreach (var previous in turnActions)
        {
            if (previous.Id == currentActionId
                || previous.MemoryInvalidated
                || previous.Status is not ("Unknown" or "Executing"))
            {
                continue;
            }

            CanonicalActionEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<CanonicalActionEnvelope>(
                    previous.CanonicalArguments, EnvelopeJsonOptions)
                    ?? throw new InvalidDataException("The persisted action has no canonical envelope.");
                if (envelope.FormatVersion != 1
                    || envelope.TurnId != request.TurnId.Value.ToString("D")
                    || !string.Equals(envelope.OwnerId, request.OwnerId, StringComparison.Ordinal)
                    || !string.Equals(envelope.ActionType, previous.ActionType, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The persisted action envelope does not match its journal identity.");
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                budget.RegisterUncertainWrite(previous.Id);
                throw new InvalidDataException("Unable to validate unresolved action history for this turn.", exception);
            }

            string previousProposal;
            try
            {
                previousProposal = GetProposalKey(envelope, previous.Id);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                budget.RegisterUncertainWrite(previous.Id);
                throw new InvalidDataException("Unable to compare unresolved action history for this turn.", exception);
            }

            if (string.Equals(currentProposal, previousProposal, StringComparison.Ordinal))
            {
                RegisterPersistedWriteUncertainty(budget, previous);
                candidates[previous.Id] = previous;
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        await budget.Gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var previous in candidates.Values)
            {
                var current = await journal.GetAsync(previous.Id, cancellationToken)
                    ?? throw new InvalidDataException("An unresolved action disappeared during recovery seeding.");
                if (current.ActionType != previous.ActionType
                    || !string.Equals(current.CanonicalArguments, previous.CanonicalArguments, StringComparison.Ordinal))
                {
                    budget.RegisterUncertainWrite(previous.Id);
                    throw new InvalidDataException("An unresolved action changed during recovery seeding.");
                }

                if (current.MemoryInvalidated || current.Status is not ("Unknown" or "Executing"))
                {
                    budget.ClearUncertainWrite(current.Id);
                }
                else
                {
                    RegisterPersistedWriteUncertainty(budget, current);
                }
            }
        }
        finally
        {
            budget.Gate.Release();
        }
    }

    private FailureBudget GetFailureBudget(ToolDispatchRequest request, ToolActionDraft draft)
    {
        var proposalKey = GetProposalKey(request, draft);
        return failureBudgets.GetOrAdd((request.TurnId, proposalKey), _ => new FailureBudget());
    }

    private void BeginDispatch(TurnId turnId)
    {
        lock (failureBudgetLifecycleGate)
        {
            activeDispatchesByTurn.TryGetValue(turnId, out var active);
            activeDispatchesByTurn[turnId] = active + 1;
        }
    }

    private void EndDispatch(TurnId turnId)
    {
        lock (failureBudgetLifecycleGate)
        {
            if (!activeDispatchesByTurn.TryGetValue(turnId, out var active) || active < 1)
            {
                throw new InvalidOperationException("A dispatcher turn lease was released without being acquired.");
            }

            if (active == 1)
            {
                activeDispatchesByTurn.Remove(turnId);
                foreach (var pair in failureBudgets.Where(pair => pair.Key.Turn == turnId).ToArray())
                {
                    RemoveReleasedBudgetIfIdle(pair.Key, pair.Value);
                }
            }
            else
            {
                activeDispatchesByTurn[turnId] = active - 1;
            }
        }
    }

    private void RemoveReleasedBudgetIfIdle(
        (TurnId Turn, string Proposal) key,
        FailureBudget budget)
    {
        if (budget.TurnReleased
            && budget.PendingApprovals.Count == 0
            && !activeDispatchesByTurn.ContainsKey(key.Turn))
        {
            failureBudgets.TryRemove(key, out _);
        }
    }

    private void HoldApproval(ActionId actionId, ToolDispatchRequest request, ToolActionDraft draft)
    {
        var key = (request.TurnId, GetProposalKey(request, draft));
        lock (failureBudgetLifecycleGate)
        {
            var budget = failureBudgets.GetOrAdd(key, _ => new FailureBudget());
            approvalBudgets[actionId] = key;
            budget.PendingApprovals.Add(actionId);
        }
    }

    private static string GetProposalKey(ToolDispatchRequest request, ToolActionDraft draft) =>
        GetProposalKey(
            request.OwnerId!,
            request.ToolName,
            draft.ActionType,
            draft.Operation,
            draft.ResolvedEntityId,
            draft.EquivalentFailureArguments ?? draft.CanonicalArguments);

    private static string GetProposalKey(CanonicalActionEnvelope envelope, ActionId actionId)
    {
        var equivalentFailureArguments = envelope.CanonicalArguments;
        if (envelope.ToolName == "memory.propose_change")
        {
            var command = JsonSerializer.Deserialize<ConfirmedMemoryCommand>(
                envelope.CanonicalArguments, EnvelopeJsonOptions)
                ?? throw new InvalidDataException("The persisted memory action has no canonical command.");
            if (command.ActionId != actionId)
            {
                throw new InvalidDataException("The persisted memory action identifier does not match its journal entry.");
            }

            equivalentFailureArguments = JsonSerializer.Serialize(new
            {
                command.OwnerId,
                command.SourceMessageId,
                Change = command.Change with
                {
                    FactId = command.Change.Operation is not ("update" or "delete")
                        && command.Change.FactId.Value == actionId.Value
                            ? default : command.Change.FactId,
                    Conflicts = command.Change.Conflicts.OrderBy(item => item.Id.Value).ToArray()
                }
            });
        }

        return GetProposalKey(
            envelope.OwnerId,
            envelope.ToolName,
            envelope.ActionType,
            envelope.Operation,
            envelope.EntityId,
            equivalentFailureArguments);
    }

    private static string GetProposalKey(
        string ownerId,
        string toolName,
        string actionType,
        string operation,
        string? entityId,
        string arguments) =>
        HashText(JsonSerializer.Serialize(new
        {
            OwnerId = ownerId,
            ToolName = toolName,
            ActionType = actionType,
            Operation = operation,
            ResolvedEntityId = entityId,
            Arguments = CanonicalizeObjectJson(arguments)
        }));

    // Called only under the proposal gate, after its outcome has been durably recorded.
    private static void RecordBudgetOutcome(FailureBudget budget, ActionId actionId, string status)
    {
        if (status == "Succeeded")
        {
            budget.FailedActions.Remove(actionId);
        }
        else if (status is "Failed" or "Unknown")
        {
            budget.FailedActions.Add(actionId);
        }

        if (status is "Succeeded" or "Failed" or "Rejected" or "Expired")
        {
            budget.ClearUncertainWrite(actionId);
        }
    }

    private ValueTask<ToolDispatchResult> CompleteUnknownAsync(
        ActionJournalEntry action,
        string? resultJson,
        string reasonCode) =>
        PersistOutcomeAsync(action, new("Unknown", resultJson, reasonCode, action.Id));

    private async ValueTask<ToolDispatchResult> PersistOutcomeAsync(
        ActionJournalEntry action,
        ToolDispatchResult outcome)
    {
        try
        {
            var completed = await journal.UpdateStatusAsync(
                action.Id,
                outcome.Status,
                action.Version,
                clock.UtcNow.ToUniversalTime(),
                CancellationToken.None,
                outcome);
            return Recorded(completed);
        }
        catch (PersistenceConcurrencyException) when (action.ActionType == "memory.propose_change")
        {
            var current = await journal.GetAsync(action.Id, CancellationToken.None);
            if (current is not null && (current.MemoryInvalidated || IsTerminal(current.Status)))
            {
                return Recorded(current);
            }
            throw;
        }
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
                CancellationToken.None,
                new("Unknown", null, "invalid_tool_outcome", actionId));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(message, exception);
        }
    }

    private async ValueTask<ActionJournalEntry> TransitionAsync(
        ActionJournalEntry action,
        string status,
        CancellationToken cancellationToken,
        string? reasonCode = null,
        ApprovalId? approvalId = null)
    {
        try
        {
            return await journal.UpdateStatusAsync(
                action.Id,
                status,
                action.Version,
                clock.UtcNow.ToUniversalTime(),
                cancellationToken,
                IsTerminal(status) ? new(status, null, reasonCode, action.Id, approvalId) : null);
        }
        catch (PersistenceConcurrencyException)
        {
            return await journal.GetAsync(action.Id, cancellationToken)
                ?? throw new InvalidOperationException("The action disappeared during a concurrent journal transition.");
        }
    }

    /// <summary>Derives the stable journal identity shared by a turn/tool-call proposal and its mutation receipt.</summary>
    public static ActionId CreateActionId(TurnId turnId, string callId)
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
        && action.ResolvedEntityId is not { Length: 0 or > 256 }
        && (action.ObservationScope is null || !string.IsNullOrWhiteSpace(action.ObservationScope)
            && action.ObservationScope.Length <= 256 && !action.ObservationScope.Any(char.IsControl));

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

    private static bool ObservationScopeMatches(string storedEnvelope, string? currentScope)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<CanonicalActionEnvelope>(storedEnvelope);
            return stored is not null && stored.ObservationScope == currentScope;
        }
        catch (JsonException)
        {
            return false;
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
        action.Outcome ?? new(action.Status, null, "recorded_action_outcome", action.Id);

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
        string CanonicalArguments,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObservationScope = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PersonalAgent.Application.Memory.MemoryReference>? MemoryReferences = null);

    private sealed class FailureBudget
    {
        private readonly object sendAdmission = new();
        private readonly ConcurrentDictionary<ActionId, byte> uncertainWrites = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HashSet<ActionId> FailedActions { get; } = [];
        public HashSet<ActionId> PendingApprovals { get; } = [];
        public bool TurnReleased { get; set; }
        public bool HasUncertainWrites => !uncertainWrites.IsEmpty;

        public void RegisterUncertainWrite(ActionId actionId)
        {
            lock (sendAdmission)
            {
                uncertainWrites.TryAdd(actionId, 0);
            }
        }

        public bool TryAdmitExecution(ActionId actionId, bool mayWrite)
        {
            // Admission linearizes with orphan registration; no external callback runs under this lock.
            lock (sendAdmission)
            {
                if (HasUncertainWrites)
                {
                    return false;
                }
                if (mayWrite)
                {
                    uncertainWrites.TryAdd(actionId, 0);
                }
                return true;
            }
        }

        public void ClearUncertainWrite(ActionId actionId) => uncertainWrites.TryRemove(actionId, out _);
    }
}
