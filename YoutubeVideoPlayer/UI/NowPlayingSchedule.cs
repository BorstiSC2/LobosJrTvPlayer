using System;
using System.Threading;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// When the "now playing" card is due: for <see cref="ShowDuration"/> when a video starts, again every
    /// <see cref="Interval"/> while it plays, and when asked (<c>!current</c>). Only the timing; the form decides whether
    /// the card can be seen at the moment (not over a vote ticker or a status text).
    /// </summary>
    /// <remarks>Lives on the UI thread like the managers; the timers run on the injected <see cref="TimeProvider"/>.</remarks>
    public sealed class NowPlayingSchedule : IDisposable
    {
        /// <summary>How long the card stays up each time.</summary>
        internal static readonly TimeSpan ShowDuration = TimeSpan.FromSeconds(12);

        /// <summary>The time from one appearance to the next while a video plays.</summary>
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        private readonly IPlaybackControl _playback;
        private readonly ContextTimer _hideTimer;
        private readonly ContextTimer _intervalTimer;
        private bool _isShown;
        private bool _disposed;

        public NowPlayingSchedule(IPlaybackControl playback, TimeProvider? timeProvider = null)
        {
            var context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("NowPlayingSchedule must be created on the UI thread.");

            var time = timeProvider ?? TimeProvider.System;
            _playback = playback;
            _hideTimer = new ContextTimer(context, ShowDuration, periodic: false, () => SetShown(false), time);
            _intervalTimer = new ContextTimer(context, Interval, periodic: true, OnInterval, time);
            _playback.NowPlayingChanged += OnNowPlayingChanged;
        }

        /// <summary>The card is due right now (it may still be hidden by the form, see <see cref="CanShow"/>).</summary>
        public bool IsShown => _isShown;

        /// <summary>A video is there to show: not standing by, not waiting for a vote with a black picture.</summary>
        public bool CanShow => _playback.Current != null && !_playback.IsStandby && !_playback.IsWaitingForHold;

        /// <summary><see cref="IsShown"/> changed.</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Shows the card for <see cref="ShowDuration"/> (longer if it is up already) and starts the interval again from
        /// now, so a request does not make the next regular appearance come right after it.
        /// </summary>
        public void Show()
        {
            if (_disposed)
                return;

            _hideTimer.Start();
            _intervalTimer.Start();
            SetShown(true);
        }

        private void OnNowPlayingChanged(object? sender, NowPlaying nowPlaying) => Show();

        private void OnInterval()
        {
            // Paused, between videos or without a video: wait for the next one.
            if (CanShow && _playback.IsPlaying)
                Show();
        }

        private void SetShown(bool shown)
        {
            if (_disposed || shown == _isShown)
                return;

            _isShown = shown;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _playback.NowPlayingChanged -= OnNowPlayingChanged;
            _hideTimer.Dispose();
            _intervalTimer.Dispose();
        }
    }
}
