using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class SkipVoteManagerTests
{
    private const string Dash = "–";

    /// <summary>A vote manager with substitutes; YouTube knows every ID except those starting with "bad".</summary>
    private sealed class Setup : IDisposable
    {
        private readonly int _optionCount;
        private bool _announced;

        public Setup(TimeSpan? duration = null, int optionCount = SkipVoteManager.OptionCount, params string[] pool)
        {
            _optionCount = optionCount;
            Chat = TestSupport.Chat.Channel(out var sent);
            Sent = sent;
            Playback.Current.Returns(new NowPlaying("current", "Current video", null));
            Pool = pool.Length > 0 ? pool.ToList() : new List<string> { "a", "b", "c", "d", "e", "f", "g" };
            Videos.GetNextVideoIds(Arg.Any<int>(), Arg.Any<string?>()).Returns(call =>
            {
                var current = call.ArgAt<string?>(1);
                return Pool.Where(id => id != current).Take(call.ArgAt<int>(0)).ToList();
            });
            YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                return id.StartsWith("bad")
                    ? Task.FromException<YouTubeVideoDetails>(new HttpRequestException("unavailable"))
                    : Task.FromResult(new YouTubeVideoDetails("Title " + id, null));
            });
            Manager = new SkipVoteManager(Chat, Playback, Videos, YouTube, duration ?? TimeSpan.FromMinutes(2), optionCount, Time, new Random(1));
        }

        public FakeTimeProvider Time { get; } = new();

        public IChatChannel Chat { get; }

        public List<string> Sent { get; }

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        public IVideoSource Videos { get; } = Substitute.For<IVideoSource>();

        public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

        public List<string> Pool { get; }

        public SkipVoteManager Manager { get; }

        public Task<string?> StartAsync(string user = "mod") => Manager.StartAsync(TestSupport.Chat.Command("!skipvote", user: user, moderator: true));

        public void Vote(string user, string choice, string? userId = null) =>
            Manager.Vote(TestSupport.Chat.Command("!vote " + choice, user: user, userId: userId));

        /// <summary>
        /// Moves the clock forward in two halves: the second countdown (after the halfway reminder) is only
        /// scheduled once the first one has completed, so one big jump would overshoot it.
        /// </summary>
        public async Task FinishAsync(TimeSpan? duration = null)
        {
            await AnnounceAsync();

            var total = duration ?? TimeSpan.FromMinutes(2);
            Time.Advance(total / 2);
            await Async.SettleAsync();
            Time.Advance(total - total / 2);
            await Async.SettleAsync();
        }

        /// <summary>
        /// Lets the announcement run: the rules message goes out at once, every option follows after
        /// <see cref="SkipVoteManager.MessageDelay"/>. Afterwards the countdown has just started.
        /// </summary>
        public async Task AnnounceAsync()
        {
            if (_announced)
                return;

            _announced = true;
            await Async.SettleAsync();
            for (var i = 0; i < _optionCount; i++)
            {
                Time.Advance(SkipVoteManager.MessageDelay);
                await Async.SettleAsync();
            }
        }

        public string? QueuedVideo() => Playback.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPlaybackControl.QueueNextVideo))
            .Select(c => (string?)c.GetArguments()[0])
            .SingleOrDefault();

        public string? PlayedVideo() => Playback.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPlaybackControl.PlayVideoAsync))
            .Select(c => (string?)c.GetArguments()[0])
            .SingleOrDefault();

        public void Dispose() => Manager.Dispose();
    }

    #region Starting

    [Fact]
    public void Start_OffersThreeRandomVideos_WithoutTheCurrentOne() => UiContext.Run(async () =>
    {
        using var setup = new Setup(pool: new[] { "current", "a", "b", "c", "d", "e", "f" });

        var reply = await setup.StartAsync();
        await setup.AnnounceAsync();

        Assert.Null(reply);
        Assert.True(setup.Manager.IsRunning);
        setup.Videos.Received(1).GetNextVideoIds(9, "current");
        Assert.Equal(
            new[]
            {
                $"1) Title a {Dash} vote with !vote 1",
                $"2) Title b {Dash} vote with !vote 2",
                $"3) Title c {Dash} vote with !vote 3"
            },
            setup.Sent.Skip(1));
    });

    [Fact]
    public void Start_PostsTheRulesFirst_ThenOneOptionPerMessageTwoSecondsApart() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        await setup.StartAsync();
        await Async.SettleAsync();

        Assert.Equal(
            $"Vote for the next video! Pick one of the 3 options below by typing its command {Dash} " +
            "you can change your vote at any time. Voting ends 2 minutes after the last option.",
            Assert.Single(setup.Sent));

        for (var option = 1; option <= 3; option++)
        {
            setup.Time.Advance(SkipVoteManager.MessageDelay - TimeSpan.FromMilliseconds(1));
            await Async.SettleAsync();
            Assert.Equal(option, setup.Sent.Count); // not yet

            setup.Time.Advance(TimeSpan.FromMilliseconds(1));
            await Async.SettleAsync();
            Assert.Equal(option + 1, setup.Sent.Count);
            Assert.StartsWith($"{option}) Title", setup.Sent.Last());
            Assert.EndsWith($"vote with !vote {option}", setup.Sent.Last());
        }

        Assert.Equal(TimeSpan.FromSeconds(2), SkipVoteManager.MessageDelay);
    });

    [Fact]
    public void Voting_IsOpenFromTheVeryStart_EvenWhileTheOptionsAreStillBeingPosted() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();
        await Async.SettleAsync();
        Assert.Single(setup.Sent); // only the rules so far

        setup.Vote("anna", "3");
        setup.Vote("ben", "3");
        await setup.FinishAsync();

        Assert.Equal("c", setup.PlayedVideo());
        Assert.Contains("wins with 2 of 2 votes", setup.Sent.Last());
    });

    [Fact]
    public void TheCountdown_OnlyStartsAfterTheLastOptionWasPosted() => UiContext.Run(async () =>
    {
        using var setup = new Setup(duration: TimeSpan.FromSeconds(30));
        await setup.StartAsync();
        await Async.SettleAsync();

        // One second at a time like a real clock: each delay is only scheduled once the previous one is over.
        async Task StepAsync(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                setup.Time.Advance(TimeSpan.FromSeconds(1));
                await Async.SettleAsync();
            }
        }

        await StepAsync(5); // rules at 0 s, options at 2 s and 4 s; the third is still due
        Assert.Equal(3, setup.Sent.Count);

        await StepAsync(1); // third option at 6 s: the countdown starts now
        Assert.Equal(4, setup.Sent.Count);

        await StepAsync(29); // 35 s after the start, but only 29 s into the countdown
        Assert.True(setup.Manager.IsRunning);
        Assert.Null(setup.PlayedVideo());

        await StepAsync(1);
        Assert.False(setup.Manager.IsRunning);
        Assert.NotNull(setup.PlayedVideo());
    });

    [Fact]
    public void AVoteForTheVideoAfterTheCurrentOne_SaysSo_AndQueuesTheWinner() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        var reply = await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromSeconds(45));
        await setup.AnnounceAsync();

        Assert.Null(reply);
        Assert.Equal(
            $"Vote for the video that plays AFTER the current one (\"Current video\")! Pick one of the 3 options below by typing its command {Dash} " +
            "you can change your vote at any time. Voting ends 45 seconds after the last option.",
            setup.Sent[0]);

        setup.Vote("anna", "3");
        setup.Vote("ben", "3");
        await setup.FinishAsync(TimeSpan.FromSeconds(45));

        // The winner waits for the end of the current video instead of interrupting it.
        Assert.Equal("c", setup.QueuedVideo());
        Assert.Null(setup.PlayedVideo());
        Assert.Equal("Voting ended: \"Title c\" wins with 2 of 2 votes! It plays after the current video.", setup.Sent.Last());
        Assert.False(setup.Manager.IsRunning);
    });

    [Fact]
    public void AVoteForTheVideoAfterTheCurrentOne_NamesThePlaysLaterInEveryResultMessage() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromSeconds(30));
        await setup.FinishAsync(TimeSpan.FromSeconds(30)); // nobody voted

        Assert.StartsWith("Nobody voted", setup.Sent.Last());
        Assert.EndsWith("It plays after the current video.", setup.Sent.Last());
        Assert.NotNull(setup.QueuedVideo());
        Assert.DoesNotContain("Playing it now", string.Join(" ", setup.Sent));
    });

    [Fact]
    public void AVoteForTheVideoAfterTheCurrentOne_TieMessageSaysItPlaysLater() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromSeconds(30));
        setup.Vote("anna", "1");
        setup.Vote("ben", "2");

        await setup.FinishAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("Tie between 1), 2)", setup.Sent.Last());
        Assert.EndsWith("It plays after the current video.", setup.Sent.Last());
    });

    [Fact]
    public void AVoteForTheVideoAfterTheCurrentOne_WithoutKnownTitle_StillSaysAfter() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Playback.Current.Returns((NowPlaying?)null);

        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("Vote for the video that plays AFTER the current one! Pick one", setup.Sent[0]);
    });

    [Fact]
    public void AVoteForTheVideoAfterTheCurrentOne_IsRejectedWhileAnotherVoteRuns() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        var reply = await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromSeconds(30));

        Assert.Equal($"A vote is already running {Dash} vote with !vote 1-3.", reply);
    });

    [Fact]
    public void ARegularVote_StillPlaysItsWinnerAtOnce() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();
        setup.Vote("anna", "2");

        await setup.FinishAsync();

        Assert.Equal("b", setup.PlayedVideo());
        Assert.Null(setup.QueuedVideo());
        Assert.StartsWith("Vote for the next video!", setup.Sent[0]);
    });

    [Fact]
    public void AFailingChat_DoesNotStopTheVoteOrTheWinnerFromPlaying() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Chat.SendMessageAsync(Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("chat is down")));

        await setup.StartAsync();
        setup.Vote("alice", "2");
        await setup.FinishAsync();

        Assert.Equal("b", setup.PlayedVideo());
        Assert.False(setup.Manager.IsRunning);
    });

    [Fact]
    public void AVoteThatFails_EndsItsSession_SoANewVoteCanStart() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Playback.PlayVideoAsync(Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("player failed")));

        await setup.StartAsync();
        await setup.FinishAsync();

        Assert.False(setup.Manager.IsRunning);
        Assert.Null(await setup.StartAsync());
    });

    [Fact]
    public void Start_ReplacesUnavailableVideosWithSpares() => UiContext.Run(async () =>
    {
        using var setup = new Setup(pool: new[] { "bad1", "a", "bad2", "b", "c", "d", "e", "f" });

        await setup.StartAsync();
        await setup.AnnounceAsync();

        Assert.Equal(
            new[]
            {
                $"1) Title a {Dash} vote with !vote 1",
                $"2) Title b {Dash} vote with !vote 2",
                $"3) Title c {Dash} vote with !vote 3"
            },
            setup.Sent.Skip(1));
    });

    [Fact]
    public void Start_TriesEverySpareCandidate_EvenWhenEarlierBatchesWereMostlyUnavailable() => UiContext.Run(async () =>
    {
        // Batch 1 (a, bad1, bad2) finds one video, batch 2 (bad3, bad4) none: "b" is the last spare and still has to be tried.
        using var setup = new Setup(pool: new[] { "a", "bad1", "bad2", "bad3", "bad4", "b" });

        var reply = await setup.StartAsync();
        await setup.AnnounceAsync();

        Assert.Null(reply);
        Assert.Equal(
            new[] { $"1) Title a {Dash} vote with !vote 1", $"2) Title b {Dash} vote with !vote 2" },
            setup.Sent.Skip(1));
    });

    [Fact]
    public void StartAfterCurrent_WithADeadline_ShortensTheCountdownToWhatIsLeft() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        // The vote has to be over in 60 s; posting the three options takes 6 s of that.
        var reply = await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), setup.Time.GetUtcNow() + TimeSpan.FromSeconds(60));
        await Async.SettleAsync();

        Assert.Null(reply);
        Assert.Contains("Voting ends 54 seconds after the last option.", setup.Sent[0]);
    });

    [Fact]
    public void StartAfterCurrent_WithTooLittleTimeBeforeTheDeadline_DoesNotStartAVote() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        var reply = await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), setup.Time.GetUtcNow() + TimeSpan.FromSeconds(20));
        await Async.SettleAsync();

        Assert.Equal("There is not enough time left for a vote.", reply);
        Assert.False(setup.Manager.IsRunning);
        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void Start_SkipsVideosWithoutTitle() => UiContext.Run(async () =>
    {
        using var setup = new Setup(optionCount: 2, pool: new[] { "untitled", "a", "b" });
        setup.YouTube.GetVideoAsync("untitled", Arg.Any<CancellationToken>()).Returns(new YouTubeVideoDetails("  ", null));

        await setup.StartAsync();
        await setup.AnnounceAsync();

        Assert.Equal(
            new[] { $"1) Title a {Dash} vote with !vote 1", $"2) Title b {Dash} vote with !vote 2" },
            setup.Sent.Skip(1));
        Assert.StartsWith("Vote for the next video! Pick one of the 2 options", setup.Sent[0]);
    });

    [Fact]
    public void Start_WithFewerThanTwoPlayableVideos_ReportsIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup(pool: new[] { "a", "bad" });

        var reply = await setup.StartAsync();

        Assert.Equal("There are not enough videos available for a vote.", reply);
        Assert.False(setup.Manager.IsRunning);
        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void Start_WhenPickingVideosFails_ReportsIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Videos.GetNextVideoIds(Arg.Any<int>(), Arg.Any<string?>()).Throws(new InvalidOperationException("broken"));

        var reply = await setup.StartAsync();

        Assert.Equal("The vote could not be started.", reply);
        Assert.False(setup.Manager.IsRunning);
    });

    [Fact]
    public void Start_WhileAVoteRuns_IsRejected() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        var reply = await setup.StartAsync();

        Assert.Equal($"A vote is already running {Dash} vote with !vote 1-3.", reply);
        Assert.Single(setup.Sent);
    });

    [Fact]
    public void Start_WhileAVoteIsBeingPrepared_IsRejected() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var lookup = new TaskCompletionSource<YouTubeVideoDetails>();
        setup.YouTube.GetVideoAsync("a", Arg.Any<CancellationToken>()).Returns(lookup.Task);

        var first = setup.StartAsync();
        var second = await setup.StartAsync();
        lookup.SetResult(new YouTubeVideoDetails("Title a", null));
        await first;

        Assert.Equal("A vote is already being prepared.", second);
    });

    [Fact]
    public void LongTitles_AreShortened_SoEveryMessageFitsIntoTheChat() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new YouTubeVideoDetails(call.ArgAt<string>(0) + new string('x', 200), null));

        await setup.StartAsync();
        await setup.AnnounceAsync();

        Assert.Equal(4, setup.Sent.Count);
        Assert.All(setup.Sent, message => Assert.True(message.Length <= 500));
        Assert.StartsWith("1) a" + new string('x', 68) + "…", setup.Sent[1]);
    });

    #endregion

    #region Voting and results

    [Fact]
    public void TheOptionWithMostVotesWins_AndEachUserCountsOnce() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        setup.Vote("anna", "2");
        setup.Vote("ben", "2");
        setup.Vote("cleo", "3");
        setup.Vote("anna", "3"); // changes her vote
        await setup.FinishAsync();

        Assert.Equal("Voting ended: \"Title c\" wins with 2 of 3 votes! Playing it now.", setup.Sent.Last());
        Assert.Equal("c", setup.PlayedVideo());
        Assert.False(setup.Manager.IsRunning);
    });

    [Theory]
    [InlineData("#3")]
    [InlineData("3)")]
    [InlineData("3.")]
    public void VoteNumbers_MayHaveDecoration(string choice) => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        setup.Vote("anna", choice);
        await setup.FinishAsync();

        Assert.Equal("c", setup.PlayedVideo());
        Assert.Contains("wins with 1 of 1 vote!", setup.Sent.Last());
    });

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("two")]
    [InlineData("")]
    public void InvalidVotes_AreIgnored(string choice) => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        setup.Vote("anna", choice);
        await setup.FinishAsync();

        Assert.StartsWith("Nobody voted", setup.Sent.Last());
    });

    [Fact]
    public void Votes_AreKeyedByUserId_OrByNameWithoutId() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        setup.Vote("anna", "1", userId: "42");
        setup.Vote("anna_renamed", "2", userId: "42"); // same person, replaces the vote
        setup.Vote("ben", "2", userId: "");
        setup.Vote("ben", "2", userId: "");             // still one vote
        await setup.FinishAsync();

        Assert.Contains("\"Title b\" wins with 2 of 2 votes!", setup.Sent.Last());
    });

    [Fact]
    public void VotesWithoutRunningVote_AreIgnored() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        setup.Vote("anna", "1");

        Assert.False(setup.Manager.IsRunning);
    });

    [Fact]
    public void ATie_IsDecidedRandomlyAmongTheLeaders() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        setup.Vote("anna", "1");
        setup.Vote("ben", "3");
        setup.Vote("cleo", "3");
        setup.Vote("dora", "1");
        setup.Vote("emil", "2");
        await setup.FinishAsync();

        Assert.StartsWith($"Tie between 1), 3) with 2 votes each {Dash} randomly picked: \"Title ", setup.Sent.Last());
        Assert.Contains(setup.PlayedVideo(), new[] { "a", "c" });
        Assert.Contains($"\"Title {setup.PlayedVideo()}\"! Playing it now.", setup.Sent.Last());
    });

    [Fact]
    public void WithoutVotes_ARandomOptionIsPlayed() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();

        await setup.FinishAsync();

        Assert.StartsWith($"Nobody voted {Dash} randomly picked: \"Title ", setup.Sent.Last());
        Assert.Contains(setup.PlayedVideo(), new[] { "a", "b", "c" });
    });

    [Fact]
    public void TheVoteEndsExactlyAfterItsDuration() => UiContext.Run(async () =>
    {
        using var setup = new Setup(duration: TimeSpan.FromSeconds(30));
        await setup.StartAsync();

        await setup.FinishAsync(TimeSpan.FromSeconds(29));
        Assert.True(setup.Manager.IsRunning);
        Assert.Equal(4, setup.Sent.Count); // rules + 3 options, no reminder for short votes

        await setup.FinishAsync(TimeSpan.FromSeconds(1));
        Assert.False(setup.Manager.IsRunning);
        Assert.NotNull(setup.PlayedVideo());
    });

    [Fact]
    public void HalfwayReminder_ShowsTheStandings() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();
        setup.Vote("anna", "2");
        setup.Vote("ben", "1");
        setup.Vote("cleo", "2");

        await setup.FinishAsync(TimeSpan.FromMinutes(1));

        Assert.Equal($"1 minute left! Current standings: 2) 2 votes, 1) 1 vote {Dash} vote with !vote 1-3.", setup.Sent.Last());
        Assert.True(setup.Manager.IsRunning);
    });

    [Fact]
    public void HalfwayReminder_WithoutVotes() => UiContext.Run(async () =>
    {
        using var setup = new Setup(duration: TimeSpan.FromMinutes(6));
        await setup.StartAsync();

        await setup.FinishAsync(TimeSpan.FromMinutes(3));

        Assert.Equal($"3 minutes left! No votes yet {Dash} vote with !vote 1-3.", setup.Sent.Last());
    });

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "90 seconds")]
    [InlineData(300, "5 minutes")]
    public void TheAnnouncementNamesTheDuration(int seconds, string expected) => UiContext.Run(async () =>
    {
        using var setup = new Setup(duration: TimeSpan.FromSeconds(seconds));

        await setup.StartAsync();

        Assert.Contains($"Voting ends {expected} after the last option.", setup.Sent[0]);
    });

    #endregion

    #region Cancelling

    [Fact]
    public void Cancel_StopsTheVoteWithoutPlayingAnything() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();
        setup.Vote("anna", "1");

        var reply = setup.Manager.Cancel(Chat.Command("!cancelvote", user: "streamer", broadcaster: true));
        await setup.FinishAsync(TimeSpan.FromMinutes(5));

        Assert.Equal("The vote was cancelled by STREAMER.", reply);
        Assert.False(setup.Manager.IsRunning);
        Assert.Null(setup.PlayedVideo());
        Assert.Single(setup.Sent);
    });

    [Fact]
    public void Cancel_WhileTheOptionsAreBeingPosted_StopsTheAnnouncement() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.StartAsync();
        await Async.SettleAsync();
        setup.Time.Advance(SkipVoteManager.MessageDelay);
        await Async.SettleAsync();
        Assert.Equal(2, setup.Sent.Count); // rules and option 1

        setup.Manager.Cancel(Chat.Command("!cancelvote", user: "mod"));
        await setup.FinishAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(2, setup.Sent.Count);
        Assert.Null(setup.PlayedVideo());
    });

    [Fact]
    public void Cancel_WithoutVote_SaysSo() => UiContext.Run(() =>
    {
        using var setup = new Setup();

        Assert.Equal("There is no vote running.", setup.Manager.Cancel(Chat.Command("!cancelvote")));
    });

    [Fact]
    public void Cancel_WhilePreparing_PreventsTheAnnouncement() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var lookup = new TaskCompletionSource<YouTubeVideoDetails>();
        setup.YouTube.GetVideoAsync("a", Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.ArgAt<CancellationToken>(1).Register(() => lookup.TrySetCanceled());
            return lookup.Task;
        });

        var start = setup.StartAsync();
        await Async.SettleAsync();
        var reply = setup.Manager.Cancel(Chat.Command("!cancelvote", user: "mod"));
        var startReply = await start;
        await setup.FinishAsync();

        Assert.Equal("The vote was cancelled by MOD.", reply);
        Assert.Null(startReply);
        Assert.Empty(setup.Sent);
        Assert.False(setup.Manager.IsRunning);
    });

    [Fact]
    public void Dispose_CancelsARunningVote() => UiContext.Run(async () =>
    {
        var setup = new Setup();
        await setup.StartAsync();

        setup.Dispose();
        setup.Dispose();
        await setup.FinishAsync();

        Assert.False(setup.Manager.IsRunning);
        Assert.Null(setup.PlayedVideo());
    });

    #endregion

    #region Holding the next video

    [Fact]
    public void AHeldVote_HoldsTheNextVideoBeforeTheOptionsAreLookedUp_AndKnowsWhenItEnds() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var hold = Substitute.For<IPlaybackHold>();
        setup.Playback.HoldNextVideo().Returns(hold);
        var lookup = new TaskCompletionSource<YouTubeVideoDetails>();
        setup.YouTube.GetVideoAsync("a", Arg.Any<CancellationToken>()).Returns(lookup.Task);

        var start = setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), holdNextVideo: true);
        await Async.SettleAsync();
        setup.Playback.Received(1).HoldNextVideo(); // a short video may end during the lookup
        Assert.Null(hold.Until);

        setup.Time.Advance(TimeSpan.FromSeconds(3)); // the lookup takes 3 s
        lookup.SetResult(new YouTubeVideoDetails("Title a", null));
        Assert.Null(await start);
        var optionsKnown = setup.Time.GetUtcNow();
        Assert.Equal(optionsKnown + SkipVoteManager.AnnouncementDuration + TimeSpan.FromMinutes(2), hold.Until);

        await setup.FinishAsync();
        Assert.Equal("a", setup.QueuedVideo());
        hold.Received(1).Release();
    });

    [Fact]
    public void Cancelling_AHeldVote_ReleasesTheHold() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        var hold = Substitute.For<IPlaybackHold>();
        setup.Playback.HoldNextVideo().Returns(hold);
        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), holdNextVideo: true);

        setup.Manager.Cancel(Chat.Command("!cancelvote", user: "mod"));
        await setup.FinishAsync();

        hold.Received(1).Release();
        Assert.Null(setup.QueuedVideo());
    });

    [Fact]
    public void AHeldVoteThatCannotStart_ReleasesTheHold() => UiContext.Run(async () =>
    {
        using var setup = new Setup(pool: new[] { "only-one" });
        var hold = Substitute.For<IPlaybackHold>();
        setup.Playback.HoldNextVideo().Returns(hold);

        var reply = await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), holdNextVideo: true);

        Assert.Equal("There are not enough videos available for a vote.", reply);
        hold.Received(1).Release();
    });

    [Fact]
    public void Dispose_DoesNotReleaseTheHold_SoNothingStartsWhileTheAppCloses() => UiContext.Run(async () =>
    {
        var setup = new Setup();
        var hold = Substitute.For<IPlaybackHold>();
        setup.Playback.HoldNextVideo().Returns(hold);
        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2), holdNextVideo: true);

        setup.Dispose();
        await setup.FinishAsync();

        hold.DidNotReceive().Release();
    });

    [Fact]
    public void ARegularVote_DoesNotHoldTheNextVideo() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        await setup.Manager.StartAfterCurrentAsync(TimeSpan.FromMinutes(2));
        await setup.StartAsync();

        setup.Playback.DidNotReceive().HoldNextVideo();
    });

    #endregion
}

