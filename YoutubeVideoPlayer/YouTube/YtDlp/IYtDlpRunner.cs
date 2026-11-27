using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>What a yt-dlp run printed, and how it ended.</summary>
    public sealed record YtDlpOutput(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs yt-dlp with the given arguments. Implemented by <see cref="YtDlpProcess"/>; an interface so
    /// <see cref="YtDlpService"/> can be tested without the exe and without network access.
    /// </summary>
    public interface IYtDlpRunner
    {
        /// <summary>The exe that is run, for messages.</summary>
        string ExecutablePath { get; }

        /// <summary>
        /// Runs yt-dlp to its end. Cancelling ends the process (with the processes it started) and throws an
        /// <see cref="OperationCanceledException"/>; a process that cannot be started throws a <see cref="YtDlpException"/>.
        /// </summary>
        Task<YtDlpOutput> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    }

    /// <summary>yt-dlp could not be run, failed, or printed nothing usable. The message is short enough for a dialog.</summary>
    public sealed class YtDlpException : Exception
    {
        public YtDlpException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
