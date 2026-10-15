using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalAgent.Domain;

namespace PersonalAgent.Application.Tools;

/// <summary>Classifies the consequence of executing a registered tool action.</summary>
public enum ToolRiskClass
{
    /// <summary>Reads approved application or device state without changing it.</summary>
    ReadOnly = 0,
    /// <summary>Performs an explicitly requested, bounded, low-risk write.</summary>
    LowRiskWrite = 1,
    /// <summary>Requires fresh owner approval for this exact action.</summary>
    ApprovalRequired = 2,
    /// <summary>Is outside the supported capability surface and must never execute.</summary>
    Unsupported = 3
}

/// <summary>Contains a typed tool proposal after the tool-specific validation and resolution step.</summary>
/// <param name="ActionType">Stable registered action kind, not a provider operation string.</param>
/// <param name="Operation">Narrow operation name used by the host allowlist.</param>
/// <param name="ResolvedEntityId">Exact configured entity identifier, or null for non-entity operations.</param>
/// <param name="CanonicalArguments">Tool-produced JSON object for the validated action arguments.</param>
/// <param name="ExplicitlyRequestedByOwner">True only when trusted tool logic verified this exact action in the current owner request.</param>
public sealed record ToolActionDraft(
    string ActionType,
    string Operation,
    string? ResolvedEntityId,
    string CanonicalArguments,
    bool ExplicitlyRequestedByOwner = false)
{
    /// <summary>Opaque host-owned destination identity; interrupted recovery may observe only the same scope. Null preserves unscoped tools.</summary>
    public string? ObservationScope { get; init; }

    /// <summary>Gets exact memory versions used by the proposal, enabling privacy-safe invalidation.</summary>
    public IReadOnlyList<PersonalAgent.Application.Memory.MemoryReference>? MemoryReferences { get; init; }

    // Retry accounting must not include generated action/new-fact identities.
    internal string? EquivalentFailureArguments { get; init; }
}

/// <summary>Describes whether a typed proposal is executable, needs clarification, or is invalid.</summary>
public enum ToolPreparationStatus
{
    /// <summary>The arguments were validated and target resolution is unique.</summary>
    Ready,
    /// <summary>More owner information is needed before a target can be selected.</summary>
    ClarificationRequired,
    /// <summary>The proposal is malformed or cannot be safely supported.</summary>
    Rejected
}

