using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!playlist</c> (everybody, it only reads): the name of the playing playlist and whether it plays shuffled
    /// (random order) or in playlist order.
    /// </summary>
    public sealed class PlaylistCommand : IChatCommand
    {
        private readonly IShuffleControl _playlist;

        public PlaylistCommand(IShuffleControl playlist, TimeSpan cooldown)
        {
            _playlist = playlist;
            Cooldown = cooldown;
        }

        public string Name => "playlist";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            var name = !_playlist.CanChangeShuffle
                ? "all playlists combined"
                : string.IsNullOrWhiteSpace(_playlist.PlaylistTitle) ? "(no name)" : $"\"{_playlist.PlaylistTitle}\"";
            var order = _playlist.IsShuffled ? "shuffled" : "in playlist order";

            return Task.FromResult<string?>($"Current playlist: {name} ({order}).");
        }
    }

    /// <summary>
    /// <c>!skipplaylist</c> (moderators, broadcaster and privileged users): switches to the next playlist now, like the
    /// automatic swap but without waiting for the end of the current video. The bot announces the new playlist when
    /// its first video starts (<see cref="PlaylistAnnouncer"/>), so a successful switch needs no reply of its own.
    /// </summary>
    public sealed class SkipPlaylistCommand : IChatCommand
    {
        internal const string NoOtherPlaylist = "There is no other playlist to switch to.";

        private readonly IPlaylistSwitch _switch;

        public SkipPlaylistCommand(IPlaylistSwitch playlistSwitch, TimeSpan cooldown)
        {
            _switch = playlistSwitch;
            Cooldown = cooldown;
        }

        public string Name => "skipplaylist";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public async Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            if (_switch.NextPlaylistTitle == null)
                return NoOtherPlaylist;

            try
            {
                var title = await _switch.SwitchToNextPlaylistAsync();
                if (title == null)
                    return NoOtherPlaylist;

                Logger.Log($"Switched to playlist \"{title}\" for {context.Username} (!skipplaylist).");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log("Switching to the next playlist via chat failed.", ex);
                return "The playlist could not be changed.";
            }
        }
    }
}