public class VoteCommandTests
{
    private static (SkipVoteManager Votes, IChatChannel Chat) CreateVotes()
    {
        var chat = Chat.Channel(out _);
        var videos = Substitute.For<IVideoSource>();
        videos.GetNextVideoIds(Arg.Any<int>(), Arg.Any<string?>()).Returns(new[] { "a", "b" });
        var youtube = Substitute.For<IYouTubeService>();
        youtube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new YouTubeVideoDetails("t", null));
        return (new SkipVoteManager(chat, Substitute.For<IPlaybackControl>(), videos, youtube, TimeSpan.FromMinutes(2), 2, new FakeTimeProvider()), chat);
    }

    [Fact]
    public void Metadata()
    {
        var (votes, _) = CreateVotes();

        IChatCommand[] commands = { new SkipVoteCommand(votes), new CancelVoteCommand(votes), new VoteCommand(votes) };

        Assert.Equal(new[] { "skipvote", "cancelvote", "vote" }, commands.Select(c => c.Name));
        Assert.Equal(new[] { true, true, false }, commands.Select(c => c.RequiresModerator));
        Assert.All(commands, c => Assert.Equal(TimeSpan.Zero, c.Cooldown));
    }

    [Fact]
    public void CommandsDelegateToTheVoteManager() => UiContext.Run(async () =>
    {
        var (votes, _) = CreateVotes();

        Assert.Null(await new SkipVoteCommand(votes).ExecuteAsync(Chat.Command("!skipvote")));
        Assert.True(votes.IsRunning);
        Assert.Null(await new VoteCommand(votes).ExecuteAsync(Chat.Command("!vote 1")));
        Assert.Equal("The vote was cancelled by MOD.", await new CancelVoteCommand(votes).ExecuteAsync(Chat.Command("!cancelvote", user: "mod")));
        Assert.False(votes.IsRunning);
    });
}
