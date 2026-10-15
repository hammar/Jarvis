using System.Net.Http.Json;
using System.Text.Json;
using PersonalAgent.Application;
using PersonalAgent.Application.Approvals;
using PersonalAgent.Application.Tools;
using PersonalAgent.Application.Tools.Home;
using PersonalAgent.Domain;
using PersonalAgent.Infrastructure.HomeAssistant;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class HomeAssistantToolTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task AllFourToolsDispatchAgainstManagedHttpWithExactThermostatApproval()
    {
        using var file = IsolatedDatabaseFile.Create();
        var database = new SqliteDatabase(file.Path);
        await database.InitializeAsync();
        var clock = new Clock();
        var catalog = HomeAssistantConfiguration.SimulatorCatalog(new(true, true));
        var client = new HomeAssistantRestClient(fixture.HomeAssistantClient,
            fixture.HomeAssistantClient.BaseAddress!, new("synthetic-reference"), new Secret(), catalog, clock);
        var journal = new SqliteActionJournalStore(database);
        var approvals = new ApprovalService(new SqliteApprovalStore(database), journal, clock);
        var dispatcher = new ToolDispatcher(
            new ToolRegistry(HomeToolRegistrations.Create(catalog, client)),
            new ToolAllowlistPolicy(catalog.ConfigurationVersion, catalog.Permissions()),
            journal, approvals, clock, TimeSpan.FromSeconds(5));
        ToolDispatchRequest Request(string tool, string arguments, string? text = null) =>
            new(new TurnId(Guid.NewGuid()), Guid.NewGuid().ToString(), tool, arguments, "owner", text);
        using var initialCounts = JsonDocument.Parse(await fixture.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
        var writesBefore = initialCounts.RootElement.GetProperty("writes").GetInt32();
        var list = await dispatcher.DispatchAsync(Request("home.list_entities", "{}"), default);
        Assert.Equal("Succeeded", list.Status);
        Assert.Contains("set_temperature", list.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("lock.forbidden", list.ResultJson!, StringComparison.Ordinal);
        var read = await dispatcher.DispatchAsync(Request("home.get_state", """{"entity":"temperature"}"""), default);
        Assert.Equal("Succeeded", read.Status);
        Assert.Contains("observedAtUtc", read.ResultJson!, StringComparison.Ordinal);
        var stale = await dispatcher.DispatchAsync(Request("home.get_state", """{"entity":"stale"}"""), default);
        Assert.Contains("\"stale\":true", stale.ResultJson!, StringComparison.Ordinal);
        var ambiguous = await dispatcher.DispatchAsync(Request("home.set_light", """{"entity":"porch","on":true}""", "turn on porch"), default);
        Assert.Equal("ClarificationRequired", ambiguous.Status);
        var lightRequest = Request("home.set_light", """{"entity":"lamp","on":true,"brightness":64}""", "turn on lamp at brightness 64");
        var light = await dispatcher.DispatchAsync(lightRequest, default);
        Assert.Equal("Succeeded", light.Status);
        Assert.Equal(light, await dispatcher.DispatchAsync(lightRequest, default));
        var thermostatRequest = Request("home.set_temperature", """{"entity":"thermostat","temperature":21}""");
        var pending = await dispatcher.DispatchAsync(thermostatRequest, default);
        Assert.Equal("AwaitingApproval", pending.Status);
        var approval = await approvals.GetAsync(pending.ApprovalId!.Value, default);
        await approvals.ResolveAsync(approval!.Id, "owner", approval.ExpectedVersion, true, default);
        var thermostat = await dispatcher.DispatchAsync(thermostatRequest, default);
        Assert.Equal("Succeeded", thermostat.Status);
        Assert.Equal(thermostat, await dispatcher.DispatchAsync(thermostatRequest, default));
        Assert.Equal(21, (await client.GetStateAsync("climate.simulator", default)).TargetTemperature);
        using var counts = JsonDocument.Parse(await fixture.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
        Assert.Equal(writesBefore + 2, counts.RootElement.GetProperty("writes").GetInt32());
        using var failure = await fixture.HomeAssistantClient.PostAsJsonAsync("/fixture/control/configure",
            new { ReadFailures = new[] { 503 }, ApplyWrites = true, WriteStatus = 200 });
        failure.EnsureSuccessStatusCode();
        var readsBefore = counts.RootElement.GetProperty("reads").GetInt32();
        await client.GetStateAsync("sensor.temperature", default);
        using var retried = JsonDocument.Parse(await fixture.HomeAssistantClient.GetStringAsync("/fixture/control/counts"));
        Assert.Equal(readsBefore + 2, retried.RootElement.GetProperty("reads").GetInt32());
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Secret : ISecretResolver
    {
        public ValueTask<ReadOnlyMemory<char>> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("synthetic-home-token".AsMemory());
        }
    }
}
