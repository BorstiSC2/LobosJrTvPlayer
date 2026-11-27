using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class FallbackYouTubeServiceTests
{
    private readonly IYouTubeService _first = Substitute.For<IYouTubeService>();
    private readonly IYouTubeService _second = Substitute.For<IYouTubeService>();
    private readonly FakeTimeProvider _time = new();

    private static readonly ResolvedVideo FromSecond = new("Second", TimeSpan.FromMinutes(1), "second.video", "second.audio");

    private FallbackYouTubeService Service() => new(_first, "first", _second, "second", _time);

    [Fact]
    public async Task WhatTheFirstFinds_IsUsed_WithoutAskingTheSecond()
    {
        var fromFirst = new ResolvedVideo("First", null, "first.video", null);
        _first.ResolveAsync("v", Arg.Any<CancellationToken>()).Returns(fromFirst);

        Assert.Same(fromFirst, await Service().ResolveAsync("v", CancellationToken.None));
        await _second.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task WhenTheFirstFails_TheSecondIsAsked()
    {
        _first.ResolveAsync("v", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Failed to extract the video author."));
        _second.ResolveAsync("v", Arg.Any<CancellationToken>()).Returns(FromSecond);

        Assert.Same(FromSecond, await Service().ResolveAsync("v", CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheFirstThrowsAtOnce_TheSecondIsAskedToo()
    {
        _first.GetVideoAsync("v", Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("broken"));
        _second.GetVideoAsync("v", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoDetails("Second", null));

        Assert.Equal("Second", (await Service().GetVideoAsync("v", CancellationToken.None)).Title);
    }

    [Fact]
    public async Task AFirstThatHangs_IsGivenUp_AfterItsTimeLimit()
    {
        var abandoned = new TaskCompletionSource<ResolvedVideo>(); // ignores its cancellation and never answers
        _first.ResolveAsync("v", Arg.Any<CancellationToken>()).Returns(abandoned.Task);
        _second.ResolveAsync("v", Arg.Any<CancellationToken>()).Returns(FromSecond);

        var lookup = Service().ResolveAsync("v", CancellationToken.None);
        _time.Advance(FallbackYouTubeService.PrimaryTimeLimit - TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.False(lookup.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(FromSecond, await lookup);
        Assert.True(FallbackYouTubeService.PrimaryTimeLimit < VideoLookup.TimeLimit - TimeSpan.FromSeconds(15),
            "The second backend must have time left before the lookup's own limit.");
    }

    [Fact]
    public async Task PlaylistDownloads_HaveNoTimeLimit()
    {
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        _first.GetPlaylistVideoIdsAsync("PL", Arg.Any<CancellationToken>()).Returns(download.Task);

        var ids = Service().GetPlaylistVideoIdsAsync("PL", CancellationToken.None);
        _time.Advance(FallbackYouTubeService.PrimaryTimeLimit * 4);
        await Task.Delay(20);
        download.SetResult(new[] { "a", "b" });

        Assert.Equal(new[] { "a", "b" }, await ids);
        await _second.DidNotReceiveWithAnyArgs().GetPlaylistVideoIdsAsync(default!, default);
    }

    [Fact]
    public async Task AnEmptyPlaylist_IsAskedForAgain_WithTheSecond()
    {
        _first.GetPlaylistVideoIdsAsync("PL", Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _second.GetPlaylistVideoIdsAsync("PL", Arg.Any<CancellationToken>()).Returns(new[] { "a" });

        Assert.Equal(new[] { "a" }, await Service().GetPlaylistVideoIdsAsync("PL", CancellationToken.None));
    }

    [Fact]
    public async Task ACancellationByTheCaller_IsNotAFailure()
    {
        _first.ResolveAsync("v", Arg.Any<CancellationToken>()).Returns(call =>
            Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith(_ => FromSecond, TaskContinuationOptions.OnlyOnRanToCompletion));
        using var cancellation = new CancellationTokenSource();

        var lookup = Service().ResolveAsync("v", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        await _second.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task WhenBothFail_TheErrorNamesBoth()
    {
        var firstError = new HttpRequestException("403");
        _first.GetPlaylistTitleAsync("PL", Arg.Any<CancellationToken>()).ThrowsAsync(firstError);
        _second.GetPlaylistTitleAsync("PL", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("private"));

        var error = await Assert.ThrowsAsync<YouTubeLookupException>(() => Service().GetPlaylistTitleAsync("PL", CancellationToken.None));

        Assert.Equal("first: 403 second: private", error.Message);
        Assert.Same(firstError, error.InnerException);
        Assert.Equal("private", error.SecondaryFailure.Message);
    }
}

[Collection(LoggerCollection.Name)]
public class FallbackYouTubeServiceLogTests
{
    [Fact]
    public async Task TheFailureOfTheFirst_IsLogged_InOneLine()
    {
        using var log = new RedirectedLog();
        var first = Substitute.For<IYouTubeService>();
        var second = Substitute.For<IYouTubeService>();
        first.GetPlaylistVideoIdsAsync("PL", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Failed to extract the video author."));
        second.GetPlaylistVideoIdsAsync("PL", Arg.Any<CancellationToken>()).Returns(new[] { "a" });

        await new FallbackYouTubeService(first, "YoutubeExplode", second, "yt-dlp").GetPlaylistVideoIdsAsync("PL", CancellationToken.None);

        Assert.Contains(
            "YoutubeExplode failed for playlist 'PL' (InvalidOperationException: Failed to extract the video author.); trying yt-dlp.",
            log.Content);
    }
}
