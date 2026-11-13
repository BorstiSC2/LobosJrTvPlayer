using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!play</c> (moderators, broadcaster and privileged users): continues a paused video, or starts the prepared
    /// first video while the player stands by (like the Play button).
    /// </summary>
    public sealed class PlayCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public PlayCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "play";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            string reply;
            if (_playback.IsStandby)
            {
                // Starting the video can take a moment, so reply right away instead of awaiting it.
                _playback.PlayNextAsync().FireAndForget("Starting playback via !play failed.");
                reply = $"Playback started (requested by {context.DisplayName}).";
            }
            else if (_playback.IsWaitingForHold)
            {
                reply = "The next video starts when the vote is over (!skip starts it now).";
            }
            else if (_playback.IsPaused)
            {
                _playback.Resume();
                reply = $"Continuing{PlayPauseTexts.Title(_playback)} (requested by {context.DisplayName}).";
            }
            else if (_playback.Current == null)
            {
                reply = "There is no video to play yet.";
            }
            else
            {
                reply = $"Already playing{PlayPauseTexts.Title(_playback)}.";
            }

            return Task.FromResult<string?>(reply);
        }
    }

    /// <summary><c>!pause</c> (moderators, broadcaster and privileged users): pauses the current video (<c>!play</c> continues it).</summary>
    public sealed class PauseCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public PauseCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "pause";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            string reply;
            if (_playback.IsPaused)
            {
                reply = $"Already paused{PlayPauseTexts.Title(_playback)} – continue with !play.";
            }
            else if (_playback.IsWaitingForHold)
            {
                reply = "Nothing is playing – the next video starts when the vote is over.";
            }
            else if (_playback.IsStandby || _playback.Current == null)
            {
                reply = "Nothing is playing.";
            }
            else
            {
                _playback.Pause();
                reply = $"Paused{PlayPauseTexts.Title(_playback)} (requested by {context.DisplayName}) – continue with !play.";
            }

            return Task.FromResult<string?>(reply);
        }
    }

    internal static class PlayPauseTexts
    {
        /// <summary>The title of the current video with a leading space, or nothing if it is not known.</summary>
        public static string Title(IPlaybackControl playback)
        {
            var title = playback.Current?.Title;
            return string.IsNullOrWhiteSpace(title) ? string.Empty : $" \"{title}\"";
        }
    }
}
