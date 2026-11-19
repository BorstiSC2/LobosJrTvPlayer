using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>Bot credentials and options, read from <c>twitchsettings.json</c> next to the executable.</summary>
    public sealed class TwitchSettings
    {
        public const string FileName = "twitchsettings.json";

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Client ID of the application registered in the Twitch developer console (client type "Public").
        /// If set, the bot signs in through the app and its token is renewed automatically.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Login name of the bot account. Required for a manual <see cref="OAuthToken"/>; with
        /// <see cref="ClientId"/> it is optional and only used to warn about signing in with another account.
        /// </summary>
        public string BotUsername { get; set; } = string.Empty;

        /// <summary>
        /// Manually created chat token (scopes <c>chat:read</c>, <c>chat:edit</c>). Only used when no
        /// <see cref="ClientId"/> is set; it is not renewed automatically.
        /// </summary>
        public string OAuthToken { get; set; } = string.Empty;

        /// <summary>The channel to join, without the leading <c>#</c>.</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>
        /// Minimum time between two replies of each command that has no cooldown setting of its own
        /// (<c>!autovotestatus</c>, <c>!toggleautovote</c>, <c>!token</c>, <c>!vodcommands</c>, <c>!modvodcommands</c>,
        /// <c>!commandhelp</c>), to stay below Twitch's rate limits. Formerly <c>timestampCooldownSeconds</c>.
        /// </summary>
        public int OtherCommandsCooldownSeconds { get; set; } = 5;

        /// <summary>Minimum time between two <c>!current</c> replies, to stay below Twitch's rate limits.</summary>
        public int CurrentCooldownSeconds { get; set; } = 5;

        /// <summary>Minimum time between two <c>!forceskip</c> commands, so two mods cannot skip two videos at once.</summary>
        public int SkipCooldownSeconds { get; set; } = 3;

        /// <summary>Minimum time between two <c>!shufflestatus</c> / <c>!toggleshuffle</c> commands (each has its own).</summary>
        public int ShuffleCooldownSeconds { get; set; } = 3;

        /// <summary>
        /// How long a <c>!forcevoteskip</c> vote runs (counted from the moment the last option has been posted)
        /// before the winner is played.
        /// </summary>
        public int SkipVoteDurationSeconds { get; set; } = 120;

        /// <summary>How long before the end of a video the Autovote starts its vote about the next one.</summary>
        public int AutoVoteLeadTimeSeconds { get; set; } = 300;

        /// <summary>How long nobody can use <c>!voteskip</c> after a vote in which the chat chose to stay.</summary>
        public int VoteSkipCooldownMinutes { get; set; } = 30;

        /// <summary>
        /// The share of the average unique chatters (in percent) that has to type <c>!skip</c> for a <c>!voteskip</c>
        /// vote to succeed (besides more <c>!skip</c> than <c>!stay</c>).
        /// </summary>
        public int VoteSkipPercent { get; set; } = 75;

        /// <summary>How long a <c>!voteskip</c> vote (<c>!skip</c> or <c>!stay</c>) runs.</summary>
        public int VoteSkipDurationSeconds { get; set; } = 60;

        /// <summary>How far back the rolling average of unique chatters reaches.</summary>
        public int ChatterAverageWindowMinutes { get; set; } = 60;

        /// <summary>The length of one bucket of the rolling average; each bucket counts the different chatters in it.</summary>
        public int ChatterAverageBucketMinutes { get; set; } = 5;

        /// <summary>Minimum time between two <c>!voteinfo</c> replies (at least 30 seconds).</summary>
        public int VoteInfoCooldownSeconds { get; set; } = 30;

        /// <summary>
        /// Login names or numeric user IDs that may use moderator-only commands even without being
        /// a moderator, e.g. your own account for testing.
        /// </summary>
        public List<string> PrivilegedUsers { get; set; } = new();

        /// <summary>The bot signs in through the app (device code flow) and renews its token automatically.</summary>
        public bool UsesAppSignIn => !string.IsNullOrWhiteSpace(ClientId);

        public bool IsComplete =>
            Enabled
            && !string.IsNullOrWhiteSpace(Channel)
            && (UsesAppSignIn
                || (!string.IsNullOrWhiteSpace(BotUsername) && !string.IsNullOrWhiteSpace(OAuthToken)));

        /// <summary>Loads the settings; returns <c>null</c> if the file is missing or cannot be read.</summary>
        /// <param name="baseDirectory">Folder containing the file; the application folder by default.</param>
        public static TwitchSettings? Load(string? baseDirectory = null)
        {
            var path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, FileName);
            try
            {
                var settings = JsonFile.Read<TwitchSettings>(path);
                if (settings == null)
                {
                    Logger.Log($"No {FileName} found; the Twitch bot is disabled.");
                    return null;
                }

                settings.Channel = settings.Channel.Trim().TrimStart('#').ToLowerInvariant();
                settings.BotUsername = settings.BotUsername.Trim().ToLowerInvariant();
                settings.OAuthToken = settings.OAuthToken.Trim();
                settings.ClientId = settings.ClientId.Trim();
                settings.PrivilegedUsers = (settings.PrivilegedUsers ?? new())
                    .Select(u => u.Trim().TrimStart('@').ToLowerInvariant())
                    .Where(u => u.Length > 0)
                    .ToList();
                settings.SkipVoteDurationSeconds = Math.Clamp(settings.SkipVoteDurationSeconds, 15, 1800);
                settings.AutoVoteLeadTimeSeconds = Math.Clamp(settings.AutoVoteLeadTimeSeconds, 60, 3600);
                settings.VoteSkipCooldownMinutes = Math.Clamp(settings.VoteSkipCooldownMinutes, 1, 1440);
                settings.VoteSkipPercent = Math.Clamp(settings.VoteSkipPercent, 1, 100);
                settings.VoteSkipDurationSeconds = Math.Clamp(settings.VoteSkipDurationSeconds, 15, 600);
                settings.ChatterAverageWindowMinutes = Math.Clamp(settings.ChatterAverageWindowMinutes, 5, 1440);
                settings.ChatterAverageBucketMinutes = Math.Clamp(
                    settings.ChatterAverageBucketMinutes, 1, Math.Min(60, settings.ChatterAverageWindowMinutes));
                settings.VoteInfoCooldownSeconds = Math.Clamp(settings.VoteInfoCooldownSeconds, 30, 3600);
                return settings;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read {FileName}; the Twitch bot is disabled.", ex);
                return null;
            }
        }
    }
}
