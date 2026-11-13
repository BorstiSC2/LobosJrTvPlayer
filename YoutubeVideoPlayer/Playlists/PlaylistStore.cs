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

        private static PlaylistLibrary? TryRead(string path)
        {
            try
            {
                var library = JsonFile.Read<PlaylistLibrary>(path);
                if (library != null)
                    library.Playlists ??= new();

                return library;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read '{path}'. The file was renamed and will be recreated.", ex);
                JsonFile.QuarantineCorruptFile(path);
                return null;
            }
        }
    }
}
