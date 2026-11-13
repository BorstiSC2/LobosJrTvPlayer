using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode.Playlists;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// Manages the playlist library and the pool of video IDs the player picks from.
    /// </summary>
    /// <remarks>
    /// Video IDs are read from the per-playlist cache; YouTube is only contacted when a playlist has
    /// no cache yet or the user requests an update. At startup the selected playlist is loaded first,
    /// all others are loaded one after another in the background.
    /// All public members must be called on the UI thread; downloads and file reads run on the
    /// thread pool and their results are applied back on the UI thread.
    /// </remarks>
    public sealed class PlaylistManager : IVideoSource, IShuffleControl
    {
        private readonly IYouTubeService _youtube;
        private readonly PlaylistStore _store;
        private readonly PlaylistCache _cache;
        private readonly List<PlaylistInfo> _playlists = new();
        private readonly Dictionary<string, Task> _loadTasks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<PlaylistUpdateResult>> _updateTasks = new(StringComparer.Ordinal);
        private readonly Random _random = new();
        private PlaylistLibrary _library = new();
        private List<string> _pool = new();

        public PlaylistManager(IYouTubeService youtube, PlaylistStore store, PlaylistCache cache)
        {
            _youtube = youtube;
            _store = store;
            _cache = cache;
        }

        public IReadOnlyList<PlaylistInfo> Playlists => _playlists;

        /// <summary>The key of the selected playlist, or <c>null</c> if all playlists are combined.</summary>
        public string? SelectedKey { get; private set; }

        public int PoolCount => _pool.Count;

        public bool IsLoading => _playlists.Any(p => p.State == PlaylistLoadState.Loading);

        /// <summary>The set of playable videos changed.</summary>
        public event EventHandler<PoolChangedEventArgs>? PoolChanged;

        /// <summary>A playlist was added, removed, renamed or its load state changed.</summary>
        public event EventHandler? PlaylistsChanged;

        /// <summary>
        /// Loads the library, then the selected playlist (from cache, or downloaded if there is none).
        /// The remaining playlists continue loading in the background after this returns.
        /// </summary>
        public async Task InitializeAsync()
        {
            _library = _store.Load();
            _playlists.Clear();
            foreach (var entry in _library.Playlists)
                _playlists.Add(CreateInfo(entry));

            var selected = FindByUrl(_library.SelectedPlaylistUrl);
            SelectedKey = selected?.Key;

            if (selected != null)
            {
                await TryEnsureLoadedAsync(selected);
            }
            else
            {
                // "All playlists": cached playlists are cheap to read; if none is cached, download the first one.
                foreach (var playlist in _playlists.Where(p => p.HasValidId && _cache.Exists(p.Key)).ToList())
                    await TryEnsureLoadedAsync(playlist);

                if (_playlists.Count > 0 && !_playlists.Any(p => p.State == PlaylistLoadState.Loaded))
                    await TryEnsureLoadedAsync(_playlists[0]);
            }

            RebuildPool(selectionChanged: false);
            LoadRemainingInBackgroundAsync().FireAndForget("Loading playlists in the background failed.");
        }

        /// <summary>
        /// The videos play in random order. This is the setting of the selected playlist; all playlists
        /// combined always play in random order, because they have no common playlist order.
        /// </summary>
        public bool IsShuffled => SelectedKey == null || (Find(SelectedKey)?.Shuffle ?? true);

        /// <summary><c>false</c> while all playlists are combined (see <see cref="IsShuffled"/>).</summary>
        public bool CanChangeShuffle => SelectedKey != null;

        /// <summary>The title of the selected playlist, or <c>null</c> if all playlists are combined.</summary>
        public string? PlaylistTitle => SelectedKey == null ? null : Find(SelectedKey)?.Title;

        public bool TryGetNextVideoId(string? currentVideoId, [NotNullWhen(true)] out string? videoId)
        {
            if (_pool.Count == 0)
            {
                videoId = null;
                return false;
            }

            var currentIndex = IndexOfVideo(currentVideoId);
            int index;
            if (!IsShuffled)
            {
                // The next one in playlist order; after the last video the playlist starts over. Without a
                // (known) current video the index is -1, so it starts at the first video.
                index = (currentIndex + 1) % _pool.Count;
            }
            else if (currentIndex < 0 || _pool.Count == 1)
            {
                index = _random.Next(_pool.Count);
            }
            else
            {
                // A random video, but not the one that is just ending.
                index = _random.Next(_pool.Count - 1);
                if (index >= currentIndex)
                    index++;
            }

            videoId = _pool[index];
            return true;
        }

        public IReadOnlyList<string> GetNextVideoIds(int count, string? currentVideoId)
        {
            if (IsShuffled)
            {
                // Partial Fisher-Yates shuffle on a copy: only the first `count` positions are randomized.
                var copy = _pool.Where(id => id != currentVideoId).ToArray();
                var take = Math.Min(count, copy.Length);
                for (var i = 0; i < take; i++)
                {
                    var j = _random.Next(i, copy.Length);
                    (copy[i], copy[j]) = (copy[j], copy[i]);
                }

                return copy.Take(take).ToList();
            }

            // The episodes that follow the current one; after the last one the playlist starts over, so the
            // list is only shorter than `count` if the playlist has fewer videos.
            var currentIndex = IndexOfVideo(currentVideoId);
            var next = new List<string>();
            for (var step = 1; step <= _pool.Count && next.Count < count; step++)
            {
                var id = _pool[(currentIndex + step) % _pool.Count];
                if (id != currentVideoId)
                    next.Add(id);
            }

            return next;
        }

        /// <summary>Position of a video in the current pool, or -1 if there is no such video.</summary>
        private int IndexOfVideo(string? videoId) => videoId == null ? -1 : _pool.IndexOf(videoId);

        /// <summary>Sets and saves whether a playlist plays in random order. If saving fails, the old value is kept.</summary>
        public void SetShuffle(string playlistKey, bool shuffle)
        {
            var playlist = Find(playlistKey) ?? throw new InvalidOperationException("The playlist no longer exists.");
            if (playlist.Shuffle == shuffle)
                return;

            playlist.Entry.Shuffle = shuffle;
            try
            {
                Save();
            }
            catch
            {
                playlist.Entry.Shuffle = !shuffle;
                throw;
            }

            Logger.Log($"Playlist '{playlist.Title}' now plays {(shuffle ? "in random order" : "in playlist order")}.");
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Switches the order of the selected playlist and returns the new value of <see cref="IsShuffled"/>.</summary>
        /// <exception cref="InvalidOperationException">All playlists are combined, so there is no single playlist to change.</exception>
        public bool ToggleShuffle()
        {
            var playlist = SelectedKey == null ? null : Find(SelectedKey);
            if (playlist == null)
                throw new InvalidOperationException("Shuffle cannot be changed while all playlists are combined.");

            SetShuffle(playlist.Key, !playlist.Shuffle);
            return playlist.Shuffle;
        }

        public bool Contains(string playlistKey) => Find(playlistKey) != null;

        /// <summary>
        /// Switches playback to a playlist (or to all playlists when <paramref name="playlistKey"/> is <c>null</c>).
        /// Waits for the playlist if it is still loading. If it cannot be loaded, the previous selection is kept.
        /// </summary>
        public async Task SelectAsync(string? playlistKey)
        {
            PlaylistInfo? playlist = null;
            if (playlistKey != null)
            {
                playlist = Find(playlistKey) ?? throw new InvalidOperationException("The playlist no longer exists.");
                await EnsureLoadedAsync(playlist);
            }

            SelectedKey = playlist?.Key;
            _library.SelectedPlaylistUrl = playlist?.Url;
            Save();
            RebuildPool(selectionChanged: true);
        }

        /// <summary>Looks up a playlist link on YouTube (used to pre-fill the name when adding a playlist).</summary>
        public async Task<YouTubePlaylistDetails> GetPlaylistDetailsAsync(string url, CancellationToken cancellationToken)
        {
            var playlistId = TryParsePlaylistId(url)
                ?? throw new ArgumentException("This is not a valid YouTube playlist link.", nameof(url));

            var title = await Task.Run(() => _youtube.GetPlaylistTitleAsync(playlistId.Value, cancellationToken), cancellationToken);
            return new YouTubePlaylistDetails(playlistId.Value, title, NormalizeUrl(playlistId));
        }

        /// <summary>Adds a playlist and downloads its video IDs right away.</summary>
        public async Task<PlaylistInfo> AddAsync(string title, string url)
        {
            var playlistId = TryParsePlaylistId(url)
                ?? throw new ArgumentException("This is not a valid YouTube playlist link.", nameof(url));

            if (Contains(playlistId.Value))
                throw new InvalidOperationException("This playlist is already in the list.");

            var entry = new PlaylistEntry { Title = title.Trim(), Url = NormalizeUrl(playlistId) };
            _library.Playlists.Add(entry);
            Save();

            var playlist = CreateInfo(entry);
            _playlists.Add(playlist);
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);

            // A previously removed playlist may still have a stale cache file.
            _cache.Delete(playlist.Key);
            await EnsureLoadedAsync(playlist);

            if (SelectedKey == null)
                RebuildPool(selectionChanged: false);

            return playlist;
        }

        public void Rename(string playlistKey, string newTitle)
        {
            var playlist = Find(playlistKey) ?? throw new InvalidOperationException("The playlist no longer exists.");
            playlist.Entry.Title = newTitle.Trim();
            Save();
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(string playlistKey)
        {
            var playlist = Find(playlistKey);
            if (playlist == null)
                return;

            _playlists.Remove(playlist);
            _library.Playlists.Remove(playlist.Entry);
            _cache.Delete(playlist.Key);

            var wasSelected = SelectedKey == playlist.Key;
            if (wasSelected)
            {
                SelectedKey = null;
                _library.SelectedPlaylistUrl = null;
            }

            Save();
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);

            if (wasSelected || SelectedKey == null)
                RebuildPool(selectionChanged: wasSelected);
        }

        /// <summary>
        /// Downloads the playlist from YouTube again and replaces its cache. If the download fails,
        /// the previous video IDs stay in use and the exception is rethrown.
        /// </summary>
        public async Task<PlaylistUpdateResult> UpdateAsync(string playlistKey)
        {
            var playlist = Find(playlistKey) ?? throw new InvalidOperationException("The playlist no longer exists.");
            if (!playlist.HasValidId)
                throw new InvalidOperationException($"'{playlist.Url}' is not a valid YouTube playlist link.");

            // A second request for a playlist that is already being updated shares that update
            // instead of downloading the whole playlist again.
            if (_updateTasks.TryGetValue(playlist.Key, out var running))
                return await running;

            var update = UpdateCoreAsync(playlist);
            _updateTasks[playlist.Key] = update;
            try
            {
                return await update;
            }
            finally
            {
                if (_updateTasks.TryGetValue(playlist.Key, out var current) && current == update)
                    _updateTasks.Remove(playlist.Key);
            }
        }

        private async Task<PlaylistUpdateResult> UpdateCoreAsync(PlaylistInfo playlist)
        {
            // Let a load that is still running finish first, so the playlist is not downloaded twice at once.
            if (_loadTasks.TryGetValue(playlist.Key, out var running))
            {
                try { await running; }
                catch { /* the update below retries anyway */ }
            }

            var previousIds = new HashSet<string>(playlist.VideoIds);
            await StartTrackedLoad(playlist, forceDownload: true);

            if (SelectedKey == null || SelectedKey == playlist.Key)
                RebuildPool(selectionChanged: false);

            var newIds = new HashSet<string>(playlist.VideoIds);
            return new PlaylistUpdateResult(
                playlist.Title,
                Added: newIds.Count(id => !previousIds.Contains(id)),
                Removed: previousIds.Count(id => !newIds.Contains(id)),
                Total: newIds.Count);
        }

        private async Task LoadRemainingInBackgroundAsync()
        {
            foreach (var playlist in _playlists.ToList())
            {
                if (!_playlists.Contains(playlist) || playlist.State is PlaylistLoadState.Loaded or PlaylistLoadState.Failed)
                    continue;

                if (await TryEnsureLoadedAsync(playlist) && SelectedKey == null)
                    RebuildPool(selectionChanged: false);
            }

            Logger.Log($"Background playlist loading finished ({_playlists.Count(p => p.State == PlaylistLoadState.Loaded)}/{_playlists.Count} loaded).");
        }

        private async Task<bool> TryEnsureLoadedAsync(PlaylistInfo playlist)
        {
            try
            {
                await EnsureLoadedAsync(playlist);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Loading playlist '{playlist.Title}' failed.", ex);
                return false;
            }
        }

        /// <summary>Loads the playlist from cache or YouTube, reusing a load that is already running.</summary>
        private Task EnsureLoadedAsync(PlaylistInfo playlist)
        {
            if (playlist.State == PlaylistLoadState.Loaded)
                return Task.CompletedTask;

            if (_loadTasks.TryGetValue(playlist.Key, out var running))
                return running;

            return StartTrackedLoad(playlist, forceDownload: false);
        }

        /// <summary>Starts a load and registers it in <see cref="_loadTasks"/> until it completes.</summary>
        private Task StartTrackedLoad(PlaylistInfo playlist, bool forceDownload)
        {
            var task = LoadCoreAsync(playlist, forceDownload);
            _loadTasks[playlist.Key] = task;
            _ = RemoveWhenDoneAsync(playlist.Key, task);
            return task;
        }

        private async Task RemoveWhenDoneAsync(string playlistKey, Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // Observed and handled by whoever awaits the load.
            }

            if (_loadTasks.TryGetValue(playlistKey, out var current) && current == task)
                _loadTasks.Remove(playlistKey);
        }

        private async Task LoadCoreAsync(PlaylistInfo playlist, bool forceDownload)
        {
            if (!playlist.HasValidId)
            {
                playlist.State = PlaylistLoadState.Failed;
                playlist.Error = "Not a valid YouTube playlist link.";
                PlaylistsChanged?.Invoke(this, EventArgs.Empty);
                throw new InvalidOperationException($"'{playlist.Url}' is not a valid YouTube playlist link.");
            }

            var previousState = playlist.State;
            playlist.State = PlaylistLoadState.Loading;
            playlist.Error = null;
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);

            try
            {
                PlaylistCacheData? data = null;
                if (!forceDownload)
                    data = await Task.Run(() => _cache.TryRead(playlist.Key));

                if (data == null)
                {
                    Logger.Log($"Downloading video IDs of playlist '{playlist.Title}' ({playlist.Key})...");
                    data = await Task.Run(() => DownloadAsync(playlist.Key, playlist.Title));

                    // The playlist may have been removed while it was downloading.
                    if (_playlists.Contains(playlist))
                        await Task.Run(() => _cache.Write(data));

                    Logger.Log($"Downloaded {data.VideoIds.Count} video IDs of playlist '{playlist.Title}'.");
                }

                playlist.VideoIds = data.VideoIds;
                playlist.FetchedAtUtc = data.FetchedAtUtc;
                playlist.State = PlaylistLoadState.Loaded;
            }
            catch (Exception ex)
            {
                playlist.Error = ex.Message;

                // A failed update keeps the previously loaded video IDs usable.
                playlist.State = previousState == PlaylistLoadState.Loaded && playlist.VideoIds.Count > 0
                    ? PlaylistLoadState.Loaded
                    : PlaylistLoadState.Failed;
                throw;
            }
            finally
            {
                PlaylistsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task<PlaylistCacheData> DownloadAsync(string playlistId, string title)
        {
            var downloaded = await _youtube.GetPlaylistVideoIdsAsync(playlistId, CancellationToken.None);

            // Keep the playlist order, but drop videos that appear more than once.
            var ids = downloaded.Distinct(StringComparer.Ordinal).ToList();

            return new PlaylistCacheData
            {
                PlaylistId = playlistId,
                Title = title,
                FetchedAtUtc = DateTime.UtcNow,
                VideoIds = ids
            };
        }

        private void RebuildPool(bool selectionChanged)
        {
            IEnumerable<PlaylistInfo> sources = SelectedKey == null
                ? _playlists
                : _playlists.Where(p => p.Key == SelectedKey);

            _pool = sources
                .Where(p => p.VideoIds.Count > 0)
                .SelectMany(p => p.VideoIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            PoolChanged?.Invoke(this, new PoolChangedEventArgs(selectionChanged));
        }

        private void Save()
        {
            try
            {
                _store.Save(_library);
            }
            catch (Exception ex)
            {
                Logger.Log("Saving playlists.json failed.", ex);
                throw;
            }
        }

        private PlaylistInfo? Find(string playlistKey) => _playlists.FirstOrDefault(p => p.Key == playlistKey);

        private PlaylistInfo? FindByUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var key = TryParsePlaylistId(url)?.Value ?? url;
            return Find(key);
        }

        private static PlaylistInfo CreateInfo(PlaylistEntry entry)
        {
            var playlistId = TryParsePlaylistId(entry.Url);
            return playlistId != null
                ? new PlaylistInfo(entry, playlistId.Value.Value, hasValidId: true)
                : new PlaylistInfo(entry, entry.Url, hasValidId: false);
        }

        public static PlaylistId? TryParsePlaylistId(string? url) =>
            string.IsNullOrWhiteSpace(url) ? null : PlaylistId.TryParse(url.Trim());

        private static string NormalizeUrl(PlaylistId playlistId) =>
            $"https://www.youtube.com/playlist?list={playlistId.Value}";
    }
}
