using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Checks every <see cref="CheckInterval"/> through the Twitch API whether the streamer's main channel is live, and
    /// when it goes live, posts <see cref="Message"/> in the VOD chat (the bot's own; it never joins the main channel).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a change the watcher sees counts: a main channel that is live already at the first check gets no alert. A
    /// stream that drops and comes back within <see cref="NewStreamAfter"/> is the same stream (no second alert); after a
    /// longer break it is a new one. A check that cannot tell (no token yet, network) changes nothing and is logged once
    /// per series of failures.
    /// </para>
    /// <para>
    /// While the chat is not connected, an alert waits and is posted at the next check once the chat is back, if the
    /// main channel is still live (at most once).
    /// </para>
    /// <para>Lives on the UI thread like the managers; the checks run on a <see cref="ContextTimer"/> of the injected clock.</para>
    /// </remarks>
    public sealed class MainChannelWatcher : IDisposable
    {
        public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

        /// <summary>How long the main channel has to be offline after a stream before going live counts as a new stream.</summary>
        public static readonly TimeSpan NewStreamAfter = TimeSpan.FromMinutes(10);

        internal const string AlertText = "ALERT LIVE ON MAIN ALERT";

        private enum State
        {
            Unknown,
            Live,
            Offline
        }

        private readonly IChatChannel _chat;
        private readonly ITwitchStreamClient _streams;
        private readonly TimeProvider _time;
        private readonly ContextTimer _timer;
        private readonly CancellationTokenSource _cancellation = new();

        private State _state = State.Unknown;
        private DateTimeOffset? _droppedAt; // when a stream that was seen live went offline
        private bool _alertPending;
        private bool _failing;
        private bool _checking;
        private bool _disposed;

        /// <param name="mainChannel">The main channel's login name (normalized, without <c>#</c>).</param>
        public MainChannelWatcher(IChatChannel chat, ITwitchStreamClient streams, string mainChannel, TimeProvider? timeProvider = null)
        {
            var context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("MainChannelWatcher must be created on the UI thread.");

            _chat = chat;
            _streams = streams;
            MainChannel = mainChannel;
            _time = timeProvider ?? TimeProvider.System;
            _timer = new ContextTimer(context, CheckInterval, periodic: true,
                () => CheckAsync().FireAndForget("Checking the main channel failed."), _time);
        }

        public string MainChannel { get; }

        /// <summary>What the bot posts in the VOD chat when the main channel goes live.</summary>
        public string Message => $"{AlertText} – twitch.tv/{MainChannel}";

        /// <summary>Checks now and then every <see cref="CheckInterval"/>.</summary>
        public void Start()
        {
            if (_disposed)
                return;

            Logger.Log($"Watching whether #{MainChannel} goes live (every {CheckInterval.TotalMinutes:0} min).");
            _timer.Start();
            CheckAsync().FireAndForget("Checking the main channel failed.");
        }

        /// <summary>One check: asks Twitch, updates the state, posts a waiting alert. The timer calls it; tests call it themselves.</summary>
        internal async Task CheckAsync()
        {
            if (_disposed || _checking)
                return; // a slow answer is not asked for twice

            _checking = true;
            try
            {
                var status = await _streams.GetStatusAsync(MainChannel, _cancellation.Token);
                if (_disposed)
                    return;

                Apply(status);
                await PostPendingAlertAsync();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // disposed while asking
            }
            finally
            {
                _checking = false;
            }
        }

        private void Apply(LiveStatus status)
        {
            if (status.IsLive is not { } live)
            {
                if (!_failing)
                    Logger.Log($"Could not check whether #{MainChannel} is live: {status.Problem}. Trying again every minute.");
                _failing = true;
                return;
            }

            if (_failing)
                Logger.Log($"Checking whether #{MainChannel} is live works again.");
            _failing = false;

            var now = _time.GetUtcNow();
            if (live)
            {
                switch (_state)
                {
                    case State.Unknown:
                        Logger.Log($"#{MainChannel} is live already; no alert for a stream that was running before.");
                        break;
                    case State.Offline when _droppedAt is { } dropped && now - dropped < NewStreamAfter:
                        Logger.Log($"#{MainChannel} is live again after a short break; the same stream, no new alert.");
                        break;
                    case State.Offline:
                        Logger.Log($"#{MainChannel} went live.");
                        _alertPending = true;
                        break;
                }

                _state = State.Live;
            }
            else
            {
                if (_state == State.Live)
                {
                    Logger.Log($"#{MainChannel} went offline.");
                    _droppedAt = now;
                }
                else if (_state == State.Unknown)
                {
                    Logger.Log($"#{MainChannel} is offline.");
                }

                _state = State.Offline;
                _alertPending = false; // the stream is over before the alert could go out
            }
        }

        private async Task PostPendingAlertAsync()
        {
            if (!_alertPending || !_chat.IsConnected)
                return;

            _alertPending = false;
            try
            {
                await _chat.SendMessageAsync(Message);
                Logger.Log($"Posted in the VOD chat that #{MainChannel} is live.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not post in the VOD chat that #{MainChannel} is live.", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Dispose();
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }
}
