using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class CurrentCommandTests
{
    private static CurrentCommand Create(
        string? videoId, string title = "Elden Ring", bool playing = true,
        TimeSpan position = default, TimeSpan? length = null)
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(videoId == null ? null : new NowPlaying(videoId, title, length));
        playback.IsPlaying.Returns(playing);
        playback.Position.Returns(position);
        playback.Length.Returns(length);
        return new CurrentCommand(playback, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Metadata()
    {
        var command = Create("v1");

        Assert.Equal("current", command.Name);
        Assert.False(command.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), command.Cooldown);
    }

    [Fact]
    public async Task PostsTheTitleThePositionAndALinkToTheVideo()
    {
        var command = Create("dQw4w9WgXcQ", position: TimeSpan.FromSeconds(201), length: TimeSpan.FromSeconds(645));

        var reply = await command.ExecuteAsync(Chat.Command("!current"));

        Assert.Equal(
            "Now playing: \"Elden Ring\" – 03:21 / 10:45 (07:24 left) – https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            reply);
    }

    [Theory]
    [InlineData("v1", true, 1)]   // answers about a video: shown on stream too
    [InlineData("v1", false, 0)]  // "Nothing is playing right now."
    [InlineData(null, true, 0)]
    public async Task BringsUpTheNowPlayingCard_OnlyWithAnAnswerAboutAVideo(string? videoId, bool playing, int shown)
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(videoId == null ? null : new NowPlaying(videoId, "Elden Ring", null));
        playback.IsPlaying.Returns(playing);
        var calls = 0;
        var command = new CurrentCommand(playback, TimeSpan.FromSeconds(5), () => calls++);

        await command.ExecuteAsync(Chat.Command("!current"));

        Assert.Equal(shown, calls);
    }

    [Fact]
    public async Task UsesHoursForLongVideos()
    {
        var command = Create("v1", title: "Marathon", position: new TimeSpan(1, 2, 3), length: new TimeSpan(10, 0, 0));

        var reply = await command.ExecuteAsync(Chat.Command("!current"));

        Assert.Equal("Now playing: \"Marathon\" – 1:02:03 / 10:00:00 (8:57:57 left) – https://www.youtube.com/watch?v=v1", reply);
    }

    [Fact]
    public async Task WithoutKnownLength_ReportsOnlyThePosition()
    {
        var reply = await Create("v1", title: "Live", position: TimeSpan.FromSeconds(75)).ExecuteAsync(Chat.Command("!current"));

        Assert.Equal("Now playing: \"Live\" – 01:15 – https://www.youtube.com/watch?v=v1", reply);
    }

    [Fact]
    public async Task RemainingTime_NeverGoesNegative()
    {
        var command = Create("v1", position: TimeSpan.FromSeconds(70), length: TimeSpan.FromSeconds(60));

        Assert.Contains("(00:00 left)", await command.ExecuteAsync(Chat.Command("!current")));
    }

    [Fact]
    public async Task KeepsAFullUrlAsItIs()
    {
        var reply = await Create("https://www.youtube.com/watch?v=abc").ExecuteAsync(Chat.Command("!current"));

        Assert.EndsWith(" – https://www.youtube.com/watch?v=abc", reply);
    }

    [Fact]
    public async Task UntitledVideo_IsCalledTheCurrentVideo()
    {
        var reply = await Create("v1", title: " ", position: TimeSpan.FromSeconds(1)).ExecuteAsync(Chat.Command("!current"));

        Assert.Equal("Now playing: The current video – 00:01 – https://www.youtube.com/watch?v=v1", reply);
    }

    [Theory]
    [InlineData(true, false)]  // nothing loaded
    [InlineData(false, true)]  // loaded but not playing
    public async Task WhenNothingPlays_SaysSo(bool noVideo, bool notPlaying)
    {
        var command = Create(noVideo ? null : "v1", playing: !notPlaying);

        Assert.Equal("Nothing is playing right now.", await command.ExecuteAsync(Chat.Command("!current")));
    }
}

public class ForceSkipCommandTests
{
    [Fact]
    public void Metadata()
    {
        var command = new ForceSkipCommand(Substitute.For<IPlaybackControl>(), TimeSpan.FromSeconds(3));

        Assert.Equal("forceskip", command.Name);
        Assert.True(command.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(3), command.Cooldown);
    }

    [Fact]
    public async Task SkipsAndNamesTheSkippedVideoAndTheRequester()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(new NowPlaying("v1", "Boring video", null));
        var command = new ForceSkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!forceskip", user: "mod"));

        await playback.Received(1).SkipAsync();
        Assert.Equal("Skipped \"Boring video\" (requested by MOD).", reply);
    }

    [Fact]
    public async Task WithoutCurrentVideo_UsesAGenericReply()
    {
        var playback = Substitute.For<IPlaybackControl>();
        var command = new ForceSkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!forceskip", user: "mod"));

        Assert.Equal("Skipped to the next video (requested by MOD).", reply);
    }

    [Fact]
    public void RepliesImmediately_EvenIfLoadingTheNextVideoFails() => UiContext.Run(async () =>
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.SkipAsync().ThrowsAsync(new InvalidOperationException("no videos"));
        var command = new ForceSkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!forceskip"));
        await Async.SettleAsync();

        Assert.NotNull(reply);
    });
}
