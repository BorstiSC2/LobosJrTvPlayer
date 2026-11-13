using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// Prepares a playlist library and cache in a temporary folder and a substitute for YouTube,
/// and creates <see cref="PlaylistManager"/>s on top of them.
/// </summary>
public sealed class PlaylistSetup : IDisposable
{
    // Syntactically valid YouTube playlist IDs.
    public const string IdA = "PLaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string IdB = "PLbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string IdC = "PLcccccccccccccccccccccccccccccccc";

    private readonly TempDirectory _dir = new();

    public PlaylistSetup()
    {
        Store = new PlaylistStore(_dir.Path);
        Cache = new PlaylistCache(_dir.Path);
        Library = new PlaylistLibrary();

        // Unknown playlists fail like an unreachable YouTube would.
        YouTube.GetPlaylistVideoIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("no such playlist"));
    }

    public string Folder => _dir.Path;

    public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

    public PlaylistStore Store { get; }

    public PlaylistCache Cache { get; }

    public PlaylistLibrary Library { get; }

    public static string Url(string id) => $"https://www.youtube.com/playlist?list={id}";

    /// <summary>Adds a playlist to the library (saved when <see cref="Create"/> is called).</summary>
    public PlaylistSetup WithPlaylist(string id, string title, bool selected = false)
    {
        Library.Playlists.Add(new PlaylistEntry { Title = title, Url = Url(id) });
        if (selected)
            Library.SelectedPlaylistUrl = Url(id);
        return this;
    }

    public PlaylistSetup WithCache(string id, params string[] videoIds)
    {
        Cache.Write(new PlaylistCacheData
        {
            PlaylistId = id,
            Title = id,
            FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            VideoIds = videoIds.ToList()
        });
        return this;
    }

    /// <summary>Makes YouTube return these IDs when the playlist is downloaded.</summary>
    public PlaylistSetup OnYouTube(string id, params string[] videoIds)
    {
        YouTube.GetPlaylistVideoIdsAsync(id, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<string>)videoIds.ToList());
        return this;
    }

    public PlaylistManager Create(TimeProvider? time = null)
    {
        Store.Save(Library);
        return Reopen(time);
    }

    /// <summary>A manager on the files as they are now, without saving <see cref="Library"/> first (like a restart of the app).</summary>
    public PlaylistManager Reopen(TimeProvider? time = null) => new(YouTube, Store, Cache, time);

    public PlaylistLibrary SavedLibrary() => new PlaylistStore(_dir.Path).Load();

    public int Downloads(string id) =>
        YouTube.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(IYouTubeService.GetPlaylistVideoIdsAsync) && (string)c.GetArguments()[0]! == id);

    public void Dispose() => _dir.Dispose();
}
