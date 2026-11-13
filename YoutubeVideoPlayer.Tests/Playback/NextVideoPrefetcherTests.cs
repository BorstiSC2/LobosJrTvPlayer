using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Playback;

public class NextVideoPrefetcherTests
{
    /// <summary>A prefetcher on a video source that offers "n1", "n2", ... and a YouTube that resolves everything.</summary>
    private sealed class Setup : IDisposable
    {
        private int _next;

        public Setup()
        {
            Source.TryGetNextVideoId(null, out _).ReturnsForAnyArgs(call =>
            {
                Requests.Add((string?)call[0]);
                call[1] = "n" + ++_next;
                return true;
            });
            YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Resolve(call.ArgAt<string>(0))));
            Prefetcher = new NextVideoPrefetcher(YouTube, Source, () => Queued, Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

        public IVideoSource Source { get; } = Substitute.For<IVideoSource>();

        public NextVideoPrefetcher Prefetcher { get; }

        public string? Queued { get; set; }

        /// <summary>What the prefetcher asked the source for ("the video that follows ...").</summary>
        public List<string?> Requests { get; } = new();

        /// <summary>URLs with an expiry time in them, like the ones from YouTube.</summary>
        public Func<string, ResolvedVideo> Resolve { get; set; } = id => new ResolvedVideo("Title " + id, TimeSpan.FromMinutes(3), id + ".video", id + ".audio");

        public string UrlExpiringIn(TimeSpan span) => $"https://stream.example/video?expire={Time.GetUtcNow().Add(span).ToUnixTimeSeconds()}&x=1";

        public async Task WaitForPreparationAsync()
        {
            await Async.SettleAsync();
            Time.Advance(NextVideoPrefetcher.Delay);
            await Async.SettleAsync();
        }

        public void Dispose() => Prefetcher.Dispose();
    }

    [Fact]
    public void ThePreparedVideoIsHandedOverOnce_WithItsStreams() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();
        var prepared = setup.Prefetcher.Take("v1", null);

