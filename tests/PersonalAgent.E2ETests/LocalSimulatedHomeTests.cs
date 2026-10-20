using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Identity;
using PersonalAgent.Infrastructure.Persistence;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class LocalSimulatedHomeTests(SimulatorHostFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("Simulator", "", "only permitted with the Local profile")]
    [InlineData("Hybrid", "", "only permitted with the Local profile")]
    [InlineData("E2E", "", "only permitted with the Local profile")]
    [InlineData("Local", "/must-not-open-live-data", "not JARVIS_DATA_DIR")]
    [Trait("Category", "AspireE2E")]
    public async Task DemoRejectsIncompatibleProfilesAndLiveDataReuse(
        string profile, string liveData, string expectedError)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.PersonalAgent_AppHost>(
                [
                    $"--JARVIS_PROFILE={profile}",
                    "--JARVIS_LOCAL_SIMULATED_HOME=true",
                    $"--JARVIS_DATA_DIR={liveData}"
                ]).WaitAsync(Timeout);
            await using var application = await builder.BuildAsync().WaitAsync(Timeout);
            await application.StartAsync().WaitAsync(Timeout);
        });
        Assert.Contains(expectedError, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task LocalDemoUsesExternalInferenceAndManagedHomeWithExactApprovals()
    {
        using var data = IsolatedDirectory.Create();
        var databasePath = Path.Combine(data.Path, "jarvis.db");
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();
        const string passphrase = "isolated-demo-test-passphrase";
        var passwordHash = new PasswordHasher<string>().HashPassword("owner", passphrase);
        Assert.True(await new SqliteOwnerStateStore(database).TryCreateOwnerAsync(
            passwordHash, DateTimeOffset.UtcNow, CancellationToken.None));

        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.PersonalAgent_AppHost>(
            [
                "--JARVIS_PROFILE=Local",
                "--JARVIS_LOCAL_SIMULATED_HOME=true",
                "--JARVIS_DATA_DIR=",
                $"--JARVIS_LOCAL_DEMO_DATA_DIR={data.Path}",
                $"--JARVIS_OLLAMA_BASE_URL={fixture.ModelClient.BaseAddress}",
                "--JARVIS_OLLAMA_MODEL=simulator-model",
                "--JARVIS_OLLAMA_SECRET_REFERENCE=",
                "--JARVIS_CLOUD_ENABLED=true",
                "--JARVIS_HOME_ASSISTANT_BASE_URL=https://must-not-contact.invalid",
                "--JARVIS_HOME_ASSISTANT_SECRET_REFERENCE=must-not-resolve",
                "--JARVIS_HOME_ENTITY_CATALOG=invalid-live-catalog",
                "--JARVIS_HOME_AUTOMATIC_LIGHTS_ENABLED=true",
                "--JARVIS_HOME_THERMOSTAT_ENABLED=true"
            ]).WaitAsync(Timeout);
        await using var application = await builder.BuildAsync().WaitAsync(Timeout);
        Assert.DoesNotContain(builder.Resources, resource => resource.Name == "simulator-model");
        await application.StartAsync().WaitAsync(Timeout);
        await application.ResourceNotifications.WaitForResourceHealthyAsync("personalagent-web").WaitAsync(Timeout);
        await application.ResourceNotifications.WaitForResourceHealthyAsync("simulator-home-assistant").WaitAsync(Timeout);

        using var discovered = application.CreateHttpClient("personalagent-web");
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = discovered.BaseAddress,
            Timeout = Timeout
        };
        await RefreshCsrfAsync(client);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { passphrase, rememberMe = false });
        login.EnsureSuccessStatusCode();
        await RefreshCsrfAsync(client);

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
        Assert.Equal("Ollama", status.RootElement.GetProperty("provider").GetString());
        using var household = JsonDocument.Parse(await client.GetStringAsync("/api/household"));
        var entities = household.RootElement.GetProperty("entities").EnumerateArray().ToArray();
        Assert.Equal(2, entities.Length);
        Assert.Contains(entities, entity => entity.GetProperty("entityId").GetString() == "light.simulator_lamp");
        Assert.Contains(entities, entity => entity.GetProperty("entityId").GetString() == "sensor.temperature");

        using var home = application.CreateHttpClient("simulator-home-assistant");
        home.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-home-token");
        using var controls = await home.GetAsync("/fixture/control/counts");
        Assert.Equal(HttpStatusCode.NotFound, controls.StatusCode);

        using var created = await client.PostAsJsonAsync("/api/conversations", new { title = "" });
        created.EnsureSuccessStatusCode();
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = conversation.RootElement.GetProperty("id").GetProperty("value").GetGuid();
        var sensorEvents = await SubmitAsync("read temperature");
        Assert.Contains("21.5", sensorEvents, StringComparison.Ordinal);
        var writeEvents = await SubmitAsync("turn on simulator lamp");
        Assert.Contains("event: ToolProposed", writeEvents, StringComparison.Ordinal);
        using var approvals = JsonDocument.Parse(await client.GetStringAsync("/api/approvals"));
        var approval = Assert.Single(approvals.RootElement.EnumerateArray()).GetProperty("approval");
        using var lamp = JsonDocument.Parse(await home.GetStringAsync("/api/states/light.simulator_lamp"));
        Assert.Equal("off", lamp.RootElement.GetProperty("state").GetString());
        var approvalId = approval.GetProperty("id").GetProperty("value").GetGuid();
        using var decision = await client.PostAsJsonAsync($"/api/approvals/{approvalId:D}/decision",
            new { expectedVersion = approval.GetProperty("expectedVersion").GetInt64(), approved = true });
        decision.EnsureSuccessStatusCode();
        using var changedLamp = JsonDocument.Parse(await home.GetStringAsync("/api/states/light.simulator_lamp"));
        Assert.Equal("on", changedLamp.RootElement.GetProperty("state").GetString());

        await application.DisposeAsync();
        Assert.True(File.Exists(databasePath));
        Assert.True(await new SqliteOwnerStateStore(database).IsConfiguredAsync(CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => home.GetAsync("/health/ready"));

        async Task<string> SubmitAsync(string text)
        {
            using var submitted = await client.PostAsJsonAsync($"/api/conversations/{id:D}/turns",
                new { requestId = Guid.NewGuid().ToString("D"), text });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            using var turn = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            var turnId = turn.RootElement.GetProperty("turnId").GetProperty("value").GetGuid();
            using var deadline = new CancellationTokenSource(Timeout);
            using var response = await client.GetAsync(
                $"/api/turns/{turnId:D}/events", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            var events = await response.Content.ReadAsStringAsync(deadline.Token);
            Assert.Contains("event: TurnCompleted", events, StringComparison.Ordinal);
            return events;
        }
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        using var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/auth/csrf"));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
    }
}
