using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!autovotestatus</c> (everybody, it only reads): tells whether the Autovote option is on, i.e.
    /// whether a chat vote for the next video starts by itself shortly before a video ends.
    /// </summary>
    public sealed class AutoVoteStatusCommand : IChatCommand
    {
        private readonly IAutoVoteControl _autoVote;

        public AutoVoteStatusCommand(IAutoVoteControl autoVote, TimeSpan cooldown)
        {
            _autoVote = autoVote;
            Cooldown = cooldown;
        }

        public string Name => "autovotestatus";

        public string Description =>
            "Tells you whether Autovote is on. With Autovote, chat gets to vote on the next video a few minutes before each video ends.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(AutoVoteTexts.Status(_autoVote, changed: false));
    }

    /// <summary>
    /// <c>!toggleautovote</c> (moderators, broadcaster and privileged users): switches the Autovote option
    /// (the checkbox in the control bar) to the opposite value and posts the new value.
    /// </summary>
    public sealed class ToggleAutoVoteCommand : IChatCommand
    {
        private readonly IAutoVoteControl _autoVote;

        public ToggleAutoVoteCommand(IAutoVoteControl autoVote, TimeSpan cooldown)
        {
            _autoVote = autoVote;
            Cooldown = cooldown;
        }

        public string Name => "toggleautovote";

        public string Description =>
            "Turns Autovote on or off. With Autovote, chat gets to vote on the next video a few minutes before each video ends.";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context)
        {
            _autoVote.Enabled = !_autoVote.Enabled;
            return Task.FromResult<string?>(AutoVoteTexts.Status(_autoVote, changed: true));
        }
    }

    /// <summary>The chat texts that describe the Autovote option.</summary>
    internal static class AutoVoteTexts
    {
        public static string Status(IAutoVoteControl autoVote, bool changed)
        {
            var now = changed ? "now " : string.Empty;
            return autoVote.Enabled
                ? $"Autovote is {now}ON: a vote for the next video starts {SkipVoteManager.FormatDuration(autoVote.LeadTime)} before the current video ends."
                : $"Autovote is {now}OFF: no vote starts by itself.";
        }
    }
}
