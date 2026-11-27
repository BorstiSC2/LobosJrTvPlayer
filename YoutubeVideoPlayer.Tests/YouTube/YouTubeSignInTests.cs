using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;
using YouTubeVideoPlayer.YouTube.SignIn;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.YouTube;

public sealed class YouTubeSignInTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static readonly DateTimeOffset InTwoYears = DateTimeOffset.FromUnixTimeSeconds(1_860_000_000);

    private static readonly SignInCookie[] SignedIn =
    {
        new(".youtube.com", "/", "__Secure-3PSID", "secret-psid", Secure: true, HttpOnly: true, InTwoYears),
        new(".youtube.com", "/", "LOGIN_INFO", "secret-login", Secure: true, HttpOnly: true, InTwoYears),
        new(".youtube.com", "/", "PREF", "f6=40000000", Secure: true, HttpOnly: false, InTwoYears),
        new("www.youtube.com", "/", "YSC", "session-value", Secure: true, HttpOnly: true, Expires: null)
    };

    [Fact]
    public void TheCookies_AreWrittenInTheNetscapeFormat()
    {
        var lines = YouTubeSignIn.ToNetscapeFile(SignedIn).Split('\n');

        Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1860000000\t__Secure-3PSID\tsecret-psid", lines);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1860000000\tPREF\tf6=40000000", lines);
        Assert.Contains("#HttpOnly_www.youtube.com\tFALSE\t/\tTRUE\t0\tYSC\tsession-value", lines); // a session cookie expires with 0
    }

    [Fact]
    public void CookiesTheFormatCannotHold_AreLeftOut()
    {
        var text = YouTubeSignIn.ToNetscapeFile(new[]
        {
            new SignInCookie(".youtube.com", "/", "BAD", "a\tb", true, false, InTwoYears),
            new SignInCookie(".youtube.com", "/", "", "x", true, false, InTwoYears),
            new SignInCookie(".youtube.com", "", "GOOD", "x", true, false, InTwoYears)
        });

        Assert.DoesNotContain("BAD", text);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1860000000\tGOOD\tx", text); // an empty path is the root
    }

    [Fact]
    public void OnlyTheCookiesOfAGoogleAccount_CountAsSignedIn()
    {
        Assert.True(YouTubeSignIn.IsSignedIn(SignedIn));
        Assert.False(YouTubeSignIn.IsSignedIn(SignedIn.Where(c => c.Name is "PREF" or "YSC")));
        Assert.False(YouTubeSignIn.IsSignedIn(new[] { SignedIn[0] with { Value = "" } }));
    }

    [Fact]
    public void SaveCookies_CreatesTheFolder_AndReplacesAnEarlierFile()
    {
        var path = Path.Combine(_folder.Path, "sub", "youtube-cookies.txt");

        YouTubeSignIn.SaveCookies(SignedIn, path);
        YouTubeSignIn.SaveCookies(SignedIn.Take(1), path);

        Assert.Contains("__Secure-3PSID", File.ReadAllText(path));
        Assert.DoesNotContain("LOGIN_INFO", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void TheSignIn_IsKeptInTheWindowsProfile_NotInTheAppFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(local, YouTubeSignIn.CookiesFile);
        Assert.StartsWith(local, YouTubeSignIn.ProfileFolder);
        Assert.False(YouTubeSignIn.CookiesFile.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    #region yt-dlp gets the sign-in

    private string CookiesFile => _folder.File("youtube-cookies.txt");

    [Fact]
    public async Task WhileASignInIsSaved_EveryRunGetsACopyOfIt_WhichIsDeletedAfterwards()
    {
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);
        string? copy = null;
        var copyExistedDuringRun = false;
        var runner = new FakeYtDlp
        {
            Respond = (arguments, _) =>
            {
                copy = arguments[arguments.ToList().IndexOf("--cookies") + 1];
                copyExistedDuringRun = File.Exists(copy) && File.ReadAllText(copy) == File.ReadAllText(CookiesFile);
                return Task.FromResult(new YtDlpOutput(0, """{ "title": "t" }""", string.Empty));
            }
        };
        using var service = new YtDlpService(runner, new[] { "--extra" }, cookiesFile: CookiesFile);

        await service.GetVideoAsync("v", CancellationToken.None);

        var arguments = Assert.Single(runner.Runs);
        Assert.Equal(new[] { "-J", "--ignore-config", "--no-playlist", "--cookies", copy!, "--extra", "--", "https://www.youtube.com/watch?v=v" }, arguments);
        Assert.NotEqual(CookiesFile, copy); // yt-dlp writes the file back when it ends; parallel runs must not share one
        Assert.True(copyExistedDuringRun);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(CookiesFile));
    }

    [Fact]
    public async Task WithoutASavedSignIn_RunsGetNoCookies_AndASignInMadeLaterIsUsedFromTheNextRunOn()
    {
        var runner = new FakeYtDlp().Prints("""{ "title": "t" }""");
        using var service = new YtDlpService(runner, cookiesFile: CookiesFile);

        await service.GetVideoAsync("a", CancellationToken.None);
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);
        await service.GetVideoAsync("b", CancellationToken.None);

        Assert.DoesNotContain("--cookies", runner.Runs[0]);
        Assert.Contains("--cookies", runner.Runs[1]);
    }

    [Fact]
    public void LeftoverCopies_AreDeleted_ButNotTheSignIn()
    {
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);
        File.WriteAllText(_folder.File("run-0123.cookies.txt"), "left behind");

        YtDlpService.DeleteLeftoverCookieCopies(CookiesFile);

        Assert.False(File.Exists(_folder.File("run-0123.cookies.txt")));
        Assert.True(File.Exists(CookiesFile));
    }

    [Theory]
    [InlineData("--cookies-from-browser", "firefox")]
    [InlineData("--cookies", "my-cookies.txt")]
    public async Task CookiesOfTheUsersOwnInTheSettings_WinOverTheSavedSignIn(string option, string value)
    {
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);
        File.WriteAllText(_folder.File("yt-dlp.exe"), string.Empty);
        var runner = new FakeYtDlp().Prints("""{ "title": "t" }""");
        var settings = new AppSettings.YouTubeSettings
        {
            SelectedBackend = YouTubeBackend.YtDlp,
            Fallback = false,
            YtDlp = new() { Path = "yt-dlp.exe", ExtraArguments = new() { option, value } }
        };
        using var backends = YouTubeBackends.Create(settings, _folder.Path, pathVariable: string.Empty, createRunner: _ => runner, cookiesFile: CookiesFile);

        await backends.Service.GetVideoAsync("v", CancellationToken.None);

        var arguments = Assert.Single(runner.Runs);
        Assert.Single(arguments, a => a.StartsWith("--cookies", StringComparison.Ordinal));
        Assert.Contains(value, arguments);
    }

    [Fact]
    public async Task TheBackends_PassTheSavedSignInToYtDlp()
    {
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);
        File.WriteAllText(_folder.File("yt-dlp.exe"), string.Empty);
        var runner = new FakeYtDlp().Prints("""{ "title": "t" }""");
        var settings = new AppSettings.YouTubeSettings { SelectedBackend = YouTubeBackend.YtDlp, Fallback = false };
        using var backends = YouTubeBackends.Create(settings, _folder.Path, pathVariable: string.Empty, createRunner: _ => runner, cookiesFile: CookiesFile);

        await backends.Service.GetVideoAsync("v", CancellationToken.None);

        Assert.Contains("--cookies", Assert.Single(runner.Runs));
    }

    [Theory]
    [InlineData(1, "ERROR: [youtube] abc: Sign in to confirm you’re not a bot. This helps protect our community.", "asks this connection to sign in")]
    [InlineData(0, "WARNING: [youtube] The provided YouTube account cookies are no longer valid. They have likely been rotated.", "no longer works")]
    public async Task WhenYouTubeAsksForASignIn_YtDlpSaysSo(int exitCode, string standardError, string reason)
    {
        var runner = new FakeYtDlp { Respond = (_, _) => Task.FromResult(new YtDlpOutput(exitCode, """{ "title": "t" }""", standardError)) };
        using var service = new YtDlpService(runner, cookiesFile: CookiesFile);
        var reasons = new List<string>();
        service.SignInRequested += (_, r) => reasons.Add(r);

        try
        {
            await service.GetVideoAsync("abc", CancellationToken.None);
        }
        catch (YtDlpException)
        {
            // the bot check fails the lookup
        }

        Assert.Contains(reason, Assert.Single(reasons));
    }

    [Fact]
    public async Task OtherFailures_AskForNoSignIn()
    {
        var runner = new FakeYtDlp { Respond = (_, _) => Task.FromResult(new YtDlpOutput(1, string.Empty, "ERROR: [youtube] abc: Video unavailable")) };
        using var service = new YtDlpService(runner, cookiesFile: CookiesFile);
        var asked = false;
        service.SignInRequested += (_, _) => asked = true;

        await Assert.ThrowsAsync<YtDlpException>(() => service.GetVideoAsync("abc", CancellationToken.None));

        Assert.False(asked);
    }

    [Fact]
    public async Task TheBackends_OfferTheSignIn_AndPassOnYouTubesRequest()
    {
        File.WriteAllText(_folder.File("yt-dlp.exe"), string.Empty);
        var runner = new FakeYtDlp { Respond = (_, _) => Task.FromResult(new YtDlpOutput(1, string.Empty, "ERROR: Sign in to confirm you're not a bot")) };
        var settings = new AppSettings.YouTubeSettings { SelectedBackend = YouTubeBackend.YtDlp, Fallback = false };
        using var backends = YouTubeBackends.Create(settings, _folder.Path, pathVariable: string.Empty, createRunner: _ => runner, cookiesFile: CookiesFile);
        var reasons = new List<string>();
        backends.SignInRequested += (_, r) => reasons.Add(r);

        await Assert.ThrowsAsync<YtDlpException>(() => backends.Service.GetVideoAsync("v", CancellationToken.None));

        Assert.True(backends.OffersSignIn);
        Assert.Single(reasons);
    }

    [Fact]
    public void WithCookiesOfTheUsersOwn_OrWithoutYtDlp_TheSignInIsNotOffered()
    {
        File.WriteAllText(_folder.File("yt-dlp.exe"), string.Empty);
        var own = new AppSettings.YouTubeSettings
        {
            SelectedBackend = YouTubeBackend.YtDlp,
            YtDlp = new() { Path = "yt-dlp.exe", ExtraArguments = new() { "--cookies-from-browser", "firefox" } }
        };
        var withoutYtDlp = new AppSettings.YouTubeSettings { SelectedBackend = YouTubeBackend.YoutubeExplode, Fallback = false };

        using var ownCookies = YouTubeBackends.Create(own, _folder.Path, pathVariable: string.Empty, createRunner: _ => new FakeYtDlp(), cookiesFile: CookiesFile);
        using var explodeOnly = YouTubeBackends.Create(withoutYtDlp, _folder.Path, pathVariable: string.Empty, cookiesFile: CookiesFile);

        Assert.False(ownCookies.OffersSignIn);
        Assert.False(explodeOnly.OffersSignIn);
    }

    #endregion

    [YtDlpIntegrationFact]
    public async Task Real_YtDlpReadsTheFile_HttpOnlyAndSessionCookiesIncluded()
    {
        // Offline: yt-dlp loads the file, fails to reach a made-up host, and writes the file back with what it understood.
        YouTubeSignIn.SaveCookies(SignedIn, CookiesFile);

        var output = await new YtDlpProcess(YtDlpIntegrationFactAttribute.Executable!).RunAsync(
            new[] { "--ignore-config", "--cookies", CookiesFile, "--socket-timeout", "5", "-J", "https://ytp-test.invalid/" },
            CancellationToken.None);

        Assert.DoesNotContain("cookie", output.StandardError, StringComparison.OrdinalIgnoreCase);
        var saved = File.ReadAllText(CookiesFile);
        foreach (var cookie in SignedIn)
            Assert.Contains($"\t{cookie.Name}\t{cookie.Value}", saved);
    }
}
