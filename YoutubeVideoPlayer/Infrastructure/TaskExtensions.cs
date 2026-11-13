using System;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Infrastructure
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Runs a task without awaiting it, logging any exception instead of losing it.
        /// </summary>
        public static async void FireAndForget(this Task task, string context)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected way for background work to end.
            }
            catch (Exception ex)
            {
                Logger.Log(context, ex);
            }
        }
    }
}
