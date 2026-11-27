using NSubstitute;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Playback;

/// <summary>A video whose stream breaks off (it freezes, or "ends" long before its length) is resumed where it stopped.</summary>
public class StreamRecoveryTests
{
    // PlaybackSetup's videos are 3 minutes long.
    private static readonly long Minute = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    /// <summary>Starts v1 and lets the watchdog see it at <paramref name="positionMs"/>.</summary>
    private static async Task<PlaybackSetup> PlayingAtAsync(long positionMs, Func<long>? position = null)
    {
        var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        var reads = 0L;
        setup.Player.TimeMs.Returns(_ => position?.Invoke() ?? positionMs + ++reads); // moving: a healthy video
        await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        return setup;
    }

    private static TimeSpan Resumed(long positionMs) =>
        TimeSpan.FromMilliseconds(positionMs) - PlaybackManager.ResumeRewind;

    [Fact]
    public void AFrozenVideo_IsResumedWhereItStopped_WithFreshStreams() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        var started = new List<NowPlaying>();
        setup.Manager.NowPlayingChanged += (_, video) => started.Add(video);
        setup.Player.TimeMs.Returns(Minute); // frozen

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        setup.Player.Received(1).Play("v1.video", "v1.audio", Resumed(Minute));
        await setup.YouTube.Received(2).ResolveAsync("v1", Arg.Any<CancellationToken>()); // looked up again
        Assert.Equal("v1", setup.Manager.Current!.VideoId);
        Assert.Empty(started); // the same video goes on: no new start for chat, autovote or playlist progress
    });

    [Fact]
    public void AnEndLongBeforeTheLength_IsABrokenStream_ThatIsResumed() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        var finished = new List<NowPlaying>();
        setup.Manager.VideoFinished += (_, video) => finished.Add(video);
        setup.Player.TimeMs.Returns(-1L); // the player forgets the position once the media has ended

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        setup.Player.Received(1).Play("v1.video", "v1.audio", Arg.Is<TimeSpan>(t => t > TimeSpan.Zero && t <= Resumed(Minute + 1)), Arg.Any<string?>());
        Assert.DoesNotContain("v2.video", setup.PlayedVideos());
        Assert.Empty(finished); // it did not play to its end
    });

    [Fact]
    public void AnEndNearTheLength_IsTheRealEnd() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(3 * Minute - PlaybackManager.FailSafeInterval.Ticks / TimeSpan.TicksPerMillisecond);
        var finished = new List<NowPlaying>();
        setup.Manager.VideoFinished += (_, video) => finished.Add(video);

        setup.Player.EndReached += Raise.Event();
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
        Assert.Single(finished);
    });

    [Fact]
    public void AResumeThatWorked_DoesNotCountAgainstLaterOnes() => UiContext.Run(async () =>
    {
        var position = Minute;
        using var setup = await PlayingAtAsync(0, () => position);

        // The stream breaks off three times, and after each resume it plays on for two minutes.
        for (var drop = 0; drop < 3; drop++)
        {
            for (var check = 0; check < 3; check++)
                await setup.AdvanceAsync(PlaybackManager.FailSafeInterval); // frozen: resumed

            for (var step = 0; step < 6; step++)
            {
                position += PlaybackManager.FailSafeInterval.Ticks / TimeSpan.TicksPerMillisecond;
                await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
            }
        }

        Assert.Equal(4, setup.PlayedVideos().Count(v => v == "v1.video"));
        Assert.DoesNotContain("v2.video", setup.PlayedVideos());
    });

    [Fact]
    public void AResumeWhoseLookupFails_PlaysTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        setup.FailToResolve("v1");
        setup.Player.TimeMs.Returns(Minute);

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void ASkipDuringAResume_Wins() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        setup.YouTube.ResolveAsync("v1", Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<ResolvedVideo>().Task);
        setup.Player.TimeMs.Returns(Minute);
        for (var check = 0; check < 2; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval); // the resume is looking v1 up

        await setup.Manager.SkipAsync();

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void IsResuming_FromTheBreakUntilTheVideoPlaysAtItsStartPoint() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        var changes = 0;
        setup.Manager.StateChanged += (_, _) => changes++;
        setup.Player.TimeMs.Returns(Minute); // frozen
        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.True(setup.Manager.IsResuming);
        Assert.Equal(Resumed(Minute), setup.Manager.ResumePosition);
        Assert.Equal(1, changes);

        // The broken stream still reports its old position (past the start point), but it does not move.
        await setup.AdvanceAsync(PlaybackManager.ResumeCheckInterval * 4);
        Assert.True(setup.Manager.IsResuming);

        setup.Player.TimeMs.Returns(0L); // VLC seeks
        await setup.AdvanceAsync(PlaybackManager.ResumeCheckInterval * 4);
        Assert.True(setup.Manager.IsResuming);

        var position = Minute - 2500;
        setup.Player.TimeMs.Returns(_ => position += 500); // plays on from the start point
        for (var check = 0; check < 2; check++)
            await setup.AdvanceAsync(PlaybackManager.ResumeCheckInterval);
        Assert.False(setup.Manager.IsResuming);
        Assert.Null(setup.Manager.ResumePosition);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void IsResuming_EndsAsSoonAsTheVideoMoves_AlsoBeforeItsStartPoint() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        setup.Player.TimeMs.Returns(Minute); // frozen
        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        Assert.True(setup.Manager.IsResuming);

        // VLC starts at the keyframe before the start point (measured: 49:57 for 50:00).
        var position = (long)Resumed(Minute).TotalMilliseconds - 3000;
        setup.Player.TimeMs.Returns(_ => position += 250);
        for (var check = 0; check < 2; check++)
            await setup.AdvanceAsync(PlaybackManager.ResumeCheckInterval);

        Assert.False(setup.Manager.IsResuming);
    });

    [Fact]
    public void TheOldStream_IsPausedAsSoonAsTheResumeBegins() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        setup.YouTube.ResolveAsync("v1", Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<ResolvedVideo>().Task);
        setup.Player.TimeMs.Returns(Minute); // frozen
        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.True(setup.Manager.IsResuming); // still looking v1 up
        setup.Player.Received(1).Pause();
        Assert.False(setup.Manager.IsPaused); // not the user's pause: Play/Pause stays as it is
    });

    [Fact]
    public void IsResuming_EndsWithASkip_AndWhenItGivesUp() => UiContext.Run(async () =>
    {
        using (var setup = await PlayingAtAsync(Minute))
        {
            setup.Player.TimeMs.Returns(Minute);
            for (var check = 0; check < 3; check++)
                await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
            Assert.True(setup.Manager.IsResuming);

            setup.Manager.SkipAsync().FireAndForget("test");
            Assert.False(setup.Manager.IsResuming); // at once, not only when the next video is there
        }

        using (var setup = await PlayingAtAsync(Minute))
        {
            setup.Player.TimeMs.Returns(Minute); // stays frozen
            for (var check = 0; check < 10; check++)
                await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
            await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

            Assert.Contains("v2.video", setup.PlayedVideos());
            Assert.False(setup.Manager.IsResuming);
        }
    });

    [Fact]
    public void IsResuming_EndsWhenTheLookupFails() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAtAsync(Minute);
        setup.FailToResolve("v1");
        setup.Player.TimeMs.Returns(Minute);
        var changes = new List<bool>();
        setup.Manager.StateChanged += (_, _) => changes.Add(setup.Manager.IsResuming);

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.Equal(new[] { true, false }, changes);
        Assert.False(setup.Manager.IsResuming);
    });

    [Fact]
    public void AVideoThatNeverPlayed_IsNotResumed() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(false); // never got going: nothing to resume

        for (var check = 0; check < 2; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v2.video" }, setup.PlayedVideos());
    });
}

