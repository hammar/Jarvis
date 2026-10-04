namespace PersonalAgent.TestSupport;

/// <summary>Owns one unique temporary directory containing a fresh SQLite database path.</summary>
public sealed class IsolatedDatabaseFile : IDisposable
{
    private readonly IsolatedDirectory directory;

    private IsolatedDatabaseFile(IsolatedDirectory directory)
    {
        this.directory = directory;
        DatabasePath = Path.Combine(directory.Path, "personal-agent.db");
    }

    /// <summary>Gets the isolated persistent database file path.</summary>
    public string DatabasePath { get; }

    /// <summary>Creates a new empty database scope safe for one test.</summary>
    /// <returns>An owner that removes only its own test directory on disposal.</returns>
    public static IsolatedDatabaseFile Create() => new(IsolatedDirectory.Create());

    /// <summary>Removes the owned directory and all database files created beneath it.</summary>
    public void Dispose() => directory.Dispose();
}
