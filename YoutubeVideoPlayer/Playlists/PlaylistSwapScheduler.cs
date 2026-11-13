using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// The automatic playlist swap: once the interval set in the playlist dialog is up (see
    /// <see cref="PlaylistManager.NextSwapUtc"/>), the next playlist of the list takes over. The video that plays then
    /// is not interrupted: shortly before it ends (<see cref="LeadTime"/>) the next playlist is selected, so the video
    /// after it, and the options of an autovote about that video, come from the next playlist.
    /// </summary>
    /// <remarks>
    /// <para>Must be created on the UI thread; the checks run on it every <see cref="CheckInterval"/>.</para>
    /// <para>
    /// While a vote about the next video runs or its winner waits to be played, the swap waits: the winner plays first,
    /// and the next playlist takes over near the end of that video. While nothing plays (the first video is prepared
    /// but not started), the next playlist takes over at once and its first video is prepared instead. A video with an
    /// unknown length (a live stream) has no end to wait for, so the next playlist is selected at once and follows it.
    /// </para>
    /// <para>
    /// The interval starts over with the swap (<see cref="PlaylistManager.SelectAsync"/>), so a playlist plays at least
    /// for the whole interval. The setting and the start of the interval are stored in <c>playlists.json</c>, so a
    /// swap that fell due while the app was closed happens right after the next start.
    /// </para>
    /// </remarks>
    public sealed class PlaylistSwapScheduler : IDisposable
    {
        /// <summary>
        /// How long before the end of the current video the next playlist is selected: a little earlier than the
        /// autovote starts (5 minutes before the end), so that its options already come from the next playlist.
        /// </summary>
        public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30);

        /// <summary>How often the scheduler looks whether the swap is due.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>The wait after a swap that failed (e.g. <c>playlists.json</c> could not be saved).</summary>
        public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

        private readonly PlaylistManager _playlists;
        private readonly IPlaybackControl _playback;
        private readonly Func<bool> _isVoteRunning;
        private readonly SynchronizationContext _context;
        private readonly TimeProvider _time;
        private readonly ContextTimer _timer;
        private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
        private bool _swapping;
        private bool _nothingToSwapToLogged;
        private bool _disposed;

        /// <param name="isVoteRunning">Whether a chat vote is running (<c>null</c> without the Twitch bot).</param>
        public PlaylistSwapScheduler(
            PlaylistManager playlists,
            IPlaybackControl playback,
            Func<bool>? isVoteRunning = null,
            TimeProvider? timeProvider = null)
        {
            _context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("PlaylistSwapScheduler must be created on the UI thread.");

            _playlists = playlists;
            _playback = playback;
            _isVoteRunning = isVoteRunning ?? (() => false);
            _time = timeProvider ?? TimeProvider.System;
            _timer = new ContextTimer(_context, CheckInterval, periodic: true, Check, _time);
            _timer.Start();

            _playback.NowPlayingChanged += OnNowPlayingChanged;
        }

        /// <summary>
        /// A video shorter than the lead time is inside it from its start: look at once, before the autovote's own check
        /// starts a vote with the options of the old playlist. Posted, so it does not run in the middle of the start.
        /// </summary>
        private void OnNowPlayingChanged(object? sender, NowPlaying e) => _context.Post(_ => Check(), null);

        private void Check()
        {
            // Before the selected playlist is loaded at startup there is nothing to swap from.
            if (_disposed || _swapping || !_playlists.IsInitialized || _playlists.NextSwapUtc is not { } due)
                return;

            var now = _time.GetUtcNow();
            if (now < due || now < _retryAt || !IsGoodMoment())
                return;

            var next = _playlists.GetNextPlaylist();
            if (next == null)
            {
                // It swaps as soon as another playlist can be played again (e.g. after an update).
                if (!_nothingToSwapToLogged)
                    Logger.Log("The automatic playlist swap is due, but no other playlist can be played.");
                _nothingToSwapToLogged = true;
                return;
            }

            _nothingToSwapToLogged = false;
            SwapAsync(next).FireAndForget("The automatic playlist swap failed.");
        }

        /// <summary>Whether the next playlist can take over now without cutting a video short or dropping a vote's winner.</summary>
        private bool IsGoodMoment()
        {
            // The winner of a vote about the next video plays first; the swap follows near the end of that video.
            if (_isVoteRunning() || _playback.HasQueuedVideo || _playback.IsWaitingForHold)
                return false;

            // Nothing plays yet: the first video of the next playlist is prepared instead.
            if (_playback.Current == null || _playback.IsStandby)
                return true;

            // Without a known length (a live stream) there is no end to wait for.
            if (_playback.Length is not { } length || length <= TimeSpan.Zero)
                return true;

            return length - _playback.Position <= LeadTime;
        }

        private async Task SwapAsync(PlaylistInfo next)
        {
            _swapping = true;
            try
            {
                Logger.Log($"Automatic playlist swap: \"{next.Title}\" takes over after the current video.");
                await _playlists.SelectAsync(next.Key, keepCurrentVideo: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"The automatic swap to playlist \"{next.Title}\" failed; trying again in {RetryDelay.TotalMinutes:0} minute(s).", ex);
                _retryAt = _time.GetUtcNow() + RetryDelay;
            }
            finally
            {
                _swapping = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _playback.NowPlayingChanged -= OnNowPlayingChanged;
            _timer.Dispose();
        }
    }
}
