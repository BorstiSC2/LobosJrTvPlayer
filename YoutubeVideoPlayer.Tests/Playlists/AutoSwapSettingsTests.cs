using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Playlists;

/// <summary>
/// The setting of the automatic playlist swap in <see cref="PlaylistManager"/>: what is saved in <c>playlists.json</c>,
/// when the interval starts over, and which playlist comes next.
/// </summary>
public class AutoSwapSettingsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static PlaylistSetup TwoPlaylists() => new PlaylistSetup()
        .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1", "a2")
        .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1", "b2");

    private static PlaylistSetup ThreePlaylists() => TwoPlaylists().WithPlaylist(IdC, "Gamma").WithCache(IdC, "c1");

    private static async Task<PlaylistManager> LoadAsync(PlaylistManager manager)
    {
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
        return manager;
    }

    private static Task<PlaylistManager> LoadAsync(PlaylistSetup setup, TimeProvider time) => LoadAsync(setup.Create(time));

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
    public void IsOffByDefault_WithAWeekAsInterval() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        Assert.False(manager.AutoSwapEnabled);
        Assert.Equal(TimeSpan.FromDays(7), manager.AutoSwapInterval);
        Assert.True(manager.CanAutoSwap);
        Assert.Null(manager.NextSwapUtc);
        Assert.False(manager.IsSwapDue);
    });

    [Fact]
    public void TurningItOn_SavesTheSettingAndWhenTheIntervalStarted_SoARestartDoesNotResetIt() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        var changes = 0;
        manager.AutoSwapChanged += (_, _) => changes++;

        manager.SetAutoSwap(true, TimeSpan.FromDays(2) + TimeSpan.FromHours(3));

        Assert.Equal(1, changes);
        Assert.True(manager.AutoSwapEnabled);
        Assert.Equal(Start, manager.LastSwapUtc);
        Assert.Equal(Start + TimeSpan.FromHours(51), manager.NextSwapUtc);
        var saved = setup.SavedLibrary();
        Assert.True(saved.AutoSwap.Enabled);
        Assert.Equal(51, saved.AutoSwap.IntervalHours);
        Assert.Equal(Start, saved.AutoSwap.LastSwapUtc);
        Assert.Equal(Url(IdA), saved.SelectedPlaylistUrl); // the current playlist is stored along with it

        // A day later the app is restarted: the interval goes on from where it was.
        time.Advance(TimeSpan.FromDays(1));
        var restarted = await LoadAsync(setup.Reopen(time));

        Assert.True(restarted.AutoSwapEnabled);
        Assert.Equal(Start, restarted.LastSwapUtc);
        Assert.Equal(Start + TimeSpan.FromHours(51), restarted.NextSwapUtc);
        Assert.False(restarted.IsSwapDue);

        time.Advance(TimeSpan.FromHours(27));
        Assert.True(restarted.IsSwapDue);
    });

    [Fact]
    public void EveryChangeOfTheSetting_StartsTheIntervalOver() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));

        time.Advance(TimeSpan.FromDays(3));
        manager.SetAutoSwap(true, TimeSpan.FromDays(4)); // another interval
        Assert.Equal(Start + TimeSpan.FromDays(3), manager.LastSwapUtc);
        Assert.Equal(Start + TimeSpan.FromDays(7), manager.NextSwapUtc);

        time.Advance(TimeSpan.FromHours(5));
        manager.SetAutoSwap(false, TimeSpan.FromDays(4)); // off
        Assert.Equal(Start + TimeSpan.FromDays(3) + TimeSpan.FromHours(5), manager.LastSwapUtc);
        Assert.Null(manager.NextSwapUtc);

        time.Advance(TimeSpan.FromHours(1));
        manager.SetAutoSwap(true, TimeSpan.FromDays(4)); // on again
        Assert.Equal(Start + TimeSpan.FromDays(3) + TimeSpan.FromHours(6), setup.SavedLibrary().AutoSwap.LastSwapUtc);
    });

    [Fact]
    public void SettingTheSameValuesAgain_ChangesNothing() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));
        var changes = 0;
        manager.AutoSwapChanged += (_, _) => changes++;

        time.Advance(TimeSpan.FromDays(1));
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));

        Assert.Equal(0, changes);
        Assert.Equal(Start, manager.LastSwapUtc);
    });

    [Fact]
    public void ChangingThePlaylistByHand_StartsTheIntervalOver_ButSelectingTheSameOneAgainDoesNot() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));
        var changes = 0;
        manager.AutoSwapChanged += (_, _) => changes++;

        time.Advance(TimeSpan.FromDays(2));
        await manager.SelectAsync(IdB);

        Assert.Equal(1, changes);
        Assert.Equal(Start + TimeSpan.FromDays(2), manager.LastSwapUtc);
        Assert.Equal(Start + TimeSpan.FromDays(9), manager.NextSwapUtc);
        Assert.Equal(Start + TimeSpan.FromDays(2), setup.SavedLibrary().AutoSwap.LastSwapUtc);

        time.Advance(TimeSpan.FromDays(1));
        await manager.SelectAsync(IdB);
        Assert.Equal(Start + TimeSpan.FromDays(2), manager.LastSwapUtc);

        await manager.SelectAsync(null); // "All playlists" is a change as well
        Assert.Equal(Start + TimeSpan.FromDays(3), manager.LastSwapUtc);
    });

    [Fact]
    public void CannotBeTurnedOnWithASinglePlaylist() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1");
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        Assert.False(manager.CanAutoSwap);
        Assert.Throws<InvalidOperationException>(() => manager.SetAutoSwap(true, TimeSpan.FromDays(1)));
        Assert.False(manager.AutoSwapEnabled);
        Assert.False(setup.SavedLibrary().AutoSwap.Enabled);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(366 * 24)]
    public void TheIntervalIsAtLeastAnHour_AndAtMost365DaysAnd23Hours(int hours) => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.SetAutoSwap(true, TimeSpan.FromHours(hours)));
        Assert.False(manager.AutoSwapEnabled);

        manager.SetAutoSwap(true, TimeSpan.FromHours(1));
        manager.SetAutoSwap(true, TimeSpan.FromDays(365) + TimeSpan.FromHours(23));
    });

    [Fact]
    public void RemovingTheSecondToLastPlaylist_TurnsItOff() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));
        var changes = 0;
        manager.AutoSwapChanged += (_, _) => changes++;

        manager.Remove(IdB);

        Assert.False(manager.AutoSwapEnabled);
        Assert.False(manager.CanAutoSwap);
        Assert.Null(manager.NextSwapUtc);
        Assert.False(setup.SavedLibrary().AutoSwap.Enabled);
        Assert.True(changes > 0);
    });

    [Fact]
    public void RemovingTheSelectedPlaylist_StartsTheIntervalOver() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));

        time.Advance(TimeSpan.FromDays(1));
        manager.Remove(IdA); // the selection falls back to all playlists

        Assert.True(manager.AutoSwapEnabled);
        Assert.Equal(Start + TimeSpan.FromDays(1), manager.LastSwapUtc);
    });

    [Fact]
    public void WhenSavingFails_TheOldSettingStays() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));
        time.Advance(TimeSpan.FromDays(1));

        using (new UnwritableLibrary(setup))
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => manager.SetAutoSwap(false, TimeSpan.FromDays(2)));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => manager.SelectAsync(IdB));
        }

        Assert.True(manager.AutoSwapEnabled);
        Assert.Equal(TimeSpan.FromDays(7), manager.AutoSwapInterval);
        Assert.Equal(Start, manager.LastSwapUtc);
        Assert.Equal(IdA, manager.SelectedKey);
    });

    [Fact]
    public void AFileFromBeforeTheSetting_IsReadAsOff() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        File.WriteAllText(Path.Combine(setup.Folder, "playlists.json"), $$"""
            {
              "selectedPlaylistUrl": "{{Url(IdA)}}",
              "playlists": [
                { "title": "Alpha", "url": "{{Url(IdA)}}" },
                { "title": "Beta", "url": "{{Url(IdB)}}" }
              ]
            }
            """);
        setup.WithCache(IdA, "a1").WithCache(IdB, "b1");

        var manager = await LoadAsync(setup.Reopen(new FakeTimeProvider(Start)));

        Assert.False(manager.AutoSwapEnabled);
        Assert.Equal(TimeSpan.FromDays(7), manager.AutoSwapInterval);
        Assert.Null(manager.LastSwapUtc);
    });

    [Theory]
    [InlineData("null")]
    [InlineData("\"2027-01-01T00:00:00+00:00\"")] // the clock was turned back since
    public void AFileWithoutAUsableStartOfTheInterval_StartsItAtTheNextStart(string lastSwap) => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        File.WriteAllText(Path.Combine(setup.Folder, "playlists.json"), $$"""
            {
              "selectedPlaylistUrl": "{{Url(IdA)}}",
              "playlists": [
                { "title": "Alpha", "url": "{{Url(IdA)}}" },
                { "title": "Beta", "url": "{{Url(IdB)}}" }
              ],
              "autoSwap": { "enabled": true, "intervalHours": 0, "lastSwapUtc": {{lastSwap}} }
            }
            """);
        setup.WithCache(IdA, "a1").WithCache(IdB, "b1");

        var manager = await LoadAsync(setup.Reopen(new FakeTimeProvider(Start)));

        Assert.Equal(TimeSpan.FromHours(1), manager.AutoSwapInterval); // a broken interval becomes the shortest one
        Assert.Equal(Start, manager.LastSwapUtc);
        Assert.Equal(Start, setup.SavedLibrary().AutoSwap.LastSwapUtc);
    });

    [Fact]
    public void TheNextPlaylist_IsTheOneAfterTheSelectedOne_StartingOverAfterTheLast() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        Assert.Equal("Beta", manager.GetNextPlaylist()?.Title);

        await manager.SelectAsync(IdC);
        Assert.Equal("Alpha", manager.GetNextPlaylist()?.Title);

        await manager.SelectAsync(null); // after all playlists combined: the first one
        Assert.Equal("Alpha", manager.GetNextPlaylist()?.Title);
    });

    [Fact]
    public void TheNextPlaylist_SkipsPlaylistsThatCannotBePlayed() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Unreachable")
            .WithPlaylist(IdC, "Gamma").WithCache(IdC, "c1");
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));
        Assert.Equal(PlaylistLoadState.Failed, manager.Playlists[1].State);

        Assert.Equal("Gamma", manager.GetNextPlaylist()?.Title);

        manager.Remove(IdC);
        Assert.Null(manager.GetNextPlaylist());
        Assert.False(((IPlaylistSwitch)manager).HasOtherPlaylist);
    });

    [Fact]
    public void SwitchingToTheNextPlaylist_SelectsIt_AndSkipsTheCurrentVideoUnlessItShouldPlayOn() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        var events = new List<PoolChangedEventArgs>();
        manager.PoolChanged += (_, e) => events.Add(e);

        time.Advance(TimeSpan.FromHours(1));
        var title = await ((IPlaylistSwitch)manager).SwitchToNextPlaylistAsync(); // !skipplaylist

        Assert.Equal("Beta", title);
        Assert.Equal(IdB, manager.SelectedKey);
        Assert.Equal(Start + TimeSpan.FromHours(1), manager.LastSwapUtc);
        var skip = Assert.Single(events);
        Assert.True(skip.SelectionChanged);
        Assert.False(skip.KeepCurrentVideo);

        var swapped = await manager.SwitchToNextAsync(keepCurrentVideo: true); // the automatic swap
        Assert.Equal("Gamma", swapped?.Title);
        Assert.True(events[1].SelectionChanged);
        Assert.True(events[1].KeepCurrentVideo);
        Assert.True(manager.IsInPool("c1"));
        Assert.False(manager.IsInPool("b1"));
    });

    [Fact]
    public void SwitchingToTheNextPlaylist_WithoutAnother_ReturnsNull() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1");
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        Assert.Null(await ((IPlaylistSwitch)manager).SwitchToNextPlaylistAsync());
        Assert.Equal(IdA, manager.SelectedKey);
    });

    [Fact]
    public void TheRandomOrder_IsOffByDefault_IsSaved_AndDoesNotStartTheIntervalOver() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var time = new FakeTimeProvider(Start);
        var manager = await LoadAsync(setup, time);
        manager.SetAutoSwap(true, TimeSpan.FromDays(7));
        Assert.False(manager.RandomSwapOrder);
        var changes = 0;
        manager.AutoSwapChanged += (_, _) => changes++;

        time.Advance(TimeSpan.FromDays(2));
        manager.SetRandomSwapOrder(true);

        Assert.True(manager.RandomSwapOrder);
        Assert.Equal(1, changes);
        Assert.Equal(Start, manager.LastSwapUtc); // it does not change when the swap happens
        Assert.True(setup.SavedLibrary().AutoSwap.RandomOrder);
        Assert.True((await LoadAsync(setup.Reopen(time))).RandomSwapOrder);

        manager.SetRandomSwapOrder(true); // no change
        Assert.Equal(1, changes);
    });

    [Fact]
    public void WithTheRandomOrder_TheNextPlaylistIsAnyOtherOne_NeverTheOneThatPlays() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists().WithPlaylist(IdD, "Delta").WithCache(IdD, "d1");
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));
        manager.SetRandomSwapOrder(true);

        var picks = Enumerable.Range(0, 200).Select(_ => manager.GetNextPlaylist()!.Title).ToHashSet();

        Assert.Equal(new HashSet<string> { "Beta", "Gamma", "Delta" }, picks);
        Assert.Equal(new[] { "Beta", "Gamma", "Delta" }, manager.GetSwapCandidates().Select(p => p.Title)); // in list order
    });

    [Fact]
    public void WithTheRandomOrder_AllPlaylistsCombined_IsNeverPicked_AlsoWhenItIsWhatPlays() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));
        manager.SetRandomSwapOrder(true);
        await manager.SelectAsync(null); // "All playlists"

        var picks = Enumerable.Range(0, 200).Select(_ => manager.GetNextPlaylist()).ToList();

        Assert.All(picks, pick => Assert.NotNull(pick));
        Assert.Equal(new HashSet<string> { "Alpha", "Beta", "Gamma" }, picks.Select(p => p!.Title).ToHashSet());

        // Switching again and again never lands on "All playlists" (SelectedKey null).
        for (var i = 0; i < 40; i++)
        {
            await ((IPlaylistSwitch)manager).SwitchToNextPlaylistAsync();
            Assert.NotNull(manager.SelectedKey);
        }
    });

    [Fact]
    public void WithTheRandomOrder_ASwitchGoesToARandomOtherPlaylist() => UiContext.Run(async () =>
    {
        using var setup = ThreePlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));
        var playlistSwitch = (IPlaylistSwitch)manager;

        Assert.True(playlistSwitch.ToggleRandomOrder());
        Assert.True(playlistSwitch.RandomOrder);
        var titles = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            var before = manager.SelectedKey;
            titles.Add((await playlistSwitch.SwitchToNextPlaylistAsync())!);
            Assert.NotEqual(before, manager.SelectedKey);
        }

        Assert.Equal(3, titles.Count);
        Assert.False(playlistSwitch.ToggleRandomOrder());
    });

    [Fact]
    public void WhenSavingTheRandomOrderFails_TheOldValueStays() => UiContext.Run(async () =>
    {
        using var setup = TwoPlaylists();
        var manager = await LoadAsync(setup, new FakeTimeProvider(Start));

        using (new UnwritableLibrary(setup))
            Assert.ThrowsAny<UnauthorizedAccessException>(() => manager.SetRandomSwapOrder(true));

        Assert.False(manager.RandomSwapOrder);
    });

    [Theory]
    [InlineData(1, "1 hour")]
    [InlineData(5, "5 hours")]
    [InlineData(24, "1 day")]
    [InlineData(7 * 24 + 3, "7 days 3 hours")]
    public void IntervalsAreLoggedReadably(int hours, string expected) =>
        Assert.Equal(expected, PlaylistManager.FormatInterval(TimeSpan.FromHours(hours)));
}
