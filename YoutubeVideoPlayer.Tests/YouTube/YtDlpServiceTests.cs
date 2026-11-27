using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class YtDlpServiceTests
{
    /// <summary>
    /// A trimmed <c>yt-dlp -J</c> answer for a video with dubbed audio tracks: storyboards, HLS, the original English track
    /// with and without dynamic range compression, a German dub, H.264, VP9, HDR VP9, a maybe-DRM AV1 stream and the
    /// small muxed format 18.
    /// </summary>
    private const string DubbedVideo = """
        {
          "id": "BepBWodKptk",
          "title": "Wolf pack – \"winter\" ä",
          "duration": 754.0,
          "is_live": false,
          "formats": [
            { "format_id": "sb0", "format_note": "storyboard", "url": "https://i.ytimg.com/sb/0", "protocol": "mhtml", "vcodec": "none", "acodec": "none" },
            { "format_id": "233", "format_note": "Default, low", "url": "https://manifest.example/233.m3u8", "protocol": "m3u8_native", "vcodec": "none", "acodec": "unknown" },
            { "format_id": "251-drc", "format_note": "English (US) original (default), medium, DRC", "url": "https://media.example/251-drc", "protocol": "https",
              "vcodec": "none", "acodec": "opus", "tbr": 140.5, "language": "en-US", "language_preference": 10, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "251-0", "format_note": "German, medium", "url": "https://media.example/251-de", "protocol": "https",
              "vcodec": "none", "acodec": "opus", "tbr": 160.0, "language": "de", "language_preference": -1, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "251-1", "format_note": "English (US) original (default), medium", "url": "https://media.example/251-en?expire=1800000000", "protocol": "https",
              "vcodec": "none", "acodec": "opus", "tbr": 130.1, "language": "en-US", "language_preference": 10, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "140-1", "format_note": "English (US) original (default), medium", "url": "https://media.example/140-en", "protocol": "https",
              "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 129.5, "language": "en-US", "language_preference": 10, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "137", "format_note": "1080p", "url": "https://media.example/137?expire=1800000000", "protocol": "https",
              "vcodec": "avc1.640028", "acodec": "none", "height": 1080, "tbr": 4400.5, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "248", "format_note": "1080p", "url": "https://media.example/248", "protocol": "https",
              "vcodec": "vp09.00.40.08", "acodec": "none", "height": 1080, "tbr": 2600, "http_headers": { "User-Agent": "UA-1" } },
            { "format_id": "337", "format_note": "2160p60 HDR", "url": "https://media.example/337", "protocol": "https",
              "vcodec": "vp09.02.51.10.01.09.16.09.00", "acodec": "none", "height": 2160, "tbr": 20000 },
            { "format_id": "616", "format_note": "1080p Premium", "url": "https://manifest.example/616.m3u8", "protocol": "m3u8_native",
              "vcodec": "vp09.00.40.08", "acodec": "none", "height": 1080, "tbr": 5000 },
            { "format_id": "399", "format_note": "1080p", "url": "https://media.example/399", "protocol": "https",
              "vcodec": "av01.0.08M.08", "acodec": "none", "height": 1080, "tbr": 9000, "has_drm": "maybe" },
            { "format_id": "18", "format_note": "360p", "url": "https://media.example/18", "protocol": "https",
              "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "tbr": 500 }
          ]
        }
        """;

    private static YtDlpService Service(FakeYtDlp runner, IReadOnlyList<string>? extraArguments = null, TimeProvider? time = null) =>
        new(runner, extraArguments, time);

    #region Videos

    [Fact]
    public async Task Resolve_PlaysTheBestVideoWithTheEnglishOriginalTrack()
    {
        var runner = new FakeYtDlp().Prints(DubbedVideo);

        var video = await Service(runner).ResolveAsync("BepBWodKptk", CancellationToken.None);

        Assert.Equal("Wolf pack – \"winter\" ä", video.Title);
        Assert.Equal(TimeSpan.FromSeconds(754), video.Duration);
        Assert.Equal("https://media.example/137?expire=1800000000", video.VideoUrl); // H.264 1080p, not the AV1 that may be DRM
        Assert.Equal("https://media.example/251-en?expire=1800000000", video.AudioUrl); // not the DRC variant, not the German dub
        Assert.Equal("UA-1", video.UserAgent);
    }

    [Fact]
    public async Task Resolve_AsksForTheVideoAlone_WithTheExtraArgumentsBeforeTheUrl()
    {
        var runner = new FakeYtDlp().Prints(DubbedVideo);

        await Service(runner, new[] { "--cookies-from-browser", "firefox" }).ResolveAsync("-abc", CancellationToken.None);

        Assert.Equal(
            new[] { "-J", "--ignore-config", "--no-playlist", "--cookies-from-browser", "firefox", "--", "https://www.youtube.com/watch?v=-abc" },
            Assert.Single(runner.Runs));
    }

    [Fact]
    public async Task Resolve_TakesTheAudioFromThePlayerClientOfTheVideo()
    {
        // VLC fetches the audio with the video's User-Agent, so a track tied to another client is left out.
        var runner = new FakeYtDlp().Prints("""
            {
              "title": "t", "duration": 60,
              "formats": [
                { "format_id": "137", "url": "https://media.example/video", "protocol": "https", "vcodec": "avc1.640028", "acodec": "none",
                  "height": 1080, "tbr": 4000, "http_headers": { "User-Agent": "TV" } },
                { "format_id": "251", "url": "https://media.example/web-audio", "protocol": "https", "vcodec": "none", "acodec": "opus",
                  "tbr": 160, "http_headers": { "User-Agent": "WEB" } },
                { "format_id": "140", "url": "https://media.example/tv-audio", "protocol": "https", "vcodec": "none", "acodec": "mp4a.40.2",
                  "tbr": 129, "http_headers": { "user-agent": "TV" } }
              ]
            }
            """);

        var video = await Service(runner).ResolveAsync("v", CancellationToken.None);

        Assert.Equal("https://media.example/tv-audio", video.AudioUrl);
        Assert.Equal("TV", video.UserAgent);
    }

    [Fact]
    public async Task Resolve_WithoutSeparateStreams_PlaysTheBestMuxedOne_AlsoHls()
    {
        var runner = new FakeYtDlp().Prints("""
            {
              "title": "t", "duration": 60,
              "formats": [
                { "format_id": "18", "url": "https://media.example/18", "protocol": "https", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "tbr": 500 },
                { "format_id": "95", "url": "https://manifest.example/95.m3u8", "protocol": "m3u8_native", "vcodec": "avc1.4D401F", "acodec": "mp4a.40.2", "height": 720, "tbr": 2500 }
              ]
            }
            """);

        var video = await Service(runner).ResolveAsync("v", CancellationToken.None);

        Assert.Equal("https://manifest.example/95.m3u8", video.VideoUrl);
        Assert.Null(video.AudioUrl);
    }

    [Fact]
    public async Task Resolve_WithoutAnyPlayableStream_Fails()
    {
        var runner = new FakeYtDlp().Prints("""
            { "title": "t", "formats": [ { "format_id": "sb0", "url": "https://i.ytimg.com/sb", "protocol": "mhtml", "vcodec": "none", "acodec": "none" } ] }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).ResolveAsync("v", CancellationToken.None));
    }

    /// <summary>A video with one video and one audio stream that YouTube serves from the given times (Unix seconds).</summary>
    private static string AvailableLater(double videoFrom, double audioFrom) => FormattableString.Invariant($$"""
        {
          "title": "t", "duration": 60,
          "formats": [
            { "format_id": "137", "url": "https://media.example/video", "protocol": "https", "vcodec": "avc1.640028", "acodec": "none",
              "height": 1080, "tbr": 4000, "available_at": {{videoFrom}} },
            { "format_id": "140", "url": "https://media.example/audio", "protocol": "https", "vcodec": "none", "acodec": "mp4a.40.2",
              "tbr": 129, "available_at": {{audioFrom}} }
          ]
        }
        """);

    [Fact]
    public async Task Resolve_WaitsUntilYouTubeServesBothStreams()
    {
        // YouTube answers 403 until then (measured: 6 s after the lookup), and VLC does not try again.
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var runner = new FakeYtDlp().Prints(AvailableLater(1_800_000_004, 1_800_000_006.5));

        var lookup = Service(runner, time: time).ResolveAsync("v", CancellationToken.None);
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(20);
        Assert.False(lookup.IsCompleted, "The audio is not served yet.");

        time.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal("https://media.example/video", (await lookup).VideoUrl);
    }

    [Fact]
    public async Task Resolve_StreamsThatAreServedAlready_AreHandedOutAtOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var runner = new FakeYtDlp().Prints(AvailableLater(1_799_999_990, 1_800_000_000));

        var lookup = Service(runner, time: time).ResolveAsync("v", CancellationToken.None);

        Assert.True(lookup.IsCompleted);
        Assert.Equal("https://media.example/audio", (await lookup).AudioUrl);
    }

    [Fact]
    public async Task Resolve_WaitsForTheStreams_AtMost_MaxAvailabilityWait()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var runner = new FakeYtDlp().Prints(AvailableLater(1_800_000_000, 1_800_003_600));

        var lookup = Service(runner, time: time).ResolveAsync("v", CancellationToken.None);
        await Task.Delay(20);
        time.Advance(YtDlpService.MaxAvailabilityWait);

        Assert.NotNull((await lookup).AudioUrl);
    }

    [Fact]
    public async Task Resolve_CancelledWhileWaitingForTheStreams_IsACancellation()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var runner = new FakeYtDlp().Prints(AvailableLater(1_800_000_006, 1_800_000_006));
        using var cancellation = new CancellationTokenSource();

        var lookup = Service(runner, time: time).ResolveAsync("v", cancellation.Token);
        await Task.Delay(20);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    [Fact]
    public async Task GetVideo_ALiveStreamHasNoLength()
    {
        var runner = new FakeYtDlp().Prints("""{ "title": "Live now", "duration": null, "is_live": true, "formats": [] }""");

        var video = await Service(runner).GetVideoAsync("v", CancellationToken.None);

        Assert.Equal("Live now", video.Title);
        Assert.Null(video.Duration);
    }

    [Fact]
    public void OriginalTrack_WithoutOneMarked_TheDefaultTrackCountsAsTheVideosOwn()
    {
        var streams = YtDlpFormats.ToStreams(new[]
        {
            new YtDlpFormat { Url = "https://a/fr", Protocol = "https", VideoCodec = "none", AudioCodec = "opus", Language = "fr", LanguagePreference = 5 },
            new YtDlpFormat { Url = "https://a/es", Protocol = "https", VideoCodec = "none", AudioCodec = "opus", Language = "es", LanguagePreference = -1 },
            new YtDlpFormat { Url = "https://a/plain", Protocol = "https", VideoCodec = "none", AudioCodec = "opus" }
        });

        Assert.Equal(new bool?[] { true, false, null }, streams.Select(s => s.Candidate.IsDefaultAudio));
    }

    #endregion

    #region Failures

    [Fact]
    public async Task AFailedRun_ReportsYtDlpsLastError()
    {
        var runner = new FakeYtDlp
        {
            Respond = (_, _) => Task.FromResult(new YtDlpOutput(1, string.Empty,
                "WARNING: [youtube] abc: something\nERROR: [youtube] abc: Video unavailable. This video is private\n"))
        };

        var error = await Assert.ThrowsAsync<YtDlpException>(() => Service(runner).ResolveAsync("abc", CancellationToken.None));

        Assert.Equal("[youtube] abc: Video unavailable. This video is private", error.Message);
    }

    [Theory]
    [InlineData("Traceback (most recent call last):\n  ...\nKeyError: 'x'\n", "KeyError: 'x'")]
    [InlineData("", "yt-dlp ended with exit code 2.")]
    public void ErrorMessage_WithoutAnErrorLine(string standardError, string expected)
    {
        Assert.Equal(expected, YtDlpService.ErrorMessage(new YtDlpOutput(2, string.Empty, standardError)));
    }

    [Theory]
    [InlineData("this is not JSON")]
    [InlineData("")]
    public async Task OutputThatIsNotJson_Fails(string output)
    {
        var runner = new FakeYtDlp().Prints(output);

        await Assert.ThrowsAsync<YtDlpException>(() => Service(runner).GetVideoAsync("v", CancellationToken.None));
    }

    [Fact]
    public async Task ARunThatTakesTooLong_IsEnded_AndCountsAsATimeout()
    {
        var time = new FakeTimeProvider();
        var ended = false;
        var runner = new FakeYtDlp
        {
            Respond = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    ended = true;
                }

                return new YtDlpOutput(0, "{}", string.Empty);
            }
        };

        var download = Service(runner, time: time).GetPlaylistVideoIdsAsync("PL1", CancellationToken.None);
        time.Advance(YtDlpService.RunTimeLimit);

        await Assert.ThrowsAsync<TimeoutException>(() => download);
        Assert.True(ended);
    }

    [Fact]
    public async Task CancellingTheCaller_EndsTheRun_AsACancellation()
    {
        var runner = new FakeYtDlp
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new YtDlpOutput(0, "{}", string.Empty);
            }
        };
        using var cancellation = new CancellationTokenSource();

        var lookup = Service(runner).ResolveAsync("v", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    [Fact]
    public async Task Dispose_EndsTheRunsThatAreStillGoing()
    {
        var runner = new FakeYtDlp
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new YtDlpOutput(0, "{}", string.Empty);
            }
        };
        var service = Service(runner);

        var lookup = service.ResolveAsync("v", CancellationToken.None);
        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveAsync("w", CancellationToken.None));
    }

    [Fact]
    public async Task AtMost_MaxParallelRuns_RunAtOnce()
    {
        var running = 0;
        var mostAtOnce = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeYtDlp
        {
            Respond = async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref mostAtOnce, now);
                await release.Task;
                Interlocked.Decrement(ref running);
                return new YtDlpOutput(0, """{ "title": "t" }""", string.Empty);
            }
        };
        var service = Service(runner);

        var lookups = Enumerable.Range(0, YtDlpService.MaxParallelRuns + 2)
            .Select(i => service.GetVideoAsync("v" + i, CancellationToken.None))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref running) == YtDlpService.MaxParallelRuns);
        await Task.Delay(50); // a fourth run would start now if the limit did not hold
        Assert.Equal(YtDlpService.MaxParallelRuns, Volatile.Read(ref running));

        release.SetResult();
        await Task.WhenAll(lookups);

        Assert.Equal(YtDlpService.MaxParallelRuns, mostAtOnce);
        Assert.Equal(YtDlpService.MaxParallelRuns + 2, runner.Runs.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 10 seconds.");
            await Task.Delay(10);
        }
    }

    #endregion

    #region Playlists

    [Fact]
    public async Task PlaylistVideoIds_AreReadFromTheFlatList_InOrder()
    {
        var runner = new FakeYtDlp().Prints("""
            { "title": "EVERYTHING", "entries": [ { "id": "b" }, { "id": "a" }, null, { "id": "" }, { "id": "c" } ] }
            """);

        var ids = await Service(runner).GetPlaylistVideoIdsAsync("PL123", CancellationToken.None);

        Assert.Equal(new[] { "b", "a", "c" }, ids);
        Assert.Equal(new[] { "-J", "--ignore-config", "--flat-playlist", "--", "https://www.youtube.com/playlist?list=PL123" },
            Assert.Single(runner.Runs));
    }

    [Fact]
    public async Task PlaylistWithoutEntries_HasNoVideos()
    {
        var runner = new FakeYtDlp().Prints("""{ "title": "Empty" }""");

        Assert.Empty(await Service(runner).GetPlaylistVideoIdsAsync("PL123", CancellationToken.None));
    }

    [Fact]
    public async Task PlaylistTitle_OnlyAsksForTheFirstEntry()
    {
        var runner = new FakeYtDlp().Prints("""{ "title": "Queen's Domain", "entries": [ { "id": "a" } ] }""");

        var title = await Service(runner).GetPlaylistTitleAsync("PL123", CancellationToken.None);

        Assert.Equal("Queen's Domain", title);
        Assert.Equal(
            new[] { "-J", "--ignore-config", "--flat-playlist", "--playlist-items", "1", "--", "https://www.youtube.com/playlist?list=PL123" },
            Assert.Single(runner.Runs));
    }

    [Fact]
    public async Task PlaylistTitle_Missing_Fails()
    {
        var runner = new FakeYtDlp().Prints("""{ "entries": [] }""");

        await Assert.ThrowsAsync<YtDlpException>(() => Service(runner).GetPlaylistTitleAsync("PL123", CancellationToken.None));
    }

    #endregion

    #region Against real YouTube (yt-dlp.exe and the network needed)

    // A long-standing public video (the same as in YoutubeExplodeServiceTests).
    private const string VideoId = "0_twHqFV6Lo";

    private static YtDlpService RealService() => new(new YtDlpProcess(YtDlpIntegrationFactAttribute.Executable!));

    [YtDlpIntegrationFact]
    public async Task Real_Resolve_ReturnsPlayableStreams()
    {
        using var service = RealService();

        var video = await service.ResolveAsync(VideoId, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(video.Title));
        Assert.True(video.Duration > TimeSpan.Zero);
        Assert.StartsWith("http", video.VideoUrl);
        Assert.NotNull(StreamUrl.GetExpiry(video.VideoUrl));
    }

    [YtDlpIntegrationFact]
    public async Task Real_StreamsPlayInVlc_WithTheirUserAgent_AlsoFromTheMiddle()
    {
        // The video and its audio (input-slave) are fetched with yt-dlp's User-Agent; starting an hour in is what a resume does.
        LibVLCSharp.Shared.Core.Initialize();
        using var service = RealService();
        var video = await service.ResolveAsync(VideoId, CancellationToken.None);
        Assert.NotNull(video.AudioUrl);
        Assert.NotNull(video.UserAgent);

        var startAt = TimeSpan.FromHours(1);
        using var player = new YouTubeVideoPlayer.Playback.VlcMediaPlayer("--vout=dummy", "--aout=dummy");
        player.Play(video.VideoUrl, video.AudioUrl, startAt, video.UserAgent);

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!(player.IsPlaying && player.TimeMs > (startAt + TimeSpan.FromSeconds(5)).TotalMilliseconds) && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        var log = string.Join(Environment.NewLine, player.RecentLog());
        Assert.True(player.TimeMs > (startAt + TimeSpan.FromSeconds(5)).TotalMilliseconds,
            $"State {player.State}, position {player.TimeMs} ms. VLC said:{Environment.NewLine}{log}");
        Assert.True(player.MediaPlayer.AudioTrackCount > 0, "The audio input-slave was not opened.");
        Assert.DoesNotContain("403", log);
    }

    [YtDlpIntegrationFact]
    public async Task Real_ErrorsWithCharactersOutsideAscii_AreReadCorrectly()
    {
        // The standalone exe writes its messages in the Windows code page (YouTube's bot check: "you’re"); offline.
        var missing = Path.Combine(Path.GetTempPath(), "ytp-missing-ä’.txt");

        var output = await new YtDlpProcess(YtDlpIntegrationFactAttribute.Executable!).RunAsync(
            new[] { "--ignore-config", "--batch-file", missing }, CancellationToken.None);

        Assert.NotEqual(0, output.ExitCode);
        Assert.Contains("ytp-missing-ä’.txt", output.StandardError);
    }

    [YtDlpIntegrationFact]
    public async Task Real_Playlist_TitleAndVideoIds()
    {
        using var service = RealService();
        var channel = await new YtDlpProcess(YtDlpIntegrationFactAttribute.Executable!).RunAsync(
            new[] { "--print", "channel_id", "--no-playlist", "--ignore-config", "--", YouTubeLinks.Video(VideoId) }, CancellationToken.None);
        var uploadsPlaylist = "UU" + channel.StandardOutput.Trim()[2..];

        var title = await service.GetPlaylistTitleAsync(uploadsPlaylist, CancellationToken.None);
        var ids = await service.GetPlaylistVideoIdsAsync(uploadsPlaylist, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Contains(VideoId, ids);
    }

    #endregion
}

[Collection(LoggerCollection.Name)]
public class YtDlpServiceLogTests
{
    [Fact]
    public async Task AWarningThatComesWithEveryVideo_IsLoggedOnce_AndTheChosenStreamsNameTheBackend()
    {
        using var log = new RedirectedLog();
        var runner = new FakeYtDlp
        {
            Respond = (arguments, _) => Task.FromResult(new YtDlpOutput(0, """
                { "title": "t", "formats": [ { "format_id": "18", "url": "https://media.example/18", "protocol": "https", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "tbr": 500 } ] }
                """, $"WARNING: [youtube] {arguments[^1][^1]}: No supported JavaScript runtime could be found.\n" +
                     "WARNING: ffmpeg not found. The downloaded format may not be the best available.\n"))
        };
        using var service = new YtDlpService(runner);

        await service.ResolveAsync("a", CancellationToken.None);
        await service.ResolveAsync("b", CancellationToken.None);

        Assert.Single(log.Content.Split('\n'), line => line.Contains("No supported JavaScript runtime"));
        Assert.DoesNotContain("ffmpeg", log.Content); // only needed for downloads, which the app does not do
        Assert.Contains("Streams for 'a' (yt-dlp): video avc1.42001E 360p 500 kbit/s, no separate audio", log.Content);
    }
}
