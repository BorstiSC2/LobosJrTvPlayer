using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Voting;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Playlists;

/// <summary>The scheduler on top of a real playlist manager; the player is a substitute whose position the test sets.</summary>
public class PlaylistSwapSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan VideoLength = TimeSpan.FromMinutes(40);

    private sealed class Setup : IDisposable
    {
        private Setup(PlaylistSetup playlists)
        {
            PlaylistSetup = playlists;
            Playback.Current.Returns(_ => Current);
            Playback.Length.Returns(_ => Length);
            Playback.Position.Returns(_ => Position);
            Playback.IsStandby.Returns(_ => Standby);
            Playback.HasQueuedVideo.Returns(_ => Queued);
            Playback.IsWaitingForHold.Returns(_ => Waiting);
        }

        /// <summary>Alpha (selected) and Beta, with the swap every hour turned on at <see cref="Start"/>.</summary>
        public static async Task<Setup> CreateAsync(bool enabled = true, PlaylistSetup? playlists = null)
        {
            var setup = new Setup(playlists ?? new PlaylistSetup()
                .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1", "a2")
                .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1", "b2"));
            setup.Manager = setup.PlaylistSetup.Create(setup.Time);
            await setup.Manager.InitializeAsync();
            await Async.WaitUntilAsync(() => !setup.Manager.IsLoading && setup.Manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
            if (enabled)
                setup.Manager.SetAutoSwap(true, Interval);

            setup.Manager.PoolChanged += (_, e) => setup.Changes.Add(e);
            setup.Scheduler = new PlaylistSwapScheduler(setup.Manager, setup.Playback, () => setup.VoteRunning, setup.Time);
            return setup;
        }

        public PlaylistSetup PlaylistSetup { get; }

        public FakeTimeProvider Time { get; } = new(Start);

        public PlaylistManager Manager { get; private set; } = null!;

        public PlaylistSwapScheduler Scheduler { get; private set; } = null!;

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        public List<PoolChangedEventArgs> Changes { get; } = new();

        public NowPlaying? Current { get; set; } = new("a1", "Video a1", VideoLength);

        public TimeSpan? Length { get; set; } = VideoLength;

        public TimeSpan Position { get; set; }

        public bool Standby { get; set; }

        public bool Queued { get; set; }

        public bool Waiting { get; set; }

        public bool VoteRunning { get; set; }

        public void Remaining(TimeSpan remaining) => Position = (Length ?? VideoLength) - remaining;

        /// <summary>Moves the clock (firing the checks) and lets them run.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            await Async.SettleAsync();
            Time.Advance(by);
            await Async.SettleAsync();
        }

        public Task TickAsync() => AdvanceAsync(PlaylistSwapScheduler.CheckInterval);

        public void Dispose()
        {
            Scheduler.Dispose();
            PlaylistSetup.Dispose();
        }
    }

    [Fact]
    public void TheNextPlaylistTakesOverBeforeTheAutovoteStarts() =>
        Assert.True(PlaylistSwapScheduler.LeadTime >= AutoVoteManager.LeadTime + 2 * AutoVoteManager.CheckInterval + PlaylistSwapScheduler.CheckInterval);

    [Fact]
    public void Constructor_RequiresASynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new PlaylistSwapScheduler(null!, Substitute.For<IPlaybackControl>()));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void BeforeTheIntervalIsUp_NothingChanges() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(1));

        await setup.AdvanceAsync(Interval - TimeSpan.FromSeconds(10));
        await setup.TickAsync();

        Assert.Equal(IdA, setup.Manager.SelectedKey);
        Assert.Empty(setup.Changes);
    });

    [Fact]
    public void WhenTheIntervalIsUp_TheVideoPlaysOn_AndTheNextPlaylistTakesOverShortlyBeforeItEnds() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(30));

        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();
        Assert.True(setup.Manager.IsSwapDue);
        Assert.Equal(IdA, setup.Manager.SelectedKey); // 30 minutes are left: the video plays on

        setup.Remaining(PlaylistSwapScheduler.LeadTime + TimeSpan.FromSeconds(1));
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        setup.Remaining(PlaylistSwapScheduler.LeadTime);
        await setup.TickAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
        var change = Assert.Single(setup.Changes);
        Assert.True(change.SelectionChanged);
        Assert.True(change.KeepCurrentVideo); // the coordinator does not skip the current video
        await setup.Playback.DidNotReceive().SkipAsync();
        Assert.Equal(Url(IdB), setup.PlaylistSetup.SavedLibrary().SelectedPlaylistUrl);
    });

    [Fact]
    public void AfterTheSwap_TheIntervalStartsOver_AndTheNextSwapGoesOnToTheFollowingPlaylist() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(1));
        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();
        Assert.Equal(IdB, setup.Manager.SelectedKey);
        var swappedAt = setup.Manager.LastSwapUtc!.Value;
        Assert.Equal(swappedAt + Interval, setup.Manager.NextSwapUtc);

        // A new video, which also ends soon: no second swap within the interval.
        setup.Current = new NowPlaying("b1", "Video b1", VideoLength);
        await setup.AdvanceAsync(Interval - TimeSpan.FromMinutes(1));
        Assert.Equal(IdB, setup.Manager.SelectedKey);

        await setup.AdvanceAsync(TimeSpan.FromMinutes(1));
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey); // two playlists: back to the first one
        Assert.Equal(2, setup.Changes.Count);
    });

    [Fact]
    public void WhileNothingPlaysYet_TheNextPlaylistTakesOverAtOnce() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Current = null;
        setup.Standby = true;

        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
        Assert.True(Assert.Single(setup.Changes).KeepCurrentVideo); // nothing starts playing, the first video is prepared again
    });

    [Fact]
    public void AVideoWithoutAKnownLength_HasNoEndToWaitFor() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Length = null; // a live stream
        setup.Current = new NowPlaying("live", "A live stream", null);

        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
        Assert.True(Assert.Single(setup.Changes).KeepCurrentVideo);
    });

    [Fact]
    public void WhileAVoteRunsOrItsWinnerWaits_TheSwapWaitsForTheWinnersVideo() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(1));
        setup.VoteRunning = true;
        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        setup.VoteRunning = false;
        setup.Queued = true; // the winner plays after the current video
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        setup.Waiting = true; // the short video is over and the next one waits for the vote
        setup.Queued = false;
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        // The winner plays; near its end the next playlist takes over.
        setup.Waiting = false;
        setup.Current = new NowPlaying("a2", "Video a2", VideoLength);
        setup.Remaining(TimeSpan.FromMinutes(2));
        await setup.TickAsync();
        Assert.Equal(IdB, setup.Manager.SelectedKey);
    });

    [Fact]
    public void AVideoShorterThanTheLeadTime_IsCheckedAsSoonAsItStarts_BeforeTheAutovoteLooks() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(20));
        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        // A three-minute video starts; no timer tick is needed.
        setup.Current = new NowPlaying("a2", "Short video", TimeSpan.FromMinutes(3));
        setup.Length = TimeSpan.FromMinutes(3);
        setup.Position = TimeSpan.Zero;
        setup.Playback.NowPlayingChanged += Raise.Event<EventHandler<NowPlaying>>(setup.Playback, setup.Current);
        await Async.SettleAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
    });

    [Fact]
    public void WhenTurnedOff_ItNeverSwaps() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync(enabled: false);
        setup.Remaining(TimeSpan.FromSeconds(30));

        await setup.AdvanceAsync(TimeSpan.FromDays(30));
        await setup.TickAsync();

        Assert.Equal(IdA, setup.Manager.SelectedKey);
        Assert.Empty(setup.Changes);
    });

    [Fact]
    public void ASwapThatFellDueWhileTheAppWasClosed_HappensRightAfterTheStart() => UiContext.Run(async () =>
    {
        var playlists = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        playlists.Library.AutoSwap = new AutoSwapSettings { Enabled = true, IntervalHours = 24, LastSwapUtc = Start - TimeSpan.FromDays(3) };
        using var setup = await Setup.CreateAsync(enabled: false, playlists);
        setup.Current = null;
        setup.Standby = true; // the app opens paused

        await setup.TickAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
        Assert.Equal(Start + PlaylistSwapScheduler.CheckInterval, setup.Manager.LastSwapUtc);
    });

    [Fact]
    public void WhileTheSelectedPlaylistIsStillLoadingAtStartup_NothingIsSwapped() => UiContext.Run(async () =>
    {
        using var playlists = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true) // no cache: downloaded at startup
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        playlists.Library.AutoSwap = new AutoSwapSettings { Enabled = true, IntervalHours = 1, LastSwapUtc = Start - TimeSpan.FromDays(1) };
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        playlists.YouTube.GetPlaylistVideoIdsAsync(IdA, Arg.Any<CancellationToken>()).Returns(download.Task);
        var time = new FakeTimeProvider(Start);
        var manager = playlists.Create(time);
        var playback = Substitute.For<IPlaybackControl>();
        playback.IsStandby.Returns(true);
        using var scheduler = new PlaylistSwapScheduler(manager, playback, timeProvider: time);

        var initialize = manager.InitializeAsync();
        await Async.WaitUntilAsync(() => manager.Playlists.Count == 2);
        time.Advance(PlaylistSwapScheduler.CheckInterval);
        await Async.SettleAsync();
        Assert.Equal(IdA, manager.SelectedKey);

        download.SetResult(new[] { "a1" });
        await initialize;
        time.Advance(PlaylistSwapScheduler.CheckInterval);
        await Async.SettleAsync();

        Assert.Equal(IdB, manager.SelectedKey);
    });

    [Fact]
    public void WithoutAnotherPlaylistThatCanBePlayed_NothingIsSwapped_UntilOneCanBePlayedAgain() => UiContext.Run(async () =>
    {
        var playlists = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Unreachable");
        using var setup = await Setup.CreateAsync(playlists: playlists);
        setup.Remaining(TimeSpan.FromMinutes(1));

        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey);

        setup.PlaylistSetup.OnYouTube(IdB, "b1");
        await setup.Manager.UpdateAsync(IdB); // e.g. "Update" in the playlist dialog
        await setup.TickAsync();

        Assert.Equal(IdB, setup.Manager.SelectedKey);
    });

    [Fact]
    public void AFailedSwap_IsTriedAgainAfterAMinute() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(3));
        await setup.AdvanceAsync(Interval - PlaylistSwapScheduler.CheckInterval);

        var file = Path.Combine(setup.PlaylistSetup.Folder, "playlists.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            await setup.TickAsync();
            Assert.Equal(IdA, setup.Manager.SelectedKey);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        await setup.TickAsync();
        Assert.Equal(IdA, setup.Manager.SelectedKey); // not at every check

        await setup.AdvanceAsync(PlaylistSwapScheduler.RetryDelay);
        await setup.TickAsync();
        Assert.Equal(IdB, setup.Manager.SelectedKey);
    });

    [Fact]
    public void Dispose_StopsTheChecks() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Remaining(TimeSpan.FromMinutes(1));
        setup.Scheduler.Dispose();

        await setup.AdvanceAsync(Interval);
        await setup.TickAsync();

        Assert.Equal(IdA, setup.Manager.SelectedKey);
    });
}
