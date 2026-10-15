using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Application;
using PersonalAgent.Application.Tools.Home;

namespace PersonalAgent.Infrastructure.HomeAssistant;

/// <summary>Parses trusted catalog settings and supplies only synthetic defaults for controlled profiles.</summary>
public static class HomeAssistantConfiguration
{
    /// <summary>Binds exact approvals to the installation, account reference and complete catalog configuration.</summary>
    /// <param name="catalog">Immutable exact entity catalog and feature/timing settings.</param>
    /// <param name="endpoint">Trusted installation endpoint, normalized as for REST request construction.</param>
    /// <param name="reference">Protected reference identity, never its resolved value.</param>
    /// <returns>Opaque configuration hash; changing installation/reference invalidates pending approvals.</returns>
    public static string PolicyVersion(HomeEntityCatalog catalog, Uri endpoint, SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(reference.Name) || reference.Name.Length > 256
            || reference.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("Home Assistant requires a valid named secret reference.");
        }
        var normalized = NormalizeEndpoint(endpoint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            catalog.ConfigurationVersion,
            endpoint = normalized.AbsoluteUri,
            reference = reference.Name
        }))));
    }

    /// <summary>Normalizes a trusted HTTP(S) installation root without userinfo, query or fragment.</summary>
    /// <param name="endpoint">Administrator-supplied endpoint, never model output.</param>
    /// <returns>The canonical root used by both policy identity and request construction.</returns>
    public static Uri NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("Home Assistant requires a trusted HTTP(S) base URI without embedded credentials or query data.");
        }
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }

    /// <summary>Creates the explicit synthetic catalog; live profiles must never use it as household discovery.</summary>
    /// <param name="options">Trusted feature/timing options; live writes are not implied.</param>
    /// <returns>Approved deterministic lights, thermostat and sensors, including ambiguous porch aliases.</returns>
    public static HomeEntityCatalog SimulatorCatalog(HomeToolOptions? options = null) => new(
    [
        new("light.simulator_lamp", "simulator lamp", "Study", null, ["lamp"], true),
        new("light.porch_left", "left porch light", "Porch", null, ["porch"], true),
        new("light.porch_right", "right porch light", "Porch", null, ["porch"], true),
        new("climate.simulator", "simulator thermostat", "Hall", "C", ["thermostat"], true),
        new("sensor.temperature", "temperature sensor", "Hall", "C", ["temperature"]),
        new("sensor.stale", "stale sensor", "Hall", "C", ["stale"]),
        new("sensor.unavailable", "unavailable sensor", "Hall", "C", ["unavailable"])
    ], options ?? new(AutomaticLightsEnabled: true));

    /// <summary>Parses a bounded administrator-authored JSON catalog with strict property validation.</summary>
    /// <param name="json">Configuration JSON, never model arguments or remotely discovered entities.</param>
    /// <param name="options">Host feature gates and time budgets.</param>
    /// <returns>Validated exact catalog; invalid configuration throws without echoing input.</returns>
    public static HomeEntityCatalog ParseCatalog(string json, HomeToolOptions options)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65_536)
        {
            throw new ArgumentException("Home Assistant requires a bounded explicit catalog.");
        }
        try
        {
            var entities = JsonSerializer.Deserialize<HomeEntityDefinition[]>(json, new JsonSerializerOptions
            {
                MaxDepth = 16,
                RespectRequiredConstructorParameters = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            return new(entities ?? throw new ArgumentException("Home Assistant catalog must be an array."), options);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Home Assistant catalog JSON is invalid.");
        }
    }
}
