using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// Changes to the next playlist (implemented by <see cref="PlaylistManager"/>); what the chat commands
    /// <c>!skipplaylist</c>, <c>!playlistshufflestatus</c> and <c>!toggleplaylistshuffle</c> work with.
    /// </summary>
    public interface IPlaylistSwitch
    {
        /// <summary>
        /// Whether there is another playlist to switch to: one that is not the playing playlist and could be loaded.
        /// </summary>
        bool HasOtherPlaylist { get; }

        /// <summary>
        /// The next playlist is a random other one; otherwise the playlists take turns in the order of the list.
        /// It applies to the automatic swap and to <see cref="SwitchToNextPlaylistAsync"/>.
        /// </summary>
        bool RandomOrder { get; }

        /// <summary>Switches <see cref="RandomOrder"/>, saves it and returns the new value.</summary>
        bool ToggleRandomOrder();

        /// <summary>
        /// Switches to the next playlist now (the current video is skipped, as with a switch in the playlist dialog) and
        /// returns its title, or <c>null</c> if there is no other playlist to play.
        /// </summary>
        Task<string?> SwitchToNextPlaylistAsync();
    }
}