/// <summary>Returns the result of validating and resolving one registered tool proposal.</summary>
/// <param name="Status">Validation outcome.</param>
/// <param name="Action">Canonical action when status is Ready.</param>
/// <param name="ReasonCode">Stable safe reason code, never raw untrusted arguments.</param>
public sealed record ToolPreparationResult(
    ToolPreparationStatus Status,
    ToolActionDraft? Action,
    string? ReasonCode)
{
    /// <summary>Creates a successful preparation result.</summary>
    /// <param name="action">Validated and resolved action.</param>
    public static ToolPreparationResult Ready(ToolActionDraft action) =>
        new(ToolPreparationStatus.Ready, action ?? throw new ArgumentNullException(nameof(action)), null);

    /// <summary>Creates a clarification result without selecting an arbitrary target.</summary>
    /// <param name="reasonCode">Stable clarification reason.</param>
    public static ToolPreparationResult Clarify(string reasonCode) =>
        new(ToolPreparationStatus.ClarificationRequired, null, RequireReason(reasonCode));

    /// <summary>Creates a denial result for invalid or unsupported arguments.</summary>
    /// <param name="reasonCode">Stable safe denial reason.</param>
    public static ToolPreparationResult Reject(string reasonCode) =>
        new(ToolPreparationStatus.Rejected, null, RequireReason(reasonCode));

    private static string RequireReason(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 96
            ? value
            : throw new ArgumentException("A short stable reason code is required.", nameof(value));
}

/// <summary>Describes the result reported by a registered tool's execution adapter.</summary>
public enum ToolExecutionStatus
{
    /// <summary>The adapter completed its operation and can attempt state verification.</summary>
    Completed,
    /// <summary>The adapter knows the operation was not applied.</summary>
    Failed,
    /// <summary>The request may have been sent but its effect is not known.</summary>
    Unknown
}

/// <summary>Reports one tool execution result without conflating acceptance and verified success.</summary>
/// <param name="Status">Known execution outcome.</param>
/// <param name="ResultJson">Optional typed result JSON returned to the engine.</param>
/// <param name="ReasonCode">Stable reason code for failure or uncertainty.</param>
public sealed record ToolExecutionOutcome(
    ToolExecutionStatus Status,
    string? ResultJson,
    string? ReasonCode);

/// <summary>Describes the result of observing the action's intended effect.</summary>
public enum ToolVerificationStatus
{
    /// <summary>Current state satisfies the intended action.</summary>
    Verified,
    /// <summary>The adapter established that the requested effect did not occur.</summary>
    /// <remarks>An unchanged snapshot after uncertain delivery is insufficient if late application remains possible;
    /// report <see cref="Unknown"/> until non-application can be established.</remarks>
    NotApplied,
    /// <summary>Observation could not determine whether the action took effect.</summary>
    Unknown
}

/// <summary>Returns one verification result after execution or uncertain delivery.</summary>
/// <param name="Status">Observed state outcome.</param>
/// <param name="ReasonCode">Stable reason code when observation is inconclusive.</param>
public sealed record ToolVerificationOutcome(ToolVerificationStatus Status, string? ReasonCode);

/// <summary>Describes an immutable host registration that exposes one tool to the agent engine.</summary>
public interface IToolRegistration
{
    /// <summary>Gets the exact name and input schema made visible to the engine.</summary>
    AgentToolDefinition Definition { get; }

    /// <summary>Gets the trusted implementation's minimum risk classification.</summary>
    ToolRiskClass RiskClass { get; }
}

internal interface IRuntimeToolRegistration : IToolRegistration
{
    ValueTask<ToolPreparationAttempt> PrepareAsync(ToolDispatchRequest request, CancellationToken cancellationToken);
    ValueTask<ToolExecutionOutcome> ExecuteAsync(object arguments, ToolActionDraft action, CancellationToken cancellationToken);
    ValueTask<ToolVerificationOutcome> VerifyAsync(
        object arguments,
        ToolActionDraft action,
        ToolExecutionOutcome execution,
        CancellationToken cancellationToken);
}

internal sealed record ToolPreparationAttempt(ToolPreparationResult Result, object? Arguments);

/// <summary>
/// Registers a typed tool handler with a fixed schema and host-controlled preparation,
/// execution, and verification callbacks.
/// </summary>
/// <typeparam name="TArguments">The validated argument type used by the tool.</typeparam>
public sealed class ToolRegistration<TArguments> : IToolRegistration, IRuntimeToolRegistration
{
    private const int MaximumArgumentCharacters = 65_536;
    private readonly JsonSerializerOptions serializerOptions = new()
    {
        MaxDepth = 32,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly Func<TArguments, ToolDispatchRequest, CancellationToken, ValueTask<ToolPreparationResult>> prepare;
    private readonly Func<TArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> execute;
    private readonly Func<TArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> verify;

    /// <summary>Creates a tool registration with strict typed JSON decoding.</summary>
    /// <param name="name">Stable engine-facing tool name.</param>
    /// <param name="inputSchema">JSON Schema object matching the argument type.</param>
    /// <param name="riskClass">Trusted minimum risk classification.</param>
    /// <param name="prepare">Validates semantics, resolves aliases, and creates canonical action arguments.</param>
    /// <param name="execute">Executes one already-authorized action; uncertain delivery must return Unknown.</param>
    /// <param name="verify">Observes the intended effect without re-executing the action.</param>
    public ToolRegistration(
        string name,
        string inputSchema,
        ToolRiskClass riskClass,
        Func<TArguments, ToolDispatchRequest, CancellationToken, ValueTask<ToolPreparationResult>> prepare,
        Func<TArguments, ToolActionDraft, CancellationToken, ValueTask<ToolExecutionOutcome>> execute,
        Func<TArguments, ToolActionDraft, ToolExecutionOutcome, CancellationToken, ValueTask<ToolVerificationOutcome>> verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || !IsValidToolName(name))
        {
            throw new ArgumentException("Tool names must be 1-64 lowercase letters, digits, dots, underscores, or hyphens.", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(inputSchema);
        using var schema = JsonDocument.Parse(inputSchema);
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A tool input schema must be a JSON object.", nameof(inputSchema));
        }

        if (!Enum.IsDefined(riskClass))
        {
            throw new ArgumentOutOfRangeException(nameof(riskClass));
        }

        Definition = new AgentToolDefinition(name, schema.RootElement.GetRawText(), riskClass == ToolRiskClass.ReadOnly);
        RiskClass = riskClass;
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.verify = verify ?? throw new ArgumentNullException(nameof(verify));
    }

    /// <inheritdoc />
    public AgentToolDefinition Definition { get; }

    /// <inheritdoc />
    public ToolRiskClass RiskClass { get; }

    async ValueTask<ToolPreparationAttempt> IRuntimeToolRegistration.PrepareAsync(
        ToolDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ArgumentsJson.Length is 0 or > MaximumArgumentCharacters)
        {
            return new(ToolPreparationResult.Reject("invalid_tool_arguments"), null);
        }

        TArguments? arguments;
        try
        {
            using var document = JsonDocument.Parse(request.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
            {
                return new(ToolPreparationResult.Reject("invalid_tool_arguments"), null);
            }

            arguments = document.RootElement.Deserialize<TArguments>(serializerOptions);
        }
        catch (JsonException)
        {
            return new(ToolPreparationResult.Reject("invalid_tool_arguments"), null);
        }

        if (arguments is null)
        {
            return new(ToolPreparationResult.Reject("invalid_tool_arguments"), null);
        }

        var result = await prepare(arguments, request, cancellationToken);
        if (result.Status == ToolPreparationStatus.Ready && result.Action is null)
        {
            throw new InvalidOperationException("A ready tool preparation must include a canonical action.");
        }

        if (result.Status != ToolPreparationStatus.Ready && result.Action is not null)
        {
            throw new InvalidOperationException("A non-ready tool preparation must not include an executable action.");
        }

        if (!Enum.IsDefined(result.Status))
        {
            throw new InvalidOperationException("The tool returned an unknown preparation status.");
        }

        return new(result, arguments);
    }

    ValueTask<ToolExecutionOutcome> IRuntimeToolRegistration.ExecuteAsync(
        object arguments,
        ToolActionDraft action,
        CancellationToken cancellationToken) =>
        execute((TArguments)arguments, action, cancellationToken);

    ValueTask<ToolVerificationOutcome> IRuntimeToolRegistration.VerifyAsync(
        object arguments,
        ToolActionDraft action,
        ToolExecutionOutcome execution,
        CancellationToken cancellationToken) =>
        verify((TArguments)arguments, action, execution, cancellationToken);

    private static bool IsValidToolName(string value)
    {
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return value[0] is >= 'a' and <= 'z';
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Provides the immutable tool registrations available to dispatcher and agent-turn composition.</summary>
public interface IToolRegistry
{
    /// <summary>Gets the exact agent definitions for registered tools.</summary>
    IReadOnlyList<AgentToolDefinition> Definitions { get; }

    /// <summary>Looks up a tool by its case-sensitive stable name.</summary>
    /// <param name="name">Registered tool name.</param>
    /// <param name="registration">Registration when found.</param>
    /// <returns>True when the name is registered.</returns>
    bool TryGet(string name, out IToolRegistration? registration);
}

/// <summary>Stores an immutable, duplicate-checked collection of host tool registrations.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, IRuntimeToolRegistration> registrations;

    /// <summary>Creates a registry from trusted application registrations.</summary>
    /// <param name="registrations">Host-authored registrations; model output cannot add entries.</param>
    public ToolRegistry(IEnumerable<IToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var entries = new Dictionary<string, IRuntimeToolRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (registration is not IRuntimeToolRegistration runtimeRegistration)
            {
                throw new ArgumentException("Registrations must use the typed host registration implementation.", nameof(registrations));
            }

            if (!entries.TryAdd(registration.Definition.Name, runtimeRegistration))
            {
                throw new ArgumentException($"Tool name '{registration.Definition.Name}' is registered more than once.", nameof(registrations));
            }
        }

        this.registrations = entries;
        Definitions = Array.AsReadOnly(entries.Values.Select(registration => registration.Definition).ToArray());
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentToolDefinition> Definitions { get; }

    /// <inheritdoc />
    public bool TryGet(string name, out IToolRegistration? registration)
    {
        if (registrations.TryGetValue(name, out var runtimeRegistration))
        {
            registration = runtimeRegistration;
            return true;
        }

        registration = null;
        return false;
    }

    internal bool TryGetRuntime(string name, out IRuntimeToolRegistration? registration)
    {
        var found = registrations.TryGetValue(name, out var runtimeRegistration);
        registration = runtimeRegistration;
        return found;
    }
}
