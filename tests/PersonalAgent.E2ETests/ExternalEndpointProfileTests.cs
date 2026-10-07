using Aspire.Hosting.Testing;
using Aspire.Hosting;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.E2ETests;

[Collection(SimulatorCollection.Name)]
public sealed class ExternalEndpointProfileTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait("Category", "AspireE2E")]
    public async Task LocalAndHybridProfilesLaunchWithExplicitExternalEndpointReferences()
    {
        await VerifyProfileAsync(
            "Local",
            new Dictionary<string, string>
            {
                ["JARVIS_OLLAMA_BASE_URL"] = "http://127.0.0.1:11434",
                ["JARVIS_OLLAMA_MODEL"] = "test-local-model"
            });

        await VerifyProfileAsync(
            "Hybrid",
            new Dictionary<string, string>
            {
                ["JARVIS_OLLAMA_BASE_URL"] = "http://127.0.0.1:11434",
                ["JARVIS_OLLAMA_MODEL"] = "test-local-model",
                ["JARVIS_CLOUD_BASE_URL"] = "https://cloud.example.invalid",
                ["JARVIS_CLOUD_SECRET_REFERENCE"] = "test-only-cloud-secret-reference"
            });
    }

    private static async Task VerifyProfileAsync(string profile, IReadOnlyDictionary<string, string> settings)
    {
        using var dataDirectory = IsolatedDirectory.Create();
        var previousValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["JARVIS_PROFILE"] = Environment.GetEnvironmentVariable("JARVIS_PROFILE"),
            ["JARVIS_DATA_DIR"] = Environment.GetEnvironmentVariable("JARVIS_DATA_DIR"),
            ["JARVIS_OLLAMA_BASE_URL"] = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_BASE_URL"),
            ["JARVIS_OLLAMA_MODEL"] = Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL"),
            ["JARVIS_CLOUD_BASE_URL"] = Environment.GetEnvironmentVariable("JARVIS_CLOUD_BASE_URL"),
            ["JARVIS_CLOUD_SECRET_REFERENCE"] = Environment.GetEnvironmentVariable("JARVIS_CLOUD_SECRET_REFERENCE")
        };

        try
        {
            Environment.SetEnvironmentVariable("JARVIS_PROFILE", profile);
            Environment.SetEnvironmentVariable("JARVIS_DATA_DIR", dataDirectory.Path);
            foreach (var setting in new[]
                     {
                         "JARVIS_OLLAMA_BASE_URL",
                         "JARVIS_OLLAMA_MODEL",
                         "JARVIS_CLOUD_BASE_URL",
                         "JARVIS_CLOUD_SECRET_REFERENCE"
                     })
            {
                Environment.SetEnvironmentVariable(
                    setting,
                    settings.GetValueOrDefault(setting));
            }

            var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.PersonalAgent_AppHost>()
                .WaitAsync(StartupTimeout);
            await using var application = await builder.BuildAsync().WaitAsync(StartupTimeout);
            await application.StartAsync().WaitAsync(StartupTimeout);
            await application.ResourceNotifications
                .WaitForResourceHealthyAsync("personalagent-web")
                .WaitAsync(StartupTimeout);

            using var client = application.CreateHttpClient("personalagent-web");
            var html = await client.GetStringAsync("/");

            Assert.Contains($"Running the {profile} profile.", html, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var previousValue in previousValues)
            {
                Environment.SetEnvironmentVariable(previousValue.Key, previousValue.Value);
            }
        }
    }
}
