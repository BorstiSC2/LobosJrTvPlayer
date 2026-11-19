using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!current</c>: posts the title of the video that is playing, its position (with the length and the
    /// time left, if the length is known) and a link to it on YouTube.
    /// </summary>
    public sealed class CurrentCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public CurrentCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "current";

        public string Description =>
            "Shows the video that is playing right now: its title, how far into it we are, how much is left, and a link to it on YouTube.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var current = _playback.Current;
            if (current == null || !_playback.IsPlaying)
                return Task.FromResult<string?>("Nothing is playing right now.");

            var title = string.IsNullOrWhiteSpace(current.Title) ? "The current video" : $"\"{current.Title}\"";
            var url = YouTubeService.ToUrl(current.VideoId);

            return Task.FromResult<string?>($"Now playing: {title} – {DescribePosition()} – {url}");
        }

        private string DescribePosition()
        {
            var position = _playback.Position;
            if (_playback.Length is not { } total || total <= TimeSpan.Zero)
                return Format(position);

            var remaining = total - position;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            return $"{Format(position)} / {Format(total)} ({Format(remaining)} left)";
        }

        private static string Format(TimeSpan time) =>
            time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}
