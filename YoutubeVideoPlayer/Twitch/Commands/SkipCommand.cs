using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!skip</c> (moderators and broadcaster only): skips to the next video.</summary>
    public sealed class SkipCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public SkipCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "skip";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var title = _playback.Current?.Title;

            // Loading the next video can take a while, so reply right away instead of awaiting it.
            _playback.SkipAsync().FireAndForget("Skipping via !skip failed.");

            var reply = string.IsNullOrWhiteSpace(title)
                ? $"Skipped to the next video (requested by {context.DisplayName})."
                : $"Skipped \"{title}\" (requested by {context.DisplayName}).";
            return Task.FromResult<string?>(reply);
        }
    }
}
