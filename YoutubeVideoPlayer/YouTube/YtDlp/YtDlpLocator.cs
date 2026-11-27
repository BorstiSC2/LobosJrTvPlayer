using System;
using System.IO;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>Finds yt-dlp.exe from the <c>ytDlp.path</c> setting.</summary>
    public static class YtDlpLocator
    {
        /// <summary>
        /// The full path of the exe, or <c>null</c> if it is not there. An absolute path is taken as it is; a relative one is
        /// looked for in <paramref name="baseDirectory"/> (the app folder); a bare file name (<c>yt-dlp.exe</c>) is also
        /// looked for in every folder on <c>PATH</c>. A name without extension also matches with <c>.exe</c>.
        /// </summary>
        /// <param name="pathVariable">The folders to search, as in <c>PATH</c>; the process's <c>PATH</c> by default.</param>
        public static string? Find(string path, string baseDirectory, string? pathVariable = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            path = path.Trim().Trim('"');
            try
            {
                if (Path.IsPathRooted(path))
                    return Existing(path);

                var local = Existing(Path.GetFullPath(Path.Combine(baseDirectory, path)));
                if (local != null || path.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    return local;

                foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                             .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var directory = folder.Trim().Trim('"');
                    if (directory.Length == 0)
                        continue;

                    var found = Existing(Path.Combine(directory, path));
                    if (found != null)
                        return found;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A path that cannot exist.
            }

            return null;
        }

        private static string? Existing(string path)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);

            var withExtension = path + ".exe";
            return Path.GetExtension(path).Length == 0 && File.Exists(withExtension) ? Path.GetFullPath(withExtension) : null;
        }
    }
}
