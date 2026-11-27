using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.Twitch.Commands;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.UI;
using YouTubeVideoPlayer.YouTube;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer
{
    /// <summary>Composition root: creates the managers, wires them together and disposes them on exit.</summary>
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Initialize LibVLC native libraries
            Core.Initialize();

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // The managers capture the UI context when they are created, so install it before creating them.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            // Prototype: "YouTubeVideoPlayer.exe --youtube-signin" opens only the YouTube sign-in window (no player, no
            // Twitch bot), and leaves the log of a player that may be running as it is.
            if (args.Any(a => a.Equals("--youtube-signin", StringComparison.OrdinalIgnoreCase)))
            {
                var settings = AppSettings.Load();
                var ytDlpPath = YtDlpLocator.Find(settings.YouTube.YtDlp.Path, AppDomain.CurrentDomain.BaseDirectory);
                Logger.Log("YouTube sign-in window opened (--youtube-signin).");
                Application.Run(new YouTubeSignInDialog(ytDlpPath));
                return;
            }

            Logger.Clear();

            // YoutubeExplode or yt-dlp, with the other as fallback, as appsettings.json says.
            var appSettings = AppSettings.Load();
            using var youtubeBackends = YouTubeBackends.Create(appSettings.YouTube);
            Logger.Log($"YouTube backend: {youtubeBackends.Description}.");
            if (youtubeBackends.Problem != null)
            {
                Logger.Log(youtubeBackends.Problem);
                MessageBox.Show(youtubeBackends.Problem, "yt-dlp not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Only tells the user when a newer yt-dlp has been released; the app never updates it.
            using var updateHttp = new HttpClient();
            var ytDlpUpdateNotice = youtubeBackends.YtDlpRunner is { } ytDlpRunner && appSettings.YouTube.YtDlp.CheckForUpdates
                ? Task.Run(() => YtDlpUpdateCheck.CheckAsync(ytDlpRunner, updateHttp, TimeProvider.System, CancellationToken.None))
                : null;

            var youtube = youtubeBackends.Service;
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
            // When the "now playing" card is up: at every video start, every 10 minutes, and on !current.
            using var nowPlaying = new NowPlayingSchedule(playback);
            using var avatars = AvatarSet.Load();

            var twitchSettings = TwitchSettings.Load();
            var twitchTokens = twitchSettings is { IsComplete: true, UsesAppSignIn: true }
                ? new TwitchTokenManager(new TwitchAuthClient(twitchSettings.SignIn.ClientId), new TwitchTokenStore())
                : null;
            var twitch = twitchSettings is { IsComplete: true } ? new TwitchChatService(twitchSettings, twitchTokens) : null;
            SkipVoteManager? votes = null;
            ChatCommandManager? commands = null;
            AutoVoteManager? autoVote = null;
            ChatActivityTracker? chatters = null;
            VoteSkipManager? voteSkip = null;
            PlaylistAnnouncer? announcer = null;
            TwitchStreamClient? streams = null;
            MainChannelWatcher? mainChannel = null;
            if (twitch != null)
            {
                votes = new SkipVoteManager(
                    twitch,
                    playback,
                    playlists,
                    youtube,
                    TimeSpan.FromSeconds(twitchSettings!.ForceVoteSkip.DurationSeconds),
                    SkipVoteManager.OptionCount)
                {
                    RevoteDuration = TimeSpan.FromSeconds(twitchSettings.Revote.DurationSeconds)
                };
                chatters = new ChatActivityTracker(
                    twitch,
                    TimeSpan.FromMinutes(twitchSettings.ChatterAverage.WindowMinutes),
                    TimeSpan.FromMinutes(twitchSettings.ChatterAverage.BucketMinutes));
                var chatterAverage = chatters;
                voteSkip = new VoteSkipManager(
                    twitch,
                    playback,
                    votes,
                    () => chatterAverage.AverageUniqueChatters,
                    TimeSpan.FromSeconds(twitchSettings.VoteSkip.DurationSeconds),
                    TimeSpan.FromMinutes(twitchSettings.VoteSkip.CooldownMinutes),
                    twitchSettings.VoteSkip.RequiredPercent)
                {
                    NextVideoVoteDuration = TimeSpan.FromSeconds(twitchSettings.VoteSkip.NextVideoVoteDurationSeconds)
                };
                var runningVoteSkip = voteSkip;
                autoVote = new AutoVoteManager(
                    twitch,
                    playback,
                    votes,
                    leadTime: TimeSpan.FromSeconds(twitchSettings.AutoVote.LeadTimeSeconds),
                    isOtherVoteRunning: () => runningVoteSkip.IsRunning,
                    voteDuration: TimeSpan.FromSeconds(twitchSettings.AutoVote.DurationSeconds));
                announcer = new PlaylistAnnouncer(twitch, playlists, playback);

                // The main channel is only watched through the Twitch API; the bot stays in the VOD chat.
                if (twitchSettings.MainChannel.Length > 0)
                {
                    streams = new TwitchStreamClient(TwitchStreamClient.CredentialsFor(twitchSettings, twitchTokens));
                    mainChannel = new MainChannelWatcher(twitch, streams, twitchSettings.MainChannel);
                    mainChannel.Start();
                }

                var cooldowns = twitchSettings.CommandCooldownSeconds;
                static TimeSpan Cooldown(int seconds) => TimeSpan.FromSeconds(seconds);

                commands = new ChatCommandManager(
                    twitch,
                    new IChatCommand[]
                    {
                        new CurrentCommand(playback, Cooldown(cooldowns.Current), nowPlaying.Show),
                        new ForceSkipCommand(playback, Cooldown(cooldowns.PlaybackControl)),
                        new ReloadCommand(playback, Cooldown(cooldowns.PlaybackControl)),
                        new PlayCommand(playback, Cooldown(cooldowns.PlaybackControl)),
                        new PauseCommand(playback, Cooldown(cooldowns.PlaybackControl)),
                        new ShuffleStatusCommand(playlists, Cooldown(cooldowns.ShuffleStatus)),
                        new ToggleShuffleCommand(playlists, Cooldown(cooldowns.ToggleShuffle)),
                        new PlaylistCommand(playlists, Cooldown(cooldowns.Playlist)),
                        new SkipPlaylistCommand(playlists, Cooldown(cooldowns.PlaybackControl)),
                        new PlaylistShuffleStatusCommand(playlists, Cooldown(cooldowns.PlaylistShuffleStatus)),
                        new TogglePlaylistShuffleCommand(playlists, Cooldown(cooldowns.TogglePlaylistShuffle)),
                        new AutoVoteStatusCommand(autoVote, Cooldown(cooldowns.AutoVoteStatus)),
                        new ToggleAutoVoteCommand(autoVote, Cooldown(cooldowns.ToggleAutoVote)),
                        new TokenCommand(twitchTokens, Cooldown(cooldowns.Token)),
                        // The two lists and the help read the registered commands when they run, so new commands appear without changes here.
                        new VodCommandsCommand(() => commands!.Commands, Cooldown(cooldowns.VodCommands)),
                        new ModVodCommandsCommand(() => commands!.Commands, Cooldown(cooldowns.ModVodCommands)),
                        new CommandHelpCommand(() => commands!.Commands, Cooldown(cooldowns.CommandHelp)),
                        new VoteSkipCommand(voteSkip, Cooldown(cooldowns.VoteSkip)),
                        new VoteInfoCommand(votes, voteSkip, Cooldown(cooldowns.VoteInfo)),
                        new ForceVoteSkipCommand(votes, voteSkip),
                        new CancelVoteCommand(votes, voteSkip),
                        // Ballots: not listed by !vodcommands, the votes name them.
                        new VoteCommand(votes),
                        new SkipBallotCommand(voteSkip),
                        new StayBallotCommand(voteSkip)
                    },
                    twitchSettings.PrivilegedUsers);
            }

            using var coordinator = new AppCoordinator(playback, playlists, twitch);
            using var swapScheduler = new PlaylistSwapScheduler(
                playlists,
                playback,
                votes == null ? null : () => votes.IsRunning || voteSkip!.IsRunning,
                autoVoteLeadTime: autoVote?.LeadTime);

            try
            {
                var form = new VideoPlayerForm(
                    playback,
                    playlists,
                    coordinator,
                    player.MediaPlayer,
                    twitch,
                    twitchTokens,
                    twitchSettings?.SignIn.BotLogin ?? string.Empty,
                    autoVote,
                    votes == null ? null : new IVoteDisplay[] { voteSkip!, votes },
                    nowPlaying,
                    avatars,
                    youtubeBackends is { OffersSignIn: true, YtDlpRunner: { } signInYtDlp }
                        ? reason => new YouTubeSignInDialog(signInYtDlp.ExecutablePath, reason: reason)
                        : null);

                // YouTube asked yt-dlp for a sign-in (the bot check): the sign-in window opens by itself, once.
                var ui = SynchronizationContext.Current!;
                youtubeBackends.SignInRequested += (_, reason) => ui.Post(_ => form.OfferYouTubeSignIn(reason), null);

                if (ytDlpUpdateNotice != null)
                {
                    // Once the window is up (the check never fails: problems are only logged). OK just closes the box.
                    form.Shown += async (_, _) =>
                    {
                        var notice = await ytDlpUpdateNotice;
                        if (notice != null && !form.IsDisposed)
                            MessageBox.Show(form, notice, "yt-dlp update available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    };
                }

                Application.Run(form);
            }
            finally
            {
                autoVote?.Dispose();
                announcer?.Dispose();
                mainChannel?.Dispose();
                streams?.Dispose();
                commands?.Dispose();
                voteSkip?.Dispose();
                chatters?.Dispose();
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
