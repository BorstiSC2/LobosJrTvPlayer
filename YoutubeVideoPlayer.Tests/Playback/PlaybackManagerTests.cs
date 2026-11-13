using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Playback;

public class PlaybackManagerTests
{
    [Fact]
    public void Constructor_RequiresASynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new PlaybackManager(
                Substitute.For<IYouTubeService>(), Substitute.For<IVideoSource>(), Substitute.For<IMediaPlayer>()));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    #region Starting videos

    [Fact]
    public void PlayNext_WaitsASecond_ResolvesTheVideo_PlaysItsStreams_AndAnnouncesIt() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1");
        var loading = 0;
        var announced = new List<NowPlaying>();
        setup.Manager.LoadingStarted += (_, _) => loading++;
        setup.Manager.NowPlayingChanged += (_, n) => announced.Add(n);

        var task = setup.Manager.PlayNextAsync();
        await Async.SettleAsync();
        setup.Player.DidNotReceiveWithAnyArgs().Play(default!, default);

        setup.Time.Advance(PlaybackManager.DelayBeforeNextVideo);
        await task;

        setup.Player.Received(1).Play("v1.video", "v1.audio");
        Assert.Equal(1, loading);
        var expected = new NowPlaying("v1", "Title v1", TimeSpan.FromMinutes(3));
        Assert.Equal(new[] { expected }, announced);
        Assert.Equal(expected, setup.Manager.Current);
    });

    [Fact]
    public void PlayNext_SkipsVideosThatCannotBeResolved() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("broken", "v2");
        setup.FailToResolve("broken");

        var task = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo); // delay before the second attempt
        await task;

        Assert.Equal(new[] { "v2.video" }, setup.PlayedVideos());
        Assert.Equal("v2", setup.Manager.Current!.VideoId);
    });

    [Fact]
    public void PlayNext_SkipsVideosThePlayerRejects() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        setup.Player.When(p => p.Play("v1.video", Arg.Any<string?>())).Throw(new InvalidOperationException("bad media"));
        var announced = new List<string>();
        setup.Manager.NowPlayingChanged += (_, n) => announced.Add(n.VideoId);

        var task = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await task;

        Assert.Equal(new[] { "v2" }, announced);
    });

    [Fact]
    public void PlayNext_WithoutVideos_RaisesNoVideosAvailable() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();
        var raised = 0;
        setup.Manager.NoVideosAvailable += (_, _) => raised++;

        await setup.Manager.PlayNextAsync();

        Assert.Equal(1, raised);
        setup.Player.DidNotReceiveWithAnyArgs().Play(default!, default);
    });

    [Fact]
    public void PlayNext_CancelsAnAttemptThatIsStillResolving() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("slow", "fast");
        var slowResolve = new TaskCompletionSource<ResolvedVideo>();
        CancellationToken slowToken = default;
        setup.YouTube.ResolveAsync("slow", Arg.Any<CancellationToken>()).Returns(call =>
        {
            slowToken = (CancellationToken)call[1];
            slowToken.Register(() => slowResolve.TrySetCanceled(slowToken));
            return slowResolve.Task;
        });

        var first = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        Assert.False(first.IsCompleted);

        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await first;

        Assert.True(slowToken.IsCancellationRequested);
        Assert.Equal(new[] { "fast.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void Skip_StopsTheCurrentVideoImmediately_ThenPlaysTheNext() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.ClearReceivedCalls();

        var skip = setup.Manager.SkipAsync();
        setup.Player.Received(1).Stop();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await skip;

        Assert.Equal(new[] { "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void Skip_StillPlaysTheNextVideoIfStoppingFails() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1");
        setup.Player.When(p => p.Stop()).Throw(new InvalidOperationException("cannot stop"));

        await setup.RunAsync(setup.Manager.SkipAsync);

        Assert.Equal(new[] { "v1.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayVideo_PlaysTheRequestedVideoWithoutDelay() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();

        await setup.Manager.PlayVideoAsync("winner");

        Assert.Equal(new[] { "winner.video" }, setup.PlayedVideos());
        Assert.Equal("winner", setup.Manager.Current!.VideoId);
        setup.Player.DidNotReceive().Stop(); // the old video keeps playing until the new one is ready
    });

    [Fact]
    public void PlayVideo_FallsBackToARandomVideoIfTheRequestedOneFails() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        setup.FailToResolve("winner");

        await setup.RunAsync(() => setup.Manager.PlayVideoAsync("winner"));

        Assert.Equal(new[] { "random.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayVideo_IsCancelledByALaterPlayNext() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("next");
        var pending = new TaskCompletionSource<ResolvedVideo>();
        setup.YouTube.ResolveAsync("winner", Arg.Any<CancellationToken>()).Returns(call =>
        {
            ((CancellationToken)call[1]).Register(() => pending.TrySetCanceled());
            return pending.Task;
        });

        var vote = setup.Manager.PlayVideoAsync("winner");
        await Async.SettleAsync();
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await vote;

        Assert.Equal(new[] { "next.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayNext_CalledTwiceWhileAnAttemptIsResolving_OnlyTheLastCallPlays() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("slow", "second", "third");
        var slowResolve = new TaskCompletionSource<ResolvedVideo>();
        setup.YouTube.ResolveAsync("slow", Arg.Any<CancellationToken>()).Returns(call =>
        {
            ((CancellationToken)call[1]).Register(() => slowResolve.TrySetCanceled());
            return slowResolve.Task;
        });

        var first = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        // Both callers see the same running attempt; they must not both start a new one.
        var second = setup.Manager.PlayNextAsync();
        var third = setup.Manager.PlayNextAsync();
        for (var i = 0; i < 3; i++)
            await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await Task.WhenAll(first, second, third);

        Assert.Equal(new[] { "third.video" }, setup.PlayedVideos());
        Assert.Equal("third", setup.Manager.Current!.VideoId);
    });

    [Fact]
    public void PlayNext_ATimeoutFromYouTube_IsRetriedWithTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("slow", "v2");
        // HttpClient reports a timeout as a TaskCanceledException although nobody cancelled the operation.
        setup.FailToResolve("slow", new TaskCanceledException("The request timed out."));

        var task = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await task;

        Assert.Equal(new[] { "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayVideo_ATimeoutFromYouTube_FallsBackToARandomVideo() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        setup.FailToResolve("winner", new TaskCanceledException("The request timed out."));

        await setup.RunAsync(() => setup.Manager.PlayVideoAsync("winner"));

        Assert.Equal(new[] { "random.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void NoVideosAvailable_HandlerCanStartPlaybackAgain() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();
        Task? restarted = null;
        setup.Manager.NoVideosAvailable += (_, _) =>
        {
            // Like the real handler: the pool was refilled (e.g. in the playlist dialog) and playback restarts.
            setup.Queue("v1");
            restarted = setup.Manager.PlayNextAsync();
        };

        await setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await restarted!;

        Assert.Equal(new[] { "v1.video" }, setup.PlayedVideos());
    });

    #region Preparing the next video (no gap between two videos)

    [Fact]
    public void TheNextVideoIsLookedUpWhileTheCurrentOnePlays_AndStartsAtOnceWhenItIsDue() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.YouTube.DidNotReceive().ResolveAsync("v2", Arg.Any<CancellationToken>());

        await setup.AdvanceAsync(NextVideoPrefetcher.Delay);
        await setup.YouTube.Received(1).ResolveAsync("v2", Arg.Any<CancellationToken>()); // looked up during v1

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync(); // the clock does not move: no waiting, no lookup

        setup.Player.Received(1).Play("v2.video", "v2.audio");
        await setup.YouTube.Received(1).ResolveAsync("v2", Arg.Any<CancellationToken>());
        Assert.Equal("v2", setup.Manager.Current!.VideoId);
    });

    [Fact]
    public void ASkipUsesThePreparedVideoToo_AndStillStopsTheOldOneAtOnce() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay);

        var skip = setup.Manager.SkipAsync();
        setup.Player.Received(1).Stop();
        await Async.SettleAsync();
        await skip;

        setup.Player.Received(1).Play("v2.video", "v2.audio");
    });

    [Fact]
    public void TheVideoThatWasChosenInAdvanceIsTheOneThatPlays_TheSourceIsAskedOnlyOnce() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay);

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        // A shuffled playlist would pick a different random video on a second request.
        Assert.Equal(new string?[] { null, "v1" }, setup.NextRequests);
        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void WhenTheVideoEndsBeforeTheNextOneWasPrepared_TheOldWayIsUsed() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Player.EndReached += Raise.Event(); // before the preparation had a chance to start
        await Async.SettleAsync();
        setup.Player.DidNotReceive().Play("v2.video", Arg.Any<string?>()); // still waiting for the pause

        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        setup.Player.Received(1).Play("v2.video", "v2.audio");
        await setup.YouTube.Received(1).ResolveAsync("v2", Arg.Any<CancellationToken>());
    });

    [Fact]
    public void AQueuedVideoReplacesWhatWasPrepared_AndIsPreparedItself() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay); // v2 is ready

        setup.Manager.QueueNextVideo("winner"); // e.g. the winner of the autovote
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay);
        await setup.YouTube.Received(1).ResolveAsync("winner", Arg.Any<CancellationToken>());

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        Assert.Equal(new[] { "v1.video", "winner.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void WhenThePlaylistChanges_WhatWasPreparedIsForgottenAndChosenAgain() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay); // v2 is ready

        setup.Source.SequenceChanged += Raise.Event();
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay);  // the source gives v3 now

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        Assert.Equal(new[] { "v1.video", "v3.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void AVideoThatCouldNotBeLookedUpInAdvance_IsHandledTheOldWay() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "bad", "v3");
        setup.FailToResolve("bad");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay); // the lookup of "bad" fails quietly

        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v3.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void EveryVideoThatStartsPreparesTheNextOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3", "v4");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        for (var i = 0; i < 3; i++)
        {
            await setup.AdvanceAsync(NextVideoPrefetcher.Delay);
            setup.Player.EndReached += Raise.Event();
            await Async.SettleAsync();
        }

        Assert.Equal(new[] { "v1.video", "v2.video", "v3.video", "v4.video" }, setup.PlayedVideos());
        Assert.Equal(new string?[] { null, "v1", "v2", "v3" }, setup.NextRequests);
    });

    [Fact]
    public void AfterDispose_NothingIsPrepared() => UiContext.Run(async () =>
    {
        var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Dispose();
        await setup.AdvanceAsync(NextVideoPrefetcher.Delay * 2);

        await setup.YouTube.DidNotReceive().ResolveAsync("v2", Arg.Any<CancellationToken>());
    });

    #endregion

    #region The video the next one follows

    [Fact]
    public void TheVideoSourceIsToldWhichVideoIsPlaying_SoAnOrderedPlaylistCanContinue() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3");

        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.RunAsync(setup.Manager.SkipAsync);

        Assert.Equal(new string?[] { null, "v1", "v2" }, setup.NextRequests);
    });

    [Fact]
    public void AVideoThatCannotBePlayed_IsNotPickedAgain_ByAnOrderedPlaylist() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("broken", "v2");
        setup.FailToResolve("broken");

        var task = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await task;

        // The retry continues behind the broken video instead of asking for the same one forever.
        Assert.Equal(new string?[] { null, "broken" }, setup.NextRequests);
        Assert.Equal(new[] { "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void AfterAQueuedVideo_TheOrderContinuesBehindIt() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Manager.QueueNextVideo("winner");
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new string?[] { null, "winner" }, setup.NextRequests);
        Assert.Equal(new[] { "v1.video", "winner.video", "v2.video" }, setup.PlayedVideos());
    });

    #endregion

    #region Queued video

    [Fact]
    public void AQueuedVideo_PlaysAfterTheCurrentOneEnds_InsteadOfARandomOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "random1", "random2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Manager.QueueNextVideo("winner");
        setup.Player.ClearReceivedCalls();
        Assert.Empty(setup.PlayedVideos()); // the current video keeps playing until it ends

        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        Assert.Equal(new[] { "winner.video" }, setup.PlayedVideos());

        // Only once: after that the random videos continue.
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        Assert.Equal(new[] { "winner.video", "random1.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void AQueuedVideo_AlsoPlaysAfterASkip() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "random");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.ClearReceivedCalls();

        setup.Manager.QueueNextVideo("winner");
        await setup.RunAsync(setup.Manager.SkipAsync);

        Assert.Equal(new[] { "winner.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void AQueuedVideo_IsNotLostWhenTheAttemptThatWasToPlayItIsCancelled() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        var calls = 0;
        var pending = new TaskCompletionSource<ResolvedVideo>();
        setup.YouTube.ResolveAsync("winner", Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (calls++ > 0)
                return Task.FromResult(PlaybackSetup.Resolved("winner"));

            ((CancellationToken)call[1]).Register(() => pending.TrySetCanceled());
            return pending.Task;
        });
        setup.Manager.QueueNextVideo("winner");

        var first = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo); // resolving the winner
        await setup.RunAsync(setup.Manager.PlayNextAsync);              // e.g. a mod skips meanwhile
        await first;

        Assert.Equal(new[] { "winner.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void AQueuedVideoThatCannotBePlayed_FallsBackToRandomVideos() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        setup.FailToResolve("winner");
        setup.Manager.QueueNextVideo("winner");

        var task = setup.Manager.PlayNextAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await task;

        Assert.Equal(new[] { "random.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayingAVideoExplicitly_ReplacesTheQueuedOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        setup.Manager.QueueNextVideo("queued");

        await setup.Manager.PlayVideoAsync("chosen");
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "chosen.video", "random.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void ClearQueuedVideo_ForgetsIt_AndNothingIsQueuedAfterDispose() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("random");
        setup.Manager.QueueNextVideo("queued");
        setup.Manager.ClearQueuedVideo();

        await setup.RunAsync(setup.Manager.PlayNextAsync);
        Assert.Equal(new[] { "random.video" }, setup.PlayedVideos());

        setup.Dispose();
        setup.Manager.QueueNextVideo("late"); // ignored, must not throw
    });

    #endregion

    [Fact]
    public void Operations_AfterDispose_DoNothing() => UiContext.Run(async () =>
    {
        var setup = new PlaybackSetup().Queue("v1");
        setup.Dispose();
        setup.Dispose(); // idempotent

        await setup.Manager.PlayNextAsync();

        setup.Player.DidNotReceiveWithAnyArgs().Play(default!, default);
        setup.Player.Received(1).Stop();
        Assert.False(setup.Manager.IsPlaying);
    });

    #endregion

    #region Player events

    [Fact]
    public void EndOfVideo_PlaysTheNextOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void PlayerEventsFromAnotherThread_AreHandledOnTheContextThread() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();
        var testThread = Environment.CurrentManagedThreadId;
        var handlerThread = 0;
        setup.Manager.BufferingChanged += (_, _) => handlerThread = Environment.CurrentManagedThreadId;

        await Task.Run(() => setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 10f));
        await Async.WaitUntilAsync(() => handlerThread != 0);

        Assert.Equal(testThread, handlerThread);
    });

    [Fact]
    public void PlayerError_StopsBufferingAndPlaysTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        var buffering = new List<bool>();
        setup.Manager.BufferingChanged += (_, b) => buffering.Add(b);
        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 20f);
        await Async.SettleAsync();

        setup.Player.EncounteredError += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { true, false }, buffering);
        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void LateEventsOfTheVideoThatWasSkipped_DoNotSkipTheNextOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        // The old video reports an error just as the user skips it; the event is delivered afterwards.
        setup.Player.EncounteredError += Raise.Event();
        var skip = setup.Manager.SkipAsync();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await skip;

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
        Assert.Equal("v2", setup.Manager.Current!.VideoId);
    });

    [Fact]
    public void Buffering_IsReportedOnceUntilItFinishes() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();
        var buffering = new List<bool>();
        setup.Manager.BufferingChanged += (_, b) => buffering.Add(b);

        foreach (var cache in new[] { 10f, 40f, 80f, 100f, 50f })
            setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, cache);
        await Async.SettleAsync();
        setup.Player.Playing += Raise.Event();
        await Async.SettleAsync();

        Assert.Equal(new[] { true, false, true, false }, buffering);
    });

    [Fact]
    public void Buffering_LongerThan30Seconds_SkipsToTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);

        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 5f);
        await setup.AdvanceAsync(PlaybackManager.BufferingTimeout - TimeSpan.FromSeconds(1));
        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 50f); // does not restart the timeout
        await setup.AdvanceAsync(TimeSpan.FromSeconds(1));
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void Buffering_ThatFinishesInTime_DoesNotSkip() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        // The position moves on with every look. (A clock value would be the same for several watchdog
        // ticks that one big Advance fires in a row, which looks like a frozen video.)
        var position = 0L;
        setup.Player.TimeMs.Returns(_ => ++position);

        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 5f);
        await setup.AdvanceAsync(TimeSpan.FromSeconds(10));
        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 100f);
        await setup.AdvanceAsync(PlaybackManager.BufferingTimeout * 2);

        Assert.Equal(new[] { "v1.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void Playing_ReappliesVolumeAndMute() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();
        setup.Manager.Volume = 42;
        setup.Manager.IsMuted = true;
        setup.Player.ClearReceivedCalls();

        setup.Player.Playing += Raise.Event();
        await Async.SettleAsync();

        setup.Player.Received().Volume = 42;
        setup.Player.Received().Mute = true;
    });

    [Fact]
    public void EventsAfterDispose_AreIgnored() => UiContext.Run(async () =>
    {
        var setup = new PlaybackSetup().Queue("v1");
        var buffering = 0;
        setup.Manager.BufferingChanged += (_, _) => buffering++;
        setup.Dispose();

        setup.Player.Buffering += Raise.Event<EventHandler<float>>(setup.Player, 5f);
        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(0, buffering);
        setup.Player.DidNotReceiveWithAnyArgs().Play(default!, default);
    });

    #endregion

    #region Fail-safe watchdog

    [Fact]
    public void FailSafe_SkipsWhenThePlayerIsNotPlayingOnTwoChecksInARow() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync); // starts the watchdog
        setup.Player.IsPlaying.Returns(false);
        setup.Player.State.Returns("Stopped");

        await setup.AdvanceAsync(PlaybackManager.FailSafeInterval - PlaybackManager.DelayBeforeNextVideo);
        Assert.Single(setup.PlayedVideos()); // first failed check only arms the watchdog

        await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void FailSafe_DoesNotInterruptAVideoThatIsStillBeingResolved() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("slow", "v2");
        var slowResolve = new TaskCompletionSource<ResolvedVideo>();
        var cancelled = false;
        setup.YouTube.ResolveAsync("slow", Arg.Any<CancellationToken>()).Returns(call =>
        {
            ((CancellationToken)call[1]).Register(() => cancelled = true);
            return slowResolve.Task;
        });
        setup.Player.IsPlaying.Returns(false);

        var task = setup.Manager.PlayNextAsync();
        for (var check = 0; check < 6; check++) // two minutes: far more than two checks in a row
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.False(cancelled);
        Assert.False(task.IsCompleted);

        slowResolve.SetResult(PlaybackSetup.Resolved("slow"));
        await task;
        Assert.Equal(new[] { "slow.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void FailSafe_StopsWatchingWhenThereAreNoVideos() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup();

        await setup.Manager.PlayNextAsync();
        for (var check = 0; check < 6; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        setup.Source.ReceivedWithAnyArgs(1).TryGetNextVideoId(null, out _); // not asked again by the watchdog
    });

    [Fact]
    public void FailSafe_SkipsAFrozenVideo() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        setup.Player.TimeMs.Returns(5000L); // position does not move

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void FailSafe_LeavesAHealthyVideoAlone() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        setup.Player.TimeMs.Returns(_ => setup.Time.GetUtcNow().ToUnixTimeMilliseconds());

        for (var check = 0; check < 10; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.Single(setup.PlayedVideos());
    });

    [Fact]
    public void FailSafe_ARecoveredVideoResetsTheWatchdog() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(false, true, false, true, false, true);
        setup.Player.TimeMs.Returns(_ => setup.Time.GetUtcNow().ToUnixTimeMilliseconds());

        for (var check = 0; check < 6; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.Single(setup.PlayedVideos());
    });

    #endregion

    #region Volume and status

    [Fact]
    public void Volume_IsClampedForwardedAndAnnouncedOnlyWhenItChanges() => UiContext.Run(() =>
    {
        using var setup = new PlaybackSetup();
        var changes = 0;
        setup.Manager.VolumeChanged += (_, _) => changes++;

        setup.Manager.Volume = 150;
        Assert.Equal(100, setup.Manager.Volume);
        Assert.Equal(0, changes); // already 100

        setup.Manager.Volume = -5;
        Assert.Equal(0, setup.Manager.Volume);
        setup.Player.Received().Volume = 0;

        setup.Manager.AdjustVolume(+5);
        setup.Manager.AdjustVolume(+5);
        Assert.Equal(10, setup.Manager.Volume);
        setup.Player.Received().Volume = 10;
        Assert.Equal(3, changes);
    });

    [Fact]
    public void Mute_IsToggledForwardedAndAnnounced() => UiContext.Run(() =>
    {
        using var setup = new PlaybackSetup();
        var changes = 0;
        setup.Manager.VolumeChanged += (_, _) => changes++;

        setup.Manager.ToggleMute();
        Assert.True(setup.Manager.IsMuted);
        setup.Player.Received().Mute = true;

        setup.Manager.IsMuted = true; // unchanged
        setup.Manager.ToggleMute();
        Assert.False(setup.Manager.IsMuted);
        setup.Player.Received().Mute = false;
        Assert.Equal(2, changes);
    });

    [Fact]
    public void PositionAndLength_ComeFromThePlayer_WithTheYouTubeDurationAsFallback() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1");
        Assert.Null(setup.Manager.Length);

        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.TimeMs.Returns(-1L);
        setup.Player.LengthMs.Returns(0L);
        Assert.Equal(TimeSpan.Zero, setup.Manager.Position);
        Assert.Equal(TimeSpan.FromMinutes(3), setup.Manager.Length);

        setup.Player.TimeMs.Returns(61_000L);
        setup.Player.LengthMs.Returns(125_000L);
        Assert.Equal(TimeSpan.FromSeconds(61), setup.Manager.Position);
        Assert.Equal(TimeSpan.FromSeconds(125), setup.Manager.Length);
    });

    [Fact]
    public void IsPlaying_ReflectsThePlayer() => UiContext.Run(() =>
    {
        using var setup = new PlaybackSetup();

        setup.Player.IsPlaying.Returns(true);
        Assert.True(setup.Manager.IsPlaying);
        setup.Player.IsPlaying.Returns(false);
        Assert.False(setup.Manager.IsPlaying);
    });

    #endregion
}
