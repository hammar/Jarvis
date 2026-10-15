using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Tools;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class ToolAuthorizationPolicyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ExactPermissionRaisesRiskAndPolicyVersionChangesWithConfiguration()
    {
        var permission = new ToolPermission(
            "home.set_light",
            "home.set_light",
            "turn_on",
            "light.porch",
            ToolRiskClass.ApprovalRequired);
        var policy = new ToolAllowlistPolicy("settings-v1", [permission]);
        var sameRulesInNewOrder = new ToolAllowlistPolicy("settings-v1", [permission]);

        var allowed = policy.Evaluate(
            "home.set_light",
            new ToolActionDraft("home.set_light", "turn_on", "light.porch", """{"on":true}""", true),
            ToolRiskClass.LowRiskWrite);
        var wrongEntity = policy.Evaluate(
            "home.set_light",
            new ToolActionDraft("home.set_light", "turn_on", "light.denied", """{"on":true}""", true),
            ToolRiskClass.LowRiskWrite);
        var unsupported = policy.Evaluate(
            "home.set_light",
            new ToolActionDraft("home.set_light", "turn_on", "light.porch", """{"on":true}""", true),
            ToolRiskClass.Unsupported);
        var changedPolicy = new ToolAllowlistPolicy(
            "settings-v1",
            [permission with { EntityId = "light.denied" }]);

        Assert.True(allowed.Allowed);
        Assert.Equal(ToolRiskClass.ApprovalRequired, allowed.EffectiveRisk);
        Assert.Equal(policy.PolicyVersion, allowed.PolicyVersion);
        Assert.Equal(policy.PolicyVersion, sameRulesInNewOrder.PolicyVersion);
        Assert.NotEqual(policy.PolicyVersion, changedPolicy.PolicyVersion);
        Assert.False(wrongEntity.Allowed);
        Assert.False(unsupported.Allowed);
        Assert.Equal(ToolRiskClass.Unsupported, unsupported.EffectiveRisk);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            policy.Evaluate("home.set_light", new ToolActionDraft("home.set_light", "turn_on", "light.porch", "{}"), (ToolRiskClass)99));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AllowlistRejectsInvalidAndDuplicateExactRules()
    {
        var valid = new ToolPermission("home.get_state", "home.get_state", "read", "sensor.test", ToolRiskClass.ReadOnly);

        Assert.Throws<ArgumentException>(() => new ToolAllowlistPolicy("v1", [valid, valid]));
        Assert.Throws<ArgumentException>(() => new ToolAllowlistPolicy("v1", [
            valid with { Operation = " " }]));
        Assert.Throws<ArgumentException>(() =>
            new ToolAllowlistPolicy("v1", [valid with { MinimumRisk = (ToolRiskClass)99 }]));
        Assert.Throws<ArgumentException>(() => new ToolAllowlistPolicy(" ", []));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AllowlistRejectsEveryOverlongOrEmptyExactPermissionComponent()
    {
        var valid = new ToolPermission("home.get_state", "home.get_state", "read", "sensor.test", ToolRiskClass.ReadOnly);
        ToolPermission[] invalidPermissions =
        [
            valid with { ToolName = new string('t', 65) },
            valid with { ActionType = new string('a', 97) },
            valid with { Operation = new string('o', 65) },
            valid with { EntityId = "" },
            valid with { EntityId = new string('e', 257) }
        ];

        foreach (var permission in invalidPermissions)
        {
            Assert.Throws<ArgumentException>(() => new ToolAllowlistPolicy("v1", [permission]));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToolRegistrationAndRegistryRejectInvalidNamesSchemasAndDuplicates()
    {
        static ToolRegistration<string> Create(
            string name,
            string schema = """{"type":"object"}""",
            ToolRiskClass riskClass = ToolRiskClass.ReadOnly) =>
            new(
                name,
                schema,
                riskClass,
                (_, _, _) => ValueTask.FromResult(ToolPreparationResult.Reject("not_used")),
                (_, _, _) => ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "not_used")),
                (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "not_used")));

        var registration = Create("home.get_state");
        Assert.Throws<ArgumentException>(() => Create("Home.GetState"));
        Assert.Throws<ArgumentException>(() => Create("1invalid"));
        Assert.Throws<ArgumentException>(() => Create("home.get_state", """[]"""));
        Assert.ThrowsAny<JsonException>(() => Create("home.get_state", """{"type":"""));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("home.get_state", riskClass: (ToolRiskClass)99));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([registration, registration]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new ExternalRegistration()]));

        var registry = new ToolRegistry([registration]);
        Assert.True(registry.TryGet("home.get_state", out var found));
        Assert.Same(registration, found);
        var empty = new ToolRegistry([]);
        Assert.Empty(empty.Definitions);
        Assert.False(empty.TryGet("home.get_state", out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToolRegistrationEnforcesCallbackAndPreparationContracts()
    {
        static ValueTask<ToolPreparationResult> Prepare(
            string _,
            ToolDispatchRequest __,
            CancellationToken ___) =>
            ValueTask.FromResult(ToolPreparationResult.Reject("invalid"));
        static ValueTask<ToolExecutionOutcome> Execute(
            string _,
            ToolActionDraft __,
            CancellationToken ___) =>
            ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionStatus.Failed, null, "invalid"));
        static ValueTask<ToolVerificationOutcome> Verify(
            string _,
            ToolActionDraft __,
            ToolExecutionOutcome ___,
            CancellationToken ____) =>
            ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Unknown, "invalid"));

        Assert.Throws<ArgumentNullException>(() => new ToolRegistration<string>(
            "home.get_state", """{"type":"object"}""", ToolRiskClass.ReadOnly, null!, Execute, Verify));
        Assert.Throws<ArgumentNullException>(() => new ToolRegistration<string>(
            "home.get_state", """{"type":"object"}""", ToolRiskClass.ReadOnly, Prepare, null!, Verify));
        Assert.Throws<ArgumentNullException>(() => new ToolRegistration<string>(
            "home.get_state", """{"type":"object"}""", ToolRiskClass.ReadOnly, Prepare, Execute, null!));
        Assert.Throws<ArgumentNullException>(() => ToolPreparationResult.Ready(null!));
        Assert.Throws<ArgumentException>(() => ToolPreparationResult.Clarify(" "));
        Assert.Throws<ArgumentException>(() => ToolPreparationResult.Reject(new string('x', 97)));
    }

    private sealed class ExternalRegistration : IToolRegistration
    {
        public AgentToolDefinition Definition { get; } = new("external", """{"type":"object"}""", true);
        public ToolRiskClass RiskClass => ToolRiskClass.ReadOnly;
    }
}
