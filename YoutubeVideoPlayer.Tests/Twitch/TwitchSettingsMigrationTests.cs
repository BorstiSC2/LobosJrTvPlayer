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
              "forceVoteSkip": { "durationSeconds": 5 },
              "autoVote": { "leadTimeSeconds": 400, "durationSeconds": 300 },
              "voteSkip": { "durationSeconds": 120, "requiredPercent": 65, "cooldownMinutes": 9, "nextVideoVoteDurationSeconds": 120 },
              "chatterAverage": { "windowMinutes": 10, "bucketMinutes": 11 }
            }
            """, result);

        // In the documented order, so the written file reads like the example.
        Assert.Equal(
            new[] { "version", "enabled", "channel", "signIn", "privilegedUsers", "commandCooldownSeconds", "forceVoteSkip", "autoVote", "voteSkip", "chatterAverage" },
            result.Properties().Select(p => p.Name));
    }

    [Fact]
    public void OnlyThePresentKeys_AreMoved_BesidesTheNewTimings()
    {
        var result = Migrate("""{ "channel": "chan", "currentCooldownSeconds": 9 }""", out _);

        AssertJson("""
            {
              "version": 2,
              "channel": "chan",
              "commandCooldownSeconds": { "current": 9, "playlist": 9, "voteSkip": 9 },
              "autoVote": { "leadTimeSeconds": 400, "durationSeconds": 300 },
              "voteSkip": { "durationSeconds": 120, "requiredPercent": 65, "nextVideoVoteDurationSeconds": 120 }
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

        Assert.Equal(new[] { "version", "channel", "autoVote", "voteSkip", "_comment", "timestampCooldownSeconds" },
            result.Properties().Select(p => p.Name));
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

    [Fact]
    public void TheAutovoteAndVoteSkipTimings_AreSetToTheNewValues_WhateverVersion1Said()
    {
        var result = Migrate("""
            {
              "skipVoteDurationSeconds": 90,
              "autoVoteLeadTimeSeconds": 240,
              "voteSkipDurationSeconds": 45,
              "voteSkipPercent": 60,
              "voteSkipCooldownMinutes": 20
            }
            """, out _);

        AssertJson("""
            {
              "version": 2,
              "forceVoteSkip": { "durationSeconds": 90 },
              "autoVote": { "leadTimeSeconds": 400, "durationSeconds": 300 },
              "voteSkip": { "durationSeconds": 120, "requiredPercent": 65, "cooldownMinutes": 20, "nextVideoVoteDurationSeconds": 120 }
            }
            """, result); // the old timings are gone, not kept as unknown keys; !forcevoteskip keeps its length
    }

    // ----- Adding the missing settings -----

    private static readonly JObject Defaults = JObject.Parse("""
        {
          "version": 2,
          "channel": "",
          "signIn": { "clientId": "", "botLogin": "" },
          "autoVote": { "leadTimeSeconds": 300, "durationSeconds": 120 }
        }
        """);

    [Fact]
    public void AddMissing_AddsEverySettingTheFileLacks_WithItsDefault_AndReportsThem()
    {
        var added = new List<string>();

        var result = TwitchSettingsMigration.AddMissing(
            JObject.Parse("""{ "version": 2, "signIn": { "botLogin": "bot" }, "autoVote": { "leadTimeSeconds": 600 } }"""),
            Defaults, added);

        AssertJson("""
            {
              "version": 2,
              "channel": "",
              "signIn": { "clientId": "", "botLogin": "bot" },
              "autoVote": { "leadTimeSeconds": 600, "durationSeconds": 120 }
            }
            """, result);
        Assert.Equal(new[] { "channel", "signIn.clientId", "autoVote.durationSeconds" }, added);
    }

    [Fact]
    public void AddMissing_PutsTheSettingsInTheOrderOfTheDefaults_AndKeepsUnknownKeysAtTheEnd()
    {
        var result = TwitchSettingsMigration.AddMissing(
            JObject.Parse("""{ "_note": "mine", "autoVote": { "durationSeconds": 60, "x": 1 }, "Channel": "chan", "version": 2 }"""),
            Defaults);

        Assert.Equal(new[] { "version", "channel", "signIn", "autoVote", "_note" }, result.Properties().Select(p => p.Name));
        Assert.Equal(new[] { "leadTimeSeconds", "durationSeconds", "x" }, ((JObject)result["autoVote"]!).Properties().Select(p => p.Name));
        Assert.Equal("chan", (string)result["channel"]!); // found whatever its case, written with the documented name
        Assert.Equal(60, (int)result["autoVote"]!["durationSeconds"]!);
    }

    [Fact]
    public void AddMissing_TreatsNullLikeMissing_AndKeepsEveryOtherValueAsItIs()
    {
        var result = TwitchSettingsMigration.AddMissing(
            JObject.Parse("""{ "version": 2, "channel": null, "signIn": null, "autoVote": { "leadTimeSeconds": 99999 } }"""),
            Defaults);

        Assert.Equal("", (string)result["channel"]!);
        Assert.Equal("", (string)result["signIn"]!["clientId"]!);
        Assert.Equal(99999, (int)result["autoVote"]!["leadTimeSeconds"]!); // not clamped here: that happens on load
    }

    [Fact]
    public void AddMissing_ChangesNothingInACompleteFile()
    {
        var complete = (JObject)Defaults.DeepClone();
        var added = new List<string>();

        var result = TwitchSettingsMigration.AddMissing(complete, Defaults, added);

        Assert.True(JToken.DeepEquals(complete, result));
        Assert.Empty(added);
    }
}
