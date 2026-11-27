using System;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>What looks up videos, streams and playlists on YouTube (<c>youtube.backend</c> in <c>appsettings.json</c>).</summary>
    public enum YouTubeBackend
    {
        /// <summary>The YoutubeExplode library, inside the app (the default).</summary>
        YoutubeExplode,

        /// <summary>yt-dlp.exe, run as a separate process for every lookup.</summary>
        YtDlp
    }

    public static class YouTubeBackendNames
    {
        /// <summary>The name used in the log and in messages.</summary>
        public static string DisplayName(this YouTubeBackend backend) => backend switch
        {
            YouTubeBackend.YtDlp => "yt-dlp",
            _ => "YoutubeExplode"
        };

        /// <summary>The value written to <c>appsettings.json</c>.</summary>
        public static string SettingValue(this YouTubeBackend backend) => backend switch
        {
            YouTubeBackend.YtDlp => "ytDlp",
            _ => "youtubeExplode"
        };

        /// <summary>
        /// The backend a setting names: <c>youtubeExplode</c> or <c>ytDlp</c>, in any case, also written <c>yt-dlp</c> or
        /// <c>youtube_explode</c>. <c>null</c> for anything else.
        /// </summary>
        public static YouTubeBackend? Parse(string? value)
        {
            var key = (value ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
            if (key.Equals("youtubeexplode", StringComparison.OrdinalIgnoreCase))
                return YouTubeBackend.YoutubeExplode;
            if (key.Equals("ytdlp", StringComparison.OrdinalIgnoreCase))
                return YouTubeBackend.YtDlp;
            return null;
        }
    }
}
