using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer
{
    /// <summary>
    /// Connects the managers: runs the startup sequence and starts or switches playback
    /// whenever the set of playable videos changes.
    /// </summary>
    public sealed class AppCoordinator : IDisposable
    {
        private readonly PlaybackManager _playback;
        private readonly PlaylistManager _playlists;
        private readonly TwitchChatService? _twitch;
        private bool _initialized;
        private bool _playbackStarted;
        private bool _standbyDone;
        private bool _noVideosReported;

        public AppCoordinator(PlaybackManager playback, PlaylistManager playlists, TwitchChatService? twitch)
        {
            _playback = playback;
            _playlists = playlists;
            _twitch = twitch;

            _playlists.PoolChanged += OnPoolChanged;
            _playlists.PlaylistsChanged += OnPlaylistsChanged;
            _playback.NoVideosAvailable += OnPlaybackRanOutOfVideos;
        }

        /// <summary>There is nothing to play and no playlist is loading anymore.</summary>
        public event EventHandler? NoVideosAvailable;

        public async Task StartAsync()
        {
            _twitch?.StartAsync().FireAndForget("Starting the Twitch bot failed.");

            try
            {
                await _playlists.InitializeAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Loading the playlists failed.", ex);
            }

            _initialized = true;
            StartPlaybackIfPossible();
        }

        private void OnPoolChanged(object? sender, PoolChangedEventArgs e)
        {
            if (!_initialized)
                return;

            if (!_playbackStarted)
                StartPlaybackIfPossible();
            else if (e.SelectionChanged)
            {
                // A video queued by a chat vote comes from the previous selection.
                _playback.ClearQueuedVideo();
                _playback.SkipAsync().FireAndForget("Switching to the selected playlist failed.");
            }
            // Otherwise the pool only grew or was updated; the current video keeps playing.
        }

        private void OnPlaylistsChanged(object? sender, EventArgs e)
        {
            // Covers the case that the last background load finished without any playable video.
            if (_initialized && !_playbackStarted)
                StartPlaybackIfPossible();
        }

        private void OnPlaybackRanOutOfVideos(object? sender, EventArgs e)
        {
            _playbackStarted = false;
            StartPlaybackIfPossible();
        }

        private void StartPlaybackIfPossible()
        {
            if (_playlists.PoolCount > 0)
            {
                _playbackStarted = true;
                _noVideosReported = false;

                // The app opens paused: the first video is prepared and starts when the user presses Play.
                // Later starts (e.g. after the pool ran empty) play at once.
                if (!_standbyDone)
                {
                    _standbyDone = true;
                    _playback.EnterStandby();
                }
                else
                {
                    _playback.PlayNextAsync().FireAndForget("Starting playback failed.");
                }
            }
            else if (!_playlists.IsLoading && !_noVideosReported)
            {
                _noVideosReported = true;
                NoVideosAvailable?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            _playlists.PoolChanged -= OnPoolChanged;
            _playlists.PlaylistsChanged -= OnPlaylistsChanged;
            _playback.NoVideosAvailable -= OnPlaybackRanOutOfVideos;
        }
    }
}
