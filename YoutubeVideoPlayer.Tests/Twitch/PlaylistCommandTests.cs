using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class PlaylistCommandTests
{
    private static IShuffleControl Playlist(bool shuffled, string? title = "Lets Play", bool single = true)
    {
        var control = Substitute.For<IShuffleControl>();
        control.IsShuffled.Returns(shuffled);
        control.PlaylistTitle.Returns(title);
        control.CanChangeShuffle.Returns(single);
        return control;
    }

    private static IPlaylistSwitch Switch(string? next)
    {
        var playlistSwitch = Substitute.For<IPlaylistSwitch>();
        playlistSwitch.HasOtherPlaylist.Returns(next != null);
        playlistSwitch.SwitchToNextPlaylistAsync().Returns(next);
        return playlistSwitch;
    }

    [Fact]
    public void Metadata_PlaylistIsForEverybody_SkipPlaylistOnlyForModeratorsBroadcasterAndPrivilegedUsers()
    {
        IChatCommand[] commands =
        {
            new PlaylistCommand(Playlist(true), TimeSpan.FromSeconds(5)),
            new SkipPlaylistCommand(Switch("Beta"), TimeSpan.FromSeconds(3))
        };

        Assert.Equal(new[] { "playlist", "skipplaylist" }, commands.Select(c => c.Name));
        Assert.False(commands[0].RequiresModerator);
        Assert.True(commands[1].RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), commands[0].Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(3), commands[1].Cooldown);
    }

    [Theory]
    [InlineData(true, "Current playlist: \"Lets Play\" (shuffled).")]
    [InlineData(false, "Current playlist: \"Lets Play\" (in playlist order).")]
    public async Task Playlist_NamesThePlaylistAndItsOrder(bool shuffled, string expected)
    {
        var reply = await new PlaylistCommand(Playlist(shuffled), TimeSpan.Zero).ExecuteAsync(Chat.Command("!playlist"));

        Assert.Equal(expected, reply);
    }

    [Fact]
    public async Task Playlist_ForAllPlaylistsCombined_SaysSo()
    {
        var reply = await new PlaylistCommand(Playlist(true, title: null, single: false), TimeSpan.Zero).ExecuteAsync(Chat.Command("!playlist"));

        Assert.Equal("Current playlist: all playlists combined (shuffled).", reply);
    }

    [Fact]
    public async Task SkipPlaylist_SwitchesToTheNextPlaylist_AndLeavesTheAnnouncementToTheBot()
    {
        var playlistSwitch = Switch("Beta");

        var reply = await new SkipPlaylistCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!skipplaylist", moderator: true));

        await playlistSwitch.Received(1).SwitchToNextPlaylistAsync();
        Assert.Null(reply); // "New Playlist has begun: Beta" follows when its first video starts
    }

    [Fact]
    public async Task SkipPlaylist_WithoutAnotherPlaylist_SaysSo()
    {
        var playlistSwitch = Switch(null);

        var reply = await new SkipPlaylistCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!skipplaylist", moderator: true));

        await playlistSwitch.DidNotReceive().SwitchToNextPlaylistAsync();
        Assert.Equal("There is no other playlist to switch to.", reply);
    }

    [Fact]
    public async Task SkipPlaylist_WhenTheSwitchFails_SaysSo()
    {
        var playlistSwitch = Switch("Beta");
        playlistSwitch.SwitchToNextPlaylistAsync().ThrowsAsync(new IOException("disk full"));

        var reply = await new SkipPlaylistCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!skipplaylist", moderator: true));

        Assert.Equal("The playlist could not be changed.", reply);
    }

    [Fact]
    public void EverybodyCanAskForThePlaylist_OnlyPrivilegedUsersCanSkipIt_ThroughTheCommandManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var playlistSwitch = Switch("Beta");
        using var manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new PlaylistCommand(Playlist(false), TimeSpan.Zero), new SkipPlaylistCommand(playlistSwitch, TimeSpan.Zero) },
            new[] { "friend" },
            new FakeTimeProvider());

        chat.Say("!skipplaylist", user: "viewer");
        chat.Say("!playlist", user: "viewer");
        await Async.SettleAsync();
        await playlistSwitch.DidNotReceive().SwitchToNextPlaylistAsync();
        Assert.Equal(new[] { "Current playlist: \"Lets Play\" (in playlist order)." }, sent);

        chat.Say("!skipplaylist", user: "mod", moderator: true);
        chat.Say("!skipplaylist", user: "streamer", broadcaster: true);
        chat.Say("!skipplaylist", user: "friend"); // listed in privilegedUsers
        await Async.SettleAsync();

        await playlistSwitch.Received(3).SwitchToNextPlaylistAsync();
    });

    [Fact]
    public void Metadata_PlaylistShuffleStatusIsForEverybody_TheToggleOnlyForModeratorsBroadcasterAndPrivilegedUsers()
    {
        IChatCommand[] commands =
        {
            new PlaylistShuffleStatusCommand(Switch("Beta"), TimeSpan.FromSeconds(3)),
            new TogglePlaylistShuffleCommand(Switch("Beta"), TimeSpan.FromSeconds(3))
        };

        Assert.Equal(new[] { "playlistshufflestatus", "toggleplaylistshuffle" }, commands.Select(c => c.Name));
        Assert.False(commands[0].RequiresModerator);
        Assert.True(commands[1].RequiresModerator);
        Assert.All(commands, c => Assert.Equal(TimeSpan.FromSeconds(3), c.Cooldown));
    }

    [Theory]
    [InlineData(true, "Playlist shuffle is ON: the next playlist is picked at random.")]
    [InlineData(false, "Playlist shuffle is OFF: the playlists take turns in the order of the list.")]
    public async Task PlaylistShuffleStatus_SaysHowTheNextPlaylistIsChosen(bool random, string expected)
    {
        var playlistSwitch = Switch("Beta");
        playlistSwitch.RandomOrder.Returns(random);

        var reply = await new PlaylistShuffleStatusCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!playlistshufflestatus"));

        Assert.Equal(expected, reply);
        playlistSwitch.DidNotReceive().ToggleRandomOrder();
    }

    [Theory]
    [InlineData(true, "Playlist shuffle is now ON: the next playlist is picked at random.")]
    [InlineData(false, "Playlist shuffle is now OFF: the playlists take turns in the order of the list.")]
    public async Task TogglePlaylistShuffle_SwitchesIt_AndPostsTheNewValue(bool after, string expected)
    {
        var playlistSwitch = Switch("Beta");
        playlistSwitch.ToggleRandomOrder().Returns(after);

        var reply = await new TogglePlaylistShuffleCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleplaylistshuffle", moderator: true));

        playlistSwitch.Received(1).ToggleRandomOrder();
        Assert.Equal(expected, reply);
    }

    [Fact]
    public async Task TogglePlaylistShuffle_WhenSavingFails_SaysSo()
    {
        var playlistSwitch = Switch("Beta");
        playlistSwitch.ToggleRandomOrder().Throws(new IOException("disk full"));

        var reply = await new TogglePlaylistShuffleCommand(playlistSwitch, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleplaylistshuffle", moderator: true));

        Assert.Equal("The playlist order could not be changed.", reply);
    }

    [Fact]
    public void OnlyPrivilegedUsersCanTogglePlaylistShuffle_ThroughTheCommandManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var playlistSwitch = Switch("Beta");
        using var manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new PlaylistShuffleStatusCommand(playlistSwitch, TimeSpan.Zero), new TogglePlaylistShuffleCommand(playlistSwitch, TimeSpan.Zero) },
            new[] { "friend" },
            new FakeTimeProvider());

        chat.Say("!toggleplaylistshuffle", user: "viewer");
        chat.Say("!playlistshufflestatus", user: "viewer");
        await Async.SettleAsync();
        playlistSwitch.DidNotReceive().ToggleRandomOrder();
        Assert.Single(sent);

        chat.Say("!toggleplaylistshuffle", user: "friend");
        await Async.SettleAsync();
        playlistSwitch.Received(1).ToggleRandomOrder();
    });

    [Fact]
    public void TogglePlaylistShuffle_WithTheRealPlaylists_IsSaved() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        var playlists = setup.Create();
        await playlists.InitializeAsync();

        var reply = await new TogglePlaylistShuffleCommand(playlists, TimeSpan.Zero).ExecuteAsync(Chat.Command("!toggleplaylistshuffle", moderator: true));

        Assert.Equal("Playlist shuffle is now ON: the next playlist is picked at random.", reply);
        Assert.True(playlists.RandomSwapOrder);
        Assert.True(setup.SavedLibrary().AutoSwap.RandomOrder);
    });

    [Fact]
    public void SkipPlaylist_WithTheRealPlaylists_SelectsTheNextOne_AndStartsTheSwapIntervalOver() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var playlists = setup.Create(time);
        await playlists.InitializeAsync();
        playlists.SetAutoSwap(true, TimeSpan.FromDays(7));
        time.Advance(TimeSpan.FromDays(2));

        var reply = await new SkipPlaylistCommand(playlists, TimeSpan.Zero).ExecuteAsync(Chat.Command("!skipplaylist", moderator: true));
        var status = await new PlaylistCommand(playlists, TimeSpan.Zero).ExecuteAsync(Chat.Command("!playlist"));

        Assert.Null(reply);
        Assert.Equal(IdB, playlists.SelectedKey);
        Assert.Equal(time.GetUtcNow(), playlists.LastSwapUtc);
        Assert.Equal("Current playlist: \"Beta\" (shuffled).", status);
    });
}
