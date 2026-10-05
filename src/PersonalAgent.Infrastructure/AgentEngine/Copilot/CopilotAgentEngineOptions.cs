using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.AgentEngine.Copilot;

/// <summary>Contains trusted host configuration for isolated Copilot turn execution.</summary>
/// <param name="RuntimeDirectory">Private root directory for replaceable, per-turn runtime state.</param>
/// <param name="LocalProvider">Explicit local provider settings, or null when local inference is not configured.</param>
/// <param name="CloudProvider">Explicit cloud provider settings, or null when cloud inference is not configured.</param>
public sealed record CopilotAgentEngineOptions(
    string RuntimeDirectory,
    CopilotProviderOptions? LocalProvider,
    CopilotProviderOptions? CloudProvider);

/// <summary>Describes an explicitly configured OpenAI-compatible inference endpoint.</summary>
/// <param name="Model">Provider model or deployment name.</param>
/// <param name="BaseUrl">Absolute endpoint base URL; local providers must be loopback and remote cloud providers HTTPS.</param>
/// <param name="WireApi">Optional SDK wire protocol, such as <c>responses</c>.</param>
/// <param name="ApiKeyReference">Optional host-owned secret reference; the secret is never placed in the child environment.</param>
public sealed record CopilotProviderOptions(
    string Model,
    Uri BaseUrl,
    string? WireApi = null,
    SecretReference? ApiKeyReference = null);
