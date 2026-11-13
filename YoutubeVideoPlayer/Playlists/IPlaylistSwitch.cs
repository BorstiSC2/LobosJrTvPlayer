using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Playlists
{
    /// <summary>
    /// Changes to the next playlist (implemented by <see cref="PlaylistManager"/>); what the chat command
    /// <c>!skipplaylist</c> works with.
    /// </summary>
    public interface IPlaylistSwitch
    {
        /// <summary>
        /// The playlist that comes next: the one after the playing playlist in the list (starting over after the last
        /// one), skipping playlists that could not be loaded. <c>null</c> if there is no other playlist to play.
        /// </summary>
        string? NextPlaylistTitle { get; }

        /// <summary>
        /// Switches to the next playlist now (the current video is skipped, as with a switch in the playlist dialog) and
        /// returns its title, or <c>null</c> if there is no other playlist to play.
        /// </summary>
        Task<string?> SwitchToNextPlaylistAsync();
    }
}
