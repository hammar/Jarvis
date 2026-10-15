using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.Application.Tools.Home;

/// <summary>Empty input for the approved catalog listing.</summary>
public sealed record HomeListArguments;

/// <summary>Targets an approved ID, friendly name or uniquely resolved alias.</summary>
/// <param name="Entity">Exact ID or trusted descriptive name.</param>
public sealed record HomeStateArguments([property: JsonPropertyName("entity")] string Entity);

/// <summary>Proposes a narrow bounded light operation.</summary>
/// <param name="Entity">Approved ID or uniquely resolved name.</param>
/// <param name="On">Requested power state.</param>
/// <param name="Brightness">Optional native HA brightness, 1-255 within entity bounds; on only.</param>
public sealed record HomeLightArguments(
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("brightness")] int? Brightness = null);

/// <summary>Proposes a thermostat target requiring exact owner approval.</summary>
/// <param name="Entity">Approved thermostat ID or uniquely resolved name.</param>
/// <param name="Temperature">Target in the catalog's C or F unit, without implicit conversion.</param>
public sealed record HomeTemperatureArguments(
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("temperature")] decimal Temperature);

/// <summary>Provides the four initial host tools without arbitrary services or model-owned permissions.</summary>
public static class HomeToolRegistrations
{
    private const string ListSchema = """{"type":"object","properties":{},"additionalProperties":false}""";
    private const string StateSchema = """{"type":"object","properties":{"entity":{"type":"string","maxLength":256}},"required":["entity"],"additionalProperties":false}""";
    private const string LightSchema = """{"type":"object","properties":{"entity":{"type":"string","maxLength":256},"on":{"type":"boolean"},"brightness":{"type":["integer","null"],"minimum":1,"maximum":255}},"required":["entity","on"],"additionalProperties":false}""";
    private const string TemperatureSchema = """{"type":"object","properties":{"entity":{"type":"string","maxLength":256},"temperature":{"type":"number"}},"required":["entity","temperature"],"additionalProperties":false}""";

