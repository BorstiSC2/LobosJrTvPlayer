using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Twitch.Voting
{
    /// <summary>
    /// <c>!voteskip</c>: any viewer can ask the chat whether the current video is skipped. The chat answers with
    /// <c>!skip</c> or <c>!stay</c>. Skipping needs more <c>!skip</c> than <c>!stay</c> and at least a share
    /// (<see cref="Percent"/>) of the average unique chatters (taken when the vote starts). If it succeeds, the regular
    /// vote for the next video (<see cref="SkipVoteManager"/>) starts at once. If it fails, <c>!voteskip</c> cannot be
    /// used for <see cref="Cooldown"/>.
    /// </summary>
    /// <remarks>
    /// <para>All members must be called on the UI thread (chat commands and player events are delivered there).</para>
    /// <para>
    /// The starter's own ballot counts as <c>!skip</c>. A vote that is interrupted (the video changed, or a moderator
    /// cancelled it) has no result and starts no cooldown.
    /// </para>
    /// </remarks>
    public sealed class VoteSkipManager : IVoteDisplay, IDisposable
    {
        private readonly IChatChannel _chat;
        private readonly IPlaybackControl _playback;
        private readonly SkipVoteManager _votes;
        private readonly Func<double> _averageChatters;
        private readonly TimeProvider _time;
        private VoteSkipSession? _session;
        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
        private bool _disposed;

        /// <param name="averageChatters">The rolling average of unique chatters (<see cref="ChatActivityTracker.AverageUniqueChatters"/>).</param>
        /// <param name="duration">How long the chat can vote.</param>
        /// <param name="cooldown">How long <c>!voteskip</c> is blocked after the chat chose to stay.</param>
        /// <param name="percent">The share of the average chatters (1-100) that has to vote <c>!skip</c>.</param>
        public VoteSkipManager(
            IChatChannel chat,
            IPlaybackControl playback,
            SkipVoteManager votes,
            Func<double> averageChatters,
            TimeSpan duration,
            TimeSpan cooldown,
            int percent,
            TimeProvider? timeProvider = null)
        {
            _chat = chat;
            _playback = playback;
            _votes = votes;
            _averageChatters = averageChatters;
            Duration = duration;
            Cooldown = cooldown;
            Percent = Math.Clamp(percent, 1, 100);
            _time = timeProvider ?? TimeProvider.System;

            _playback.NowPlayingChanged += OnNowPlayingChanged;
        }

        public TimeSpan Duration { get; }

        public TimeSpan Cooldown { get; }

        public int Percent { get; }

        public bool IsRunning => _session != null;

        public event EventHandler? DisplayChanged;

        /// <summary>The running vote for the vote ticker; <c>null</c> without one.</summary>
        public VoteDisplay? Display
        {
            get
            {
                var session = _session;
                if (session == null)
                    return null;

                var (skip, stay) = session.Count();
                var left = session.EndsAt - _time.GetUtcNow();
                return new VoteDisplay(
                    VoteKind.Skip,
                    new[] { new VoteDisplayOption(null, "!skip", "!skip", skip), new VoteDisplayOption(null, "!stay", "!stay", stay) },
                    left > TimeSpan.Zero ? left : TimeSpan.Zero,
                    SkipVoteManager.Shorten(session.Video.Title),
                    session.Required);
            }
        }

        private void OnDisplayChanged() => DisplayChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>How long <c>!voteskip</c> is still blocked after a vote the chat chose to stay in; zero if it is not.</summary>
        public TimeSpan CooldownRemaining
        {
            get
            {
                var left = _cooldownUntil - _time.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        /// <summary>The number of <c>!skip</c> votes needed with the given average of unique chatters (at least one).</summary>
        public int RequiredVotes(double averageChatters) =>
            Math.Max(1, (int)Math.Ceiling(Math.Round(averageChatters * Percent / 100.0, 6)));

        /// <summary>Starts a vote about skipping the current video. Returns a reply for the chat if it could not be started.</summary>
        public string? Start(ChatCommandContext context)
        {
            if (_disposed)
                return null;

            if (_session != null)
                return "A !voteskip vote is already running – type !skip or !stay.";

            if (_votes.IsRunning)
                return "A vote is already running – !voteinfo shows it.";

            var cooldown = CooldownRemaining;
            if (cooldown > TimeSpan.Zero)
                return $"!voteskip can be used again in {FormatWait(cooldown)}.";

            var current = _playback.Current;
            if (current == null || _playback.IsStandby || _playback.IsWaitingForHold)
                return "There is no video to skip right now.";

            var average = Math.Max(0, _averageChatters());
            var session = new VoteSkipSession(current, RequiredVotes(average), average, _time.GetUtcNow() + Duration);
            session.Ballots[VoterKey(context)] = true; // the starter wants to skip
            _session = session;
            OnDisplayChanged();

            Logger.Log($"Vote-skip started by {context.Username} for \"{current.Title}\": needs {session.Required} !skip votes (average {average:0.##} chatters).");
            RunAsync(session, context.DisplayName).FireAndForget("The vote-skip failed.");
            return null;
        }

        /// <summary>Registers a <c>!skip</c> (<paramref name="skip"/> = <c>true</c>) or <c>!stay</c> ballot. A later ballot replaces an earlier one.</summary>
        public void Vote(ChatCommandContext context, bool skip)
        {
            var session = _session;
            if (session == null)
                return;

            var key = VoterKey(context);
            if (session.Ballots.TryGetValue(key, out var earlier) && earlier == skip)
                return;

            session.Ballots[key] = skip;
            OnDisplayChanged();
        }

        /// <summary>Cancels a running vote (no result, no cooldown). Returns the chat reply, or <c>null</c> if none runs.</summary>
        public string? Cancel(ChatCommandContext context)
        {
            var session = _session;
            if (session == null)
                return null;

            EndSession(session);
            Logger.Log($"Vote-skip cancelled by {context.Username}.");
            return $"The !voteskip vote was cancelled by {context.DisplayName}.";
        }

        /// <summary>What <c>!voteinfo</c> says about the running vote; <c>null</c> without one.</summary>
        public string? DescribeForChat()
        {
            var session = _session;
            if (session == null)
                return null;

            var (skip, stay) = session.Count();
            return $"Vote to skip \"{SkipVoteManager.Shorten(session.Video.Title)}\": !skip {skip}, !stay {stay} – skipping needs " +
                   $"{Votes(session.Required)} and more !skip than !stay. " +
                   $"{SkipVoteManager.FormatTimeLeft(session.EndsAt - _time.GetUtcNow())} left.";
        }

        private async Task RunAsync(VoteSkipSession session, string starter)
        {
            try
            {
                await RunCoreAsync(session, starter);
            }
            catch (OperationCanceledException)
            {
                // Interrupted: cancelled, the video changed or the app is closing.
            }
            catch
            {
                // Never leave a dead session behind: it would block every later vote.
                EndSession(session);
                throw;
            }
        }

        private async Task RunCoreAsync(VoteSkipSession session, string starter)
        {
            var token = session.Cancellation.Token;
            var title = SkipVoteManager.Shorten(session.Video.Title);

            await TrySendAsync(
                $"{starter} wants to skip \"{title}\"! Type !skip or !stay. Skipping needs more !skip than !stay and at least " +
                $"{Votes(session.Required)} ({Percent}% of ~{session.Average.ToString("0.#", CultureInfo.InvariantCulture)} recent chatters). " +
                $"Voting ends in {SkipVoteManager.FormatDuration(Duration)}.");

            // Remind the chat halfway through longer votes (the same rule as the vote for the next video).
            var reminderAt = Duration >= TimeSpan.FromSeconds(60) ? Duration / 2 : TimeSpan.Zero;
            if (reminderAt > TimeSpan.Zero)
            {
                await Task.Delay(reminderAt, _time, token);
                var (skipSoFar, staySoFar) = session.Count();
                await TrySendAsync(
                    $"{SkipVoteManager.FormatDuration(Duration - reminderAt)} left to vote on skipping \"{title}\": " +
                    $"!skip {skipSoFar}, !stay {staySoFar} – type !skip or !stay.");
            }

            await Task.Delay(Duration - reminderAt, _time, token);
            if (token.IsCancellationRequested)
                return;

            EndSession(session);
            var (skip, stay) = session.Count();

            if (skip > stay && skip >= session.Required)
            {
                Logger.Log($"Vote-skip succeeded: !skip {skip}, !stay {stay} (needed {session.Required}).");
                await TrySendAsync($"The chat voted to skip \"{title}\" (!skip {skip}, !stay {stay})!");
                await StartNextVideoVoteAsync();
                return;
            }

            _cooldownUntil = _time.GetUtcNow() + Cooldown;
            var again = $"!voteskip can be used again in {FormatWait(Cooldown)}.";
            var message = skip > stay
                ? $"!skip won {skip}:{stay}, but it needed {Votes(session.Required)} – \"{title}\" stays. {again}"
                : $"The chat voted to stay (!stay {stay}, !skip {skip}) – \"{title}\" keeps playing. {again}";
            Logger.Log($"Vote-skip failed: !skip {skip}, !stay {stay} (needed {session.Required}).");
            await TrySendAsync(message);
        }

        /// <summary>The chat wants another video: it votes which one. If that vote cannot start, the next video plays.</summary>
        private async Task StartNextVideoVoteAsync()
        {
            var reply = await _votes.StartAsync("!voteskip");
            if (reply == null)
                return;

            Logger.Log($"The vote for the next video did not start after a vote-skip: {reply}");
            if (_votes.IsRunning)
                return; // another vote picks the next video anyway

            await TrySendAsync("Skipping to the next video.");
            await _playback.SkipAsync();
        }

        private void OnNowPlayingChanged(object? sender, NowPlaying video)
        {
            var session = _session;
            if (session == null || ReferenceEquals(video, session.Video))
                return;

            // The video the vote was about is gone: there is nothing left to skip.
            EndSession(session);
            Logger.Log("Vote-skip ended: the video changed.");
            TrySendAsync("The video changed – the !voteskip vote has ended.").FireAndForget("Sending a vote-skip message failed.");
        }

        /// <summary>Sends a chat message; a failing chat must not stop the vote.</summary>
        private async Task TrySendAsync(string message)
        {
            try
            {
                await _chat.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                Logger.Log("Sending a vote-skip message to the chat failed.", ex);
            }
        }

        private void EndSession(VoteSkipSession session)
        {
            var wasRunning = ReferenceEquals(_session, session);
            if (wasRunning)
                _session = null;

            session.Cancellation.Cancel();
            if (wasRunning)
                OnDisplayChanged();
        }

        private static string VoterKey(ChatCommandContext context) =>
            string.IsNullOrEmpty(context.UserId) ? context.Username : context.UserId;

        private static string Votes(int count) => count == 1 ? "1 !skip vote" : $"{count} !skip votes";

        /// <summary>"30 minutes", "1 minute", "45 seconds": whole minutes rounded up, seconds below one minute.</summary>
        internal static string FormatWait(TimeSpan wait)
        {
            if (wait < TimeSpan.FromMinutes(1))
                return $"{(int)Math.Ceiling(wait.TotalSeconds)} seconds";

            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _playback.NowPlayingChanged -= OnNowPlayingChanged;
            if (_session != null)
                EndSession(_session);
        }

        private sealed class VoteSkipSession
        {
            public VoteSkipSession(NowPlaying video, int required, double average, DateTimeOffset endsAt)
            {
                Video = video;
                Required = required;
                Average = average;
                EndsAt = endsAt;
            }

            /// <summary>The video the vote is about.</summary>
            public NowPlaying Video { get; }

            /// <summary>The <c>!skip</c> votes needed (taken when the vote started).</summary>
            public int Required { get; }

            /// <summary>The average of unique chatters when the vote started.</summary>
            public double Average { get; }

            public DateTimeOffset EndsAt { get; }

            public CancellationTokenSource Cancellation { get; } = new();

            /// <summary>Voter (user ID or login name) → <c>true</c> for <c>!skip</c>, <c>false</c> for <c>!stay</c>.</summary>
            public Dictionary<string, bool> Ballots { get; } = new(StringComparer.Ordinal);

            public (int Skip, int Stay) Count()
            {
                var skip = Ballots.Values.Count(b => b);
                return (skip, Ballots.Count - skip);
            }
        }
    }
}
