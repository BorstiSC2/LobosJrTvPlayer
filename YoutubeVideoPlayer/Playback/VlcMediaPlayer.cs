using System;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary><see cref="IMediaPlayer"/> on top of LibVLC. <c>Core.Initialize()</c> must have been called.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Play"/> and <see cref="Stop"/> run on the thread pool, one after the other. In LibVLC 3
    /// <c>Stop</c> waits for the video output, which lives in a child window of the UI thread, so calling
    /// it on the UI thread can deadlock. Because the commands are serialized, a stop that was requested
    /// before a play can never stop the media that the play started.
    /// </para>
    /// <para>If the media cannot be started, <see cref="EncounteredError"/> is raised.</para>
    /// </remarks>
    public sealed class VlcMediaPlayer : IMediaPlayer
    {
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);

        private readonly LibVLC _libVLC;
        private readonly object _commandGate = new();
        private Task _lastCommand = Task.CompletedTask;
        private Media? _currentMedia; // only touched by the serialized commands
        private volatile bool _disposed;

        public VlcMediaPlayer()
        {
            // - The title overlay would show the (very long) stream URL at the start of every video.
            // - After a slow start the first frames are "late". By default VLC drops late frames and skips
            //   frames when the decoder is behind; if that hits a keyframe, the frames that depend on it show
            //   as a flat grey picture with only motion outlines until the next keyframe. Decode everything.
            _libVLC = new LibVLC("--no-video-title-show", "--no-drop-late-frames", "--no-skip-frames");

            // Without this the video window keeps the keyboard and mouse for itself: the form's shortcuts
            // and the double-click on the video stop working, and VLC's own hotkeys (space = pause) work.
            MediaPlayer = new MediaPlayer(_libVLC)
            {
                EnableKeyInput = false,
                EnableMouseInput = false
            };

            MediaPlayer.EndReached += (_, __) => EndReached?.Invoke(this, EventArgs.Empty);
            MediaPlayer.Buffering += (_, e) => Buffering?.Invoke(this, e.Cache);
            MediaPlayer.Playing += (_, __) => Playing?.Invoke(this, EventArgs.Empty);
            MediaPlayer.EncounteredError += (_, __) => EncounteredError?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The LibVLC player, to attach to a <c>VideoView</c>.</summary>
        public MediaPlayer MediaPlayer { get; }

        public bool IsPlaying => !_disposed && MediaPlayer.State == VLCState.Playing;

        public string State => _disposed ? "Disposed" : MediaPlayer.State.ToString();

        public long TimeMs => _disposed ? -1 : MediaPlayer.Time;

        public long LengthMs => _disposed ? 0 : MediaPlayer.Length;

        public int Volume
        {
            get => MediaPlayer.Volume;
            set => MediaPlayer.Volume = value;
        }

        public bool Mute
        {
            get => MediaPlayer.Mute;
            set => MediaPlayer.Mute = value;
        }

        public event EventHandler? EndReached;

        public event EventHandler<float>? Buffering;

        public event EventHandler? Playing;

        public event EventHandler? EncounteredError;

        public void Play(string videoUrl, string? audioUrl)
        {
            if (_disposed)
                return;

            Enqueue(() => PlayCore(videoUrl, audioUrl));
        }

        public void Stop()
        {
            if (_disposed)
                return;

            Enqueue(StopCore);
        }

        /// <summary>Completes when all commands requested so far have run (for tests).</summary>
        internal Task WhenIdleAsync()
        {
            lock (_commandGate)
                return _lastCommand;
        }

        private void PlayCore(string videoUrl, string? audioUrl)
        {
            if (_disposed)
                return;

            try
            {
                // Stop whatever state the player is in (also opening, buffering or paused media).
                MediaPlayer.Stop();
                _currentMedia?.Dispose();
                _currentMedia = null;

                var media = new Media(_libVLC, videoUrl, FromType.FromLocation);
                _currentMedia = media;

                // If there is a separate audio stream, add it as an input-slave so libVLC plays video + audio together
                if (!string.IsNullOrEmpty(audioUrl))
                    media.AddOption($":input-slave={audioUrl}");

                // The stream URLs are long-lived HTTP downloads; reconnect after a dropped connection
                // instead of ending the video early.
                media.AddOption(":http-reconnect=true");

                if (!MediaPlayer.Play(media))
                    throw new InvalidOperationException("LibVLC could not start the media.");
            }
            catch (Exception ex)
            {
                // No event follows a failed start, so report it; the playback manager moves on.
                Logger.Log("Exception occured trying to play the media.", ex);
                EncounteredError?.Invoke(this, EventArgs.Empty);
            }
        }

        private void StopCore()
        {
            if (_disposed)
                return;

            try
            {
                MediaPlayer.Stop();
                _currentMedia?.Dispose();
                _currentMedia = null;
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured trying to stop the media.", ex);
            }
        }

        private void DisposeCore()
        {
            try
            {
                MediaPlayer.Stop();
                _currentMedia?.Dispose();
                _currentMedia = null;
                MediaPlayer.Dispose();
                _libVLC.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured while disposing the media player.", ex);
            }
        }

        /// <summary>Runs <paramref name="command"/> on the thread pool after all earlier commands.</summary>
        private Task Enqueue(Action command)
        {
            lock (_commandGate)
            {
                _lastCommand = _lastCommand.ContinueWith(
                    _ => command(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return _lastCommand;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Wait a moment so the natives are released before the process exits, but never hang on it.
            // DisposeCore bypasses the _disposed check on purpose; it is the last command.
            var disposal = Enqueue(DisposeCore);
            try
            {
                if (!disposal.Wait(DisposeTimeout))
                    Logger.Log("Disposing the media player took longer than expected; it continues in the background.");
            }
            catch (Exception ex)
            {
                Logger.Log("Exception occured while disposing the media player.", ex);
            }
        }
    }
}
