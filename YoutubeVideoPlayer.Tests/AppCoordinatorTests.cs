using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwitchLib.Client.Interfaces;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.YouTube;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests;

/// <summary>
/// The coordinator together with the real playlist and playback managers; only YouTube, the player,
/// the Twitch client and the clock are replaced.
/// </summary>
public class AppCoordinatorTests
{
    private sealed class App : IDisposable
    {
        public App(PlaylistSetup playlists, ITwitchClient? twitchClient = null)
        {
            PlaylistSetup = playlists;
            playlists.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => PlaybackSetup.Resolved(call.ArgAt<string>(0)));
            Playlists = playlists.Create();
            Playback = new PlaybackManager(playlists.YouTube, Playlists, Player, Time);
            Twitch = twitchClient == null
                ? null
                : new TwitchChatService(new TwitchSettings { BotUsername = "lobosbot", OAuthToken = "oauth:x", Channel = "lobosjr" }, null, twitchClient);
            Coordinator = new AppCoordinator(Playback, Playlists, Twitch);
            Coordinator.NoVideosAvailable += (_, _) => NoVideosReports++;
        }

        public PlaylistSetup PlaylistSetup { get; }

        public FakeTimeProvider Time { get; } = new();

        public IMediaPlayer Player { get; } = Substitute.For<IMediaPlayer>();

        public PlaylistManager Playlists { get; }

        public PlaybackManager Playback { get; }

        public TwitchChatService? Twitch { get; }

        public AppCoordinator Coordinator { get; }

        public int NoVideosReports { get; private set; }

        public List<string> Played() => Player.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play))
            .Select(c => ((string)c.GetArguments()[0]!).Replace(".video", ""))
            .ToList();

        /// <summary>Lets pending work run and passes the one-second delay before a random video.</summary>
        public async Task NextVideoDelayAsync()
        {
            await Async.SettleAsync();
            Time.Advance(PlaybackManager.DelayBeforeNextVideo);
            await Async.SettleAsync();
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Playback.Dispose();
            PlaylistSetup.Dispose();
        }
    }

    [Fact]
    public void Start_LoadsThePlaylists_AndStandsByWithTheFirstVideoPrepared() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1"));

        await app.Coordinator.StartAsync();
        await Async.SettleAsync();

        // The app opens paused: nothing plays, but the first video is already looked up.
        Assert.True(app.Playback.IsStandby);
        Assert.Empty(app.Played());
        await app.PlaylistSetup.YouTube.Received(1).ResolveAsync("a1", Arg.Any<CancellationToken>());
        Assert.Equal(0, app.NoVideosReports);

        // Play starts it at once: no one-second wait and no second lookup.
        await app.Playback.PlayNextAsync();

        Assert.Equal(new[] { "a1" }, app.Played());
        Assert.False(app.Playback.IsStandby);
        await app.PlaylistSetup.YouTube.Received(1).ResolveAsync("a1", Arg.Any<CancellationToken>());
    });

    [Fact]
    public void Start_AlsoConnectsTheTwitchBot() => UiContext.Run(async () =>
    {
        var twitchClient = Chat.Client();
        using var app = new App(new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1"), twitchClient);

        await app.Coordinator.StartAsync();
        await Async.SettleAsync();

        await twitchClient.Received(1).ConnectAsync();
    });

    [Fact]
    public void Start_WithoutPlaylists_ReportsNoVideosOnce() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup());

        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();

        Assert.Equal(1, app.NoVideosReports);
        Assert.Empty(app.Played());
    });

    [Fact]
    public void Start_WhenNoPlaylistCanBeLoaded_ReportsNoVideos() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().WithPlaylist(IdA, "Unreachable").WithPlaylist(IdB, "Unreachable too"));

        await app.Coordinator.StartAsync();
        await Async.WaitUntilAsync(() => !app.Playlists.IsLoading && app.Playlists.Playlists.All(p => p.State == PlaylistLoadState.Failed));
        await Async.SettleAsync();

        Assert.Equal(1, app.NoVideosReports);
    });

    [Fact]
    public void AddingTheFirstPlaylist_PreparesItsFirstVideo_AndPlayStartsIt() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().OnYouTube(IdA, "a1"));
        await app.Coordinator.StartAsync();
        Assert.Equal(1, app.NoVideosReports);

        await app.Playlists.AddAsync("A", Url(IdA));
        await Async.SettleAsync();
        Assert.True(app.Playback.IsStandby);
        await app.Playback.PlayNextAsync();

        Assert.Equal(new[] { "a1" }, app.Played());
    });

    [Fact]
    public void SelectingAnotherPlaylist_SkipsToItImmediately() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1"));
        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();
        app.Player.ClearReceivedCalls();

        await app.Playlists.SelectAsync(IdB);
        app.Player.Received(1).Stop();
        await app.NextVideoDelayAsync();

        Assert.Equal(new[] { "b1" }, app.Played());
    });

    [Fact]
    public void AnAutomaticSwap_LetsTheCurrentVideoPlayToItsEnd_AndTheNextVideoComesFromTheNewPlaylist() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1"));
        await app.Coordinator.StartAsync();
        await app.Playback.PlayNextAsync();
        app.Player.ClearReceivedCalls();

        await app.Playlists.SelectAsync(IdB, keepCurrentVideo: true);
        await app.NextVideoDelayAsync();

        app.Player.DidNotReceive().Stop();
        Assert.Empty(app.Played());
        Assert.Equal("a1", app.Playback.Current?.VideoId);

        app.Player.EndReached += Raise.Event();
        await app.NextVideoDelayAsync();
        await app.NextVideoDelayAsync();

        Assert.Equal(new[] { "b1" }, app.Played());
    });

    [Fact]
    public void AGrowingPool_DoesNotInterruptTheCurrentVideo() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1").OnYouTube(IdB, "b1"));
        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();
        app.Player.ClearReceivedCalls();

        await app.Playlists.AddAsync("B", Url(IdB));
        await app.NextVideoDelayAsync();

        app.Player.DidNotReceive().Stop();
        Assert.Empty(app.Played());
    });

    [Fact]
    public void RemovingTheLastPlaylist_ReportsNoVideosWhenTheVideoEnds() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1"));
        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();

        app.Playlists.Remove(IdA);              // selection changes -> skip -> nothing left
        await app.NextVideoDelayAsync();

        Assert.Equal(1, app.NoVideosReports);
    });

    [Fact]
    public void PoolChangesBeforeStartup_AreIgnored() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup().OnYouTube(IdA, "a1"));

        await app.Playlists.AddAsync("A", Url(IdA)); // before StartAsync
        await app.NextVideoDelayAsync();

        Assert.Empty(app.Played());
    });

    [Fact]
    public void Dispose_StopsReactingToPoolChanges() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1").WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var app = new App(setup);
        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();
        app.Coordinator.Dispose();
        app.Player.ClearReceivedCalls();

        await app.Playlists.SelectAsync(IdB);
        await app.NextVideoDelayAsync();

        app.Player.DidNotReceive().Stop();
        app.Playback.Dispose();
    });

    [Fact]
    public void AStartupErrorInThePlaylists_IsSurvived_AndReportedAsNoVideos() => UiContext.Run(async () =>
    {
        using var app = new App(new PlaylistSetup());
        // A folder where playlists.json should be makes loading the library throw.
        var path = Path.Combine(app.PlaylistSetup.Folder, "playlists.json");
        File.Delete(path);
        Directory.CreateDirectory(path);

        await app.Coordinator.StartAsync();
        await app.NextVideoDelayAsync();

        Assert.Empty(app.Played());
        Assert.Equal(1, app.NoVideosReports);
    });
}
