using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// An <see cref="IntegrationFactAttribute"/> test that also needs yt-dlp.exe: from the environment variable
/// <c>YTP_YTDLP_PATH</c>, else next to the test assembly or on <c>PATH</c>. Skipped without it.
/// </summary>
public sealed class YtDlpIntegrationFactAttribute : FactAttribute
{
    public const string PathVariable = "YTP_YTDLP_PATH";

    /// <summary>The yt-dlp.exe the tests run, or <c>null</c> if there is none.</summary>
    public static string? Executable { get; } =
        YtDlpLocator.Find(Environment.GetEnvironmentVariable(PathVariable) ?? "yt-dlp.exe", AppContext.BaseDirectory);

    public YtDlpIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnvironmentVariable) != "1")
            Skip = $"Integration test; set {IntegrationFactAttribute.EnvironmentVariable}=1 to run it.";
        else if (Executable == null)
            Skip = $"yt-dlp.exe not found; put it on PATH or set {PathVariable}.";
    }
}
