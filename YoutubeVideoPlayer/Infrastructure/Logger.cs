using System;
using System.IO;
using System.Security;

namespace YouTubeVideoPlayer.Infrastructure
{
    /// <summary>
    /// Writes <c>errorlog.txt</c>. A log must never be the reason for a failure: it is called from <c>catch</c> blocks
    /// and <c>async void</c> handlers, where an exception would take the application down. So a log file that cannot be
    /// written (held by another instance on a shared folder, a sync client or a virus scanner; no access) is skipped.
    /// </summary>
    public static class Logger
    {
        private static readonly object LockObject = new object();
        /// <summary>Where the log is written; changeable for tests.</summary>
        internal static string LogFilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "errorlog.txt");
        private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

        public static void Clear()
        {
            lock (LockObject)
            {
                try
                {
                    if (File.Exists(LogFilePath))
                    {
                        File.WriteAllText(LogFilePath, string.Empty);
                    }
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    // The old log stays; new entries are appended to it.
                }
            }
        }

        private static bool IsLogFileTooLarge()
        {
            try
            {
                if (!File.Exists(LogFilePath))
                {
                    return false;
                }

                FileInfo fileInfo = new FileInfo(LogFilePath);
                return fileInfo.Length >= MaxLogFileSizeBytes;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                return false; // the append below decides whether the file can be written at all
            }
        }

        public static void Log(string message, Exception? exception)
        {
            if (IsLogFileTooLarge())
            {
                return;
            }

            if (exception == null)
            {
                return;
            }

            string logEntry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().FullName}{Environment.NewLine}" +
                $"Message: {message} - {exception.Message}{Environment.NewLine}" +
                $"StackTrace:{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}";

            if (exception.InnerException != null)
            {
                logEntry +=
                    $"Inner Exception:{Environment.NewLine}" +
                    $"{exception.InnerException}{Environment.NewLine}";
            }

            logEntry += new string('-', 80) + Environment.NewLine;

            Append(logEntry);
        }

        public static void Log(string message)
        {
            if (IsLogFileTooLarge())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string logEntry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}" +
                new string('-', 80) + Environment.NewLine;

            Append(logEntry);
        }

        private static void Append(string logEntry)
        {
            lock (LockObject)
            {
                try
                {
                    File.AppendAllText(LogFilePath, logEntry);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    // Nothing sensible is left to do with a message that cannot be logged.
                }
            }
        }

        private static bool IsFileError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException;
    }
}
