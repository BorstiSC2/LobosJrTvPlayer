using System;
using System.IO;
using System.Linq;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// Stores the video IDs of each playlist in its own file (<c>PlaylistCache/&lt;playlistId&gt;.json</c>),
    /// so playlists only have to be downloaded from YouTube once or when the user asks for an update.
    /// Methods are thread-safe as long as each playlist is written by one caller at a time.
    /// </summary>
    public sealed class PlaylistCache
    {
        private readonly string _directory;

        public PlaylistCache(string? baseDirectory = null)
        {
            _directory = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "PlaylistCache");
        }

        /// <summary>Returns the cached data, or <c>null</c> if there is no usable cache file.</summary>
        public PlaylistCacheData? TryRead(string playlistId)
        {
            var path = GetPath(playlistId);
            try
            {
                var data = JsonFile.Read<PlaylistCacheData>(path);
                if (data == null)
                    return null;

                data.VideoIds ??= new();
                return data;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read the cache of playlist '{playlistId}'. It will be downloaded again.", ex);
                JsonFile.QuarantineCorruptFile(path);
                return null;
            }
        }

        public bool Exists(string playlistId) => File.Exists(GetPath(playlistId));

        public void Write(PlaylistCacheData data) => JsonFile.WriteAtomic(GetPath(data.PlaylistId), data);

        public void Delete(string playlistId)
        {
            try
            {
                var path = GetPath(playlistId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not delete the cache of playlist '{playlistId}'.", ex);
            }
        }

        private string GetPath(string playlistId)
        {
            // YouTube playlist IDs only contain [A-Za-z0-9_-]; strip anything else to be safe.
            var safeName = new string(playlistId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            return Path.Combine(_directory, safeName + ".json");
        }
    }
}
