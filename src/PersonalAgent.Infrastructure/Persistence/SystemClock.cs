using PersonalAgent.Application;

namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Provides UTC wall-clock time to Infrastructure persistence services.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
