using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class VideoLookupTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly IYouTubeService _youtube = Substitute.For<IYouTubeService>();
    private CancellationToken _requestToken;

    /// <summary>A lookup that never answers (and ignores its cancellation).</summary>
    private void Hang() =>
        _youtube.ResolveAsync("v1", Arg.Do<CancellationToken>(t => _requestToken = t))
            .Returns(new TaskCompletionSource<ResolvedVideo>().Task);

    [Fact]
    public void ALookupThatHangs_FailsAfterTheTimeLimit_AndIsCancelled() => UiContext.Run(async () =>
    {
        Hang();
        var lookup = VideoLookup.ResolveAsync(_youtube, "v1", _time, CancellationToken.None);

        _time.Advance(VideoLookup.TimeLimit - TimeSpan.FromSeconds(1));
        await Async.SettleAsync();
        Assert.False(lookup.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => lookup);

        Assert.Contains("v1", error.Message);
        Assert.True(_requestToken.IsCancellationRequested);
    });

    [Fact]
    public void ALookupInTime_ReturnsItsResult() => UiContext.Run(async () =>
    {
        var answer = new TaskCompletionSource<ResolvedVideo>();
        _youtube.ResolveAsync("v1", Arg.Any<CancellationToken>()).Returns(answer.Task);
        var lookup = VideoLookup.ResolveAsync(_youtube, "v1", _time, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(30));
        answer.SetResult(PlaybackSetup.Resolved("v1"));

        Assert.Equal("v1.video", (await lookup).VideoUrl);
    });

    [Fact]
    public void CancellingTheCaller_IsACancellation_NotATimeout() => UiContext.Run(async () =>
    {
        Hang();
        using var cancel = new CancellationTokenSource();
        var lookup = VideoLookup.ResolveAsync(_youtube, "v1", _time, cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.True(_requestToken.IsCancellationRequested);
    });
}
