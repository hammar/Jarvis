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
    public async Task MissingRequiredArgumentsAreRejectedBeforePreparationOrJournalWrites()
    {
        using var fixture = new Fixture();
        fixture.PrepareOverride = (_, _) =>
            throw new InvalidOperationException("Malformed arguments must not reach preparation.");

        foreach (var json in new[] { """{"Entity":"light.porch"}""", """{"On":true}""", "{}" })
        {
            var result = await fixture.Dispatcher.DispatchAsync(
                Request with { ArgumentsJson = json }, CancellationToken.None);
            Assert.Equal("Rejected", result.Status);
            Assert.Equal("invalid_tool_arguments", result.ReasonCode);
        }

        Assert.Empty(fixture.Journal.Entries);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal(0, fixture.VerificationCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task AuthorizationIsRecheckedAfterDurableClaimBeforeSending(bool expire)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        fixture.Journal.StatusUpdated = status =>
        {
            if (status == "Executing")
            {
                if (expire)
                {
                    fixture.Clock.Advance(TimeSpan.FromMinutes(5));
                }
                else
                {
                    fixture.Policy.Replace("policy-v2");
                }
            }
        };

        var result = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal(expire ? "Expired" : "Rejected", result.Status);
        Assert.Equal(expire ? "approval_expired_before_execution" : "policy_changed_before_execution", result.ReasonCode);
        Assert.Equal(result.Status, fixture.Journal.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal(0, fixture.VerificationCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AutomaticActionCannotExecuteWhenPolicyChangesWhileClaimingIt()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        fixture.Journal.StatusUpdated = status =>
        {
            if (status == "Executing")
            {
                fixture.Policy.Replace("policy-v2");
            }
        };

        var result = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);

        Assert.Equal("Rejected", result.Status);
        Assert.Equal("policy_changed_before_execution", result.ReasonCode);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.ReadOnly, false)]
    [InlineData(ToolRiskClass.ReadOnly, true)]
    [InlineData(ToolRiskClass.LowRiskWrite, false)]
    [Trait("Category", "Unit")]
    public async Task TwoEquivalentFailuresStopFreshCallIdsButNotLaterTurns(ToolRiskClass riskClass, bool throws)
    {
        using var fixture = new Fixture(riskClass);
        fixture.Execution = (_, _, _) => throws
            ? throw new InvalidOperationException("controlled failed execution")
            : ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = Request with { CallId = $"failed-{attempt}" };
            if (throws)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await fixture.Dispatcher.DispatchAsync(request, CancellationToken.None));
            }
            else
            {
                Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(request, CancellationToken.None)).Status);
            }
        }

        var rejected = await fixture.Dispatcher.DispatchAsync(
            Request with
            {
                CallId = "failed-3",
                ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}"""
            }, CancellationToken.None);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("equivalent_failure_limit", rejected.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
        fixture.Dispatcher.ReleaseTurn(TurnId.New());
        fixture.Dispatcher.ReleaseTurn(Request.TurnId);
        var duplicate = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "failed-0" }, CancellationToken.None);
        Assert.Equal(throws ? "Unknown" : "Failed", duplicate.Status);
        Assert.Equal(2, fixture.ExecutionCount);

        fixture.Execution = (_, _, _) => ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, null));
        var later = await fixture.Dispatcher.DispatchAsync(
            Request with { TurnId = TurnId.New() }, CancellationToken.None);
        Assert.Equal("Failed", later.Status);
        Assert.Equal(3, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentEquivalentFailuresCannotRacePastTheTwoAttemptLimit()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure");
        };
        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "concurrent-2" }, CancellationToken.None).AsTask();
        var third = fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "concurrent-3" }, CancellationToken.None).AsTask();
        release.SetResult();
        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, results.Count(result => result.Status == "Failed"));
        Assert.Single(results, result => result.ReasonCode == "equivalent_failure_limit");
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    public async Task StaleApprovalReadCannotReportExpiryAfterExecutionBegins(bool completed, bool pendingRead)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        if (!pendingRead)
        {
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }

        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.ApprovalStore.AfterRead = async (_, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        fixture.Execution = async (_, _, token) =>
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        };

        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (pendingRead)
        {
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }

        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            if (completed)
            {
                releaseExecution.SetResult();
                Assert.Equal("Succeeded", (await first).Status);
            }

            releaseRead.SetResult();
            var duplicate = await stale.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(completed ? "Succeeded" : "Executing", duplicate.Status);
            Assert.Equal(completed ? null : "recorded_action_outcome", duplicate.ReasonCode);
            Assert.Equal(pending.ActionId, duplicate.ActionId);
            Assert.Equal(duplicate.Status, fixture.Journal.Entries.Single().Status);
            Assert.Equal(1, fixture.ExecutionCount);
        }
        finally
        {
            releaseRead.TrySetResult();
            releaseExecution.TrySetResult();
            await first;
        }
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "execution-error")]
    [InlineData(true, "execution-error")]
    [InlineData(false, "execution-cancelled")]
    [InlineData(true, "execution-cancelled")]
    [InlineData(false, "verification-error")]
    [InlineData(true, "verification-error")]
    [InlineData(false, "verification-cancelled")]
    [InlineData(true, "verification-cancelled")]
    [InlineData(false, "journal-error")]
    [InlineData(true, "journal-error")]
    [Trait("Category", "Unit")]
    public async Task UncertainWritesBlockEquivalentFreshIdsIncludingQueuedAttempts(bool concurrent, string outcome)
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return outcome switch
            {
                "execution-error" => throw new InvalidOperationException("controlled execution failure"),
                "execution-cancelled" => throw new OperationCanceledException(),
                _ => new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "delivery_uncertain")
            };
        };
        fixture.Verification = (_, _, _, _) => outcome switch
        {
            "verification-error" => throw new InvalidOperationException("controlled verification failure"),
            "verification-cancelled" => throw new OperationCanceledException(),
            _ => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"))
        };
        fixture.Journal.FailUnknownUpdate = outcome == "journal-error";

        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextRequest = Request with { CallId = "fresh-id", ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}""" };
        var next = concurrent
            ? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask()
            : null;
        release.SetResult();
        if (outcome is "execution-error" or "verification-error")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        }
        else if (outcome == "journal-error")
        {
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() => first);
        }
        else
        {
            Assert.Equal("Unknown", (await first).Status);
        }

        var rejected = await (next ?? fixture.Dispatcher.DispatchAsync(nextRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("equivalent_write_outcome_unknown", rejected.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Rejected", (await fixture.Journal.GetAsync(rejected.ActionId!.Value, CancellationToken.None))!.Status);
        Assert.Equal(outcome == "journal-error" ? "Executing" : "Unknown",
            fixture.Journal.Entries.Single(entry => entry.Id != rejected.ActionId).Status);

        fixture.Journal.FailUnknownUpdate = false;
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null));
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        var later = await fixture.Dispatcher.DispatchAsync(
            Request with { TurnId = TurnId.New() }, CancellationToken.None);
        Assert.Equal("Succeeded", later.Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolRiskClass.LowRiskWrite, false)]
    [InlineData(ToolRiskClass.ReadOnly, false)]
    [InlineData(ToolRiskClass.ReadOnly, true)]
    [Trait("Category", "Unit")]
    public async Task DelayedDuplicateDoesNotConsumeASecondFailureSlot(ToolRiskClass riskClass, bool throws)
    {
        using var fixture = new Fixture(riskClass);
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = 0;
        fixture.Journal.BeforeExecutingUpdate = async token =>
        {
            if (Interlocked.Increment(ref claims) == 1)
            {
                claiming.SetResult();
                await releaseClaim.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "Prepared")
            {
                reading.TrySetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        fixture.Execution = (_, _, _) => throws
            ? throw new InvalidOperationException("controlled failed execution")
            : ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));

        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await claiming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseClaim.SetResult();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        }
        else
        {
            Assert.Equal("Failed", (await first).Status);
        }

        releaseRead.SetResult();
        var replay = await duplicate.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(throws ? "Unknown" : "Failed", replay.Status);
        Assert.Equal(throws ? "tool_execution_exception" : "known_failure", replay.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);

        var second = fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "second-attempt" }, CancellationToken.None).AsTask();
        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        }
        else
        {
            Assert.Equal("Failed", (await second).Status);
        }
        Assert.Equal(2, fixture.ExecutionCount);
        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "third-attempt" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    public async Task MissingApprovalSnapshotReturnsTheExistingDecisionAfterExecutionStarts(bool completed)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var missingReads = 0;
        fixture.ApprovalStore.AfterRead = async (entry, token) =>
        {
            if (entry is null && Interlocked.Increment(ref missingReads) == 1)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        };

        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            if (completed)
            {
                releaseExecution.SetResult();
                Assert.Equal("Succeeded", (await first).Status);
            }

            releaseRead.SetResult();
            var duplicate = await stale.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(completed ? "Succeeded" : "Executing", duplicate.Status);
            Assert.Equal(pending.ActionId, duplicate.ActionId);
            Assert.Equal("Approved", fixture.ApprovalStore.Entries.Single().Status);
            Assert.Equal(1, fixture.ExecutionCount);
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
                await fixture.Approvals.CreateAsync(pending.ActionId!.Value, "another-owner", CancellationToken.None));
        }
        finally
        {
            releaseRead.TrySetResult();
            releaseExecution.TrySetResult();
            await first;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    public async Task StalePolicyInvalidationPreservesApprovalWinnerButStillRejectsChangedContent(bool expired, bool changedContent)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        fixture.Policy.Replace("policy-v2");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "AwaitingApproval" && Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        var request = changedContent ? Request with { ArgumentsJson = """{"Entity":"light.porch","On":false}""" } : Request;
        var stale = fixture.Dispatcher.DispatchAsync(request, CancellationToken.None).AsTask();
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (expired)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            }
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None);
            var winner = fixture.Journal.Entries.Single();
            release.SetResult();
            var result = await stale.WaitAsync(TimeSpan.FromSeconds(2));
            if (changedContent)
            {
                Assert.Equal(new ToolDispatchResult("Rejected", null, "action_identity_reused", winner.Id), result);
            }
            else
            {
                Assert.Equal(winner.Outcome, result);
                Assert.Equal(pending.ApprovalId, result.ApprovalId);
            }
            Assert.Equal(winner, fixture.Journal.Entries.Single());
            Assert.Equal(0, fixture.ExecutionCount);
            Assert.Equal(0, fixture.VerificationCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Rejected")]
    [Trait("Category", "Unit")]
    public async Task StaleApprovalDecisionCannotReplaceAPolicyInvalidationWinner(string decision)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        if (decision == "Expired")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        await fixture.ApprovalStore.ResolveAsync(pending.ApprovalId!.Value, "owner", decision, 1,
            fixture.Clock.UtcNow, CancellationToken.None);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.ApprovalStore.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == decision && Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Policy.Replace("policy-v2");
            var winner = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
            var persisted = fixture.Journal.Entries.Single();
            Assert.Equal("action_identity_reused", winner.ReasonCode);
            release.SetResult();
            Assert.Equal(winner, await stale.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(persisted, fixture.Journal.Entries.Single());
            Assert.Equal(0, fixture.ExecutionCount);
            Assert.Equal(0, fixture.VerificationCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Executing")]
    [InlineData("Succeeded")]
    [InlineData("Approved")]
    [Trait("Category", "Unit")]
    public async Task NonexpiredPendingSnapshotReturnsTheWinningDecisionWithoutRequestingAnotherApproval(string winner)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.ApprovalStore.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "Pending" && Interlocked.Increment(ref reads) == 1)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        fixture.Execution = async (_, _, token) =>
        {
            executing.TrySetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        };

        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", 1, winner != "Rejected", CancellationToken.None);
        Task<ToolDispatchResult>? first = null;
        try
        {
            if (winner is "Executing" or "Succeeded")
            {
                first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
                await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (winner == "Succeeded")
                {
                    releaseExecution.SetResult();
                    Assert.Equal("Succeeded", (await first).Status);
                }
            }
            else
            {
                releaseExecution.SetResult();
            }

            releaseRead.SetResult();
            var result = await stale.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(winner == "Approved" ? "Succeeded" : winner, result.Status);
            Assert.NotEqual("owner_approval_required", result.ReasonCode);
            Assert.Equal(pending.ActionId, result.ActionId);
            Assert.Equal(result.Status, fixture.Journal.Entries.Single().Status);
            Assert.Equal(winner == "Rejected" ? 0 : 1, fixture.ExecutionCount);
            Assert.Equal(2, fixture.ApprovalStore.Entries.Single().Version);
            Assert.Equal(Start, fixture.Clock.UtcNow);
        }
        finally
        {
            releaseRead.TrySetResult();
            releaseExecution.TrySetResult();
            if (first is not null)
            {
                await first;
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    public async Task KnownRecoveryUpdatesTheSameProposalBudgetBeforeFreshCalls(bool verified, bool queued)
    {
        using var fixture = new Fixture();
        fixture.Journal.FailCompletionUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Executing", fixture.Journal.Entries.Single().Status);
        fixture.Journal.FailCompletionUpdate = false;
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Verification = async (_, _, outcome, token) =>
        {
            Assert.Equal(ToolExecutionStatus.Unknown, outcome.Status);
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolVerificationOutcome(
                verified ? ToolVerificationStatus.Verified : ToolVerificationStatus.NotApplied, null);
        };
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));

        var recovery = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshRequest = Request with { CallId = "after-recovery", ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}""" };
        var fresh = queued ? fixture.Dispatcher.DispatchAsync(freshRequest, CancellationToken.None).AsTask() : null;
        releaseObservation.SetResult();
        Assert.Equal(verified ? "Succeeded" : "Failed", (await recovery).Status);
        Assert.Equal("Failed", (await (fresh ?? fixture.Dispatcher.DispatchAsync(freshRequest, CancellationToken.None).AsTask())).Status);
        Assert.Equal(2, fixture.ExecutionCount);

        if (verified)
        {
            Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(
                Request with { CallId = "second-known-failure" }, CancellationToken.None)).Status);
        }

        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "past-two-known-failures" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(verified ? 3 : 2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("verification-error")]
    [InlineData("verification-cancelled")]
    [InlineData("journal-error")]
    [Trait("Category", "Unit")]
    public async Task InconclusiveOrUnjournaledRecoveryKeepsEquivalentWritesBlocked(string outcome)
    {
        using var fixture = new Fixture();
        fixture.Journal.FailCompletionUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        fixture.Journal.FailCompletionUpdate = outcome == "journal-error";
        fixture.Verification = (_, _, _, _) => outcome switch
        {
            "verification-error" => throw new InvalidOperationException("controlled recovery error"),
            "verification-cancelled" => throw new OperationCanceledException(),
            "journal-error" => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null)),
            _ => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"))
        };
        var recovery = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        if (outcome is "verification-error" or "journal-error")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery);
        }
        else
        {
            Assert.Equal("Unknown", (await recovery).Status);
        }

        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "after-unconfirmed-recovery" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(outcome == "journal-error" ? "Executing" : "Unknown",
            fixture.Journal.Entries.Single(entry => entry.Id != stopped.ActionId).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    public async Task PersistedExecutingWriteBlocksEquivalentFreshCallsUntilKnownRecovery(bool verified)
    {
        using var fixture = new Fixture();
        await fixture.MarkExecutingAsync(Request);
        var blocked = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "uncertain-action" }, CancellationToken.None);
        Assert.Equal("Rejected", blocked.Status);
        Assert.Equal("equivalent_write_outcome_unknown", blocked.ReasonCode);
        fixture.Verification = (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(
            verified ? ToolVerificationStatus.Verified : ToolVerificationStatus.NotApplied, null));

        Assert.Equal(verified ? "Succeeded" : "Failed",
            (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal(verified ? "Succeeded" : "Failed", (await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "after-known-recovery" }, CancellationToken.None)).Status);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveredReadFailureConsumesOnlyOneAttempt()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        await fixture.MarkExecutingAsync(Request);
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, null));
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "second-failed-read" }, CancellationToken.None)).Status);

        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "third-failed-read" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExceptionalReadRecoveryConsumesOnlyOneAttempt()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        await fixture.MarkExecutingAsync(Request);
        fixture.Verification = (_, _, _, _) => throw new InvalidOperationException("controlled observation failure");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal("Unknown", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "after-failed-observation" }, CancellationToken.None)).Status);
        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "past-failure-budget" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellingRecoveryWhileWaitingForTheProposalGateReleasesItsActiveClaim()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        await fixture.MarkExecutingAsync(Request);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null);
        };
        var first = fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "holding-proposal-gate" }, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = fixture.Dispatcher.DispatchAsync(Request, cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            releaseExecution.SetResult();
            await first;
        }

        Assert.Equal("Succeeded", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(3, fixture.VerificationCount);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [Trait("Category", "Unit")]
    public async Task TerminalReplayReconcilesCancelledStaleRecoveryWithoutClearingAnotherOrphan(
        bool failed, bool changedPolicy, bool anotherOrphan, bool failReplayRead)
    {
        using var fixture = new Fixture();
        var otherRequest = Request with { CallId = "other-unresolved-orphan" };
        ActionJournalEntry? otherOrphan = null;
        if (anotherOrphan)
        {
            var otherCompleted = await fixture.Dispatcher.DispatchAsync(otherRequest, CancellationToken.None);
            otherOrphan = (await fixture.Journal.GetAsync(otherCompleted.ActionId!.Value, CancellationToken.None))!;
        }
        var restarted = fixture.CreateRestartedDispatcher();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(
                failed ? ToolExecutionStatus.Failed : ToolExecutionStatus.Completed, null, null);
        };
        var first = restarted.DispatchAsync(Request, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Inline completion resumes the stale read through registration to the next gate wait before cancellation.
        var releaseRead = new TaskCompletionSource();
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var readDeadlineRegistration = readDeadline.Token.Register(
            () => releaseRead.TrySetCanceled(readDeadline.Token));
        var paused = 0;
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "Executing" && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task;
            }
        };
        using var cancellation = new CancellationTokenSource();
        var stale = restarted.DispatchAsync(Request, cancellation.Token).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseExecution.SetResult();
        var winner = await first;
        Assert.Equal(failed ? "Failed" : "Succeeded", winner.Status);
        if (otherOrphan is not null)
        {
            await fixture.Journal.UpdateStatusAsync(
                otherOrphan.Id, "Executing", otherOrphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        }
        cancellation.Cancel();
        releaseRead.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        var sends = fixture.ExecutionCount;
        var observations = fixture.VerificationCount;
        var beforeReplay = await restarted.DispatchAsync(
            Request with { CallId = "before-terminal-reconciliation" }, CancellationToken.None);
        Assert.Equal("equivalent_write_outcome_unknown", beforeReplay.ReasonCode);
        Assert.Equal(sends, fixture.ExecutionCount);
        if (changedPolicy)
        {
            fixture.Policy.Replace("new-policy");
        }
        if (failReplayRead)
        {
            var reads = 0;
            fixture.Journal.AfterRead = (entry, _) =>
            {
                if (entry?.Id == winner.ActionId && Interlocked.Increment(ref reads) == 2)
                {
                    throw new InvalidOperationException("controlled terminal refresh failure");
                }
                return Task.CompletedTask;
            };
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.DispatchAsync(Request, CancellationToken.None));
            var blocked = await restarted.DispatchAsync(
                Request with { CallId = "after-failed-terminal-refresh" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", blocked.ReasonCode);
            fixture.Journal.AfterRead = null;
        }
        var replay = await restarted.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal(winner.Status, replay.Status);
        Assert.Equal(winner, replay);
        Assert.Equal(sends, fixture.ExecutionCount);
        Assert.Equal(observations, fixture.VerificationCount);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        if (anotherOrphan)
        {
            var blocked = await restarted.DispatchAsync(
                Request with { CallId = "other-orphan-must-remain-blocked" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", blocked.ReasonCode);
            Assert.Equal(sends, fixture.ExecutionCount);
            Assert.Equal("Succeeded", (await restarted.DispatchAsync(otherRequest, CancellationToken.None)).Status);
        }
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            Request with { CallId = "fresh-after-terminal-reconciliation" }, CancellationToken.None)).Status);
        if (!failed)
        {
            Assert.Equal("Failed", (await restarted.DispatchAsync(
                Request with { CallId = "second-failure-after-terminal-reconciliation" }, CancellationToken.None)).Status);
        }
        var stopped = await restarted.DispatchAsync(
            Request with { CallId = "past-reconciled-failure-budget" }, CancellationToken.None);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(sends + (failed ? 1 : 2), fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("lowered-risk")]
    [Trait("Category", "Unit")]
    public async Task OrphanRegisteredDuringTheClaimPreventsFinalSendAdmission(string originalRisk)
    {
        using var fixture = new Fixture(
            originalRisk == "write" ? ToolRiskClass.LowRiskWrite : ToolRiskClass.ReadOnly);
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.RequireApproval();
            var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }
        var completed = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var orphan = (await fixture.Journal.GetAsync(completed.ActionId!.Value, CancellationToken.None))!;
        fixture.Policy.Replace("current-policy");
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        fixture.Journal.BeforeExecutingUpdate = async token =>
        {
            if (Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                claiming.SetResult();
                await releaseClaim.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        var restarted = fixture.CreateRestartedDispatcher();
        var request = Request with { CallId = "claiming-while-orphan-is-discovered" };
        var fresh = restarted.DispatchAsync(request, CancellationToken.None).AsTask();
        await claiming.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Journal.UpdateStatusAsync(
            orphan.Id, "Executing", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var recovery = restarted.DispatchAsync(Request, cancellation.Token).AsTask();
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
        var read = originalRisk == "read";
        Assert.Equal(read ? "Succeeded" : "Rejected", result.Status);
        Assert.Equal(result.Status, fixture.Journal.Entries.Single(entry => entry.Id == result.ActionId).Status);
        Assert.Equal("Executing", fixture.Journal.Entries.Single(entry => entry.Id == orphan.Id).Status);
        Assert.Equal(read ? 2 : 1, fixture.ExecutionCount);
        Assert.Equal(read ? 2 : 1, fixture.VerificationCount);
        if (!read)
        {
            Assert.Equal("equivalent_write_outcome_unknown", result.ReasonCode);
            var stopped = await restarted.DispatchAsync(
                request with { CallId = "after-rejected-unsent-claim" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
            Assert.Equal(1, fixture.ExecutionCount);
        }
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(
            request with { CallId = "after-known-orphan-recovery" }, CancellationToken.None)).Status);
        Assert.Equal(read ? 3 : 2, fixture.ExecutionCount);
        Assert.Equal(read ? 4 : 3, fixture.VerificationCount);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("lowered-risk")]
    [Trait("Category", "Unit")]
    public async Task CancelledQueuedRecoveryRetainsOriginalWriteRiskAfterTheGateHolderSucceeds(string originalRisk)
    {
        using var fixture = new Fixture(
            originalRisk == "write" ? ToolRiskClass.LowRiskWrite : ToolRiskClass.ReadOnly);
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.RequireApproval();
            var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }
        var orphan = await fixture.MarkExecutingAsync(Request);
        fixture.Policy.Replace("current-policy");
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Verification = async (_, _, _, token) =>
        {
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolVerificationOutcome(ToolVerificationStatus.Verified, null);
        };
        var restarted = fixture.CreateRestartedDispatcher();
        var first = restarted.DispatchAsync(Request, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelled = restarted.DispatchAsync(
            Request with { CallId = "cancelled-queued-recovery" }, cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            releaseObservation.SetResult();
            Assert.Equal("Succeeded", (await first).Status);
        }
        Assert.Equal("Succeeded", fixture.Journal.Entries.Single(entry => entry.Id == orphan.Id).Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        var next = await restarted.DispatchAsync(Request with
        {
            CallId = "after-cancelled-recovery",
            ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}"""
        }, CancellationToken.None);
        Assert.Equal("Failed", next.Status);
        Assert.Equal(2, fixture.ExecutionCount);

        Assert.Equal("Succeeded", (await restarted.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal(2, fixture.VerificationCount);
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            Request with { CallId = "after-confirmed-cancelled-recovery" }, CancellationToken.None)).Status);
        Assert.Equal(3, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed)]
    [InlineData(ToolExecutionStatus.Unknown)]
    [Trait("Category", "Unit")]
    public async Task DefinitiveNonApplicationPermitsOnlyTheSecondEquivalentAttempt(ToolExecutionStatus executionStatus)
    {
        using var fixture = new Fixture();
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(executionStatus, null, "transport_result"));
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.NotApplied, "definitively_not_applied"));

        var first = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Failed", first.Status);
        Assert.Equal("Failed", fixture.Journal.Entries.Single().Status);
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal("Failed", (await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "second-definitively-failed-attempt" }, CancellationToken.None)).Status);
        var stopped = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "third-definitively-failed-attempt" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(2, fixture.VerificationCount);
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed, "denied")]
    [InlineData(ToolExecutionStatus.Unknown, "denied")]
    [InlineData(ToolExecutionStatus.Completed, "unsupported")]
    [InlineData(ToolExecutionStatus.Unknown, "unsupported")]
    [InlineData(ToolExecutionStatus.Completed, "allowed")]
    [InlineData(ToolExecutionStatus.Unknown, "allowed")]
    [Trait("Category", "Unit")]
    public async Task NormalVerificationRechecksPermissionAfterExecution(
        ToolExecutionStatus delivery, string permission)
    {
        using var fixture = new Fixture();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, "delivery_result");
        };
        var dispatch = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (permission == "denied")
        {
            fixture.Policy.RevokePermissions();
        }
        else if (permission == "unsupported")
        {
            fixture.Policy.AllowUnsupported = true;
        }
        else
        {
            fixture.Policy.Replace("allowed-v2");
        }
        releaseExecution.SetResult();
        var result = await dispatch;
        Assert.Equal(permission == "allowed" ? "Succeeded" : "Unknown", result.Status);
        Assert.Equal(permission == "allowed" ? 1 : 0, fixture.VerificationCount);
        Assert.Equal(result.Status, fixture.Journal.Entries.Single().Status);
        Assert.Equal(1, fixture.ExecutionCount);
        if (permission != "allowed")
        {
            Assert.Equal("execution_observation_not_permitted", result.ReasonCode);
            fixture.Policy.AllowUnsupported = false;
            fixture.Policy.Replace("restored");
            var stopped = await fixture.Dispatcher.DispatchAsync(
                Request with { CallId = "after-revoked-observation" }, CancellationToken.None);
            Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
            Assert.Equal(1, fixture.ExecutionCount);
        }
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Completed, "denied")]
    [InlineData(ToolExecutionStatus.Completed, "unsupported")]
    [InlineData(ToolExecutionStatus.Completed, "observation-cancelled")]
    [InlineData(ToolExecutionStatus.Failed, "denied")]
    [InlineData(ToolExecutionStatus.Unknown, "denied")]
    [Trait("Category", "Unit")]
    public async Task StaleExecutingReadReturnsWinningOutcomeWithoutObservingAgain(
        ToolExecutionStatus delivery, string change)
    {
        using var fixture = new Fixture();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(delivery, null, "delivery_result");
        };
        if (delivery == ToolExecutionStatus.Unknown)
        {
            fixture.Verification = (_, _, _, _) =>
                ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"));
        }
        var first = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "Executing" && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseExecution.SetResult();
        var winner = await first;
        var observations = fixture.VerificationCount;
        if (change == "denied")
        {
            fixture.Policy.RevokePermissions();
        }
        else if (change == "unsupported")
        {
            fixture.Policy.AllowUnsupported = true;
        }
        else
        {
            fixture.Verification = (_, _, _, _) => throw new OperationCanceledException();
        }
        releaseRead.SetResult();
        var recorded = await stale;
        Assert.Equal(winner.Status, recorded.Status);
        Assert.Equal(winner.ActionId, recorded.ActionId);
        Assert.Equal(winner, recorded);
        Assert.Equal(winner.Status, fixture.Journal.Entries.Single().Status);
        Assert.Equal(observations, fixture.VerificationCount);
        Assert.Equal(1, fixture.ExecutionCount);
        fixture.Policy.AllowUnsupported = false;
        fixture.Policy.Replace("restored");
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        var fresh = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "after-recorded-winner" }, CancellationToken.None);
        Assert.Equal(delivery == ToolExecutionStatus.Unknown ? "Rejected" : "Failed", fresh.Status);
        if (delivery == ToolExecutionStatus.Unknown)
        {
            Assert.Equal("equivalent_write_outcome_unknown", fresh.ReasonCode);
        }
        Assert.Equal(delivery == ToolExecutionStatus.Unknown ? 1 : 2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Expired")]
    [Trait("Category", "Unit")]
    public async Task StaleClaimedReadPreservesKnownNonExecutionWithoutInventingWriteUncertainty(string winner)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "Executing" && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        Task<ToolDispatchResult>? stale = null;
        fixture.Journal.StatusUpdated = status =>
        {
            if (status == "Executing")
            {
                stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
                if (winner == "Rejected")
                {
                    fixture.Policy.RevokePermissions();
                }
                else
                {
                    fixture.Clock.Advance(TimeSpan.FromMinutes(5));
                }
            }
        };
        var first = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(winner, first.Status);
        releaseRead.SetResult();
        Assert.Equal(winner, (await stale!).Status);
        Assert.Equal(0, fixture.ExecutionCount);
        Assert.Equal(0, fixture.VerificationCount);

        fixture.Journal.StatusUpdated = null;
        fixture.Policy.Replace("restored");
        var freshRequest = Request with { CallId = "after-known-non-execution" };
        var fresh = await fixture.Dispatcher.DispatchAsync(freshRequest, CancellationToken.None);
        Assert.Equal("AwaitingApproval", fresh.Status);
        await fixture.Approvals.ResolveAsync(fresh.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        Assert.Equal("Succeeded", (await fixture.Dispatcher.DispatchAsync(freshRequest, CancellationToken.None)).Status);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unsupported")]
    [InlineData("allowed")]
    [Trait("Category", "Unit")]
    public async Task QueuedRecoveryRechecksCurrentAuthorizationBeforeObserving(string permission)
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        await fixture.MarkExecutingAsync(Request);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Execution = async (_, _, token) =>
        {
            executing.SetResult();
            await releaseExecution.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure");
        };
        var first = fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "holding-recovery-gate" }, CancellationToken.None).AsTask();
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var recovery = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        if (permission == "unsupported")
        {
            fixture.Policy.AllowUnsupported = true;
        }
        else if (permission == "denied")
        {
            fixture.Policy.RevokePermissions();
        }
        else
        {
            fixture.Policy.Replace("policy-v2");
        }

        releaseExecution.SetResult();
        Assert.Equal("Failed", (await first).Status);
        var result = await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(permission == "allowed" ? "Succeeded" : "Unknown", result.Status);
        if (permission != "allowed")
        {
            Assert.Equal("interrupted_execution_observation_not_permitted", result.ReasonCode);
        }
        Assert.Equal(permission == "allowed" ? 2 : 1, fixture.VerificationCount);
        Assert.Equal(2, fixture.ExecutionCount);
        Assert.Equal(result.Status, fixture.Journal.Entries.Single(entry => entry.Id == result.ActionId).Status);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "observation-error")]
    [InlineData(true, "observation-error")]
    [InlineData(false, "observation-cancelled")]
    [InlineData(true, "observation-cancelled")]
    [InlineData(false, "unknown-journal-error")]
    [InlineData(true, "unknown-journal-error")]
    [InlineData(false, "completion-journal-error")]
    [InlineData(true, "completion-journal-error")]
    [InlineData(false, "observation-and-journal-error")]
    [InlineData(true, "observation-and-journal-error")]
    [Trait("Category", "Unit")]
    public async Task FreshDispatcherRecoveryBlocksUnresolvedEquivalentWrites(bool queued, string outcome)
    {
        using var fixture = new Fixture();
        var orphan = await fixture.MarkExecutingAsync(Request);
        var restarted = fixture.CreateRestartedDispatcher();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Journal.FailUnknownUpdate = outcome is "unknown-journal-error" or "observation-and-journal-error";
        fixture.Journal.FailCompletionUpdate = outcome == "completion-journal-error";
        fixture.Verification = async (_, _, _, token) =>
        {
            observing.SetResult();
            await releaseObservation.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return outcome switch
            {
                "observation-error" or "observation-and-journal-error" =>
                    throw new InvalidOperationException("controlled orphan observation error"),
                "observation-cancelled" => throw new OperationCanceledException(),
                "completion-journal-error" => new ToolVerificationOutcome(ToolVerificationStatus.Verified, null),
                _ => new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed")
            };
        };
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "unexpected_fresh_send"));
        var recovery = restarted.DispatchAsync(Request, CancellationToken.None).AsTask();
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshRequest = Request with { CallId = "fresh-after-orphan", ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}""" };
        var fresh = queued ? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask() : null;
        releaseObservation.SetResult();
        if (outcome == "observation-and-journal-error")
        {
            await Assert.ThrowsAsync<AggregateException>(() => recovery);
        }
        else if (outcome is "observation-error" or "completion-journal-error")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery);
        }
        else
        {
            Assert.Equal(outcome == "unknown-journal-error" ? "Executing" : "Unknown", (await recovery).Status);
        }

        var stopped = await (fresh ?? restarted.DispatchAsync(freshRequest, CancellationToken.None).AsTask());
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
        var persisted = (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!;
        Assert.Equal(outcome.Contains("journal-error", StringComparison.Ordinal) ? "Executing" : "Unknown", persisted.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryUsesPersistedWriteRiskEvenWhenCurrentPolicyWouldAllowReads()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        fixture.Policy.RequireApproval();
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        await fixture.MarkExecutingAsync(Request);
        fixture.Policy.Replace("read-only-policy");
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"));
        var restarted = fixture.CreateRestartedDispatcher();

        Assert.Equal("Unknown", (await restarted.DispatchAsync(Request, CancellationToken.None)).Status);
        var stopped = await restarted.DispatchAsync(
            Request with { CallId = "read-classification-cannot-clear-original-risk" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveringOneKnownOrphanCannotDiscardAnotherUnresolvedOrphan()
    {
        using var fixture = new Fixture();
        var secondRequest = Request with { CallId = "second-orphan" };
        var first = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var second = await fixture.Dispatcher.DispatchAsync(secondRequest, CancellationToken.None);
        var firstEntry = (await fixture.Journal.GetAsync(first.ActionId!.Value, CancellationToken.None))!;
        var secondEntry = (await fixture.Journal.GetAsync(second.ActionId!.Value, CancellationToken.None))!;
        await fixture.Journal.UpdateStatusAsync(
            firstEntry.Id, "Executing", firstEntry.Version, fixture.Clock.UtcNow, CancellationToken.None);
        await fixture.Journal.UpdateStatusAsync(
            secondEntry.Id, "Executing", secondEntry.Version, fixture.Clock.UtcNow, CancellationToken.None);
        var restarted = fixture.CreateRestartedDispatcher();
        fixture.Journal.FailCompletionUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await restarted.DispatchAsync(Request, CancellationToken.None));
        fixture.Journal.FailCompletionUpdate = false;
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"));
        Assert.Equal("Unknown", (await restarted.DispatchAsync(Request, CancellationToken.None)).Status);
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        Assert.Equal("Succeeded", (await restarted.DispatchAsync(secondRequest, CancellationToken.None)).Status);

        var stopped = await restarted.DispatchAsync(
            Request with { CallId = "after-two-orphans" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("write", "none")]
    [InlineData("write", "cancel")]
    [InlineData("write", "read-error")]
    [InlineData("lowered-risk", "none")]
    [InlineData("lowered-risk", "cancel")]
    [InlineData("lowered-risk", "read-error")]
    [InlineData("read", "none")]
    [InlineData("read", "cancel")]
    [InlineData("read", "read-error")]
    [Trait("Category", "Unit")]
    public async Task PersistedUnknownReplayRetainsOriginalRiskBeforeCancellableRefresh(
        string originalRisk, string replayFailure)
    {
        using var fixture = new Fixture(
            originalRisk == "write" ? ToolRiskClass.LowRiskWrite : ToolRiskClass.ReadOnly);
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.RequireApproval();
            var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
            await fixture.Approvals.ResolveAsync(pending.ApprovalId!.Value, "owner", 1, true, CancellationToken.None);
        }
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Unknown, null, "unconfirmed"));
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"));
        var unknown = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal("Unknown", unknown.Status);
        var persisted = (await fixture.Journal.GetAsync(unknown.ActionId!.Value, CancellationToken.None))!;
        if (originalRisk == "lowered-risk")
        {
            fixture.Policy.Replace("read-only-policy");
        }
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null));
        var restarted = fixture.CreateRestartedDispatcher();
        if (replayFailure == "cancel")
        {
            var replaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            fixture.Journal.AfterRead = async (entry, token) =>
            {
                if (entry?.Id != unknown.ActionId)
                {
                    return;
                }

                switch (Interlocked.Increment(ref reads))
                {
                    case 2:
                        replaying.SetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                        break;
                    case 3:
                        queuedRead.SetResult();
                        break;
                }
            };
            var holder = restarted.DispatchAsync(Request, CancellationToken.None).AsTask();
            await replaying.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var cancellation = new CancellationTokenSource();
            var replay = restarted.DispatchAsync(Request, cancellation.Token).AsTask();
            await queuedRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(replay.IsCompleted);
            cancellation.Cancel();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
            }
            finally
            {
                release.SetResult();
                Assert.Equal("Unknown", (await holder).Status);
            }
            fixture.Journal.AfterRead = null;
        }
        else if (replayFailure == "read-error")
        {
            var reads = 0;
            fixture.Journal.AfterRead = (entry, _) =>
            {
                if (entry?.Id == unknown.ActionId && Interlocked.Increment(ref reads) == 2)
                {
                    throw new InvalidOperationException("controlled Unknown refresh error");
                }
                return Task.CompletedTask;
            };
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.DispatchAsync(Request, CancellationToken.None));
            fixture.Journal.AfterRead = null;
        }
        var sends = fixture.ExecutionCount;
        var observations = fixture.VerificationCount;
        var read = originalRisk == "read";
        if (replayFailure != "none")
        {
            fixture.Execution = (_, _, _) =>
                ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Completed, null, null));
            var afterError = await restarted.DispatchAsync(
                Request with { CallId = "fresh-after-interrupted-unknown-replay" }, CancellationToken.None);
            Assert.Equal(read ? "Succeeded" : "Rejected", afterError.Status);
            if (!read)
            {
                Assert.Equal("equivalent_write_outcome_unknown", afterError.ReasonCode);
            }
            Assert.Equal(sends + (read ? 1 : 0), fixture.ExecutionCount);
            Assert.Equal(observations + (read ? 1 : 0), fixture.VerificationCount);
            observations = fixture.VerificationCount;
        }
        for (var duplicate = 0; duplicate < 2; duplicate++)
        {
            var replay = await restarted.DispatchAsync(Request, CancellationToken.None);
            Assert.Equal("Unknown", replay.Status);
            Assert.Equal(unknown, replay);
        }
        Assert.Equal(persisted, await fixture.Journal.GetAsync(persisted.Id, CancellationToken.None));
        Assert.Equal(observations, fixture.VerificationCount);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        var fresh = await restarted.DispatchAsync(
            Request with { CallId = "fresh-after-persisted-unknown", ArgumentsJson = """{"On":true,"Entity":"porch-lamp"}""" },
            CancellationToken.None);
        Assert.Equal(read ? "Failed" : "Rejected", fresh.Status);
        Assert.Equal(read ? "known_failure" : "equivalent_write_outcome_unknown", fresh.ReasonCode);
        var stopped = await restarted.DispatchAsync(
            Request with { CallId = "past-persisted-unknown-budget" }, CancellationToken.None);
        Assert.Equal(read ? "equivalent_failure_limit" : "equivalent_write_outcome_unknown", stopped.ReasonCode);
        var additionalReadSends = replayFailure == "none" ? 1 : 2;
        Assert.Equal(sends + (read ? additionalReadSends : 0), fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreshReadRecoveryDoesNotInventWriteUncertainty()
    {
        using var fixture = new Fixture(ToolRiskClass.ReadOnly);
        await fixture.MarkExecutingAsync(Request);
        fixture.Verification = (_, _, _, _) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "unconfirmed"));
        var restarted = fixture.CreateRestartedDispatcher();
        Assert.Equal("Unknown", (await restarted.DispatchAsync(Request, CancellationToken.None)).Status);
        fixture.Execution = (_, _, _) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "known_failure"));
        Assert.Equal("Failed", (await restarted.DispatchAsync(
            Request with { CallId = "second-read" }, CancellationToken.None)).Status);
        var stopped = await restarted.DispatchAsync(Request with { CallId = "third-read" }, CancellationToken.None);
        Assert.Equal("equivalent_failure_limit", stopped.ReasonCode);
        Assert.Equal(2, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("invalid-json", false)]
    [InlineData("missing-risk", false)]
    [InlineData("null", false)]
    [InlineData("invalid-json", true)]
    [InlineData("missing-risk", true)]
    [InlineData("null", true)]
    [Trait("Category", "Unit")]
    public async Task UnreadablePersistedRiskSurfacesAndKeepsEquivalentCallsBlocked(string malformed, bool terminal)
    {
        using var fixture = new Fixture();
        var orphan = await fixture.MarkExecutingAsync(Request);
        if (terminal)
        {
            await fixture.Journal.UpdateStatusAsync(
                orphan.Id, "Unknown", orphan.Version, fixture.Clock.UtcNow, CancellationToken.None);
        }
        fixture.Journal.Corrupt(orphan.Id, preserveHash: true,
            canonicalEnvelope: malformed == "missing-risk" ? """{"FormatVersion":1}""" : malformed);
        var restarted = fixture.CreateRestartedDispatcher();
        if (malformed == "null")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.DispatchAsync(Request, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<JsonException>(async () =>
                await restarted.DispatchAsync(Request, CancellationToken.None));
        }
        var stopped = await restarted.DispatchAsync(
            Request with { CallId = "after-unreadable-risk" }, CancellationToken.None);
        Assert.Equal("Rejected", stopped.Status);
        Assert.Equal("equivalent_write_outcome_unknown", stopped.ReasonCode);
        Assert.Equal(1, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExpiredPendingApprovalConflictRemainsVisibleWhenNoDecisionWasRecorded()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.ApprovalStore.FailResolution = true;

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None));

        Assert.Equal("Pending", fixture.ApprovalStore.Entries.Single().Status);
        Assert.Equal("AwaitingApproval", fixture.Journal.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("approved")]
    [InlineData("rejected")]
    [Trait("Category", "Unit")]
    public async Task StalePendingApprovalReadUsesTheWinningDecisionWithoutSending(string winner)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.Journal.AfterRead = async (entry, token) =>
        {
            if (entry?.Status == "AwaitingApproval" && Interlocked.Increment(ref reads) == 2)
            {
                reading.SetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        };
        if (winner == "expired")
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        var stale = fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (winner == "expired")
        {
            Assert.Equal("Expired", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        }
        else
        {
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId!.Value, "owner", 1, winner == "approved", CancellationToken.None);
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        releaseRead.SetResult();
        var result = await stale.WaitAsync(TimeSpan.FromSeconds(2));
        var expectedStatus = winner == "rejected" ? "Rejected" : "Expired";
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedStatus, fixture.Journal.Entries.Single().Status);
        Assert.Equal(winner == "approved" ? "Approved" : expectedStatus, fixture.ApprovalStore.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    public async Task ConcurrentActionSettlementReturnsTheCommittedApprovalDecision(bool expired)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        if (expired)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSettlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ApprovalStore.AfterResolve = async (_, token) =>
        {
            decided.SetResult();
            await releaseSettlement.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        };
        var resolution = fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None).AsTask();
        await decided.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        releaseSettlement.SetResult();
        var settled = await resolution.WaitAsync(TimeSpan.FromSeconds(2));

        var expectedStatus = expired ? "Expired" : "Rejected";
        Assert.Equal(expectedStatus, settled.Status);
        Assert.Equal(expectedStatus, duplicate.Status);
        Assert.Equal(expectedStatus, fixture.Journal.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalSettlementAcceptsConcurrentMatchingActionOutcome()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        fixture.Journal.SettleThenConflictStatus = "Rejected";

        var result = await fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None);

        Assert.Equal("Rejected", result.Status);
        Assert.Equal("Rejected", fixture.Journal.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalSettlementRejectsConcurrentRemovalOfAction()
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        fixture.Journal.RemoveOnTerminalConflict = true;

        await Assert.ThrowsAsync<PersistenceConcurrencyException>(async () =>
            await fixture.Approvals.ResolveAsync(
                pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    public async Task ApprovalSettlementPreservesOtherConflictsAndPersistenceErrors(bool expired, bool persistenceError)
    {
        using var fixture = new Fixture(ToolRiskClass.ApprovalRequired);
        var pending = await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None);
        if (expired)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        fixture.ApprovalStore.AfterResolve = async (_, token) =>
        {
            if (persistenceError)
            {
                fixture.Journal.FailTerminalUpdate = true;
            }
            else
            {
                var action = fixture.Journal.Entries.Single();
                await fixture.Journal.UpdateStatusAsync(
                    action.Id, "Unknown", action.Version, fixture.Clock.UtcNow, token);
            }
        };
        var resolution = fixture.Approvals.ResolveAsync(
            pending.ApprovalId!.Value, "owner", 1, false, CancellationToken.None).AsTask();
        if (persistenceError)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolution);
        }
        else
        {
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() => resolution);
        }

        Assert.Equal(expired ? "Expired" : "Rejected", fixture.ApprovalStore.Entries.Single().Status);
        Assert.Equal(persistenceError ? "AwaitingApproval" : "Unknown", fixture.Journal.Entries.Single().Status);
        Assert.Equal(0, fixture.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecutionClaimConflictDoesNotMarkAnUnsentWriteUncertain()
    {
        using var fixture = new Fixture();
        fixture.Journal.FailNextExecutingUpdate = true;
        Assert.Equal("Prepared", (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).Status);
        Assert.Equal(0, fixture.ExecutionCount);

        var next = await fixture.Dispatcher.DispatchAsync(
            Request with { CallId = "after-claim-conflict" }, CancellationToken.None);
        Assert.Equal("Succeeded", next.Status);
        Assert.Equal(1, fixture.ExecutionCount);
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
        foreach (var invalidScope in new[] { "", " ", "bad\nscope", new string('x', 257) })
        {
            fixture.PrepareOverride = (_, _) => ToolPreparationResult.Ready(
                new ToolActionDraft("home.set_light", "turn_on", "light.porch", """{"on":true}""")
                {
                    ObservationScope = invalidScope
                });
            Assert.Equal("invalid_tool_action",
                (await fixture.Dispatcher.DispatchAsync(Request, CancellationToken.None)).ReasonCode);
        }
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

    [Theory]
    [InlineData(null, "current", false)]
    [InlineData("original", null, false)]
    [InlineData("original", "changed", false)]
    [InlineData("same", "same", true)]
    [InlineData(null, null, true)]
    [Trait("Category", "Unit")]
    public async Task RecoveryRequiresThePersistedObservationScopeWhileUnscopedToolsRemainCompatible(
        string? originalScope, string? currentScope, bool observe)
    {
        using var fixture = new Fixture();
        var scope = originalScope;
        fixture.PrepareOverride = (_, _) => ToolPreparationResult.Ready(
            new ToolActionDraft("home.set_light", "turn_on", "light.porch", """{"on":true}""", true)
            {
                ObservationScope = scope
            });
        var orphan = await fixture.MarkExecutingAsync(Request);
        scope = currentScope;
        fixture.Policy.Replace("changed-policy");
        var restarted = fixture.CreateRestartedDispatcher();
        var result = await restarted.DispatchAsync(Request, CancellationToken.None);
        Assert.Equal(observe ? "Succeeded" : "Unknown", result.Status);
        Assert.Equal(result, await restarted.DispatchAsync(Request, CancellationToken.None));
        Assert.Equal(result.Status, (await fixture.Journal.GetAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, fixture.ExecutionCount);
        Assert.Equal(observe ? 2 : 1, fixture.VerificationCount);
        if (!observe)
        {
            Assert.Equal("interrupted_execution_observation_not_permitted", result.ReasonCode);
            Assert.Equal("equivalent_write_outcome_unknown",
                (await restarted.DispatchAsync(Request with { CallId = "next" }, CancellationToken.None)).ReasonCode);
            Assert.Equal(1, fixture.ExecutionCount);
        }
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
        Assert.Equal(first, replay);
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
        Assert.Equal(first, replay);
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
        public void RevokePermissions() => current = new ToolAllowlistPolicy("revoked", []);
        public void RequireApproval() => current = Create("approval-policy", ToolRiskClass.ApprovalRequired);

        public ToolAuthorizationDecision Evaluate(
            string toolName,
            ToolActionDraft action,
            ToolRiskClass declaredRisk) =>
            AllowUnsupported
                ? new(true, "test_allow_unsupported", current.PolicyVersion, ToolRiskClass.Unsupported)
                : current.Evaluate(toolName, action, declaredRisk);

        private static ToolAllowlistPolicy Create(string version, ToolRiskClass minimumRisk = ToolRiskClass.ReadOnly) =>
            new(version,
            [
                new ToolPermission("home.set_light", "home.set_light", "turn_on", "light.porch", minimumRisk),
                new ToolPermission("home.set_light", "home.set_light", "turn_off", "light.porch", minimumRisk)
            ]);

        public bool AllowUnsupported { get; set; }
    }

    private sealed class MemoryJournal : IActionJournalStore
    {
        private readonly Dictionary<ActionId, ActionJournalEntry> entries = [];
        private readonly object gate = new();

        public bool FailNextExecutingUpdate { get; set; }
        public bool FailTerminalUpdate { get; set; }
        public string? SettleThenConflictStatus { get; set; }
        public bool RemoveOnTerminalConflict { get; set; }
        public bool FailCompletionUpdate { get; set; }
        public string? ReturnStatusAfterExecuting { get; set; }
        public string? ReturnStatusAfterAwaitingApproval { get; set; }
        public Action<string>? StatusUpdated { get; set; }
        public Func<CancellationToken, Task>? BeforeExecutingUpdate { get; set; }
        public Func<ActionJournalEntry?, CancellationToken, Task>? AfterRead { get; set; }

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

        public void Corrupt(ActionId id, bool preserveHash = false, string canonicalEnvelope = "invalid-envelope")
        {
            lock (gate)
            {
                entries[id] = entries[id] with
                {
                    CanonicalArguments = canonicalEnvelope,
                    RequestHash = preserveHash ? entries[id].RequestHash : "changed-hash"
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

        public async ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionJournalEntry? entry;
            lock (gate)
            {
                entries.TryGetValue(id, out entry);
            }

            if (AfterRead is { } afterRead)
            {
                await afterRead(entry, cancellationToken);
            }

            return entry;
        }

        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(
            string ownerId, int maximumActions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult<IReadOnlyList<ActionJournalEntry>>(entries.Values
                    .Where(entry => entry.CanonicalArguments.Contains(ownerId, StringComparison.Ordinal))
                    .OrderByDescending(entry => entry.UpdatedAtUtc).Take(maximumActions).ToArray());
            }
        }

        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadForTurnAsync(
            string ownerId, TurnId turnId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var turn = turnId.Value.ToString("D");
                return ValueTask.FromResult<IReadOnlyList<ActionJournalEntry>>(entries.Values
                    .Where(entry => entry.CanonicalArguments.Contains(ownerId, StringComparison.Ordinal)
                        && entry.CanonicalArguments.Contains(turn, StringComparison.Ordinal))
                    .ToArray());
            }
        }

        public async ValueTask<ActionJournalEntry> UpdateStatusAsync(
            ActionId id,
            string status,
            long expectedVersion,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken,
            ToolDispatchResult? outcome = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status == "Executing" && BeforeExecutingUpdate is { } beforeExecuting)
            {
                await beforeExecuting(cancellationToken);
            }

            lock (gate)
            {
                if (FailCompletionUpdate && status is "Succeeded" or "Failed")
                {
                    throw new InvalidOperationException("Controlled completion persistence failure.");
                }

                if (FailTerminalUpdate && status is "Expired" or "Rejected")
                {
                    throw new InvalidOperationException("Controlled terminal persistence failure.");
                }

                if (SettleThenConflictStatus == status)
                {
                    entries[id] = entries[id] with { Status = status, Version = expectedVersion + 1 };
                    SettleThenConflictStatus = null;
                    throw new PersistenceConcurrencyException("Concurrent matching settlement.");
                }

                if (RemoveOnTerminalConflict && status is "Expired" or "Rejected")
                {
                    entries.Remove(id);
                    RemoveOnTerminalConflict = false;
                    throw new PersistenceConcurrencyException("Concurrent action removal.");
                }

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
                var updated = entry with
                {
                    Status = returnedStatus!,
                    UpdatedAtUtc = updatedAtUtc,
                    Version = entry.Version + 1,
                    Outcome = outcome
                };
                entries[id] = updated;
                StatusUpdated?.Invoke(status);
                return updated;
            }
        }
    }

    private sealed class MemoryApprovalStore : IApprovalStore
    {
        private readonly Dictionary<ApprovalId, ApprovalStorageRecord> entries = [];
        private readonly object gate = new();

        public Func<ApprovalStorageRecord?, CancellationToken, Task>? AfterRead { get; set; }
        public Func<ApprovalStorageRecord, CancellationToken, Task>? AfterResolve { get; set; }
        public bool FailResolution { get; set; }

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

        public async ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApprovalStorageRecord? entry;
            lock (gate)
            {
                entries.TryGetValue(id, out entry);
            }

            if (AfterRead is { } afterRead)
            {
                await afterRead(entry, cancellationToken);
            }

            return entry;
        }

        public ValueTask<IReadOnlyList<ApprovalStorageRecord>> ReadRecentForOwnerAsync(
            string ownerId, int maximumRequests, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult<IReadOnlyList<ApprovalStorageRecord>>(entries.Values
                    .Where(entry => entry.OwnerId == ownerId)
                    .OrderByDescending(entry => entry.CreatedAtUtc).Take(maximumRequests).ToArray());
            }
        }

        public async ValueTask<ApprovalStorageRecord> ResolveAsync(
            ApprovalId id,
            string ownerId,
            string status,
            long expectedVersion,
            DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApprovalStorageRecord updated;
            lock (gate)
            {
                if (FailResolution
                    || !entries.TryGetValue(id, out var entry)
                    || entry.OwnerId != ownerId
                    || entry.Version != expectedVersion
                    || entry.Status != "Pending"
                    || resolvedAtUtc < entry.CreatedAtUtc
                    || (status != "Expired" && resolvedAtUtc >= entry.ExpiresAtUtc))
                {
                    throw new PersistenceConcurrencyException("Approval is stale, expired, or belongs to another owner.");
                }

                updated = entry with
                {
                    Status = status,
                    ResolvedAtUtc = resolvedAtUtc,
                    Version = entry.Version + 1
                };
                entries[id] = updated;
            }

            if (AfterResolve is { } afterResolve)
            {
                await afterResolve(updated, cancellationToken);
            }

            return updated;
        }
    }
}