        Assert.NotNull(prepared);
        Assert.Equal("n1", prepared.VideoId);
        Assert.Equal("n1.video", (await prepared.Video).VideoUrl);
        Assert.Equal(new string?[] { "v1" }, setup.Requests); // asked for the video that follows v1
        Assert.Null(setup.Prefetcher.Take("v1", null));       // it belongs to the caller now
    });

    [Fact]
    public void NothingIsChosenOrLookedUpBeforeTheDelayIsOver_AndTakeDropsThePreparation() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        setup.Prefetcher.Start("v1");
        await Async.SettleAsync();
        setup.Time.Advance(NextVideoPrefetcher.Delay - TimeSpan.FromMilliseconds(1));
        await Async.SettleAsync();
        Assert.Null(setup.Prefetcher.Take("v1", null)); // the choice has not been made yet

        setup.Time.Advance(TimeSpan.FromSeconds(10));
        await Async.SettleAsync();
        Assert.Empty(setup.Requests); // and it is not made later either
        await setup.YouTube.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    });

    [Fact]
    public void AStartForAnotherVideoReplacesThePreparation() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        setup.Prefetcher.Start("a");
        setup.Prefetcher.Start("b");
        await setup.WaitForPreparationAsync();

        Assert.Equal(new string?[] { "b" }, setup.Requests);
        Assert.Null(setup.Prefetcher.Take("a", null)); // prepared for the wrong video
    });

    [Fact]
    public void ThePreparationIsOnlyUsableForTheVideoItWasMadeFor() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        Assert.Null(setup.Prefetcher.Take("other", null));
        Assert.Null(setup.Prefetcher.Take("v1", null)); // and a miss drops it
    });

    [Fact]
    public void AQueuedVideoIsPreparedInsteadOfAskingTheSource() => UiContext.Run(async () =>
    {
        using var setup = new Setup { Queued = "winner" };
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        var prepared = setup.Prefetcher.Take("v1", "winner");

        Assert.Equal("winner", prepared!.VideoId);
        Assert.Empty(setup.Requests);
    });

    [Fact]
    public void AQueuedVideoThatIsNotTheOnePrepared_MakesThePreparationUnusable() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        Assert.Null(setup.Prefetcher.Take("v1", "winner")); // the winner has to be looked up by the caller
    });

    [Fact]
    public void AVideoThatIsStillBeingLookedUp_IsHandedOverToo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var pending = new TaskCompletionSource<ResolvedVideo>();
        setup.YouTube.ResolveAsync("n1", Arg.Any<CancellationToken>()).Returns(pending.Task);
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        var prepared = setup.Prefetcher.Take("v1", null);

        Assert.NotNull(prepared);
        Assert.False(prepared.Video.IsCompleted);
        pending.SetResult(setup.Resolve("n1")); // the caller waits for it instead of starting a second lookup
        Assert.Equal("n1.video", (await prepared.Video).VideoUrl);
    });

    [Fact]
    public void AFailedLookup_IsNotHandedOver() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.YouTube.ResolveAsync("n1", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("unavailable"));
        setup.Prefetcher.Start("v1");

        await setup.WaitForPreparationAsync();

        Assert.Null(setup.Prefetcher.Take("v1", null));
    });

    [Fact]
    public void WithoutAnotherVideo_NothingIsPrepared() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Source.TryGetNextVideoId(null, out _).ReturnsForAnyArgs(call =>
        {
            call[1] = null;
            return false;
        });
        setup.Prefetcher.Start("v1");

        await setup.WaitForPreparationAsync();

        Assert.Null(setup.Prefetcher.Take("v1", null));
        await setup.YouTube.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    });

    [Fact]
    public void WithoutACurrentVideo_NothingIsPrepared() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        setup.Prefetcher.Start(null);
        await setup.WaitForPreparationAsync();

        Assert.Empty(setup.Requests);
    });

    [Fact]
    public void StreamsThatExpireSoon_AreNotUsed_ButLongLivedOnesAre() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Resolve = id => new ResolvedVideo("T", null, setup.UrlExpiringIn(TimeSpan.FromMinutes(5)), setup.UrlExpiringIn(TimeSpan.FromHours(6)));
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();
        Assert.Null(setup.Prefetcher.Take("v1", null)); // the video URL runs out within the safety margin

        setup.Resolve = id => new ResolvedVideo("T", null, setup.UrlExpiringIn(TimeSpan.FromHours(6)), setup.UrlExpiringIn(TimeSpan.FromHours(6)));
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();
        Assert.NotNull(setup.Prefetcher.Take("v1", null));
    });

    [Fact]
    public void APreparedVideoOfAVeryLongVideo_IsNotUsedOnceItsStreamsRanOut() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Resolve = id => new ResolvedVideo("T", null, setup.UrlExpiringIn(TimeSpan.FromHours(6)), null);
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        // A six hour video: the URLs are no good anymore (minus the margin) when it ends.
        setup.Time.Advance(TimeSpan.FromHours(6) - NextVideoPrefetcher.ExpiryMargin + TimeSpan.FromMinutes(1));

        Assert.Null(setup.Prefetcher.Take("v1", null));
    });

    [Fact]
    public void WithoutAnExpiryInTheUrls_APreparedVideoIsGoodForHalfAnHour() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();
        setup.Time.Advance(NextVideoPrefetcher.FallbackLifetime - TimeSpan.FromMinutes(1));
        Assert.NotNull(setup.Prefetcher.Take("v1", null));

        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();
        setup.Time.Advance(NextVideoPrefetcher.FallbackLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(setup.Prefetcher.Take("v1", null));
    });

    [Fact]
    public void Cancel_DropsThePreparation() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Prefetcher.Start("v1");
        await setup.WaitForPreparationAsync();

        setup.Prefetcher.Cancel();

        Assert.Null(setup.Prefetcher.Take("v1", null));
    });

    [Fact]
    public void Dispose_StopsEverything_AndIsIdempotent() => UiContext.Run(async () =>
    {
        var setup = new Setup();
        setup.Prefetcher.Start("v1");

        setup.Dispose();
        setup.Dispose();
        setup.Prefetcher.Start("v1"); // ignored
        await setup.WaitForPreparationAsync();

        Assert.Empty(setup.Requests);
        Assert.Null(setup.Prefetcher.Take("v1", null));
    });
}
