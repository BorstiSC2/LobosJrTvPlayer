using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary><c>!skipvote</c> (privileged): lets the chat vote which video plays next.</summary>
    public sealed class SkipVoteCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;

        public SkipVoteCommand(SkipVoteManager votes) => _votes = votes;

        public string Name => "skipvote";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown => TimeSpan.Zero; // a running vote blocks a second one anyway

        public Task<string?> ExecuteAsync(ChatCommandContext context) => _votes.StartAsync(context);
    }

    /// <summary><c>!cancelvote</c> (privileged): stops the running vote without changing the video.</summary>
    public sealed class CancelVoteCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;

        public CancelVoteCommand(SkipVoteManager votes) => _votes = votes;

        public string Name => "cancelvote";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public Task<string?> ExecuteAsync(ChatCommandContext context) => Task.FromResult<string?>(_votes.Cancel(context));
    }

    /// <summary><c>!vote n</c> (everyone): votes for option n of the running vote. No reply, to keep the chat clean.</summary>
    public sealed class VoteCommand : IChatCommand
    {
        private readonly SkipVoteManager _votes;

        public VoteCommand(SkipVoteManager votes) => _votes = votes;

        public string Name => "vote";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown => TimeSpan.Zero;

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            _votes.Vote(context);
            return Task.FromResult<string?>(null);
        }
    }
}
