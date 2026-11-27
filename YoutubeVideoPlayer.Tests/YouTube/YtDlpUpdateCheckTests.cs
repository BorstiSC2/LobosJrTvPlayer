using System.Net;
using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class YtDlpUpdateCheckTests
{
    private readonly FakeHttpHandler _github = new();
    private readonly FakeYtDlp _ytDlp = new(@"C:\Player\yt-dlp.exe");
    private readonly FakeTimeProvider _time = new();

    private void Installed(string version) =>
        _ytDlp.Respond = (_, _) => Task.FromResult(new YtDlpOutput(0, version + "\n", string.Empty));

    private void Latest(string tag) =>
        _github.Respond = _ => (HttpStatusCode.OK, $$"""{ "tag_name": "{{tag}}", "name": "yt-dlp {{tag}}" }""");

    private Task<string?> CheckAsync() =>
        YtDlpUpdateCheck.CheckAsync(_ytDlp, new HttpClient(_github), _time, CancellationToken.None);

    [Fact]
    public async Task AnOlderInstalledVersion_GivesANotice_ThatNamesBothVersions_AndHowToUpdate()
    {
        Installed("2026.10.26");
        Latest("2026.11.23");

        var notice = await CheckAsync();

        Assert.NotNull(notice);
        Assert.Contains("2026.11.23", notice);
        Assert.Contains("2026.10.26", notice);
        Assert.Contains("yt-dlp -U", notice);
        Assert.Contains(@"C:\Player", notice);
        Assert.Equal(new[] { "--version" }, Assert.Single(_ytDlp.Runs));
    }

    [Fact]
    public async Task AsksGitHubForTheLatestRelease_WithAUserAgent()
    {
        Installed("2026.11.23");
        Latest("2026.11.23");

        await CheckAsync();

        var request = Assert.Single(_github.Requests);
        Assert.Equal("/repos/yt-dlp/yt-dlp/releases/latest", request.Path);
        Assert.False(string.IsNullOrWhiteSpace(request.UserAgent));
    }

    [Theory]
    [InlineData("2026.11.23", "2026.11.23")]
    [InlineData("2026.11.25.232953", "2026.11.23")] // a nightly build newer than the latest release
    [InlineData("2026.12.01", "2026.11.23")]
    public async Task ACurrentVersion_GivesNoNotice(string installed, string latest)
    {
        Installed(installed);
        Latest(latest);

        Assert.Null(await CheckAsync());
    }

    [Fact]
    public async Task AnOlderNightly_GivesANotice()
    {
        Installed("2026.11.20.101010");
        Latest("2026.11.23");

        Assert.NotNull(await CheckAsync());
    }

    [Fact]
    public async Task GitHubAnsweringWithAnError_GivesNoNotice()
    {
        Installed("2026.01.01");
        _github.Respond = _ => (HttpStatusCode.Forbidden, """{ "message": "API rate limit exceeded" }""");

        Assert.Null(await CheckAsync());
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    public async Task AReleaseThatIsNoVersion_GivesNoNotice(string tag)
    {
        Installed("2026.01.01");
        Latest(tag);

        Assert.Null(await CheckAsync());
    }

    [Fact]
    public async Task AYtDlpThatFails_GivesNoNotice()
    {
        _ytDlp.Respond = (_, _) => Task.FromResult(new YtDlpOutput(1, string.Empty, "ERROR: broken"));
        Latest("2026.11.23");

        Assert.Null(await CheckAsync());
    }

    [Fact]
    public async Task ACheckWithoutAnswer_EndsAfterItsTimeLimit_WithoutANotice()
    {
        _ytDlp.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new YtDlpOutput(0, "2026.01.01", string.Empty);
        };
        Latest("2026.11.23");

        var check = CheckAsync();
        _time.Advance(YtDlpUpdateCheck.TimeLimit);

        Assert.Null(await check);
    }

    [Theory]
    [InlineData("2026.11.23", "2026.11.23", 0)]
    [InlineData("2026.11.23", "2026.11.23.0", 0)]
    [InlineData("2026.11.3", "2026.11.23", -1)]
    [InlineData("v2026.12.01", "2026.11.23", 1)]
    public void Versions_AreComparedPartByPart(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(YtDlpUpdateCheck.Compare(YtDlpUpdateCheck.Parse(a)!, YtDlpUpdateCheck.Parse(b)!)));
    }
}
