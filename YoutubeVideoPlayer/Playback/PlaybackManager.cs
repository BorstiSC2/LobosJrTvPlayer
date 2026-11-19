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
    /// <para>
    /// A video whose stream breaks off (it freezes, or the player reports its end well before its length) is resumed
    /// first: it is looked up again (fresh stream URLs) and started a little before where it stopped. Only after
    /// <see cref="MaxResumeAttempts"/> resumes in a row that did not get it going does it move on to the next video.
    /// </para>
    /// <para>
    /// Every automatic move is logged with its reason, and a broken stream also with the player's own recent
    /// warnings and errors (<see cref="IMediaPlayer.RecentLog"/>).
    /// </para>
    /// </remarks>
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

        /// <summary>
        /// An end reported this long (or longer) before the video's known length is not its real end: the stream broke
        /// off. More than a <see cref="FailSafeInterval"/>, because the position is only known from the watchdog's checks.
        /// </summary>
        public static readonly TimeSpan EarlyEndMargin = TimeSpan.FromSeconds(60);

        /// <summary>How often in a row a broken video is resumed before the next video plays instead.</summary>
        public const int MaxResumeAttempts = 2;

        /// <summary>A resume worked once the video has played this far past the point it resumed at.</summary>
        public static readonly TimeSpan ResumeSucceededAfter = TimeSpan.FromMinutes(1);

        /// <summary>A resume starts this much before the last known position, so nothing is missed.</summary>
        public static readonly TimeSpan ResumeRewind = TimeSpan.FromSeconds(3);

        /// <summary>How often a resumed video is checked for having reached its start point (ends <see cref="IsResuming"/>).</summary>
        public static readonly TimeSpan ResumeCheckInterval = TimeSpan.FromMilliseconds(250);

        private readonly SynchronizationContext _context;
        private readonly IYouTubeService _youtube;
        private readonly IVideoSource _videoSource;
        private readonly IMediaPlayer _player;
        private readonly TimeProvider _time;
        private readonly ContextTimer _bufferingTimeoutTimer;
        private readonly ContextTimer _failSafeTimer;
        private readonly ContextTimer _resumeCheckTimer;
        private readonly NextVideoPrefetcher _prefetcher;

        private CancellationTokenSource? _playNextCancellationTokenSource;
        private Task? _playNextTask;
        private string? _queuedVideoId;
        private PlaybackHold? _hold;
        private bool _isWaitingForHold;
        private DateTimeOffset _waitingSince;
        private bool _failSafeTriggered;
        private long _failSafePlaybackTime = long.MinValue;
        private long _lastKnownPositionMs = -1;
        private int _resumeAttempts;
        private long _resumedAtMs;
        private bool _isResuming;
        private bool _isReloading;
        private long _resumeCheckPositionMs = -1;
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
            _resumeCheckTimer = new ContextTimer(_context, ResumeCheckInterval, periodic: true, OnResumeCheckTick, _time);

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

        /// <summary>
        /// Whether the stream of the current video broke off and it is being resumed: from the moment that was detected
        /// until the video plays again at <see cref="ResumePosition"/> (or another video starts, or it gives up).
        /// </summary>
        public bool IsResuming => _isResuming;

        /// <summary>Where the video that is being resumed continues; <c>null</c> while nothing is resumed.</summary>
        public TimeSpan? ResumePosition => _isResuming ? TimeSpan.FromMilliseconds(_resumedAtMs) : null;

        /// <summary>Whether the resume that runs was asked for with <see cref="Reload"/> (not a broken stream).</summary>
        public bool IsReloading => _isResuming && _isReloading;

        /// <summary>
        /// Gets fresh stream links for the current video and continues it <see cref="ResumeRewind"/> before where it is,
        /// like the resume of a broken stream, but asked for (<c>!reload</c>). It does not count as one of the
        /// <see cref="MaxResumeAttempts"/>, and a paused video plays on.
        /// </summary>
        public ReloadResult Reload()
        {
            if (_disposed || Current is not { } video || _isStandby || _isWaitingForHold)
                return ReloadResult.NothingPlaying;

            // A skip, a resume or another reload is looking a video up; this one would cancel it.
            if (_playNextTask != null)
                return ReloadResult.Busy;

            // While a resume waits for the video to play on, the player does not know the position (it seeks).
            var position = _isResuming ? _resumedAtMs + (long)ResumeRewind.TotalMilliseconds
                : _player.TimeMs > 0 ? _player.TimeMs
                : Math.Max(0, _lastKnownPositionMs);
            StopBufferingWatch();
            StartResume(video, position, reload: true);
            return ReloadResult.Started;
        }

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
        /// Raised when <see cref="IsStandby"/>, <see cref="IsStandbyReady"/>, <see cref="IsPaused"/>,
        /// <see cref="IsWaitingForHold"/> or <see cref="IsResuming"/> changed.
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
            // An explicit change does not wait: whatever held the next video back is over, and so is a resume.
            EndHold();
            SetResuming(false);
            return RunPlaybackOperationAsync(PlayNextInternalAsync);
        }

        /// <summary>
        /// Plays a specific video (e.g. the winner of a chat vote). The current video keeps playing until
        /// the new one is ready. If the video cannot be played, the next video is played instead.
        /// </summary>
        public Task PlayVideoAsync(string videoId)
        {
            EndHold();
            SetResuming(false);
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
            SetResuming(false);
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
                : await VideoLookup.ResolveAsync(_youtube, idOrUrl, _time, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Everything the player reports from here on belongs to the new video; late events of the
            // previous one are dropped (see PostFromPlayer). The watchdog starts over as well.
            Interlocked.Increment(ref _mediaGeneration);
            _failSafeTriggered = false;
            _failSafePlaybackTime = long.MinValue;
            _lastKnownPositionMs = -1;
            _resumeAttempts = 0;
            SetResuming(false);
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
            var video = Current;
            var position = Math.Max(_player.TimeMs, _lastKnownPositionMs);
            var length = video?.Duration ?? (_player.LengthMs > 0 ? TimeSpan.FromMilliseconds(_player.LengthMs) : null);
            var where = $"at {FormatTime(position)} of {(length is { } known ? FormatTime((long)known.TotalMilliseconds) : "an unknown length")}";

            if (video != null && position > 0 && length is { } total
                && TimeSpan.FromMilliseconds(position) < total - EarlyEndMargin)
            {
                // Not the real end: the stream broke off (e.g. the connection was closed and could not be reopened).
                LogPlayerProblem($"The player reported the end of \"{video.Title}\" ({video.VideoId}) {where}: the stream broke off early.");
                StopBufferingWatch();
                if (TryResume())
                    return;

                AdvanceAutomatically("PlayNextAsync after an early end failed.");
                return;
            }

            Logger.Log(video == null
                ? "The player reported the end of the media; playing the next video."
                : $"\"{video.Title}\" ({video.VideoId}) ended {where}; playing the next video.");
            if (video != null)
                VideoFinished?.Invoke(this, video);

            AdvanceAutomatically("PlayNextAsync after EndReached failed.");
        }

        private void OnEncounteredError()
        {
            // Stream error; skip to next video silently
            LogPlayerProblem("The player encountered an error; skipping to the next video.");
            StopBufferingWatch();
            AdvanceAutomatically("PlayNextAsync after a player error failed.");
        }

        private void OnBufferingTimeout()
        {
            LogPlayerProblem($"Buffering took longer than {BufferingTimeout.TotalSeconds:0} seconds; skipping to the next video.");
            StopBufferingWatch();
            AdvanceAutomatically("PlayNextAsync after buffering timeout failed.");
        }

        /// <summary>Logs <paramref name="message"/> with the player's own recent warnings and errors.</summary>
        private void LogPlayerProblem(string message)
        {
            var recent = _player.RecentLog();
            Logger.Log(recent.Length == 0
                ? message + Environment.NewLine + "The player logged no warnings or errors."
                : message + Environment.NewLine + "The player's recent warnings and errors:" + Environment.NewLine
                    + string.Join(Environment.NewLine, recent));
        }

        private static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0)
                return "an unknown position";

            return PositionText.Format(TimeSpan.FromMilliseconds(milliseconds));
        }

        /// <summary>
        /// Continues the current video a little before where its stream broke off: it is looked up again (fresh
        /// stream URLs) and started there. Returns <c>false</c> (move on instead) if nothing is known about where it
        /// was, or if it was already resumed <see cref="MaxResumeAttempts"/> times in a row without getting going.
        /// </summary>
        private bool TryResume()
        {
            if (Current is not { } video || _lastKnownPositionMs <= 0)
                return false;

            if (_resumeAttempts >= MaxResumeAttempts)
            {
                Logger.Log($"\"{video.Title}\" was resumed {_resumeAttempts} times in a row without playing on; moving on to the next video.");
                SetResuming(false);
                return false;
            }

            _resumeAttempts++;
            StartResume(video, _lastKnownPositionMs, reload: false);
            return true;
        }

        /// <summary>Looks the video up again and continues it <see cref="ResumeRewind"/> before <paramref name="positionMs"/>.</summary>
        private void StartResume(NowPlaying video, long positionMs, bool reload)
        {
            var startAt = Math.Max(0, positionMs - (long)ResumeRewind.TotalMilliseconds);
            _resumedAtMs = startAt;

            // The old stream stops where the label appears (a reload would otherwise play on for the seconds the lookup
            // takes, and then jump back). A paused stream does not move, so movement means the new one plays.
            try
            {
                _player.Pause();
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured trying to pause the video before resuming it.", ex);
            }

            SetResuming(true, reload);
            Logger.Log(reload
                ? $"Reloading \"{video.Title}\" ({video.VideoId}) with fresh stream links at {FormatTime(startAt)}."
                : $"Resuming \"{video.Title}\" ({video.VideoId}) at {FormatTime(startAt)} (attempt {_resumeAttempts} of {MaxResumeAttempts}).");

            // Late events of the old stream must not start another resume or the next video.
            Interlocked.Increment(ref _mediaGeneration);
            _failSafeTriggered = false;
            _failSafePlaybackTime = long.MinValue;

            RunPlaybackOperationAsync(token => ResumeInternalAsync(video, TimeSpan.FromMilliseconds(startAt), token))
                .FireAndForget(reload ? "Reloading the video failed." : "Resuming the video failed.");
        }

        private async Task<bool> ResumeInternalAsync(NowPlaying video, TimeSpan startAt, CancellationToken cancellationToken)
        {
            try
            {
                var resolved = await VideoLookup.ResolveAsync(_youtube, video.VideoId, _time, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                Interlocked.Increment(ref _mediaGeneration);
                _failSafeTriggered = false;
                _failSafePlaybackTime = long.MinValue;
                SetState(standby: false, paused: false); // a reloaded video plays, also if it was paused
                _player.Play(resolved.VideoUrl, resolved.AudioUrl, startAt);

                // The label stays until the video really plays on there (VLC reports 0 while it seeks).
                _resumeCheckPositionMs = -1;
                _resumeCheckTimer.Start();
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Log("Resuming the video was canceled.");
                SetResuming(false);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Resuming \"{video.Title}\" ({video.VideoId}) failed; playing the next video instead.", ex);
                SetResuming(false);
                return await PlayNextInternalAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Ends <see cref="IsResuming"/> as soon as the resumed video plays: its position moves. Not the start point: VLC
        /// starts at the keyframe before it, a few seconds earlier (measured: 49:57 for 50:00), and would keep the label up
        /// for those seconds. Until the player has started the new stream, the old one may still report its position, but
        /// it does not move: it was paused when the resume began, or it froze or ended.
        /// </summary>
        private void OnResumeCheckTick()
        {
            if (!_isResuming)
            {
                _resumeCheckTimer.Stop();
                return;
            }

            var previous = _resumeCheckPositionMs;
            var position = _player.TimeMs;
            _resumeCheckPositionMs = position;
            if (_player.IsPlaying && previous > 0 && position > previous)
            {
                Logger.Log($"\"{Current?.Title}\" plays on at {FormatTime(position)}.");
                SetResuming(false);
            }
        }

        private void SetResuming(bool resuming, bool reload = false)
        {
            if (!resuming)
            {
                _resumeCheckTimer.Stop();
                reload = false;
            }

            if (_isResuming == resuming && _isReloading == reload)
                return;

            _isResuming = resuming;
            _isReloading = reload;
            StateChanged?.Invoke(this, EventArgs.Empty);
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

            // Where the video is, for a resume after its stream broke off (the player may not know it any more then).
            if (currentPlaybackTime > 0)
            {
                _lastKnownPositionMs = currentPlaybackTime;
                if (_resumeAttempts > 0 && currentPlaybackTime >= _resumedAtMs + (long)ResumeSucceededAfter.TotalMilliseconds)
                    _resumeAttempts = 0; // the last resume worked
            }

            if (!isPlaying || currentPlaybackTime == _failSafePlaybackTime)
            {
                // Only act if the problem is still there on the next check.
                if (!_failSafeTriggered)
                {
                    _failSafeTriggered = true;
                    return;
                }

                if (isPlaying)
                    LogPlayerProblem($"The player says it is playing, but the video is frozen at {FormatTime(currentPlaybackTime)}. Disconnect possible.");
                else
                    LogPlayerProblem($"The player stopped playing (state: {_player.State}) at {FormatTime(currentPlaybackTime)}.");

                StopBufferingWatch();
                if (TryResume())
                    return;

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
                _resumeCheckTimer.Dispose();
                _player.Stop();
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured while disposing the playback manager.", ex);
            }
        }
    }
}
