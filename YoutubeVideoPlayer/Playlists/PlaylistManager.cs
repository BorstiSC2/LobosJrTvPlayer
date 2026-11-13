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
    public sealed class PlaylistManager : IVideoSource, IShuffleControl, IPlaylistSwitch
    {
        private readonly IYouTubeService _youtube;
        private readonly PlaylistStore _store;
        private readonly PlaylistCache _cache;
        private readonly TimeProvider _time;
        private readonly List<PlaylistInfo> _playlists = new();
        private readonly Dictionary<string, Task> _loadTasks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<PlaylistUpdateResult>> _updateTasks = new(StringComparer.Ordinal);
        private readonly Random _random = new();
        private PlaylistLibrary _library = new();
        private List<string> _pool = new();
        private bool _poolShuffled = true; // IsShuffled when the pool was last built
        private bool _libraryLoadFailed;

        /// <param name="timeProvider">The clock for the automatic swap; <see cref="TimeProvider.System"/> by default.</param>
        public PlaylistManager(IYouTubeService youtube, PlaylistStore store, PlaylistCache cache, TimeProvider? timeProvider = null)
        {
            _youtube = youtube;
            _store = store;
            _cache = cache;
            _time = timeProvider ?? TimeProvider.System;
        }

        public IReadOnlyList<PlaylistInfo> Playlists => _playlists;

        /// <summary>The key of the selected playlist, or <c>null</c> if all playlists are combined.</summary>
        public string? SelectedKey { get; private set; }

        public int PoolCount => _pool.Count;

        public bool IsLoading => _playlists.Any(p => p.State == PlaylistLoadState.Loading);

        /// <summary>The library and the selected playlist are loaded (<see cref="InitializeAsync"/> has finished).</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>The set of playable videos changed.</summary>
        public event EventHandler<PoolChangedEventArgs>? PoolChanged;

        /// <summary>A playlist was added, removed, renamed or its load state changed.</summary>
        public event EventHandler? PlaylistsChanged;

        /// <summary>The playable videos or their order changed (see <see cref="IVideoSource.SequenceChanged"/>).</summary>
        public event EventHandler? SequenceChanged;

        /// <summary>The automatic swap was switched or changed, or its interval started over (the playlist changed).</summary>
        public event EventHandler? AutoSwapChanged;

        /// <summary>
        /// Loads the library, then the selected playlist (from cache, or downloaded if there is none).
        /// The remaining playlists continue loading in the background after this returns.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                _library = _store.Load();
                _libraryLoadFailed = false;
            }
            catch
            {
                // The file exists but cannot be read (locked, no access). Saving the empty library that is in
                // memory now would replace the user's playlists, so Save refuses until the application is restarted.
                _libraryLoadFailed = true;
                throw;
            }

            _playlists.Clear();
            foreach (var entry in _library.Playlists)
                _playlists.Add(CreateInfo(entry));

            var selected = FindByUrl(_library.SelectedPlaylistUrl);
            SelectedKey = selected?.Key;
            CheckSwapStart();
            AutoSwapChanged?.Invoke(this, EventArgs.Empty);

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
            IsInitialized = true;
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
            if (playlist.Key == SelectedKey)
                SequenceChanged?.Invoke(this, EventArgs.Empty); // a video that was chosen in advance may not fit anymore
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

        /// <summary>Whether a video is one of those the player picks from now.</summary>
        public bool IsInPool(string videoId) => _pool.Contains(videoId);

        #region Automatic swap

        /// <summary>The automatic swap is turned on (it only acts while <see cref="CanAutoSwap"/> is <c>true</c>).</summary>
        public bool AutoSwapEnabled => _library.AutoSwap.Enabled;

        /// <summary>How long each playlist plays before the next one takes over.</summary>
        public TimeSpan AutoSwapInterval => TimeSpan.FromHours(_library.AutoSwap.IntervalHours);

        /// <summary>When the playlist or the swap setting last changed; the interval counts from there.</summary>
        public DateTimeOffset? LastSwapUtc => _library.AutoSwap.LastSwapUtc;

        /// <summary>The automatic swap needs a playlist to swap to, so it can only be turned on with two playlists or more.</summary>
        public bool CanAutoSwap => _playlists.Count >= 2;

        /// <summary>When the next automatic swap is due; <c>null</c> while it is off.</summary>
        public DateTimeOffset? NextSwapUtc =>
            AutoSwapEnabled && CanAutoSwap ? (LastSwapUtc ?? _time.GetUtcNow()) + AutoSwapInterval : null;

        /// <summary>The automatic swap is due and waits for the right moment (near the end of the current video).</summary>
        public bool IsSwapDue => NextSwapUtc is { } due && _time.GetUtcNow() >= due;

        /// <summary>
        /// Turns the automatic swap on or off and sets its interval. Every change starts the interval over. If saving
        /// fails, the old setting is kept.
        /// </summary>
        /// <exception cref="InvalidOperationException">Turning it on with fewer than two playlists.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The interval is shorter than an hour or longer than the dialog can show.</exception>
        public void SetAutoSwap(bool enabled, TimeSpan interval)
        {
            if (enabled && !CanAutoSwap)
                throw new InvalidOperationException("Swapping playlists automatically needs at least two playlists.");

            var hours = (int)Math.Round(interval.TotalHours);
            if (hours < AutoSwapSettings.MinIntervalHours || hours > AutoSwapSettings.MaxIntervalHours)
                throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be between one hour and 365 days.");

            var settings = _library.AutoSwap;
            if (settings.Enabled == enabled && settings.IntervalHours == hours)
                return;

            var (previousEnabled, previousHours, previousSwap) = (settings.Enabled, settings.IntervalHours, settings.LastSwapUtc);
            settings.Enabled = enabled;
            settings.IntervalHours = hours;
            settings.LastSwapUtc = _time.GetUtcNow();
            try
            {
                Save();
            }
            catch
            {
                (settings.Enabled, settings.IntervalHours, settings.LastSwapUtc) = (previousEnabled, previousHours, previousSwap);
                throw;
            }

            Logger.Log(enabled
                ? $"Automatic playlist swap on: every {FormatInterval(AutoSwapInterval)}, the next one at {NextSwapUtc:u}."
                : $"Automatic playlist swap off (interval {FormatInterval(AutoSwapInterval)}).");
            AutoSwapChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The playlist that comes next: the one after the selected playlist in the list, starting over after the last one
        /// (after "All playlists" the first one). Playlists that could not be loaded, and those without videos, are skipped.
        /// <c>null</c> if no other playlist can be played.
        /// </summary>
        public PlaylistInfo? GetNextPlaylist()
        {
            var start = SelectedKey == null ? -1 : _playlists.FindIndex(p => p.Key == SelectedKey);
            for (var step = 1; step <= _playlists.Count; step++)
            {
                var playlist = _playlists[(start + step + _playlists.Count) % _playlists.Count];
                if (playlist.Key != SelectedKey && IsPlayable(playlist))
                    return playlist;
            }

            return null;
        }

        /// <summary>Loaded with videos, or still to be loaded (a failed playlist and an empty one are not).</summary>
        private static bool IsPlayable(PlaylistInfo playlist) => playlist.HasValidId && playlist.State switch
        {
            PlaylistLoadState.Loaded => playlist.VideoIds.Count > 0,
            PlaylistLoadState.Failed => false,
            _ => true
        };

        /// <summary>
        /// Switches to <see cref="GetNextPlaylist"/>. With <paramref name="keepCurrentVideo"/> (the automatic swap) the
        /// current video plays to its end and only the videos after it come from the next playlist; otherwise it is
        /// skipped, as with any other switch. Returns the playlist that is selected now, or <c>null</c> if there is none.
        /// </summary>
        public async Task<PlaylistInfo?> SwitchToNextAsync(bool keepCurrentVideo)
        {
            var next = GetNextPlaylist();
            if (next == null)
                return null;

            await SelectAsync(next.Key, keepCurrentVideo);
            return next;
        }

        public string? NextPlaylistTitle => GetNextPlaylist()?.Title;

        async Task<string?> IPlaylistSwitch.SwitchToNextPlaylistAsync() =>
            (await SwitchToNextAsync(keepCurrentVideo: false))?.Title;

        /// <summary>
        /// A file without a start of the interval (written by hand), or with one in the future (the clock was turned back),
        /// would delay the swap for an unknown time: the interval starts now.
        /// </summary>
        private void CheckSwapStart()
        {
            var settings = _library.AutoSwap;
            var now = _time.GetUtcNow();
            if (!settings.Enabled || (settings.LastSwapUtc is { } last && last <= now))
                return;

            settings.LastSwapUtc = now;
            try
            {
                Save();
            }
            catch
            {
                // Logged by Save; the start is kept in memory and saved with the next change.
            }
        }

        internal static string FormatInterval(TimeSpan interval)
        {
            var days = (int)interval.TotalDays;
            var hours = interval.Hours;
            var parts = new List<string>();
            if (days > 0)
                parts.Add(days == 1 ? "1 day" : $"{days} days");
            if (hours > 0 || days == 0)
                parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
            return string.Join(" ", parts);
        }

        #endregion

        /// <summary>
        /// Switches playback to a playlist (or to all playlists when <paramref name="playlistKey"/> is <c>null</c>).
        /// Waits for the playlist if it is still loading. If it cannot be loaded, the previous selection is kept.
        /// A change of the selection starts the interval of the automatic swap over.
        /// </summary>
        /// <param name="keepCurrentVideo">
        /// The current video plays to its end instead of being skipped (see <see cref="PoolChangedEventArgs.KeepCurrentVideo"/>).
        /// </param>
        public async Task SelectAsync(string? playlistKey, bool keepCurrentVideo = false)
        {
            PlaylistInfo? playlist = null;
            if (playlistKey != null)
            {
                playlist = Find(playlistKey) ?? throw new InvalidOperationException("The playlist no longer exists.");
                await EnsureLoadedAsync(playlist);

                // It may have been removed while it was loading.
                if (!_playlists.Contains(playlist))
                    throw new InvalidOperationException("The playlist no longer exists.");
            }

            // Saved first: if that fails, the selection stays as it was.
            var changed = playlist?.Key != SelectedKey;
            var previousUrl = _library.SelectedPlaylistUrl;
            var previousSwap = _library.AutoSwap.LastSwapUtc;
            _library.SelectedPlaylistUrl = playlist?.Url;
            if (changed)
                _library.AutoSwap.LastSwapUtc = _time.GetUtcNow();
            try
            {
                Save();
            }
            catch
            {
                _library.SelectedPlaylistUrl = previousUrl;
                _library.AutoSwap.LastSwapUtc = previousSwap;
                throw;
            }

            SelectedKey = playlist?.Key;
            RebuildPool(selectionChanged: true, keepCurrentVideo);
            if (changed)
                AutoSwapChanged?.Invoke(this, EventArgs.Empty);
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
            try
            {
                Save();
            }
            catch
            {
                // Otherwise the entry would be written with the next save, and a second try would add it again.
                _library.Playlists.Remove(entry);
                throw;
            }

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
            var previousTitle = playlist.Entry.Title;
            playlist.Entry.Title = newTitle.Trim();
            try
            {
                Save();
            }
            catch
            {
                playlist.Entry.Title = previousTitle;
                throw;
            }

            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(string playlistKey)
        {
            var playlist = Find(playlistKey);
            if (playlist == null)
                return;

            // Saved first: if that fails, the playlist stays in the list and keeps its cache.
            var wasSelected = SelectedKey == playlist.Key;
            var previousUrl = _library.SelectedPlaylistUrl;
            var swap = _library.AutoSwap;
            var (previousSwapEnabled, previousSwap) = (swap.Enabled, swap.LastSwapUtc);
            var index = _library.Playlists.IndexOf(playlist.Entry);
            _library.Playlists.Remove(playlist.Entry);
            if (wasSelected)
            {
                _library.SelectedPlaylistUrl = null;
                swap.LastSwapUtc = _time.GetUtcNow(); // the playlist changed
            }

            // With a single playlist left there is nothing to swap to.
            var swapTurnedOff = swap.Enabled && _library.Playlists.Count < 2;
            if (swapTurnedOff)
            {
                swap.Enabled = false;
                swap.LastSwapUtc = _time.GetUtcNow();
            }

            try
            {
                Save();
            }
            catch
            {
                if (index >= 0)
                    _library.Playlists.Insert(index, playlist.Entry);

                _library.SelectedPlaylistUrl = previousUrl;
                (swap.Enabled, swap.LastSwapUtc) = (previousSwapEnabled, previousSwap);
                throw;
            }

            _playlists.Remove(playlist);
            _cache.Delete(playlist.Key);
            if (wasSelected)
                SelectedKey = null;

            if (swapTurnedOff)
                Logger.Log("Automatic playlist swap disabled: fewer than two playlists are left.");

            PlaylistsChanged?.Invoke(this, EventArgs.Empty);

            if (wasSelected || SelectedKey == null)
                RebuildPool(selectionChanged: wasSelected);

            // Also when nothing about the swap changed: without the removed playlist another one may come next.
            AutoSwapChanged?.Invoke(this, EventArgs.Empty);
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

                    // YouTube sometimes answers with nothing at all (a consent or region page, a playlist that was made
                    // private). That must not replace a list that works: the update fails and the old list stays.
                    if (data.VideoIds.Count == 0 && playlist.VideoIds.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"YouTube returned no videos for \"{playlist.Title}\". Its {playlist.VideoIds.Count} known videos are kept.");
                    }

                    // The playlist may have been removed while it was downloading. A playlist without videos is not
                    // cached, so the next start asks YouTube again instead of remembering an empty answer.
                    if (data.VideoIds.Count > 0 && _playlists.Contains(playlist))
                        await TryWriteCacheAsync(data);

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

        /// <summary>The cache only saves a download the next time; failing to write it must not lose the download.</summary>
        private async Task TryWriteCacheAsync(PlaylistCacheData data)
        {
            try
            {
                await Task.Run(() => _cache.Write(data));
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not cache the video IDs of playlist '{data.Title}'. They are downloaded again next time.", ex);
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

        private void RebuildPool(bool selectionChanged, bool keepCurrentVideo = false)
        {
            IEnumerable<PlaylistInfo> sources = SelectedKey == null
                ? _playlists
                : _playlists.Where(p => p.Key == SelectedKey);

            var pool = sources
                .Where(p => p.VideoIds.Count > 0)
                .SelectMany(p => p.VideoIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // A video that was chosen in advance is only out of date if the videos, their order or the way they are
            // picked changed. A rebuild that changes nothing (e.g. an update that found nothing new) must not drop it.
            var shuffled = IsShuffled;
            var sequenceChanged = selectionChanged || shuffled != _poolShuffled || !pool.SequenceEqual(_pool, StringComparer.Ordinal);
            _pool = pool;
            _poolShuffled = shuffled;

            if (sequenceChanged)
                SequenceChanged?.Invoke(this, EventArgs.Empty);

            PoolChanged?.Invoke(this, new PoolChangedEventArgs(selectionChanged, selectionChanged && keepCurrentVideo));
        }

        private void Save()
        {
            if (_libraryLoadFailed)
                throw new InvalidOperationException("playlists.json could not be read when the application started, so nothing is saved: it would replace the playlists in that file. Restart the application.");

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
