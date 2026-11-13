using System;
using System.IO;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// Loads and saves <c>playlists.json</c> next to the executable. If the file is missing it is
    /// created from the shipped <c>playlists.default.json</c>, so a rebuild never overwrites
    /// playlists that were added at runtime.
    /// </summary>
    public sealed class PlaylistStore
    {
        private readonly string _path;
        private readonly string _defaultPath;

        public PlaylistStore(string? baseDirectory = null)
        {
            var directory = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            _path = Path.Combine(directory, "playlists.json");
            _defaultPath = Path.Combine(directory, "playlists.default.json");
        }

        public PlaylistLibrary Load()
        {
            var library = TryRead(_path);
            if (library != null)
                return library;

            library = TryRead(_defaultPath) ?? new PlaylistLibrary();
            Save(library);
            return library;
        }

        public void Save(PlaylistLibrary library) => JsonFile.WriteAtomic(_path, library);

        /// <summary>
        /// A file that is damaged is renamed and treated as missing. A file that cannot be read right now
        /// (locked, no access) is not damaged: the exception is thrown, and the file is neither renamed nor replaced.
        /// </summary>
        private static PlaylistLibrary? TryRead(string path)
        {
            var library = JsonFile.ReadOrQuarantine<PlaylistLibrary>(path, "it will be recreated.");
            if (library != null)
            {
                library.Playlists ??= new();
                library.AutoSwap ??= new();
                library.AutoSwap.IntervalHours = Math.Clamp(
                    library.AutoSwap.IntervalHours, AutoSwapSettings.MinIntervalHours, AutoSwapSettings.MaxIntervalHours);
            }

            return library;
        }
    }
}
