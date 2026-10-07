using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class AspireSimulatorTests(SimulatorHostFixture fixture)
{
    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task ReadinessReportsDatabaseUnavailableAfterStartup()
    {
        var databasePath = Path.Combine(fixture.DataDirectoryPath, "jarvis.db");
        var movedFiles = new List<(string Original, string Backup)>();
        try
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    var backup = path + ".readiness-probe";
                    File.Move(path, backup);
                    movedFiles.Add((path, backup));
                }
            }

            using var response = await fixture.WebClient.GetAsync("/health/ready");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "not_ready",
                body.RootElement.GetProperty("checks")
                    .GetProperty("local-turn-runtime")
                    .GetProperty("data")
                    .GetProperty("database_and_migrations")
                    .GetString());
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            foreach (var (original, backup) in movedFiles)
            {
                File.Move(backup, original);
            }
        }
    }

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task SimulatorStartsWebAndDiscoversDeterministicManagedEndpoints()
    {
        using var webResponse = await fixture.WebClient.GetAsync("/health/ready");
        using var modelResponse = await fixture.ModelClient.GetAsync("/fixture/model");
        using var modelsResponse = await fixture.ModelClient.GetAsync("/v1/models");
        using var homeResponse = await fixture.HomeAssistantClient.GetAsync("/fixture/entities");
        var page = await fixture.WebClient.GetStringAsync("/");
        var model = await modelResponse.Content.ReadAsStringAsync();
        using var ready = JsonDocument.Parse(await webResponse.Content.ReadAsStringAsync());
        using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var home = await homeResponse.Content.ReadAsStringAsync();
        using var completionResponse = await fixture.ModelClient.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "simulator-model", messages = new[] { new { role = "user", content = "test" } } });
        using var completion = JsonDocument.Parse(await completionResponse.Content.ReadAsStringAsync());
        using var streamingResponse = await fixture.ModelClient.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "simulator-model",
                stream = true,
                messages = new[] { new { role = "user", content = "test" } }
            });
        var streaming = await streamingResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, streamingResponse.StatusCode);
        Assert.Equal(
            "configured",
            ready.RootElement.GetProperty("checks")
                .GetProperty("local-turn-runtime")
                .GetProperty("data")
                .GetProperty("local_provider_configuration")
                .GetString());
        Assert.Equal("simulator-model", models.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal(
            "Controlled fixture response",
            completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Contains("Controlled fixture response", streaming, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", streaming, StringComparison.Ordinal);
        Assert.Contains("Running the E2E profile.", page, StringComparison.Ordinal);
        Assert.Equal("controlled fixture response", JsonDocument.Parse(model).RootElement.GetProperty("completion").GetString());
        Assert.Equal(
            "light.simulator_lamp",
            JsonDocument.Parse(home).RootElement[0].GetProperty("entityId").GetString());
    }
}
