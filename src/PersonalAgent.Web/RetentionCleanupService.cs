using Microsoft.Extensions.Hosting;
using PersonalAgent.Application;

namespace PersonalAgent.Web;

/// <summary>Periodically applies durable owner retention settings while the Web host remains running.</summary>
public sealed class RetentionCleanupService : BackgroundService
{
    private readonly IHistoryRetentionStore retentionStore;
    private readonly IClock clock;
    private readonly RetentionSettings defaults;
    private readonly TimeSpan cleanupInterval;

    /// <summary>Creates the periodic cleanup worker using trusted first-run defaults.</summary>
    /// <param name="retentionStore">Application-owned boundary for durable cleanup.</param>
    /// <param name="clock">UTC clock used to calculate retention cutoffs.</param>
    /// <param name="defaults">Validated trusted defaults used when no durable owner settings exist.</param>
    /// <param name="cleanupInterval">Positive interval between background cleanup passes.</param>
    public RetentionCleanupService(
        IHistoryRetentionStore retentionStore,
        IClock clock,
        RetentionSettings defaults,
        TimeSpan cleanupInterval)
    {
        ArgumentNullException.ThrowIfNull(retentionStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(defaults);
        defaults.Validate();
        if (cleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupInterval));
        }

        this.retentionStore = retentionStore;
        this.clock = clock;
        this.defaults = defaults;
        this.cleanupInterval = cleanupInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(cleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await retentionStore.CleanupExpiredAsync(defaults, clock.UtcNow, stoppingToken);
        }
    }
}
