using System;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary>
    /// The media player <see cref="PlaybackManager"/> drives. Implemented by <see cref="VlcMediaPlayer"/>;
    /// an interface so the playback logic can be tested without LibVLC.
    /// </summary>
    /// <remarks>Events may be raised on any thread.</remarks>
    public interface IMediaPlayer : IDisposable
    {
        bool IsPlaying { get; }

        /// <summary>A readable description of the player state (for logging).</summary>
        string State { get; }

        /// <summary>Current position in milliseconds (negative if unknown).</summary>
        long TimeMs { get; }

        /// <summary>Length of the current media in milliseconds (0 or negative if unknown).</summary>
        long LengthMs { get; }

        int Volume { get; set; }

        bool Mute { get; set; }

        /// <summary>
        /// Stops the current media (if any) and plays the given streams. The call may return before
        /// playback has started; if the media cannot be started, <see cref="EncounteredError"/> is raised.
        /// </summary>
        /// <param name="audioUrl">A separate audio stream played along with the video, or <c>null</c>.</param>
        void Play(string videoUrl, string? audioUrl);

        /// <summary>Pauses the current media (it keeps its position). Does nothing if nothing is loaded.</summary>
        void Pause();

        /// <summary>Continues media that was paused with <see cref="Pause"/>.</summary>
        void Resume();

        /// <summary>Stops playback and releases the current media. The call may return before playback has stopped.</summary>
        void Stop();

        event EventHandler? EndReached;

        /// <summary>Buffering progress in percent (100 = done).</summary>
        event EventHandler<float>? Buffering;

        event EventHandler? Playing;

        event EventHandler? EncounteredError;
    }
}
