using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace YouTubeVideoPlayer.Infrastructure
{
    /// <summary>
    /// Small helpers for reading and atomically writing JSON files.
    /// </summary>
    public static class JsonFile
    {
        /// <summary>
        /// Reads and deserializes <paramref name="path"/>. Returns <c>null</c> if the file does not exist.
        /// Throws if the file exists but cannot be read or parsed.
        /// </summary>
        public static T? Read<T>(string path) where T : class => Read<T>(path, Thread.Sleep);

        /// <param name="waitBeforeRetry">How to wait after a read that failed because the file is in use (a test passes its own).</param>
        internal static T? Read<T>(string path, Action<TimeSpan> waitBeforeRetry) where T : class
        {
            if (!File.Exists(path))
                return null;

            var json = ReadAllText(path, waitBeforeRetry);
            return JsonConvert.DeserializeObject<T>(json);
        }

        /// <summary>
        /// Like <see cref="Read{T}"/>, but a file whose content cannot be parsed is renamed (so it is kept for
        /// inspection) and <c>null</c> is returned, so the caller starts from scratch. Only a damaged file is treated
        /// like that: a file that cannot be read for another reason (locked, no access) says nothing about its content,
        /// so that exception is thrown and the file stays where it is.
        /// </summary>
        /// <param name="consequence">What happens next, for the log (e.g. "it will be recreated.").</param>
        public static T? ReadOrQuarantine<T>(string path, string consequence) where T : class
        {
            try
            {
                return Read<T>(path);
            }
            catch (JsonException ex)
            {
                Logger.Log($"Could not parse '{path}'. The file was renamed; {consequence}", ex);
                QuarantineCorruptFile(path);
                return null;
            }
        }

        private const int ReadAttempts = 3;
        private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

        private static string ReadAllText(string path, Action<TimeSpan> waitBeforeRetry)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                // A virus scanner or a sync client may hold the file for a moment.
                catch (IOException ex) when (attempt < ReadAttempts && ex is not (FileNotFoundException or DirectoryNotFoundException))
                {
                    waitBeforeRetry(ReadRetryDelay);
                }
            }
        }

        /// <summary>
        /// Serializes <paramref name="value"/> to a temporary file first and then replaces
        /// <paramref name="path"/>, so a crash during the write never leaves a half-written file behind.
        /// </summary>
        public static void WriteAtomic<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(value, Formatting.Indented));
            File.Move(tempPath, path, overwrite: true);
        }

        /// <summary>
        /// Renames a file that could not be parsed so it is preserved for inspection instead of being overwritten.
        /// </summary>
        public static void QuarantineCorruptFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not quarantine corrupt file '{path}'.", ex);
            }
        }
    }
}
