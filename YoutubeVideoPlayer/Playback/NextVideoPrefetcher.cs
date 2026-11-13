using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Playback
{
    /// <summary>A video that was chosen and resolved in advance.</summary>
    /// <param name="VideoId">The video that was chosen.</param>
    /// <param name="Video">Its streams; may still be running, so await it.</param>
    public sealed record PrefetchedVideo(string VideoId, Task<ResolvedVideo> Video);

    /// <summary>
    /// Closes the gap between two videos: shortly after a video has started, it picks the video that comes
    /// next and resolves its streams, so that the change only has to start the player. Looking up a video
    /// on YouTube takes two to three seconds, which is otherwise a black screen between the videos.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pick is made once and then kept (for a shuffled playlist it is a random video), so the video that
    /// was prepared is the one that plays. A video that was queued (e.g. a vote winner) is preferred.
    /// </para>
    /// <para>
    /// Stream URLs expire after about six hours. A prepared video is only handed out while its URL is
    /// still good, so the next video of a very long one is looked up again at the end, as before.
    /// </para>
    /// Must be used on the UI thread (it reads the state of the video source).
    /// </remarks>
    public sealed class NextVideoPrefetcher : IDisposable
    {
        /// <summary>Wait after a video has started, so the preparation does not compete with its own start.</summary>
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

        /// <summary>A URL is not used if it expires within this time.</summary>
        public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);

        /// <summary>How long a prepared video is good if its URLs do not say when they expire.</summary>
        public static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(30);

        private readonly IYouTubeService _youtube;
        private readonly IVideoSource _videoSource;
        private readonly Func<string?> _queuedVideo;
        private readonly TimeProvider _time;
        private Entry? _entry;
        private bool _disposed;

        /// <param name="queuedVideo">The video that is queued to play next, or <c>null</c>.</param>
        public NextVideoPrefetcher(IYouTubeService youtube, IVideoSource videoSource, Func<string?> queuedVideo, TimeProvider time)
        {
            _youtube = youtube;
            _videoSource = videoSource;
            _queuedVideo = queuedVideo;
            _time = time;
        }

        /// <summary>
        /// Starts preparing the video that follows <paramref name="currentVideoId"/> (after <see cref="Delay"/>).
        /// An earlier preparation is dropped.
        /// </summary>
        public void Start(string? currentVideoId)
        {
            Cancel();
            if (_disposed || currentVideoId == null)
                return;

            var entry = new Entry(currentVideoId);
            _entry = entry;
            RunAsync(entry).FireAndForget("Preparing the next video failed.");
        }

        /// <summary>Drops the prepared video (or the preparation that is still running).</summary>
        public void Cancel()
        {
            _entry?.Cancellation.Cancel();
            _entry = null;
        }

        /// <summary>
        /// Hands over the prepared video if it is the one that follows <paramref name="after"/> and still
        /// usable; the caller is then responsible for it. Otherwise returns <c>null</c> and drops what is left.
        /// </summary>
        /// <param name="after">The video that is playing now (what the preparation was made for).</param>
        /// <param name="queuedVideoId">The video that is queued to play next, or <c>null</c>.</param>
        public PrefetchedVideo? Take(string? after, string? queuedVideoId)
        {
            var entry = _entry;
            if (entry == null)
                return null;

            var usable = entry.After == after
                && entry.VideoId != null
                && entry.Video != null
                && !entry.Failed
                && (queuedVideoId == null || queuedVideoId == entry.VideoId)
                && (entry.ExpiresAt == null || _time.GetUtcNow() < entry.ExpiresAt);

            if (!usable)
            {
                Cancel();
                return null;
            }

            // From now on the caller owns it: later changes must not cancel the lookup it is waiting for.
            _entry = null;
            return new PrefetchedVideo(entry.VideoId!, entry.Video!);
        }

        private async Task RunAsync(Entry entry)
        {
            var token = entry.Cancellation.Token;
            try
            {
                await Task.Delay(Delay, _time, token);

                var id = _queuedVideo();
                if (id == null && !_videoSource.TryGetNextVideoId(entry.After, out id))
                    return;

                entry.VideoId = id;
                var video = _youtube.ResolveAsync(id, token);
                entry.Video = video;

                var resolved = await video;
                entry.ExpiresAt = ExpiryOf(resolved);
                Logger.Log($"Prepared the next video '{id}' in advance.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced or dropped.
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                Logger.Log($"Preparing the next video '{entry.VideoId}' in advance failed; it is looked up when it is due.", ex);
            }
        }

        /// <summary>The earlier of the two stream URLs' expiry times, minus a margin.</summary>
        private DateTimeOffset ExpiryOf(ResolvedVideo video)
        {
            var videoExpiry = StreamUrl.GetExpiry(video.VideoUrl);
            var audioExpiry = StreamUrl.GetExpiry(video.AudioUrl);

            DateTimeOffset? expiry = (videoExpiry, audioExpiry) switch
            {
                ({ } v, { } a) => v < a ? v : a,
                ({ } v, null) => v,
                (null, { } a) => a,
                _ => null
            };

            return expiry is { } known
                ? known - ExpiryMargin
                : _time.GetUtcNow() + FallbackLifetime;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Cancel();
            _disposed = true;
        }

        /// <summary>One preparation: for the video that follows <see cref="After"/>.</summary>
        private sealed class Entry
        {
            public Entry(string after) => After = after;

            public string After { get; }

            public CancellationTokenSource Cancellation { get; } = new();

            /// <summary>The video that was chosen; <c>null</c> until the delay is over.</summary>
            public string? VideoId { get; set; }

            public Task<ResolvedVideo>? Video { get; set; }

            /// <summary>Until when the streams are good; <c>null</c> while they are being resolved.</summary>
            public DateTimeOffset? ExpiresAt { get; set; }

            public bool Failed { get; set; }
        }
    }
}
