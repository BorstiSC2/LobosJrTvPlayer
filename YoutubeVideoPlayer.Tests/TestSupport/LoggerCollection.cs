using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// Tests that redirect the static <see cref="Logger"/> to their own file. They run after all other tests,
/// one at a time, so log output of parallel tests cannot end up in their files.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoggerCollection
{
    public const string Name = "Logger";
}

/// <summary>Points <see cref="Logger"/> at a temporary file for the lifetime of the object.</summary>
public sealed class RedirectedLog : IDisposable
{
    private readonly string _previousPath;
    private readonly TempDirectory _directory = new();

    public RedirectedLog()
    {
        _previousPath = Logger.LogFilePath;
        Path = _directory.File("errorlog.txt");
        Logger.LogFilePath = Path;
    }

    public string Path { get; }

    public string Content => File.Exists(Path) ? File.ReadAllText(Path) : string.Empty;

    public void Dispose()
    {
        Logger.LogFilePath = _previousPath;
        _directory.Dispose();
    }
}
