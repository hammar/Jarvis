using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalAgent.Application.Tools.Home;

/// <summary>Declares an exact administrator-approved entity and its operation limits.</summary>
/// <param name="EntityId">Canonical lowercase HA identifier, never a pattern.</param>
/// <param name="FriendlyName">Trusted display name.</param>
/// <param name="Area">Trusted display area.</param>
/// <param name="Unit">Sensor/thermostat unit; thermostat must use C or F.</param>
/// <param name="Aliases">Trusted descriptive aliases; duplicates across entities require clarification.</param>
/// <param name="AllowWrite">Whether narrow light or thermostat operations are permitted.</param>
/// <param name="MinimumBrightness">Minimum optional brightness in HA native 0-255 units.</param>
/// <param name="MaximumBrightness">Maximum optional brightness in HA native 0-255 units.</param>
/// <param name="MinimumTemperature">Minimum thermostat target in Unit.</param>
/// <param name="MaximumTemperature">Maximum thermostat target in Unit.</param>
/// <param name="MaximumAgeSeconds">Maximum accepted source age in seconds.</param>
public sealed record HomeEntityDefinition(
    string EntityId, string FriendlyName, string Area, string? Unit, string[] Aliases,
    bool AllowWrite = false, int MinimumBrightness = 1, int MaximumBrightness = 255,
    decimal MinimumTemperature = 16, decimal MaximumTemperature = 28,
    int MaximumAgeSeconds = 300);

/// <summary>Contains validated host-owned time budgets and feature gates.</summary>
/// <param name="ThermostatEnabled">Whether thermostat writes are registered; always approval-required.</param>
/// <param name="AutomaticLightsEnabled">Whether exact owner light commands can bypass approval.</param>
/// <param name="RequestTimeoutSeconds">One shared deadline for a read's at most two attempts.</param>
/// <param name="VerificationSeconds">Maximum observation window, never a write retry window.</param>
/// <param name="PollMilliseconds">Bounded observation interval.</param>
public sealed record HomeToolOptions(
    bool ThermostatEnabled = false, bool AutomaticLightsEnabled = false,
    int RequestTimeoutSeconds = 5, int VerificationSeconds = 5, int PollMilliseconds = 250);

/// <summary>Owns immutable exact catalog resolution, permissions, limits and complete configuration identity.</summary>
public sealed class HomeEntityCatalog
{
    private readonly HomeEntityDefinition[] entities;
    private readonly Dictionary<string, HomeEntityDefinition[]> names;

