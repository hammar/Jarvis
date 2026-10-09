using Aspire.Hosting.Testing;
using Aspire.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    internal const string BootstrapToken = "e2e-only-bootstrap-token";
    internal const string OwnerPassphrase = "e2e-owner-passphrase-for-tests";
    private IsolatedDirectory? dataDirectory;
    private DistributedApplication? application;

    /// <summary>Gets an HTTP client routed to the managed Web resource.</summary>
    public HttpClient WebClient { get; private set; } = null!;

    /// <summary>Gets an HTTP client routed to the controlled model fixture.</summary>
    public HttpClient ModelClient { get; private set; } = null!;

    /// <summary>Gets an HTTP client routed to the controlled Home Assistant fixture.</summary>
    public HttpClient HomeAssistantClient { get; private set; } = null!;

    /// <summary>Gets the private E2E data directory owned by this fixture.</summary>
    public string DataDirectoryPath => dataDirectory?.Path
        ?? throw new InvalidOperationException("The E2E host fixture has not started.");

    /// <summary>Builds and starts the isolated E2E resource graph.</summary>
    public async Task InitializeAsync()
    {
        dataDirectory = IsolatedDirectory.Create();
        await StartApplicationAsync();
    }

    /// <summary>Restarts the Aspire resource graph while preserving its isolated application data.</summary>
    public async Task RestartAsync()
    {
        WebClient?.Dispose();
        ModelClient?.Dispose();
        HomeAssistantClient?.Dispose();
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }

        await StartApplicationAsync();
    }

    private async Task StartApplicationAsync()
    {
        var previousProfile = Environment.GetEnvironmentVariable("JARVIS_PROFILE");
        var previousDataDirectory = Environment.GetEnvironmentVariable("JARVIS_E2E_DATA_DIR");
        var previousBootstrapToken = Environment.GetEnvironmentVariable("JARVIS_E2E_BOOTSTRAP_TOKEN");
        IDistributedApplicationTestingBuilder appBuilder;

        try
        {
            Environment.SetEnvironmentVariable("JARVIS_PROFILE", "E2E");
            Environment.SetEnvironmentVariable("JARVIS_E2E_DATA_DIR", DataDirectoryPath);
            Environment.SetEnvironmentVariable("JARVIS_E2E_BOOTSTRAP_TOKEN", BootstrapToken);
            appBuilder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.PersonalAgent_AppHost>()
                .WaitAsync(StartupTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JARVIS_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("JARVIS_E2E_DATA_DIR", previousDataDirectory);
            Environment.SetEnvironmentVariable("JARVIS_E2E_BOOTSTRAP_TOKEN", previousBootstrapToken);
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

        using (var discoveredWebClient = application.CreateHttpClient("personalagent-web"))
        {
            WebClient = new HttpClient(new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true
            })
            {
                BaseAddress = discoveredWebClient.BaseAddress
            };
        }
        ModelClient = application.CreateHttpClient("simulator-model");
        HomeAssistantClient = application.CreateHttpClient("simulator-home-assistant");
        await AuthenticateOwnerAsync();
    }

    private async Task AuthenticateOwnerAsync()
    {
        using var csrfResponse = await WebClient.GetAsync("/api/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        using var csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
        var requestToken = csrf.RootElement.GetProperty("token").GetString();
        WebClient.DefaultRequestHeaders.Add("X-CSRF-TOKEN", requestToken);
        using var statusResponse = await WebClient.GetAsync("/api/auth/status");
        statusResponse.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        var bootstrapRequired = status.RootElement.GetProperty("bootstrapRequired").GetBoolean();
        using var signIn = bootstrapRequired
            ? await WebClient.PostAsJsonAsync(
                "/api/auth/bootstrap",
                new
                {
                    bootstrapToken = BootstrapToken,
                    passphrase = OwnerPassphrase,
                    rememberMe = false
                })
            : await WebClient.PostAsJsonAsync(
                "/api/auth/login",
                new { passphrase = OwnerPassphrase, rememberMe = false });
        signIn.EnsureSuccessStatusCode();
        using var authenticatedCsrfResponse = await WebClient.GetAsync("/api/auth/csrf");
        authenticatedCsrfResponse.EnsureSuccessStatusCode();
        using var authenticatedCsrf = JsonDocument.Parse(await authenticatedCsrfResponse.Content.ReadAsStringAsync());
        WebClient.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        WebClient.DefaultRequestHeaders.Add(
            "X-CSRF-TOKEN",
            authenticatedCsrf.RootElement.GetProperty("token").GetString());
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
