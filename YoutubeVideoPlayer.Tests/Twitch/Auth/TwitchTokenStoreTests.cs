using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Tests.Twitch.Auth;

public class TwitchTokenStoreTests
{
    private static readonly TwitchToken Token = new()
    {
        AccessToken = "acc",
        RefreshToken = "ref",
        ExpiresAtUtc = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc),
        Login = "lobosbot",
        UserId = "4711",
        Scopes = new[] { "chat:read", "chat:edit" }
    };

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        using var dir = new TempDirectory();

        Assert.Null(new TwitchTokenStore(dir.Path).Load());
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        var store = new TwitchTokenStore(dir.Path);

        store.Save(Token);
        var loaded = store.Load()!;

        Assert.Equal(Token with { Scopes = loaded.Scopes }, loaded);
        Assert.Equal(Token.Scopes, loaded.Scopes);
    }

    [Fact]
    public void Save_WritesPlainJson_SoTheFileCanBeMovedToAnotherComputer()
    {
        using var dir = new TempDirectory();

        new TwitchTokenStore(dir.Path).Save(Token);

        var json = File.ReadAllText(dir.File(TwitchTokenStore.FileName));
        Assert.Contains("\"RefreshToken\": \"ref\"", json);
        Assert.Contains("\"AccessToken\": \"acc\"", json);
    }

    [Fact]
    public void Load_WithoutRefreshToken_ReturnsNull()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File(TwitchTokenStore.FileName), "{ \"AccessToken\": \"acc\" }");

        Assert.Null(new TwitchTokenStore(dir.Path).Load());
    }

    [Fact]
    public void Load_WithAFileThatCannotBeReadRightNow_ReturnsNullAndKeepsTheFile()
    {
        using var dir = new TempDirectory();
        var store = new TwitchTokenStore(dir.Path);
        store.Save(Token);

        using (new FileStream(dir.File(TwitchTokenStore.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(store.Load());
        }

        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt-*"));
        Assert.Equal("ref", store.Load()!.RefreshToken);
    }

    [Fact]
    public void Load_WithCorruptFile_QuarantinesItAndReturnsNull()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File(TwitchTokenStore.FileName), "{ broken");

        Assert.Null(new TwitchTokenStore(dir.Path).Load());
        Assert.Single(Directory.GetFiles(dir.Path, "twitchtoken.json.corrupt-*"));
    }
}
