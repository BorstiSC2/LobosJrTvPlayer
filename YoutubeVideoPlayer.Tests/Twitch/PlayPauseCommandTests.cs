using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class PlayPauseCommandTests
{
    private static IPlaybackControl Playback(
        string? title = "Elden Ring", bool paused = false, bool standby = false, bool waiting = false)
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(title == null ? null : new NowPlaying("v1", title, null));
        playback.IsPaused.Returns(paused);
        playback.IsStandby.Returns(standby);
        playback.IsWaitingForHold.Returns(waiting);
        return playback;
    }

    private static Task<string?> PlayAsync(IPlaybackControl playback) =>
        new PlayCommand(playback, TimeSpan.Zero).ExecuteAsync(Chat.Command("!play", user: "mod", moderator: true));

    private static Task<string?> PauseAsync(IPlaybackControl playback) =>
        new PauseCommand(playback, TimeSpan.Zero).ExecuteAsync(Chat.Command("!pause", user: "mod", moderator: true));

    [Fact]
    public void Metadata_BothAreForModeratorsOnly()
    {
        var play = new PlayCommand(Playback(), TimeSpan.FromSeconds(3));
        var pause = new PauseCommand(Playback(), TimeSpan.FromSeconds(3));

        Assert.Equal("play", play.Name);
        Assert.Equal("pause", pause.Name);
        Assert.True(play.RequiresModerator);
        Assert.True(pause.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(3), play.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(3), pause.Cooldown);
    }

    [Fact]
    public async Task Pause_PausesThePlayingVideo()
    {
        var playback = Playback();

        var reply = await PauseAsync(playback);

        playback.Received(1).Pause();
        Assert.Equal("Paused \"Elden Ring\" (requested by MOD) – continue with !play.", reply);
    }

    [Fact]
    public async Task Pause_WhenAlreadyPaused_SaysSo()
    {
        var playback = Playback(paused: true);

        Assert.Equal("Already paused \"Elden Ring\" – continue with !play.", await PauseAsync(playback));
        playback.DidNotReceive().Pause();
    }

    [Theory]
    [InlineData(true, "Elden Ring")]
    [InlineData(false, null)]
    public async Task Pause_WithNothingPlaying_DoesNothing(bool standby, string? title)
    {
        var playback = Playback(title, standby: standby);

        Assert.Equal("Nothing is playing.", await PauseAsync(playback));
        playback.DidNotReceive().Pause();
    }

    [Fact]
    public async Task Pause_WhileTheNextVideoWaitsForAVote_DoesNothing()
    {
        var playback = Playback(waiting: true);

        Assert.Equal("Nothing is playing – the next video starts when the vote is over.", await PauseAsync(playback));
        playback.DidNotReceive().Pause();
    }

    [Fact]
    public async Task Play_ContinuesAPausedVideo()
    {
        var playback = Playback(paused: true);

        var reply = await PlayAsync(playback);

        playback.Received(1).Resume();
        Assert.Equal("Continuing \"Elden Ring\" (requested by MOD).", reply);
    }

    [Fact]
    public async Task Play_WhileStandingBy_StartsTheFirstVideo()
    {
        var playback = Playback(title: null, standby: true);

        var reply = await PlayAsync(playback);

        await playback.Received(1).PlayNextAsync();
        Assert.Equal("Playback started (requested by MOD).", reply);
    }

    [Fact]
    public async Task Play_WhileAVideoPlays_SaysSo()
    {
        var playback = Playback();

        Assert.Equal("Already playing \"Elden Ring\".", await PlayAsync(playback));
        playback.DidNotReceive().Resume();
        await playback.DidNotReceive().PlayNextAsync();
    }

    [Fact]
    public async Task Play_WhileTheNextVideoWaitsForAVote_PointsToSkip()
    {
        var playback = Playback(waiting: true);

        Assert.Equal("The next video starts when the vote is over (!skip starts it now).", await PlayAsync(playback));
        await playback.DidNotReceive().PlayNextAsync();
    }

    [Fact]
    public async Task Play_BeforeAnythingIsLoaded_SaysSo()
    {
        var playback = Playback(title: null);

        Assert.Equal("There is no video to play yet.", await PlayAsync(playback));
    }

    [Fact]
    public void OnlyModeratorsAndPrivilegedUsers_CanUseThem() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var playback = Playback();
        using var commands = new ChatCommandManager(
            chat, new IChatCommand[] { new PauseCommand(playback, TimeSpan.Zero) }, new[] { "helper" });

        chat.Say("!pause", user: "viewer");
        await Async.SettleAsync();
        playback.DidNotReceive().Pause();
        Assert.Empty(sent);

        chat.Say("!pause", user: "helper"); // in the settings' list of privileged users
        await Async.WaitUntilAsync(() => sent.Count == 1);
        playback.Received(1).Pause();
    });
}
