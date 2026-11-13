using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using YoutubeExplode;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.Twitch.Commands;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.UI;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer
{
    /// <summary>Composition root: creates the managers, wires them together and disposes them on exit.</summary>
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Initialize LibVLC native libraries
            Core.Initialize();

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // The managers capture the UI context when they are created, so install it before creating them.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            Logger.Clear();

            using var youtubeClient = new YoutubeClient();
            var youtube = new YouTubeService(youtubeClient);
            var playlists = new PlaylistManager(youtube, new PlaylistStore(), new PlaylistCache());

            VlcMediaPlayer player;
            try
            {
                player = new VlcMediaPlayer();
            }
            catch (Exception ex)
            {
                Logger.Log("Initializing the media player failed.", ex);
                MessageBox.Show($"Initialisierungsfehler: {ex.Message}");
                return;
            }

            var playback = new PlaybackManager(youtube, playlists, player);

            var twitchSettings = TwitchSettings.Load();
            var twitchTokens = twitchSettings is { IsComplete: true, UsesAppSignIn: true }
                ? new TwitchTokenManager(new TwitchAuthClient(twitchSettings.ClientId), new TwitchTokenStore())
                : null;
            var twitch = twitchSettings is { IsComplete: true } ? new TwitchChatService(twitchSettings, twitchTokens) : null;
            SkipVoteManager? votes = null;
            ChatCommandManager? commands = null;
            AutoVoteManager? autoVote = null;
            PlaylistAnnouncer? announcer = null;
            if (twitch != null)
            {
                votes = new SkipVoteManager(
                    twitch,
                    playback,
                    playlists,
                    youtube,
                    TimeSpan.FromSeconds(twitchSettings!.SkipVoteDurationSeconds),
                    SkipVoteManager.OptionCount);
                autoVote = new AutoVoteManager(twitch, playback, votes);
                announcer = new PlaylistAnnouncer(twitch, playlists, playback);

                commands = new ChatCommandManager(
                    twitch,
                    new IChatCommand[]
                    {
                        new TimestampCommand(playback, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        new CurrentCommand(playback, TimeSpan.FromSeconds(twitchSettings.CurrentCooldownSeconds)),
                        new SkipCommand(playback, TimeSpan.FromSeconds(twitchSettings.SkipCooldownSeconds)),
                        new PlayCommand(playback, TimeSpan.FromSeconds(twitchSettings.SkipCooldownSeconds)),
                        new PauseCommand(playback, TimeSpan.FromSeconds(twitchSettings.SkipCooldownSeconds)),
                        new ShuffleStatusCommand(playlists, TimeSpan.FromSeconds(twitchSettings.ShuffleCooldownSeconds)),
                        new ToggleShuffleCommand(playlists, TimeSpan.FromSeconds(twitchSettings.ShuffleCooldownSeconds)),
                        new PlaylistCommand(playlists, TimeSpan.FromSeconds(twitchSettings.CurrentCooldownSeconds)),
                        new SkipPlaylistCommand(playlists, TimeSpan.FromSeconds(twitchSettings.SkipCooldownSeconds)),
                        new AutoVoteStatusCommand(autoVote, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        new ToggleAutoVoteCommand(autoVote, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        new TokenCommand(twitchTokens, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        // The two lists read the registered commands when they run, so new commands appear without changes here.
                        new VodCommandsCommand(() => commands!.Commands, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        new ModVodCommandsCommand(() => commands!.Commands, TimeSpan.FromSeconds(twitchSettings.TimestampCooldownSeconds)),
                        new SkipVoteCommand(votes),
                        new CancelVoteCommand(votes),
                        new VoteCommand(votes)
                    },
                    twitchSettings.PrivilegedUsers);
            }

            using var coordinator = new AppCoordinator(playback, playlists, twitch);
            using var swapScheduler = new PlaylistSwapScheduler(playlists, playback, votes == null ? null : () => votes.IsRunning);

            try
            {
                Application.Run(new VideoPlayerForm(
                    playback, playlists, coordinator, player.MediaPlayer, twitch, twitchTokens, twitchSettings?.BotUsername ?? string.Empty, autoVote));
            }
            finally
            {
                autoVote?.Dispose();
                announcer?.Dispose();
                commands?.Dispose();
                votes?.Dispose();
                if (twitch != null)
                {
                    // The message loop has ended, so disconnect on the thread pool and don't wait forever.
                    Task.Run(() => twitch.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
                }

                twitchTokens?.Dispose();

                playback.Dispose();
                player.Dispose();
            }
        }
    }
}
