using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>An <see cref="IYtDlpRunner"/> that answers from a delegate and records the arguments of every run.</summary>
public sealed class FakeYtDlp : IYtDlpRunner
{
    private readonly List<IReadOnlyList<string>> _runs = new();

    public FakeYtDlp(string executablePath = @"C:\Tools\yt-dlp.exe")
    {
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }

    /// <summary>Gets the arguments and returns what yt-dlp "printed"; exit code 0 and empty JSON by default.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<YtDlpOutput>> Respond { get; set; } =
        (_, _) => Task.FromResult(new YtDlpOutput(0, "{}", string.Empty));

    /// <summary>The arguments of every run so far, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Runs
    {
        get
        {
            lock (_runs)
                return _runs.ToList();
        }
    }

    /// <summary>Every run prints <paramref name="json"/> and ends with exit code 0.</summary>
    public FakeYtDlp Prints(string json, string standardError = "")
    {
        Respond = (_, _) => Task.FromResult(new YtDlpOutput(0, json, standardError));
        return this;
    }

    public Task<YtDlpOutput> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        lock (_runs)
            _runs.Add(arguments.ToList());
        return Respond(arguments, cancellationToken);
    }
}
