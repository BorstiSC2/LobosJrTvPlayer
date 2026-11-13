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
        public PoolChangedEventArgs(bool selectionChanged)
        {
            SelectionChanged = selectionChanged;
        }

        /// <summary><c>true</c> if the user picked another playlist (so the current video should be skipped).</summary>
        public bool SelectionChanged { get; }
    }
}
