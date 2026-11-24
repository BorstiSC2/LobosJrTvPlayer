using System;
using System.Collections.Generic;

namespace YouTubeVideoPlayer.Twitch.Voting
{
    /// <summary>What a running vote decides.</summary>
    public enum VoteKind
    {
        /// <summary>The video that plays next, at once (<c>!forcevoteskip</c>, or after a won <c>!voteskip</c>).</summary>
        NextVideo,

        /// <summary>The video after the current one (the autovote).</summary>
        VideoAfterCurrent,

        /// <summary>Whether to skip the current video (<c>!voteskip</c>).</summary>
        Skip
    }

    /// <summary>One option of a running vote, as the vote ticker shows it.</summary>
    /// <param name="Number">The option's number (<c>1</c>); <c>null</c> for options without one (<c>!skip</c>).</param>
    /// <param name="Title">What the option is: a (shortened) video title, or the command for skip and stay.</param>
    /// <param name="Command">What to type for it, e.g. <c>!vote 1</c> or <c>!skip</c>.</param>
    /// <param name="Votes">The votes it has so far.</param>
    public sealed record VoteDisplayOption(string? Number, string Title, string Command, int Votes);

    /// <summary>A running vote, as the vote ticker at the bottom of the video shows it.</summary>
    /// <param name="TimeLeft">The time left; <c>null</c> while it is not known yet (the options are still being posted).</param>
    /// <param name="Subject">For a skip vote: the (shortened) title of the video it is about.</param>
    /// <param name="Required">For a skip vote: the <c>!skip</c> votes it needs.</param>
    /// <param name="Ending">
    /// The vote is in its last part: from the moment its last warning ("1 minute left! ...") is posted in chat; a vote too
    /// short for a warning from halfway on.
    /// </param>
    public sealed record VoteDisplay(
        VoteKind Kind, IReadOnlyList<VoteDisplayOption> Options, TimeSpan? TimeLeft, string? Subject = null, int? Required = null,
        bool Ending = false);

    /// <summary>A vote the UI can show (implemented by <see cref="SkipVoteManager"/> and <see cref="VoteSkipManager"/>).</summary>
    /// <remarks>Read on the UI thread; <see cref="DisplayChanged"/> is raised there.</remarks>
    public interface IVoteDisplay
    {
        /// <summary>
        /// The running vote, read at this moment (<see cref="VoteDisplay.TimeLeft"/> is computed on the manager's own clock);
        /// <c>null</c> while no vote can be shown.
        /// </summary>
        VoteDisplay? Display { get; }

        /// <summary>Raised when a vote starts showing, its options or counts change, its countdown starts, or it ends.</summary>
        event EventHandler? DisplayChanged;
    }
}
