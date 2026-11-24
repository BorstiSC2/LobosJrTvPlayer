using Newtonsoft.Json.Linq;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TwitchSettingsMigrationTests
{
    private static JObject Migrate(string json, out int from)
    {
        var (result, fromVersion) = TwitchSettingsMigration.Migrate(JObject.Parse(json));
        from = fromVersion;
        return result;
    }

    private static void AssertJson(string expected, JObject actual) =>
        Assert.True(JToken.DeepEquals(JObject.Parse(expected), actual), actual.ToString());

    [Fact]
    public void AFullVersion1File_BecomesTheVersion2Layout()
    {
        var result = Migrate("""
            {
              "enabled": false,
              "clientId": "cid",
              "botUsername": "bot",
              "oAuthToken": "oauth:x",
              "channel": "chan",
              "otherCommandsCooldownSeconds": 1,
              "currentCooldownSeconds": 2,
              "skipCooldownSeconds": 3,
              "shuffleCooldownSeconds": 4,
              "skipVoteDurationSeconds": 5,
              "autoVoteLeadTimeSeconds": 6,
              "voteSkipDurationSeconds": 7,
              "voteSkipPercent": 8,
              "voteSkipCooldownMinutes": 9,
              "chatterAverageWindowMinutes": 10,
              "chatterAverageBucketMinutes": 11,
              "voteInfoCooldownSeconds": 12,
              "privilegedUsers": [ "a", "b" ]
            }
            """, out var from);

        Assert.Equal(1, from);
        AssertJson("""
            {
              "version": 2,
              "enabled": false,
              "channel": "chan",
              "signIn": { "clientId": "cid", "botLogin": "bot", "oauthToken": "oauth:x" },
              "privilegedUsers": [ "a", "b" ],
              "commandCooldownSeconds": {
                "playbackControl": 3,
                "current": 2, "playlist": 2, "voteSkip": 2,
                "voteInfo": 12,
                "shuffleStatus": 4, "toggleShuffle": 4, "playlistShuffleStatus": 4, "togglePlaylistShuffle": 4,
                "autoVoteStatus": 1, "toggleAutoVote": 1, "token": 1, "vodCommands": 1, "modVodCommands": 1, "commandHelp": 1
              },
              "nextVideoVote": { "durationSeconds": 5 },
              "autoVote": { "leadTimeSeconds": 6 },
              "voteSkip": { "durationSeconds": 7, "requiredPercent": 8, "cooldownMinutes": 9 },
              "chatterAverage": { "windowMinutes": 10, "bucketMinutes": 11 }
            }
            """, result);

        // In the documented order, so the written file reads like the example.
        Assert.Equal(
            new[] { "version", "enabled", "channel", "signIn", "privilegedUsers", "commandCooldownSeconds", "nextVideoVote", "autoVote", "voteSkip", "chatterAverage" },
            result.Properties().Select(p => p.Name));
    }

    [Fact]
    public void OnlyThePresentKeys_AreMoved_SoTheDefaultsStillApply()
    {
        var result = Migrate("""{ "channel": "chan", "currentCooldownSeconds": 9, "voteSkipPercent": 50 }""", out _);

        AssertJson("""
            {
              "version": 2,
              "channel": "chan",
              "commandCooldownSeconds": { "current": 9, "playlist": 9, "voteSkip": 9 },
              "voteSkip": { "requiredPercent": 50 }
            }
            """, result);
    }

    [Fact]
    public void OldKeys_AreFound_WhateverTheirCase()
    {
        var result = Migrate("""{ "OAuthToken": "oauth:x", "BotUsername": "bot" }""", out _);

        Assert.Equal("oauth:x", (string)result["signIn"]!["oauthToken"]!);
        Assert.Equal("bot", (string)result["signIn"]!["botLogin"]!);
        Assert.Null(result["OAuthToken"]);
    }

    [Fact]
    public void UnknownKeys_AreKept_AtTheEnd()
    {
        var result = Migrate("""{ "_comment": "my bot", "channel": "chan", "timestampCooldownSeconds": 5 }""", out _);

        Assert.Equal(new[] { "version", "channel", "_comment", "timestampCooldownSeconds" }, result.Properties().Select(p => p.Name));
        Assert.Equal("my bot", (string)result["_comment"]!);
    }

    [Fact]
    public void AnUnknownKey_NeverReplacesAMigratedOne()
    {
        var result = Migrate("""{ "clientId": "cid", "signIn": "something old" }""", out _);

        Assert.Equal("cid", (string)result["signIn"]!["clientId"]!);
    }

    [Theory]
    [InlineData("""{ "version": 2, "channel": "chan" }""", 2)]
    [InlineData("""{ "version": 7, "channel": "chan" }""", 7)]
    public void ACurrentOrNewerFile_IsLeftAsItIs(string json, int version)
    {
        var original = JObject.Parse(json);

        var (result, from) = TwitchSettingsMigration.Migrate(original);

        Assert.Equal(version, from);
        Assert.True(JToken.DeepEquals(JObject.Parse(json), result));
    }

    [Theory]
    [InlineData("""{ }""", 1)]
    [InlineData("""{ "version": "two" }""", 1)]
    [InlineData("""{ "version": 1 }""", 1)]
    [InlineData("""{ "version": 2 }""", 2)]
    public void VersionOf_ReadsTheVersion_OneWithout(string json, int expected)
    {
        Assert.Equal(expected, TwitchSettingsMigration.VersionOf(JObject.Parse(json)));
    }

    [Fact]
    public void AMigratedFile_IsAtTheCurrentVersion()
    {
        var result = Migrate("{ }", out _);

        Assert.Equal(TwitchSettingsMigration.CurrentVersion, TwitchSettingsMigration.VersionOf(result));
    }
}
