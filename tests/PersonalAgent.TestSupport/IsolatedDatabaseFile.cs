namespace PersonalAgent.TestSupport;

/// <summary>Owns an isolated temporary directory and SQLite file path for one test scope.</summary>
public sealed class IsolatedDatabaseFile : IDisposable
{
    private readonly IsolatedDirectory directory;

    private IsolatedDatabaseFile(IsolatedDirectory directory)
    {
        this.directory = directory;
        Path = System.IO.Path.Combine(directory.Path, "jarvis-test.db");
    }

    /// <summary>Gets the database file path inside the owned temporary directory.</summary>
    public string Path { get; }

    /// <summary>Creates a new file-backed SQLite path that cannot collide with another test scope.</summary>
    /// <returns>A disposable owner for the isolated database file.</returns>
    public static IsolatedDatabaseFile Create() => new(IsolatedDirectory.Create());

    /// <summary>Removes only the temporary directory owned by this instance.</summary>
    public void Dispose() => directory.Dispose();
}
