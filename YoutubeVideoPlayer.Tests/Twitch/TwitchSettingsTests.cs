using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TwitchSettingsTests
{
    private static TwitchSettings? LoadFrom(string json)
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File(TwitchSettings.FileName), json);
        return TwitchSettings.Load(dir.Path);
    }

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        using var dir = new TempDirectory();

        Assert.Null(TwitchSettings.Load(dir.Path));
    }

    [Fact]
    public void Load_WithInvalidJson_ReturnsNull()
    {
        Assert.Null(LoadFrom("{ nope"));
    }

    [Fact]
    public void Load_NormalizesNamesAndPrivilegedUsers()
    {
        var settings = LoadFrom("""
            {
              "clientId": "  cid  ",
              "botUsername": "  LobosBot ",
              "oAuthToken": "  oauth:abc ",
              "channel": " #LobosJr ",
              "privilegedUsers": [ " @Carsten ", "", "12345", "   " ]
            }
            """)!;

        Assert.Equal("cid", settings.ClientId);
        Assert.Equal("lobosbot", settings.BotUsername);
        Assert.Equal("oauth:abc", settings.OAuthToken);
        Assert.Equal("lobosjr", settings.Channel);
        Assert.Equal(new[] { "carsten", "12345" }, settings.PrivilegedUsers);
    }

    [Fact]
    public void Load_UsesDefaultsForMissingValues()
    {
        var settings = LoadFrom("{ \"channel\": \"c\", \"privilegedUsers\": null }")!;

        Assert.True(settings.Enabled);
        Assert.Equal(5, settings.TimestampCooldownSeconds);
        Assert.Equal(3, settings.SkipCooldownSeconds);
        Assert.Equal(120, settings.SkipVoteDurationSeconds);
        Assert.Equal(5, settings.CurrentCooldownSeconds);
        Assert.Equal(3, settings.ShuffleCooldownSeconds);
        Assert.Empty(settings.PrivilegedUsers);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(120, 120)]
    [InlineData(99999, 1800)]
    public void Load_ClampsTheVoteDuration(int configured, int expected)
    {
        Assert.Equal(expected, LoadFrom($"{{ \"skipVoteDurationSeconds\": {configured} }}")!.SkipVoteDurationSeconds);
    }

    [Theory]
    [InlineData(true, "cid", "", "", "chan", true, true)]       // app sign-in
    [InlineData(true, "", "bot", "oauth:x", "chan", true, false)] // manual token
    [InlineData(true, "", "bot", "", "chan", false, false)]      // manual token missing
    [InlineData(true, "", "", "oauth:x", "chan", false, false)]  // bot name missing
    [InlineData(true, "cid", "", "", "", false, true)]           // channel missing
    [InlineData(false, "cid", "bot", "oauth:x", "chan", false, true)] // disabled
    public void IsComplete_And_UsesAppSignIn(bool enabled, string clientId, string bot, string token, string channel,
        bool complete, bool appSignIn)
    {
        var settings = new TwitchSettings
        {
            Enabled = enabled,
            ClientId = clientId,
            BotUsername = bot,
            OAuthToken = token,
            Channel = channel
        };

        Assert.Equal(complete, settings.IsComplete);
        Assert.Equal(appSignIn, settings.UsesAppSignIn);
    }

    [Fact]
    public void ExampleSettingsFile_IsValidAndComplete()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "YoutubeVideoPlayer", "twitchsettings.example.json");
        using var dir = new TempDirectory();
        File.Copy(example, dir.File(TwitchSettings.FileName));

        var settings = TwitchSettings.Load(dir.Path)!;

        Assert.True(settings.IsComplete);
        Assert.True(settings.UsesAppSignIn);
        Assert.Equal(2, settings.PrivilegedUsers.Count);
    }
}
