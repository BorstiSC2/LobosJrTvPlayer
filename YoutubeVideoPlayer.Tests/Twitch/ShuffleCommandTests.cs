using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class ShuffleCommandTests
{
    private static IShuffleControl Control(bool shuffled, string? title = "Lets Play", bool canChange = true)
    {
        var control = Substitute.For<IShuffleControl>();
        control.IsShuffled.Returns(shuffled);
        control.PlaylistTitle.Returns(title);
        control.CanChangeShuffle.Returns(canChange);
        return control;
    }

    [Fact]
    public void Metadata_BothCommandsAreForModeratorsBroadcasterAndPrivilegedUsersOnly()
    {
        var control = Control(true);
        IChatCommand[] commands =
        {
            new ShuffleStatusCommand(control, TimeSpan.FromSeconds(3)),
            new ToggleShuffleCommand(control, TimeSpan.FromSeconds(3))
        };

        Assert.Equal(new[] { "shufflestatus", "toggleshuffle" }, commands.Select(c => c.Name));
        Assert.All(commands, c => Assert.True(c.RequiresModerator));
        Assert.All(commands, c => Assert.Equal(TimeSpan.FromSeconds(3), c.Cooldown));
    }

    [Theory]
    [InlineData(true, "Shuffle is ON for \"Lets Play\": the videos play in random order.")]
    [InlineData(false, "Shuffle is OFF for \"Lets Play\": the videos play in playlist order.")]
    public async Task Status_SaysWhetherThePlaylistIsShuffled(bool shuffled, string expected)
    {
        var command = new ShuffleStatusCommand(Control(shuffled), TimeSpan.Zero);

        Assert.Equal(expected, await command.ExecuteAsync(Chat.Command("!shufflestatus", moderator: true)));
    }

    [Fact]
    public async Task Status_DoesNotChangeAnything()
    {
        var control = Control(true);

        await new ShuffleStatusCommand(control, TimeSpan.Zero).ExecuteAsync(Chat.Command("!shufflestatus"));

        control.DidNotReceive().ToggleShuffle();
    }

    [Fact]
    public async Task Status_ForAllPlaylistsCombined_ExplainsThatTheyAlwaysPlayInRandomOrder()
    {
        var command = new ShuffleStatusCommand(Control(true, title: null, canChange: false), TimeSpan.Zero);

        var reply = await command.ExecuteAsync(Chat.Command("!shufflestatus"));

        Assert.Equal("All playlists are combined and always play in random order. Select a single playlist to change the order.", reply);
    }

    [Fact]
    public async Task Status_WithoutPlaylistTitle_SaysTheCurrentPlaylist()
    {
        var command = new ShuffleStatusCommand(Control(false, title: " "), TimeSpan.Zero);

        Assert.Equal(
            "Shuffle is OFF for the current playlist: the videos play in playlist order.",
            await command.ExecuteAsync(Chat.Command("!shufflestatus")));
    }

    [Theory]
    [InlineData(true, false, "Shuffle is now OFF for \"Lets Play\": the videos play in playlist order.")]
    [InlineData(false, true, "Shuffle is now ON for \"Lets Play\": the videos play in random order.")]
    public async Task Toggle_SwitchesTheOrder_AndPostsTheNewStatus(bool before, bool after, string expected)
    {
        var control = Control(before);
        control.ToggleShuffle().Returns(_ =>
        {
            control.IsShuffled.Returns(after); // what the playlist manager does
            return after;
        });

        var reply = await new ToggleShuffleCommand(control, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleshuffle", moderator: true));

        control.Received(1).ToggleShuffle();
        Assert.Equal(expected, reply);
    }

    [Fact]
    public async Task Toggle_ForAllPlaylistsCombined_ChangesNothingAndExplainsWhy()
    {
        var control = Control(true, title: null, canChange: false);

        var reply = await new ToggleShuffleCommand(control, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleshuffle"));

        control.DidNotReceive().ToggleShuffle();
        Assert.Contains("always play in random order", reply);
        Assert.Contains("Select a single playlist", reply);
    }

    [Fact]
    public async Task Toggle_WhenSavingFails_SaysSoInsteadOfClaimingASuccess()
    {
        var control = Control(true);
        control.ToggleShuffle().Throws(new IOException("disk full"));

        var reply = await new ToggleShuffleCommand(control, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleshuffle"));

        Assert.Equal("The shuffle setting could not be changed.", reply);
    }

    [Fact]
    public void OnlyModeratorsBroadcasterAndPrivilegedUsersCanUseThem_ThroughTheCommandManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var control = Control(true);
        control.ToggleShuffle().Returns(false);
        using var manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new ShuffleStatusCommand(control, TimeSpan.Zero), new ToggleShuffleCommand(control, TimeSpan.Zero) },
            new[] { "friend" },
            new FakeTimeProvider());

        chat.Say("!toggleshuffle", user: "viewer");
        chat.Say("!shufflestatus", user: "viewer");
        await Async.SettleAsync();
        Assert.Empty(sent);
        control.DidNotReceive().ToggleShuffle();

        chat.Say("!shufflestatus", user: "viewer2", moderator: true);
        chat.Say("!toggleshuffle", user: "streamer", broadcaster: true);
        chat.Say("!toggleshuffle", user: "friend"); // listed in privilegedUsers
        await Async.SettleAsync();

        Assert.Equal(3, sent.Count);
        control.Received(2).ToggleShuffle();
    });
}
