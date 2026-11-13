using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary>
    /// Keeps playback going: resolves YouTube streams, plays them on the <see cref="IMediaPlayer"/>,
    /// moves on at the end of a video or on errors, and watches for stuck buffering and frozen playback.
    /// </summary>
    /// <remarks>
    /// Must be created on the UI thread. All player events and timer ticks are marshalled onto the
    /// captured <see cref="SynchronizationContext"/>, so every member and event runs on that thread.
    /// </remarks>
    public sealed class PlaybackManager : IPlaybackControl, IDisposable
    {
        /// <summary>Max buffering before skipping to the next video.</summary>
        public static readonly TimeSpan BufferingTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Watchdog interval; skips after two failed checks in a row.</summary>
        public static readonly TimeSpan FailSafeInterval = TimeSpan.FromSeconds(20);

        public static readonly TimeSpan DelayBeforeNextVideo = TimeSpan.FromSeconds(1);

        private readonly SynchronizationContext _context;
        private readonly IYouTubeService _youtube;
        private readonly IVideoSource _videoSource;
        private readonly IMediaPlayer _player;
        private readonly TimeProvider _time;
        private readonly ContextTimer _bufferingTimeoutTimer;
        private readonly ContextTimer _failSafeTimer;

        private CancellationTokenSource? _playNextCancellationTokenSource;
        private Task? _playNextTask;
        private string? _queuedVideoId;
        private bool _failSafeTriggered;
        private long _failSafePlaybackTime = long.MinValue;
        private int _mediaGeneration;
        private bool _isBuffering;
        private int _volume = 100;
        private bool _isMuted;
        private bool _disposed;

        public PlaybackManager(IYouTubeService youtube, IVideoSource videoSource, IMediaPlayer player, TimeProvider? timeProvider = null)
        {
            _context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("PlaybackManager must be created on the UI thread.");
            _youtube = youtube;
            _videoSource = videoSource;
            _player = player;
            _time = timeProvider ?? TimeProvider.System;

            _player.EndReached += (_, __) => PostFromPlayer(() => PlayNextAsync().FireAndForget("PlayNextAsync after EndReached failed."));
            _player.Buffering += (_, cache) => PostFromPlayer(() => OnBuffering(cache));
            _player.Playing += (_, __) => PostFromPlayer(OnPlaying);
            _player.EncounteredError += (_, __) => PostFromPlayer(OnEncounteredError);

            _bufferingTimeoutTimer = new ContextTimer(_context, BufferingTimeout, periodic: false, OnBufferingTimeout, _time);
            _failSafeTimer = new ContextTimer(_context, FailSafeInterval, periodic: true, OnFailSafeTick, _time);
        }

        /// <summary>The video currently loaded, or <c>null</c> while nothing has been started.</summary>
        public NowPlaying? Current { get; private set; }

        public bool IsPlaying => !_disposed && _player.IsPlaying;

        public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _player.TimeMs));

        /// <summary>Length reported by the player, falling back to the duration from YouTube.</summary>
        public TimeSpan? Length => _player.LengthMs > 0
            ? TimeSpan.FromMilliseconds(_player.LengthMs)
            : Current?.Duration;

        public int Volume
        {
            get => _volume;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (clamped == _volume)
                    return;

                _volume = clamped;
                _player.Volume = clamped;
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (value == _isMuted)
                    return;

                _isMuted = value;
                _player.Mute = value;
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>A new video is being resolved (shown as "Loading next video...").</summary>
        public event EventHandler? LoadingStarted;

        /// <summary>The metadata of the new video is known and playback is starting.</summary>
        public event EventHandler<NowPlaying>? NowPlayingChanged;

        /// <summary><c>true</c> while the player is buffering, <c>false</c> once it is done.</summary>
        public event EventHandler<bool>? BufferingChanged;

        public event EventHandler? VolumeChanged;

        /// <summary>The video source returned no video, so playback stopped.</summary>
        public event EventHandler? NoVideosAvailable;

        public void AdjustVolume(int delta) => Volume = _volume + delta;

        public void ToggleMute() => IsMuted = !_isMuted;

        /// <summary>Stops the current video immediately and starts the next one.</summary>
        public Task SkipAsync()
        {
            // Events of the video that is being stopped must not skip the next one as well.
            Interlocked.Increment(ref _mediaGeneration);

            // Stop current playback immediately to avoid waiting for manifests to load
            try
            {
                _player.Stop();
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured trying to stop the current video before skipping.", ex);
            }

            return PlayNextAsync();
        }

        /// <summary>
        /// Plays the next video from the video source: a random one if the playlist is shuffled, otherwise the
        /// one after the current video in playlist order. A previous call that is still resolving
        /// streams is cancelled first, so overlapping triggers cannot race each other.
        /// </summary>
        public Task PlayNextAsync() => RunPlaybackOperationAsync(PlayNextInternalAsync);

        /// <summary>
        /// Plays a specific video (e.g. the winner of a chat vote). The current video keeps playing until
        /// the new one is ready. If the video cannot be played, the next video is played instead.
        /// </summary>
        public Task PlayVideoAsync(string videoId) => RunPlaybackOperationAsync(async cancellationToken =>
        {
            try
            {
                await PlayVideoByIdAsync(videoId, cancellationToken);
                _queuedVideoId = null; // an explicit choice replaces an older queued one
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            // Only our own cancellation ends the operation; a timeout inside YouTube's HTTP client
            // also surfaces as an OperationCanceledException and must fall back to the next video.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Log("PlayVideoAsync operation was canceled.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Playing the requested video '{videoId}' failed; playing the next video instead.", ex);
                return await PlayNextInternalAsync(cancellationToken);
            }
        });

        /// <summary>
        /// Queues a video that plays when the current one ends (or is skipped) instead of the regular next one.
        /// It is only dropped once it was started or could not be played; a cancelled attempt keeps it.
        /// </summary>
        public void QueueNextVideo(string videoId)
        {
            if (_disposed)
                return;

            Logger.Log($"Queued video '{videoId}' to play after the current one.");
            _queuedVideoId = videoId;
        }

        /// <summary>Forgets a queued video, e.g. because the pool it came from was switched.</summary>
        public void ClearQueuedVideo() => _queuedVideoId = null;

        /// <summary>
        /// Runs one playback operation at a time: the previous operation is cancelled and awaited first.
        /// The operation returns <c>true</c> if the video source had no video to offer.
        /// </summary>
        private async Task RunPlaybackOperationAsync(Func<CancellationToken, Task<bool>> operation)
        {
            if (_disposed)
                return;

            // Cancel and await whatever is registered. Other callers may register a new operation while
            // we wait, so look again after every await until nothing is left. Between the last check and
            // the registration below there is no await, so two callers can never both start an operation.
            while (_playNextCancellationTokenSource is { } previousCancellationTokenSource)
            {
                var previousTask = _playNextTask;

                Logger.Log("Cancelling previous PlayNextAsync operation...");

                await previousCancellationTokenSource.CancelAsync();

                if (previousTask != null)
                {
                    try
                    {
                        await previousTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the previous operation was cancelled.
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Previous PlayNextAsync operation failed.", ex);
                    }
                }

                // Only clear the fields if they still refer to the operation that we captured.
                if (ReferenceEquals(_playNextCancellationTokenSource, previousCancellationTokenSource))
                {
                    _playNextCancellationTokenSource = null;
                    _playNextTask = null;
                }

                previousCancellationTokenSource.Dispose();
            }

            if (_disposed)
                return;

            if (!_failSafeTimer.IsRunning)
                _failSafeTimer.Start();

            var cancellationTokenSource = new CancellationTokenSource();
            _playNextCancellationTokenSource = cancellationTokenSource;

            var currentTask = operation(cancellationTokenSource.Token);
            _playNextTask = currentTask;

            bool outOfVideos;
            try
            {
                outOfVideos = await currentTask;
            }
            finally
            {
                // Only clean up if this is still the current operation.
                if (ReferenceEquals(_playNextTask, currentTask))
                    _playNextTask = null;

                if (ReferenceEquals(_playNextCancellationTokenSource, cancellationTokenSource))
                {
                    _playNextCancellationTokenSource = null;
                    cancellationTokenSource.Dispose();
                }
            }

            // Raised after the operation is unregistered: the handlers show modal dialogs, and a new
            // operation started from there must not wait for this one to return.
            if (outOfVideos && !_disposed)
            {
                if (_playNextCancellationTokenSource == null)
                    _failSafeTimer.Stop(); // nothing is playing, so there is nothing to watch

                NoVideosAvailable?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task<bool> PlayNextInternalAsync(CancellationToken cancellationToken)
        {
            Logger.Log("Attempting to play next video...");

            // The video the next one follows. An ordered playlist continues after it; a video that cannot
            // be played moves it forward, so the retry does not pick the same broken video again.
            var after = Current?.VideoId;

            while (!_disposed)
            {
                // A queued video (e.g. the winner of a chat vote) goes first. It stays queued until it was
                // started, so an attempt that is cancelled by another skip does not lose it.
                var queued = _queuedVideoId;
                string? id = queued;
                if (id == null && !_videoSource.TryGetNextVideoId(after, out id))
                {
                    Logger.Log("No videos available to play.");
                    return !cancellationToken.IsCancellationRequested;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(DelayBeforeNextVideo, _time, cancellationToken);

                    await PlayVideoByIdAsync(id, cancellationToken);

                    if (queued != null && _queuedVideoId == queued)
                        _queuedVideoId = null;

                    cancellationToken.ThrowIfCancellationRequested();

                    return false;
                }
                // Only our own cancellation stops the retry loop; a timeout inside YouTube's HTTP client
                // also surfaces as an OperationCanceledException and has to move on to the next video.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.Log("PlayNextAsync operation was canceled.");
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Exception occurred in PlayNextAsync for video '{id}'.", ex);

                    // A queued video that cannot be played must not block the playback: continue with the regular next video.
                    if (queued != null)
                    {
                        if (_queuedVideoId == queued)
                            _queuedVideoId = null;
                    }
                    else
                    {
                        after = id;
                    }
                }
            }

            return false;
        }

        private async Task PlayVideoByIdAsync(string idOrUrl, CancellationToken cancellationToken)
        {
            Logger.Log($"Queuing new video with Id: {idOrUrl}");

            LoadingStarted?.Invoke(this, EventArgs.Empty);

            var video = await _youtube.ResolveAsync(idOrUrl, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Everything the player reports from here on belongs to the new video; late events of the
            // previous one are dropped (see PostFromPlayer). The watchdog starts over as well.
            Interlocked.Increment(ref _mediaGeneration);
            _failSafeTriggered = false;
            _failSafePlaybackTime = long.MinValue;

            _player.Play(video.VideoUrl, video.AudioUrl);

            Current = new NowPlaying(idOrUrl, video.Title, video.Duration);
            NowPlayingChanged?.Invoke(this, Current);
        }

        private void OnBuffering(float cache)
        {
            if (cache < 100)
            {
                SetBuffering(true);

                // Start timeout timer if not already running
                if (!_bufferingTimeoutTimer.IsRunning)
                    _bufferingTimeoutTimer.Start();
            }
            else
            {
                StopBufferingWatch();
            }
        }

        private void OnPlaying()
        {
            // The player may reset these when a new media starts, so re-apply the user's choice.
            _player.Volume = _volume;
            _player.Mute = _isMuted;
            StopBufferingWatch();
        }

        private void OnEncounteredError()
        {
            // Stream error; skip to next video silently
            Logger.Log("The player encountered an error; skipping to the next video.");
            StopBufferingWatch();
            PlayNextAsync().FireAndForget("PlayNextAsync after a player error failed.");
        }

        private void OnBufferingTimeout()
        {
            Logger.Log($"Buffering took longer than {BufferingTimeout.TotalSeconds:0} seconds; skipping to the next video.");
            StopBufferingWatch();
            PlayNextAsync().FireAndForget("PlayNextAsync after buffering timeout failed.");
        }

        private void OnFailSafeTick()
        {
            // While an operation is resolving the next video the player is idle by design, and the
            // operation has its own retry loop. Cancelling it here would only restart a slow load.
            if (_playNextTask != null)
            {
                _failSafeTriggered = false;
                return;
            }

            var currentPlaybackTime = _player.TimeMs;
            var isPlaying = _player.IsPlaying;

            if (!isPlaying || currentPlaybackTime == _failSafePlaybackTime)
            {
                // Only act if the problem is still there on the next check.
                if (!_failSafeTriggered)
                {
                    _failSafeTriggered = true;
                    return;
                }

                if (isPlaying)
                    Logger.Log("Media player state is playing, but video is frozen. Disconnect possible.");
                else
                    Logger.Log("Failstate triggered player state: " + _player.State);

                StopBufferingWatch();
                PlayNextAsync().FireAndForget("PlayNextAsync after fail-safe trigger failed.");
            }

            _failSafePlaybackTime = currentPlaybackTime;
            _failSafeTriggered = false;
        }

        private void StopBufferingWatch()
        {
            _bufferingTimeoutTimer.Stop();
            SetBuffering(false);
        }

        private void SetBuffering(bool isBuffering)
        {
            if (_isBuffering == isBuffering)
                return;

            _isBuffering = isBuffering;
            BufferingChanged?.Invoke(this, isBuffering);
        }

        /// <summary>
        /// Posts a player event onto the UI context, unless a newer video was started in the meantime.
        /// The generation is read when the event is raised (on the player's thread), so an error or
        /// end-of-video event of the previous media cannot skip the one that follows it.
        /// </summary>
        private void PostFromPlayer(Action action)
        {
            var generation = Volatile.Read(ref _mediaGeneration);
            Post(() =>
            {
                if (generation == Volatile.Read(ref _mediaGeneration))
                    action();
            });
        }

        private void Post(Action action)
        {
            _context.Post(_ =>
            {
                if (!_disposed)
                    action();
            }, null);
        }

        /// <summary>Stops playback and the watchdogs. The <see cref="IMediaPlayer"/> is disposed by its owner.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _playNextCancellationTokenSource?.Cancel();
                _bufferingTimeoutTimer.Dispose();
                _failSafeTimer.Dispose();
                _player.Stop();
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured while disposing the playback manager.", ex);
            }
        }
    }
}
