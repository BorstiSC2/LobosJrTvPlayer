using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class AutoVoteCommandTests
{
    private const string On = "Autovote is ON: a vote for the next video starts 5 minutes before the current video ends.";
    private const string Off = "Autovote is OFF: no vote starts by itself.";

    private static IAutoVoteControl Control(bool enabled)
    {
        var control = Substitute.For<IAutoVoteControl>();
        control.Enabled = enabled; // a substitute property keeps what is set
        return control;
    }

    [Fact]
    public void Metadata_TheStatusIsForEverybody_TheToggleOnlyForModeratorsBroadcasterAndPrivilegedUsers()
    {
        var control = Control(false);
        IChatCommand status = new AutoVoteStatusCommand(control, TimeSpan.FromSeconds(5));
        IChatCommand toggle = new ToggleAutoVoteCommand(control, TimeSpan.FromSeconds(5));

        Assert.Equal("autovotestatus", status.Name);
        Assert.False(status.RequiresModerator);
        Assert.Equal("toggleautovote", toggle.Name);
        Assert.True(toggle.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), status.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(5), toggle.Cooldown);
    }

    [Theory]
    [InlineData(true, On)]
    [InlineData(false, Off)]
    public async Task Status_ReportsTheCurrentValue_WithoutChangingIt(bool enabled, string expected)
    {
        var control = Control(enabled);

        var reply = await new AutoVoteStatusCommand(control, TimeSpan.Zero).ExecuteAsync(Chat.Command("!autovotestatus"));

        Assert.Equal(expected, reply);
        Assert.Equal(enabled, control.Enabled);
    }

    [Fact]
    public async Task Toggle_SwitchesToTheOppositeValue_AndPostsTheNewOne()
    {
        var control = Control(false);
        var command = new ToggleAutoVoteCommand(control, TimeSpan.Zero);

        var first = await command.ExecuteAsync(Chat.Command("!toggleautovote", moderator: true));
        Assert.True(control.Enabled);
        Assert.Equal("Autovote is now ON: a vote for the next video starts 5 minutes before the current video ends.", first);

        var second = await command.ExecuteAsync(Chat.Command("!toggleautovote", moderator: true));
        Assert.False(control.Enabled);
        Assert.Equal("Autovote is now OFF: no vote starts by itself.", second);
    }

    [Fact]
    public void EverybodyCanAskTheStatus_OnlyModeratorsBroadcasterAndPrivilegedUsersCanToggle_ThroughTheCommandManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var control = Control(false);
        using var manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new AutoVoteStatusCommand(control, TimeSpan.Zero), new ToggleAutoVoteCommand(control, TimeSpan.Zero) },
            new[] { "friend" },
            new FakeTimeProvider());

        chat.Say("!toggleautovote", user: "viewer");
        await Async.SettleAsync();
        Assert.Empty(sent);
        Assert.False(control.Enabled);

        chat.Say("!autovotestatus", user: "viewer");
        await Async.SettleAsync();
        Assert.Equal(new[] { Off }, sent);

        chat.Say("!toggleautovote", user: "mod", moderator: true);
        await Async.SettleAsync();
        Assert.True(control.Enabled);

        chat.Say("!toggleautovote", user: "friend"); // listed in privilegedUsers
        await Async.SettleAsync();
        Assert.False(control.Enabled);
        Assert.Equal(3, sent.Count);
    });

    [Fact]
    public void TheCommandListsPickUpTheNewCommandsOnTheirOwn() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var control = Control(false);
        ChatCommandManager? manager = null;
        manager = new ChatCommandManager(
            chat,
            new IChatCommand[]
            {
                new AutoVoteStatusCommand(control, TimeSpan.Zero),
                new ToggleAutoVoteCommand(control, TimeSpan.Zero),
                new VodCommandsCommand(() => manager!.Commands, TimeSpan.Zero),
                new ModVodCommandsCommand(() => manager!.Commands, TimeSpan.Zero)
            },
            Array.Empty<string>());

        chat.Say("!vodcommands");
        chat.Say("!modvodcommands", moderator: true);
        await Async.SettleAsync();
        manager.Dispose();

        Assert.Equal(new[] { "Commands: !autovotestatus", "Moderator commands: !toggleautovote" }, sent);
    });

    [Fact]
    public void TheAutoVoteManager_ImplementsTheControl_AndAnnouncesEveryChange() => UiContext.Run(() =>
    {
        var chat = Chat.Channel(out _);
        var votes = new SkipVoteManager(
            chat, Substitute.For<IPlaybackControl>(), Substitute.For<IVideoSource>(), Substitute.For<IYouTubeService>(),
            TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, new FakeTimeProvider());
        using var autoVote = new AutoVoteManager(chat, Substitute.For<IPlaybackControl>(), votes, new FakeTimeProvider());
        IAutoVoteControl control = autoVote;
        var raised = 0;
        autoVote.EnabledChanged += (_, _) => raised++;

        control.Enabled = true;
        control.Enabled = true; // no change, no event
        control.Enabled = false;

        Assert.Equal(2, raised);
        votes.Dispose();
    });
}