/// <summary>What the log says when a stream breaks off: the reason, the position and the player's own messages.</summary>
[Collection(LoggerCollection.Name)]
public class StreamRecoveryLogTests
{
    [Fact]
    public void AnEarlyEnd_IsLoggedWithPositionLengthAndThePlayersMessages() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        setup.Player.TimeMs.Returns(61_000L);
        setup.Player.RecentLog().Returns(new[] { "01:28:44 Error http: connection reset" });
        await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        Assert.Contains("The player reported the end of \"Title v1\" (v1) at 1:01 of 3:00: the stream broke off early.", log.Content);
        Assert.Contains("01:28:44 Error http: connection reset", log.Content);
        Assert.Contains("Resuming \"Title v1\" (v1) at 0:58 (attempt 1 of 2).", log.Content);
    });

    [Fact]
    public void TheRealEnd_IsLoggedToo() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        setup.Player.TimeMs.Returns(179_000L);

        setup.Player.EndReached += Raise.Event();
        await Async.SettleAsync();

        Assert.Contains("\"Title v1\" (v1) ended at 2:59 of 3:00; playing the next video.", log.Content);
    });

    [Fact]
    public void AFrozenVideo_IsLoggedWithThePlayersMessages() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();
        using var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        setup.Player.TimeMs.Returns(90_000L);
        setup.Player.RecentLog().Returns(Array.Empty<string>());

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval);

        Assert.Contains("the video is frozen at 1:30", log.Content);
        Assert.Contains("The player logged no warnings or errors.", log.Content);
        Assert.Contains("Resuming \"Title v1\" (v1) at 1:27 (attempt 1 of 2).", log.Content);
    });
}

