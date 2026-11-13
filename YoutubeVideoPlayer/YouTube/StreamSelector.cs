using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTubeVideoPlayer.YouTube
{
    public enum StreamKind
    {
        VideoOnly,
        AudioOnly,
        Muxed
    }

    /// <summary>One stream offered by YouTube.</summary>
    /// <param name="Codec">The codec string from the manifest (e.g. <c>avc1.640028</c>, <c>vp9</c>, <c>vp9.2</c>, <c>av01.0.12M.10</c>), if known.</param>
    /// <param name="Height">The video height in pixels, if known.</param>
    public sealed record StreamCandidate(StreamKind Kind, string Url, long BitsPerSecond, string? Codec = null, int? Height = null)
    {
        /// <summary>A short description for the log, e.g. <c>avc1.640028 1080p 4500 kbit/s</c>.</summary>
        public string Describe() =>
            $"{Codec ?? "unknown codec"}{(Height is { } h ? $" {h}p" : string.Empty)} {BitsPerSecond / 1000} kbit/s";
    }

    /// <summary>The streams to play: a video stream plus an optional separate audio stream.</summary>
    public sealed record SelectedStreams(string VideoUrl, string? AudioUrl);

    /// <summary>Chooses which of the offered streams to play.</summary>
    public static class StreamSelector
    {
        /// <summary>
        /// The tallest video that is played. Bigger streams (4K and up) are not worth the decoding effort.
        /// YouTube only offers H.264 up to 1080p: 1440p comes as VP9 or AV1.
        /// </summary>
        public const int MaxHeight = 1440;

        /// <summary>
        /// Prefers a video-only stream together with the highest-bitrate audio-only stream (played as VLC
        /// input slave). The video-only stream is chosen like this:
        /// <list type="number">
        /// <item>HDR streams (VP9 profile 2, 10-bit AV1) are only used if there is nothing else: VLC 3 cannot tone-map
        /// them and shows washed-out colours.</item>
        /// <item>The tallest picture up to <see cref="MaxHeight"/> wins (a stream without a known height counts as the
        /// lowest). If everything is taller, the lowest of those is taken.</item>
        /// <item>At that height the codec that is easiest to decode wins: H.264, then VP9, then 8-bit AV1.</item>
        /// <item>Then the highest bitrate.</item>
        /// </list>
        /// Falls back to the highest-bitrate muxed stream if there is no video-only stream.
        /// </summary>
        /// <exception cref="InvalidOperationException">No playable stream is available.</exception>
        public static SelectedStreams Select(IEnumerable<StreamCandidate> streams)
        {
            var list = streams.ToList();

            var video = BestVideo(list);
            if (video != null)
                return new SelectedStreams(video.Url, HighestBitrate(list.Where(s => s.Kind == StreamKind.AudioOnly))?.Url);

            var muxed = HighestBitrate(list.Where(s => s.Kind == StreamKind.Muxed))
                ?? throw new InvalidOperationException("No muxed stream found");
            return new SelectedStreams(muxed.Url, null);
        }

        /// <summary>The video-only stream <see cref="Select"/> would pick, or <c>null</c> if there is none.</summary>
        public static StreamCandidate? BestVideo(IReadOnlyCollection<StreamCandidate> streams)
        {
            var videos = streams.Where(s => s.Kind == StreamKind.VideoOnly).ToList();
            if (videos.Count == 0)
                return null;

            // HDR only as a last resort.
            var pool = videos.Where(s => !IsHdr(s)).ToList();
            if (pool.Count == 0)
                pool = videos;

            // The tallest picture up to the limit; if everything is taller, the lowest of those.
            var heights = pool.Select(s => s.Height ?? 0).Distinct().ToList();
            var height = heights.Where(h => h <= MaxHeight).DefaultIfEmpty(heights.Min()).Max();

            return pool
                .Where(s => (s.Height ?? 0) == height)
                .OrderBy(CodecRank)
                .ThenByDescending(s => s.BitsPerSecond)
                .First();
        }

        /// <summary>0 = easiest to decode. Streams without a codec come after the known SDR codecs.</summary>
        private static int CodecRank(StreamCandidate stream)
        {
            var codec = stream.Codec;
            if (codec == null)
                return 3;

            if (codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase))
                return 1;

            if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase))
                return 2;

            return 3;
        }

        /// <summary>VP9 profile 2 ("vp9.2", "vp09.02...") and AV1 with 10 bits ("av01.0.12M.10...") carry HDR.</summary>
        private static bool IsHdr(StreamCandidate stream)
        {
            var codec = stream.Codec;
            if (codec == null)
                return false;

            if (codec.StartsWith("vp9.2", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("vp09.02", StringComparison.OrdinalIgnoreCase))
                return true;

            // av01.<profile>.<level><tier>.<bit depth>...
            if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase))
            {
                var parts = codec.Split('.');
                return parts.Length > 3 && parts[3] != "08";
            }

            return false;
        }

        private static StreamCandidate? HighestBitrate(IEnumerable<StreamCandidate> streams) =>
            streams.OrderByDescending(s => s.BitsPerSecond).FirstOrDefault();
    }
}
