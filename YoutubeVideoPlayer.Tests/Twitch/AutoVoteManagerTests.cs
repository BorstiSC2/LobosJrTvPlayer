using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class AutoVoteManagerTests
{
    private const string Dash = "–";
    private static readonly TimeSpan VideoLength = TimeSpan.FromMinutes(20);

    /// <summary>An autovote on top of a real vote manager; the player is a substitute whose position the test sets.</summary>
    private sealed class Setup : IDisposable
    {
        public Setup(TimeSpan? voteDuration = null, bool connected = true)
        {
            Chat = TestSupport.Chat.Channel(out var sent);
            Sent = sent;
            Chat.IsConnected.Returns(connected);

            Playback.Current.Returns(call => Current);
            Playback.IsPlaying.Returns(true);
            Playback.Length.Returns(_ => Length);
            Playback.Position.Returns(_ => Position);
            Playback.HoldNextVideo().Returns(Hold);

            Videos.GetNextVideoIds(Arg.Any<int>(), Arg.Any<string?>())
                .Returns(call => new[] { "a", "b", "c", "d", "e", "f" }.Take(call.ArgAt<int>(0)).ToList());
            YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => TitleAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));

            Votes = new SkipVoteManager(
                Chat, Playback, Videos, YouTube, voteDuration ?? TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, Time, new Random(1));
            AutoVote = new AutoVoteManager(Chat, Playback, Votes, Time);
        }

        public FakeTimeProvider Time { get; } = new();

        /// <summary>How long (on the fake clock) every title lookup takes.</summary>
        public TimeSpan LookupDelay { get; set; }

        private async Task<YouTubeVideoDetails> TitleAsync(string id, CancellationToken cancellationToken)
        {
            if (LookupDelay > TimeSpan.Zero)
                await Task.Delay(LookupDelay, Time, cancellationToken);

            return new YouTubeVideoDetails("Title " + id, null);
        }

        public IChatChannel Chat { get; }

        public List<string> Sent { get; }

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        /// <summary>What <see cref="IPlaybackControl.HoldNextVideo"/> hands out.</summary>
        public IPlaybackHold Hold { get; } = Substitute.For<IPlaybackHold>();

        /// <summary>Moves the clock one second at a time (like a real clock) until the vote is over.</summary>
        public async Task RunVoteToTheEndAsync(int maxSeconds = 300)
        {
            for (var i = 0; i < maxSeconds && (Votes.IsRunning || QueuedVideo() == null); i++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                await Async.SettleAsync();
            }
        }

        public IVideoSource Videos { get; } = Substitute.For<IVideoSource>();

        public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

        public SkipVoteManager Votes { get; }

        public AutoVoteManager AutoVote { get; }

        public NowPlaying? Current { get; set; } = new NowPlaying("current", "The current video", VideoLength);

        public TimeSpan? Length { get; set; } = VideoLength;

        public TimeSpan Position { get; set; } = TimeSpan.Zero;

        /// <summary>Sets how much of the current video is left.</summary>
        public void Remaining(TimeSpan remaining) => Position = (Length ?? VideoLength) - remaining;

        /// <summary>Lets the check timer fire once.</summary>
        public async Task TickAsync()
        {
            await Async.SettleAsync();
            Time.Advance(AutoVoteManager.CheckInterval);
            await Async.SettleAsync();
        }

        public string? QueuedVideo() => Playback.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPlaybackControl.QueueNextVideo))
            .Select(c => (string?)c.GetArguments()[0])
            .SingleOrDefault();

        public void Dispose()
        {
            AutoVote.Dispose();
            Votes.Dispose();
        }
    }

    [Fact]
    public void TheVoteStartsFiveMinutesBeforeTheEnd() => Assert.Equal(TimeSpan.FromMinutes(5), AutoVoteManager.LeadTime);

    [Fact]
    public void Constructor_RequiresASynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new AutoVoteManager(
                Substitute.For<IChatChannel>(), Substitute.For<IPlaybackControl>(), null!));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void NoVoteWhileMoreThanFiveMinutesAreLeft_ThenAVoteStartsAtFiveMinutes() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.AutoVote.Enabled = true;

        setup.Remaining(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await setup.TickAsync();
        Assert.False(setup.Votes.IsRunning);
        Assert.Empty(setup.Sent);

        setup.Remaining(TimeSpan.FromMinutes(5));
        await setup.TickAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        // The message makes clear that the vote is about the video AFTER the current one.
        Assert.StartsWith("Vote for the video that plays AFTER the current one (\"The current video\")!", setup.Sent[0]);
    });

    [Fact]
    public void EnablingInsideTheLastFiveMinutes_StartsTheVoteAtOnce() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Remaining(TimeSpan.FromMinutes(4));

        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        Assert.Contains("AFTER the current one", setup.Sent[0]);
    });

    [Fact]
    public void TheWinnerIsQueuedForAfterTheCurrentVideo_NotPlayedAtOnce() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromSeconds(30));
        setup.Remaining(TimeSpan.FromMinutes(5));
        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);
        setup.Votes.Vote(Chat.Command("!vote 2", user: "anna"));

        // Posting the options, then the countdown (one second at a time like a real clock).
        for (var i = 0; i < 40 && setup.Votes.IsRunning; i++)
        {
            setup.Time.Advance(TimeSpan.FromSeconds(1));
            await Async.SettleAsync();
        }

        Assert.False(setup.Votes.IsRunning);
        Assert.Equal("b", setup.QueuedVideo());
        await setup.Playback.DidNotReceive().PlayVideoAsync(Arg.Any<string>());
        Assert.Equal($"Voting ended: \"Title b\" wins with 1 of 1 vote! It plays after the current video.", setup.Sent.Last());
    });

    [Fact]
    public void OnlyOneVotePerVideo_ANewVideoGetsItsOwn() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromSeconds(15));
        setup.Remaining(TimeSpan.FromMinutes(4));
        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);
        for (var i = 0; i < 40 && setup.Votes.IsRunning; i++)
        {
            setup.Time.Advance(TimeSpan.FromSeconds(1));
            await Async.SettleAsync();
        }

        Assert.False(setup.Votes.IsRunning);
        var messages = setup.Sent.Count;

        await setup.TickAsync();
        await setup.TickAsync();
        Assert.Equal(messages, setup.Sent.Count); // same video: no second vote
        Assert.False(setup.Votes.IsRunning);

        setup.Current = new NowPlaying("next", "The next video", VideoLength);
        setup.Remaining(TimeSpan.FromMinutes(4));
        await setup.TickAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);

        Assert.Contains("The next video", setup.Sent[messages]);
    });

    [Fact]
    public void WhenDisabled_NothingHappens_AndDisablingStopsFurtherVotes() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Remaining(TimeSpan.FromMinutes(4));

        await setup.TickAsync();
        Assert.False(setup.Votes.IsRunning); // never enabled

        setup.Position = TimeSpan.Zero;
        setup.AutoVote.Enabled = true;
        setup.AutoVote.Enabled = false;
        setup.Remaining(TimeSpan.FromMinutes(4));
        await setup.TickAsync();
        await setup.TickAsync();

        Assert.False(setup.Votes.IsRunning);
        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void TheVoteIsShortenedSoItEndsBeforeTheVideoDoes() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromMinutes(5));
        setup.Remaining(TimeSpan.FromMinutes(3));

        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        // 180 s left - 6 s of announcements - 10 s safety margin; the next video does not have to wait.
        Assert.Contains("Voting ends 164 seconds after the last option.", setup.Sent[0]);
        Assert.DoesNotContain("next video starts when", setup.Sent[0]);
        setup.Playback.DidNotReceive().HoldNextVideo();
    });

    [Fact]
    public void AShortVideo_GetsATwoMinuteVote_AndTheNextVideoWaitsUntilTheWinnerIsQueued() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromMinutes(3));
        setup.Remaining(TimeSpan.FromSeconds(60)); // e.g. a one-minute video that just started

        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        setup.Playback.Received(1).HoldNextVideo();
        Assert.Contains("Voting ends 2 minutes after the last option. The next video starts when voting has ended.", setup.Sent[0]);

        // The end of the hold is known once the options are: 3 messages 2 s apart, then two minutes.
        var start = setup.Time.GetUtcNow();
        Assert.Equal(start + TimeSpan.FromSeconds(6) + TimeSpan.FromMinutes(2), setup.Hold.Until);

        setup.Votes.Vote(Chat.Command("!vote 3", user: "anna"));
        await setup.RunVoteToTheEndAsync();

        Assert.Equal("c", setup.QueuedVideo());
        Received.InOrder(() =>
        {
            setup.Playback.QueueNextVideo("c");
            setup.Hold.Release();
        });
        setup.Hold.Received(1).Release();
        Assert.Equal(TimeSpan.FromSeconds(126), setup.Time.GetUtcNow() - start);
    });

    [Fact]
    public void AShortVideo_WithAShorterConfiguredVote_KeepsThatLength() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromSeconds(30));
        setup.Remaining(TimeSpan.FromSeconds(25)); // 25 - 16 = 9 s: no room for the 30 s vote

        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        setup.Playback.Received(1).HoldNextVideo();
        Assert.Contains("Voting ends 30 seconds after the last option.", setup.Sent[0]);

        await setup.TickAsync();
        setup.Playback.Received(1).HoldNextVideo(); // one vote per video
    });

    [Fact]
    public void AVideoWithEnoughTimeLeftForTheWholeVote_IsNotHeld() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromSeconds(30));
        setup.Remaining(TimeSpan.FromSeconds(50)); // 50 - 16 = 34 s: the 30 s vote fits

        setup.AutoVote.Enabled = true;
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning && setup.Sent.Count > 0);

        Assert.Contains("Voting ends 30 seconds after the last option.", setup.Sent[0]);
        setup.Playback.DidNotReceive().HoldNextVideo();
    });

    [Fact]
    public void SlowOptionLookups_ComeOutOfTheCountdown_SoTheVoteStillEndsBeforeTheVideoDoes() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromMinutes(5)) { LookupDelay = TimeSpan.FromSeconds(8) };
        setup.Remaining(TimeSpan.FromMinutes(3));
        var start = setup.Time.GetUtcNow();

        setup.AutoVote.Enabled = true;
        await setup.RunVoteToTheEndAsync();

        // The video ends after 180 s and the vote has to be over 10 s before that. Without the lookup time taken
        // out of the countdown the winner is only known after 8 + 6 + 164 = 178 s. (The lookups finish on the thread
        // pool, so they may be noticed a few fake seconds late.)
        Assert.NotNull(setup.QueuedVideo());
        Assert.True(setup.Time.GetUtcNow() - start <= TimeSpan.FromSeconds(175), "the vote ended too late");
    });

    [Fact]
    public void IfTheLookupTakesTooLong_NoVoteStartsAtAll() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromMinutes(5)) { LookupDelay = TimeSpan.FromSeconds(160) };
        setup.Remaining(TimeSpan.FromMinutes(3)); // fine at first: 180 - 16 = 164 s

        setup.AutoVote.Enabled = true;
        for (var i = 0; i < 170; i++)
        {
            setup.Time.Advance(TimeSpan.FromSeconds(1));
            await Async.SettleAsync();
        }

        // 160 s are gone before the options are known: too little is left for a vote of at least 15 s.
        Assert.False(setup.Votes.IsRunning);
        Assert.Empty(setup.Sent);
        Assert.Null(setup.QueuedVideo());
        setup.Playback.DidNotReceive().HoldNextVideo();
    });

    [Fact]
    public void WithoutAKnownLength_ThereIsNoVote() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Length = null; // live stream
        setup.AutoVote.Enabled = true;

        await setup.TickAsync();
        await setup.TickAsync();

        Assert.False(setup.Votes.IsRunning);
    });

    [Fact]
    public void NothingPlaying_MeansNoVote() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Remaining(TimeSpan.FromMinutes(1));
        setup.Current = null;
        setup.AutoVote.Enabled = true;

        await setup.TickAsync();

        Assert.False(setup.Votes.IsRunning);
    });

    [Fact]
    public void WithoutAChatConnection_NoVoteStartsUntilTheBotIsConnected() => UiContext.Run(async () =>
    {
        using var setup = new Setup(connected: false);
        setup.Remaining(TimeSpan.FromMinutes(4));
        setup.AutoVote.Enabled = true;

        await setup.TickAsync();
        Assert.False(setup.Votes.IsRunning);

        setup.Chat.IsConnected.Returns(true);
        await setup.TickAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);
    });

    [Fact]
    public void AVoteThatIsAlreadyRunning_IsNotInterrupted_ButTheAutovoteFollowsAfterIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup(voteDuration: TimeSpan.FromSeconds(15));
        setup.Remaining(TimeSpan.FromMinutes(4));
        await setup.Votes.StartAsync(Chat.Command("!skipvote", user: "mod", moderator: true)); // manual vote
        setup.AutoVote.Enabled = true;

        await setup.TickAsync();
        Assert.DoesNotContain(setup.Sent, m => m.Contains("AFTER"));

        // The manual vote ends (and plays its winner at once); the autovote then takes its turn.
        for (var i = 0; i < 40 && setup.Votes.IsRunning; i++)
        {
            setup.Time.Advance(TimeSpan.FromSeconds(1));
            await Async.SettleAsync();
        }

        await setup.TickAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);
        Assert.Contains(setup.Sent, m => m.Contains("AFTER the current one"));
    });

    [Fact]
    public void Dispose_StopsTheChecks() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.AutoVote.Enabled = true;
        setup.AutoVote.Dispose();
        setup.AutoVote.Dispose(); // idempotent

        setup.Remaining(TimeSpan.FromMinutes(4));
        await setup.TickAsync();
        await setup.TickAsync();

        Assert.False(setup.Votes.IsRunning);
    });
}
