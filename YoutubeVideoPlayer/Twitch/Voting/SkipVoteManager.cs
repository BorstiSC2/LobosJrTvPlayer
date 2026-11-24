using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Twitch.Voting
{
    /// <summary>
    /// Runs chat votes for the next video: offers videos from the current playlist (random ones, or the next
    /// episodes if it plays in order), collects
    /// <c>!vote n</c> messages for a fixed time and then plays the winner (ties are broken randomly).
    /// A vote started with <see cref="StartAfterCurrentAsync"/> is about the video that follows the
    /// current one: its winner is queued and plays when the current video has ended.
    /// </summary>
    /// <remarks>All members must be called on the UI thread (chat commands are delivered there).</remarks>
    public sealed class SkipVoteManager : IVoteDisplay, IDisposable
    {
        /// <summary>How many videos a vote offers.</summary>
        public const int OptionCount = 3;

        /// <summary>Pause between the messages that announce a vote (Twitch rate limits and readability).</summary>
        public static readonly TimeSpan MessageDelay = TimeSpan.FromSeconds(2);

        /// <summary>The shortest vote that is worth running (same lower bound as the setting).</summary>
        public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(15);

        /// <summary>How long posting the rules and all options takes; the countdown starts after that.</summary>
        public static TimeSpan AnnouncementDuration => MessageDelay * OptionCount;

        /// <summary>The length of a revote if none is set (<c>revote.durationSeconds</c>).</summary>
        public static readonly TimeSpan DefaultRevoteDuration = TimeSpan.FromSeconds(45);

        /// <summary>
        /// How many revotes may follow one another: when the winners of all of them fail too, the regular next video stays
        /// (YouTube is probably having trouble, and more votes would only fill the chat).
        /// </summary>
        public const int MaxRevotes = 2;

        private const int MaxTitleLength = 70;
        private static readonly TimeSpan TitleLookupTimeout = TimeSpan.FromSeconds(10);

        private readonly IChatChannel _chat;
        private readonly IPlaybackControl _playback;
        private readonly IVideoSource _videos;
        private readonly IYouTubeService _youtube;
        private readonly TimeSpan _duration;
        private readonly int _optionCount;
        private readonly TimeProvider _time;
        private readonly Random _random;
        private VoteSession? _session;
        private HandedOverWinner? _winner;
        private bool _disposed;

        /// <param name="random">Source for random tie-breaks; a new <see cref="Random"/> by default.</param>
        public SkipVoteManager(
            IChatChannel chat,
            IPlaybackControl playback,
            IVideoSource videos,
            IYouTubeService youtube,
            TimeSpan duration,
            int optionCount,
            TimeProvider? timeProvider = null,
            Random? random = null)
        {
            _chat = chat;
            _playback = playback;
            _videos = videos;
            _youtube = youtube;
            _duration = duration;
            _optionCount = optionCount;
            _time = timeProvider ?? TimeProvider.System;
            _random = random ?? new Random();
            _playback.RequestedVideoFailed += OnRequestedVideoFailed;
        }

        /// <summary>
        /// The countdown of a revote: the vote that runs again when a winner could not be played (the regular next video
        /// plays meanwhile, and the revote's winner replaces it at once).
        /// </summary>
        public TimeSpan RevoteDuration { get; init; } = DefaultRevoteDuration;

        public bool IsRunning => _session != null;

        public event EventHandler? DisplayChanged;

        /// <summary>
        /// The running vote for the vote ticker; <c>null</c> without one. While the options are still being looked up it has
        /// no options and no time (the ticker says the videos are being picked), so a vote shows from its start: a won
        /// <c>!voteskip</c> hands over to it without a gap.
        /// </summary>
        public VoteDisplay? Display
        {
            get
            {
                var session = _session;
                if (session == null)
                    return null;

                var kind = session.AfterCurrent ? VoteKind.VideoAfterCurrent : VoteKind.NextVideo;
                if (session.Options.Count == 0)
                    return new VoteDisplay(kind, Array.Empty<VoteDisplayOption>(), null);

                var counts = new int[session.Options.Count];
                foreach (var choice in session.Votes.Values)
                    counts[choice]++;

                var options = session.Options
                    .Select((o, i) => new VoteDisplayOption($"{i + 1}", Shorten(o.Title), $"!vote {i + 1}", counts[i]))
                    .ToList();
                TimeSpan? left = session.EndsAt is { } endsAt ? Max(TimeSpan.Zero, endsAt - _time.GetUtcNow()) : null;
                return new VoteDisplay(kind, options, left, Ending: IsEnding(session.Warned, session.Duration, left));
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        /// <summary>Votes of at least this length get a warning halfway through ("1 minute left! ...").</summary>
        internal static readonly TimeSpan WarningFrom = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether a vote is in its last part (<see cref="VoteDisplay.Ending"/>): once its warning has been posted, or, for a
        /// vote too short for one, from halfway on.
        /// </summary>
        internal static bool IsEnding(bool warned, TimeSpan duration, TimeSpan? left) =>
            warned || (duration < WarningFrom && left is { } remaining && remaining <= duration / 2);

        private void OnDisplayChanged() => DisplayChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// The length of a vote started by a moderator (<c>!forcevoteskip</c>): the countdown after the last option has been
        /// posted. The autovote and the vote after a won <c>!voteskip</c> pass their own.
        /// </summary>
        public TimeSpan Duration => _duration;

        /// <summary>
        /// Starts a vote whose winner plays right away. Returns a reply for the chat if the vote could
        /// not be started.
        /// </summary>
        public Task<string?> StartAsync(ChatCommandContext context) => StartAsync(context.Username);

        /// <inheritdoc cref="StartAsync(ChatCommandContext)"/>
        /// <param name="startedBy">Who started the vote, for the log (e.g. <c>!voteskip</c>).</param>
        /// <param name="duration">Length of the countdown; <see cref="Duration"/> by default.</param>
        public Task<string?> StartAsync(string startedBy, TimeSpan? duration = null) =>
            StartCoreAsync(startedBy, afterCurrent: false, duration ?? _duration);

        /// <summary>
        /// What <c>!voteinfo</c> says about the running vote: its options and the time left; <c>null</c> without a vote.
        /// </summary>
        public string? DescribeForChat()
        {
            var session = _session;
            if (session == null)
                return null;

            if (session.Options.Count == 0)
                return "A vote for the next video is being prepared.";

            var options = string.Join(", ", session.Options.Select((o, i) => $"{i + 1}) {Shorten(o.Title)}"));
            var left = session.EndsAt is { } endsAt
                ? $"{FormatTimeLeft(endsAt - _time.GetUtcNow())} left."
                : $"Voting ends {FormatDuration(session.Duration)} after the last option.";
            return $"{Headline(session)} {options} – vote with !vote 1-{session.Options.Count}. {left}";
        }

        /// <summary>"1:05" (minutes and seconds, rounded up); never negative.</summary>
        internal static string FormatTimeLeft(TimeSpan left)
        {
            var seconds = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        /// <summary>
        /// Starts a vote about the video that plays <i>after</i> the current one (used by the automatic
        /// vote shortly before a video ends). The winner is queued instead of played immediately.
        /// </summary>
        /// <param name="duration">Length of the countdown; shorter than <see cref="Duration"/> if the video ends soon.</param>
        /// <param name="mustEndBy">
        /// When the vote has to be over at the latest (on the clock of this manager). Looking up the options and posting
        /// them takes time, and the countdown only starts after that, so it is shortened by the time that is already gone:
        /// the vote then ends by this time, not that much later. If there is too little time left, no vote starts.
        /// </param>
        /// <param name="holdNextVideo">
        /// The vote may last longer than the current video: the next video waits until the vote is over (the player
        /// shows a black screen with the time left), and then the winner plays.
        /// </param>
        /// <returns>A message if the vote could not be started, otherwise <c>null</c>.</returns>
        public Task<string?> StartAfterCurrentAsync(TimeSpan duration, DateTimeOffset? mustEndBy = null, bool holdNextVideo = false) =>
            StartCoreAsync("the autovote", afterCurrent: true, duration, mustEndBy, holdNextVideo);

        /// <param name="revote">Set for the vote that repeats one whose winner could not be played.</param>
        private async Task<string?> StartCoreAsync(
            string startedBy, bool afterCurrent, TimeSpan duration, DateTimeOffset? mustEndBy = null, bool holdNextVideo = false,
            HandedOverWinner? revote = null)
        {
            if (_session != null)
            {
                return _session.Options.Count > 0
                    ? $"A vote is already running – vote with !vote 1-{_session.Options.Count}."
                    : "A vote is already being prepared.";
            }

            var session = new VoteSession(duration, afterCurrent, _playback.Current) { FailedWinner = revote };
            _session = session;
            OnDisplayChanged(); // "being prepared"
            Logger.Log($"Skip vote started by {startedBy}{(afterCurrent ? " (for the video after the current one)" : string.Empty)}.");

            // At once, not after the option lookup: a short video may end while the options are being looked up.
            if (afterCurrent && holdNextVideo)
                session.Hold = _playback.HoldNextVideo();

            List<VoteOption> options;
            try
            {
                options = await PickOptionsAsync(revote?.VideoId, session.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return null; // cancelled while preparing; !cancelvote already replied
            }
            catch (Exception ex)
            {
                Logger.Log("Preparing the skip vote failed.", ex);
                EndSession(session);
                return "The vote could not be started.";
            }

            if (session.Cancellation.IsCancellationRequested)
                return null;

            if (options.Count < 2)
            {
                EndSession(session);
                return "There are not enough videos available for a vote.";
            }

            if (mustEndBy is { } deadline)
            {
                var room = deadline - _time.GetUtcNow() - MessageDelay * options.Count;
                if (room < session.Duration)
                    session.Duration = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, room.TotalSeconds)));

                if (session.Duration < MinimumDuration)
                {
                    EndSession(session);
                    return "There is not enough time left for a vote.";
                }
            }

            session.Options.AddRange(options);
            if (session.Hold != null)
                session.Hold.Until = _time.GetUtcNow() + MessageDelay * options.Count + session.Duration;

            OnDisplayChanged();

            RunVoteAsync(session).FireAndForget("The skip vote failed.");
            return null;
        }

        /// <summary>Cancels the running vote. Returns the chat reply.</summary>
        public string Cancel(ChatCommandContext context)
        {
            var session = _session;
            if (session == null)
                return "There is no vote running.";

            EndSession(session);
            Logger.Log($"Skip vote cancelled by {context.Username}.");
            return $"The vote was cancelled by {context.DisplayName}.";
        }

        /// <summary>Registers a vote (<c>!vote n</c>). Each user has one vote; voting again replaces it.</summary>
        public void Vote(ChatCommandContext context)
        {
            var session = _session;
            if (session == null || session.Options.Count == 0 || context.Arguments.Count == 0)
                return;

            var argument = context.Arguments[0].Trim('#', ')', '.', ' ');
            if (!int.TryParse(argument, out var number) || number < 1 || number > session.Options.Count)
                return;

            var voterKey = string.IsNullOrEmpty(context.UserId) ? context.Username : context.UserId;
            if (session.Votes.TryGetValue(voterKey, out var earlier) && earlier == number - 1)
                return;

            session.Votes[voterKey] = number - 1;
            OnDisplayChanged();
        }

        private async Task RunVoteAsync(VoteSession session)
        {
            try
            {
                await RunVoteCoreAsync(session);
            }
            catch (OperationCanceledException)
            {
                // The vote was cancelled.
            }
            catch
            {
                // Never leave a dead session behind: it would block every later vote.
                EndSession(session);
                throw;
            }
        }

        private async Task RunVoteCoreAsync(VoteSession session)
        {
            var token = session.Cancellation.Token;
            var range = $"!vote 1-{session.Options.Count}";

            // Voting is already open while the options are being posted (Vote only needs session.Options),
            // but the countdown below only starts once the last option is in the chat.
            var waits = session.Hold != null ? " The next video starts when voting has ended." : string.Empty;
            await TrySendAsync(
                $"{Headline(session)} Pick one of the {session.Options.Count} options below by typing its command – " +
                $"you can change your vote at any time. Voting ends {FormatDuration(session.Duration)} after the last option.{waits}");

            for (var i = 0; i < session.Options.Count; i++)
            {
                await Task.Delay(MessageDelay, _time, token);
                await TrySendAsync($"{i + 1}) {Shorten(session.Options[i].Title)} – vote with !vote {i + 1}");
            }

            // Now the end is exact (sending may have taken longer than planned).
            session.EndsAt = _time.GetUtcNow() + session.Duration;
            if (session.Hold != null)
                session.Hold.Until = session.EndsAt;

            OnDisplayChanged();

            // Remind the chat halfway through longer votes; the ticker shows the vote is ending from the same moment.
            var reminderAt = session.Duration >= WarningFrom ? session.Duration / 2 : TimeSpan.Zero;
            if (reminderAt > TimeSpan.Zero)
            {
                await Task.Delay(reminderAt, _time, token);
                session.Warned = true;
                OnDisplayChanged();
                await TrySendAsync($"{FormatDuration(session.Duration - reminderAt)} left! {FormatStandings(session)} – vote with {range}.");
            }

            await Task.Delay(session.Duration - reminderAt, _time, token);

            if (session.Cancellation.IsCancellationRequested)
                return;

            // The hold is released only once the winner is queued, otherwise the regular next video would start.
            var hold = session.Hold;
            session.Hold = null;
            try
            {
                EndSession(session);
                var (winner, message) = DetermineWinner(session);
                Logger.Log($"Skip vote finished: {message}");

                // Before the hand-over: playback reports a failure of the winner while it is still being handed over.
                _winner = new HandedOverWinner(winner.VideoId, winner.Title, (session.FailedWinner?.Revotes ?? -1) + 1);
                await TrySendAsync(message);

                if (session.AfterCurrent)
                    _playback.QueueNextVideo(winner.VideoId);
                else
                    await _playback.PlayVideoAsync(winner.VideoId);
            }
            finally
            {
                hold?.Release();
            }
        }

        /// <summary>The first words of the rules message; they say which video the vote is about.</summary>
        private static string Headline(VoteSession session)
        {
            if (session.FailedWinner is { } failed)
                return $"\"{Shorten(failed.Title)}\" could not be played – vote again for the next video!";

            if (!session.AfterCurrent)
                return "Vote for the next video!";

            var title = session.CurrentAtStart?.Title;
            return string.IsNullOrWhiteSpace(title)
                ? "Vote for the video that plays AFTER the current one!"
                : $"Vote for the video that plays AFTER the current one (\"{Shorten(title)}\")!";
        }

        private static string PlaysText(VoteSession session) =>
            session.AfterCurrent ? "It plays after the current video." : "Playing it now.";

        /// <summary>Sends a chat message; a failing chat must not stop the vote or the playback of the winner.</summary>
        private async Task TrySendAsync(string message)
        {
            try
            {
                await _chat.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                Logger.Log("Sending a vote message to the chat failed.", ex);
            }
        }

        private (VoteOption Winner, string Message) DetermineWinner(VoteSession session)
        {
            var totalVotes = session.Votes.Count;
            if (totalVotes == 0)
            {
                var random = session.Options[_random.Next(session.Options.Count)];
                return (random, $"Nobody voted – randomly picked: \"{Shorten(random.Title)}\"! {PlaysText(session)}");
            }

            var counts = new int[session.Options.Count];
            foreach (var choice in session.Votes.Values)
                counts[choice]++;

            var best = counts.Max();
            var leaders = Enumerable.Range(0, counts.Length).Where(i => counts[i] == best).ToList();
            var winnerIndex = leaders[_random.Next(leaders.Count)];
            var winner = session.Options[winnerIndex];

            if (leaders.Count > 1)
            {
                var tied = string.Join(", ", leaders.Select(i => $"{i + 1})"));
                return (winner, $"Tie between {tied} with {Votes(best)} each – randomly picked: \"{Shorten(winner.Title)}\"! {PlaysText(session)}");
            }

            return (winner, $"Voting ended: \"{Shorten(winner.Title)}\" wins with {best} of {Votes(totalVotes)}! {PlaysText(session)}");
        }

        /// <param name="exclude">A video that must not be offered (the winner that could not be played), or <c>null</c>.</param>
        private async Task<List<VoteOption>> PickOptionsAsync(string? exclude, CancellationToken cancellationToken)
        {
            // The videos that could follow the current one, never the current one itself: random ones if the
            // playlist is shuffled, otherwise the next episodes in order. Ask for spares in case some of them
            // are unavailable; they come in the same order, so the options stay the next ones in line.
            var candidates = _videos.GetNextVideoIds(_optionCount * 3, _playback.Current?.VideoId)
                .Where(id => id != exclude)
                .ToList();
            var options = new List<VoteOption>();

            // A batch only asks for the options that are still missing, so the next one starts right after the
            // candidates this one used (not a fixed number further, which would skip spares).
            var next = 0;
            while (next < candidates.Count && options.Count < _optionCount)
            {
                var batch = candidates.Skip(next).Take(_optionCount - options.Count).ToList();
                next += batch.Count;
                var titles = await Task.WhenAll(batch.Select(id => Task.Run(() => TryGetTitleAsync(id, cancellationToken))));
                cancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < batch.Count; i++)
                {
                    if (titles[i] != null)
                        options.Add(new VoteOption(batch[i], titles[i]!));
                }
            }

            return options;
        }

        private async Task<string?> TryGetTitleAsync(string videoId, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TitleLookupTimeout);
            try
            {
                var video = await _youtube.GetVideoAsync(videoId, timeout.Token);
                return string.IsNullOrWhiteSpace(video.Title) ? null : video.Title;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Log($"Skipping video '{videoId}' for the vote: {ex.Message}");
                return null;
            }
        }

        private static string FormatStandings(VoteSession session)
        {
            if (session.Votes.Count == 0)
                return "No votes yet";

            var standings = session.Votes.Values
                .GroupBy(choice => choice)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => $"{g.Key + 1}) {Votes(g.Count())}");
            return "Current standings: " + string.Join(", ", standings);
        }

        private static string Votes(int count) => count == 1 ? "1 vote" : $"{count} votes";

        /// <summary>"30 seconds", "1 minute", "90 seconds", "5 minutes": whole minutes as minutes, anything else as seconds.</summary>
        internal static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds >= 60 && duration.Seconds == 0)
                return duration.TotalMinutes == 1 ? "1 minute" : $"{(int)duration.TotalMinutes} minutes";

            return $"{(int)Math.Round(duration.TotalSeconds)} seconds";
        }

        internal static string Shorten(string title) =>
            title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)].TrimEnd() + "…";

        /// <param name="releaseHold">
        /// Lets a video that waits for this vote start (the vote was cancelled or failed). Not on dispose: the app is
        /// closing, and nothing should start playing.
        /// </param>
        private void EndSession(VoteSession session, bool releaseHold = true)
        {
            var wasShown = ReferenceEquals(_session, session);
            if (ReferenceEquals(_session, session))
                _session = null;

            session.Cancellation.Cancel();

            var hold = session.Hold;
            session.Hold = null;
            if (releaseHold)
                hold?.Release();

            if (wasShown)
                OnDisplayChanged();
        }

        /// <summary>
        /// The winner of the last vote could not be played, and the regular next video plays instead: the chat votes again,
        /// shorter (<see cref="RevoteDuration"/>), without the failed video, and the new winner plays at once. Up to
        /// <see cref="MaxRevotes"/> times in a row; not while another vote runs or the chat is not connected.
        /// </summary>
        private void OnRequestedVideoFailed(object? sender, string videoId)
        {
            var winner = _winner;
            if (_disposed || winner == null || winner.VideoId != videoId)
                return; // not a winner of this manager's votes

            _winner = null;
            if (winner.Revotes >= MaxRevotes)
            {
                Logger.Log($"The winner \"{winner.Title}\" could not be played either; no more revotes, the next video plays.");
                return;
            }

            if (_session != null)
            {
                Logger.Log($"The winner \"{winner.Title}\" could not be played; another vote runs, so there is no revote.");
                return;
            }

            if (!_chat.IsConnected)
            {
                Logger.Log($"The winner \"{winner.Title}\" could not be played; the chat is not connected, so there is no revote.");
                return;
            }

            Logger.Log($"The winner \"{winner.Title}\" ({videoId}) could not be played; the chat votes again.");
            RevoteAsync(winner).FireAndForget("The revote failed.");
        }

        private async Task RevoteAsync(HandedOverWinner failed)
        {
            var reply = await StartCoreAsync("a failed winner", afterCurrent: false, RevoteDuration, revote: failed);
            if (reply != null)
                Logger.Log($"The revote did not start: {reply}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _playback.RequestedVideoFailed -= OnRequestedVideoFailed;
            if (_session != null)
                EndSession(_session, releaseHold: false);
        }

        private sealed record VoteOption(string VideoId, string Title);

        /// <summary>A winner that was handed to playback, and how many revotes came before it (0 for a regular vote).</summary>
        private sealed record HandedOverWinner(string VideoId, string Title, int Revotes);

        private sealed class VoteSession
        {
            public VoteSession(TimeSpan duration, bool afterCurrent, NowPlaying? currentAtStart)
            {
                Duration = duration;
                AfterCurrent = afterCurrent;
                CurrentAtStart = currentAtStart;
            }

            /// <summary>The countdown after the last option has been posted (shortened if the options took long to find).</summary>
            public TimeSpan Duration { get; set; }

            /// <summary>The warning halfway through has been posted.</summary>
            public bool Warned { get; set; }

            /// <summary>When the countdown ends; <c>null</c> until it has started (the options are still being posted).</summary>
            public DateTimeOffset? EndsAt { get; set; }

            /// <summary>The vote is about the video after the current one; the winner is queued, not played at once.</summary>
            public bool AfterCurrent { get; }

            /// <summary>The video that was playing when the vote started.</summary>
            public NowPlaying? CurrentAtStart { get; }

            /// <summary>Keeps the next video waiting until the vote is over; <c>null</c> once released (or if there is none).</summary>
            public IPlaybackHold? Hold { get; set; }

            /// <summary>For a revote: the winner that could not be played; <c>null</c> for a regular vote.</summary>
            public HandedOverWinner? FailedWinner { get; init; }

            public CancellationTokenSource Cancellation { get; } = new();

            public List<VoteOption> Options { get; } = new();

            /// <summary>Voter (user ID or login name) → zero-based option index.</summary>
            public Dictionary<string, int> Votes { get; } = new(StringComparer.Ordinal);
        }
    }
}
