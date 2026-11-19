using System;
using System.Linq;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>Turns a running vote into what the vote ticker shows.</summary>
    internal static class VoteTicker
    {
        /// <summary>
        /// A vote for a video: badge "Vote", the time left, the options with number, bar (their share of the votes) and
        /// count, the leader highlighted, and what to type. A skip vote: badge "Skip this video?", the video, <c>!skip</c>
        /// and <c>!stay</c> with bars measured against the votes skipping needs.
        /// </summary>
        public static TickerContent From(VoteDisplay display)
        {
            var time = display.TimeLeft is { } left ? SkipVoteManager.FormatTimeLeft(left) : null;
            var most = display.Options.Count == 0 ? 0 : display.Options.Max(o => o.Votes);

            if (display.Kind == VoteKind.Skip)
            {
                var required = Math.Max(1, display.Required ?? 1);
                var items = display.Options
                    .Select(o => new TickerItem(
                        null,
                        o.Title,
                        Math.Min(1.0, (double)o.Votes / required),
                        o.Command == "!skip" ? $"{o.Votes} of {required}" : $"{o.Votes}",
                        o.Votes > 0 && o.Votes == most && display.Options.Count(other => other.Votes == most) == 1))
                    .ToList();
                var lead = display.Subject == null ? null : $"\"{display.Subject}\"";
                return new TickerContent(OverlayStyle.SkipGlyph, "Skip this video?", time, lead, items,
                    "Type " + string.Join(" or ", display.Options.Select(o => o.Command)));
            }

            var total = display.Options.Sum(o => o.Votes);
            var options = display.Options
                .Select(o => new TickerItem(
                    o.Number,
                    o.Title,
                    total == 0 ? 0 : (double)o.Votes / total,
                    o.Votes.ToString(),
                    most > 0 && o.Votes == most))
                .ToList();
            var hint = $"!vote 1–{display.Options.Count}";
            if (display.Kind == VoteKind.VideoAfterCurrent)
                hint += " for the video after this one";

            return new TickerContent(OverlayStyle.VoteGlyph, "Vote", time, null, options, hint);
        }
    }
}
