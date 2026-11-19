using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!forcevoteskip</c> (privileged): lets the chat vote which video plays next, without a <c>!voteskip</c> first.</summary>
    public sealed class ForceVoteSkipCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;
        private readonly VoteSkipManager? _voteSkip;

        /// <param name="voteSkip">The <c>!voteskip</c> vote; while it runs, no other vote starts.</param>
        public ForceVoteSkipCommand(SkipVoteManager votes, VoteSkipManager? voteSkip = null)
        {
            _votes = votes;
            _voteSkip = voteSkip;
        }

        public string Name => "forcevoteskip";

        public string Description =>
            "Starts a vote right away in which chat picks the next video to play.";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown => TimeSpan.Zero; // a running vote blocks a second one anyway

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            if (_voteSkip is { IsRunning: true })
                return Task.FromResult<string?>("A !voteskip vote is running – wait for it, or end it with !cancelvote.");

            return _votes.StartAsync(context);
        }
    }

    /// <summary><c>!cancelvote</c> (privileged): stops the running vote (a <c>!voteskip</c> vote too) without changing the video.</summary>
    public sealed class CancelVoteCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;
        private readonly VoteSkipManager? _voteSkip;

        public CancelVoteCommand(SkipVoteManager votes, VoteSkipManager? voteSkip = null)
        {
            _votes = votes;
            _voteSkip = voteSkip;
        }

        public string Name => "cancelvote";

        public string Description =>
            "Stops the vote that is running. The current video keeps playing.";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(_voteSkip?.Cancel(context) ?? _votes.Cancel(context));
    }

    /// <summary><c>!vote n</c> (everyone): votes for option n of the running vote. No reply, to keep the chat clean.</summary>
    public sealed class VoteCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;

        public VoteCommand(SkipVoteManager votes) => _votes = votes;

        public string Name => "vote";

        public string Description =>
            "Your vote while chat picks the next video. Type !vote and the number of your choice, for example !vote 2.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public bool IsHidden => true; // the vote names it

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            _votes.Vote(context);
            return Task.FromResult<string?>(null);
        }
    }
}