    /// <summary>Validates and snapshots trusted settings; invalid settings fail before any network I/O.</summary>
    /// <param name="entities">Explicit approved entities; unsupported domains and patterns are rejected.</param>
    /// <param name="options">Trusted budgets and gates.</param>
    public HomeEntityCatalog(IEnumerable<HomeEntityDefinition> entities, HomeToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestTimeoutSeconds is < 1 or > 60 || options.VerificationSeconds is < 1 or > 60
            || options.PollMilliseconds is < 10 or > 1000)
        {
            throw new ArgumentException("Home Assistant time budgets are outside supported bounds.");
        }
        Options = options;
        this.entities = entities.Select(ValidateAndCopy).OrderBy(e => e.EntityId, StringComparer.Ordinal).ToArray();
        if (this.entities.Length is 0 or > 100
            || this.entities.Select(e => e.EntityId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != this.entities.Length)
        {
            throw new ArgumentException("Home Assistant requires 1-100 unique explicit entities.");
        }
        names = this.entities.SelectMany(e => e.Aliases.Append(e.FriendlyName).Append(e.EntityId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => (name, entity: e)))
            .GroupBy(pair => pair.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.entity).ToArray(), StringComparer.OrdinalIgnoreCase);
        if (this.entities.Any(e => names[e.EntityId].Length != 1))
        {
            throw new ArgumentException("An entity ID cannot also name another entity.");
        }
        ConfigurationVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { entities = this.entities, options }))));
    }

    /// <summary>Gets the immutable timing and gate settings.</summary>
    public HomeToolOptions Options { get; }

    /// <summary>Gets the hash of all catalog metadata, bounds, aliases and gates.</summary>
    public string ConfigurationVersion { get; }

    /// <summary>Returns approved metadata without exposing mutable catalog arrays.</summary>
    public IReadOnlyList<HomeEntityDefinition> Entities =>
        Array.AsReadOnly(entities.Select(e => e with { Aliases = (string[])e.Aliases.Clone() }).ToArray());

    /// <summary>Resolves an ID/name/alias without selecting one of multiple matches.</summary>
    /// <param name="target">Untrusted bounded target.</param>
    /// <returns>Zero, one or multiple approved candidates.</returns>
    public IReadOnlyList<HomeEntityDefinition> Resolve(string? target) =>
        target is not null && names.TryGetValue(target, out var matches)
            ? Array.AsReadOnly(matches.Select(e => e with { Aliases = (string[])e.Aliases.Clone() }).ToArray())
            : Array.Empty<HomeEntityDefinition>();

    /// <summary>Requires a canonical approved ID; aliases are not accepted at the transport boundary.</summary>
    /// <param name="entityId">Exact canonical entity ID.</param>
    /// <returns>Approved immutable metadata.</returns>
    public HomeEntityDefinition Require(string entityId) =>
        entities.FirstOrDefault(e => e.EntityId == entityId) is { } entity
            ? entity with { Aliases = (string[])entity.Aliases.Clone() }
            : throw new HomeAssistantException(HomeAssistantFailure.NotAllowed);

    /// <summary>Checks a narrow light operation and all bounds without network access.</summary>
    /// <param name="entityId">Canonical light ID.</param>
    /// <param name="on">Requested power state.</param>
    /// <param name="brightness">Optional native brightness, only allowed for on.</param>
    /// <returns>Whether this operation is approved.</returns>
    public bool AllowsLight(string entityId, bool on, int? brightness)
    {
        var entity = entities.FirstOrDefault(e => e.EntityId == entityId);
        return entity is { AllowWrite: true } && entityId.StartsWith("light.", StringComparison.Ordinal)
            && (brightness is null || on && brightness >= entity.MinimumBrightness && brightness <= entity.MaximumBrightness);
    }

    /// <summary>Checks the feature gate, exact thermostat and target bounds.</summary>
    /// <param name="entityId">Canonical thermostat ID.</param>
    /// <param name="temperature">Target setpoint in configured units.</param>
    /// <returns>Whether this proposal may request exact approval.</returns>
    public bool AllowsTemperature(string entityId, decimal temperature)
    {
        var entity = entities.FirstOrDefault(e => e.EntityId == entityId);
        return Options.ThermostatEnabled && entity is { AllowWrite: true }
            && entityId.StartsWith("climate.", StringComparison.Ordinal)
            && temperature >= entity.MinimumTemperature && temperature <= entity.MaximumTemperature;
    }

    /// <summary>Builds exact dispatcher permissions from the same validated catalog.</summary>
    /// <returns>No wildcard permissions; thermostat risk can never be lowered.</returns>
    public IReadOnlyList<ToolPermission> Permissions()
    {
        var permissions = new List<ToolPermission>
        {
            new("home.list_entities", "home.list_entities", "list", null, ToolRiskClass.ReadOnly)
        };
        foreach (var entity in entities)
        {
            permissions.Add(new("home.get_state", "home.get_state", "read", entity.EntityId, ToolRiskClass.ReadOnly));
            if (entity.AllowWrite && entity.EntityId.StartsWith("light.", StringComparison.Ordinal))
            {
                permissions.Add(new("home.set_light", "home.set_light", "set_light", entity.EntityId, ToolRiskClass.LowRiskWrite));
            }
            if (entity.AllowWrite && entity.EntityId.StartsWith("climate.", StringComparison.Ordinal) && Options.ThermostatEnabled)
            {
                permissions.Add(new("home.set_temperature", "home.set_temperature", "set_temperature", entity.EntityId, ToolRiskClass.ApprovalRequired));
            }
        }
        return permissions.AsReadOnly();
    }

    private static HomeEntityDefinition ValidateAndCopy(HomeEntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.EntityId is null || !Regex.IsMatch(entity.EntityId, @"\A(light|climate|sensor)\.[a-z0-9_]{1,128}\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || !ValidText(entity.FriendlyName) || !ValidText(entity.Area)
            || entity.Aliases is null || entity.Aliases.Length > 20 || entity.Aliases.Any(alias => !ValidText(alias))
            || entity.Unit is { Length: > 32 } || entity.MaximumAgeSeconds is < 1 or > 86400
            || entity.MinimumBrightness is < 1 or > 255 || entity.MaximumBrightness < entity.MinimumBrightness
            || entity.MaximumBrightness > 255 || entity.MinimumTemperature >= entity.MaximumTemperature
            || entity.MinimumTemperature < -100 || entity.MaximumTemperature > 200
            || entity.EntityId.StartsWith("climate.", StringComparison.Ordinal) && entity.Unit is not ("C" or "F")
            || entity.EntityId.StartsWith("sensor.", StringComparison.Ordinal) && entity.AllowWrite)
        {
            throw new ArgumentException("Home Assistant catalog contains invalid metadata, entity type or bounds.");
        }
        return entity with { Aliases = entity.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray() };
    }

    private static bool ValidText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 128
        && text == text.Trim() && !text.Any(char.IsControl);
}
