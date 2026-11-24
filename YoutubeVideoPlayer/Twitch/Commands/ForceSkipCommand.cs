using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!forceskip</c> (moderators and broadcaster only): skips to the next video at once, without a vote.</summary>
    public sealed class ForceSkipCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public ForceSkipCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "forceskip";

        public string Description =>
            "Skips the current video right away and starts the next one, without a vote.";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        /// <summary>Shares one cooldown with the other commands that change what plays.</summary>
        public string CooldownGroup => ChatCommandManager.PlaybackControlCooldownGroup;

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var title = _playback.Current?.Title;

            // Loading the next video can take a while, so reply right away instead of awaiting it.
            _playback.SkipAsync().FireAndForget("Skipping via !forceskip failed.");

            var reply = string.IsNullOrWhiteSpace(title)
                ? $"Skipped to the next video (requested by {context.DisplayName})."
                : $"Skipped \"{title}\" (requested by {context.DisplayName}).";
            return Task.FromResult<string?>(reply);
        }
    }
}
