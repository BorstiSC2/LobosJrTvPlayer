using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!shufflestatus</c> (everybody, it only reads): tells whether the playing playlist is shuffled
    /// (random order) or plays in playlist order.
    /// </summary>
    public sealed class ShuffleStatusCommand : IChatCommand
    {
        private readonly IShuffleControl _shuffle;

        public ShuffleStatusCommand(IShuffleControl shuffle, TimeSpan cooldown)
        {
            _shuffle = shuffle;
            Cooldown = cooldown;
        }

        public string Name => "shufflestatus";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(ShuffleTexts.Status(_shuffle, changed: false));
    }

    /// <summary>
    /// <c>!toggleshuffle</c> (moderators, broadcaster and privileged users): switches the playing playlist
    /// between random order and playlist order, saves it with the playlist and posts the new status.
    /// </summary>
    public sealed class ToggleShuffleCommand : IChatCommand
    {
        private readonly IShuffleControl _shuffle;

        public ToggleShuffleCommand(IShuffleControl shuffle, TimeSpan cooldown)
        {
            _shuffle = shuffle;
            Cooldown = cooldown;
        }

        public string Name => "toggleshuffle";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            if (!_shuffle.CanChangeShuffle)
                return Task.FromResult<string?>(ShuffleTexts.Status(_shuffle, changed: false));

            try
            {
                _shuffle.ToggleShuffle();
            }
            catch (Exception ex)
            {
                Logger.Log("Toggling the shuffle setting via chat failed.", ex);
                return Task.FromResult<string?>("The shuffle setting could not be changed.");
            }

            return Task.FromResult<string?>(ShuffleTexts.Status(_shuffle, changed: true));
        }
    }

    /// <summary>The chat texts that describe the shuffle setting.</summary>
    internal static class ShuffleTexts
    {
        public static string Status(IShuffleControl shuffle, bool changed)
        {
            if (!shuffle.CanChangeShuffle)
                return "All playlists are combined and always play in random order. Select a single playlist to change the order.";

            var playlist = string.IsNullOrWhiteSpace(shuffle.PlaylistTitle) ? "the current playlist" : $"\"{shuffle.PlaylistTitle}\"";
            var now = changed ? "now " : string.Empty;

            return shuffle.IsShuffled
                ? $"Shuffle is {now}ON for {playlist}: the videos play in random order."
                : $"Shuffle is {now}OFF for {playlist}: the videos play in playlist order.";
        }
    }
}
