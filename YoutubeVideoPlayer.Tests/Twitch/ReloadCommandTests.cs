using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class ReloadCommandTests
{
    [Fact]
    public void Metadata()
    {
        var command = new ReloadCommand(Substitute.For<IPlaybackControl>(), TimeSpan.FromSeconds(3));

        Assert.Equal("reload", command.Name);
        Assert.True(command.RequiresModerator);
        Assert.False(((YouTubeVideoPlayer.Twitch.IChatCommand)command).IsHidden); // listed in !modvodcommands
        Assert.Equal(TimeSpan.FromSeconds(3), command.Cooldown);
    }

    [Fact]
    public async Task Reloads_AndSaysWhereTheVideoContinues()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(new NowPlaying("v1", "Long video", TimeSpan.FromHours(4)));
        playback.Reload().Returns(ReloadResult.Started);
        playback.ResumePosition.Returns(TimeSpan.FromMinutes(50) + TimeSpan.FromSeconds(28));
        var command = new ReloadCommand(playback, TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!reload", user: "mod"));

        playback.Received(1).Reload();
        Assert.Equal("Reloading \"Long video\", continuing at 50:28 (requested by MOD).", reply);
    }

    [Fact]
    public async Task PositionsFromAnHourOn_HaveHours()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(new NowPlaying("v1", "Long video", null));
        playback.Reload().Returns(ReloadResult.Started);
        playback.ResumePosition.Returns(new TimeSpan(1, 5, 9));

        var reply = await new ReloadCommand(playback, TimeSpan.Zero).ExecuteAsync(Chat.Command("!reload", user: "mod"));

        Assert.Contains("continuing at 1:05:09", reply);
    }

    [Fact]
    public async Task WithoutAVideo_SaysThereIsNothingToReload()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Reload().Returns(ReloadResult.NothingPlaying);

        var reply = await new ReloadCommand(playback, TimeSpan.Zero).ExecuteAsync(Chat.Command("!reload", user: "mod"));

        Assert.Equal("There is no video playing that could be reloaded.", reply);
    }

    [Fact]
    public async Task WhileAVideoIsLoading_AsksToTryAgain()
    {
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(new NowPlaying("v1", "Video", null));
        playback.Reload().Returns(ReloadResult.Busy);

        var reply = await new ReloadCommand(playback, TimeSpan.Zero).ExecuteAsync(Chat.Command("!reload", user: "mod"));

        Assert.Equal("A video is being loaded right now; try !reload again in a moment.", reply);
    }
}
