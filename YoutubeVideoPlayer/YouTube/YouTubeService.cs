using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary><see cref="IYouTubeService"/> on top of YoutubeExplode (no official API key needed).</summary>
    public sealed class YouTubeService : IYouTubeService
    {
        private readonly YoutubeClient _yt;

        public YouTubeService(YoutubeClient youtubeClient)
        {
            _yt = youtubeClient;
        }

        public async Task<YouTubeVideoDetails> GetVideoAsync(string videoIdOrUrl, CancellationToken cancellationToken)
        {
            var video = await _yt.Videos.GetAsync(ToUrl(videoIdOrUrl), cancellationToken);
            return new YouTubeVideoDetails(video.Title ?? string.Empty, video.Duration);
        }

        public async Task<ResolvedVideo> ResolveAsync(string videoIdOrUrl, CancellationToken cancellationToken)
        {
            var url = ToUrl(videoIdOrUrl);

            // Get video metadata (to show title) and resolve stream manifest
            var video = await _yt.Videos.GetAsync(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = await _yt.Videos.Streams.GetManifestAsync(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var streams = SelectStreams(manifest, videoIdOrUrl);
            return new ResolvedVideo(video.Title ?? string.Empty, video.Duration, streams.VideoUrl, streams.AudioUrl);
        }

        public async Task<string> GetPlaylistTitleAsync(string playlistId, CancellationToken cancellationToken)
        {
            var playlist = await _yt.Playlists.GetAsync(playlistId, cancellationToken);
            return playlist.Title;
        }

        public async Task<IReadOnlyList<string>> GetPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            await foreach (var video in _yt.Playlists.GetVideosAsync(playlistId, cancellationToken))
                ids.Add(video.Id.Value);

            return ids;
        }

        private static SelectedStreams SelectStreams(StreamManifest manifest, string videoIdOrUrl)
        {
            var candidates = new List<StreamCandidate>();

            try
            {
                candidates.AddRange(manifest.GetVideoOnlyStreams()
                    .Select(s => new StreamCandidate(
                        StreamKind.VideoOnly, s.Url, s.Bitrate.BitsPerSecond, s.VideoCodec, s.VideoQuality.MaxHeight)));
                candidates.AddRange(manifest.GetAudioOnlyStreams()
                    .Select(s => new StreamCandidate(StreamKind.AudioOnly, s.Url, s.Bitrate.BitsPerSecond, s.AudioCodec)));
            }
            catch (Exception ex)
            {
                // If the API throws for any reason, fall back to the muxed streams below.
                Logger.Log("Exception occured trying to assign video and audio streams.", ex);
                candidates.Clear();
            }

            candidates.AddRange(manifest.GetMuxedStreams()
                .Select(s => new StreamCandidate(
                    StreamKind.Muxed, s.Url, s.Bitrate.BitsPerSecond, s.VideoCodec, s.VideoQuality.MaxHeight)));

            var selected = StreamSelector.Select(candidates);

            // Visual glitches are tied to the codec and resolution, so keep a record of what was played.
            var video = candidates.FirstOrDefault(c => c.Url == selected.VideoUrl);
            var audio = candidates.FirstOrDefault(c => c.Url == selected.AudioUrl);
            Logger.Log($"Streams for '{videoIdOrUrl}': video {video?.Describe() ?? "?"}"
                + (audio != null ? $", audio {audio.Describe()}" : ", no separate audio"));

            return selected;
        }

        /// <summary>Accepts either a raw video ID or a full URL.</summary>
        public static string ToUrl(string videoIdOrUrl) =>
            videoIdOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? videoIdOrUrl
                : $"https://www.youtube.com/watch?v={videoIdOrUrl}";
    }
}
