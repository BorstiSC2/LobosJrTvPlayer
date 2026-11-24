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

        /// <summary>Whether a video is queued with <see cref="QueueNextVideo"/> and has not been started yet.</summary>
        bool HasQueuedVideo { get; }

        /// <summary>A new video started playing (it is <see cref="Current"/> now).</summary>
        event EventHandler<NowPlaying>? NowPlayingChanged;

        /// <summary>
        /// A video asked for with <see cref="PlayVideoAsync"/> or <see cref="QueueNextVideo"/> (a vote winner) did not play,
        /// and the regular next video plays instead: it could not be looked up, or it stopped by itself (player error,
        /// buffering timeout, broken stream, freeze) before it had played <see cref="PlaybackManager.FailedStartWindow"/>.
        /// The argument is its ID. Raised once the video that plays instead has started (after <see cref="NowPlayingChanged"/>),
        /// so it is <see cref="Current"/>. Not raised when an explicit choice replaced the winner (a skip or a playlist switch
        /// while it was looked up, another vote's winner).
        /// </summary>
        event EventHandler<string>? RequestedVideoFailed;

        /// <summary>
        /// Keeps the next video from starting when the current one ends, until the returned hold is released
        /// (a vote about the next video that lasts longer than the current video). Meanwhile the screen is black
        /// and <see cref="IsWaitingForHold"/> is <c>true</c>. An explicit skip or another video ends the hold.
        /// </summary>
        IPlaybackHold HoldNextVideo();

        /// <summary>Whether the current video has ended and the player waits for a hold (<see cref="HoldNextVideo"/>) to be released.</summary>
        bool IsWaitingForHold { get; }

        /// <summary>Whether the current video is paused.</summary>
        bool IsPaused { get; }

        /// <summary>Whether the first video is prepared but was not started yet.</summary>
        bool IsStandby { get; }

        /// <summary>Pauses the current video; does nothing without one.</summary>
        void Pause();

        /// <summary>Continues a paused video; does nothing if it is not paused.</summary>
        void Resume();

        /// <summary>Plays the next video (also starts the prepared first video while standing by).</summary>
        Task PlayNextAsync();

        /// <summary>
        /// Gets fresh stream links for the current video and continues it a few seconds before where it is (<c>!reload</c>):
        /// a safety measure when picture or sound misbehave. It replies at once; the reload runs in the background.
        /// </summary>
        ReloadResult Reload();

        /// <summary>Where a video that is being resumed or reloaded continues; <c>null</c> while none is.</summary>
        TimeSpan? ResumePosition { get; }
    }

    /// <summary>What <see cref="IPlaybackControl.Reload"/> did.</summary>
    public enum ReloadResult
    {
        /// <summary>The reload runs.</summary>
        Started,

        /// <summary>No video plays (none yet, standing by, or the next one waits for a vote).</summary>
        NothingPlaying,

        /// <summary>A video is being loaded right now (a skip, a resume, or another reload).</summary>
        Busy
    }

    /// <summary>A hold on the change to the next video, see <see cref="IPlaybackControl.HoldNextVideo"/>.</summary>
    public interface IPlaybackHold
    {
        /// <summary>
        /// When the hold is expected to be released (on the player's clock), shown as a countdown while the player
        /// waits; <c>null</c> while it is not known yet.
        /// </summary>
        DateTimeOffset? Until { get; set; }

        /// <summary>Lets the player continue: if it is already waiting, the next video starts now. Later calls do nothing.</summary>
        void Release();
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
