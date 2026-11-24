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

    /// <summary>A version 2 file with only the given sections.</summary>
    private static TwitchSettings LoadV2(string body) => LoadFrom($"{{ \"version\": 2, {body} }}")!;

    [Fact]
    public void Load_WithoutFile_ReturnsNull()
    {
        using var dir = new TempDirectory();

        Assert.Null(TwitchSettings.Load(dir.Path));
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{ \"version\": 2, \"signIn\": 5 }")]
    public void Load_WithInvalidContent_ReturnsNull(string json)
    {
        Assert.Null(LoadFrom(json));
    }

    [Fact]
    public void Load_NormalizesNamesAndPrivilegedUsers()
    {
        var settings = LoadV2("""
            "signIn": { "clientId": "  cid  ", "botLogin": "  LobosBot ", "oauthToken": "  oauth:abc " },
            "channel": " #LobosJr ",
            "privilegedUsers": [ " @Carsten ", "", "12345", "   " ]
            """);

        Assert.Equal("cid", settings.SignIn.ClientId);
        Assert.Equal("lobosbot", settings.SignIn.BotLogin);
        Assert.Equal("oauth:abc", settings.SignIn.OAuthToken);
        Assert.Equal("lobosjr", settings.Channel);
        Assert.Equal(new[] { "carsten", "12345" }, settings.PrivilegedUsers);
    }

    [Fact]
    public void Load_UsesDefaultsForMissingValues()
    {
        var settings = LoadV2("\"channel\": \"c\", \"privilegedUsers\": null, \"signIn\": null, \"voteSkip\": null");

        Assert.True(settings.Enabled);
        Assert.Equal(TwitchSettingsMigration.CurrentVersion, settings.Version);
        Assert.Equal(string.Empty, settings.SignIn.ClientId);
        Assert.Empty(settings.PrivilegedUsers);

        var c = settings.CommandCooldownSeconds;
        Assert.Equal(3, c.PlaybackControl);
        Assert.Equal(5, c.Current);
        Assert.Equal(5, c.Playlist);
        Assert.Equal(5, c.VoteSkip);
        Assert.Equal(30, c.VoteInfo);
        Assert.Equal(3, c.ShuffleStatus);
        Assert.Equal(3, c.ToggleShuffle);
        Assert.Equal(3, c.PlaylistShuffleStatus);
        Assert.Equal(3, c.TogglePlaylistShuffle);
        Assert.Equal(5, c.AutoVoteStatus);
        Assert.Equal(5, c.ToggleAutoVote);
        Assert.Equal(5, c.Token);
        Assert.Equal(5, c.VodCommands);
        Assert.Equal(5, c.ModVodCommands);
        Assert.Equal(5, c.CommandHelp);

        Assert.Equal(120, settings.NextVideoVote.DurationSeconds);
        Assert.Equal(45, settings.NextVideoVote.RevoteDurationSeconds);
        Assert.Equal(300, settings.AutoVote.LeadTimeSeconds);
        Assert.Equal(60, settings.VoteSkip.DurationSeconds);
        Assert.Equal(75, settings.VoteSkip.RequiredPercent);
        Assert.Equal(30, settings.VoteSkip.CooldownMinutes);
        Assert.Equal(60, settings.ChatterAverage.WindowMinutes);
        Assert.Equal(5, settings.ChatterAverage.BucketMinutes);
    }

    [Theory]
    [InlineData("voteSkip", "cooldownMinutes", 0, 1)]
    [InlineData("voteSkip", "cooldownMinutes", 45, 45)]
    [InlineData("voteSkip", "cooldownMinutes", 99999, 1440)]
    [InlineData("voteSkip", "requiredPercent", 0, 1)]
    [InlineData("voteSkip", "requiredPercent", 50, 50)]
    [InlineData("voteSkip", "requiredPercent", 150, 100)]
    [InlineData("voteSkip", "durationSeconds", 1, 15)]
    [InlineData("voteSkip", "durationSeconds", 90, 90)]
    [InlineData("voteSkip", "durationSeconds", 99999, 600)]
    [InlineData("chatterAverage", "windowMinutes", 1, 5)]
    [InlineData("chatterAverage", "windowMinutes", 30, 30)]
    [InlineData("chatterAverage", "windowMinutes", 99999, 1440)]
    [InlineData("nextVideoVote", "durationSeconds", 1, 15)]
    [InlineData("nextVideoVote", "durationSeconds", 120, 120)]
    [InlineData("nextVideoVote", "durationSeconds", 99999, 1800)]
    [InlineData("nextVideoVote", "revoteDurationSeconds", 1, 15)]
    [InlineData("nextVideoVote", "revoteDurationSeconds", 30, 30)]
    [InlineData("nextVideoVote", "revoteDurationSeconds", 99999, 600)]
    [InlineData("autoVote", "leadTimeSeconds", 1, 60)]
    [InlineData("autoVote", "leadTimeSeconds", 420, 420)]
    [InlineData("autoVote", "leadTimeSeconds", 99999, 3600)]
    [InlineData("commandCooldownSeconds", "voteInfo", 5, 30)]
    [InlineData("commandCooldownSeconds", "voteInfo", 45, 45)]
    [InlineData("commandCooldownSeconds", "voteInfo", 99999, 3600)]
    [InlineData("commandCooldownSeconds", "current", -5, 0)]
    [InlineData("commandCooldownSeconds", "current", 0, 0)]
    [InlineData("commandCooldownSeconds", "current", 99999, 3600)]
    [InlineData("commandCooldownSeconds", "playbackControl", 99999, 3600)]
    public void Load_ClampsTheNumbers(string section, string key, int configured, int expected)
    {
        var settings = LoadV2($"\"{section}\": {{ \"{key}\": {configured} }}");
        var value = (section, key) switch
        {
            ("voteSkip", "cooldownMinutes") => settings.VoteSkip.CooldownMinutes,
            ("voteSkip", "requiredPercent") => settings.VoteSkip.RequiredPercent,
            ("voteSkip", "durationSeconds") => settings.VoteSkip.DurationSeconds,
            ("chatterAverage", "windowMinutes") => settings.ChatterAverage.WindowMinutes,
            ("nextVideoVote", "revoteDurationSeconds") => settings.NextVideoVote.RevoteDurationSeconds,
            ("nextVideoVote", _) => settings.NextVideoVote.DurationSeconds,
            ("autoVote", _) => settings.AutoVote.LeadTimeSeconds,
            (_, "voteInfo") => settings.CommandCooldownSeconds.VoteInfo,
            (_, "current") => settings.CommandCooldownSeconds.Current,
            _ => settings.CommandCooldownSeconds.PlaybackControl
        };

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(60, 0, 1)]
    [InlineData(60, 10, 10)]
    [InlineData(60, 999, 60)]   // never more than an hour
    [InlineData(20, 30, 20)]    // never longer than the window
    public void Load_ClampsTheBucketLength(int window, int bucket, int expected)
    {
        var settings = LoadV2($"\"chatterAverage\": {{ \"windowMinutes\": {window}, \"bucketMinutes\": {bucket} }}");

        Assert.Equal(expected, settings.ChatterAverage.BucketMinutes);
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
            Channel = channel,
            SignIn = { ClientId = clientId, BotLogin = bot, OAuthToken = token }
        };

        Assert.Equal(complete, settings.IsComplete);
        Assert.Equal(appSignIn, settings.UsesAppSignIn);
    }

    [Fact]
    public void ExampleSettingsFile_IsValidAndComplete_AndCurrent()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "YoutubeVideoPlayer", "twitchsettings.example.json");
        using var dir = new TempDirectory();
        var path = dir.File(TwitchSettings.FileName);
        File.Copy(example, path);
        var before = File.ReadAllBytes(path);

        var settings = TwitchSettings.Load(dir.Path)!;

        Assert.True(settings.IsComplete);
        Assert.True(settings.UsesAppSignIn);
        Assert.Equal(2, settings.PrivilegedUsers.Count);
        Assert.Equal(300, settings.AutoVote.LeadTimeSeconds);
        Assert.Equal(45, settings.NextVideoVote.RevoteDurationSeconds);
        Assert.Equal(before, File.ReadAllBytes(path)); // already the current version: nothing to migrate
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    // ----- Migration of an older file on disk -----

    private const string Version1File = """
        {
          "enabled": true,
          "clientId": "cid",
          "botUsername": "LobosBot",
          "oAuthToken": "",
          "channel": "lobosjr",
          "otherCommandsCooldownSeconds": 7,
          "currentCooldownSeconds": 11,
          "skipCooldownSeconds": 4,
          "shuffleCooldownSeconds": 6,
          "skipVoteDurationSeconds": 90,
          "autoVoteLeadTimeSeconds": 240,
          "voteSkipDurationSeconds": 45,
          "voteSkipPercent": 60,
          "voteSkipCooldownMinutes": 20,
          "chatterAverageWindowMinutes": 30,
          "chatterAverageBucketMinutes": 3,
          "voteInfoCooldownSeconds": 40,
          "privilegedUsers": [ "carsten" ]
        }
        """;

    [Fact]
    public void Load_MigratesAVersion1File_KeepsTheOriginal_AndWritesTheNewLayout()
    {
        using var dir = new TempDirectory();
        var path = dir.File(TwitchSettings.FileName);
        File.WriteAllText(path, Version1File);
        var original = File.ReadAllBytes(path);

        var settings = TwitchSettings.Load(dir.Path)!;

        Assert.Equal(original, File.ReadAllBytes(dir.File("twitchsettings.v1.json.bak")));
        var written = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
        Assert.Equal(2, (int)written["version"]!);
        Assert.Equal("LobosBot", (string)written["signIn"]!["botLogin"]!); // values are copied as they were
        Assert.Null(written["botUsername"]);
        AssertMigratedValues(settings);

        // The new file loads to the same settings, without another migration.
        var again = TwitchSettings.Load(dir.Path)!;
        AssertMigratedValues(again);
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }

    /// <summary>Every v1 value of <see cref="Version1File"/> where it belongs now; each command keeps the cooldown it had.</summary>
    private static void AssertMigratedValues(TwitchSettings settings)
    {
        Assert.Equal("cid", settings.SignIn.ClientId);
        Assert.Equal("lobosbot", settings.SignIn.BotLogin);
        Assert.Equal("lobosjr", settings.Channel);
        Assert.Equal(new[] { "carsten" }, settings.PrivilegedUsers);

        var c = settings.CommandCooldownSeconds;
        Assert.Equal(4, c.PlaybackControl);  // skipCooldownSeconds
        Assert.Equal(11, c.Current);         // currentCooldownSeconds
        Assert.Equal(11, c.Playlist);
        Assert.Equal(11, c.VoteSkip);
        Assert.Equal(40, c.VoteInfo);
        Assert.Equal(6, c.ShuffleStatus);    // shuffleCooldownSeconds
        Assert.Equal(6, c.ToggleShuffle);
        Assert.Equal(6, c.PlaylistShuffleStatus);
        Assert.Equal(6, c.TogglePlaylistShuffle);
        Assert.Equal(7, c.AutoVoteStatus);   // otherCommandsCooldownSeconds
        Assert.Equal(7, c.ToggleAutoVote);
        Assert.Equal(7, c.Token);
        Assert.Equal(7, c.VodCommands);
        Assert.Equal(7, c.ModVodCommands);
        Assert.Equal(7, c.CommandHelp);

        Assert.Equal(90, settings.NextVideoVote.DurationSeconds);
        Assert.Equal(45, settings.NextVideoVote.RevoteDurationSeconds); // new in version 2: no old key, the default
        Assert.Equal(240, settings.AutoVote.LeadTimeSeconds);
        Assert.Equal(45, settings.VoteSkip.DurationSeconds);
        Assert.Equal(60, settings.VoteSkip.RequiredPercent);
        Assert.Equal(20, settings.VoteSkip.CooldownMinutes);
        Assert.Equal(30, settings.ChatterAverage.WindowMinutes);
        Assert.Equal(3, settings.ChatterAverage.BucketMinutes);
    }

    [Fact]
    public void Load_NeverReplacesAnEarlierBackup()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("twitchsettings.v1.json.bak"), "earlier");
        File.WriteAllText(dir.File(TwitchSettings.FileName), Version1File);

        TwitchSettings.Load(dir.Path);

        Assert.Equal("earlier", File.ReadAllText(dir.File("twitchsettings.v1.json.bak")));
        var second = Assert.Single(Directory.GetFiles(dir.Path, "twitchsettings.v1-*.json.bak"));
        Assert.Equal(Version1File, File.ReadAllText(second));
    }

    [Fact]
    public void Load_OfAFileThatCannotBeWritten_UsesTheMigratedSettings_AndLeavesTheFile()
    {
        using var dir = new TempDirectory();
        var path = dir.File(TwitchSettings.FileName);
        File.WriteAllText(path, Version1File);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var settings = TwitchSettings.Load(dir.Path)!;

            AssertMigratedValues(settings);
            Assert.Equal(Version1File, File.ReadAllText(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Load_OfAFileFromANewerVersion_ReadsIt_AndLeavesItAlone()
    {
        using var dir = new TempDirectory();
        var path = dir.File(TwitchSettings.FileName);
        const string json = """{ "version": 99, "channel": "lobosjr", "autoVote": { "leadTimeSeconds": 600 }, "future": true }""";
        File.WriteAllText(path, json);

        var settings = TwitchSettings.Load(dir.Path)!;

        Assert.Equal("lobosjr", settings.Channel);
        Assert.Equal(600, settings.AutoVote.LeadTimeSeconds);
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }
}
