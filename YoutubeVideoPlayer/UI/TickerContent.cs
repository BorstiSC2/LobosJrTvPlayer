using System.Collections.Generic;
using System.Linq;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>One item the vote ticker scrolls through.</summary>
    /// <param name="Number">Shown in a circle before the text (e.g. <c>1</c>); <c>null</c> for none.</param>
    /// <param name="Text">The option, e.g. a video title or <c>!skip</c>.</param>
    /// <param name="Share">How full its bar is (0 to 1); <c>null</c> for no bar.</param>
    /// <param name="Count">Shown after the bar, e.g. the votes; <c>null</c> for none.</param>
    /// <param name="Leading">The option with the most votes: drawn highlighted.</param>
    public sealed record TickerItem(string? Number, string Text, double? Share, string? Count, bool Leading);

    /// <summary>What the vote ticker shows: a badge and the time at the left, standing still, and the items scrolling past.</summary>
    /// <param name="Icon">The badge's icon (an <see cref="OverlayStyle"/> glyph).</param>
    /// <param name="Label">The badge's word, e.g. <c>Vote</c>.</param>
    /// <param name="Time">The time left, e.g. <c>1:20</c>; <c>null</c> while it is not known.</param>
    /// <param name="Lead">Muted text before the items (e.g. the video a skip vote is about); <c>null</c> for none.</param>
    /// <param name="Hint">Muted text after the items (what to type); <c>null</c> for none.</param>
    /// <param name="Ending">The vote is about to end (the ticker's avatar shows it; no cross-fade).</param>
    public sealed record TickerContent(
        char Icon, string Label, string? Time, string? Lead, IReadOnlyList<TickerItem> Items, string? Hint, bool Ending = false)
    {
        /// <summary>The standing part (badge and time) as text; it is drawn again when this changes.</summary>
        public string StandingText => Time == null ? Label : $"{Label} {Time}";

        /// <summary>The scrolling part as text; it is drawn again when this changes.</summary>
        public string ScrollingText => string.Join("  ·  ", new[] { Lead }
            .Concat(Items.Select(i => (i.Number == null ? "" : i.Number + " ") + i.Text + (i.Count == null ? "" : $" ({i.Count})")))
            .Append(Hint)
            .Where(part => !string.IsNullOrEmpty(part)));

        /// <summary>Everything as one line (for logs and tests).</summary>
        public string PlainText => $"{StandingText}  |  {ScrollingText}";
    }
}
