using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var profile = builder.Configuration["JARVIS_PROFILE"] ?? "Simulator";
if (profile is not ("Simulator" or "Local" or "Hybrid" or "E2E"))
{
    throw new InvalidOperationException("JARVIS_PROFILE must be Simulator, Local, Hybrid, or E2E.");
}

var dataDirectory = ResolveDataDirectory(profile, builder.Configuration);
var web = builder.AddProject<Projects.PersonalAgent_Web>("personalagent-web", launchProfileName: null)
    .WithHttpEndpoint()
    .WithEnvironment("JARVIS_PROFILE", profile)
    .WithEnvironment("JARVIS_DATA_DIR", dataDirectory)
    .WithHttpHealthCheck("/health/ready");

if (profile is "Simulator" or "E2E")
{
    web.WithEnvironment("JARVIS_CLOUD_ENABLED", "false")
        .WithEnvironment("JARVIS_OLLAMA_BASE_URL", string.Empty)
        .WithEnvironment("JARVIS_OLLAMA_MODEL", string.Empty)
        .WithEnvironment("JARVIS_OLLAMA_SECRET_REFERENCE", string.Empty)
        .WithEnvironment("JARVIS_CLOUD_BASE_URL", string.Empty)
        .WithEnvironment("JARVIS_CLOUD_SECRET_REFERENCE", string.Empty)
        .WithEnvironment("JARVIS_HOME_ASSISTANT_BASE_URL", string.Empty)
        .WithEnvironment("JARVIS_HOME_ASSISTANT_SECRET_REFERENCE", string.Empty);

    var simulatorModel = builder.AddProject<Projects.PersonalAgent_SimulatorEndpoints>(
            "simulator-model",
            launchProfileName: null)
        .WithHttpEndpoint()
        .WithEnvironment("JARVIS_SIMULATOR_KIND", "model")
        .WithHttpHealthCheck("/health/ready");
    var simulatorHome = builder.AddProject<Projects.PersonalAgent_SimulatorEndpoints>(
            "simulator-home-assistant",
            launchProfileName: null)
        .WithHttpEndpoint()
        .WithEnvironment("JARVIS_SIMULATOR_KIND", "home-assistant")
        .WithHttpHealthCheck("/health/ready");
    web.WithReference(simulatorModel).WithReference(simulatorHome);
}
else
{
    var ollamaEndpoint = RequireEndpoint(builder.Configuration, "JARVIS_OLLAMA_BASE_URL");
    var ollamaModel = builder.Configuration["JARVIS_OLLAMA_MODEL"];
    if (string.IsNullOrWhiteSpace(ollamaModel))
    {
        throw new InvalidOperationException("Local and Hybrid profiles require an explicit JARVIS_OLLAMA_MODEL.");
    }

    web.WithEnvironment("JARVIS_OLLAMA_BASE_URL", ollamaEndpoint)
        .WithEnvironment("JARVIS_OLLAMA_MODEL", ollamaModel);
    var localSecretReference = builder.Configuration["JARVIS_OLLAMA_SECRET_REFERENCE"];
    if (!string.IsNullOrWhiteSpace(localSecretReference))
    {
        web.WithEnvironment("JARVIS_OLLAMA_SECRET_REFERENCE", localSecretReference);
    }
    web.WithEnvironment("JARVIS_CLOUD_ENABLED", profile == "Hybrid" ? "true" : "false");

    if (profile == "Hybrid")
    {
        var cloudEndpoint = RequireEndpoint(builder.Configuration, "JARVIS_CLOUD_BASE_URL");
        var cloudSecretReference = builder.Configuration["JARVIS_CLOUD_SECRET_REFERENCE"];
        if (string.IsNullOrWhiteSpace(cloudSecretReference))
        {
            throw new InvalidOperationException(
                "Hybrid profile requires JARVIS_CLOUD_SECRET_REFERENCE; provide a reference, never a secret value.");
        }

        web.WithEnvironment("JARVIS_CLOUD_BASE_URL", cloudEndpoint)
            .WithEnvironment("JARVIS_CLOUD_SECRET_REFERENCE", cloudSecretReference);
    }

    var homeAssistantEndpoint = builder.Configuration["JARVIS_HOME_ASSISTANT_BASE_URL"];
    if (!string.IsNullOrWhiteSpace(homeAssistantEndpoint))
    {
        web.WithEnvironment(
            "JARVIS_HOME_ASSISTANT_BASE_URL",
            ValidateEndpoint("JARVIS_HOME_ASSISTANT_BASE_URL", homeAssistantEndpoint));
    }

    var homeAssistantSecretReference = builder.Configuration["JARVIS_HOME_ASSISTANT_SECRET_REFERENCE"];
    if (!string.IsNullOrWhiteSpace(homeAssistantSecretReference))
    {
        web.WithEnvironment("JARVIS_HOME_ASSISTANT_SECRET_REFERENCE", homeAssistantSecretReference);
    }
}

builder.Build().Run();

static string ResolveDataDirectory(string profile, IConfiguration configuration)
{
    if (profile == "E2E")
    {
        var e2eDirectory = configuration["JARVIS_E2E_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(e2eDirectory))
        {
            throw new InvalidOperationException(
                "E2E profile requires JARVIS_E2E_DATA_DIR so each test run has isolated data.");
        }

        return Path.GetFullPath(e2eDirectory);
    }

    if (profile == "Simulator")
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PersonalAgent",
            "simulator");
    }

    var configuredDirectory = configuration["JARVIS_DATA_DIR"];
    if (string.IsNullOrWhiteSpace(configuredDirectory))
    {
        return string.Empty;
    }

    return Path.GetFullPath(configuredDirectory);
}

static string RequireEndpoint(IConfiguration configuration, string settingName)
{
    var endpoint = configuration[settingName];
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        throw new InvalidOperationException($"Profile requires explicit {settingName} configuration.");
    }

    return ValidateEndpoint(settingName, endpoint);
}

static string ValidateEndpoint(string settingName, string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
        || endpoint.Scheme is not ("http" or "https")
        || !string.IsNullOrEmpty(endpoint.UserInfo)
        || !string.IsNullOrEmpty(endpoint.Query)
        || !string.IsNullOrEmpty(endpoint.Fragment))
    {
        throw new InvalidOperationException(
            $"{settingName} must be an absolute HTTP(S) endpoint without embedded credentials or query data.");
    }

    return endpoint.ToString().TrimEnd('/');
}
