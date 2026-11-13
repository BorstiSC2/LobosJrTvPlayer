using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!timestamp</c>: posts the current position of the video and how much of it is left.</summary>
    public sealed class TimestampCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public TimestampCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "timestamp";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var current = _playback.Current;
            if (current == null || !_playback.IsPlaying)
                return Task.FromResult<string?>("Nothing is playing right now.");

            var position = _playback.Position;
            var length = _playback.Length;
            var title = string.IsNullOrWhiteSpace(current.Title) ? "The current video" : $"\"{current.Title}\"";

            string reply;
            if (length is { } total && total > TimeSpan.Zero)
            {
                var remaining = total - position;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                reply = $"{title} – {Format(position)} / {Format(total)} ({Format(remaining)} left)";
            }
            else
            {
                reply = $"{title} – {Format(position)}";
            }

            return Task.FromResult<string?>(reply);
        }

        private static string Format(TimeSpan time) =>
            time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}
