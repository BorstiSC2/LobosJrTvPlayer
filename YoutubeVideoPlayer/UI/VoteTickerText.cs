using System.Collections.Generic;
using System.Linq;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>Builds the line the vote ticker scrolls through from a <see cref="VoteDisplay"/>.</summary>
    internal static class VoteTickerText
    {
        private const string Separator = "   ·   ";

        /// <summary>
        /// e.g. <c>VOTE NOW for the next video!   1) Title A (4)  ·  2) Title B (1)   —   type !vote 1-2   ·   1:20 left</c>.
        /// </summary>
        public static string Format(VoteDisplay display)
        {
            var options = string.Join("  ·  ", display.Options.Select(o => $"{o.Label} ({o.Votes})"));
            var parts = new List<string> { $"{display.Headline}   {options}   —   type {Commands(display.Options)}" };
            if (!string.IsNullOrWhiteSpace(display.Note))
                parts.Add(display.Note!);
            if (display.TimeLeft is { } left)
                parts.Add($"{SkipVoteManager.FormatTimeLeft(left)} left");

            return string.Join(Separator, parts);
        }

        /// <summary><c>!vote 1-3</c> for numbered options, otherwise the commands one after the other (<c>!skip / !stay</c>).</summary>
        private static string Commands(IReadOnlyList<VoteDisplayOption> options)
        {
            if (options.Count > 1 && options.Select((o, i) => o.Command == $"!vote {i + 1}").All(numbered => numbered))
                return $"!vote 1-{options.Count}";

            return string.Join(" / ", options.Select(o => o.Command));
        }
    }
}
