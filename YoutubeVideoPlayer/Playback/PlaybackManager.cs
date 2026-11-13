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

        /// <summary>
        /// A hold that is not released this long after its <see cref="IPlaybackHold.Until"/> is ignored, so a
        /// forgotten hold cannot keep the screen black.
        /// </summary>
        public static readonly TimeSpan MaxHoldOverrun = TimeSpan.FromSeconds(30);

        /// <summary>The longest wait for a hold whose end is not known.</summary>
        public static readonly TimeSpan MaxHoldWait = TimeSpan.FromMinutes(5);

        private readonly SynchronizationContext _context;
        private readonly IYouTubeService _youtube;
        private readonly IVideoSource _videoSource;
        private readonly IMediaPlayer _player;
        private readonly TimeProvider _time;
        private readonly ContextTimer _bufferingTimeoutTimer;
        private readonly ContextTimer _failSafeTimer;
        private readonly NextVideoPrefetcher _prefetcher;

        private CancellationTokenSource? _playNextCancellationTokenSource;
        private Task? _playNextTask;
        private string? _queuedVideoId;
        private PlaybackHold? _hold;
        private bool _isWaitingForHold;
        private DateTimeOffset _waitingSince;
        private bool _failSafeTriggered;
        private long _failSafePlaybackTime = long.MinValue;
        private int _mediaGeneration;
        private bool _isBuffering;
        private bool _isPaused;
        private bool _isStandby;
        private bool _standbyReady;
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

            _player.EndReached += (_, __) => PostFromPlayer(OnEndReached);
            _player.Buffering += (_, cache) => PostFromPlayer(() => OnBuffering(cache));
            _player.Playing += (_, __) => PostFromPlayer(OnPlaying);
            _player.EncounteredError += (_, __) => PostFromPlayer(OnEncounteredError);

            _bufferingTimeoutTimer = new ContextTimer(_context, BufferingTimeout, periodic: false, OnBufferingTimeout, _time);
            _failSafeTimer = new ContextTimer(_context, FailSafeInterval, periodic: true, OnFailSafeTick, _time);

            // While a video plays, the next one is chosen and resolved, so the change has no gap.
            _prefetcher = new NextVideoPrefetcher(_youtube, _videoSource, () => _queuedVideoId, _time);
            _prefetcher.PreparationFinished += (_, __) => OnPreparationFinished();
            _videoSource.SequenceChanged += OnSequenceChanged;
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

        /// <summary>
        /// The current video played to its end (the player reported the end; not a skip, an error or the watchdog).
        /// Raised before the next video is started.
        /// </summary>
        public event EventHandler<NowPlaying>? VideoFinished;

        /// <summary><c>true</c> while the player is buffering, <c>false</c> once it is done.</summary>
        public event EventHandler<bool>? BufferingChanged;

        public event EventHandler? VolumeChanged;

        /// <summary>The video source returned no video, so playback stopped.</summary>
        public event EventHandler? NoVideosAvailable;

        public void AdjustVolume(int delta) => Volume = _volume + delta;

        public void ToggleMute() => IsMuted = !_isMuted;

        /// <summary>Whether the current video was paused with <see cref="Pause"/> and not resumed yet.</summary>
        public bool IsPaused => _isPaused;

        /// <summary>
        /// Whether the first video is prepared but not started: <see cref="EnterStandby"/> was called and
        /// nothing has played since. <see cref="PlayNextAsync"/> (or a skip) starts it.
        /// </summary>
        public bool IsStandby => _isStandby;

        /// <summary>
        /// Whether, while standing by, the first video has been looked up, so Play starts it at once. It is also
        /// <c>true</c> if the lookup failed (Play then looks the video up again), so Play never stays blocked.
        /// </summary>
        public bool IsStandbyReady => _standbyReady;

        /// <summary>
        /// Whether the current video has ended and the next one waits for a hold (<see cref="HoldNextVideo"/>), e.g. a
        /// chat vote that runs longer than the video. The player is stopped meanwhile, so the picture is black.
        /// </summary>
        public bool IsWaitingForHold => _isWaitingForHold;

        /// <summary>While waiting for a hold: how much longer it is expected to last; <c>null</c> if that is not known (or nothing waits).</summary>
        public TimeSpan? HoldRemaining
        {
            get
            {
                if (!_isWaitingForHold || _hold?.Until is not { } until)
                    return null;

                var remaining = until - _time.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Raised when <see cref="IsStandby"/>, <see cref="IsStandbyReady"/>, <see cref="IsPaused"/> or
        /// <see cref="IsWaitingForHold"/> changed.
        /// </summary>
        public event EventHandler? StateChanged;

        /// <summary>
        /// Prepares the first video without playing it: it is picked and its streams are resolved in advance,
        /// like the next video during playback, so <see cref="PlayNextAsync"/> starts it without a lookup.
        /// Does nothing once a video was started.
        /// </summary>
        public void EnterStandby()
        {
            if (_disposed || Current != null || _isStandby)
                return;

            Logger.Log("Standing by: the first video is prepared, playback starts on request.");
            SetState(standby: true, paused: false);
            RestartPrefetch();
        }

        private void SetState(bool standby, bool paused)
        {
            var ready = standby && _standbyReady;
            if (_isStandby == standby && _isPaused == paused && _standbyReady == ready)
                return;

            _isStandby = standby;
            _isPaused = paused;
            _standbyReady = ready;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetStandbyReady(bool ready)
        {
            if (_standbyReady == ready)
                return;

            _standbyReady = ready;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnPreparationFinished()
        {
            if (!_disposed && _isStandby && Current == null)
                SetStandbyReady(true);
        }

        /// <summary>Prepares the video after the current one, or the first one while standing by.</summary>
        private void RestartPrefetch()
        {
            if (Current != null)
                _prefetcher.Start(Current.VideoId);
            else if (_isStandby)
            {
                SetStandbyReady(false);
                _prefetcher.StartFirst();
            }
            else
                _prefetcher.Cancel();
        }

        /// <summary>
        /// Pauses the current video. The watchdogs stand still meanwhile (a paused player is not "frozen"), and
        /// the pause ends by itself when another video starts.
        /// </summary>
        public void Pause()
        {
            if (_disposed || _isPaused || _isWaitingForHold || Current == null)
                return;

            StopBufferingWatch();
            _player.Pause();
            SetState(standby: false, paused: true);
        }

        /// <summary>Continues a video paused with <see cref="Pause"/>; does nothing if it is not paused.</summary>
        public void Resume()
        {
            if (_disposed || !_isPaused)
                return;

            _failSafeTriggered = false;
            _failSafePlaybackTime = long.MinValue;
            _player.Resume();
            SetState(standby: false, paused: false);
        }

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
        public Task PlayNextAsync()
        {
            // An explicit change does not wait: whatever held the next video back is over.
            EndHold();
            return RunPlaybackOperationAsync(PlayNextInternalAsync);
        }

        /// <summary>
        /// Plays a specific video (e.g. the winner of a chat vote). The current video keeps playing until
        /// the new one is ready. If the video cannot be played, the next video is played instead.
        /// </summary>
        public Task PlayVideoAsync(string videoId)
        {
            EndHold();
            return RunPlaybackOperationAsync(cancellationToken => PlayVideoInternalAsync(videoId, cancellationToken));
        }

        private async Task<bool> PlayVideoInternalAsync(string videoId, CancellationToken cancellationToken)
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
        }

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

            // The queued video is the one to prepare, not whatever was chosen before.
            RestartPrefetch();
        }

        public bool HasQueuedVideo => _queuedVideoId != null;

        /// <summary>Forgets a queued video, e.g. because the pool it came from was switched.</summary>
        public void ClearQueuedVideo()
        {
            _queuedVideoId = null;
            RestartPrefetch();
        }

        /// <summary>
        /// Keeps the next video from starting when the current one ends, until the returned hold is released. The hold
        /// belongs to the video that plays now: an explicit skip or another video ends it, and a newer hold replaces it.
        /// </summary>
        public IPlaybackHold HoldNextVideo()
        {
            var hold = new PlaybackHold(this, Current);
            if (!_disposed)
            {
                Logger.Log($"The video after \"{Current?.Title}\" waits until the hold is released.");
                _hold = hold;
            }

            return hold;
        }

        private void ReleaseHold(PlaybackHold hold)
        {
            if (_disposed || !ReferenceEquals(_hold, hold))
                return;

            _hold = null;
            if (_isWaitingForHold)
            {
                Logger.Log("The hold was released; playing the next video.");
                PlayNextAsync().FireAndForget("PlayNextAsync after a released hold failed.");
            }
        }

        /// <summary>Drops the hold and stops waiting for it.</summary>
        private void EndHold()
        {
            _hold = null;
            SetWaiting(false);
        }

        private void SetWaiting(bool waiting)
        {
            if (_isWaitingForHold == waiting)
                return;

            _isWaitingForHold = waiting;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Moves on after the current video ended or failed by itself: to the next video, or, if a hold for this video
        /// is active, into the wait for it (a black screen until the hold is released).
        /// </summary>
        private void AdvanceAutomatically(string failureMessage)
        {
            if (_hold is { } hold && Current != null && ReferenceEquals(hold.Video, Current))
            {
                if (!_isWaitingForHold)
                    EnterWaiting();
                return;
            }

            PlayNextAsync().FireAndForget(failureMessage);
        }

        private void EnterWaiting()
        {
            Logger.Log("The video is over; waiting for the hold to be released before the next video starts.");
            StopBufferingWatch();

            // Late events of the finished video must not start the next one.
            Interlocked.Increment(ref _mediaGeneration);
            try
            {
                _player.Stop(); // a black picture instead of the last frame
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured trying to stop the video before waiting for the hold.", ex);
            }

            _waitingSince = _time.GetUtcNow();
            _failSafeTriggered = false;
            SetState(standby: false, paused: false);
            SetWaiting(true);
        }

        private void OnSequenceChanged(object? sender, EventArgs e)
        {
            if (!_disposed)
                RestartPrefetch();
        }

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

                // A video that was chosen and resolved while the previous one played: no lookup, no waiting.
                var prefetched = _prefetcher.Take(after, queued);
                string? id = queued ?? prefetched?.VideoId;
                if (id == null && !_videoSource.TryGetNextVideoId(after, out id))
                {
                    Logger.Log("No videos available to play.");
                    return !cancellationToken.IsCancellationRequested;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The pause is a back-off between lookups; there is nothing to back off from here.
                    if (prefetched == null)
                        await Task.Delay(DelayBeforeNextVideo, _time, cancellationToken);

                    await PlayVideoByIdAsync(id, cancellationToken, prefetched?.Video);

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

        /// <param name="resolved">The streams if they were looked up in advance; otherwise they are looked up now.</param>
        private async Task PlayVideoByIdAsync(string idOrUrl, CancellationToken cancellationToken, Task<ResolvedVideo>? resolved = null)
        {
            Logger.Log($"Queuing new video with Id: {idOrUrl}");

            LoadingStarted?.Invoke(this, EventArgs.Empty);

            var video = resolved != null
                ? await resolved.WaitAsync(cancellationToken)
                : await _youtube.ResolveAsync(idOrUrl, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Everything the player reports from here on belongs to the new video; late events of the
            // previous one are dropped (see PostFromPlayer). The watchdog starts over as well.
            Interlocked.Increment(ref _mediaGeneration);
            _failSafeTriggered = false;
            _failSafePlaybackTime = long.MinValue;
            SetState(standby: false, paused: false);

            _player.Play(video.VideoUrl, video.AudioUrl);

            Current = new NowPlaying(idOrUrl, video.Title, video.Duration);
            NowPlayingChanged?.Invoke(this, Current);

            // Now that this video plays, choose and look up the one after it.
            _prefetcher.Start(idOrUrl);
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

        private void OnEndReached()
        {
            if (Current is { } finished)
                VideoFinished?.Invoke(this, finished);

            AdvanceAutomatically("PlayNextAsync after EndReached failed.");
        }

        private void OnEncounteredError()
        {
            // Stream error; skip to next video silently
            Logger.Log("The player encountered an error; skipping to the next video.");
            StopBufferingWatch();
            AdvanceAutomatically("PlayNextAsync after a player error failed.");
        }

        private void OnBufferingTimeout()
        {
            Logger.Log($"Buffering took longer than {BufferingTimeout.TotalSeconds:0} seconds; skipping to the next video.");
            StopBufferingWatch();
            AdvanceAutomatically("PlayNextAsync after buffering timeout failed.");
        }

        private void OnFailSafeTick()
        {
            // The player is stopped by design while it waits for a hold; only a hold that is never released is a problem.
            if (_isWaitingForHold)
            {
                var deadline = _hold?.Until is { } until ? until + MaxHoldOverrun : _waitingSince + MaxHoldWait;
                if (_time.GetUtcNow() > deadline)
                {
                    Logger.Log("The hold on the next video was not released in time; playing the next video.");
                    PlayNextAsync().FireAndForget("PlayNextAsync after an expired hold failed.");
                }

                return;
            }

            // While an operation is resolving the next video the player is idle by design, and the
            // operation has its own retry loop. Cancelling it here would only restart a slow load.
            if (_playNextTask != null || _isPaused)
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
                AdvanceAutomatically("PlayNextAsync after fail-safe trigger failed.");
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

        /// <summary>A hold handed out by <see cref="HoldNextVideo"/>; only the newest one counts.</summary>
        private sealed class PlaybackHold : IPlaybackHold
        {
            private readonly PlaybackManager _owner;

            public PlaybackHold(PlaybackManager owner, NowPlaying? video)
            {
                _owner = owner;
                Video = video;
            }

            /// <summary>The video whose end is held.</summary>
            public NowPlaying? Video { get; }

            public DateTimeOffset? Until { get; set; }

            public void Release() => _owner.ReleaseHold(this);
        }

        /// <summary>Stops playback and the watchdogs. The <see cref="IMediaPlayer"/> is disposed by its owner.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _videoSource.SequenceChanged -= OnSequenceChanged;
            _prefetcher.Dispose();

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
