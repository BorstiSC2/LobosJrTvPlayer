using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Playlists;

/// <summary>An ordered playlist continues where it left off: after another playlist, and after a restart.</summary>
public class ResumeTests
{
    private static readonly string[] Episodes = { "e1", "e2", "e3", "e4", "e5" };

    /// <summary>Alpha plays in order (e1-e5), Beta is shuffled (b1).</summary>
    private static PlaylistSetup AlphaInOrderAndBeta()
    {
        var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, Episodes)
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        setup.Library.Playlists[0].Shuffle = false;
        return setup;
    }

    private static async Task<PlaylistManager> LoadAsync(PlaylistManager manager)
    {
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
        return manager;
    }

    /// <summary>The real playlist manager, playback manager and coordinator on the files of a <see cref="PlaylistSetup"/>.</summary>
    private sealed class App : IDisposable
    {
        private App(PlaylistSetup setup, PlaylistManager playlists)
        {
            setup.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => PlaybackSetup.Resolved(call.ArgAt<string>(0)));
            Playlists = playlists;
            Playback = new PlaybackManager(setup.YouTube, Playlists, Player, Time);
            Coordinator = new AppCoordinator(Playback, Playlists, null);
        }

        /// <summary>Starts the app (it opens paused) on the saved files, like a start of the real app.</summary>
        public static async Task<App> StartAsync(PlaylistSetup setup, bool restart = false)
        {
            var app = new App(setup, restart ? setup.Reopen() : setup.Create());
            await app.Coordinator.StartAsync();
            await Async.WaitUntilAsync(() => !app.Playlists.IsLoading && app.Playlists.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
            await Async.SettleAsync();
            return app;
        }

        public FakeTimeProvider Time { get; } = new();

        public IMediaPlayer Player { get; } = Substitute.For<IMediaPlayer>();

        public PlaylistManager Playlists { get; }

        public PlaybackManager Playback { get; }

        public AppCoordinator Coordinator { get; }

        public List<string> Played() => Player.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play))
            .Select(c => ((string)c.GetArguments()[0]!).Replace(".video", ""))
            .ToList();

        /// <summary>Lets pending work run and passes the one-second pause before a video that was not prepared.</summary>
        public async Task NextVideoAsync()
        {
            for (var i = 0; i < 2; i++)
            {
                await Async.SettleAsync();
                Time.Advance(PlaybackManager.DelayBeforeNextVideo);
                await Async.SettleAsync();
            }
        }

        public async Task PlayAsync()
        {
            var play = Playback.PlayNextAsync();
            await NextVideoAsync();
            await play;
        }

        /// <summary>The current video plays to its end.</summary>
        public async Task EndOfVideoAsync()
        {
            Player.EndReached += Raise.Event();
            await NextVideoAsync();
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Playback.Dispose();
        }
    }

    [Fact]
    public void ACutOffVideo_PlaysAgain_WhenThePlaylistComesBack() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        using var app = await App.StartAsync(setup);

        await app.PlayAsync();                  // e1
        await app.EndOfVideoAsync();            // e1 played to its end: e2
        await app.Playlists.SelectAsync(IdB);   // e2 is cut off
        await app.NextVideoAsync();
        await app.Playlists.SelectAsync(IdA);   // back to Alpha: e2 again, from its start
        await app.NextVideoAsync();
        await app.EndOfVideoAsync();

        Assert.Equal(new[] { "e1", "e2", "b1", "e2", "e3" }, app.Played());
    });

    [Fact]
    public void AVideoThatPlayedToItsEnd_IsFollowedByTheNextOne_WhenThePlaylistComesBack() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        using var app = await App.StartAsync(setup);

        await app.PlayAsync();                                      // e1
        await app.EndOfVideoAsync();                                // e2
        await app.Playlists.SelectAsync(IdB, keepCurrentVideo: true); // the automatic swap: e2 plays on
        await app.NextVideoAsync();
        await app.EndOfVideoAsync();                                // e2 played to its end: Beta begins
        await app.Playlists.SelectAsync(IdA);
        await app.NextVideoAsync();

        Assert.Equal(new[] { "e1", "e2", "b1", "e3" }, app.Played());
    });

    [Fact]
    public void ARestart_ContinuesWhereThePlaylistLeftOff() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        using (var app = await App.StartAsync(setup))
        {
            await app.PlayAsync();       // e1
            await app.EndOfVideoAsync(); // e2, then the app is closed
        }

        var saved = setup.SavedLibrary().Playlists[0];
        Assert.Equal("e2", saved.LastVideoId);
        Assert.Equal(1, saved.LastVideoIndex);
        Assert.False(saved.LastVideoFinished);

        using (var app = await App.StartAsync(setup, restart: true))
        {
            await app.PlayAsync();       // e2 was cut off: it plays again
            await app.EndOfVideoAsync(); // e3
            Assert.Equal(new[] { "e2", "e3" }, app.Played());

            app.Player.EndReached += Raise.Event(); // e3 played to its end; the app is closed before e4 started
            await Async.SettleAsync();
            Assert.Equal(new[] { "e2", "e3" }, app.Played());
        }

        using (var app = await App.StartAsync(setup, restart: true))
        {
            await app.PlayAsync();
            Assert.Equal(new[] { "e4" }, app.Played());
        }
    });

    [Fact]
    public void AShuffledPlaylist_DoesNotSaveWhereItIs() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, Episodes);
        using var app = await App.StartAsync(setup);

        await app.PlayAsync();
        await app.EndOfVideoAsync();

        Assert.Null(setup.SavedLibrary().Playlists[0].LastVideoId);
        Assert.Null(app.Playlists.Playlists[0].Entry.LastVideoId);
    });

    [Theory]
    [InlineData("e3", 2, true, "e4")]   // played to its end: the next one
    [InlineData("e3", 2, false, "e3")]  // cut off: the same one again
    [InlineData("e5", 4, true, "e1")]   // after the last one the playlist starts over
    [InlineData(null, 0, false, "e1")]  // nothing played yet: the first one
    [InlineData("gone", 2, true, "e3")] // an update removed it: what is at its place now
    [InlineData("gone", 9, true, "e1")]
    public void WithoutACurrentVideoOfItsOwn_AnOrderedPlaylistContinuesWhereItLeftOff(string? last, int index, bool finished, string expected) =>
        UiContext.Run(async () =>
        {
            using var setup = AlphaInOrderAndBeta();
            var entry = setup.Library.Playlists[0];
            (entry.LastVideoId, entry.LastVideoIndex, entry.LastVideoFinished) = (last, index, finished);
            var manager = await LoadAsync(setup.Create());

            Assert.True(manager.TryGetNextVideoId(null, out var first)); // just started
            Assert.Equal(expected, first);
            Assert.True(manager.TryGetNextVideoId("b1", out var afterBeta)); // the last video of another playlist
            Assert.Equal(expected, afterBeta);
            Assert.Equal(expected, manager.GetNextVideoIds(3, "b1")[0]); // a vote offers it first

            Assert.True(manager.TryGetNextVideoId("e1", out var afterOwn)); // a video of its own: simply the next one
            Assert.Equal("e2", afterOwn);
        });

    [Fact]
    public void AVoteRightAfterTheSwapToAnOrderedPlaylist_OffersTheEpisodesFromWhereItLeftOff() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        var entry = setup.Library.Playlists[0];
        (entry.LastVideoId, entry.LastVideoIndex, entry.LastVideoFinished) = ("e2", 1, true);
        setup.Library.SelectedPlaylistUrl = Url(IdB);
        var manager = await LoadAsync(setup.Create());

        await manager.SelectAsync(IdA, keepCurrentVideo: true); // b1 still plays

        Assert.Equal(new[] { "e3", "e4", "e5" }, manager.GetNextVideoIds(3, "b1"));
    });

    [Fact]
    public void ProgressThatCannotBeSaved_IsKeptInMemory_AndNothingThrows() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        var manager = await LoadAsync(setup.Create());
        var file = Path.Combine(setup.Folder, "playlists.json");

        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            manager.RecordVideoStarted("e3");
            manager.RecordVideoFinished("e3");
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Assert.True(manager.TryGetNextVideoId(null, out var next));
        Assert.Equal("e4", next);
        Assert.Null(setup.SavedLibrary().Playlists[0].LastVideoId);

        manager.Rename(IdB, "Beta 2"); // the next save writes it
        Assert.Equal("e3", setup.SavedLibrary().Playlists[0].LastVideoId);
    });

    [Fact]
    public void OnlyTheVideoThatStartedLast_CanBeMarkedAsFinished() => UiContext.Run(async () =>
    {
        using var setup = AlphaInOrderAndBeta();
        var manager = await LoadAsync(setup.Create());

        manager.RecordVideoStarted("e2");
        manager.RecordVideoFinished("e1"); // a late report of an earlier video

        Assert.False(manager.Playlists[0].Entry.LastVideoFinished);
        manager.RecordVideoFinished("e2");
        Assert.True(manager.Playlists[0].Entry.LastVideoFinished);
    });

    [Fact]
    public void ThePlaybackManager_ReportsAVideoAsFinished_OnlyWhenItPlayedToItsEnd() => UiContext.Run(async () =>
    {
        using var setup = new PlaybackSetup().Queue("v1", "v2", "v3");
        var finished = new List<string>();
        setup.Manager.VideoFinished += (_, video) => finished.Add(video.VideoId);

        await setup.RunAsync(setup.Manager.PlayNextAsync); // v1
        await setup.RunAsync(setup.Manager.SkipAsync);     // v1 is skipped: v2
        setup.Player.EndReached += Raise.Event();           // v2 played to its end
        await setup.AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);

        Assert.Equal(new[] { "v2" }, finished);
    });
}
