using PersonalAgent.Application;
using PersonalAgent.Domain;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class ApplicationContractTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void AgentTurnContractCarriesHostSelectedProviderContextToolsAndLimits()
    {
        var turnId = TurnId.New();
        var context = new ContextPacket(
            "packet-1",
            "policy-1",
            [new ContextItem("conversation:1", "approved text", "LocalOnly", "user")],
            new ContextEstimate(0, 0, 0, 0, 0, 0, 0, 0, 8192, 0, false, false, "fixture estimate"));
        var tool = new AgentToolDefinition("read_state", """{"type":"object"}""", true);
        var request = new AgentTurnRequest(
            turnId,
            ProviderKind.Cloud,
            "Summarize the selected context.",
            context,
            [tool],
            TimeSpan.FromSeconds(30),
            2);
        var result = new AgentTurnResult(TurnStatus.Interrupted, null, "engine_timeout");

        Assert.Equal(turnId, request.TurnId);
        Assert.Equal(ProviderKind.Cloud, request.Provider);
        Assert.Equal("approved text", request.Context.Items[0].Text);
        Assert.Equal("read_state", request.Tools[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Deadline);
        Assert.Equal(2, request.MaximumToolCalls);
        Assert.Equal(TurnStatus.Interrupted, result.Status);
        Assert.Equal("engine_timeout", result.FailureCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoutingDispatchAndApprovalContractsKeepHostAuthorityExplicit()
    {
        var turnId = TurnId.New();
        var actionId = ActionId.New();
        var approvalId = ApprovalId.New();
        var expiresAtUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var routingRequest = new RoutingRequest(RouteMode.AskBeforeCloud, "private question", false);
        var route = RouteDecision.Local("local_default", "policy-1");
        var proposal = new ToolDispatchRequest(turnId, "call-1", "read_state", """{"entity":"sensor.temp"}""");
        var outcome = new ToolDispatchResult("Unknown", null, "physical_outcome_uncertain");
        var approval = new ApprovalRequest(approvalId, actionId, "owner", expiresAtUtc, 7);

        Assert.Equal(RouteMode.AskBeforeCloud, routingRequest.RequestedMode);
        Assert.Equal(ProviderKind.Local, route.Provider);
        Assert.Equal("call-1", proposal.CallId);
        Assert.Equal(turnId, proposal.TurnId);
        Assert.Equal("Unknown", outcome.Status);
        Assert.Equal(approvalId, approval.Id);
        Assert.Equal(actionId, approval.ActionId);
        Assert.Equal(expiresAtUtc, approval.ExpiresAtUtc);
        Assert.Equal(7, approval.ExpectedVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DurableStateAndAdapterContractsPreserveProvenanceVersionsAndUtc()
    {
        var nowUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var conversationId = ConversationId.New();
        var fact = new MemoryFact(
            MemoryFactId.New(),
            "owner",
            "preferred_name",
            "Alex",
            "conversation:1",
            "Personal",
            3);
        var message = new ConversationMessage(Guid.NewGuid(), conversationId, "user", "Hello", nowUtc);
        var lease = new JobLease(JobId.New(), "worker-1", nowUtc.AddMinutes(1), 2);
        var entity = new HomeAssistantEntity("sensor.temperature", "21.5", nowUtc, true);
        var secretReference = new SecretReference("home-assistant-token");

        Assert.Equal("conversation:1", fact.SourceId);
        Assert.Equal(3, fact.Version);
        Assert.Equal(conversationId, message.ConversationId);
        Assert.Equal(nowUtc, message.CreatedAtUtc);
        Assert.Equal("worker-1", lease.LeaseOwner);
        Assert.Equal(nowUtc.AddMinutes(1), lease.LeaseExpiresAtUtc);
        Assert.Equal(nowUtc, entity.ObservedAtUtc);
        Assert.True(entity.Available);
        Assert.Equal("home-assistant-token", secretReference.Name);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TypedTurnEventsPreserveApplicationIdentityAndObservedUtcTime()
    {
        var turnId = TurnId.New();
        var occurredAtUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        AgentEvent[] events =
        [
            new TurnStarted(turnId, occurredAtUtc),
            new RouteSelected(turnId, occurredAtUtc, ProviderKind.Local, "local_default"),
            new ContextReviewRequired(turnId, occurredAtUtc, "packet-1"),
            new TextDelta(turnId, occurredAtUtc, "partial"),
            new ToolProposed(turnId, occurredAtUtc, "call-1", "read_state"),
            new ApprovalRequired(turnId, occurredAtUtc, ApprovalId.New()),
            new ToolCompleted(turnId, occurredAtUtc, "call-1", "Unknown"),
            new TurnCompleted(turnId, occurredAtUtc),
            new TurnFailed(turnId, occurredAtUtc, "provider_failure"),
            new TurnCancelled(turnId, occurredAtUtc),
            new TurnInterrupted(turnId, occurredAtUtc, "process_stopped")
        ];

        Assert.Equal(11, events.Length);
        Assert.All(events, item =>
        {
            Assert.Equal(turnId, item.TurnId);
            Assert.Equal(occurredAtUtc, item.OccurredAtUtc);
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PersistenceContractsKeepJournalApprovalAuditAndEventIdentityExplicit()
    {
        var nowUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var conversationId = ConversationId.New();
        var turnId = TurnId.New();
        var actionId = ActionId.New();
        var approvalId = ApprovalId.New();
        var turn = new ConversationTurn(turnId, conversationId, TurnStatus.Running, nowUtc, nowUtc, 2);
        var turnEvent = new PersistedTurnEvent(Guid.NewGuid(), turnId, 4, "tool.completed", """{"outcome":"Unknown"}""", nowUtc);
        var action = new ActionJournalEntry(
            actionId, "home.set_light", """{"entity":"light.test"}""", "hash", "Unknown", nowUtc, nowUtc, 3);
        var approval = new ApprovalStorageRecord(
            approvalId, actionId, "owner", "Approved", nowUtc, nowUtc.AddMinutes(5), nowUtc.AddMinutes(1), 2);
        var audit = new AuditEventRecord(Guid.NewGuid(), "action.unknown", actionId.Value.ToString("D"), "{}", nowUtc);
        var concurrencyError = new PersistenceConcurrencyException("The record changed.");

        Assert.Equal(turnId, turn.Id);
        Assert.Equal(conversationId, turn.ConversationId);
        Assert.Equal(TurnStatus.Running, turn.Status);
        Assert.Equal(2, turn.Version);
        Assert.Equal(4, turnEvent.Sequence);
        Assert.Equal("tool.completed", turnEvent.EventType);
        Assert.Equal("Unknown", action.Status);
        Assert.Equal(3, action.Version);
        Assert.Equal(approvalId, approval.Id);
        Assert.Equal(actionId, approval.ActionId);
        Assert.Equal(nowUtc.AddMinutes(5), approval.ExpiresAtUtc);
        Assert.Equal("action.unknown", audit.EventType);
        Assert.Equal(nowUtc, audit.OccurredAtUtc);
        Assert.Equal("The record changed.", concurrencyError.Message);
    }
}
