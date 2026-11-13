using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Playlists;

public class PlaylistManagerTests
{
    private static List<string> Pool(PlaylistManager manager) => manager.GetNextVideoIds(int.MaxValue, null).OrderBy(x => x).ToList();

    private static async Task WaitForBackgroundLoadingAsync(PlaylistManager manager) =>
        await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));

    #region Startup

    [Fact]
    public void Initialize_WithEmptyLibrary_HasNothingToPlay() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Empty(manager.Playlists);
        Assert.Equal(0, manager.PoolCount);
        Assert.False(manager.TryGetNextVideoId(null, out _));
        Assert.Null(manager.SelectedKey);
    });

    [Fact]
    public void Initialize_LoadsTheSelectedPlaylistFromCache_WithoutContactingYouTube() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2")
            .WithPlaylist(IdB, "B").OnYouTube(IdB, "b1");
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Equal(IdA, manager.SelectedKey);
        Assert.Equal(new[] { "a1", "a2" }, Pool(manager));
        Assert.Equal(0, setup.Downloads(IdA));
        Assert.Equal(PlaylistLoadState.Loaded, manager.Playlists[0].State);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), manager.Playlists[0].FetchedAtUtc);
    });

    [Fact]
    public void Initialize_DownloadsTheSelectedPlaylistIfItHasNoCache_AndCachesItWithoutDuplicates() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).OnYouTube(IdA, "a1", "a2", "a1", "a3");
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Equal(new[] { "a1", "a2", "a3" }, Pool(manager));
        Assert.Equal(new[] { "a1", "a2", "a3" }, setup.Cache.TryRead(IdA)!.VideoIds);
        Assert.Equal(1, setup.Downloads(IdA));
    });

    [Fact]
    public void Initialize_LoadsTheOtherPlaylistsInTheBackground_WithoutChangingTheSelectedPool() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").OnYouTube(IdB, "b1", "b2")
            .WithPlaylist(IdC, "C").WithCache(IdC, "c1");
        var manager = setup.Create();
        var poolChanges = 0;
        manager.PoolChanged += (_, _) => poolChanges++;

        await manager.InitializeAsync();
        var changesAfterInit = poolChanges;
        await WaitForBackgroundLoadingAsync(manager);

        Assert.All(manager.Playlists, p => Assert.Equal(PlaylistLoadState.Loaded, p.State));
        Assert.Equal(new[] { "b1", "b2" }, manager.Playlists[1].VideoIds);
        Assert.Equal(new[] { "c1" }, manager.Playlists[2].VideoIds);
        Assert.True(setup.Cache.Exists(IdB));
        Assert.Equal(new[] { "a1" }, Pool(manager));
        Assert.Equal(changesAfterInit, poolChanges); // background loads do not touch a single selected playlist
    });

    [Fact]
    public void Initialize_WithAllPlaylists_UsesTheCachedOnes_AndGrowsThePoolInTheBackground() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A").OnYouTube(IdA, "a1", "shared")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1", "shared");
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Null(manager.SelectedKey);
        Assert.Equal(new[] { "b1", "shared" }, Pool(manager));
        Assert.Equal(0, setup.Downloads(IdB));

        await WaitForBackgroundLoadingAsync(manager);
        Assert.Equal(new[] { "a1", "b1", "shared" }, Pool(manager)); // union without duplicates
    });

    [Fact]
    public void Initialize_WithAllPlaylistsAndNoCache_DownloadsTheFirstPlaylistBeforeReturning() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A").OnYouTube(IdA, "a1")
            .WithPlaylist(IdB, "B").OnYouTube(IdB, "b1");
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Equal(new[] { "a1" }, Pool(manager));
        await WaitForBackgroundLoadingAsync(manager);
        Assert.Equal(new[] { "a1", "b1" }, Pool(manager));
    });

    [Fact]
    public void Initialize_WithAnUnknownSelectedUrl_FallsBackToAllPlaylists() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1");
        setup.Library.SelectedPlaylistUrl = Url(IdB);
        var manager = setup.Create();

        await manager.InitializeAsync();

        Assert.Null(manager.SelectedKey);
        Assert.Equal(new[] { "a1" }, Pool(manager));
    });

    [Fact]
    public void Initialize_MarksInvalidLinksAndFailedDownloadsAsFailed_AndKeepsTheRest() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Unreachable")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        setup.Library.Playlists.Add(new PlaylistEntry { Title = "Broken", Url = "not a link" });
        var manager = setup.Create();

        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        Assert.Equal(PlaylistLoadState.Failed, manager.Playlists[0].State);
        Assert.Equal("no such playlist", manager.Playlists[0].Error);
        Assert.Equal(PlaylistLoadState.Failed, manager.Playlists[2].State);
        Assert.Equal("Not a valid YouTube playlist link.", manager.Playlists[2].Error);
        Assert.Equal("not a link", manager.Playlists[2].Key);
        Assert.Equal(new[] { "b1" }, Pool(manager));
    });

    [Fact]
    public void Initialize_RaisesPlaylistsChangedWhileLoading() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        var states = new List<PlaylistLoadState>();
        manager.PlaylistsChanged += (_, _) => states.Add(manager.Playlists[0].State);

        await manager.InitializeAsync();

        Assert.Equal(new[] { PlaylistLoadState.Loading, PlaylistLoadState.Loaded }, states);
    });

    #endregion

    #region Random picks

    [Fact]
    public void TryGetNextVideoId_ReturnsVideosFromThePool() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var picks = Enumerable.Range(0, 200).Select(_ => manager.TryGetNextVideoId(null, out var id) ? id : null).ToList();

        Assert.All(picks, id => Assert.Contains(id, new[] { "a1", "a2", "a3" }));
        Assert.Equal(3, picks.Distinct().Count()); // all of them come up eventually
    });

    [Fact]
    public void GetNextVideoIds_ReturnsDistinctIds_LimitedToCount_WithoutTheCurrentOne() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3", "a4", "a5");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var three = manager.GetNextVideoIds(3, null);
        var withoutA1 = manager.GetNextVideoIds(10, "a1");

        Assert.Equal(3, three.Distinct().Count());
        Assert.Equal(new[] { "a2", "a3", "a4", "a5" }, withoutA1.OrderBy(x => x));
        Assert.Empty(manager.GetNextVideoIds(0, null));
    });

    #endregion

    #region Selecting

    [Fact]
    public void Select_SwitchesThePool_RaisesSelectionChanged_AndPersistsTheChoice() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var events = new List<PoolChangedEventArgs>();
        manager.PoolChanged += (_, e) => events.Add(e);

        await manager.SelectAsync(IdB);

        Assert.Equal(IdB, manager.SelectedKey);
        Assert.Equal(new[] { "b1" }, Pool(manager));
        Assert.True(Assert.Single(events).SelectionChanged);
        Assert.Equal(Url(IdB), setup.SavedLibrary().SelectedPlaylistUrl);
    });

    [Fact]
    public void SelectNull_SwitchesToAllPlaylists() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        await manager.SelectAsync(null);

        Assert.Null(manager.SelectedKey);
        Assert.Equal(new[] { "a1", "b1" }, Pool(manager));
        Assert.Null(setup.SavedLibrary().SelectedPlaylistUrl);
    });

    [Fact]
    public void Select_OfAPlaylistThatCannotBeLoaded_ThrowsAndKeepsTheSelection() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Unreachable");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.SelectAsync(IdB));

        Assert.Equal(IdA, manager.SelectedKey);
        Assert.Equal(new[] { "a1" }, Pool(manager));
    });

    [Fact]
    public void Select_OfAFailedPlaylist_RetriesTheDownload() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        Assert.Equal(PlaylistLoadState.Failed, manager.Playlists[1].State);

        setup.OnYouTube(IdB, "b1"); // YouTube is reachable again
        await manager.SelectAsync(IdB);

        Assert.Equal(new[] { "b1" }, Pool(manager));
    });

    [Fact]
    public void Select_OfAnUnknownPlaylist_Throws() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        var manager = setup.Create();
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SelectAsync(IdA));
    });

    [Fact]
    public void Select_WhileThePlaylistIsStillDownloading_WaitsForThatDownloadInsteadOfStartingAnother() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B");
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdB, Arg.Any<CancellationToken>()).Returns(download.Task);
        var manager = setup.Create();
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => manager.Playlists[1].State == PlaylistLoadState.Loading);

        var select = manager.SelectAsync(IdB);
        await Async.SettleAsync();
        Assert.False(select.IsCompleted);

        download.SetResult(new[] { "b1" });
        await select;

        Assert.Equal(new[] { "b1" }, Pool(manager));
        Assert.Equal(1, setup.Downloads(IdB));
    });

    #endregion

    #region Adding, renaming, removing

    [Fact]
    public void GetPlaylistDetails_ReturnsTheYouTubeTitleAndANormalizedLink() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        setup.YouTube.GetPlaylistTitleAsync(IdA, Arg.Any<CancellationToken>()).Returns("Best of");
        var manager = setup.Create();

        var details = await manager.GetPlaylistDetailsAsync($"https://www.youtube.com/watch?v=abcdefghijk&list={IdA}&index=3", CancellationToken.None);

        Assert.Equal(new YouTubePlaylistDetails(IdA, "Best of", Url(IdA)), details);
    });

    [Fact]
    public void GetPlaylistDetails_RejectsInvalidLinks() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        var manager = setup.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.GetPlaylistDetailsAsync("https://example.com", CancellationToken.None));
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    public void TryParsePlaylistId_RejectsNonPlaylistInput(string? input)
    {
        Assert.Null(PlaylistManager.TryParsePlaylistId(input));
    }

    [Fact]
    public void TryParsePlaylistId_AcceptsPlaylistAndWatchLinks()
    {
        Assert.Equal(IdA, PlaylistManager.TryParsePlaylistId("  " + Url(IdA) + "  ")!.Value.Value);
        Assert.Equal(IdA, PlaylistManager.TryParsePlaylistId($"https://youtube.com/watch?v=abcdefghijk&list={IdA}")!.Value.Value);
    }

    [Fact]
    public void Add_SavesThePlaylist_DownloadsItsVideos_AndAddsThemToAllPlaylists() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1").OnYouTube(IdB, "b1", "b2");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var added = await manager.AddAsync("  My list  ", $"https://www.youtube.com/watch?v=x&list={IdB}");

        Assert.Equal("My list", added.Title);
        Assert.Equal(Url(IdB), added.Url);
        Assert.Equal(PlaylistLoadState.Loaded, added.State);
        Assert.True(setup.Cache.Exists(IdB));
        Assert.Equal(new[] { "a1", "b1", "b2" }, Pool(manager));
        Assert.Contains(setup.SavedLibrary().Playlists, p => p.Title == "My list" && p.Url == Url(IdB));
    });

    [Fact]
    public void Add_DoesNotChangeThePoolOfASingleSelectedPlaylist() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1").OnYouTube(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        await manager.AddAsync("B", Url(IdB));

        Assert.Equal(new[] { "a1" }, Pool(manager));
    });

    [Fact]
    public void Add_IgnoresAStaleCacheFromAnEarlierRemove() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithCache(IdA, "stale").OnYouTube(IdA, "fresh");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var added = await manager.AddAsync("A", Url(IdA));

        Assert.Equal(new[] { "fresh" }, added.VideoIds);
    });

    [Fact]
    public void Add_RejectsDuplicatesAndInvalidLinks() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddAsync("again", $"https://youtube.com/watch?v=x&list={IdA}"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync("bad", "no link"));
        Assert.Single(manager.Playlists);
    });

    [Fact]
    public void Add_KeepsThePlaylistAsFailedIfTheDownloadFails() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        var manager = setup.Create();
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.AddAsync("B", Url(IdB)));

        var playlist = Assert.Single(manager.Playlists);
        Assert.Equal(PlaylistLoadState.Failed, playlist.State);
        Assert.Single(setup.SavedLibrary().Playlists);
    });

    [Fact]
    public void Rename_ChangesOnlyTheTitle_AndKeepsTheCache() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "Old", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var changed = 0;
        manager.PlaylistsChanged += (_, _) => changed++;

        manager.Rename(IdA, "  New  ");

        Assert.Equal("New", manager.Playlists[0].Title);
        Assert.Equal("New", Assert.Single(setup.SavedLibrary().Playlists).Title);
        Assert.Equal(new[] { "a1" }, setup.Cache.TryRead(IdA)!.VideoIds);
        Assert.Equal(1, changed);
        Assert.Equal(0, setup.Downloads(IdA));
        Assert.Throws<InvalidOperationException>(() => manager.Rename(IdB, "x"));
    });

    [Fact]
    public void Remove_DeletesPlaylistAndCache_AndFallsBackToAllIfItWasSelected() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        var events = new List<PoolChangedEventArgs>();
        manager.PoolChanged += (_, e) => events.Add(e);

        manager.Remove(IdA);

        Assert.Equal(new[] { IdB }, manager.Playlists.Select(p => p.Key));
        Assert.False(setup.Cache.Exists(IdA));
        Assert.Null(manager.SelectedKey);
        Assert.Equal(new[] { "b1" }, Pool(manager));
        Assert.True(Assert.Single(events).SelectionChanged);
        var saved = setup.SavedLibrary();
        Assert.Null(saved.SelectedPlaylistUrl);
        Assert.Single(saved.Playlists);
    });

    [Fact]
    public void Remove_OfAnotherPlaylistInAllMode_ShrinksThePoolWithoutSelectionChange() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A").WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var events = new List<PoolChangedEventArgs>();
        manager.PoolChanged += (_, e) => events.Add(e);

        manager.Remove(IdB);
        manager.Remove("unknown"); // ignored

        Assert.Equal(new[] { "a1" }, Pool(manager));
        Assert.False(Assert.Single(events).SelectionChanged);
    });

    [Fact]
    public void Remove_WhileDownloading_DoesNotLeaveACacheFileBehind() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B");
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdB, Arg.Any<CancellationToken>()).Returns(download.Task);
        var manager = setup.Create();
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => manager.Playlists[1].State == PlaylistLoadState.Loading);

        manager.Remove(IdB);
        download.SetResult(new[] { "b1" });
        await Async.SettleAsync();

        Assert.False(setup.Cache.Exists(IdB));
    });

    #endregion

    #region Updating

    [Fact]
    public void Update_DownloadsAgain_ReplacesTheCache_AndReportsTheDifference() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var poolChanges = 0;
        manager.PoolChanged += (_, e) => { poolChanges++; Assert.False(e.SelectionChanged); };

        setup.OnYouTube(IdA, "a2", "a3", "a4", "a5");
        var result = await manager.UpdateAsync(IdA);

        Assert.Equal(new PlaylistUpdateResult("A", Added: 2, Removed: 1, Total: 4), result);
        Assert.Equal(new[] { "a2", "a3", "a4", "a5" }, Pool(manager));
        var cached = setup.Cache.TryRead(IdA)!;
        Assert.Equal(new[] { "a2", "a3", "a4", "a5" }, cached.VideoIds);
        Assert.True(cached.FetchedAtUtc > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, poolChanges);
    });

    [Fact]
    public void Update_OfANotSelectedPlaylist_DoesNotTouchThePool() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        var poolChanges = 0;
        manager.PoolChanged += (_, _) => poolChanges++;

        setup.OnYouTube(IdB, "b2");
        await manager.UpdateAsync(IdB);

        Assert.Equal(0, poolChanges);
        Assert.Equal(new[] { "b2" }, manager.Playlists[1].VideoIds);
    });

    [Fact]
    public void Update_ThatFails_KeepsThePreviousVideosAndReportsTheError() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.UpdateAsync(IdA));

        var playlist = manager.Playlists[0];
        Assert.Equal(PlaylistLoadState.Loaded, playlist.State);
        Assert.Equal("no such playlist", playlist.Error);
        Assert.Equal(new[] { "a1" }, Pool(manager));
        Assert.Equal(new[] { "a1" }, setup.Cache.TryRead(IdA)!.VideoIds);
    });

    [Fact]
    public void Update_RejectsUnknownAndInvalidPlaylists() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        setup.Library.Playlists.Add(new PlaylistEntry { Title = "Broken", Url = "not a link" });
        var manager = setup.Create();
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpdateAsync(IdA));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpdateAsync("not a link"));
    });

    [Fact]
    public void Update_WhileTheFirstLoadIsRunning_WaitsForItAndThenDownloadsAgain() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B");
        var firstDownload = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdB, Arg.Any<CancellationToken>())
            .Returns(firstDownload.Task, Task.FromResult<IReadOnlyList<string>>(new[] { "b1", "b2" }));
        var manager = setup.Create();
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => manager.Playlists[1].State == PlaylistLoadState.Loading);

        var update = manager.UpdateAsync(IdB);
        await Async.SettleAsync();
        Assert.False(update.IsCompleted);
        firstDownload.SetResult(new[] { "b1" });
        var result = await update;

        Assert.Equal(2, setup.Downloads(IdB));
        Assert.Equal(new PlaylistUpdateResult("B", Added: 1, Removed: 0, Total: 2), result);
    });

    [Fact]
    public void Update_RequestedTwiceAtOnce_DownloadsOnlyOnce() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdA, Arg.Any<CancellationToken>()).Returns(download.Task);
        var manager = setup.Create();
        await manager.InitializeAsync();

        var first = manager.UpdateAsync(IdA);
        var second = manager.UpdateAsync(IdA);
        await Async.SettleAsync();
        download.SetResult(new[] { "a1", "a2" });
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, setup.Downloads(IdA));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(new PlaylistUpdateResult("A", Added: 1, Removed: 0, Total: 2), results[0]);

        // Once finished, a new update downloads again.
        setup.OnYouTube(IdA, "a1", "a2", "a3");
        await manager.UpdateAsync(IdA);
        Assert.Equal(2, setup.Downloads(IdA));
    });

    [Fact]
    public void Update_RetriesAPlaylistWhoseFirstLoadFailed() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true);
        var manager = setup.Create();
        await manager.InitializeAsync();
        Assert.Equal(PlaylistLoadState.Failed, manager.Playlists[0].State);

        setup.OnYouTube(IdA, "a1");
        var result = await manager.UpdateAsync(IdA);

        Assert.Equal(new PlaylistUpdateResult("A", 1, 0, 1), result);
        Assert.Equal(PlaylistLoadState.Loaded, manager.Playlists[0].State);
        Assert.Null(manager.Playlists[0].Error);
        Assert.Equal(new[] { "a1" }, Pool(manager));
    });

    #endregion

    #region Failures that must not damage the playlists

    /// <summary>Makes playlists.json impossible to replace, so every save fails, until it is disposed.</summary>
    private sealed class UnwritableLibrary : IDisposable
    {
        private readonly string _path;

        public UnwritableLibrary(PlaylistSetup setup)
        {
            _path = Path.Combine(setup.Folder, "playlists.json");
            File.SetAttributes(_path, FileAttributes.ReadOnly);
        }

        public void Dispose() => File.SetAttributes(_path, FileAttributes.Normal);
    }

    [Fact]
    public void Add_WhoseSaveFails_LeavesNothingBehind_SoTheNextTryAddsOnlyOnePlaylist() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1").OnYouTube(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        using (new UnwritableLibrary(setup))
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => manager.AddAsync("B", Url(IdB)));

        Assert.Equal(new[] { IdA }, manager.Playlists.Select(p => p.Key));
        await manager.AddAsync("B", Url(IdB));
        Assert.Equal(new[] { "A", "B" }, setup.SavedLibrary().Playlists.Select(p => p.Title));
    });

    [Fact]
    public void Remove_WhoseSaveFails_KeepsThePlaylistItsCacheAndTheSelection() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        using (new UnwritableLibrary(setup))
            Assert.ThrowsAny<UnauthorizedAccessException>(() => manager.Remove(IdA));

        Assert.Equal(new[] { IdA, IdB }, manager.Playlists.Select(p => p.Key));
        Assert.Equal(IdA, manager.SelectedKey);
        Assert.True(setup.Cache.Exists(IdA));
        Assert.Equal(new[] { "a1" }, Pool(manager));

        // The failed attempt must not change what the next successful save writes: playlist and selection are still in it.
        manager.Rename(IdA, "A2");
        var saved = setup.SavedLibrary();
        Assert.Equal(new[] { "A2", "B" }, saved.Playlists.Select(p => p.Title));
        Assert.Equal(Url(IdA), saved.SelectedPlaylistUrl);

        manager.Remove(IdA); // works again once the file can be written
        Assert.Equal(new[] { "B" }, setup.SavedLibrary().Playlists.Select(p => p.Title));
    });

    [Fact]
    public void Rename_WhoseSaveFails_KeepsTheOldTitle() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        using (new UnwritableLibrary(setup))
            Assert.ThrowsAny<UnauthorizedAccessException>(() => manager.Rename(IdA, "New"));

        Assert.Equal("A", manager.Playlists[0].Title);
    });

    [Fact]
    public void Select_WhoseSaveFails_KeepsTheSelection() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        using (new UnwritableLibrary(setup))
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => manager.SelectAsync(IdB));

        Assert.Equal(IdA, manager.SelectedKey);
        Assert.Equal(new[] { "a1" }, Pool(manager));

        // A later successful save must still write the selection that is really in use.
        manager.Rename(IdB, "B2");
        Assert.Equal(Url(IdA), setup.SavedLibrary().SelectedPlaylistUrl);
    });

    [Fact]
    public void Select_OfAPlaylistThatWasRemovedWhileItLoaded_Throws() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1").WithPlaylist(IdB, "B");
        var download = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdB, Arg.Any<CancellationToken>()).Returns(download.Task);
        var manager = setup.Create();
        await manager.InitializeAsync();
        var select = manager.SelectAsync(IdB);
        await Async.WaitUntilAsync(() => manager.Playlists[1].State == PlaylistLoadState.Loading);

        manager.Remove(IdB);
        download.SetResult(new[] { "b1" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => select);
        Assert.Equal(IdA, manager.SelectedKey);
        Assert.Equal(Url(IdA), setup.SavedLibrary().SelectedPlaylistUrl);
    });

    [Fact]
    public void Initialize_WithAFileThatCannotBeRead_RefusesToReplaceItLater() => UiContext.Run(async () =>
    {
        // Saving the empty library in memory would wipe the playlists that are in the (locked) file.
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1").OnYouTube(IdB, "b1");
        var manager = setup.Create();

        using (new FileStream(Path.Combine(setup.Folder, "playlists.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => manager.InitializeAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddAsync("B", Url(IdB)));
        Assert.Equal(new[] { "A" }, setup.SavedLibrary().Playlists.Select(p => p.Title));
    });

    [Fact]
    public void Update_ThatReturnsNoVideos_KeepsThePreviousListAndItsCache() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2").OnYouTube(IdA);
        var manager = setup.Create();
        await manager.InitializeAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpdateAsync(IdA));

        Assert.Contains("no videos", error.Message);
        Assert.Equal(PlaylistLoadState.Loaded, manager.Playlists[0].State);
        Assert.Equal(new[] { "a1", "a2" }, Pool(manager));
        Assert.Equal(new[] { "a1", "a2" }, setup.Cache.TryRead(IdA)!.VideoIds);
    });

    [Fact]
    public void Add_OfAnEmptyPlaylist_IsLoadedButNotCached_SoTheNextStartAsksYouTubeAgain() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().OnYouTube(IdB);
        var manager = setup.Create();
        await manager.InitializeAsync();

        var playlist = await manager.AddAsync("B", Url(IdB));

        Assert.Equal(PlaylistLoadState.Loaded, playlist.State);
        Assert.Empty(playlist.VideoIds);
        Assert.False(setup.Cache.Exists(IdB));
    });

    [Fact]
    public void ADownloadWhoseCacheCannotBeWritten_IsStillUsed() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().OnYouTube(IdB, "b1", "b2");
        File.WriteAllText(Path.Combine(setup.Folder, "PlaylistCache"), "a file where the cache folder should be");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var playlist = await manager.AddAsync("B", Url(IdB));

        Assert.Equal(PlaylistLoadState.Loaded, playlist.State);
        Assert.Equal(new[] { "b1", "b2" }, playlist.VideoIds);
        Assert.Equal(new[] { "b1", "b2" }, Pool(manager));
    });

    #endregion

    [Fact]
    public void SequenceChanged_IsNotRaisedByARebuildThatChangesNothing() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var sequenceChanges = 0;
        var poolChanges = 0;
        manager.SequenceChanged += (_, _) => sequenceChanges++;
        manager.PoolChanged += (_, _) => poolChanges++;

        setup.OnYouTube(IdA, "a1", "a2"); // the same videos again: a prepared next video stays valid
        var unchanged = await manager.UpdateAsync(IdA);

        Assert.Equal(new PlaylistUpdateResult("A", 0, 0, 2), unchanged);
        Assert.Equal(0, sequenceChanges);
        Assert.Equal(1, poolChanges);

        setup.OnYouTube(IdA, "a2", "a1"); // another order
        await manager.UpdateAsync(IdA);
        Assert.Equal(1, sequenceChanges);
    });

    [Fact]
    public void PlaylistInfo_ExposesTheEntry() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var info = manager.Playlists[0];

        Assert.Equal(("A", Url(IdA), IdA), (info.Title, info.Url, info.Key));
        Assert.True(manager.Contains(IdA));
        Assert.False(manager.Contains(IdB));
    });

    [Fact]
    public void Save_Failure_IsPropagated() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A").WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var path = Path.Combine(setup.Folder, "playlists.json");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => manager.Rename(IdA, "x"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    public void YouTubeErrorsDuringDetailsLookup_ArePropagated() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        setup.YouTube.GetPlaylistTitleAsync(IdA, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));
        var manager = setup.Create();

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetPlaylistDetailsAsync(Url(IdA), CancellationToken.None));
    });
}
