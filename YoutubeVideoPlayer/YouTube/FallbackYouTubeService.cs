using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>
    /// Asks one backend and, if it fails, the other (<c>youtube.fallback</c> in <c>appsettings.json</c>). YouTube changes
    /// break YoutubeExplode and yt-dlp at different times, so a lookup that one of them gets wrong usually works with the
    /// other.
    /// </summary>
    /// <remarks>
    /// A cancellation by the caller is passed on, never answered by the other backend. A video lookup gives the first
    /// backend at most <see cref="PrimaryTimeLimit"/>, so one that hangs still leaves the second time within
    /// <see cref="VideoLookup.TimeLimit"/>. A playlist without any video counts as failed too (YouTube sometimes answers
    /// with nothing).
    /// </remarks>
    public sealed class FallbackYouTubeService : IYouTubeService
    {
        /// <summary>How long the first backend may take for a video before the second is asked.</summary>
        public static readonly TimeSpan PrimaryTimeLimit = TimeSpan.FromSeconds(25);

        private readonly IYouTubeService _primary;
        private readonly IYouTubeService _secondary;
        private readonly TimeProvider _time;

        public FallbackYouTubeService(
            IYouTubeService primary, string primaryName, IYouTubeService secondary, string secondaryName, TimeProvider? time = null)
        {
            _primary = primary;
            PrimaryName = primaryName;
            _secondary = secondary;
            SecondaryName = secondaryName;
            _time = time ?? TimeProvider.System;
        }

        public string PrimaryName { get; }

        public string SecondaryName { get; }

        public Task<YouTubeVideoDetails> GetVideoAsync(string videoIdOrUrl, CancellationToken cancellationToken) =>
            RunAsync((service, token) => service.GetVideoAsync(videoIdOrUrl, token), $"'{videoIdOrUrl}'", limitPrimary: true, cancellationToken);

        public Task<ResolvedVideo> ResolveAsync(string videoIdOrUrl, CancellationToken cancellationToken) =>
            RunAsync((service, token) => service.ResolveAsync(videoIdOrUrl, token), $"'{videoIdOrUrl}'", limitPrimary: true, cancellationToken);

        public Task<string> GetPlaylistTitleAsync(string playlistId, CancellationToken cancellationToken) =>
            RunAsync((service, token) => service.GetPlaylistTitleAsync(playlistId, token), $"playlist '{playlistId}'", limitPrimary: false, cancellationToken);

        public Task<IReadOnlyList<string>> GetPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken) =>
            RunAsync((service, token) => service.GetPlaylistVideoIdsAsync(playlistId, token), $"playlist '{playlistId}'", limitPrimary: false,
                cancellationToken, isEmpty: ids => ids.Count == 0);

        private async Task<T> RunAsync<T>(
            Func<IYouTubeService, CancellationToken, Task<T>> call,
            string subject,
            bool limitPrimary,
            CancellationToken cancellationToken,
            Func<T, bool>? isEmpty = null)
        {
            Exception? primaryFailure;
            using (var limit = limitPrimary ? new CancellationTokenSource(PrimaryTimeLimit, _time) : new CancellationTokenSource())
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token))
            {
                var primary = Start(call, _primary, request.Token);
                try
                {
                    // WaitAsync ends the wait even if the request does not react to its cancellation.
                    var result = await primary.WaitAsync(request.Token).ConfigureAwait(false);
                    if (isEmpty?.Invoke(result) != true)
                        return result;

                    primaryFailure = null;
                    Logger.Log($"{PrimaryName} found no videos in {subject}; trying {SecondaryName}.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Observe(primary);
                    throw;
                }
                catch (Exception ex)
                {
                    request.Cancel();
                    Observe(primary);
                    primaryFailure = ex is OperationCanceledException && limit.IsCancellationRequested
                        ? new TimeoutException($"{PrimaryName} took longer than {PrimaryTimeLimit.TotalSeconds:0} seconds.")
                        : ex;
                    Logger.Log($"{PrimaryName} failed for {subject} ({primaryFailure.GetType().Name}: {primaryFailure.Message}); trying {SecondaryName}.");
                }
            }

            try
            {
                return await call(_secondary, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (primaryFailure != null && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                throw new YouTubeLookupException(
                    $"{PrimaryName}: {primaryFailure.Message} {SecondaryName}: {ex.Message}", primaryFailure, ex);
            }
        }

        /// <summary>The call's task, also when it throws before returning one.</summary>
        private static Task<T> Start<T>(Func<IYouTubeService, CancellationToken, Task<T>> call, IYouTubeService service, CancellationToken token)
        {
            try
            {
                return call(service, token);
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        /// <summary>A request that was left behind may still fail; that must not go unobserved.</summary>
        private static void Observe(Task task) =>
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Both backends failed. The message names both reasons; the first backend's exception is the inner one.</summary>
    public sealed class YouTubeLookupException : Exception
    {
        public YouTubeLookupException(string message, Exception primaryFailure, Exception secondaryFailure)
            : base(message, primaryFailure)
        {
            SecondaryFailure = secondaryFailure;
        }

        public Exception SecondaryFailure { get; }
    }
}
