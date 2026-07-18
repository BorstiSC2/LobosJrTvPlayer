using System;
using System.IO;

namespace YouTubeVideoPlayer
{
    public static class Logger
    {
        private static readonly object LockObject = new object();
        private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "errorlog.txt");

        public static void Log(string message, Exception exception)
        {
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

            lock (LockObject)
            {
                File.AppendAllText(LogFilePath, logEntry);
            }
        }

        public static void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string logEntry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}" +
                new string('-', 80) + Environment.NewLine;

            lock (LockObject)
            {
                File.AppendAllText(LogFilePath, logEntry);
            }
        }
    }
}
