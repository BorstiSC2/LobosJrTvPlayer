using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TimestampCommandTests
{
    private static (TimestampCommand Command, IPlaybackControl Playback) Create(
        string? title, TimeSpan position, TimeSpan? length, bool playing = true)
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(title == null ? null : new NowPlaying("v1", title, length));
        playback.IsPlaying.Returns(playing);
        playback.Position.Returns(position);
        playback.Length.Returns(length);
        return (new TimestampCommand(playback, TimeSpan.FromSeconds(5)), playback);
    }

    [Fact]
    public void Metadata()
    {
        var (command, _) = Create("x", TimeSpan.Zero, null);

        Assert.Equal("timestamp", command.Name);
        Assert.False(command.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), command.Cooldown);
    }

    [Fact]
    public async Task ReportsPositionLengthAndRemainingTime()
    {
        var (command, _) = Create("Elden Ring", TimeSpan.FromSeconds(201), TimeSpan.FromSeconds(645));

        var reply = await command.ExecuteAsync(Chat.Command("!timestamp"));

        Assert.Equal("\"Elden Ring\" – 03:21 / 10:45 (07:24 left)", reply);
    }

    [Fact]
    public async Task UsesHoursForLongVideos()
    {
        var (command, _) = Create("Marathon", new TimeSpan(1, 2, 3), new TimeSpan(10, 0, 0));

        var reply = await command.ExecuteAsync(Chat.Command("!timestamp"));

        Assert.Equal("\"Marathon\" – 1:02:03 / 10:00:00 (8:57:57 left)", reply);
    }

    [Fact]
    public async Task WithoutKnownLength_ReportsOnlyThePosition()
    {
        var (command, _) = Create("Live", TimeSpan.FromSeconds(75), null);

        Assert.Equal("\"Live\" – 01:15", await command.ExecuteAsync(Chat.Command("!timestamp")));
    }

    [Fact]
    public async Task RemainingTime_NeverGoesNegative()
    {
        var (command, _) = Create("x", TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(60));

        Assert.EndsWith("(00:00 left)", await command.ExecuteAsync(Chat.Command("!timestamp")));
    }

    [Fact]
    public async Task UntitledVideo_IsCalledTheCurrentVideo()
    {
        var (command, _) = Create("  ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        Assert.StartsWith("The current video – 00:01", await command.ExecuteAsync(Chat.Command("!timestamp")));
    }

    [Theory]
    [InlineData(true, false)]  // nothing loaded
    [InlineData(false, true)]  // loaded but not playing
    public async Task WhenNothingPlays_SaysSo(bool noVideo, bool notPlaying)
    {
        var (command, _) = Create(noVideo ? null : "x", TimeSpan.Zero, null, playing: !notPlaying);

        Assert.Equal("Nothing is playing right now.", await command.ExecuteAsync(Chat.Command("!timestamp")));
    }
}

public class CurrentCommandTests
{
    private static CurrentCommand Create(string? videoId, string title = "Elden Ring", bool playing = true)
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(videoId == null ? null : new NowPlaying(videoId, title, null));
        playback.IsPlaying.Returns(playing);
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
    public async Task PostsTheTitleAndALinkToTheVideo()
    {
        var reply = await Create("dQw4w9WgXcQ").ExecuteAsync(Chat.Command("!current"));

        Assert.Equal("Now playing: \"Elden Ring\" – https://www.youtube.com/watch?v=dQw4w9WgXcQ", reply);
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
        var reply = await Create("v1", title: " ").ExecuteAsync(Chat.Command("!current"));

        Assert.Equal("Now playing: The current video – https://www.youtube.com/watch?v=v1", reply);
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

public class SkipCommandTests
{
    [Fact]
    public void Metadata()
    {
        var command = new SkipCommand(Substitute.For<IPlaybackControl>(), TimeSpan.FromSeconds(3));

        Assert.Equal("skip", command.Name);
        Assert.True(command.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(3), command.Cooldown);
    }

    [Fact]
    public async Task SkipsAndNamesTheSkippedVideoAndTheRequester()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(new NowPlaying("v1", "Boring video", null));
        var command = new SkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!skip", user: "mod"));

        await playback.Received(1).SkipAsync();
        Assert.Equal("Skipped \"Boring video\" (requested by MOD).", reply);
    }

    [Fact]
    public async Task WithoutCurrentVideo_UsesAGenericReply()
    {
        var playback = Substitute.For<IPlaybackControl>();
        var command = new SkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!skip", user: "mod"));

        Assert.Equal("Skipped to the next video (requested by MOD).", reply);
    }

    [Fact]
    public void RepliesImmediately_EvenIfLoadingTheNextVideoFails() => UiContext.Run(async () =>
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.SkipAsync().ThrowsAsync(new InvalidOperationException("no videos"));
        var command = new SkipCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!skip"));
        await Async.SettleAsync();

        Assert.NotNull(reply);
    });
}
