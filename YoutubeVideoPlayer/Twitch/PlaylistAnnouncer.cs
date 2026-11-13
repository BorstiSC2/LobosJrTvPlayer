using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Tells the chat when another playlist has begun: after a change of the playlist (an automatic swap,
    /// <c>!skipplaylist</c> or a switch in the playlist dialog) it posts <see cref="MessageFormat"/> as soon as the
    /// first video of the new playlist starts. Nothing is posted while the bot is not connected.
    /// </summary>
    /// <remarks>
    /// The message waits for a video of the new playlist: after an automatic swap the current video still plays to its
    /// end, and a vote winner from the old playlist may play before the new playlist begins. Must be used on the UI thread.
    /// </remarks>
    public sealed class PlaylistAnnouncer : IDisposable
    {
        internal const string MessageFormat = "New Playlist has begun: {0}";

        /// <summary>The name of the selection that combines every playlist.</summary>
        internal const string AllPlaylistsName = "All playlists";

        private readonly IChatChannel _chat;
        private readonly PlaylistManager _playlists;
        private readonly IPlaybackControl _playback;
        private string? _pendingTitle;

        public PlaylistAnnouncer(IChatChannel chat, PlaylistManager playlists, IPlaybackControl playback)
        {
            _chat = chat;
            _playlists = playlists;
            _playback = playback;

            _playlists.PoolChanged += OnPoolChanged;
            _playback.NowPlayingChanged += OnNowPlayingChanged;
        }

        private void OnPoolChanged(object? sender, PoolChangedEventArgs e)
        {
            // A later change replaces one that has not begun yet: only the playlist that really plays is announced.
            if (e.SelectionChanged)
                _pendingTitle = _playlists.PlaylistTitle ?? AllPlaylistsName;
        }

        private void OnNowPlayingChanged(object? sender, NowPlaying video)
        {
            if (_pendingTitle is not { } title || !_playlists.IsInPool(video.VideoId))
                return;

            _pendingTitle = null;
            if (!_chat.IsConnected)
                return;

            SendAsync(string.Format(MessageFormat, title)).FireAndForget("Announcing the new playlist failed.");
        }

        private async Task SendAsync(string message)
        {
            Logger.Log($"Announcing in chat: {message}");
            await _chat.SendMessageAsync(message);
        }

        public void Dispose()
        {
            _playlists.PoolChanged -= OnPoolChanged;
            _playback.NowPlayingChanged -= OnNowPlayingChanged;
        }
    }
}
