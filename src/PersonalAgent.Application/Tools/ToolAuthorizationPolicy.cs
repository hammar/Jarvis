using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.Application.Tools;

/// <summary>Defines one exact allowed tool/action/operation/entity tuple.</summary>
/// <param name="ToolName">Case-sensitive registered tool name.</param>
/// <param name="ActionType">Stable action kind declared by the registered tool.</param>
/// <param name="Operation">Narrow operation identifier.</param>
/// <param name="EntityId">Exact configured entity identifier, or null for a non-entity operation.</param>
/// <param name="MinimumRisk">Minimum risk imposed by trusted host configuration.</param>
public sealed record ToolPermission(
    string ToolName,
    string ActionType,
    string Operation,
    string? EntityId,
    ToolRiskClass MinimumRisk);

/// <summary>Reports the allowlist and risk decision for one prepared action.</summary>
/// <param name="Allowed">Whether the exact action is permitted to continue.</param>
/// <param name="ReasonCode">Stable safe policy result code.</param>
/// <param name="PolicyVersion">Content-derived version of the active trusted allowlist.</param>
/// <param name="EffectiveRisk">Higher of tool-declared and configured minimum risk.</param>
public sealed record ToolAuthorizationDecision(
    bool Allowed,
    string ReasonCode,
    string PolicyVersion,
    ToolRiskClass EffectiveRisk);

/// <summary>Evaluates exact entity and operation permissions independently of model output.</summary>
public interface IToolAuthorizationPolicy
{
    /// <summary>Evaluates a prepared action against the current host-owned allowlist.</summary>
    /// <param name="toolName">Registered tool name requested by the model.</param>
    /// <param name="action">Validated action with an exact resolved entity where applicable.</param>
    /// <param name="declaredRisk">Minimum risk declared by the trusted handler registration.</param>
    /// <returns>The exact allowlist decision and active policy version.</returns>
    ToolAuthorizationDecision Evaluate(
        string toolName,
        ToolActionDraft action,
        ToolRiskClass declaredRisk);
}

/// <summary>Applies exact allowlist rules and derives a policy version from their complete contents.</summary>
public sealed class ToolAllowlistPolicy : IToolAuthorizationPolicy
{
    private readonly IReadOnlyDictionary<PermissionKey, ToolPermission> permissions;

    /// <summary>Creates the current entity/operation policy.</summary>
    /// <param name="policyVersion">Trusted configuration version label.</param>
    /// <param name="permissions">Exact allowed tool, action, operation, and entity combinations.</param>
    public ToolAllowlistPolicy(string policyVersion, IEnumerable<ToolPermission> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        ArgumentNullException.ThrowIfNull(permissions);
        var entries = new Dictionary<PermissionKey, ToolPermission>();
        foreach (var permission in permissions)
        {
            ArgumentNullException.ThrowIfNull(permission);
            ValidatePermission(permission);
            if (!entries.TryAdd(
                    new PermissionKey(
                        permission.ToolName,
                        permission.ActionType,
                        permission.Operation,
                        permission.EntityId),
                    permission))
            {
                throw new ArgumentException("The tool allowlist contains a duplicate exact permission.", nameof(permissions));
            }
        }

        this.permissions = entries;
        PolicyVersion = ComputePolicyVersion(policyVersion, entries.Values);
    }

    /// <summary>Gets the content-derived identity of this allowlist configuration.</summary>
    public string PolicyVersion { get; }

    /// <inheritdoc />
    public ToolAuthorizationDecision Evaluate(
        string toolName,
        ToolActionDraft action,
        ToolRiskClass declaredRisk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(declaredRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(declaredRisk));
        }

        var key = new PermissionKey(toolName, action.ActionType, action.Operation, action.ResolvedEntityId);
        if (declaredRisk == ToolRiskClass.Unsupported
            || !permissions.TryGetValue(key, out var permission)
            || permission.MinimumRisk == ToolRiskClass.Unsupported)
        {
            return new(false, "tool_action_not_allowlisted", PolicyVersion, ToolRiskClass.Unsupported);
        }

        var effectiveRisk = (ToolRiskClass)Math.Max((int)declaredRisk, (int)permission.MinimumRisk);
        return new(true, "tool_action_allowed", PolicyVersion, effectiveRisk);
    }

    private static string ComputePolicyVersion(string label, IEnumerable<ToolPermission> permissions)
    {
        var canonicalPermissions = permissions
            .OrderBy(permission => permission.ToolName, StringComparer.Ordinal)
            .ThenBy(permission => permission.ActionType, StringComparer.Ordinal)
            .ThenBy(permission => permission.Operation, StringComparer.Ordinal)
            .ThenBy(permission => permission.EntityId, StringComparer.Ordinal)
            .ToArray();
        var content = JsonSerializer.Serialize(new { label, permissions = canonicalPermissions });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..24];
        return $"{label}:{digest}";
    }

    private static void ValidatePermission(ToolPermission permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.ActionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.Operation);
        if (permission.ToolName.Length > 64
            || permission.ActionType.Length > 96
            || permission.Operation.Length > 64
            || permission.EntityId is { Length: 0 or > 256 }
            || !Enum.IsDefined(permission.MinimumRisk))
        {
            throw new ArgumentException("The allowlist contains an invalid tool permission.", nameof(permission));
        }
    }

    private readonly record struct PermissionKey(
        string ToolName,
        string ActionType,
        string Operation,
        string? EntityId);
}
