using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>Title and duration of a video.</summary>
    /// <param name="Duration"><c>null</c> for live streams or if unknown.</param>
    public sealed record YouTubeVideoDetails(string Title, TimeSpan? Duration);

    /// <summary>A video with the stream URLs to play.</summary>
    /// <param name="AudioUrl">A separate audio stream to play along, or <c>null</c> for a muxed stream.</param>
    public sealed record ResolvedVideo(string Title, TimeSpan? Duration, string VideoUrl, string? AudioUrl);

    /// <summary>
    /// The YouTube operations the app needs. Implemented by <see cref="YouTubeService"/> on top of
    /// YoutubeExplode; an interface so the managers can be tested without network access.
    /// </summary>
    public interface IYouTubeService
    {
        /// <summary>Gets title and duration of a video (ID or URL).</summary>
        Task<YouTubeVideoDetails> GetVideoAsync(string videoIdOrUrl, CancellationToken cancellationToken);

        /// <summary>Gets the video's metadata and selects the best streams to play.</summary>
        Task<ResolvedVideo> ResolveAsync(string videoIdOrUrl, CancellationToken cancellationToken);

        Task<string> GetPlaylistTitleAsync(string playlistId, CancellationToken cancellationToken);

        /// <summary>Gets the IDs of all videos in a playlist, in playlist order.</summary>
        Task<IReadOnlyList<string>> GetPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken);
    }
}
