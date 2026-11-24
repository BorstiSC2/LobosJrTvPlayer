using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Bot credentials and options, read from <c>twitchsettings.json</c> next to the executable. A file of an older layout
    /// is migrated (<see cref="TwitchSettingsMigration"/>) and written back; the original is kept as a backup.
    /// </summary>
    public sealed class TwitchSettings
    {
        public const string FileName = "twitchsettings.json";

        /// <summary>The layout of the file (<see cref="TwitchSettingsMigration.CurrentVersion"/> once loaded).</summary>
        public int Version { get; set; } = TwitchSettingsMigration.CurrentVersion;

        public bool Enabled { get; set; } = true;

        /// <summary>The channel to join, without the leading <c>#</c>.</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>How the bot signs in.</summary>
        public SignInSettings SignIn { get; set; } = new();

        /// <summary>
        /// Login names or numeric user IDs that may use moderator-only commands even without being
        /// a moderator, e.g. your own account for testing.
        /// </summary>
        public List<string> PrivilegedUsers { get; set; } = new();

        /// <summary>The minimum time between two uses of each command, to stay below Twitch's rate limits.</summary>
        public CommandCooldownSettings CommandCooldownSeconds { get; set; } = new();

        /// <summary>The vote about the next video (<c>!forcevoteskip</c>, the Autovote, a successful <c>!voteskip</c>).</summary>
        public NextVideoVoteSettings NextVideoVote { get; set; } = new();

        public AutoVoteSettings AutoVote { get; set; } = new();

        /// <summary>The viewers' vote to skip the current video (<c>!voteskip</c>, ballots <c>!skip</c> / <c>!stay</c>).</summary>
        public VoteSkipSettings VoteSkip { get; set; } = new();

        /// <summary>The rolling average of unique chatters that <c>!voteskip</c> measures against.</summary>
        public ChatterAverageSettings ChatterAverage { get; set; } = new();

        /// <summary>The bot signs in through the app (device code flow) and renews its token automatically.</summary>
        public bool UsesAppSignIn => !string.IsNullOrWhiteSpace(SignIn.ClientId);

        public bool IsComplete =>
            Enabled
            && !string.IsNullOrWhiteSpace(Channel)
            && (UsesAppSignIn
                || (!string.IsNullOrWhiteSpace(SignIn.BotLogin) && !string.IsNullOrWhiteSpace(SignIn.OAuthToken)));

        public sealed class SignInSettings
        {
            /// <summary>
            /// Client ID of the application registered in the Twitch developer console (client type "Public").
            /// If set, the bot signs in through the app and its token is renewed automatically.
            /// </summary>
            public string ClientId { get; set; } = string.Empty;

            /// <summary>
            /// Login name of the bot account. Required for a manual <see cref="OAuthToken"/>; with
            /// <see cref="ClientId"/> it is optional and only used to warn about signing in with another account.
            /// </summary>
            public string BotLogin { get; set; } = string.Empty;

            /// <summary>
            /// Manually created chat token (scopes <c>chat:read</c>, <c>chat:edit</c>). Only used when no
            /// <see cref="ClientId"/> is set; it is not renewed automatically.
            /// </summary>
            public string OAuthToken { get; set; } = string.Empty;
        }

        /// <summary>
        /// Seconds per command (0 turns a cooldown off). Each command has its own, except the moderator commands that change
        /// what plays, which share one timer: a second one right after the first would stack up (two skips at once).
        /// <c>!forcevoteskip</c>, <c>!cancelvote</c> and the ballots have none (a running vote blocks them anyway).
        /// </summary>
        public sealed class CommandCooldownSettings
        {
            /// <summary><c>!forceskip</c>, <c>!skipplaylist</c>, <c>!reload</c>, <c>!play</c> and <c>!pause</c> together.</summary>
            public int PlaybackControl { get; set; } = 3;

            public int Current { get; set; } = 5;

            public int Playlist { get; set; } = 5;

            /// <summary>Only throttles the replies; the vote has a cooldown of its own (<see cref="VoteSkipSettings.CooldownMinutes"/>).</summary>
            public int VoteSkip { get; set; } = 5;

            /// <summary>At least 30 seconds.</summary>
            public int VoteInfo { get; set; } = 30;

            public int ShuffleStatus { get; set; } = 3;

            public int ToggleShuffle { get; set; } = 3;

            public int PlaylistShuffleStatus { get; set; } = 3;

            public int TogglePlaylistShuffle { get; set; } = 3;

            public int AutoVoteStatus { get; set; } = 5;

            public int ToggleAutoVote { get; set; } = 5;

            public int Token { get; set; } = 5;

            public int VodCommands { get; set; } = 5;

            public int ModVodCommands { get; set; } = 5;

            public int CommandHelp { get; set; } = 5;

            internal void Clamp()
            {
                static int Seconds(int value) => Math.Clamp(value, 0, 3600);

                PlaybackControl = Seconds(PlaybackControl);
                Current = Seconds(Current);
                Playlist = Seconds(Playlist);
                VoteSkip = Seconds(VoteSkip);
                VoteInfo = Math.Clamp(VoteInfo, 30, 3600);
                ShuffleStatus = Seconds(ShuffleStatus);
                ToggleShuffle = Seconds(ToggleShuffle);
                PlaylistShuffleStatus = Seconds(PlaylistShuffleStatus);
                TogglePlaylistShuffle = Seconds(TogglePlaylistShuffle);
                AutoVoteStatus = Seconds(AutoVoteStatus);
                ToggleAutoVote = Seconds(ToggleAutoVote);
                Token = Seconds(Token);
                VodCommands = Seconds(VodCommands);
                ModVodCommands = Seconds(ModVodCommands);
                CommandHelp = Seconds(CommandHelp);
            }
        }

        public sealed class NextVideoVoteSettings
        {
            /// <summary>How long the vote runs (15 to 1800), counted from the moment the last option has been posted.</summary>
            public int DurationSeconds { get; set; } = 120;
        }

        public sealed class AutoVoteSettings
        {
            /// <summary>How long before the end of a video the Autovote starts its vote about the next one (60 to 3600).</summary>
            public int LeadTimeSeconds { get; set; } = 300;
        }

        public sealed class VoteSkipSettings
        {
            /// <summary>How long a <c>!voteskip</c> vote runs (15 to 600).</summary>
            public int DurationSeconds { get; set; } = 60;

            /// <summary>
            /// The share of the average unique chatters (in percent, 1 to 100) that has to type <c>!skip</c> for the vote
            /// to succeed (besides more <c>!skip</c> than <c>!stay</c>).
            /// </summary>
            public int RequiredPercent { get; set; } = 75;

            /// <summary>How long nobody can use <c>!voteskip</c> after a vote in which the chat chose to stay (1 to 1440).</summary>
            public int CooldownMinutes { get; set; } = 30;
        }

        public sealed class ChatterAverageSettings
        {
            /// <summary>How far back the average reaches (5 to 1440).</summary>
            public int WindowMinutes { get; set; } = 60;

            /// <summary>The length of one bucket, in which the different chatters are counted (1 to 60, at most the window).</summary>
            public int BucketMinutes { get; set; } = 5;
        }

        /// <summary>
        /// Loads the settings; returns <c>null</c> if the file is missing or cannot be read. A file of an older layout is
        /// migrated and written back (the original is kept as <c>twitchsettings.v&lt;version&gt;.json.bak</c>); if that
        /// fails, the migrated settings are used anyway and the migration runs again on the next start.
        /// </summary>
        /// <param name="baseDirectory">Folder containing the file; the application folder by default.</param>
        public static TwitchSettings? Load(string? baseDirectory = null)
        {
            var path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, FileName);
            try
            {
                var text = JsonFile.ReadText(path);
                if (text == null)
                {
                    Logger.Log($"No {FileName} found; the Twitch bot is disabled.");
                    return null;
                }

                if (JToken.Parse(text) is not JObject json)
                {
                    Logger.Log($"{FileName} does not contain a JSON object; the Twitch bot is disabled.");
                    return null;
                }

                var (migrated, fromVersion) = TwitchSettingsMigration.Migrate(json);
                if (fromVersion > TwitchSettingsMigration.CurrentVersion)
                {
                    Logger.Log($"{FileName} has version {fromVersion}, written by a newer version of the app; " +
                               $"it is read as version {TwitchSettingsMigration.CurrentVersion} and not changed.");
                }
                else if (fromVersion < TwitchSettingsMigration.CurrentVersion)
                {
                    SaveMigrated(path, migrated, fromVersion);
                }

                var settings = migrated.ToObject<TwitchSettings>(JsonSerializer.CreateDefault()) ?? new TwitchSettings();
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read {FileName}; the Twitch bot is disabled.", ex);
                return null;
            }
        }

        /// <summary>Keeps the original next to it and writes the migrated file; a failure is only logged.</summary>
        private static void SaveMigrated(string path, JObject migrated, int fromVersion)
        {
            var target = $"version {TwitchSettingsMigration.CurrentVersion}";
            try
            {
                var backup = BackupPath(path, fromVersion);
                File.Copy(path, backup); // the original bytes
                JsonFile.WriteAtomic(path, migrated);
                Logger.Log($"Migrated {FileName} from version {fromVersion} to {target}; the old file is kept as {Path.GetFileName(backup)}.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not write {FileName} migrated to {target}; the migrated settings are used for now, " +
                           "and the migration is tried again on the next start.", ex);
                try
                {
                    File.Delete(path + ".tmp"); // what WriteAtomic may have left behind
                }
                catch (Exception)
                {
                    // nothing more to do
                }
            }
        }

        /// <summary><c>twitchsettings.v1.json.bak</c>, or with a time if that exists already (an earlier backup is never replaced).</summary>
        private static string BackupPath(string path, int fromVersion)
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var backup = Path.Combine(directory, $"{name}.v{fromVersion}.json.bak");
            return File.Exists(backup)
                ? Path.Combine(directory, $"{name}.v{fromVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json.bak")
                : backup;
        }

        /// <summary>Trims the names, fills sections that the file set to <c>null</c>, and keeps every number in its range.</summary>
        private void Normalize()
        {
            SignIn ??= new();
            CommandCooldownSeconds ??= new();
            NextVideoVote ??= new();
            AutoVote ??= new();
            VoteSkip ??= new();
            ChatterAverage ??= new();

            Version = TwitchSettingsMigration.CurrentVersion;
            Channel = (Channel ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();
            SignIn.BotLogin = (SignIn.BotLogin ?? string.Empty).Trim().ToLowerInvariant();
            SignIn.OAuthToken = (SignIn.OAuthToken ?? string.Empty).Trim();
            SignIn.ClientId = (SignIn.ClientId ?? string.Empty).Trim();
            PrivilegedUsers = (PrivilegedUsers ?? new())
                .Where(u => u != null)
                .Select(u => u.Trim().TrimStart('@').ToLowerInvariant())
                .Where(u => u.Length > 0)
                .ToList();

            CommandCooldownSeconds.Clamp();
            NextVideoVote.DurationSeconds = Math.Clamp(NextVideoVote.DurationSeconds, 15, 1800);
            AutoVote.LeadTimeSeconds = Math.Clamp(AutoVote.LeadTimeSeconds, 60, 3600);
            VoteSkip.DurationSeconds = Math.Clamp(VoteSkip.DurationSeconds, 15, 600);
            VoteSkip.RequiredPercent = Math.Clamp(VoteSkip.RequiredPercent, 1, 100);
            VoteSkip.CooldownMinutes = Math.Clamp(VoteSkip.CooldownMinutes, 1, 1440);
            ChatterAverage.WindowMinutes = Math.Clamp(ChatterAverage.WindowMinutes, 5, 1440);
            ChatterAverage.BucketMinutes = Math.Clamp(ChatterAverage.BucketMinutes, 1, Math.Min(60, ChatterAverage.WindowMinutes));
        }
    }
}
