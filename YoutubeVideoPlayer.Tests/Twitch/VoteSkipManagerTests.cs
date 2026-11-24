using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class VoteSkipManagerTests
{
    private const string Dash = "–";
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    /// <summary>A vote-skip on top of a real vote manager for the next video; the player and YouTube are substitutes.</summary>
    private sealed class Setup : IDisposable
    {
        public Setup(params string[] pool)
        {
            Chat = TestSupport.Chat.Channel(out var sent);
            Sent = sent;
            Playback.Current.Returns(_ => Current);
            var ids = pool.Length > 0 ? pool : new[] { "a", "b", "c", "d", "e", "f" };
            Videos.GetNextVideoIds(Arg.Any<int>(), Arg.Any<string?>()).Returns(call => ids.Take(call.ArgAt<int>(0)).ToList());
            YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new YouTubeVideoDetails("Title " + call.ArgAt<string>(0), null)));

            Votes = new SkipVoteManager(Chat, Playback, Videos, YouTube, TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, Time, new Random(1));
            VoteSkip = new VoteSkipManager(Chat, Playback, Votes, () => Average, Duration, Cooldown, 75, Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public IChatChannel Chat { get; }

        public List<string> Sent { get; }

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        public IVideoSource Videos { get; } = Substitute.For<IVideoSource>();

        public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

        public SkipVoteManager Votes { get; }

        public VoteSkipManager VoteSkip { get; }

        /// <summary>The rolling average of unique chatters the manager sees.</summary>
        public double Average { get; set; } = 4; // 75 % → 3 votes

        public NowPlaying? Current { get; set; } = new NowPlaying("current", "Boring video", null);

        public string? Start(string user = "viewer") => VoteSkip.Start(TestSupport.Chat.Command("!voteskip", user: user));

        public void Skip(string user) => VoteSkip.Vote(TestSupport.Chat.Command("!skip", user: user), skip: true);

        public void Stay(string user) => VoteSkip.Vote(TestSupport.Chat.Command("!stay", user: user), skip: false);

        /// <summary>
        /// Runs the vote to its end in two halves: the second countdown (after the halfway reminder) is only scheduled
        /// once the first one has completed.
        /// </summary>
        public async Task FinishAsync()
        {
            await Async.SettleAsync();
            Time.Advance(Duration / 2);
            await Async.SettleAsync();
            Time.Advance(Duration - Duration / 2);
            await Async.SettleAsync();
        }

        public void ChangeVideo(NowPlaying video)
        {
            Current = video;
            Playback.NowPlayingChanged += Raise.Event<EventHandler<NowPlaying>>(Playback, video);
        }

        public void Dispose()
        {
            VoteSkip.Dispose();
            Votes.Dispose();
        }
    }

    #region Starting

    [Fact]
    public void Start_AnnouncesTheBallotsAndTheVotesNeeded() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        Assert.Null(setup.Start());
        await Async.SettleAsync();

        Assert.True(setup.VoteSkip.IsRunning);
        Assert.Equal(
            new[]
            {
                "VIEWER wants to skip \"Boring video\"! Type !skip or !stay. Skipping needs more !skip than !stay and at least " +
                "3 !skip votes (75% of ~4 recent chatters). Voting ends in 1 minute."
            },
            setup.Sent);
    });

    [Theory]
    [InlineData(0, 1)]     // nobody chatted: the starter alone is enough
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]     // 3.75 rounds up
    [InlineData(8, 6)]
    [InlineData(2.5, 2)]   // 1.875 rounds up
    public void RequiredVotes_AreTheShareOfTheAverage_RoundedUp_AtLeastOne(double average, int expected) => UiContext.Run(() =>
    {
        using var setup = new Setup();
        Assert.Equal(expected, setup.VoteSkip.RequiredVotes(average));
    });

    [Fact]
    public void Start_TakesTheAverageOnce_ChattersDuringTheVoteDoNotRaiseTheBar() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        setup.Start();

        setup.Average = 100;
        await setup.FinishAsync();

        Assert.Contains("The chat voted to skip \"Boring video\" (!skip 1, !stay 0)!", setup.Sent);
    });

    [Fact]
    public void Start_IsRefused_WhileAVoteSkipRuns() => UiContext.Run(() =>
    {
        using var setup = new Setup();
        setup.Start();

        Assert.Equal($"A !voteskip vote is already running {Dash} type !skip or !stay.", setup.Start("other"));
    });

    [Fact]
    public void Start_IsRefused_WhileAVoteForTheNextVideoRuns() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.Votes.StartAsync(Chat.Command("!forcevoteskip", user: "mod", moderator: true));

        Assert.Equal($"A vote is already running {Dash} !voteinfo shows it.", setup.Start());
        Assert.False(setup.VoteSkip.IsRunning);
    });

    [Fact]
    public void Start_IsRefused_WhileNothingPlays() => UiContext.Run(() =>
    {
        using var setup = new Setup();

        setup.Current = null;
        Assert.Equal("There is no video to skip right now.", setup.Start());

        setup.Current = new NowPlaying("v", "Prepared video", null);
        setup.Playback.IsStandby.Returns(true);
        Assert.Equal("There is no video to skip right now.", setup.Start());

        setup.Playback.IsStandby.Returns(false);
        setup.Playback.IsWaitingForHold.Returns(true);
        Assert.Equal("There is no video to skip right now.", setup.Start());

        Assert.False(setup.VoteSkip.IsRunning);
    });

    #endregion

    #region Results

    [Fact]
    public void SkipWins_WithEnoughVotes_StartsTheVoteForTheNextVideo_WithoutCooldown() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start("viewer"); // the starter counts as !skip
        setup.Skip("b");
        setup.Skip("c");
        setup.Stay("d");

        await setup.FinishAsync();
        await Async.WaitUntilAsync(() => setup.Sent.Any(m => m.StartsWith("Vote for the next video!")));

        Assert.Contains("The chat voted to skip \"Boring video\" (!skip 3, !stay 1)!", setup.Sent);
        Assert.False(setup.VoteSkip.IsRunning);
        Assert.True(setup.Votes.IsRunning);
        Assert.Equal(TimeSpan.Zero, setup.VoteSkip.CooldownRemaining);
        await setup.Playback.DidNotReceive().SkipAsync();
    });

    [Fact]
    public void SkipWins_TheTickerHandsOverToTheVoteForTheNextVideo_WithoutAGap() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        var lookup = new TaskCompletionSource<YouTubeVideoDetails>();
        setup.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup.Task);
        var shown = new List<VoteKind?>();
        void Record(object? sender, EventArgs e) => shown.Add((setup.VoteSkip.Display ?? setup.Votes.Display)?.Kind);
        setup.VoteSkip.DisplayChanged += Record;
        setup.Votes.DisplayChanged += Record;
        setup.Start();
        setup.Stay("b");
        setup.Skip("c");

        await setup.FinishAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);

        // The skip vote is gone, the vote for the next video shows while its options are looked up.
        Assert.False(setup.VoteSkip.IsRunning);
        Assert.Null(setup.VoteSkip.Display);
        Assert.Empty(setup.Votes.Display!.Options);

        lookup.SetResult(new YouTubeVideoDetails("Title", null));
        await Async.WaitUntilAsync(() => setup.Votes.Display!.Options.Count > 0);

        Assert.DoesNotContain(null, shown); // never nothing in between
        Assert.Equal(VoteKind.Skip, shown.First());
        Assert.Equal(VoteKind.NextVideo, shown.Last());
    });

    [Fact]
    public void WhileTheResultIsPosted_BallotsAndAVideoChange_DoNotStopTheVoteForTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        var send = new TaskCompletionSource();
        setup.Chat.SendMessageAsync(Arg.Is<string>(m => m.StartsWith("The chat voted to skip"))).Returns(send.Task);
        setup.Start();
        await setup.FinishAsync(); // the starter's !skip wins

        // The result is being posted: the decided vote stays (and on the ticker) until the next vote shows.
        Assert.True(setup.VoteSkip.IsRunning);
        Assert.NotNull(setup.VoteSkip.Display);
        setup.Stay("late");
        setup.ChangeVideo(new NowPlaying("next", "Next video", null));
        Assert.True(setup.VoteSkip.IsRunning);
        Assert.Equal((1, 0), (setup.VoteSkip.Display!.Options[0].Votes, setup.VoteSkip.Display.Options[1].Votes));
        send.SetResult();

        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);
        Assert.False(setup.VoteSkip.IsRunning);
        Assert.DoesNotContain(setup.Sent, m => m.StartsWith("The video changed"));
    });

    [Fact]
    public void ACancelWhileTheResultIsPosted_StartsNoVoteForTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        var send = new TaskCompletionSource();
        setup.Chat.SendMessageAsync(Arg.Is<string>(m => m.StartsWith("The chat voted to skip"))).Returns(send.Task);
        setup.Start();
        await setup.FinishAsync();

        Assert.NotNull(setup.VoteSkip.Cancel(Chat.Command("!cancelvote", user: "mod")));
        send.SetResult();
        await Async.SettleAsync();

        Assert.False(setup.VoteSkip.IsRunning);
        Assert.False(setup.Votes.IsRunning);
        await setup.Playback.DidNotReceive().SkipAsync();
    });

    [Fact]
    public void AfterASuccessfulSkip_VoteSkipCanBeUsedAgain_OnceTheVoteForTheNextVideoIsOver() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        setup.Start();
        await setup.FinishAsync();
        await Async.WaitUntilAsync(() => setup.Votes.IsRunning);

        Assert.StartsWith("A vote is already running", setup.Start("other"));

        setup.Votes.Cancel(Chat.Command("!cancelvote", user: "mod"));
        Assert.Null(setup.Start("other"));
    });

    [Fact]
    public void StayWins_StartsTheCooldown() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        setup.Stay("b");
        setup.Stay("c");

        await setup.FinishAsync();

        Assert.Equal(
            $"The chat voted to stay (!stay 2, !skip 1) {Dash} \"Boring video\" keeps playing. !voteskip can be used again in 30 minutes.",
            setup.Sent.Last());
        Assert.False(setup.Votes.IsRunning);
        await setup.Playback.DidNotReceive().SkipAsync();

        Assert.Equal("!voteskip can be used again in 30 minutes.", setup.Start());
        setup.Time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal("!voteskip can be used again in 1 minute.", setup.Start());
        setup.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("!voteskip can be used again in 30 seconds.", setup.Start());
        setup.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(setup.Start());
    });

    [Fact]
    public void SkipWins_ButBelowTheShareOfTheChatters_TheVideoStays_AndTheCooldownStarts() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 8; // 6 votes needed
        setup.Start();
        setup.Skip("b");
        setup.Stay("c");

        await setup.FinishAsync();

        Assert.Equal(
            $"!skip won 2:1, but it needed 6 !skip votes {Dash} \"Boring video\" stays. !voteskip can be used again in 30 minutes.",
            setup.Sent.Last());
        Assert.False(setup.Votes.IsRunning);
        Assert.Equal(Cooldown, setup.VoteSkip.CooldownRemaining);
    });

    [Fact]
    public void ATie_CountsAsStay() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        setup.Start();
        setup.Stay("b");

        await setup.FinishAsync();

        Assert.StartsWith("The chat voted to stay (!stay 1, !skip 1)", setup.Sent.Last());
        Assert.False(setup.Votes.IsRunning);
    });

    [Fact]
    public void ALaterBallot_ReplacesTheEarlierOne_TheStarterToo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Average = 1;
        setup.Start("viewer");
        setup.Skip("b");
        setup.Stay("b");
        setup.Stay("viewer");

        await setup.FinishAsync();

        Assert.StartsWith("The chat voted to stay (!stay 2, !skip 0)", setup.Sent.Last());
    });

    [Fact]
    public void HalfwayThrough_TheChatIsReminded() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        setup.Stay("b");
        await Async.SettleAsync();

        setup.Time.Advance(Duration / 2);
        await Async.SettleAsync();

        Assert.Equal($"30 seconds left to vote on skipping \"Boring video\": !skip 1, !stay 1 {Dash} type !skip or !stay.", setup.Sent.Last());
        Assert.True(setup.VoteSkip.IsRunning);
    });

    [Fact]
    public void WhenTheVoteForTheNextVideoCannotStart_TheVideoIsSkipped() => UiContext.Run(async () =>
    {
        using var setup = new Setup("only-one");
        setup.Average = 1;
        setup.Start();

        await setup.FinishAsync();
        await Async.WaitUntilAsync(() => setup.Sent.Contains("Skipping to the next video."));

        await setup.Playback.Received(1).SkipAsync();
        Assert.Equal(TimeSpan.Zero, setup.VoteSkip.CooldownRemaining);
    });

    #endregion

    #region Interruptions

    [Fact]
    public void WhenTheVideoChanges_TheVoteEnds_WithoutResultOrCooldown() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        await Async.SettleAsync();

        setup.ChangeVideo(new NowPlaying("next", "Next video", null));
        await Async.SettleAsync();
        var sent = setup.Sent.Count;
        await setup.FinishAsync();

        Assert.False(setup.VoteSkip.IsRunning);
        Assert.Contains($"The video changed {Dash} the !voteskip vote has ended.", setup.Sent);
        Assert.Equal(sent, setup.Sent.Count);
        Assert.Equal(TimeSpan.Zero, setup.VoteSkip.CooldownRemaining);
        Assert.Null(setup.Start());
    });

    [Fact]
    public void TheSameVideoReportedAgain_DoesNotEndTheVote() => UiContext.Run(() =>
    {
        using var setup = new Setup();
        setup.Start();

        setup.ChangeVideo(setup.Current!);

        Assert.True(setup.VoteSkip.IsRunning);
    });

    [Fact]
    public void CancelVote_EndsTheVoteSkip_WithoutCooldown() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        await Async.SettleAsync();
        var sent = setup.Sent.Count;

        var reply = await new CancelVoteCommand(setup.Votes, setup.VoteSkip).ExecuteAsync(Chat.Command("!cancelvote", user: "mod"));
        await setup.FinishAsync();

        Assert.Equal("The !voteskip vote was cancelled by MOD.", reply);
        Assert.False(setup.VoteSkip.IsRunning);
        Assert.Equal(sent, setup.Sent.Count);
        Assert.Equal(TimeSpan.Zero, setup.VoteSkip.CooldownRemaining);
    });

    [Fact]
    public void CancelVote_WithoutAVoteSkip_CancelsTheVoteForTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.Votes.StartAsync("mod");

        var reply = await new CancelVoteCommand(setup.Votes, setup.VoteSkip).ExecuteAsync(Chat.Command("!cancelvote", user: "mod"));

        Assert.Equal("The vote was cancelled by MOD.", reply);
        Assert.False(setup.Votes.IsRunning);
    });

    [Fact]
    public void Dispose_EndsTheVoteSilently() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        await Async.SettleAsync();
        var sent = setup.Sent.Count;

        setup.VoteSkip.Dispose();
        await setup.FinishAsync();

        Assert.False(setup.VoteSkip.IsRunning);
        Assert.Equal(sent, setup.Sent.Count);
        Assert.Null(setup.Start());
    });

    #endregion

    #region Commands

    [Fact]
    public void Commands_Metadata() => UiContext.Run(() =>
    {
        using var setup = new Setup();

        IChatCommand[] commands =
        {
            new VoteSkipCommand(setup.VoteSkip, TimeSpan.FromSeconds(5)),
            new SkipBallotCommand(setup.VoteSkip),
            new StayBallotCommand(setup.VoteSkip),
            new VoteInfoCommand(setup.Votes, setup.VoteSkip, TimeSpan.FromSeconds(30)),
            new ForceVoteSkipCommand(setup.Votes, setup.VoteSkip)
        };

        Assert.Equal(new[] { "voteskip", "skip", "stay", "voteinfo", "forcevoteskip" }, commands.Select(c => c.Name));
        Assert.Equal(new[] { false, false, false, false, true }, commands.Select(c => c.RequiresModerator));
        Assert.Equal(new[] { false, true, true, false, false }, commands.Select(c => c.IsHidden));
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.Zero },
            commands.Select(c => c.Cooldown));
    });

    [Fact]
    public void Ballots_AreCountedByTheCommands_AndIgnoredWithoutAVote() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var skip = new SkipBallotCommand(setup.VoteSkip);
        var stay = new StayBallotCommand(setup.VoteSkip);

        Assert.Null(await stay.ExecuteAsync(Chat.Command("!stay", user: "early")));
        Assert.Null(await new VoteSkipCommand(setup.VoteSkip, TimeSpan.Zero).ExecuteAsync(Chat.Command("!voteskip")));
        Assert.Null(await skip.ExecuteAsync(Chat.Command("!skip", user: "b")));
        Assert.Null(await stay.ExecuteAsync(Chat.Command("!stay", user: "c")));

        Assert.Contains("!skip 2, !stay 1", setup.VoteSkip.DescribeForChat());
    });

    [Fact]
    public void ForceVoteSkip_IsRefused_WhileAVoteSkipRuns() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();

        var reply = await new ForceVoteSkipCommand(setup.Votes, setup.VoteSkip).ExecuteAsync(Chat.Command("!forcevoteskip", user: "mod"));

        Assert.Equal($"A !voteskip vote is running {Dash} wait for it, or end it with !cancelvote.", reply);
        Assert.False(setup.Votes.IsRunning);
    });

    #endregion

    #region Display for the vote ticker

    [Fact]
    public void Display_ShowsSkipAndStay_WithTheVotesNeeded() => UiContext.Run(() =>
    {
        using var setup = new Setup();
        var changes = 0;
        setup.VoteSkip.DisplayChanged += (_, _) => changes++;
        Assert.Null(setup.VoteSkip.Display);

        setup.Start();
        setup.Stay("b");
        setup.Stay("b"); // the same ballot again
        setup.Time.Advance(TimeSpan.FromSeconds(15));

        var display = setup.VoteSkip.Display!;
        Assert.Equal(2, changes); // the start and the first !stay
        Assert.Equal(VoteKind.Skip, display.Kind);
        Assert.Equal("Boring video", display.Subject);
        Assert.Equal(
            new[] { ((string?)null, "!skip", "!skip", 1), (null, "!stay", "!stay", 1) },
            display.Options.Select(o => (o.Number, o.Title, o.Command, o.Votes)));
        Assert.Equal(3, display.Required);
        Assert.Equal(TimeSpan.FromSeconds(45), display.TimeLeft);
    });

    [Fact]
    public void Display_IsGone_AfterTheResult_AVideoChange_OrACancel() => UiContext.Run(async () =>
    {
        using (var setup = new Setup())
        {
            setup.Start();
            await setup.FinishAsync();
            Assert.Null(setup.VoteSkip.Display);
        }

        using (var setup = new Setup())
        {
            setup.Start();
            var changed = false;
            setup.VoteSkip.DisplayChanged += (_, _) => changed = true;
            setup.ChangeVideo(new NowPlaying("next", "Next video", null));
            Assert.True(changed);
            Assert.Null(setup.VoteSkip.Display);
        }

        using (var setup = new Setup())
        {
            setup.Start();
            setup.VoteSkip.Cancel(Chat.Command("!cancelvote", user: "mod"));
            Assert.Null(setup.VoteSkip.Display);
        }
    });

    #endregion

    #region !voteinfo

    private static Task<string?> VoteInfoAsync(Setup setup) =>
        new VoteInfoCommand(setup.Votes, setup.VoteSkip, TimeSpan.FromSeconds(30)).ExecuteAsync(Chat.Command("!voteinfo"));

    [Fact]
    public void VoteInfo_WithoutAVote() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        Assert.Equal("No vote is running right now.", await VoteInfoAsync(setup));
    });

    [Fact]
    public void VoteInfo_DuringAVoteSkip_ShowsTheStandingAndTheTimeLeft() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Start();
        setup.Stay("b");
        setup.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(
            $"Vote to skip \"Boring video\": !skip 1, !stay 1 {Dash} skipping needs 3 !skip votes and more !skip than !stay. 0:45 left.",
            await VoteInfoAsync(setup));
    });

    [Fact]
    public void VoteInfo_WhileTheOptionsAreLookedUp() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var lookup = new TaskCompletionSource<YouTubeVideoDetails>();
        setup.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup.Task);

        _ = setup.Votes.StartAsync("mod");
        await Async.SettleAsync();

        Assert.Equal("A vote for the next video is being prepared.", await VoteInfoAsync(setup));
        setup.Votes.Dispose();
    });

    [Fact]
    public void VoteInfo_RepeatsTheOptions_WhileTheyArePostedAndDuringTheCountdown() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.Votes.StartAsync("mod");

        Assert.Equal(
            $"Vote for the next video! 1) Title a, 2) Title b, 3) Title c {Dash} vote with !vote 1-3. Voting ends 2 minutes after the last option.",
            await VoteInfoAsync(setup));

        for (var i = 0; i < SkipVoteManager.OptionCount; i++)
        {
            await Async.SettleAsync();
            setup.Time.Advance(SkipVoteManager.MessageDelay);
        }

        await Async.SettleAsync();
        setup.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(
            $"Vote for the next video! 1) Title a, 2) Title b, 3) Title c {Dash} vote with !vote 1-3. 1:50 left.",
            await VoteInfoAsync(setup));
    });

    #endregion
}
