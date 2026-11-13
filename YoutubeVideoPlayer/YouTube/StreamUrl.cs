using System;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>Helpers for the stream URLs that YouTube hands out.</summary>
    public static class StreamUrl
    {
        /// <summary>
        /// The time at which a stream URL stops working (its <c>expire</c> query parameter, about six hours
        /// after it was issued), or <c>null</c> if the URL does not say.
        /// </summary>
        public static DateTimeOffset? GetExpiry(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || pair[0] != "expire" || !long.TryParse(pair[1], out var seconds) || seconds <= 0)
                    continue;

                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            return null;
        }
    }
}