/// <summary><c>!reload</c>: fresh stream links for the current video, continuing a few seconds before where it is.</summary>
public class ReloadTests
{
    private static async Task<PlaybackSetup> PlayingAsync()
    {
        var setup = new PlaybackSetup().Queue("v1", "v2");
        await setup.RunAsync(setup.Manager.PlayNextAsync);
        setup.Player.IsPlaying.Returns(true);
        return setup;
    }

    [Fact]
    public void AReload_GetsFreshLinks_AndContinuesAFewSecondsBeforeThePosition() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAsync();
        var started = new List<NowPlaying>();
        setup.Manager.NowPlayingChanged += (_, video) => started.Add(video);
        setup.Player.TimeMs.Returns(90_000L);

        Assert.Equal(ReloadResult.Started, setup.Manager.Reload());
        Assert.True(setup.Manager.IsReloading);
        Assert.Equal(TimeSpan.FromSeconds(87), setup.Manager.ResumePosition);
        setup.Player.Received(1).Pause(); // at once, with the label: it does not play on during the lookup
        await Async.SettleAsync();

        setup.Player.Received(1).Play("v1.video", "v1.audio", TimeSpan.FromSeconds(87));
        await setup.YouTube.Received(2).ResolveAsync("v1", Arg.Any<CancellationToken>()); // looked up again
        Assert.Equal("v1", setup.Manager.Current!.VideoId);
        Assert.Empty(started); // the same video goes on

        var position = 87_000L;
        setup.Player.TimeMs.Returns(_ => position += 500); // plays on from there
        for (var check = 0; check < 2; check++)
            await setup.AdvanceAsync(PlaybackManager.ResumeCheckInterval);

        Assert.False(setup.Manager.IsReloading);
        Assert.False(setup.Manager.IsResuming);
    });

    [Fact]
    public void ThereIsNothingToReload_WithoutAVideo_WhileStandingBy_OrWhileWaitingForAVote() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3");
        Assert.Equal(ReloadResult.NothingPlaying, setup.Manager.Reload());

        setup.Manager.EnterStandby();
        await Async.SettleAsync();
        Assert.Equal(ReloadResult.NothingPlaying, setup.Manager.Reload());

        await setup.Manager.PlayNextAsync();
        setup.Manager.HoldNextVideo();
        setup.Player.EndReached += Raise.Event();
        await Async.WaitUntilAsync(() => setup.Manager.IsWaitingForHold);
        Assert.Equal(ReloadResult.NothingPlaying, setup.Manager.Reload());
        setup.Player.DidNotReceive().Play(Arg.Any<string>(), Arg.Any<string?>(), Arg.Is<TimeSpan>(t => t > TimeSpan.Zero), Arg.Any<string?>());
    });

    [Fact]
    public void WhileAVideoIsBeingLoaded_ItIsBusy() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAsync();
        setup.Manager.SkipAsync().FireAndForget("test"); // waits for the next video
        await Async.SettleAsync();

        Assert.Equal(ReloadResult.Busy, setup.Manager.Reload());
        Assert.False(setup.Manager.IsResuming);
    });

    [Fact]
    public void AReload_DoesNotCountAsOneOfTheAutomaticResumes() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAsync();
        setup.Player.TimeMs.Returns(60_000L); // frozen, also after every resume

        for (var check = 0; check < 3; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval); // automatic resume 1
        Assert.Equal(ReloadResult.Started, setup.Manager.Reload());
        for (var check = 0; check < 6; check++)
            await setup.AdvanceAsync(PlaybackManager.FailSafeInterval); // automatic resume 2 (3rd check), it gives up (6th)
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v1.video", "v1.video", "v1.video", "v1.video", "v2.video" }, setup.PlayedVideos());
    });

    [Fact]
    public void APausedVideo_PlaysOnAfterAReload() => UiContext.Run(async () =>
    {
        using var setup = await PlayingAsync();
        setup.Player.TimeMs.Returns(30_000L);
        setup.Manager.Pause();
        Assert.True(setup.Manager.IsPaused);

        Assert.Equal(ReloadResult.Started, setup.Manager.Reload());
        await Async.SettleAsync();

        Assert.False(setup.Manager.IsPaused);
        setup.Player.Received(1).Play("v1.video", "v1.audio", TimeSpan.FromSeconds(27));
    });
}
