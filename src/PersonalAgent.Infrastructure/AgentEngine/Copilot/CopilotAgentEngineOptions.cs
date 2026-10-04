using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Describes the explicitly configured model endpoint for one provider route.</summary>
/// <param name="BaseUrl">Absolute provider API URL used only for this route.</param>
/// <param name="Model">Provider model or deployment name.</param>
/// <param name="ApiKeyReference">Optional host secret reference; local providers normally omit it.</param>
public sealed record CopilotProviderSettings(
    Uri BaseUrl,
    string Model,
    SecretReference? ApiKeyReference = null);

/// <summary>
/// Configures ephemeral runtime storage and explicit local/cloud providers for the Copilot adapter.
/// </summary>
/// <param name="RuntimeDataDirectory">Application-owned parent directory for per-turn disposable runtime state.</param>
/// <param name="LocalProvider">Explicit local provider configuration.</param>
/// <param name="CloudProvider">Explicit cloud provider configuration and secret reference.</param>
public sealed record CopilotAgentEngineOptions(
    string RuntimeDataDirectory,
    CopilotProviderSettings LocalProvider,
    CopilotProviderSettings CloudProvider);
