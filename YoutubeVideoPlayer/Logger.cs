using System;
using System.IO;

namespace YouTubeVideoPlayer
{
    public static class Logger
    {
        private static readonly object LockObject = new object();
        private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "errorlog.txt");
        private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB

        public static void Clear()
        {
            lock (LockObject)
            {
                if (File.Exists(LogFilePath))
                {
                    File.WriteAllText(LogFilePath, string.Empty);
                }
            }
        }

        private static bool IsLogFileTooLarge()
        {
            if (!File.Exists(LogFilePath))
            {
                return false;
            }

            FileInfo fileInfo = new FileInfo(LogFilePath);
            return fileInfo.Length >= MaxLogFileSizeBytes;
        }

        public static void Log(string message, Exception exception)
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

            lock (LockObject)
            {
                File.AppendAllText(LogFilePath, logEntry);
            }
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

            lock (LockObject)
            {
                File.AppendAllText(LogFilePath, logEntry);
            }
        }
    }
}
