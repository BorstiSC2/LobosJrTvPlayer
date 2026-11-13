using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Voting
{
    /// <summary>The Autovote switch, as the chat commands <c>!autovotestatus</c> and <c>!toggleautovote</c> use it.</summary>
    public interface IAutoVoteControl
    {
        /// <summary>Whether a vote for the next video starts by itself before a video ends.</summary>
        bool Enabled { get; set; }
    }

    /// <summary>
    /// The "Autovote" option: while enabled, starts a chat vote <see cref="LeadTime"/> before the current
    /// video ends. The vote is about the video that plays <i>after</i> the current one; its winner is
    /// queued and starts when the current video is over.
    /// </summary>
    /// <remarks>
    /// <para>Must be created on the UI thread; the checks run on it every <see cref="CheckInterval"/>.</para>
    /// <para>
    /// There is one vote per video. The vote is shortened so that it is over, with a safety margin,
    /// before the video ends; a video with too little time left (or an unknown length, e.g. a live
    /// stream) gets no vote. Nothing happens while the bot is not connected to the chat.
    /// </para>
    /// </remarks>
    public sealed class AutoVoteManager : IAutoVoteControl, IDisposable
    {
        /// <summary>How long before the end of a video the vote starts.</summary>
        public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(5);

        /// <summary>How often the remaining time of the current video is checked.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>The vote ends at least this long before the video does.</summary>
        public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(10);

        private readonly IChatChannel _chat;
        private readonly IPlaybackControl _playback;
        private readonly SkipVoteManager _votes;
        private readonly ContextTimer _timer;
        private NowPlaying? _handledVideo;
        private bool _enabled;
        private bool _disposed;

        public AutoVoteManager(
            IChatChannel chat,
            IPlaybackControl playback,
            SkipVoteManager votes,
            TimeProvider? timeProvider = null)
        {
            var context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("AutoVoteManager must be created on the UI thread.");

            _chat = chat;
            _playback = playback;
            _votes = votes;
            _timer = new ContextTimer(context, CheckInterval, periodic: true, Check, timeProvider);
        }

        /// <summary>
        /// Turns the automatic vote on or off. Turning it off does not cancel a vote that is already
        /// running; it only prevents new ones.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_disposed || value == _enabled)
                    return;

                _enabled = value;
                Logger.Log(value ? "Autovote enabled." : "Autovote disabled.");

                if (value)
                {
                    _timer.Start();
                    Check(); // the current video may already be inside the last minutes
                }
                else
                {
                    _timer.Stop();
                }

                EnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Raised when <see cref="Enabled"/> changed (by the checkbox or by a chat command).</summary>
        public event EventHandler? EnabledChanged;

        private void Check()
        {
            if (!_enabled || _disposed || _votes.IsRunning || !_chat.IsConnected)
                return;

            var current = _playback.Current;
            if (current == null || ReferenceEquals(current, _handledVideo) || !_playback.IsPlaying)
                return;

            // Without a known length (live stream) there is no "5 minutes before the end".
            if (_playback.Length is not { } length || length <= TimeSpan.Zero)
                return;

            var remaining = length - _playback.Position;
            if (remaining > LeadTime)
                return;

            // One attempt per video, whether or not it works out.
            _handledVideo = current;

            // Posting the options takes a few seconds; the vote has to be over before the video is.
            var available = remaining - SkipVoteManager.AnnouncementDuration - SafetyMargin;
            var duration = TimeSpan.FromSeconds(Math.Floor(Math.Min(_votes.Duration.TotalSeconds, available.TotalSeconds)));
            if (duration < SkipVoteManager.MinimumDuration)
            {
                Logger.Log($"Autovote: only {remaining:mm\\:ss} left of \"{current.Title}\"; too short for a vote.");
                return;
            }

            StartVoteAsync(duration).FireAndForget("The autovote failed.");
        }

        private async Task StartVoteAsync(TimeSpan duration)
        {
            var reply = await _votes.StartAfterCurrentAsync(duration);
            if (reply != null)
                Logger.Log($"Autovote did not start: {reply}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Dispose();
        }
    }
}
