using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class PlaylistAnnouncerTests
{
    private sealed class Setup : IDisposable
    {
        private Setup(PlaylistSetup playlists, PlaylistManager manager, bool connected)
        {
            PlaylistSetup = playlists;
            Manager = manager;
            Chat = TestSupport.Chat.Channel(out var sent);
            Chat.IsConnected.Returns(connected);
            Sent = sent;
            Announcer = new PlaylistAnnouncer(Chat, Manager, Playback);
        }

        public static async Task<Setup> CreateAsync(bool connected = true)
        {
            var playlists = new PlaylistSetup()
                .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1", "a2")
                .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1", "b2");
            var manager = playlists.Create();
            await manager.InitializeAsync();
            await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
            return new Setup(playlists, manager, connected);
        }

        public PlaylistSetup PlaylistSetup { get; }

        public PlaylistManager Manager { get; }

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        public IChatChannel Chat { get; }

        public List<string> Sent { get; }

        public PlaylistAnnouncer Announcer { get; }

        public async Task StartsAsync(string videoId)
        {
            Playback.NowPlayingChanged += Raise.Event<EventHandler<NowPlaying>>(Playback, new NowPlaying(videoId, "Video " + videoId, null));
            await Async.SettleAsync();
        }

        public void Dispose()
        {
            Announcer.Dispose();
            PlaylistSetup.Dispose();
        }
    }

    [Fact]
    public void AfterAChangeOfThePlaylist_TheBotAnnouncesItWhenItsFirstVideoStarts() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();

        await setup.Manager.SelectAsync(IdB);
        Assert.Empty(setup.Sent); // nothing of Beta plays yet

        await setup.StartsAsync("b1");
        await setup.StartsAsync("b2");

        Assert.Equal(new[] { "New Playlist has begun: Beta" }, setup.Sent);
    });

    [Fact]
    public void AVideoOfTheOldPlaylist_ThatStillPlaysFirst_IsNotTakenForTheBeginning() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();

        await setup.Manager.SelectAsync(IdB, keepCurrentVideo: true); // the automatic swap
        await setup.StartsAsync("a2"); // e.g. the winner of a vote from before the swap
        Assert.Empty(setup.Sent);

        await setup.StartsAsync("b1");
        Assert.Equal(new[] { "New Playlist has begun: Beta" }, setup.Sent);
    });

    [Fact]
    public void AllPlaylistsCombined_AreAnnouncedAsSuch() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();

        await setup.Manager.SelectAsync(null);
        await setup.StartsAsync("b2");

        Assert.Equal(new[] { "New Playlist has begun: All playlists" }, setup.Sent);
    });

    [Fact]
    public void WithoutAChangeOfThePlaylist_NothingIsAnnounced() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();

        await setup.StartsAsync("a1");
        setup.PlaylistSetup.OnYouTube(IdA, "a1", "a2", "a3");
        await setup.Manager.UpdateAsync(IdA); // a new pool, but no change of the playlist
        await setup.StartsAsync("a3");

        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void OnlyThePlaylistThatReallyBegins_IsAnnounced() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();

        await setup.Manager.SelectAsync(IdB);
        await setup.Manager.SelectAsync(IdA); // changed again before anything of Beta played
        await setup.StartsAsync("a1");

        Assert.Equal(new[] { "New Playlist has begun: Alpha" }, setup.Sent);
    });

    [Fact]
    public void WhileTheBotIsNotConnected_NothingIsPosted_NorLater() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync(connected: false);

        await setup.Manager.SelectAsync(IdB);
        await setup.StartsAsync("b1");
        setup.Chat.IsConnected.Returns(true);
        await setup.StartsAsync("b2");

        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void Dispose_StopsTheAnnouncements() => UiContext.Run(async () =>
    {
        using var setup = await Setup.CreateAsync();
        setup.Announcer.Dispose();

        await setup.Manager.SelectAsync(IdB);
        await setup.StartsAsync("b1");

        Assert.Empty(setup.Sent);
    });
}
