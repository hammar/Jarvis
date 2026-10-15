using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalAgent.Application.Tools.Home;

namespace PersonalAgent.Infrastructure.HomeAssistant;

/// <summary>Parses trusted catalog settings and supplies only synthetic defaults for controlled profiles.</summary>
public static class HomeAssistantConfiguration
{
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
