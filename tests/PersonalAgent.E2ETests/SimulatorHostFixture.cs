using Aspire.Hosting.Testing;
using Aspire.Hosting;
using PersonalAgent.TestSupport;
using Xunit;

namespace PersonalAgent.E2ETests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SimulatorCollection : ICollectionFixture<SimulatorHostFixture>
{
    public const string Name = "Aspire simulator";
}

/// <summary>Starts one isolated Aspire E2E profile and owns its temporary data path.</summary>
public sealed class SimulatorHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private IsolatedDirectory? dataDirectory;
    private DistributedApplication? application;

    /// <summary>Gets an HTTP client routed to the managed Web resource.</summary>
    public HttpClient WebClient { get; private set; } = null!;

    /// <summary>Gets an HTTP client routed to the controlled model fixture.</summary>
    public HttpClient ModelClient { get; private set; } = null!;

    /// <summary>Gets an HTTP client routed to the controlled Home Assistant fixture.</summary>
    public HttpClient HomeAssistantClient { get; private set; } = null!;

    /// <summary>Builds and starts the isolated E2E resource graph.</summary>
    public async Task InitializeAsync()
    {
        dataDirectory = IsolatedDirectory.Create();
        var previousProfile = Environment.GetEnvironmentVariable("JARVIS_PROFILE");
        var previousDataDirectory = Environment.GetEnvironmentVariable("JARVIS_E2E_DATA_DIR");
        IDistributedApplicationTestingBuilder appBuilder;

        try
        {
            Environment.SetEnvironmentVariable("JARVIS_PROFILE", "E2E");
            Environment.SetEnvironmentVariable("JARVIS_E2E_DATA_DIR", dataDirectory.Path);
            appBuilder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.PersonalAgent_AppHost>()
                .WaitAsync(StartupTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JARVIS_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("JARVIS_E2E_DATA_DIR", previousDataDirectory);
        }

        application = await appBuilder.BuildAsync().WaitAsync(StartupTimeout);
        await application.StartAsync().WaitAsync(StartupTimeout);
        await application.ResourceNotifications
            .WaitForResourceHealthyAsync("personalagent-web")
            .WaitAsync(StartupTimeout);
        await application.ResourceNotifications
            .WaitForResourceHealthyAsync("simulator-model")
            .WaitAsync(StartupTimeout);
        await application.ResourceNotifications
            .WaitForResourceHealthyAsync("simulator-home-assistant")
            .WaitAsync(StartupTimeout);

        WebClient = application.CreateHttpClient("personalagent-web");
        ModelClient = application.CreateHttpClient("simulator-model");
        HomeAssistantClient = application.CreateHttpClient("simulator-home-assistant");
    }

    /// <summary>Stops only resources launched by this fixture and removes its private test data.</summary>
    public async Task DisposeAsync()
    {
        WebClient?.Dispose();
        ModelClient?.Dispose();
        HomeAssistantClient?.Dispose();

        if (application is not null)
        {
            await application.DisposeAsync();
        }

        dataDirectory?.Dispose();
    }
}
