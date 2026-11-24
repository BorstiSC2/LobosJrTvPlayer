using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Brings a <c>twitchsettings.json</c> written by an older version of the app to the current layout, one version at
    /// a time. A file without <c>"version"</c> is version 1 (the flat layout before versions existed).
    /// </summary>
    /// <remarks>
    /// A migration only moves and renames what is there: values are copied unchanged (they are clamped when the settings
    /// are loaded), keys that are missing stay missing (the defaults apply), and keys it does not know are kept at the end.
    /// To change the layout again: raise <see cref="CurrentVersion"/>, add a step to <see cref="Steps"/> and a test.
    /// </remarks>
    public static class TwitchSettingsMigration
    {
        public const int CurrentVersion = 2;

        private const string VersionKey = "version";

        /// <summary>Each step turns a file of version <c>From</c> into one of version <c>From + 1</c>.</summary>
        private static readonly (int From, Func<JObject, JObject> Apply)[] Steps =
        {
            (1, FromVersion1)
        };

        /// <summary>The version a file says it has; 1 without (or with an unreadable) <c>"version"</c>.</summary>
        public static int VersionOf(JObject settings) =>
            settings[VersionKey] is JValue { Type: JTokenType.Integer } value ? value.Value<int>() : 1;

        /// <summary>
        /// Applies every step from the file's version up to <see cref="CurrentVersion"/>. Returns the file unchanged if it
        /// is current already, or newer (written by a later version of the app, which this one cannot migrate back).
        /// </summary>
        /// <returns>The migrated settings and the version the file had.</returns>
        public static (JObject Result, int FromVersion) Migrate(JObject settings)
        {
            var from = VersionOf(settings);
            var result = settings;
            foreach (var step in Steps.Where(s => s.From >= from).OrderBy(s => s.From))
                result = step.Apply(result);

            return (result, from);
        }

        /// <summary>
        /// Version 1 → 2: the sign-in in its own section, one cooldown per command (the old shared values are copied to
        /// each command that used them; the mod commands that change what plays share one), and a section per vote.
        /// Settings that are new in version 2 (<c>nextVideoVote.revoteDurationSeconds</c>) have no older key: they stay
        /// missing, so their defaults apply.
        /// </summary>
        private static JObject FromVersion1(JObject old)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VersionKey };

            JToken? Take(string key)
            {
                used.Add(key);
                var property = old.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                return property?.Value.DeepClone();
            }

            static void Put(JObject target, string key, JToken? value)
            {
                if (value != null)
                    target[key] = value;
            }

            static void PutSection(JObject target, string key, JObject section)
            {
                if (section.HasValues)
                    target[key] = section;
            }

            var result = new JObject { [VersionKey] = 2 };
            Put(result, "enabled", Take("enabled"));
            Put(result, "channel", Take("channel"));

            var signIn = new JObject();
            Put(signIn, "clientId", Take("clientId"));
            Put(signIn, "botLogin", Take("botUsername"));
            Put(signIn, "oauthToken", Take("oAuthToken"));
            PutSection(result, "signIn", signIn);

            Put(result, "privilegedUsers", Take("privilegedUsers"));

            var cooldowns = new JObject();
            void Spread(string oldKey, params string[] commands)
            {
                var value = Take(oldKey);
                foreach (var command in commands)
                    Put(cooldowns, command, value?.DeepClone());
            }

            Spread("skipCooldownSeconds", "playbackControl");
            Spread("currentCooldownSeconds", "current", "playlist", "voteSkip");
            Spread("voteInfoCooldownSeconds", "voteInfo");
            Spread("shuffleCooldownSeconds", "shuffleStatus", "toggleShuffle", "playlistShuffleStatus", "togglePlaylistShuffle");
            Spread("otherCommandsCooldownSeconds",
                "autoVoteStatus", "toggleAutoVote", "token", "vodCommands", "modVodCommands", "commandHelp");
            PutSection(result, "commandCooldownSeconds", cooldowns);

            var nextVideoVote = new JObject();
            Put(nextVideoVote, "durationSeconds", Take("skipVoteDurationSeconds"));
            PutSection(result, "nextVideoVote", nextVideoVote);

            var autoVote = new JObject();
            Put(autoVote, "leadTimeSeconds", Take("autoVoteLeadTimeSeconds"));
            PutSection(result, "autoVote", autoVote);

            var voteSkip = new JObject();
            Put(voteSkip, "durationSeconds", Take("voteSkipDurationSeconds"));
            Put(voteSkip, "requiredPercent", Take("voteSkipPercent"));
            Put(voteSkip, "cooldownMinutes", Take("voteSkipCooldownMinutes"));
            PutSection(result, "voteSkip", voteSkip);

            var chatterAverage = new JObject();
            Put(chatterAverage, "windowMinutes", Take("chatterAverageWindowMinutes"));
            Put(chatterAverage, "bucketMinutes", Take("chatterAverageBucketMinutes"));
            PutSection(result, "chatterAverage", chatterAverage);

            // Whatever this step does not know (notes, typos, keys of a later version) stays, after the known ones.
            foreach (var property in old.Properties().Where(p => !used.Contains(p.Name)))
            {
                if (result.Property(property.Name, StringComparison.OrdinalIgnoreCase) == null)
                    result[property.Name] = property.Value.DeepClone();
            }

            return result;
        }
    }
}
