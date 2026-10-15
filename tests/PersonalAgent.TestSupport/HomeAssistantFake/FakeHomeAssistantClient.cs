using PersonalAgent.Application;

namespace PersonalAgent.TestSupport.HomeAssistantFake;

/// <summary>Owns isolated synthetic device state; service changes never touch a real household.</summary>
public sealed class FakeHomeAssistantClient : IHomeAssistantClient
{
    private readonly object gate = new();
    private readonly Dictionary<string, HomeAssistantEntity> states;
    private readonly IClock clock;

    /// <summary>Creates deterministic fixtures relative to an injected UTC clock.</summary>
    /// <param name="clock">Fixture-owned clock, not household time/state.</param>
    public FakeHomeAssistantClient(IClock clock)
    {
        this.clock = clock;
        states = new[]
        {
            new HomeAssistantEntity("light.simulator_lamp", "off", clock.UtcNow, true),
            new HomeAssistantEntity("light.porch_left", "off", clock.UtcNow, true),
            new HomeAssistantEntity("light.porch_right", "off", clock.UtcNow, true),
            new HomeAssistantEntity("climate.simulator", "heat", clock.UtcNow, true) { TargetTemperature = 20 },
            new HomeAssistantEntity("sensor.temperature", "21.5", clock.UtcNow, true),
            new HomeAssistantEntity("sensor.stale", "18", clock.UtcNow.AddHours(-1), true) { Stale = true },
            new HomeAssistantEntity("sensor.unavailable", "unavailable", clock.UtcNow, false),
            new HomeAssistantEntity("lock.forbidden", "locked", clock.UtcNow, true)
        }.ToDictionary(s => s.EntityId, StringComparer.Ordinal);
    }

    /// <summary>Gets whether accepted commands immediately update fixture state.</summary>
    public bool ApplyWrites { get; set; } = true;

    /// <inheritdoc />
    public ValueTask<HomeAssistantEntity> GetStateAsync(string entityId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(states.TryGetValue(entityId, out var state)
                ? state : throw new HomeAssistantException(HomeAssistantFailure.UnknownEntity));
        }
    }

    /// <summary>Changes only this fake's state for controlled observation tests.</summary>
    /// <param name="state">Complete synthetic snapshot, including source timestamp.</param>
    public void SetState(HomeAssistantEntity state)
    {
        lock (gate)
        {
            states[state.EntityId] = state;
        }
    }

    /// <inheritdoc />
    public async ValueTask<HomeAssistantWriteResult> SetLightAsync(
        string entityId, bool on, int? brightness, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(entityId, cancellationToken);
        if (ApplyWrites)
        {
            SetState(state with { State = on ? "on" : "off", Brightness = brightness, ObservedAtUtc = clock.UtcNow, Stale = false });
        }
        return new(HomeAssistantWriteStatus.Accepted, null);
    }

    /// <inheritdoc />
    public async ValueTask<HomeAssistantWriteResult> SetTemperatureAsync(
        string entityId, decimal temperature, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(entityId, cancellationToken);
        if (ApplyWrites)
        {
            SetState(state with { TargetTemperature = temperature, ObservedAtUtc = clock.UtcNow, Stale = false });
        }
        return new(HomeAssistantWriteStatus.Accepted, null);
    }
}
