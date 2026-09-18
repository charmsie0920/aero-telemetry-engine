namespace Ingestion.Worker.Configuration;

/// <summary>
/// Locates and loads the repo-root .env for local development.
/// </summary>
/// <remarks>
/// A fixed relative path like "../../../.env" only resolves correctly when the
/// process is launched from one specific directory: `dotnet run` uses the
/// shell's working directory, not the project directory, so running from the
/// repo root silently loaded nothing and produced an opaque 401 later.
/// This walks up from both the binary location and the working directory
/// instead, and logs which file it used.
/// </remarks>
public static class DotEnvLoader
{
    private const string FileName = ".env";
    private const int MaxDepth = 8;

    /// <summary>Returns the path that was loaded, or null if no .env was found.</summary>
    public static string? Load()
    {
        var path = Find(AppContext.BaseDirectory) ?? Find(Directory.GetCurrentDirectory());
        if (path is null)
        {
            return null;
        }

        DotNetEnv.Env.Load(path);
        return path;
    }

    private static string? Find(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        for (var depth = 0; depth < MaxDepth && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
