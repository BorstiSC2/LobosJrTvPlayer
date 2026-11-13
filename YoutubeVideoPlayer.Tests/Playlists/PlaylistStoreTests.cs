using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;

namespace YouTubeVideoPlayer.Tests.Playlists;

public class PlaylistStoreTests
{
    [Fact]
    public void Load_WithoutAnyFile_CreatesAnEmptyLibrary()
    {
        using var dir = new TempDirectory();

        var library = new PlaylistStore(dir.Path).Load();

        Assert.Empty(library.Playlists);
        Assert.Null(library.SelectedPlaylistUrl);
        Assert.True(File.Exists(dir.File("playlists.json")));
    }

    [Fact]
    public void Load_WithoutPlaylistsJson_CopiesTheDefaultFile()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("playlists.default.json"),
            "{ \"selectedPlaylistUrl\": \"u1\", \"playlists\": [ { \"title\": \"Default\", \"url\": \"u1\" } ] }");

        var library = new PlaylistStore(dir.Path).Load();

        var entry = Assert.Single(library.Playlists);
        Assert.Equal("Default", entry.Title);
        Assert.Equal("u1", library.SelectedPlaylistUrl);
        Assert.Contains("Default", File.ReadAllText(dir.File("playlists.json")));
    }

    [Fact]
    public void Load_PrefersPlaylistsJsonOverTheDefault()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("playlists.default.json"), "{ \"playlists\": [ { \"title\": \"Default\", \"url\": \"d\" } ] }");
        File.WriteAllText(dir.File("playlists.json"), "{ \"playlists\": [ { \"title\": \"Mine\", \"url\": \"m\" } ] }");

        var library = new PlaylistStore(dir.Path).Load();

        Assert.Equal("Mine", Assert.Single(library.Playlists).Title);
    }

    [Fact]
    public void Load_WithACorruptFile_QuarantinesItAndFallsBackToTheDefault()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("playlists.json"), "{ broken");
        File.WriteAllText(dir.File("playlists.default.json"), "{ \"playlists\": [ { \"title\": \"Default\", \"url\": \"d\" } ] }");

        var library = new PlaylistStore(dir.Path).Load();

        Assert.Equal("Default", Assert.Single(library.Playlists).Title);
        Assert.Single(Directory.GetFiles(dir.Path, "playlists.json.corrupt-*"));
    }

    [Fact]
    public void Load_WithNullPlaylists_ReturnsAnEmptyList()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("playlists.json"), "{ \"playlists\": null }");

        var library = new PlaylistStore(dir.Path).Load();

        Assert.NotNull(library.Playlists);
        Assert.Empty(library.Playlists);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        var store = new PlaylistStore(dir.Path);
        var library = new PlaylistLibrary
        {
            SelectedPlaylistUrl = "b",
            Playlists = { new PlaylistEntry { Title = "A", Url = "a" }, new PlaylistEntry { Title = "B", Url = "b" } }
        };

        store.Save(library);
        var loaded = store.Load();

        Assert.Equal("b", loaded.SelectedPlaylistUrl);
        Assert.Equal(new[] { "A", "B" }, loaded.Playlists.Select(p => p.Title));
    }

    [Fact]
    public void DefaultConstructor_UsesTheApplicationFolder()
    {
        var store = new PlaylistStore();
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "playlists.json");
        var existed = File.Exists(path);
        try
        {
            Assert.NotNull(store.Load());
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (!existed)
                File.Delete(path);
        }
    }
}

public class PlaylistCacheTests
{
    private static PlaylistCacheData Data(string id, params string[] videos) => new()
    {
        PlaylistId = id,
        Title = "Title " + id,
        FetchedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        VideoIds = videos.ToList()
    };

    [Fact]
    public void TryRead_ReturnsNullWithoutCache()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);

        Assert.Null(cache.TryRead("PLmissing"));
        Assert.False(cache.Exists("PLmissing"));
    }

    [Fact]
    public void Write_ThenTryRead_RoundTrips()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);

        cache.Write(Data("PLabc", "v1", "v2"));
        var read = cache.TryRead("PLabc");

        Assert.True(cache.Exists("PLabc"));
        Assert.NotNull(read);
        Assert.Equal(new[] { "v1", "v2" }, read!.VideoIds);
        Assert.Equal("Title PLabc", read.Title);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), read.FetchedAtUtc);
        Assert.True(File.Exists(Path.Combine(dir.Path, "PlaylistCache", "PLabc.json")));
    }

    [Fact]
    public void Delete_RemovesTheCache_AndIgnoresMissingFiles()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);
        cache.Write(Data("PLabc", "v1"));

        cache.Delete("PLabc");
        cache.Delete("PLabc");

        Assert.False(cache.Exists("PLabc"));
    }

    [Fact]
    public void TryRead_WithACorruptFile_QuarantinesItAndReturnsNull()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);
        Directory.CreateDirectory(Path.Combine(dir.Path, "PlaylistCache"));
        File.WriteAllText(Path.Combine(dir.Path, "PlaylistCache", "PLabc.json"), "{ broken");

        Assert.Null(cache.TryRead("PLabc"));
        Assert.False(cache.Exists("PLabc"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "PlaylistCache"), "*.corrupt-*"));
    }

    [Fact]
    public void TryRead_WithNullVideoIds_ReturnsAnEmptyList()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);
        Directory.CreateDirectory(Path.Combine(dir.Path, "PlaylistCache"));
        File.WriteAllText(Path.Combine(dir.Path, "PlaylistCache", "PLabc.json"), "{ \"playlistId\": \"PLabc\", \"videoIds\": null }");

        Assert.Empty(cache.TryRead("PLabc")!.VideoIds);
    }

    [Fact]
    public void FileNames_AreSanitized_SoIdsCannotEscapeTheCacheFolder()
    {
        using var dir = new TempDirectory();
        var cache = new PlaylistCache(dir.Path);

        cache.Write(Data(@"..\..\evil/../PL_ok-1", "v1"));

        Assert.True(File.Exists(Path.Combine(dir.Path, "PlaylistCache", "evilPL_ok-1.json")));
        Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }
}
