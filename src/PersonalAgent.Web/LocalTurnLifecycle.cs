using Microsoft.Extensions.Diagnostics.HealthChecks;
using PersonalAgent.Application;

internal sealed class LocalTurnCoordinatorHostedService(ILocalTurnCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        coordinator.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        coordinator.StopAsync(cancellationToken);
}

internal sealed class LocalTurnReadinessHealthCheck(
    ILocalTurnCoordinator coordinator,
    Func<CancellationToken, Task<bool>> databaseReadinessProbe,
    bool localProviderConfigured) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databaseReady = await databaseReadinessProbe(cancellationToken);
        var configured = localProviderConfigured;
        var data = new Dictionary<string, object>
        {
            ["database_and_migrations"] = databaseReady ? "ready" : "not_ready",
            ["local_policy"] = "ready",
            ["turn_runtime"] = coordinator.IsReady ? "ready" : "not_ready",
            ["local_provider_configuration"] = configured ? "configured" : "not_configured",
            ["model_connectivity"] = "not_probed"
        };

        if (!databaseReady)
        {
            return HealthCheckResult.Unhealthy("SQLite is unavailable or its expected migrations are not applied.", data: data);
        }

        if (!coordinator.IsReady)
        {
            return HealthCheckResult.Unhealthy("Local turn recovery or worker startup has not completed.", data: data);
        }

        return configured
            ? HealthCheckResult.Healthy("Local turn coordinator is ready.", data)
            : HealthCheckResult.Degraded(
                "Local turns are not configured; model connectivity is checked separately.",
                data: data);
    }
}
