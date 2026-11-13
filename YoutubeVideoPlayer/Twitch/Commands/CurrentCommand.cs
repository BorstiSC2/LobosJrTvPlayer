using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!current</c>: posts the title of the video that is playing and a link to it on YouTube.</summary>
    public sealed class CurrentCommand : IChatCommand
    {
        private readonly IPlaybackControl _playback;

        public CurrentCommand(IPlaybackControl playback, TimeSpan cooldown)
        {
            _playback = playback;
            Cooldown = cooldown;
        }

        public string Name => "current";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var current = _playback.Current;
            if (current == null || !_playback.IsPlaying)
                return Task.FromResult<string?>("Nothing is playing right now.");

            var title = string.IsNullOrWhiteSpace(current.Title) ? "The current video" : $"\"{current.Title}\"";
            var url = YouTubeService.ToUrl(current.VideoId);

            return Task.FromResult<string?>($"Now playing: {title} – {url}");
        }
    }
}
