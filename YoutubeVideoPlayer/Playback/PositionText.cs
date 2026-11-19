using System;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary>Positions in a video as people read them.</summary>
    public static class PositionText
    {
        /// <summary><c>50:31</c>, or <c>1:05:09</c> from an hour on.</summary>
        public static string Format(TimeSpan position) =>
            position.TotalHours >= 1 ? position.ToString(@"h\:mm\:ss") : position.ToString(@"m\:ss");
    }
}
