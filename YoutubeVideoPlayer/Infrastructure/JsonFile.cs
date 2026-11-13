using System;
using System.IO;
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
        public static T? Read<T>(string path) where T : class
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(json);
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
