using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!voteskip</c> (everyone): asks the chat whether the current video is skipped (<c>!skip</c> / <c>!stay</c>).
    /// The command's own cooldown only limits the replies; the long wait after a failed vote is kept by the manager.
    /// </summary>
    public sealed class VoteSkipCommand : IChatCommand
    {
        private readonly VoteSkipManager _voteSkip;

        public VoteSkipCommand(VoteSkipManager voteSkip, TimeSpan cooldown)
        {
            _voteSkip = voteSkip;
            Cooldown = cooldown;
        }

        public string Name => "voteskip";

        public string Description =>
            "Asks chat whether to skip the current video. Everyone answers with !skip or !stay. If enough people want to skip, chat then votes on which video plays next. If not, it can't be used again for a while.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) => Task.FromResult(_voteSkip.Start(context));
    }

    /// <summary><c>!skip</c> (everyone): a ballot for skipping in a running <c>!voteskip</c> vote. Does nothing otherwise.</summary>
    public sealed class SkipBallotCommand : IChatCommand
    {
        private readonly VoteSkipManager _voteSkip;

        public SkipBallotCommand(VoteSkipManager voteSkip) => _voteSkip = voteSkip;

        public string Name => "skip";

        public string Description =>
            "Your vote to skip the video while a !voteskip vote is running.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public bool IsHidden => true; // only used within a vote, which names it

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            _voteSkip.Vote(context, skip: true);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary><c>!stay</c> (everyone): a ballot for staying in a running <c>!voteskip</c> vote. Does nothing otherwise.</summary>
    public sealed class StayBallotCommand : IChatCommand
    {
        private readonly VoteSkipManager _voteSkip;

        public StayBallotCommand(VoteSkipManager voteSkip) => _voteSkip = voteSkip;

        public string Name => "stay";

        public string Description =>
            "Your vote to keep watching the video while a !voteskip vote is running.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public bool IsHidden => true; // only used within a vote, which names it

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            _voteSkip.Vote(context, skip: false);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// <c>!voteinfo</c> (everyone): says whether a vote is running and, if so, repeats it: the options of a vote for the
    /// next video, or the standing of a <c>!voteskip</c> vote, with the time left.
    /// </summary>
    public sealed class VoteInfoCommand : IChatCommand
    {
        internal const string NoVoteMessage = "No vote is running right now.";

        private readonly SkipVoteManager _votes;
        private readonly VoteSkipManager? _voteSkip;

        public VoteInfoCommand(SkipVoteManager votes, VoteSkipManager? voteSkip, TimeSpan cooldown)
        {
            _votes = votes;
            _voteSkip = voteSkip;
            Cooldown = cooldown;
        }

        public string Name => "voteinfo";

        public string Description =>
            "Shows the vote that is running right now: the choices, how it is going and how much time is left.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(_voteSkip?.DescribeForChat() ?? _votes.DescribeForChat() ?? NoVoteMessage);
    }
}