    /// <summary>Creates fixed typed registrations; caller uses catalog permissions and configuration hash for policy.</summary>
    /// <param name="catalog">Validated immutable host configuration.</param>
    /// <param name="client">Narrow application-owned HA client.</param>
    /// <param name="observationScope">Opaque trusted connection/catalog identity binding recovery to the original destination; never a token.</param>
    /// <param name="timeProvider">Controlled time for observation deadlines and delays; system time by default.</param>
    /// <returns>Three registrations plus thermostat only when explicitly enabled.</returns>
    public static IReadOnlyList<IToolRegistration> Create(
        HomeEntityCatalog catalog, IHomeAssistantClient client, string observationScope, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationScope);
        var time = timeProvider ?? TimeProvider.System;
        var registrations = new List<IToolRegistration>
        {
            new ToolRegistration<HomeListArguments>("home.list_entities", ListSchema, ToolRiskClass.ReadOnly,
                (_, _, _) => ValueTask.FromResult(ToolPreparationResult.Ready(
                    new("home.list_entities", "list", null, "{}"))),
                (_, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var result = catalog.Entities.Select(entity => new
                    {
                        entityId = entity.EntityId, friendlyName = entity.FriendlyName, area = entity.Area,
                        unit = entity.Unit,
                        operations = catalog.Permissions().Where(p => p.EntityId == entity.EntityId)
                            .Select(p => p.Operation).ToArray()
                    });
                    return ValueTask.FromResult(new ToolExecutionOutcome(
                        ToolExecutionStatus.Completed, JsonSerializer.Serialize(result), null));
                },
                (_, _, _, _) => ValueTask.FromResult(new ToolVerificationOutcome(ToolVerificationStatus.Verified, null))),
            new ToolRegistration<HomeStateArguments>("home.get_state", StateSchema, ToolRiskClass.ReadOnly,
                (args, _, _) => ValueTask.FromResult(Prepare(catalog, args.Entity, "home.get_state", "read",
                    id => JsonSerializer.Serialize(new HomeStateArguments(id)), _ => true)),
                async (_, action, token) =>
                {
                    try
                    {
                        var state = await client.GetStateAsync(action.ResolvedEntityId!, token);
                        return new(ToolExecutionStatus.Completed, JsonSerializer.Serialize(new
                        {
                            entityId = state.EntityId, state = state.State, observedAtUtc = state.ObservedAtUtc,
                            available = state.Available, stale = state.Stale,
                            unit = catalog.Require(state.EntityId).Unit,
                            brightness = state.Brightness, targetTemperature = state.TargetTemperature
                        }), null);
                    }
                    catch (HomeAssistantException error)
                    {
                        return new(ToolExecutionStatus.Failed, null, Reason(error.Failure));
                    }
                },
                (_, _, execution, _) => ValueTask.FromResult(new ToolVerificationOutcome(
                    execution.Status == ToolExecutionStatus.Completed ? ToolVerificationStatus.Verified : ToolVerificationStatus.Unknown,
                    "read_interrupted"))),
            new ToolRegistration<HomeLightArguments>("home.set_light", LightSchema, ToolRiskClass.LowRiskWrite,
                (args, request, _) => ValueTask.FromResult(Prepare(catalog, args.Entity, "home.set_light", "set_light",
                    id => JsonSerializer.Serialize(args with { Entity = id }),
                    id => catalog.AllowsLight(id, args.On, args.Brightness),
                    id => HomeLightIntent.Matches(catalog, request.CurrentUserRequest, id, args.On, args.Brightness),
                    observationScope)),
                async (args, action, token) => WriteOutcome(await client.SetLightAsync(
                    action.ResolvedEntityId!, args.On, args.Brightness, token)),
                (args, action, _, token) => VerifyAsync(client, catalog, time, action.ResolvedEntityId!,
                    state => state.State == (args.On ? "on" : "off")
                        && (args.Brightness is null || state.Brightness == args.Brightness), token))
        };
        if (catalog.Options.ThermostatEnabled)
        {
            registrations.Add(new ToolRegistration<HomeTemperatureArguments>(
                "home.set_temperature", TemperatureSchema, ToolRiskClass.ApprovalRequired,
                (args, _, _) => ValueTask.FromResult(Prepare(catalog, args.Entity, "home.set_temperature", "set_temperature",
                    id => JsonSerializer.Serialize(args with { Entity = id }),
                    id => catalog.AllowsTemperature(id, args.Temperature), observationScope: observationScope)),
                async (args, action, token) => WriteOutcome(await client.SetTemperatureAsync(
                    action.ResolvedEntityId!, args.Temperature, token)),
                (args, action, _, token) => VerifyAsync(client, catalog, time, action.ResolvedEntityId!,
                    state => state.TargetTemperature == args.Temperature, token)));
        }
        return registrations.AsReadOnly();
    }

    private static ToolPreparationResult Prepare(
        HomeEntityCatalog catalog, string? target, string tool, string operation,
        Func<string, string> canonical, Func<string, bool> allowed, Func<string, bool>? explicitIntent = null,
        string? observationScope = null)
    {
        var candidates = catalog.Resolve(target);
        if (candidates.Count > 1)
        {
            return ToolPreparationResult.Clarify("home_entity_ambiguous");
        }
        if (candidates.Count == 0 || !allowed(candidates[0].EntityId))
        {
            return ToolPreparationResult.Reject("home_operation_not_allowed");
        }
        var id = candidates[0].EntityId;
        return ToolPreparationResult.Ready(new(tool, operation, id, canonical(id), explicitIntent?.Invoke(id) ?? false)
        {
            ObservationScope = observationScope
        });
    }

    private static ToolExecutionOutcome WriteOutcome(HomeAssistantWriteResult result) =>
        new(result.Status switch
        {
            HomeAssistantWriteStatus.Accepted => ToolExecutionStatus.Completed,
            HomeAssistantWriteStatus.Rejected => ToolExecutionStatus.Failed,
            _ => ToolExecutionStatus.Unknown
        }, JsonSerializer.Serialize(new { submission = result.Status.ToString(), causality = "not_established" }),
            result.ReasonCode);

    private static async ValueTask<ToolVerificationOutcome> VerifyAsync(
        IHomeAssistantClient client, HomeEntityCatalog catalog, TimeProvider time, string id,
        Func<HomeAssistantEntity, bool> matches, CancellationToken token)
    {
        var start = time.GetTimestamp();
        var window = TimeSpan.FromSeconds(catalog.Options.VerificationSeconds);
        while (time.GetElapsedTime(start) < window)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var state = await client.GetStateAsync(id, token);
                if (state.Available && !state.Stale && matches(state))
                {
                    return new(ToolVerificationStatus.Verified, null);
                }
            }
            catch (HomeAssistantException error)
            {
                return new(ToolVerificationStatus.Unknown, Reason(error.Failure));
            }
            var remaining = window - time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(catalog.Options.PollMilliseconds)
                ? remaining : TimeSpan.FromMilliseconds(catalog.Options.PollMilliseconds), time, token);
        }
        return new(ToolVerificationStatus.Unknown, "home_state_unconfirmed");
    }

    private static string Reason(HomeAssistantFailure failure) => "home_" + failure.ToString().ToLowerInvariant();
}
