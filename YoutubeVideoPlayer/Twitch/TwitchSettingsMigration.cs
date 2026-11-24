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
    /// A migration step only moves and renames what is there: values are copied unchanged (they are clamped when the
    /// settings are loaded), and keys it does not know are kept at the end. Afterwards <see cref="AddMissing"/> adds every
    /// setting the file still lacks with its default. To change the layout again: raise <see cref="CurrentVersion"/>, add
    /// a step to <see cref="Steps"/> and a test, and add the new settings to <see cref="TwitchSettings"/> and the example
    /// file.
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
        /// <c>!forcevoteskip</c> keeps the vote length of version 1 (<c>skipVoteDurationSeconds</c> →
        /// <c>forceVoteSkip.durationSeconds</c>). The Autovote and the
        /// <c>!voteskip</c> vote get new timings with version 2, whatever the old file said: the Autovote starts 400 s before
        /// the end and runs 300 s, a <c>!voteskip</c> vote runs 120 s and needs 65 % of the recent chatters, and the vote
        /// after a won <c>!voteskip</c> runs 120 s. These are written as fixed values (not the current defaults), so the
        /// step stays the same when a default changes later. Settings that are new in version 2 and not set here
        /// (<c>revote.durationSeconds</c>) are added with their defaults by <see cref="AddMissing"/>.
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
            Put(result, "vodChannel", Take("channel")); // the bot's own channel; mainChannel is new (empty)

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

            var forceVoteSkip = new JObject();
            Put(forceVoteSkip, "durationSeconds", Take("skipVoteDurationSeconds"));
            PutSection(result, "forceVoteSkip", forceVoteSkip);

            // New timings in version 2: the old values are dropped (taken, so they are not kept as unknown keys).
            Take("autoVoteLeadTimeSeconds");
            Take("voteSkipDurationSeconds");
            Take("voteSkipPercent");

            var autoVote = new JObject
            {
                ["leadTimeSeconds"] = 400,
                ["durationSeconds"] = 300
            };
            result["autoVote"] = autoVote;

            var voteSkip = new JObject
            {
                ["durationSeconds"] = 120,
                ["requiredPercent"] = 65
            };
            Put(voteSkip, "cooldownMinutes", Take("voteSkipCooldownMinutes"));
            voteSkip["nextVideoVoteDurationSeconds"] = 120;
            result["voteSkip"] = voteSkip;

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

        /// <summary>
        /// Keys of version 2 that were renamed before version 2 was released (a file written by a development build): the
        /// value moves to the new name, unless that is there already. Returns the file unchanged if there is nothing to do.
        /// </summary>
        public static JObject RenamePreReleaseKeys(JObject settings)
        {
            var renames = new[] { ("channel", "vodChannel") };
            var result = settings;
            foreach (var (from, to) in renames)
            {
                var old = result.Property(from, StringComparison.OrdinalIgnoreCase);
                if (old == null || result.Property(to, StringComparison.OrdinalIgnoreCase) != null)
                    continue;

                if (ReferenceEquals(result, settings))
                    result = (JObject)settings.DeepClone(); // the caller compares with the original to decide on writing

                var property = result.Property(old.Name)!;
                property.AddAfterSelf(new JProperty(to, property.Value.DeepClone()));
                property.Remove();
            }

            return result;
        }

        /// <summary>
        /// The file with every setting of <paramref name="defaults"/> it does not have (or has as <c>null</c>) added with
        /// its default. The result has the order of the defaults (that of the example file), sections included; the file's
        /// values are kept as they are, keys are found whatever their case, and keys the defaults do not know (notes, typos)
        /// are kept after the known ones.
        /// </summary>
        /// <param name="added">Receives the settings that were added, as <c>section.key</c>.</param>
        public static JObject AddMissing(JObject settings, JObject defaults, ICollection<string>? added = null) =>
            AddMissing(settings, defaults, added, prefix: string.Empty);

        private static JObject AddMissing(JObject settings, JObject defaults, ICollection<string>? added, string prefix)
        {
            var result = new JObject();
            foreach (var setting in defaults.Properties())
            {
                var own = settings.Property(setting.Name, StringComparison.OrdinalIgnoreCase)?.Value;
                if (own is null or { Type: JTokenType.Null })
                {
                    result[setting.Name] = setting.Value.DeepClone();
                    added?.Add(prefix + setting.Name);
                }
                else if (setting.Value is JObject section && own is JObject ownSection)
                {
                    result[setting.Name] = AddMissing(ownSection, section, added, prefix + setting.Name + ".");
                }
                else
                {
                    result[setting.Name] = own.DeepClone();
                }
            }

            foreach (var property in settings.Properties())
            {
                if (defaults.Property(property.Name, StringComparison.OrdinalIgnoreCase) == null)
                    result[property.Name] = property.Value.DeepClone();
            }

            return result;
        }
    }
}
