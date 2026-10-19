using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Tools;
using PersonalAgent.Application.Tools.Home;
using PersonalAgent.Domain;
using PersonalAgent.TestSupport.HomeAssistantFake;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class HomeToolTests
{
    private static readonly HomeEntityDefinition Lamp = new("light.lamp", "lamp", "Room", null, ["desk"], true);
    private static readonly HomeEntityDefinition Thermostat = new("climate.hall", "hall thermostat", "Hall", "C", ["heat"], true);

    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogSnapshotsSettingsResolvesAmbiguityAndBindsEveryLimit()
    {
        var aliases = new[] { "porch", "desk" };
        var original = Lamp with { Aliases = aliases };
        var catalog = new HomeEntityCatalog([original, Lamp with { EntityId = "light.other", FriendlyName = "other", Aliases = ["porch"] }, Thermostat], new(true, true));
        aliases[0] = "mutated";
        Assert.Equal(2, catalog.Resolve("porch").Count);
        Assert.Empty(catalog.Resolve("mutated"));
        Assert.Single(catalog.Resolve("LAMP"));
        Assert.Single(catalog.Resolve("light.lamp"));
        Assert.Empty(catalog.Resolve(null));
        catalog.Entities[0].Aliases[0] = "changed-copy";
        catalog.Require("light.lamp").Aliases[0] = "changed-copy";
        catalog.Resolve("lamp")[0].Aliases[0] = "changed-copy";
        Assert.Single(catalog.Resolve("desk"));
        Assert.Throws<HomeAssistantException>(() => catalog.Require("light.unknown"));
        foreach (var changed in new[]
        {
            original with { MaximumBrightness = 200 }, original with { MaximumAgeSeconds = 60 },
            original with { Aliases = ["new"] }, original with { FriendlyName = "new name" },
            original with { MinimumTemperature = 17 }
        })
        {
            Assert.NotEqual(new HomeEntityCatalog([original], new()).ConfigurationVersion,
                new HomeEntityCatalog([changed], new()).ConfigurationVersion);
        }
        Assert.NotEqual(new HomeEntityCatalog([Lamp], new()).ConfigurationVersion,
            new HomeEntityCatalog([Lamp], new(AutomaticLightsEnabled: true)).ConfigurationVersion);
        Assert.Contains(catalog.Permissions(), p => p.ToolName == "home.set_temperature" && p.MinimumRisk == ToolRiskClass.ApprovalRequired);
        Assert.DoesNotContain(new HomeEntityCatalog([Thermostat], new()).Permissions(), p => p.ToolName == "home.set_temperature");
        Assert.DoesNotContain(new HomeEntityCatalog([Lamp with { AllowWrite = false }], new()).Permissions(), p => p.ToolName == "home.set_light");
    }

    [Theory]
    [InlineData("lock.front")]
    [InlineData("alarm_control_panel.house")]
    [InlineData("cover.garage")]
    [InlineData("switch.appliance")]
    [InlineData("light.*")]
    [InlineData("light.lamp/../../lock.front")]
    [InlineData("light.Lamp")]
    [InlineData("light.lamp\n")]
    [InlineData("light.lamp\r\n")]
    [Trait("Category", "Unit")]
    public void UnsupportedIdsAndPatternsAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([Lamp with { EntityId = id }], new()));

    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogRejectsInvalidMetadataBoundsCollisionsAndBudgets()
    {
        Assert.Throws<ArgumentNullException>(() => new HomeEntityCatalog(null!, new()));
        Assert.Throws<ArgumentNullException>(() => new HomeEntityCatalog([Lamp], null!));
        Assert.Throws<ArgumentNullException>(() => new HomeEntityCatalog([null!], new()));
        foreach (var invalid in new[]
        {
            Lamp with { EntityId = null! }, Lamp with { FriendlyName = "" }, Lamp with { FriendlyName = " lamp" },
            Lamp with { FriendlyName = "a\nb" }, Lamp with { FriendlyName = new string('a', 129) },
            Lamp with { Area = null! }, Lamp with { Aliases = null! }, Lamp with { Aliases = [""] },
            Lamp with { Aliases = Enumerable.Repeat("a", 21).ToArray() }, Lamp with { Unit = new string('a', 33) },
            Lamp with { MaximumAgeSeconds = 0 }, Lamp with { MaximumAgeSeconds = 86401 },
            Lamp with { MinimumBrightness = 0 }, Lamp with { MinimumBrightness = 256 },
            Lamp with { MaximumBrightness = 0 }, Lamp with { MaximumBrightness = 256 },
            Lamp with { MinimumTemperature = 30 }, Lamp with { MinimumTemperature = -101 },
            Lamp with { MaximumTemperature = 201 }, Thermostat with { Unit = null },
            Lamp with { EntityId = "sensor.write" }
        })
        {
            Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([invalid], new()));
        }
        Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([], new()));
        Assert.Throws<ArgumentException>(() => new HomeEntityCatalog(Enumerable.Range(0, 101).Select(i => Lamp with { EntityId = $"light.lamp{i}" }), new()));
        Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([Lamp, Lamp], new()));
        Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([Lamp, Thermostat with { Aliases = ["light.lamp"] }], new()));
        foreach (var options in new[]
        {
            new HomeToolOptions(RequestTimeoutSeconds: 0), new(RequestTimeoutSeconds: 61),
            new(VerificationSeconds: 0), new(VerificationSeconds: 61),
            new(PollMilliseconds: 9), new(PollMilliseconds: 1001)
        })
        {
            Assert.Throws<ArgumentException>(() => new HomeEntityCatalog([Lamp], options));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ExactOperationBoundsCannotBeBypassed()
    {
        var catalog = new HomeEntityCatalog([Lamp with { MinimumBrightness = 10, MaximumBrightness = 100 }, Thermostat], new(true));
        Assert.True(catalog.AllowsLight("light.lamp", false, null));
        Assert.True(catalog.AllowsLight("light.lamp", true, 10));
        Assert.True(catalog.AllowsLight("light.lamp", true, 100));
        Assert.False(catalog.AllowsLight("light.lamp", true, 9));
        Assert.False(catalog.AllowsLight("light.lamp", true, 101));
        Assert.False(catalog.AllowsLight("light.lamp", false, 10));
        Assert.False(catalog.AllowsLight("climate.hall", true, null));
        Assert.False(catalog.AllowsLight("light.missing", true, null));
        Assert.False(new HomeEntityCatalog([Lamp with { AllowWrite = false }], new()).AllowsLight("light.lamp", true, null));
        Assert.True(catalog.AllowsTemperature("climate.hall", 16));
        Assert.True(catalog.AllowsTemperature("climate.hall", 28));
        Assert.False(catalog.AllowsTemperature("climate.hall", 15));
        Assert.False(catalog.AllowsTemperature("climate.hall", 29));
        Assert.False(catalog.AllowsTemperature("light.lamp", 20));
        Assert.False(catalog.AllowsTemperature("climate.missing", 20));
        Assert.False(new HomeEntityCatalog([Thermostat], new()).AllowsTemperature("climate.hall", 20));
        Assert.False(new HomeEntityCatalog([Thermostat with { AllowWrite = false }], new(true)).AllowsTemperature("climate.hall", 20));
    }

    [Theory]
    [InlineData("turn on lamp", true, null, true)]
    [InlineData("TURN ON DESK", true, null, true)]
    [InlineData("turn off light.lamp", false, null, true)]
    [InlineData("turn on lamp at brightness 30", true, 30, true)]
    [InlineData(null, true, null, false)]
    [InlineData("do not turn on lamp", true, null, false)]
    [InlineData("turn off lamp", true, null, false)]
    [InlineData("turn on lamp then thermostat", true, null, false)]
    [InlineData("turn on lamp at brightness 30", true, 31, false)]
    [InlineData("turn on lamp at brightness 30", true, null, false)]
    [InlineData("turn on lamp", true, 30, false)]
    [InlineData("turn on other", true, null, false)]
    [InlineData("turn on lamp ", true, null, false)]
    [InlineData("turn on lamp at brightness 256", true, 256, false)]
    [Trait("Category", "Unit")]
    public void ExplicitIntentRequiresTheWholeCurrentRequestAndEveryParameter(string? text, bool on, int? brightness, bool expected)
    {
        var catalog = new HomeEntityCatalog([Lamp], new(AutomaticLightsEnabled: true));
        Assert.Equal(expected, HomeLightIntent.Matches(catalog, text, "light.lamp", on, brightness));
        Assert.False(HomeLightIntent.Matches(new([Lamp], new()), text, "light.lamp", on, brightness));
        Assert.False(HomeLightIntent.Matches(catalog, text, "light.other", on, brightness));
        var ambiguous = new HomeEntityCatalog([Lamp, Lamp with { EntityId = "light.other" }], new(AutomaticLightsEnabled: true));
        Assert.False(HomeLightIntent.Matches(ambiguous, "turn on lamp", "light.lamp", true, null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RegistrationsExecuteSafeMetadataReadAndExplicitLightsWithExactApprovals()
    {
        var clock = new Clock();
        var fake = new FakeHomeAssistantClient(clock);
        var catalog = new HomeEntityCatalog(
        [
            new("light.simulator_lamp", "lamp", "Study", null, ["desk"], true),
            new("climate.simulator", "thermostat", "Hall", "C", [], true),
            new("sensor.temperature", "sensor", "Hall", "C", [])
        ], new(true, true));
        var journal = new Journal();
        var approvals = new ApprovalService(new ApprovalStore(), journal, clock);
        var registry = new ToolRegistry(HomeToolRegistrations.Create(catalog, fake, catalog.ConfigurationVersion));
        var dispatcher = new ToolDispatcher(registry, new ToolAllowlistPolicy(catalog.ConfigurationVersion, catalog.Permissions()), journal, approvals, clock, TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentNullException>(() => HomeToolRegistrations.Create(null!, fake, catalog.ConfigurationVersion));
        Assert.Throws<ArgumentNullException>(() => HomeToolRegistrations.Create(catalog, null!, catalog.ConfigurationVersion));
        Assert.Throws<ArgumentException>(() => HomeToolRegistrations.Create(catalog, fake, ""));
        Assert.DoesNotContain(HomeToolRegistrations.Create(new([Lamp], new()), fake, catalog.ConfigurationVersion), r => r.Definition.Name == "home.set_temperature");
        async Task<ToolDispatchResult> Dispatch(string tool, string args, string? text = null) =>
            await dispatcher.DispatchAsync(new(new TurnId(Guid.NewGuid()), Guid.NewGuid().ToString(), tool, args, "owner", text), default);
        var list = await Dispatch("home.list_entities", "{}");
        Assert.Equal("Succeeded", list.Status);
        Assert.Contains("friendlyName", list.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("lock.forbidden", list.ResultJson!, StringComparison.Ordinal);
        var state = await Dispatch("home.get_state", """{"entity":"sensor"}""");
        Assert.Equal("Succeeded", state.Status);
        Assert.Contains("observedAtUtc", state.ResultJson!, StringComparison.Ordinal);
        Assert.Equal("Succeeded", (await Dispatch("home.set_light", """{"entity":"desk","on":true,"brightness":30}""", "turn on lamp at brightness 30")).Status);
        Assert.Equal("Succeeded", (await Dispatch("home.set_light", """{"entity":"lamp","on":false}""", "turn off lamp")).Status);
        Assert.Equal("AwaitingApproval", (await Dispatch("home.set_light", """{"entity":"lamp","on":true}""", "please turn on lamp")).Status);
        Assert.Equal("AwaitingApproval", (await Dispatch("home.set_temperature", """{"entity":"thermostat","temperature":22}""")).Status);
        Assert.Equal("Rejected", (await Dispatch("home.set_temperature", """{"entity":"thermostat","temperature":100}""")).Status);
        Assert.Equal("Rejected", (await Dispatch("home.get_state", """{"entity":null}""")).Status);
        Assert.Equal("Rejected", (await Dispatch("home.set_light", """{"entity":"lamp"}""")).Status);
        Assert.Equal("Rejected", (await Dispatch("home.list_entities", """{"url":"https://bad"}""")).Status);
        var request = new ToolDispatchRequest(new(Guid.NewGuid()), "thermostat", "home.set_temperature", """{"entity":"thermostat","temperature":22}""", "owner");
        var pending = await dispatcher.DispatchAsync(request, default);
        var approval = await approvals.GetAsync(pending.ApprovalId!.Value, default);
        await approvals.ResolveAsync(approval!.Id, "owner", approval.ExpectedVersion, true, default);
        Assert.Equal("Succeeded", (await dispatcher.DispatchAsync(request, default)).Status);
        fake.SetState(new("sensor.temperature", "unavailable", clock.UtcNow, false));
        Assert.Contains("\"available\":false", (await Dispatch("home.get_state", """{"entity":"sensor"}""")).ResultJson!);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Journal : IActionJournalStore
    {
        private readonly Dictionary<ActionId, ActionJournalEntry> entries = [];
        public ValueTask<ActionJournalEntry> CreateAsync(ActionJournalEntry entry, CancellationToken token)
        {
            entries.TryAdd(entry.Id, entry);
            return ValueTask.FromResult(entries[entry.Id]);
        }
        public ValueTask<ActionJournalEntry?> GetAsync(ActionId id, CancellationToken token) => ValueTask.FromResult(entries.GetValueOrDefault(id));
        public ValueTask<IReadOnlyList<ActionJournalEntry>> ReadRecentForOwnerAsync(string ownerId, int maximumActions, CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<ActionJournalEntry>>(entries.Values
                .Where(entry => entry.CanonicalArguments.Contains(ownerId, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.UpdatedAtUtc).Take(maximumActions).ToArray());
        public ValueTask<ActionJournalEntry> UpdateStatusAsync(ActionId id, string status, long expectedVersion,
            DateTimeOffset time, CancellationToken token, ToolDispatchResult? outcome = null)
        {
            var entry = entries[id];
            Assert.Equal(expectedVersion, entry.Version);
            return ValueTask.FromResult(entries[id] = entry with { Status = status, Version = entry.Version + 1, UpdatedAtUtc = time, Outcome = outcome });
        }
    }

    private sealed class ApprovalStore : IApprovalStore
    {
        private readonly Dictionary<ApprovalId, ApprovalStorageRecord> entries = [];
        public ValueTask<ApprovalStorageRecord> CreateAsync(ApprovalStorageRecord entry, CancellationToken token)
        {
            entries.Add(entry.Id, entry);
            return ValueTask.FromResult(entry);
        }
        public ValueTask<ApprovalStorageRecord?> GetAsync(ApprovalId id, CancellationToken token) => ValueTask.FromResult(entries.GetValueOrDefault(id));
        public ValueTask<IReadOnlyList<ApprovalStorageRecord>> ReadRecentForOwnerAsync(string ownerId, int maximumRequests, CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<ApprovalStorageRecord>>(entries.Values
                .Where(entry => entry.OwnerId == ownerId)
                .OrderByDescending(entry => entry.CreatedAtUtc).Take(maximumRequests).ToArray());
        public ValueTask<ApprovalStorageRecord> ResolveAsync(ApprovalId id, string ownerId, string status, long expectedVersion,
            DateTimeOffset time, CancellationToken token)
        {
            var entry = entries[id];
            Assert.Equal(expectedVersion, entry.Version);
            Assert.Equal(ownerId, entry.OwnerId);
            return ValueTask.FromResult(entries[id] = entry with { Status = status, Version = entry.Version + 1, ResolvedAtUtc = time });
        }
    }
}
