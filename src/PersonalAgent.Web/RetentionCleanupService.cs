using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PersonalAgent.Application;

namespace PersonalAgent.Web;

/// <summary>Periodically applies durable owner retention settings while the Web host remains running.</summary>
public sealed class RetentionCleanupService : BackgroundService, IHealthCheck
{
    private readonly IHistoryRetentionStore retentionStore;
    private readonly IClock clock;
    private readonly RetentionSettings defaults;
    private readonly TimeSpan cleanupInterval;
    private readonly ILogger<RetentionCleanupService> logger;
    private volatile bool cleanupFailed;

    /// <summary>Creates the periodic cleanup worker using trusted first-run defaults.</summary>
    /// <param name="retentionStore">Application-owned boundary for durable cleanup.</param>
    /// <param name="clock">UTC clock used to calculate retention cutoffs.</param>
    /// <param name="defaults">Validated trusted defaults used when no durable owner settings exist.</param>
    /// <param name="cleanupInterval">Positive interval between background cleanup passes.</param>
    /// <param name="logger">Privacy-safe maintenance failure reporting.</param>
    public RetentionCleanupService(
        IHistoryRetentionStore retentionStore,
        IClock clock,
        RetentionSettings defaults,
        TimeSpan cleanupInterval,
        ILogger<RetentionCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(retentionStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(logger);
        defaults.Validate();
        if (cleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupInterval));
        }

        this.retentionStore = retentionStore;
        this.clock = clock;
        this.defaults = defaults;
        this.cleanupInterval = cleanupInterval;
        this.logger = logger;
    }

    /// <summary>Reports degraded readiness after a failed pass until a subsequent cleanup succeeds.</summary>
    /// <param name="context">Health probe context.</param>
    /// <param name="cancellationToken">Token that cancels the probe.</param>
    /// <returns>A privacy-safe maintenance health result without storage exception details.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(cleanupFailed
            ? HealthCheckResult.Degraded("History cleanup failed; the next scheduled pass will retry.")
            : HealthCheckResult.Healthy("History cleanup has no outstanding failure."));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(cleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await retentionStore.CleanupExpiredAsync(defaults, clock.UtcNow, stoppingToken);
                cleanupFailed = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                cleanupFailed = true;
                logger.LogError(
                    "History cleanup failed with {ErrorType}; retrying at the next scheduled pass in {Interval}.",
                    error.GetType().Name,
                    cleanupInterval);
            }
        }
    }
}
