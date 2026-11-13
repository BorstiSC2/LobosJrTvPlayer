using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.YouTube;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.Playlists;

/// <summary>The order of a playlist: random (shuffled) or one video after the other.</summary>
public class ShuffleTests
{
    private static async Task WaitForBackgroundLoadingAsync(PlaylistManager manager) =>
        await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));

    /// <summary>Marks the playlist at <paramref name="index"/> as "plays in playlist order".</summary>
    private static PlaylistSetup InOrder(PlaylistSetup setup, int index = 0)
    {
        setup.Library.Playlists[index].Shuffle = false;
        return setup;
    }

    #region Saving the setting

    [Fact]
    public void Playlists_AreShuffledByDefault_AlsoWhenTheFileHasNoSuchValue() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup();
        // An older playlists.json: no "Shuffle" on the entries.
        File.WriteAllText(
            Path.Combine(setup.Folder, "playlists.json"),
            $"{{ \"SelectedPlaylistUrl\": \"{Url(IdA)}\", \"Playlists\": [ {{ \"Title\": \"A\", \"Url\": \"{Url(IdA)}\" }} ] }}");
        setup.WithCache(IdA, "a1", "a2");
        var manager = new PlaylistManager(setup.YouTube, setup.Store, setup.Cache);

        await manager.InitializeAsync();

        Assert.True(manager.Playlists[0].Shuffle);
        Assert.True(manager.IsShuffled);
    });

    [Fact]
    public void TheOrderIsSavedWithThePlaylist_AndComesBackAfterARestart() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();

        manager.SetShuffle(IdA, false);

        var saved = setup.SavedLibrary().Playlists;
        Assert.False(saved[0].Shuffle);
        Assert.True(saved[1].Shuffle); // only the changed playlist

        var restarted = new PlaylistManager(setup.YouTube, setup.Store, setup.Cache);
        await restarted.InitializeAsync();
        Assert.False(restarted.IsShuffled);
        Assert.False(restarted.Playlists[0].Shuffle);
        Assert.True(restarted.Playlists[1].Shuffle);
    });

    [Fact]
    public void SetShuffle_RaisesPlaylistsChangedOnlyForARealChange_AndRejectsUnknownPlaylists() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        var changes = 0;
        var poolChanges = 0;
        manager.PlaylistsChanged += (_, _) => changes++;
        manager.PoolChanged += (_, _) => poolChanges++;

        manager.SetShuffle(IdA, true); // already shuffled
        Assert.Equal(0, changes);

        manager.SetShuffle(IdA, false);
        Assert.Equal(1, changes);
        Assert.Equal(0, poolChanges); // the videos stay the same; only their order changes

        Assert.Throws<InvalidOperationException>(() => manager.SetShuffle("PLunknown", false));
    });

    [Fact]
    public void SetShuffle_WhenSavingFails_KeepsTheOldValue() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var file = Path.Combine(setup.Folder, "playlists.json");
        File.SetAttributes(file, FileAttributes.ReadOnly); // can be read, cannot be replaced
        try
        {
            Assert.ThrowsAny<Exception>(() => manager.SetShuffle(IdA, false));

            Assert.True(manager.Playlists[0].Shuffle);
            Assert.True(manager.IsShuffled);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    });

    #endregion

    #region Which video comes next

    [Fact]
    public void AnOrderedPlaylist_PlaysOneVideoAfterTheOther_AndStartsOverAfterTheLastOne() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3"));
        var manager = setup.Create();
        await manager.InitializeAsync();

        string? Next(string? current) => manager.TryGetNextVideoId(current, out var id) ? id : null;

        Assert.Equal("a1", Next(null));      // nothing played yet: the first video
        Assert.Equal("a2", Next("a1"));
        Assert.Equal("a3", Next("a2"));
        Assert.Equal("a1", Next("a3"));      // loops
        Assert.Equal("a1", Next("unknown")); // e.g. coming from another playlist
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal("a2", Next("a1"))); // never random
    });

    [Fact]
    public void AShuffledPlaylist_PicksRandomVideos_ButNotTheOneThatJustEnded() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3");
        var manager = setup.Create();
        await manager.InitializeAsync();

        var picks = Enumerable.Range(0, 300).Select(_ => manager.TryGetNextVideoId("a2", out var id) ? id : null).ToList();

        Assert.DoesNotContain("a2", picks);
        Assert.Contains("a1", picks);
        Assert.Contains("a3", picks);
    });

    [Fact]
    public void AShuffledPlaylistWithOneVideo_RepeatsIt() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "only");
        var manager = setup.Create();
        await manager.InitializeAsync();

        Assert.True(manager.TryGetNextVideoId("only", out var id));
        Assert.Equal("only", id);
    });

    [Fact]
    public void AnOrderedPlaylist_OffersTheNextEpisodesInOrder_ForAVote() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true)
            .WithCache(IdA, "e1", "e2", "e3", "e4", "e5", "e6"));
        var manager = setup.Create();
        await manager.InitializeAsync();

        Assert.Equal(new[] { "e3", "e4", "e5" }, manager.GetNextVideoIds(3, "e2"));
        Assert.Equal(new[] { "e1", "e2", "e3" }, manager.GetNextVideoIds(3, null));            // nothing playing yet
        Assert.Equal(new[] { "e6", "e1", "e2" }, manager.GetNextVideoIds(3, "e5"));            // wraps around
        Assert.Equal(new[] { "e1", "e2", "e3", "e4", "e5" }, manager.GetNextVideoIds(99, "e6")); // never the current one
        Assert.Empty(manager.GetNextVideoIds(0, "e1"));
    });

    [Fact]
    public void AnOrderedPlaylistWithFewVideos_OffersOnlyWhatIsThere() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "e1", "e2", "e3"));
        var manager = setup.Create();
        await manager.InitializeAsync();

        Assert.Equal(new[] { "e3", "e1" }, manager.GetNextVideoIds(3, "e2")); // only two others exist
    });

    [Fact]
    public void AllPlaylistsCombined_AlwaysPlayInRandomOrder_AndTheOrderCannotBeChanged() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1", "b2"));
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        Assert.False(manager.IsShuffled); // A is ordered

        await manager.SelectAsync(null);

        Assert.True(manager.IsShuffled);
        Assert.False(manager.CanChangeShuffle);
        Assert.Null(manager.PlaylistTitle);
        Assert.Throws<InvalidOperationException>(() => manager.ToggleShuffle());
        var picks = Enumerable.Range(0, 200).Select(_ => manager.TryGetNextVideoId("a1", out var id) ? id : null).Distinct().ToList();
        Assert.True(picks.Count > 2); // not just the one video after "a1"
    });

    #endregion

    #region Toggling

    [Fact]
    public void ToggleShuffle_SwitchesTheSelectedPlaylist_AndReturnsTheNewValue() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1")
            .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);

        Assert.True(manager.CanChangeShuffle);
        Assert.Equal("Alpha", manager.PlaylistTitle);
        Assert.True(manager.IsShuffled);

        Assert.False(manager.ToggleShuffle());
        Assert.False(manager.IsShuffled);
        Assert.False(setup.SavedLibrary().Playlists[0].Shuffle);
        Assert.True(manager.Playlists[1].Shuffle); // Beta is not affected

        Assert.True(manager.ToggleShuffle());
        Assert.True(manager.IsShuffled);
    });

    [Fact]
    public void EachPlaylistKeepsItsOwnOrder_WhenTheSelectionChanges() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1", "b2"));
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        Assert.False(manager.IsShuffled);

        await manager.SelectAsync(IdB);
        Assert.True(manager.IsShuffled); // B was never changed

        await manager.SelectAsync(IdA);
        Assert.False(manager.IsShuffled);
    });

    #endregion

    #region SequenceChanged (a choice made in advance is out of date)

    [Fact]
    public void SequenceChanged_IsRaisedWhenTheSelectionOrTheOrderOfTheSelectedPlaylistChanges() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup()
            .WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2")
            .WithPlaylist(IdB, "B").WithCache(IdB, "b1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        await WaitForBackgroundLoadingAsync(manager);
        var raised = 0;
        manager.SequenceChanged += (_, _) => raised++;

        manager.SetShuffle(IdA, false);        // the playlist that plays
        Assert.Equal(1, raised);

        manager.SetShuffle(IdA, false);        // no change
        manager.SetShuffle(IdB, false);        // a playlist that does not play
        Assert.Equal(1, raised);

        await manager.SelectAsync(IdB);        // another playlist
        Assert.Equal(2, raised);
    });

    [Fact]
    public void SequenceChanged_IsRaisedWhenThePlayingPlaylistIsUpdated() => UiContext.Run(async () =>
    {
        using var setup = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1");
        var manager = setup.Create();
        await manager.InitializeAsync();
        var raised = 0;
        manager.SequenceChanged += (_, _) => raised++;

        setup.OnYouTube(IdA, "a1", "a2");
        await manager.UpdateAsync(IdA);

        Assert.Equal(1, raised);
    });

    #endregion

    #region Playback and votes follow the order

    [Fact]
    public void SkipAndTheEndOfAVideo_ContinueInPlaylistOrder_AndLoop() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "e1", "e2", "e3"));
        var playlists = setup.Create();
        await playlists.InitializeAsync();
        setup.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => PlaybackSetup.Resolved(call.ArgAt<string>(0)));
        var time = new FakeTimeProvider();
        var player = Substitute.For<IMediaPlayer>();
        using var playback = new PlaybackManager(setup.YouTube, playlists, player, time);

        async Task FinishAsync(Task operation)
        {
            await Async.SettleAsync();
            time.Advance(PlaybackManager.DelayBeforeNextVideo);
            await Async.SettleAsync();
            await operation;
        }

        List<string> Played() => player.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play))
            .Select(c => (string)c.GetArguments()[0]!)
            .ToList();

        await FinishAsync(playback.PlayNextAsync());   // starts at the first video
        await FinishAsync(playback.SkipAsync());       // a skip only goes to the next video
        player.EndReached += Raise.Event();            // so does the end of a video
        await FinishAsync(Task.CompletedTask);
        player.EndReached += Raise.Event();
        await FinishAsync(Task.CompletedTask);

        Assert.Equal(new[] { "e1.video", "e2.video", "e3.video", "e1.video" }, Played());
    });

    [Fact]
    public void AfterAVoteWinner_TheOrderContinuesBehindTheWinner() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true)
            .WithCache(IdA, "e1", "e2", "e3", "e4", "e5"));
        var playlists = setup.Create();
        await playlists.InitializeAsync();
        setup.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => PlaybackSetup.Resolved(call.ArgAt<string>(0)));
        var time = new FakeTimeProvider();
        var player = Substitute.For<IMediaPlayer>();
        using var playback = new PlaybackManager(setup.YouTube, playlists, player, time);

        var first = playback.PlayNextAsync();
        await Async.SettleAsync();
        time.Advance(PlaybackManager.DelayBeforeNextVideo);
        await first;                                   // e1
        await playback.PlayVideoAsync("e4");           // the winner of a vote: one of the next episodes
        var skip = playback.SkipAsync();
        await Async.SettleAsync();
        time.Advance(PlaybackManager.DelayBeforeNextVideo);
        await skip;

        player.Received(1).Play("e5.video", Arg.Any<string?>()); // continues after e4, not after e1
    });

    private static (SkipVoteManager Votes, List<string> Sent) VoteOn(PlaylistSetup setup, PlaylistManager playlists, string? current, FakeTimeProvider time)
    {
        var chat = Chat.Channel(out var sent);
        var playback = Substitute.For<IPlaybackControl>();
        playback.Current.Returns(current == null ? null : new NowPlaying(current, "Current", null));
        setup.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new YouTubeVideoDetails("Title " + call.ArgAt<string>(0), null));
        return (new SkipVoteManager(chat, playback, playlists, setup.YouTube, TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, time), sent);
    }

    private static async Task PostAllOptionsAsync(FakeTimeProvider time)
    {
        await Async.SettleAsync();
        for (var i = 0; i < SkipVoteManager.OptionCount; i++)
        {
            time.Advance(SkipVoteManager.MessageDelay);
            await Async.SettleAsync();
        }
    }

    [Fact]
    public void AVoteInAnOrderedPlaylist_OffersTheNextThreeEpisodes() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true)
            .WithCache(IdA, "e1", "e2", "e3", "e4", "e5", "e6"));
        var playlists = setup.Create();
        await playlists.InitializeAsync();
        var time = new FakeTimeProvider();
        var (votes, sent) = VoteOn(setup, playlists, "e2", time);

        var reply = await votes.StartAsync(YouTubeVideoPlayer.Tests.TestSupport.Chat.Command("!skipvote", moderator: true));
        await PostAllOptionsAsync(time);

        Assert.Null(reply);
        Assert.Equal(
            new[] { "1) Title e3 – vote with !vote 1", "2) Title e4 – vote with !vote 2", "3) Title e5 – vote with !vote 3" },
            sent.Skip(1));
        votes.Dispose();
    });

    [Fact]
    public void AVoteInAShortOrderedPlaylist_OffersTheEpisodesThatExist() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "e1", "e2", "e3"));
        var playlists = setup.Create();
        await playlists.InitializeAsync();
        var time = new FakeTimeProvider();
        var (votes, sent) = VoteOn(setup, playlists, "e2", time);

        var reply = await votes.StartAsync(YouTubeVideoPlayer.Tests.TestSupport.Chat.Command("!skipvote", moderator: true));
        await Async.SettleAsync();
        time.Advance(SkipVoteManager.MessageDelay);
        await Async.SettleAsync();
        time.Advance(SkipVoteManager.MessageDelay);
        await Async.SettleAsync();

        Assert.Null(reply);
        Assert.Equal(new[] { "1) Title e3 – vote with !vote 1", "2) Title e1 – vote with !vote 2" }, sent.Skip(1));
        votes.Dispose();
    });

    [Fact]
    public void AVoteWithOnlyOneOtherEpisode_IsNotPossible() => UiContext.Run(async () =>
    {
        using var setup = InOrder(new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "e1", "e2"));
        var playlists = setup.Create();
        await playlists.InitializeAsync();
        var (votes, sent) = VoteOn(setup, playlists, "e1", new FakeTimeProvider());

        var reply = await votes.StartAsync(YouTubeVideoPlayer.Tests.TestSupport.Chat.Command("!skipvote", moderator: true));

        Assert.Equal("There are not enough videos available for a vote.", reply);
        Assert.Empty(sent);
    });

    #endregion
}
