using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>The part of yt-dlp's <c>-J</c> output for a video that the app reads.</summary>
    internal sealed class YtDlpVideoInfo
    {
        [JsonProperty("title")]
        public string? Title { get; set; }

        /// <summary>Seconds; missing for live streams.</summary>
        [JsonProperty("duration")]
        public double? Duration { get; set; }

        [JsonProperty("is_live")]
        public bool? IsLive { get; set; }

        [JsonProperty("formats")]
        public List<YtDlpFormat?>? Formats { get; set; }

        /// <summary>The length; <c>null</c> for a live stream or when yt-dlp does not know it.</summary>
        public TimeSpan? Length =>
            IsLive != true && Duration is > 0 and < 1e9 ? TimeSpan.FromSeconds(Duration.Value) : null;
    }

    /// <summary>One entry of <c>formats</c>.</summary>
    internal sealed class YtDlpFormat
    {
        [JsonProperty("format_id")]
        public string? FormatId { get; set; }

        [JsonProperty("format_note")]
        public string? FormatNote { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        /// <summary><c>https</c>, <c>m3u8_native</c>, <c>mhtml</c> (storyboards), ...</summary>
        [JsonProperty("protocol")]
        public string? Protocol { get; set; }

        /// <summary>The codec, or <c>none</c> for a stream without video.</summary>
        [JsonProperty("vcodec")]
        public string? VideoCodec { get; set; }

        /// <summary>The codec, or <c>none</c> for a stream without audio.</summary>
        [JsonProperty("acodec")]
        public string? AudioCodec { get; set; }

        [JsonProperty("height")]
        public int? Height { get; set; }

        /// <summary>Total bitrate in kbit/s (the only bitrate yt-dlp sets for YouTube).</summary>
        [JsonProperty("tbr")]
        public double? TotalBitrate { get; set; }

        [JsonProperty("language")]
        public string? Language { get; set; }

        /// <summary>10 for the video's original audio track, 5 for the default one, lower for others.</summary>
        [JsonProperty("language_preference")]
        public int? LanguagePreference { get; set; }

        /// <summary><c>true</c>, <c>false</c> or <c>"maybe"</c>.</summary>
        [JsonProperty("has_drm")]
        public JToken? HasDrm { get; set; }

        [JsonProperty("http_headers")]
        public Dictionary<string, string?>? HttpHeaders { get; set; }

        /// <summary>
        /// When YouTube starts serving the URL (Unix seconds): a few seconds after the lookup (measured: 6 s); before that
        /// it answers 403.
        /// </summary>
        [JsonProperty("available_at")]
        public double? AvailableAt { get; set; }

        public DateTimeOffset? AvailableFrom =>
            AvailableAt is > 0 and < 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(AvailableAt.Value * 1000)) : null;

        public string? UserAgent =>
            HttpHeaders?.FirstOrDefault(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>yt-dlp's <c>-J --flat-playlist</c> output for a playlist.</summary>
    internal sealed class YtDlpPlaylistInfo
    {
        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("entries")]
        public List<YtDlpPlaylistEntry?>? Entries { get; set; }
    }

    internal sealed class YtDlpPlaylistEntry
    {
        [JsonProperty("id")]
        public string? Id { get; set; }
    }

    /// <summary>A stream yt-dlp offers, with the User-Agent its URL is fetched with and when YouTube starts serving it.</summary>
    internal sealed record YtDlpStream(StreamCandidate Candidate, string? UserAgent, DateTimeOffset? AvailableFrom = null);

    /// <summary>Turns yt-dlp's formats into the streams <see cref="StreamSelector"/> chooses from.</summary>
    internal static class YtDlpFormats
    {
        private const int OriginalLanguage = 10;
        private const int DefaultLanguage = 5;

        /// <summary>
        /// The formats that can be played as they are: with a URL, without DRM, as a direct download (<c>https</c>); a
        /// stream with picture and sound may also be HLS (<c>m3u8</c>), which VLC plays as well and which
        /// <see cref="StreamSelector"/> only takes when there is nothing else. Left out: storyboards (no picture codec,
        /// no sound codec), the dynamic range compressed audio (<c>-drc</c>), AI-upscaled video (<c>-sr</c>) and streams
        /// yt-dlp marks as damaged.
        /// </summary>
        public static IReadOnlyList<YtDlpStream> ToStreams(IEnumerable<YtDlpFormat?>? formats)
        {
            var usable = (formats ?? Enumerable.Empty<YtDlpFormat?>())
                .OfType<YtDlpFormat>()
                .Where(IsUsable)
                .ToList();

            // The original track counts as the video's own; without one marked, the default track does.
            var ownTrack = usable.Any(f => f.LanguagePreference >= OriginalLanguage) ? OriginalLanguage : DefaultLanguage;

            var streams = new List<YtDlpStream>();
            foreach (var format in usable)
            {
                var hasVideo = !IsNone(format.VideoCodec);
                var hasAudio = !IsNone(format.AudioCodec);
                var kind = hasVideo && hasAudio ? StreamKind.Muxed : hasVideo ? StreamKind.VideoOnly : StreamKind.AudioOnly;
                if (!IsPlayableProtocol(format.Protocol, kind))
                    continue;

                var bitsPerSecond = (long)Math.Round((format.TotalBitrate ?? 0) * 1000);
                var candidate = kind switch
                {
                    StreamKind.AudioOnly => new StreamCandidate(
                        kind, format.Url!, bitsPerSecond, format.AudioCodec,
                        AudioLanguage: NullIfEmpty(format.Language),
                        IsDefaultAudio: format.LanguagePreference is { } preference ? preference >= ownTrack : null,
                        AudioLanguageName: NullIfEmpty(format.FormatNote)),
                    _ => new StreamCandidate(kind, format.Url!, bitsPerSecond, format.VideoCodec, format.Height)
                };
                streams.Add(new YtDlpStream(candidate, format.UserAgent, format.AvailableFrom));
            }

            return streams;
        }

        private static bool IsUsable(YtDlpFormat format)
        {
            if (string.IsNullOrWhiteSpace(format.Url))
                return false;

            // has_drm is true, false or "maybe"; only a clear false (or nothing) is playable.
            if (format.HasDrm is { Type: not JTokenType.Null } drm && !(drm.Type == JTokenType.Boolean && !drm.Value<bool>()))
                return false;

            if (IsNone(format.VideoCodec) && IsNone(format.AudioCodec))
                return false;

            var id = format.FormatId ?? string.Empty;
            if (id.Split('-').Skip(1).Any(part => part.Equals("drc", StringComparison.OrdinalIgnoreCase)
                                                   || part.Equals("sr", StringComparison.OrdinalIgnoreCase)))
                return false;

            var note = format.FormatNote ?? string.Empty;
            return !note.Contains("DRC", StringComparison.Ordinal)
                   && !note.Contains("upscaled", StringComparison.OrdinalIgnoreCase)
                   && !note.Contains("DAMAGED", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlayableProtocol(string? protocol, StreamKind kind)
        {
            var value = (protocol ?? "https").ToLowerInvariant();
            if (value is "https" or "http")
                return true;

            return kind == StreamKind.Muxed && value is "m3u8_native" or "m3u8";
        }

        private static bool IsNone(string? codec) => string.Equals(codec, "none", StringComparison.OrdinalIgnoreCase);

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
