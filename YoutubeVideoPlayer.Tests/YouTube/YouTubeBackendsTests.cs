using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.YouTube;

public sealed class YouTubeBackendsTests : IDisposable
{
    private readonly TempDirectory _app = new();
    private readonly TempDirectory _elsewhere = new();

    public void Dispose()
    {
        _app.Dispose();
        _elsewhere.Dispose();
    }

    private void PutYtDlp(string relativePath = "yt-dlp.exe", TempDirectory? folder = null)
    {
        var path = (folder ?? _app).File(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private YouTubeBackends Create(YouTubeBackend backend, bool fallback, string path = "yt-dlp.exe") =>
        YouTubeBackends.Create(
            new AppSettings.YouTubeSettings { SelectedBackend = backend, Fallback = fallback, YtDlp = new() { Path = path } },
            _app.Path,
            pathVariable: string.Empty, // a yt-dlp installed on this machine must not count
            createRunner: exe => new FakeYtDlp(exe));

    [Fact]
    public void YtDlp_WithFallback_AsksYtDlpFirst_ThenYoutubeExplode()
    {
        PutYtDlp();
        using var backends = Create(YouTubeBackend.YtDlp, fallback: true);

        var service = Assert.IsType<FallbackYouTubeService>(backends.Service);
        Assert.Equal("yt-dlp", service.PrimaryName);
        Assert.Equal("YoutubeExplode", service.SecondaryName);
        Assert.Equal(_app.File("yt-dlp.exe"), backends.YtDlpRunner!.ExecutablePath);
        Assert.Null(backends.Problem);
        Assert.Contains("yt-dlp", backends.Description);
    }

    [Fact]
    public void YtDlp_WithoutFallback_IsUsedAlone()
    {
        PutYtDlp();
        using var backends = Create(YouTubeBackend.YtDlp, fallback: false);

        Assert.IsType<YtDlpService>(backends.Service);
        Assert.NotNull(backends.YtDlpRunner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void YtDlp_WithoutTheExe_UsesYoutubeExplode_AndSaysSo(bool fallback)
    {
        using var backends = Create(YouTubeBackend.YtDlp, fallback);

        Assert.IsType<YoutubeExplodeService>(backends.Service);
        Assert.Null(backends.YtDlpRunner);
        Assert.Contains("'yt-dlp.exe' was not found", backends.Problem);
    }

    [Fact]
    public void YoutubeExplode_WithFallback_AndTheExe_FallsBackToYtDlp()
    {
        PutYtDlp();
        using var backends = Create(YouTubeBackend.YoutubeExplode, fallback: true);

        var service = Assert.IsType<FallbackYouTubeService>(backends.Service);
        Assert.Equal("YoutubeExplode", service.PrimaryName);
        Assert.Equal("yt-dlp", service.SecondaryName);
        Assert.NotNull(backends.YtDlpRunner); // so the update check runs
        Assert.Null(backends.Problem);
    }

    [Fact]
    public void YoutubeExplode_WithFallback_ButWithoutTheExe_IsUsedAlone_WithoutAProblem()
    {
        using var backends = Create(YouTubeBackend.YoutubeExplode, fallback: true);

        Assert.IsType<YoutubeExplodeService>(backends.Service);
        Assert.Null(backends.Problem);
        Assert.Contains("no fallback", backends.Description);
    }

    [Fact]
    public void YoutubeExplode_WithoutFallback_LeavesYtDlpAlone()
    {
        PutYtDlp();
        using var backends = Create(YouTubeBackend.YoutubeExplode, fallback: false);

        Assert.IsType<YoutubeExplodeService>(backends.Service);
        Assert.Null(backends.YtDlpRunner); // not used, so not checked for updates either
    }

    #region Finding yt-dlp.exe

    [Fact]
    public void Locator_FindsTheExeInTheAppFolder()
    {
        PutYtDlp();

        Assert.Equal(_app.File("yt-dlp.exe"), YtDlpLocator.Find("yt-dlp.exe", _app.Path, string.Empty));
        Assert.Equal(_app.File("yt-dlp.exe"), YtDlpLocator.Find("yt-dlp", _app.Path, string.Empty)); // without extension
    }

    [Fact]
    public void Locator_FindsARelativePath_ButDoesNotSearchPathForIt()
    {
        PutYtDlp(@"tools\yt-dlp.exe");
        PutYtDlp(@"tools\other.exe", _elsewhere);

        Assert.Equal(_app.File(@"tools\yt-dlp.exe"), YtDlpLocator.Find(@"tools\yt-dlp.exe", _app.Path, string.Empty));
        Assert.Null(YtDlpLocator.Find(@"tools\other.exe", _app.Path, _elsewhere.Path));
    }

    [Fact]
    public void Locator_FindsABareNameOnPath()
    {
        PutYtDlp(folder: _elsewhere);

        Assert.Equal(_elsewhere.File("yt-dlp.exe"),
            YtDlpLocator.Find("yt-dlp.exe", _app.Path, $"C:\\does-not-exist;\"{_elsewhere.Path}\""));
    }

    [Fact]
    public void Locator_TakesAnAbsolutePathAsItIs()
    {
        PutYtDlp(folder: _elsewhere);

        Assert.Equal(_elsewhere.File("yt-dlp.exe"), YtDlpLocator.Find(_elsewhere.File("yt-dlp.exe"), _app.Path, string.Empty));
        Assert.Null(YtDlpLocator.Find(_elsewhere.File("missing.exe"), _app.Path, _elsewhere.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("in<valid>.exe")]
    public void Locator_FindsNothingForNonsense(string path)
    {
        Assert.Null(YtDlpLocator.Find(path, _app.Path, string.Empty));
    }

    #endregion
}
