using System.Globalization;

namespace PersonalAgent.Application.Tools.Home;

/// <summary>Recognizes only whole-request exact light commands, never model assertions or partial natural language.</summary>
public static class HomeLightIntent
{
    /// <summary>Checks that every canonical light parameter was explicitly requested by the current owner.</summary>
    /// <param name="catalog">Trusted catalog and automatic-write gate.</param>
    /// <param name="request">Host-carried current owner request.</param>
    /// <param name="entityId">Canonical resolved target.</param>
    /// <param name="on">Proposed power state.</param>
    /// <param name="brightness">Optional native brightness.</param>
    /// <returns>True only for the documented whole-command grammar and unique matching target.</returns>
    public static bool Matches(HomeEntityCatalog catalog, string? request, string entityId, bool on, int? brightness)
    {
        if (!catalog.Options.AutomaticLightsEnabled || request is null)
        {
            return false;
        }
        var prefix = on ? "turn on " : "turn off ";
        if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var target = request[prefix.Length..];
        if (brightness is not null)
        {
            var suffix = " at brightness " + brightness.Value.ToString(CultureInfo.InvariantCulture);
            if (!target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            target = target[..^suffix.Length];
        }
        var matches = catalog.Resolve(target);
        return matches.Count == 1 && matches[0].EntityId == entityId
            && catalog.AllowsLight(entityId, on, brightness);
    }
}
