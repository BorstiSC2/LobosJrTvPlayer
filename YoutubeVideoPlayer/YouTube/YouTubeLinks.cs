using System;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>The links to a video or a playlist, the same whichever backend looks them up.</summary>
    public static class YouTubeLinks
    {
        /// <summary>The watch link of a video; a full URL is returned as it is.</summary>
        public static string Video(string videoIdOrUrl) =>
            videoIdOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? videoIdOrUrl
                : $"https://www.youtube.com/watch?v={videoIdOrUrl}";

        /// <summary>The link of a playlist, from its list ID.</summary>
        public static string Playlist(string playlistId) => $"https://www.youtube.com/playlist?list={playlistId}";
    }
}
