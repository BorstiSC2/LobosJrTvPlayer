using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary>The video that is currently loaded into the player.</summary>
    /// <param name="VideoId">The YouTube ID or URL the video was started from.</param>
    /// <param name="Title">The video title from YouTube.</param>
    /// <param name="Duration">The duration reported by YouTube; <c>null</c> for live streams or if unknown.</param>
    public sealed record NowPlaying(string VideoId, string Title, TimeSpan? Duration);

    /// <summary>The part of the player that chat features need (implemented by <see cref="PlaybackManager"/>).</summary>
    public interface IPlaybackControl
    {
        NowPlaying? Current { get; }

        bool IsPlaying { get; }

        TimeSpan Position { get; }

        /// <summary>Length of the current video, or <c>null</c> if unknown.</summary>
        TimeSpan? Length { get; }

        /// <summary>Stops the current video immediately and starts the next one (a random one if the playlist is shuffled).</summary>
        Task SkipAsync();

        /// <summary>Plays a specific video, falling back to the next one if it cannot be played.</summary>
        Task PlayVideoAsync(string videoId);

        /// <summary>
        /// Makes the given video the one that plays after the current video ends (or is skipped), instead
        /// of the regular next one. A later call replaces an earlier one; the current video keeps playing.
        /// </summary>
        void QueueNextVideo(string videoId);
    }

    /// <summary>
    /// Supplies the IDs of the videos the player picks from, in the order of the playing playlist:
    /// random if it is shuffled, otherwise one after the other (starting over after the last one).
    /// </summary>
    public interface IVideoSource
    {
        /// <summary>
        /// Picks the video that follows <paramref name="currentVideoId"/>: a random one if the playlist is
        /// shuffled, otherwise the next one in playlist order. Without a current video (or one that is not in
        /// the playlist) an ordered playlist starts at its first video.
        /// </summary>
        bool TryGetNextVideoId(string? currentVideoId, [NotNullWhen(true)] out string? videoId);

        /// <summary>
        /// Returns up to <paramref name="count"/> different videos that could follow
        /// <paramref name="currentVideoId"/>, never including it: random ones if the playlist is shuffled,
        /// otherwise the next ones in playlist order.
        /// </summary>
        IReadOnlyList<string> GetNextVideoIds(int count, string? currentVideoId);

        /// <summary>
        /// Raised when the videos or their order changed (another playlist, an update, a different
        /// shuffle setting), so a choice that was made in advance is out of date.
        /// </summary>
        event EventHandler? SequenceChanged;
    }
}
