using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Tools;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.IntegrationTests;

public sealed class ToolDispatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly ToolDispatchRequest PorchRequest =
        new(new TurnId(Guid.Parse("35D3D0CC-3B47-45C6-84A3-3DCE5E03C34F")), "call-1", "home.set_light",
            """{"Entity":"light.porch","On":true}""", "owner", "Turn on the porch light");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownMalformedAmbiguousAndDisallowedCallsNeverExecute()
    {
        using var fixture = await Fixture.CreateAsync();

        var unknown = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ToolName = "home.run_service" },
            CancellationToken.None);
        var malformed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "malformed", ArgumentsJson = """{"Entity":1,"On":true}""" },
            CancellationToken.None);
        var duplicate = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "duplicate", ArgumentsJson = """{"Entity":"light.porch","Entity":"light.denied","On":true}""" },
            CancellationToken.None);
        var unknownProperty = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "extra", ArgumentsJson = """{"Entity":"light.porch","On":true,"Service":"dangerous"}""" },
            CancellationToken.None);
        var ambiguous = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "ambiguous", ArgumentsJson = """{"Entity":"porch","On":true}""" },
            CancellationToken.None);
        var denied = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "denied", ArgumentsJson = """{"Entity":"light.denied","On":true}""" },
            CancellationToken.None);

        Assert.Equal("unregistered_tool", unknown.ReasonCode);
        Assert.Equal("invalid_tool_arguments", malformed.ReasonCode);
        Assert.Equal("invalid_tool_arguments", duplicate.ReasonCode);
        Assert.Equal("invalid_tool_arguments", unknownProperty.ReasonCode);
        Assert.Equal("ClarificationRequired", ambiguous.Status);
        Assert.Equal("Rejected", denied.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(denied.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SameCallIdentityReturnsRecordedOutcomeButSimilarLaterCallExecutes()
    {
        using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var later = await fixture.Dispatcher.DispatchAsync(PorchRequest with { CallId = "call-2" }, CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("Succeeded", duplicate.Status);
        Assert.Equal("recorded_action_outcome", duplicate.ReasonCode);
        Assert.NotEqual(first.ActionId, later.ActionId);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        var persisted = await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None);
        Assert.Contains("CurrentRequestHash", persisted!.CanonicalArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn on the porch light", persisted.CanonicalArguments, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangedArgumentsCannotReuseCompletedActionIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        var changed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.LowRiskWrite)]
    [InlineData(ToolRiskClass.ReadOnly)]
    [Trait("Category", "Integration")]
    public async Task OrphanRegisteredDuringSqliteClaimPreventsFinalSendAdmission(ToolRiskClass risk)
    {
        using var fixture = await Fixture.CreateAsync(risk);
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var orphan = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            orphan.Id, "Executing", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var journal = new PausedJournal(fixture.Journal, (entry, _) =>
        {
            if (entry?.Id == orphan.Id && entry.Status == "Executing")
            {
                reading.TrySetResult();
            }
            return Task.CompletedTask;
        }, async token =>
        {
            if (Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                claiming.SetResult();
                await releaseClaim.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var restarted = fixture.CreateRestartedDispatcher(journal);
        var request = PorchRequest with { CallId = "claiming-during-orphan-discovery" };
        var fresh = restarted.DispatchAsync(request, CancellationToken.None).AsTask();
        await claiming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var recovery = restarted.DispatchAsync(PorchRequest, cancellation.Token).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        }
        finally
        {
            releaseClaim.SetResult();
        }
        var result = await fresh;
        var read = risk == ToolRiskClass.ReadOnly;
        Assert.Equal(read ? "Succeeded" : "Rejected", result.Status);
        Assert.Equal(result.Status, (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal("Executing", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(read ? 2 : 1, fixture.ExecutionCount);
        Assert.Equal(read ? 2 : 1, fixture.VerificationCount);
        if (!read)
        {
            Assert.Equal("equivalent_write_outcome_unknown", result.ReasonCode);
            var stopped = await restarted.DispatchAsync(
                request with { CallId = "after-unsent-sqlite-claim" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
            Assert.Equal(1, fixture.ExecutionCount);
        }
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(PorchRequest, CancellationToken.None)).Status);
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(
            request with { CallId = "after-known-sqlite-orphan" }, CancellationToken.None)).Status);
        Assert.Equal(read ? 3 : 2, fixture.ExecutionCount);
        Assert.Equal(read ? 4 : 3, fixture.VerificationCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.LowRiskWrite)]
    [InlineData(ToolRiskClass.ReadOnly)]
    [Trait("Category", "Integration")]
    public async Task CancelledQueuedRecoveryPreservesSqliteOrphanRiskAfterTheGateHolderSucceeds(ToolRiskClass risk)
    {
        using var fixture = await Fixture.CreateAsync(risk);
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var orphan = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            orphan.Id, "Executing", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        });
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new PausedJournal(fixture.Journal, (entry, _) =>
        {
            if (entry?.Id == orphan.Id && entry.Status == "Executing")
            {
                reading.TrySetResult();
            }
            return Task.CompletedTask;
        });
        var restarted = fixture.CreateRestartedDispatcher(journal);
        var first = restarted.DispatchAsync(
            PorchRequest with { CallId = "holding-cancelled-orphan-gate" }, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelled = restarted.DispatchAsync(PorchRequest, cancellation.Token).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            releaseExecution.SetResult();
            Assert.Equal("Succeeded", (await first).Status);
        }
        Assert.Equal("Executing", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure")));
        var fresh = await restarted.DispatchAsync(PorchRequest with
        {
            CallId = "after-cancelled-sqlite-recovery",
            ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}"""
        }, CancellationToken.None);
        Assert.Equal(risk == ToolRiskClass.ReadOnly ? "Failed" : "Rejected", fresh.Status);
        if (risk != ToolRiskClass.ReadOnly)
        {
            Assert.Equal("equivalent_write_outcome_unknown", fresh.ReasonCode);
        }
        Assert.Equal(risk == ToolRiskClass.ReadOnly ? 3 : 2, fixture.ExecutionCount);
        Assert.Equal(fresh.Status, (await fixture.Journal.GetAsync(fresh.ActionId!.Value, CancellationToken.None))!.Status);

        Assert.Equal("Succeeded", (await restarted.DispatchAsync(PorchRequest, CancellationToken.None)).Status);
        Assert.Equal(3, fixture.VerificationCount);
        Assert.Equal("Succeeded", (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            PorchRequest with { CallId = "after-confirmed-sqlite-recovery" }, CancellationToken.None)).Status);
        Assert.Equal(risk == ToolRiskClass.ReadOnly ? 4 : 3, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed, false)]
    [InlineData(ToolExecutionStatus.Unknown, false)]
    [InlineData(ToolExecutionStatus.Completed, true)]
    [InlineData(ToolExecutionStatus.Unknown, true)]
    [Trait("Category", "Integration")]
    public async Task RevocationDuringExecutionPersistsUnknownWithoutObservation(
        ToolExecutionStatus delivery, bool unsupported)
    {
        using var fixture = await Fixture.CreateAsync();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, null);
        });
        var dispatch = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Policy.Replace("revoked", fixture.AllowedPermission with
        {
            EntityId = unsupported ? "light.porch" : "light.other",
            MinimumRisk = unsupported ? ToolRiskClass.Unsupported : ToolRiskClass.ReadOnly
        });
        release.SetResult();
        var result = await dispatch;
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("execution_observation_not_permitted", result.ReasonCode);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.VerificationCount);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed)]
    [InlineData(ToolExecutionStatus.Failed)]
    [InlineData(ToolExecutionStatus.Unknown)]
    [Trait("Category", "Integration")]
    public async Task StaleExecutingSnapshotReturnsSqliteWinnerWithoutObserving(ToolExecutionStatus delivery)
    {
        using var fixture = await Fixture.CreateAsync();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetExecution(async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, null);
        });
        if (delivery == ToolExecutionStatus.Unknown)
        {
            fixture.SetVerification((_, _, _, _) =>
                ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        }
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var journal = new PausedJournal(fixture.Journal, async (entry, token) =>
        {
            if (entry?.Status == "Executing" && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        });
        var dispatcher = fixture.CreateRestartedDispatcher(journal);
        var first = dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stale = dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseExecution.SetResult();
        var winner = await first;
        var observations = fixture.VerificationCount;
        fixture.Policy.Replace("revoked", fixture.AllowedPermission with { EntityId = "light.other" });
        releaseRead.SetResult();
        var result = await stale;
        Assert.Equal(winner.Status, result.Status);
        Assert.Equal("recorded_action_outcome", result.ReasonCode);
        Assert.Equal(winner.Status, (await fixture.Journal.GetAsync(winner.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(observations, fixture.VerificationCount);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task EquivalentUncertainWritesCannotResendWithFreshIds(bool concurrent, bool throws)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(execution: async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return throws
                ? throw new InvalidOperationException("controlled uncertain send")
                : new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "delivery_uncertain");
        }, verification: (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")));
        var first = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextRequest = PorchRequest with { CallId = "fresh-id" };
        var queued = concurrent
            ? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask()
            : null;
        release.SetResult();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        }
        else
        {
            Assert.Equal("Unknown", (await first).Status);
        }

        var rejected = await (queued ?? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("equivalent_write_outcome_unknown", rejected.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None))!.Status);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(rejected.ActionId!.Value, CancellationToken.None))!.Status);

        fixture.SetExecution((_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null)));
        fixture.SetVerification((_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)));
        var later = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { TurnId = TurnId.New() }, CancellationToken.None);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExactApprovalMustBeResolvedBeforeActionExecutesAndIsSingleUse()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);

        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal(Start.AddMinutes(5), pending.ApprovalExpiresAtUtc);
        Assert.Equal(0, fixture.ExecutionCount);

        var resolved = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value,
            "owner",
            1,
            approved: true,
            CancellationToken.None);
        Assert.Equal("Approved", resolved.Status);

        var executed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Succeeded", executed.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId.Value,
                "owner",
                1,
                approved: true,
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LowRiskWriteWithoutVerifiedExplicitOwnerIntentRequiresApproval()
    {
        using var fixture = await Fixture.CreateAsync();

        var pending = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CurrentUserRequest = "Maybe turn on the porch light" },
            CancellationToken.None);

        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal("owner_approval_required", pending.ReasonCode);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExpiredApprovalIsJournaledExpiredWithoutExecution()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persistedApproval = await fixture.Approvals.GetAsync(pending.ApprovalId!.Value, CancellationToken.None);
        var action = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", persistedApproval!.Status);
        Assert.Equal("Expired", action!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovedActionThatExpiresBeforeExecutionCannotRun()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");
        _ = await fixture.Approvals.ResolveAsync(approvalId, "owner", 1, true, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", (await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangedProposalWhileApprovalIsPendingInvalidatesTheAction()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var changed = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");

        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(approvalId, "owner", 1, true, CancellationToken.None));
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovalRejectsWrongOwnerAndDoesNotExecuteAction()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var approvalId = pending.ApprovalId ?? throw new InvalidOperationException("Expected an approval identifier.");

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(
                approvalId,
                "other-owner",
                1,
                approved: true,
                CancellationToken.None));
        var rejected = await fixture.Approvals.ResolveAsync(
            approvalId,
            "owner",
            1,
            approved: false,
            CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Rejected", replay.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PolicyVersionChangeInvalidatesPendingApproval()
    {
        using var fixture = await Fixture.CreateAsync(riskClass: ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);

        var changed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var action = await fixture.Journal.GetAsync(pending.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Rejected", changed.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.Equal("Rejected", action!.Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownExecutionIsObservedOnceAndNeverBlindlyReplayed()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "state_unconfirmed")));

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Unknown", first.Status);
        Assert.Equal("Unknown", replay.Status);
        Assert.Equal("Unknown", persisted!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ObservationCanConfirmDesiredStateAfterUnknownDelivery()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, """{"accepted":true}""", "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)));

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("""{"accepted":true}""", result.ResultJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Integration")]
    public async Task FreshDispatcherKeepsOrphanedWritesUncertainAcrossEquivalentFreshIds(bool queued, bool throws)
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var action = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            action.Id, "Executing", action.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetVerification(async (_, _, outcome, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, outcome.Status);
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (throws)
            {
                throw new InvalidOperationException("controlled orphan observation failure");
            }
            return new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed");
        });
        var restarted = fixture.CreateRestartedDispatcher();
        var recovery = restarted.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshRequest = PorchRequest with { CallId = "fresh-after-orphan" };
        var fresh = queued ? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask() : null;
        releaseObservation.SetResult();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery);
        }
        else
        {
            Assert.Equal("Unknown", (await recovery).Status);
        }

        var stopped = await (fresh ?? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(action.Id, CancellationToken.None))!.Status);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(stopped.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DefinitiveNonApplicationAfterUnknownDeliveryAllowsOnlyOneFreshRetry()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "transport_timeout")),
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "definitively_not_applied")));

        var first = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var second = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "second-definitive-failure" }, CancellationToken.None);
        var stopped = await fixture.Dispatcher.DispatchAsync(
            PorchRequest with { CallId = "third-definitive-failure" }, CancellationToken.None);

        Assert.Equal("Failed", first.Status);
        Assert.Equal("Failed", replay.Status);
        Assert.Equal("Failed", second.Status);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal("Failed", (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedExecutionAndConfirmedNotAppliedVerificationRemainFailed()
    {
        using var knownFailure = await Fixture.CreateAsync(
            execution: (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "device_rejected")));
        using var notApplied = await Fixture.CreateAsync(
            verification: (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "state_unchanged")));

        var executionFailure = await knownFailure.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var verificationFailure = await notApplied.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Failed", executionFailure.Status);
        Assert.Equal("device_rejected", executionFailure.ReasonCode);
        Assert.Equal(0, knownFailure.VerificationCount);
        Assert.Equal("Failed", verificationFailure.Status);
        Assert.Equal(1, notApplied.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerificationDeadlineRecordsUnknownInsteadOfFailed()
    {
        using var fixture = await Fixture.CreateAsync(
            verification: async (_, _, _, cancellationToken) =>
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                    .Task.WaitAsync(cancellationToken);
                return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
            });

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Unknown", result.Status);
        Assert.Equal("verification_interrupted", result.ReasonCode);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationAfterExecutionBeginsRecordsUnknown()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.SetExecution((_, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var result = await fixture.Dispatcher.DispatchAsync(PorchRequest, cancellation.Token);
        var replay = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None);

        Assert.Equal("Unknown", result.Status);
        Assert.Equal("Unknown", replay.Status);
        Assert.Equal("execution_cancelled_after_dispatch", result.ReasonCode);
        Assert.Equal("Unknown", persisted!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationBeforeDispatchCreatesNoJournalEntry()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Dispatcher.DispatchAsync(PorchRequest, cancellation.Token));

        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Null(await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentDuplicateDispatchClaimsExecutionOnlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(
            execution: async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new ToolExecutionOutcome(ToolExecutionStatus.Completed, """{"ok":true}""", null);
            });

        var firstTask = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        release.TrySetResult();
        var first = await firstTask;

        Assert.Equal("Executing", duplicate.Status);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestartedDispatcherReconcilesPersistedExecutingActionWithoutReplayingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        await fixture.Journal.UpdateStatusAsync(
            persisted!.Id,
            "Executing",
            persisted.Version,
            fixture.Clock.UtcNow,
            CancellationToken.None);
        fixture.SetVerification((_, _, execution, _) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        });

        var restartedDispatcher = fixture.CreateRestartedDispatcher();
        var recovered = await restartedDispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var replay = await restartedDispatcher.DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Succeeded", recovered.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal("recorded_action_outcome", replay.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        Assert.Equal("Succeeded", (await fixture.Journal.GetAsync(completed.ActionId.Value, CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public async Task RestartedExecutionHandlesChangedPolicyWithoutReplayingIt(bool allowed)
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        await fixture.Journal.UpdateStatusAsync(
            persisted!.Id, "Executing", persisted.Version, fixture.Clock.UtcNow, CancellationToken.None);
        fixture.Policy.Replace("policy-v2",
            allowed ? fixture.AllowedPermission : fixture.AllowedPermission with { EntityId = "light.other" });

        var recovered = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal(allowed ? "Succeeded" : "Unknown", recovered.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(allowed ? 2 : 1, fixture.VerificationCount);
        Assert.Equal(recovered.Status, (await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PolicyChangeDoesNotMistakeActiveExecutionForRestartRecovery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = await Fixture.CreateAsync(execution: async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        });
        var first = fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Policy.Replace("policy-v2", fixture.AllowedPermission);
        var duplicate = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        Assert.Equal("Executing", duplicate.Status);
        Assert.Equal(0, fixture.VerificationCount);
        release.SetResult();
        Assert.Equal("Succeeded", (await first).Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestartedDispatcherDoesNotOverwriteAConcurrentJournalOutcome()
    {
        using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var persisted = await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None);
        var interrupted = await fixture.Journal.UpdateStatusAsync(
            persisted!.Id,
            "Executing",
            persisted.Version,
            fixture.Clock.UtcNow,
            CancellationToken.None);
        fixture.SetVerification(async (_, _, execution, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            var concurrent = await fixture.Journal.GetAsync(interrupted.Id, token)
                ?? throw new InvalidOperationException("The action was not journaled.");
            await fixture.Journal.UpdateStatusAsync(
                concurrent.Id,
                "Unknown",
                concurrent.Version,
                fixture.Clock.UtcNow,
                token);
            return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
        });

        var recovered = await fixture.CreateRestartedDispatcher().DispatchAsync(PorchRequest, CancellationToken.None);

        Assert.Equal("Unknown", recovered.Status);
        Assert.Equal("recorded_action_outcome", recovered.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Unknown", (await fixture.Journal.GetAsync(interrupted.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnexpectedHandlerExceptionIsSurfacedAndJournaledUnknown()
    {
        using var fixture = await Fixture.CreateAsync(
            execution: (_, _, _) => throw new InvalidOperationException("controlled handler failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None));

        var persisted = await fixture.Journal.GetAsync(fixture.ActionId(PorchRequest), CancellationToken.None);
        Assert.Equal("Unknown", persisted!.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnsupportedRiskAndUnauthenticatedActionsAreDenied()
    {
        using var unsupported = await Fixture.CreateAsync(riskClass: ToolRiskClass.Unsupported);
        using var unauthenticated = await Fixture.CreateAsync();

        var unsupportedResult = await unsupported.Dispatcher.DispatchAsync(PorchRequest, CancellationToken.None);
        var missingOwner = await unauthenticated.Dispatcher.DispatchAsync(PorchRequest with { OwnerId = null }, CancellationToken.None);

        Assert.Equal("Rejected", unsupportedResult.Status);
        Assert.Equal("tool_action_not_allowlisted", unsupportedResult.ReasonCode);
        Assert.Equal("Rejected", missingOwner.Status);
        Assert.Equal("authenticated_owner_required", missingOwner.ReasonCode);
        Assert.Equal(0, unsupported.ExecutionCount);
        Assert.Equal(0, unauthenticated.ExecutionCount);
        Assert.Null(await unauthenticated.Journal.GetAsync(unauthenticated.ActionId(PorchRequest), CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IsolatedDatabaseFile file;
        private Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> execution;
        private Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> verification;
        private int executionCount;
        private int verificationCount;
        private ToolRegistry registry = null!;

        private Fixture(
            IsolatedDatabaseFile file,
            SqliteDatabase database,
            ToolRiskClass riskClass,
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>>? execution,
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>>? verification)
        {
            this.file = file;
            Journal = new SqliteActionJournalStore(database);
            var approvalStore = new SqliteApprovalStore(database);
            Clock = new MutableClock(Start);
            Approvals = new ApprovalService(approvalStore, Journal, Clock);
            AllowedPermission = new ToolPermission(
                "home.set_light",
                "home.set_light",
                "turn_on",
                "light.porch",
                ToolRiskClass.ReadOnly);
            Policy = new MutablePolicy("policy-v1", AllowedPermission);
            this.execution = execution ?? DefaultExecution;
            this.verification = verification ?? DefaultVerification;
            var registration = new ToolRegistration<ToolArguments>(
                "home.set_light",
                """{"type":"object","properties":{"Entity":{"type":"string"},"On":{"type":"boolean"}},"required":["Entity","On"],"additionalProperties":false}""",
                riskClass,
                Prepare,
                Execute,
                Verify);
            registry = new ToolRegistry([registration]);
            Dispatcher = new ToolDispatcher(registry, Policy, Journal, Approvals, Clock, TimeSpan.FromSeconds(1));
        }

        public SqliteActionJournalStore Journal { get; }
        public ApprovalService Approvals { get; }
        public MutableClock Clock { get; }
        public MutablePolicy Policy { get; }
        public ToolPermission AllowedPermission { get; }
        public ToolDispatcher Dispatcher { get; }
        public int ExecutionCount => Volatile.Read(ref executionCount);
        public int VerificationCount => Volatile.Read(ref verificationCount);

        public static async Task<Fixture> CreateAsync(
            ToolRiskClass riskClass = ToolRiskClass.LowRiskWrite,
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>>? execution = null,
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>>? verification = null)
        {
            var file = IsolatedDatabaseFile.Create();
            try
            {
                var database = new SqliteDatabase(file.Path);
                await database.InitializeAsync();
                return new Fixture(file, database, riskClass, execution, verification);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public ActionId ActionId(ToolDispatchRequest request) =>
            new(new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{request.TurnId.Value:D}\n{request.CallId}")).AsSpan(0, 16)));

        public void SetExecution(
            Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> handler) =>
            execution = handler;

        public void SetVerification(
            Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> handler) =>
            verification = handler;

        public ToolDispatcher CreateRestartedDispatcher(IActionJournalStore? journal = null) =>
            new(registry, Policy, journal ?? Journal, Approvals, Clock, TimeSpan.FromSeconds(1));

        public void Dispose() => file.Dispose();

        private ValueTask<ToolPreparationResult> Prepare(
            ToolArguments arguments,
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Entity == "porch")
            {
                return ValueTask.FromResult(ToolPreparationResult.Clarify("entity_alias_ambiguous"));
            }

            var entityId = arguments.Entity == "porch-lamp" ? "light.porch" : arguments.Entity;
            var operation = arguments.On ? "turn_on" : "turn_off";
            var canonicalArguments = JsonSerializer.Serialize(new { entityId, on = arguments.On });
            return ValueTask.FromResult(ToolPreparationResult.Ready(
                new ToolActionDraft(
                    "home.set_light",
                    operation,
                    entityId,
                    canonicalArguments,
                    request.CurrentUserRequest == "Turn on the porch light")));
        }

        private async ValueTask<ToolExecutionOutcome> Execute(
            ToolArguments arguments,
            ToolActionDraft action,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            return await execution(arguments, action, cancellationToken);
        }

        private async ValueTask<ToolVerificationOutcome> Verify(
            ToolArguments arguments,
            ToolActionDraft action,
            ToolExecutionOutcome outcome,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref verificationCount);
            return await verification(arguments, action, outcome, cancellationToken);
        }

        private static ValueTask<ToolExecutionOutcome> DefaultExecution(
            ToolArguments _,
            ToolActionDraft __,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolExecutionOutcome(
                ToolExecutionStatus.Completed,
                """{"ok":true}""",
                null));
        }

        private static ValueTask<ToolVerificationOutcome> DefaultVerification(
            ToolArguments _,
            ToolActionDraft __,
            ToolExecutionOutcome ___,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        }
    }

    private sealed class PausedJournal(
        IActionJournalStore inner,
        Func<ActionJournalEntry?, CancellationToken, Task> afterRead,
        Func<CancellationToken, Task>? beforeExecuting = null) : IActionJournalStore
    {
        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken cancellationToken) =>
            inner.CreateAsync(entry, cancellationToken);

        public async ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken)
        {
            var entry = await inner.GetAsync(id, cancellationToken);
            await afterRead(entry, cancellationToken);
            return entry;
        }

        public async ValueTask<ActionJournalEntry> UpdateStatusAsync(
            ActionId id, string status, long expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
        {
            if (status == "Executing" && beforeExecuting is not null)
            {
                await beforeExecuting(cancellationToken);
            }
            return await inner.UpdateStatusAsync(id, status, expectedVersion, updatedAtUtc, cancellationToken);
        }
    }

    private sealed record ToolArguments(string Entity, bool On);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset now = now;

        public DateTimeOffset UtcNow => now;

        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }

    private sealed class MutablePolicy(string version, ToolPermission permission) : IToolAuthorizationPolicy
    {
        private ToolAllowlistPolicy current = new(version, [permission]);

        public void Replace(string newVersion, ToolPermission newPermission) =>
            current = new ToolAllowlistPolicy(newVersion, [newPermission]);

        public ToolAuthorizationDecision Evaluate(
            string toolName,
            ToolActionDraft action,
            ToolRiskClass declaredRisk) =>
            current.Evaluate(toolName, action, declaredRisk);
    }
}
