namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// The shuffle setting of the playlist that is playing (implemented by <see cref="PlaylistManager"/>);
    /// what the chat commands <c>!shufflestatus</c> and <c>!toggleshuffle</c> work with.
    /// </summary>
    public interface IShuffleControl
    {
        /// <summary>The title of the playlist that is playing, or <c>null</c> if all playlists are combined.</summary>
        string? PlaylistTitle { get; }

        /// <summary>
        /// <c>false</c> while all playlists are combined: that mix always plays in random order,
        /// because there is no single playlist order.
        /// </summary>
        bool CanChangeShuffle { get; }

        /// <summary>The videos play in random order (otherwise one after the other in playlist order).</summary>
        bool IsShuffled { get; }

        /// <summary>Switches the setting of the playing playlist, saves it and returns the new value.</summary>
        /// <exception cref="System.InvalidOperationException"><see cref="CanChangeShuffle"/> is <c>false</c>.</exception>
        bool ToggleShuffle();
    }
}
