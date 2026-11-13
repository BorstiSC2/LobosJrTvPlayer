using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests;

/// <summary>
/// The automatic swap with the real managers (playlists, playback, coordinator, votes, autovote, announcement); only
/// YouTube, the player, the chat and the clock are replaced.
/// </summary>
public class PlaylistSwapEndToEndTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan VideoLength = TimeSpan.FromMinutes(20);

    [Fact]
    public void TheVideoPlaysToItsEnd_TheAutovoteOffersVideosOfTheNextPlaylist_AndTheBotAnnouncesItWhenItBegins() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider(Start);
        using var playlists = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1", "a2", "a3", "a4")
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1", "b2", "b3", "b4");
        playlists.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new ResolvedVideo("Title " + call.ArgAt<string>(0), VideoLength, call.ArgAt<string>(0) + ".video", null));
        playlists.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new YouTubeVideoDetails("Title " + call.ArgAt<string>(0), VideoLength));

        // The position follows the clock from the start of each video, and moves on every call so the watchdog never
        // takes the video for frozen when one big step of the clock fires several of its checks at once.
        var player = Substitute.For<IMediaPlayer>();
        var videoStart = time.GetUtcNow();
        var reads = 0L;
        player.IsPlaying.Returns(true);
        player.LengthMs.Returns((long)VideoLength.TotalMilliseconds);
        player.TimeMs.Returns(_ => (long)(time.GetUtcNow() - videoStart).TotalMilliseconds + ++reads);
        player.When(p => p.Play(Arg.Any<string>(), Arg.Any<string?>())).Do(_ => videoStart = time.GetUtcNow());
        List<string> Played() => player.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play))
            .Select(c => ((string)c.GetArguments()[0]!).Replace(".video", ""))
            .ToList();

        var manager = playlists.Create(time);
        using var playback = new PlaybackManager(playlists.YouTube, manager, player, time);
        var chat = Chat.Channel(out var sent);
        using var votes = new SkipVoteManager(
            chat, playback, manager, playlists.YouTube, TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, time, new Random(1));
        using var autoVote = new AutoVoteManager(chat, playback, votes, time);
        using var announcer = new PlaylistAnnouncer(chat, manager, playback);
        using var coordinator = new AppCoordinator(playback, manager, null);
        using var scheduler = new PlaylistSwapScheduler(manager, playback, () => votes.IsRunning, time);

        async Task StepAsync(TimeSpan by)
        {
            await Async.SettleAsync();
            time.Advance(by);
            await Async.SettleAsync();
        }

        async Task StepUntilAsync(Func<bool> condition, TimeSpan step, int maxSteps, string because)
        {
            for (var i = 0; i < maxSteps && !condition(); i++)
                await StepAsync(step);
            Assert.True(condition(), because);
        }

        // The app opens paused; the swap every hour is turned on, and 50 minutes later Play starts a video of Alpha.
        await coordinator.StartAsync();
        manager.SetAutoSwap(true, TimeSpan.FromHours(1));
        autoVote.Enabled = true;
        await StepAsync(TimeSpan.FromMinutes(50));
        Assert.Equal(IdA, manager.SelectedKey);
        var play = playback.PlayNextAsync();
        await StepAsync(PlaybackManager.DelayBeforeNextVideo); // the prepared first video has expired by now
        await play;
        var first = Assert.Single(Played());
        Assert.StartsWith("a", first);

        // The swap falls due 10 minutes into the video. It plays on, and 5½ minutes before its end Beta takes over.
        await StepAsync(TimeSpan.FromMinutes(14));
        Assert.True(manager.IsSwapDue);
        Assert.Equal(IdA, manager.SelectedKey);
        await StepUntilAsync(() => manager.SelectedKey == IdB, TimeSpan.FromSeconds(5), 20, "Beta should take over before the end");
        Assert.True(playback.Length - playback.Position > AutoVoteManager.LeadTime);
        Assert.Equal(new[] { first }, Played()); // not skipped
        Assert.DoesNotContain(sent, m => m.StartsWith("New Playlist"));

        // Five minutes before the end the autovote starts: its options come from Beta.
        await StepUntilAsync(() => votes.IsRunning, TimeSpan.FromSeconds(5), 20, "the autovote should start");
        await StepUntilAsync(() => sent.Count(m => m.Contains("– vote with !vote")) == 3, TimeSpan.FromSeconds(1), 20, "all options should be posted");
        var options = sent.Where(m => m.Contains("– vote with !vote")).ToList();
        Assert.All(options, option => Assert.Contains("Title b", option));

        // The winner is queued, and starts when the video ends.
        await StepUntilAsync(() => !votes.IsRunning && playback.HasQueuedVideo, TimeSpan.FromSeconds(2), 100, "the vote should end with a winner");
        await StepAsync(TimeSpan.FromSeconds(5)); // the winner is prepared in advance
        Assert.Equal(new[] { first }, Played());

        player.EndReached += Raise.Event();
        await StepUntilAsync(() => Played().Count == 2, TimeSpan.FromSeconds(1), 5, "the winner should play");

        Assert.StartsWith("b", Played()[1]);
        Assert.Equal("New Playlist has begun: Beta", sent.Last());
        Assert.Single(sent, m => m.StartsWith("New Playlist"));
    });
}
