using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!reload</c> (moderators, broadcaster and privileged users): gets fresh stream links for the current video and
    /// continues it a few seconds before where it is. A safety measure when picture or sound misbehave.
    /// </summary>
    public sealed class ReloadCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public ReloadCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "reload";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var title = _playback.Current?.Title;
            var reply = _playback.Reload() switch
            {
                // The lookup can take a while, so reply right away; the video shows "Reloading" meanwhile.
                ReloadResult.Started => $"Reloading{Quoted(title)}"
                    + (_playback.ResumePosition is { } at ? $", continuing at {PositionText.Format(at)}" : string.Empty)
                    + $" (requested by {context.DisplayName}).",
                ReloadResult.Busy => "A video is being loaded right now; try !reload again in a moment.",
                _ => "There is no video playing that could be reloaded."
            };
            return Task.FromResult<string?>(reply);
        }

        private static string Quoted(string? title) => string.IsNullOrWhiteSpace(title) ? " the video" : $" \"{title}\"";
    }
}
