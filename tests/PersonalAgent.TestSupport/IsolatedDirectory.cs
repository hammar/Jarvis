namespace PersonalAgent.TestSupport;

/// <summary>Owns one uniquely named temporary directory and removes only that directory on disposal.</summary>
public sealed class IsolatedDirectory : IDisposable
{
    private readonly string path;
    private bool disposed;

    private IsolatedDirectory(string path)
    {
        this.path = path;
        Path = path;
    }

    /// <summary>Gets the full path to the owned temporary directory.</summary>
    public string Path { get; }

    /// <summary>Creates an isolated temporary data directory for one test scope.</summary>
    /// <returns>A disposable owner for the newly created directory.</returns>
    public static IsolatedDirectory Create()
    {
        var directory = Directory.CreateTempSubdirectory("jarvis-test-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new IsolatedDirectory(directory.FullName);
    }

    /// <summary>Removes the directory created by this instance, if it still exists.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        disposed = true;
    }
}
