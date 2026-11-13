using System;
using System.Collections.Generic;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>One playlist as stored in <c>playlists.json</c>.</summary>
    public sealed class PlaylistEntry
    {
        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// <c>true</c>: the videos play in random order; <c>false</c>: one after the other in playlist order.
        /// Entries without this value (older <c>playlists.json</c> files) are shuffled, as before.
        /// </summary>
        public bool Shuffle { get; set; } = true;
    }

    /// <summary>The content of <c>playlists.json</c>.</summary>
    public sealed class PlaylistLibrary
    {
        /// <summary>URL of the playlist that plays; <c>null</c> means all playlists combined.</summary>
        public string? SelectedPlaylistUrl { get; set; }

        public List<PlaylistEntry> Playlists { get; set; } = new();

        /// <summary>The automatic playlist swap (off unless the user turned it on).</summary>
        public AutoSwapSettings AutoSwap { get; set; } = new();
    }

    /// <summary>
    /// The automatic playlist swap, stored in <c>playlists.json</c> so that a restart does not reset it: once
    /// <see cref="IntervalHours"/> have passed since <see cref="LastSwapUtc"/>, the next playlist takes over.
    /// </summary>
    public sealed class AutoSwapSettings
    {
        public const int MinIntervalHours = 1;

        /// <summary>What the playlist dialog can show: 365 days and 23 hours.</summary>
        public const int MaxIntervalHours = 365 * 24 + 23;

        public const int DefaultIntervalHours = 7 * 24;

        public bool Enabled { get; set; }

        /// <summary>How long each playlist plays before the next one takes over.</summary>
        public int IntervalHours { get; set; } = DefaultIntervalHours;

        /// <summary>
        /// When the interval started: the last change of the playlist (an automatic swap, or one by hand) or of this
        /// setting. <c>null</c> in files written before the setting existed.
        /// </summary>
        public DateTimeOffset? LastSwapUtc { get; set; }
    }

    /// <summary>The cached video IDs of one playlist, stored in <c>PlaylistCache/&lt;playlistId&gt;.json</c>.</summary>
    public sealed class PlaylistCacheData
    {
        public string PlaylistId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public DateTime FetchedAtUtc { get; set; }

        public List<string> VideoIds { get; set; } = new();
    }

    public enum PlaylistLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed
    }

    /// <summary>Runtime state of one playlist, as shown in the playlist dialog.</summary>
    public sealed class PlaylistInfo
    {
        internal PlaylistInfo(PlaylistEntry entry, string key, bool hasValidId)
        {
            Entry = entry;
            Key = key;
            HasValidId = hasValidId;
        }

        internal PlaylistEntry Entry { get; }

        /// <summary>The YouTube playlist ID (or the raw URL if it could not be parsed).</summary>
        public string Key { get; }

        internal bool HasValidId { get; }

        public string Title => Entry.Title;

        public string Url => Entry.Url;

        /// <summary>The videos play in random order (otherwise in playlist order).</summary>
        public bool Shuffle => Entry.Shuffle;

        public PlaylistLoadState State { get; internal set; } = PlaylistLoadState.NotLoaded;

        public IReadOnlyList<string> VideoIds { get; internal set; } = Array.Empty<string>();

        public DateTime? FetchedAtUtc { get; internal set; }

        /// <summary>The last load or update error, if any.</summary>
        public string? Error { get; internal set; }
    }

    /// <summary>The outcome of re-downloading a playlist.</summary>
    public sealed record PlaylistUpdateResult(string Title, int Added, int Removed, int Total);

    /// <summary>What YouTube reports for a playlist link.</summary>
    public sealed record YouTubePlaylistDetails(string PlaylistId, string Title, string NormalizedUrl);

    public sealed class PoolChangedEventArgs : EventArgs
    {
        public PoolChangedEventArgs(bool selectionChanged, bool keepCurrentVideo = false)
        {
            SelectionChanged = selectionChanged;
            KeepCurrentVideo = keepCurrentVideo;
        }

        /// <summary>
        /// <c>true</c> if another playlist was selected. The current video is skipped, unless <see cref="KeepCurrentVideo"/> is set.
        /// </summary>
        public bool SelectionChanged { get; }

        /// <summary>
        /// The selection changed, but the current video plays to its end (an automatic swap): only the videos after it
        /// come from the new selection.
        /// </summary>
        public bool KeepCurrentVideo { get; }
    }
}
