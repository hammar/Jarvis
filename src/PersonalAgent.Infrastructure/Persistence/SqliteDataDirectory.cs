namespace PersonalAgent.Infrastructure.Persistence;

/// <summary>Resolves the durable application data directory outside the deployment folder.</summary>
public static class SqliteDataDirectory
{
    /// <summary>Uses a configured path or the per-user Jarvis directory beneath local application data.</summary>
    /// <param name="configuredPath">Optional owner-configured data directory.</param>
    /// <param name="localApplicationDataPath">Operating-system local application-data directory.</param>
    /// <returns>The selected directory path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when neither path is available.</exception>
    public static string Resolve(string? configuredPath, string? localApplicationDataPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        if (string.IsNullOrWhiteSpace(localApplicationDataPath))
        {
            throw new InvalidOperationException(
                "Set JARVIS_DATA_DIR or configure a local application-data directory.");
        }

        return Path.Combine(localApplicationDataPath, "Jarvis");
    }
}
