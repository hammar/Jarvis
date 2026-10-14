using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Tools;
using PersonalAgent.Domain;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class ToolDispatcherUnitTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly ToolDispatchRequest Request = new(
        new TurnId(Guid.Parse("35D3D0CC-3B47-45C6-84A3-3DCE5E03C34F")),
        "call-1",
        "home.set_light",
        """{"Entity":"light.porch","On":true}""",
        "owner",
        "Turn on the porch light");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherValidatesRequestsAndNeverJournalsClarificationOrMalformedArguments()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, cancelled.Token));

        var invalid = await fixture.Dispatcher.DispatchAsync(Request with { CallId = "\n" }, CancellationToken.None);
        var unregistered = await fixture.Dispatcher.DispatchAsync(Request with { ToolName = "home.unknown" }, CancellationToken.None);
        var malformed = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":1,"On":true}""" },
            CancellationToken.None);
        var duplicate = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":"light.porch","On":true,"Nested":{"x":1,"x":2}}""" },
            CancellationToken.None);
        var unknownProperty = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":"light.porch","On":true,"Service":"unsafe"}""" },
            CancellationToken.None);
        var nestedArray = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":"light.porch","On":true,"Nested":[{"Safe":true}]}""" },
            CancellationToken.None);
        var nullArguments = await fixture.Dispatcher.DispatchAsync(Request with { ArgumentsJson = "null" }, CancellationToken.None);
        var emptyArguments = await fixture.Dispatcher.DispatchAsync(Request with { ArgumentsJson = "" }, CancellationToken.None);
        var oversizedArguments = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = new string('x', 65_537) },
            CancellationToken.None);
        var alias = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "alias", ArgumentsJson = """{"Entity":"porch","On":true}""" },
            CancellationToken.None);

        Assert.Equal("invalid_tool_request", invalid.ReasonCode);
        Assert.Equal("unregistered_tool", unregistered.ReasonCode);
        Assert.Equal("invalid_tool_arguments", malformed.ReasonCode);
        Assert.Equal("invalid_tool_arguments", duplicate.ReasonCode);
        Assert.Equal("invalid_tool_arguments", unknownProperty.ReasonCode);
        Assert.Equal("invalid_tool_arguments", nestedArray.ReasonCode);
        Assert.Equal("invalid_tool_arguments", nullArguments.ReasonCode);
        Assert.Equal("invalid_tool_request", emptyArguments.ReasonCode);
        Assert.Equal("invalid_tool_arguments", oversizedArguments.ReasonCode);
        Assert.Equal("ClarificationRequired", alias.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Empty(fixture.Journal.Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DispatcherRejectsUnsupportedVerificationTimeouts()
    {
        using var fixture = new Fixture();
        var registry = new ToolRegistry([]);
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(
            null!,
            fixture.Policy,
            fixture.Journal,
            fixture.Approvals,
            fixture.Clock,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(
            registry,
            null!,
            fixture.Journal,
            fixture.Approvals,
            fixture.Clock,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(
            registry,
            fixture.Policy,
            null!,
            fixture.Approvals,
            fixture.Clock,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(
            registry,
            fixture.Policy,
            fixture.Journal,
            null!,
            fixture.Clock,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(
            registry,
            fixture.Policy,
            fixture.Journal,
            fixture.Approvals,
            null!,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolDispatcher(
            registry,
            fixture.Policy,
            fixture.Journal,
            fixture.Approvals,
            fixture.Clock,
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolDispatcher(
            new ToolRegistry([]),
            fixture.Policy,
            fixture.Journal,
            fixture.Approvals,
            fixture.Clock,
            TimeSpan.FromMinutes(3)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherRejectsInvalidActionsOwnersAndDisallowedTargets()
    {
        using var fixture = new Fixture();
        fixture.PrepareOverride = (_, _) => ToolPreparationResult.Ready(
            new ToolActionDraft(" ", "turn_on", "light.porch", """{"on":true}"""));
        var invalidAction = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        fixture.PrepareOverride = (_, _) => ToolPreparationResult.Ready(
            new ToolActionDraft("home.set_light", "turn_on", "light.porch", """[]"""));
        var invalidCanonical = await fixture.Dispatcher.DispatchAsync(Request with { CallId = "bad-json" }, CancellationToken.None);
        fixture.PrepareOverride = null;
        var noOwner = await fixture.Dispatcher.DispatchAsync(Request with { CallId = "no-owner", OwnerId = null }, CancellationToken.None);
        var wrongTarget = await fixture.Dispatcher.DispatchAsync(
            Request with
            {
                CallId = "denied",
                ArgumentsJson = """{"Entity":"light.denied","On":true}"""
            },
            CancellationToken.None);

        Assert.Equal("invalid_tool_action", invalidAction.ReasonCode);
        Assert.Equal("invalid_tool_action", invalidCanonical.ReasonCode);
        Assert.Equal("authenticated_owner_required", noOwner.ReasonCode);
        Assert.Equal("Rejected", wrongTarget.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal("Rejected", fixture.Journal.Entries.Single().Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherRejectsMalformedPreparationResultsAndUnsupportedPolicyActions()
    {
        using var fixture = new Fixture();
        fixture.PrepareOverride = (_, _) => new ToolPreparationResult(
            ToolPreparationStatus.Ready,
            null,
            null);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));

        fixture.PrepareOverride = (_, _) => new ToolPreparationResult(
            ToolPreparationStatus.Rejected,
            new ToolActionDraft("home.set_light", "turn_on", "light.porch", """{"on":true}"""),
            "invalid");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request with { CallId = "non-ready-action" }, CancellationToken.None));

        fixture.PrepareOverride = (_, _) => new ToolPreparationResult(
            (ToolPreparationStatus)99,
            null,
            "invalid");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request with { CallId = "bad-status" }, CancellationToken.None));

        fixture.PrepareOverride = null;
        using var unsupported = new Fixture(ToolRiskClass.Unsupported);
        unsupported.Policy.AllowUnsupported = true;
        var result = await unsupported.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("unsupported_action", result.ReasonCode);
        Assert.Equal(0, unsupported.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherExecutesVerifiedReadAndReturnsRecordedResultWithoutRepeatingIt()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        var request = Request with { CurrentUserRequest = null };
        var first = await fixture.Dispatcher.DispatchAsync(request, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal("recorded_action_outcome", replay.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(1, fixture.VerificationCount);
        Assert.Equal("Succeeded", fixture.Journal.Entries.Single().Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherSeparatesCallIdentityAndRejectsChangedContentUnderAnExistingIdentity()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        var first = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var changed = await fixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);
        var later = await fixture.Dispatcher.DispatchAsync(Request with { CallId = "call-2" }, CancellationToken.None);

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.NotEqual(first.ActionId, later.ActionId);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatcherRequiresExactApprovalAndResolvesItOnce()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var approval = Assert.IsType<ApprovalRequest>(await fixture.Approvals.GetAsync(pending.ApprovalId!.Value, CancellationToken.None));
        var approved = await fixture.Approvals.ResolveAsync(approval.Id, "owner", approval.ExpectedVersion, true, CancellationToken.None);
        var executed = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var replay = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal(Start.AddMinutes(5), pending.ApprovalExpiresAtUtc);
        Assert.Equal("Approved", approved.Status);
        Assert.Equal("Succeeded", executed.Status);
        Assert.Equal("Succeeded", replay.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(approval.Id, "owner", approval.ExpectedVersion, true, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LowRiskWithoutVerifiedIntentRequiresApprovalAndExactOwnerIntentExecutes()
    {
        using var fixture = new Fixture();
        var pending = await fixture.Dispatcher.DispatchAsync(
            Request with { CurrentUserRequest = "Maybe turn on the porch light" },
            CancellationToken.None);
        var explicitRequest = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "explicit", CurrentUserRequest = "Turn on the porch light" },
            CancellationToken.None);

        Assert.Equal("AwaitingApproval", pending.Status);
        Assert.Equal("owner_approval_required", pending.ReasonCode);
        Assert.Equal("Succeeded", explicitRequest.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        var persisted = await fixture.Journal.GetAsync(explicitRequest.ActionId!.Value, CancellationToken.None);
        Assert.Contains("CurrentRequestHash", persisted!.CanonicalArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn on the porch light", persisted.CanonicalArguments, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalServiceBindsOwnerActionExpiryAndSingleUse()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var id = pending.ApprovalId!.Value;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Approvals.ResolveAsync(id, " ", 1, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fixture.Approvals.ResolveAsync(id, "owner", 0, true, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(id, "other-owner", 1, true, CancellationToken.None));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(id, "owner", 2, true, CancellationToken.None));

        var rejected = await fixture.Approvals.ResolveAsync(id, "owner", 1, false, CancellationToken.None);
        var result = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Null(await fixture.Approvals.GetAsync(new ApprovalId(Guid.NewGuid()), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Approvals.CreateAsync(pending.ActionId!.Value, " ", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await fixture.Approvals.CreateAsync(new ActionId(Guid.NewGuid()), "owner", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalServiceValidatesDependenciesAndReturnsAnExistingExactBinding()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        Assert.Throws<ArgumentNullException>(() =>
            new ApprovalService(null!, fixture.Journal, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new ApprovalService(fixture.ApprovalStore, null!, fixture.Clock));
        Assert.Throws<ArgumentNullException>(() =>
            new ApprovalService(fixture.ApprovalStore, fixture.Journal, null!));

        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var original = await fixture.Approvals.CreateAsync(pending.ActionId!.Value, "owner", CancellationToken.None);
        Assert.Equal(pending.ApprovalId, original.Id);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.CreateAsync(pending.ActionId.Value, "another-owner", CancellationToken.None));

        using var completed = new Fixture(ToolRiskClass.ReadOnly);
        var action = await completed.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await completed.Approvals.CreateAsync(action.ActionId!.Value, "owner", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalExpiryAndChangedPendingProposalNeverExecute()
    {
        using var expiredFixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await expiredFixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        expiredFixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await expiredFixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", (await expiredFixture.Approvals.GetAsync(pending.ApprovalId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(0, expiredFixture.ExecutionCount);

        using var changedFixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pendingChanged = await changedFixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var changed = await changedFixture.Dispatcher.DispatchAsync(
            Request with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" },
            CancellationToken.None);
        Assert.Equal("action_identity_reused", changed.ReasonCode);
        Assert.Equal("Rejected", changed.Status);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await changedFixture.Approvals.ResolveAsync(
                pendingChanged.ApprovalId!.Value, "owner", 1, true, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalTransitionsRejectForeignOrInvalidDecisionsAndPersistExpiry()
    {
        using var wrongOwner = new Fixture(ToolRiskClass.ApprovalRequired);
        var ownerPending = await wrongOwner.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var ownerApproval = await wrongOwner.ApprovalStore.GetAsync(ownerPending.ApprovalId!.Value, CancellationToken.None);
        wrongOwner.ApprovalStore.Replace(ownerApproval! with
        {
            OwnerId = "other-owner"
        });
        var mismatched = await wrongOwner.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("approval_owner_mismatch", mismatched.ReasonCode);

        using var invalidStatus = new Fixture(ToolRiskClass.ApprovalRequired);
        var invalidPending = await invalidStatus.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var invalidApproval = await invalidStatus.ApprovalStore.GetAsync(invalidPending.ApprovalId!.Value, CancellationToken.None);
        invalidStatus.ApprovalStore.Replace(invalidApproval! with
        {
            Status = "Corrupt"
        });
        var invalid = await invalidStatus.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("invalid_approval_state", invalid.ReasonCode);

        using var expiredStatus = new Fixture(ToolRiskClass.ApprovalRequired);
        var expiredPending = await expiredStatus.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var expiredApproval = await expiredStatus.ApprovalStore.GetAsync(expiredPending.ApprovalId!.Value, CancellationToken.None);
        expiredStatus.ApprovalStore.Replace(expiredApproval! with
        {
            Status = "Expired"
        });
        var expired = await expiredStatus.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Expired", expired.Status);
        Assert.Equal("Expired", (await expiredStatus.Journal.GetAsync(expiredPending.ActionId!.Value, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalJournalTransitionReturningNonPendingStatusDoesNotCreateAnApproval()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        fixture.Journal.ReturnStatusAfterAwaitingApproval = "Rejected";
        var result = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal("Rejected", result.Status);
        Assert.Empty(fixture.ApprovalStore.Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalPolicyChangesAndWrongOwnerDoNotExecute()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "other-owner", 1, true, CancellationToken.None));

        fixture.Policy.Replace("policy-v2");
        var changedPolicy = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("action_identity_reused", changedPolicy.ReasonCode);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovedActionCannotExecuteAfterExpiryAndPolicyChangeCannotReplayCompletedAction()
    {
        using var expiring = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await expiring.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await expiring.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        expiring.Clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await expiring.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Expired", expired.Status);
        Assert.Equal(0, expiring.ExecutionCount);

        using var completed = new Fixture(ToolRiskClass.ReadOnly);
        var first = await completed.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        completed.Policy.Replace("policy-v2");
        var replay = await completed.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("recorded_action_outcome", replay.ReasonCode);
        Assert.Equal(1, completed.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CanonicalEnvelopeNormalizesNestedValuesAndRejectsInvalidCanonicalJson()
    {
        using var valid = new Fixture(ToolRiskClass.ReadOnly);
        valid.PrepareOverride = (_, _) => ToolPreparationResult.Ready(new ToolActionDraft(
            "home.set_light",
            "turn_on",
            "light.porch",
            """{"items":[false,null,2,{"z":"value"}]}"""));
        var result = await valid.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var persisted = await valid.Journal.GetAsync(result.ActionId!.Value, CancellationToken.None);
        Assert.Equal("Succeeded", result.Status);
        Assert.Contains("items", persisted!.CanonicalArguments, StringComparison.Ordinal);

        using var malformed = new Fixture(ToolRiskClass.ReadOnly);
        malformed.PrepareOverride = (_, _) => ToolPreparationResult.Ready(new ToolActionDraft(
            "home.set_light", "turn_on", "light.porch", """{"broken":"""));
        var malformedResult = await malformed.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("invalid_tool_action", malformedResult.ReasonCode);

        using var duplicate = new Fixture(ToolRiskClass.ReadOnly);
        duplicate.PrepareOverride = (_, _) => ToolPreparationResult.Ready(new ToolActionDraft(
            "home.set_light", "turn_on", "light.porch", """{"nested":{"x":1,"x":2}}"""));
        var duplicateResult = await duplicate.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("invalid_tool_action", duplicateResult.ReasonCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatchRacesAndCorruptJournalEntriesCannotCauseExecution()
    {
        using var arbitration = new Fixture(ToolRiskClass.ReadOnly);
        arbitration.Journal.FailNextExecutingUpdate = true;
        var concurrent = await arbitration.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Prepared", concurrent.Status);
        Assert.Equal(0, arbitration.ExecutionCount);

        using var nonExecuting = new Fixture(ToolRiskClass.ReadOnly);
        nonExecuting.Journal.ReturnStatusAfterExecuting = "Rejected";
        var rejected = await nonExecuting.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(0, nonExecuting.ExecutionCount);

        using var corrupted = new Fixture(ToolRiskClass.ReadOnly);
        var completed = await corrupted.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        corrupted.Journal.Corrupt(completed.ActionId!.Value);
        var replay = await corrupted.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("action_identity_reused", replay.ReasonCode);
        Assert.Equal(1, corrupted.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecutionAndVerificationOutcomesRemainTruthful()
    {
        using var failed = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"))
        };
        using var notApplied = new Fixture(ToolRiskClass.ReadOnly)
        {
            Verification = (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "not_applied"))
        };
        using var uncertain = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "delivery_unknown")),
            Verification = (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "not_observed"))
        };

        var failedResult = await failed.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var notAppliedResult = await notApplied.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var unknownResult = await uncertain.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal("Failed", failedResult.Status);
        Assert.Equal(0, failed.VerificationCount);
        Assert.Equal("Failed", notAppliedResult.Status);
        Assert.Equal("Unknown", unknownResult.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InvalidAdapterStatusesAreJournaledUnknownBeforeSurfacingTheContractError()
    {
        using var invalidExecution = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome((ToolExecutionStatus)99, null, null))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await invalidExecution.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", invalidExecution.Journal.Entries.Single().Status);

        using var invalidVerification = new Fixture(ToolRiskClass.ReadOnly)
        {
            Verification = (_, _, _, _) => ValueTask.FromResult(
                new ToolVerificationOutcome((ToolVerificationStatus)99, null))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await invalidVerification.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", invalidVerification.Journal.Entries.Single().Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationAndExceptionsAfterDispatchAreJournaledUnknown()
    {
        using var cancelled = new Fixture(ToolRiskClass.ReadOnly);
        using var source = new CancellationTokenSource();
        cancelled.Execution = (_, _, _) =>
        {
            source.Cancel();
            throw new OperationCanceledException(source.Token);
        };
        var cancellationResult = await cancelled.Dispatcher.DispatchAsync(Request, source.Token);
        Assert.Equal("Unknown", cancellationResult.Status);

        using var exceptionFixture = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => throw new InvalidOperationException("controlled failure")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await exceptionFixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", exceptionFixture.Journal.Entries.Single().Status);

        using var verifyException = new Fixture(ToolRiskClass.ReadOnly)
        {
            Verification = (_, _, _, _) => throw new InvalidOperationException("controlled verification failure")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await verifyException.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", verifyException.Journal.Entries.Single().Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerificationTimeoutAndCancellationBeforeDispatchDoNotClaimSuccess()
    {
        using var timeout = new Fixture(ToolRiskClass.ReadOnly)
        {
            Verification = async (_, _, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
            }
        };
        var unknown = await timeout.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Unknown", unknown.Status);

        using var beforeSend = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await beforeSend.Dispatcher.DispatchAsync(Request, cancelled.Token));
        Assert.Empty(beforeSend.Journal.Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationAfterExecutionClaimButBeforeHandlerSendRecordsFailure()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        using var cancellation = new CancellationTokenSource();
        fixture.Journal.StatusUpdated = status =>
        {
            if (status == "Executing")
            {
                cancellation.Cancel();
            }
        };

        var result = await fixture.Dispatcher.DispatchAsync(Request, cancellation.Token);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("cancelled_before_send", result.ReasonCode);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailureToJournalUncertainExecutionOrVerificationSurfacesBothFailures()
    {
        using var execution = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => throw new InvalidOperationException("execution failure")
        };
        execution.Journal.FailUnknownUpdate = true;
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await execution.Dispatcher.DispatchAsync(Request, CancellationToken.None));

        using var verification = new Fixture(ToolRiskClass.ReadOnly)
        {
            Verification = (_, _, _, _) => throw new InvalidOperationException("verification failure")
        };
        verification.Journal.FailUnknownUpdate = true;
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await verification.Dispatcher.DispatchAsync(Request, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InvalidAdapterStatusAndJournalFailureAreBothSurfaced()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly)
        {
            Execution = (_, _, _) => ValueTask.FromResult(
                new ToolExecutionOutcome((ToolExecutionStatus)99, null, null))
        };
        fixture.Journal.FailUnknownUpdate = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Executing", fixture.Journal.Entries.Single().Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentDuplicateCallsClaimExecutionOnlyOnce()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        };

        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        release.SetResult();
        var result = await first;

        Assert.Equal("Executing", duplicate.Status);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestartedDispatcherObservesOrphanedExecutionWithoutReplayingIt()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        var persisted = await fixture.MarkExecutingAsync(Request);
        fixture.Verification = (_, _, execution, _) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, null));
        };

        var restartedDispatcher = fixture.CreateRestartedDispatcher();
        var recovered = await restartedDispatcher.DispatchAsync(Request, CancellationToken.None);
        var replay = await restartedDispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal("Failed", recovered.Status);
        Assert.Equal("interrupted_execution_not_applied", recovered.ReasonCode);
        Assert.Equal("Failed", replay.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        Assert.Equal("Failed", (await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestartedDispatcherJournalsUnknownForInconclusiveOrInterruptedObservation()
    {
        using var inconclusive = new Fixture(ToolRiskClass.ReadOnly);
        var inconclusiveAction = await inconclusive.MarkExecutingAsync(Request);
        inconclusive.Verification = (_, _, execution, _) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, null));
        };
        var unknown = await inconclusive.CreateRestartedDispatcher().DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Unknown", unknown.Status);
        Assert.Equal("interrupted_execution_outcome_unknown", unknown.ReasonCode);
        Assert.Equal("Unknown", (await inconclusive.Journal.GetAsync(inconclusiveAction.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, inconclusive.ExecutionCount);

        using var interrupted = new Fixture(ToolRiskClass.ReadOnly);
        var interruptedAction = await interrupted.MarkExecutingAsync(Request);
        interrupted.Verification = async (_, _, execution, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, execution.Status);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
        };
        var interruption = await interrupted.CreateRestartedDispatcher().DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Unknown", interruption.Status);
        Assert.Equal("interrupted_execution_observation_interrupted", interruption.ReasonCode);
        Assert.Equal("Unknown", (await interrupted.Journal.GetAsync(interruptedAction.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, interrupted.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestartedDispatcherSurfacesInvalidOrFailedObservationAndPreservesUncertainty()
    {
        using var invalid = new Fixture(ToolRiskClass.ReadOnly);
        var invalidAction = await invalid.MarkExecutingAsync(Request);
        invalid.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome((ToolVerificationStatus)99, null));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await invalid.CreateRestartedDispatcher().DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", (await invalid.Journal.GetAsync(invalidAction.Id, CancellationToken.None))!.Status);

        using var failed = new Fixture(ToolRiskClass.ReadOnly);
        var failedAction = await failed.MarkExecutingAsync(Request);
        failed.Verification = (_, _, _, _) => throw new InvalidOperationException("controlled recovery failure");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failed.CreateRestartedDispatcher().DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", (await failed.Journal.GetAsync(failedAction.Id, CancellationToken.None))!.Status);

        using var unjournaled = new Fixture(ToolRiskClass.ReadOnly);
        var unjournaledAction = await unjournaled.MarkExecutingAsync(Request);
        unjournaled.Verification = (_, _, _, _) => throw new InvalidOperationException("controlled recovery failure");
        unjournaled.Journal.FailUnknownUpdate = true;
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await unjournaled.CreateRestartedDispatcher().DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Executing", (await unjournaled.Journal.GetAsync(unjournaledAction.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, unjournaled.ExecutionCount);
    }

    private sealed class Fixture : IDisposable
    {
        private int executions;
        private int verifications;

        public Fixture(ToolRiskClass riskClass = ToolRiskClass.LowRiskWrite)
        {
            Journal = new MemoryJournal();
            ApprovalStore = new MemoryApprovalStore();
            Clock = new MutableClock(Start);
            Approvals = new ApprovalService(ApprovalStore, Journal, Clock);
            Policy = new MutablePolicy("policy-v1");
            var registration = new ToolRegistration<ToolArguments>(
                "home.set_light",
                """{"type":"object","properties":{"Entity":{"type":"string"},"On":{"type":"boolean"}},"required":["Entity","On"],"additionalProperties":false}""",
                riskClass,
                PrepareAsync,
                ExecuteAsync,
                VerifyAsync);
            Registry = new ToolRegistry([registration]);
            Dispatcher = new ToolDispatcher(
                Registry,
                Policy,
                Journal,
                Approvals,
                Clock,
                TimeSpan.FromMilliseconds(100));
        }

        public MemoryJournal Journal { get; }
        public MemoryApprovalStore ApprovalStore { get; }
        public MutableClock Clock { get; }
        public MutablePolicy Policy { get; }
        public ApprovalService Approvals { get; }
        public ToolRegistry Registry { get; }
        public ToolDispatcher Dispatcher { get; }
        public int ExecutionCount => Volatile.Read(ref executions);
        public int VerificationCount => Volatile.Read(ref verifications);
        public Func<ToolArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> Execution { get; set; } =
            (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, """{"ok":true}""", null));
            };
        public Func<ToolArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> Verification { get; set; } =
            (_, _, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
            };
        public Func<ToolArguments, ToolDispatchRequest, ToolPreparationResult>? PrepareOverride { get; set; }

        public ToolDispatcher CreateRestartedDispatcher() =>
            new(Registry, Policy, Journal, Approvals, Clock, TimeSpan.FromMilliseconds(100));

        public async Task<ActionJournalEntry> MarkExecutingAsync(ToolDispatchRequest request)
        {
            var completed = await Dispatcher.DispatchAsync(request, CancellationToken.None);
            var current = await Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None)
                ?? throw new InvalidOperationException("The action was not journaled.");
            return await Journal.UpdateStatusAsync(
                current.Id,
                "Executing",
                current.Version,
                Clock.UtcNow,
                CancellationToken.None);
        }

        public void Dispose()
        {
        }

        private ValueTask<ToolPreparationResult> PrepareAsync(
            ToolArguments arguments,
            ToolDispatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareOverride is not null)
            {
                return ValueTask.FromResult(PrepareOverride(arguments, request));
            }

            if (arguments.Entity == "porch")
            {
                return ValueTask.FromResult(ToolPreparationResult.Clarify("entity_alias_ambiguous"));
            }

            var entity = arguments.Entity == "porch-lamp" ? "light.porch" : arguments.Entity;
            var operation = arguments.On ? "turn_on" : "turn_off";
            return ValueTask.FromResult(ToolPreparationResult.Ready(new ToolActionDraft(
                "home.set_light",
                operation,
                entity,
                JsonSerializer.Serialize(new { entityId = entity, on = arguments.On }),
                request.CurrentUserRequest == "Turn on the porch light")));
        }

        private async ValueTask<ToolExecutionOutcome> ExecuteAsync(
            ToolArguments arguments,
            ToolActionDraft action,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executions);
            return await Execution(arguments, action, cancellationToken);
        }

        private async ValueTask<ToolVerificationOutcome> VerifyAsync(
            ToolArguments arguments,
            ToolActionDraft action,
            ToolExecutionOutcome outcome,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref verifications);
            return await Verification(arguments, action, outcome, cancellationToken);
        }
    }

    private sealed record ToolArguments(string Entity, bool On);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset now = now;

        public DateTimeOffset UtcNow => now;

        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }

    private sealed class MutablePolicy(string version) : IToolAuthorizationPolicy
    {
        private ToolAllowlistPolicy current = Create(version);

        public void Replace(string newVersion) => current = Create(newVersion);

        public ToolAuthorizationDecision Evaluate(
            string toolName,
            ToolActionDraft action,
            ToolRiskClass declaredRisk) =>
            AllowUnsupported
                ? new(true, "test_allow_unsupported", current.PolicyVersion, ToolRiskClass.Unsupported)
                : current.Evaluate(toolName, action, declaredRisk);

        private static ToolAllowlistPolicy Create(string version) =>
            new(version,
            [
                new ToolPermission("home.set_light", "home.set_light", "turn_on", "light.porch", ToolRiskClass.ReadOnly),
                new ToolPermission("home.set_light", "home.set_light", "turn_off", "light.porch", ToolRiskClass.ReadOnly)
            ]);

        public bool AllowUnsupported { get; set; }
    }

    private sealed class MemoryJournal : IActionJournalStore
    {
        private readonly Dictionary<ActionId, ActionJournalEntry> entries = [];
        private readonly object gate = new();

        public bool FailNextExecutingUpdate { get; set; }
        public string? ReturnStatusAfterExecuting { get; set; }
        public string? ReturnStatusAfterAwaitingApproval { get; set; }
        public Action<string>? StatusUpdated { get; set; }

        public IReadOnlyCollection<ActionJournalEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.Values.ToArray();
                }
            }
        }

        public void Corrupt(ActionId id)
        {
            lock (gate)
            {
                entries[id] = entries[id] with
                {
                    CanonicalArguments = "invalid-envelope",
                    RequestHash = "changed-hash"
                };
            }
        }

        public bool FailUnknownUpdate { get; set; }

        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (entries.TryGetValue(entry.Id, out var existing))
                {
                    if (existing.RequestHash != entry.RequestHash)
                    {
                        throw new PersistenceConcurrencyException("Action identity content changed.");
                    }

                    return ValueTask.FromResult(existing);
                }

                entries.Add(entry.Id, entry);
                return ValueTask.FromResult(entry);
            }
        }

        public ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                entries.TryGetValue(id, out var entry);
                return ValueTask.FromResult(entry);
            }
        }

        public ValueTask<ActionJournalEntry> UpdateStatusAsync(
            ActionId id,
            string status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (status == "Executing" && FailNextExecutingUpdate)
                {
                    FailNextExecutingUpdate = false;
                    throw new PersistenceConcurrencyException("Controlled execution arbitration conflict.");
                }

                if (status == "Unknown" && FailUnknownUpdate)
                {
                    throw new PersistenceConcurrencyException("Controlled journal failure.");
                }

                if (!entries.TryGetValue(id, out var entry))
                {
                    throw new KeyNotFoundException();
                }

                if (entry.Version != expectedVersion)
                {
                    throw new PersistenceConcurrencyException("Action version changed.");
                }

                var returnedStatus = status switch
                {
                    "Executing" when ReturnStatusAfterExecuting is not null => ReturnStatusAfterExecuting,
                    "AwaitingApproval" when ReturnStatusAfterAwaitingApproval is not null => ReturnStatusAfterAwaitingApproval,
                    _ => status
                };
                var updated = entry with { Status = returnedStatus!, UpdatedAtUtc = updatedAtUtc, Version = entry.Version + 1 };
                entries[id] = updated;
                StatusUpdated?.Invoke(status);
                return ValueTask.FromResult(updated);
            }
        }
    }

    private sealed class MemoryApprovalStore : IApprovalStore
    {
        private readonly Dictionary<ApprovalId, ApprovalStorageRecord> entries = [];
        private readonly object gate = new();

        public IReadOnlyCollection<ApprovalStorageRecord> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.Values.ToArray();
                }
            }
        }

        public void Replace(ApprovalStorageRecord approval)
        {
            lock (gate)
            {
                entries[approval.Id] = approval;
            }
        }

        public ValueTask<ApprovalStorageRecord> CreateAsync(ApprovalStorageRecord approval, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (entries.TryGetValue(approval.Id, out var existing))
                {
                    return ValueTask.FromResult(existing);
                }

                entries.Add(approval.Id, approval);
                return ValueTask.FromResult(approval);
            }
        }

        public ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                entries.TryGetValue(id, out var entry);
                return ValueTask.FromResult(entry);
            }
        }

        public ValueTask<ApprovalStorageRecord> ResolveAsync(
            ApprovalId id,
            string ownerId,
            string status,
            long expectedVersion,
            DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!entries.TryGetValue(id, out var entry)
                    || entry.OwnerId != ownerId
                    || entry.Version != expectedVersion
                    || entry.Status != "Pending"
                    || resolvedAtUtc < entry.CreatedAtUtc
                    || (status != "Expired" && resolvedAtUtc >= entry.ExpiresAtUtc))
                {
                    throw new PersistenceConcurrencyException("Approval is stale, expired, or belongs to another owner.");
                }

                var updated = entry with
                {
                    Status = status,
                    ResolvedAtUtc = resolvedAtUtc,
                    Version = entry.Version + 1
                };
                entries[id] = updated;
                return ValueTask.FromResult(updated);
            }
        }
    }
}
