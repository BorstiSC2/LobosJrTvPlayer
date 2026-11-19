using System;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>Looks up a video's streams with a time limit, so a lookup that hangs cannot stop the playback.</summary>
    /// <remarks>
    /// A lookup normally takes 2-3 s, but YouTube sometimes answers the stream request very slowly (measured: up to
    /// 29 s), and nothing else ends a request that never answers. Without the limit the app waited forever on
    /// "Loading first video..." (Play stays disabled until the first video is looked up) or on a black screen
    /// between two videos.
    /// </remarks>
    public static class VideoLookup
    {
        /// <summary>How long a lookup may take before it counts as failed.</summary>
        public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Like <see cref="IYouTubeService.ResolveAsync"/>, but throws a <see cref="TimeoutException"/> (a normal
        /// failure, not a cancellation) once <see cref="TimeLimit"/> has passed on <paramref name="time"/>; the request
        /// is cancelled then. Cancelling <paramref name="cancellationToken"/> ends it with an
        /// <see cref="OperationCanceledException"/> as before.
        /// </summary>
        public static async Task<ResolvedVideo> ResolveAsync(
            IYouTubeService youtube, string videoIdOrUrl, TimeProvider time, CancellationToken cancellationToken)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var lookup = youtube.ResolveAsync(videoIdOrUrl, request.Token);
            try
            {
                // WaitAsync ends the wait even if the request does not react to its cancellation.
                return await lookup.WaitAsync(TimeLimit, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Looking up the streams of '{videoIdOrUrl}' took longer than {TimeLimit.TotalSeconds:0} seconds.");
            }
            finally
            {
                // Cancelled explicitly: after a cancellation of the caller this method can resume inline, before the
                // linked source has seen it, and disposing the source would then leave the request running.
                if (!lookup.IsCompleted)
                {
                    request.Cancel();
                    _ = lookup.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }
    }
}
