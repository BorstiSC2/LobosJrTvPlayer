using System;
using System.Collections.Generic;

namespace YouTubeVideoPlayer.Twitch.Voting
{
    /// <summary>One option of a running vote, as the vote ticker shows it.</summary>
    /// <param name="Label">What the option is, e.g. <c>1) Some title</c> or <c>Skip</c>.</param>
    /// <param name="Command">What to type for it, e.g. <c>!vote 1</c> or <c>!skip</c>.</param>
    /// <param name="Votes">The votes it has so far.</param>
    public sealed record VoteDisplayOption(string Label, string Command, int Votes);

    /// <summary>A running vote, as the vote ticker at the bottom of the video shows it.</summary>
    /// <param name="Headline">The first words, e.g. <c>VOTE NOW for the next video!</c>.</param>
    /// <param name="Note">An extra remark, e.g. how many votes skipping needs; <c>null</c> for none.</param>
    /// <param name="TimeLeft">The time left; <c>null</c> while it is not known yet (the options are still being posted).</param>
    public sealed record VoteDisplay(string Headline, IReadOnlyList<VoteDisplayOption> Options, string? Note, TimeSpan? TimeLeft);

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
