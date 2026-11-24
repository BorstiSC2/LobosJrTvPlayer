using System.Drawing;
using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;
using Content = YouTubeVideoPlayer.UI.TitleStrip.Content;

namespace YouTubeVideoPlayer.Tests.UI;

public class TitleStripTests
{
    private static readonly Point BottomLeft = new(10, 300);
    private const int Available = 600;

    /// <summary>A strip on a hidden form; the test moves the fake clock and calls <see cref="TitleStrip.Advance"/> itself.</summary>
    private sealed class Host : IDisposable
    {
        private readonly Form _form = new() { Width = 800, Height = 400 };

        public Host(bool layered = true)
        {
            Strip = new TitleStrip(Time, layered) { FrameRate = 60 };
            _form.Controls.Add(Strip);
            _form.ShowHidden();
            Strip.AvailableWidth = Available;
            Strip.BottomLeft = BottomLeft;
        }

        public FakeTimeProvider Time { get; } = new();

        public TitleStrip Strip { get; }

        /// <summary>Lets <paramref name="duration"/> pass in frames of 10 ms.</summary>
        public void Run(TimeSpan duration)
        {
            for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(10))
            {
                Time.Advance(TimeSpan.FromMilliseconds(10));
                Strip.Advance();
            }
        }

        /// <summary>Runs every animation to its end.</summary>
        public void Finish() => Run(TimeSpan.FromSeconds(1));

        public void ShowTitle(string title)
        {
            Strip.Title = title;
            Finish();
        }

        public void Dispose() => _form.Dispose();
    }

    /// <summary>A ticker with one item.</summary>
    private static TickerContent Ticker(string text, int votes = 0, string? time = null) =>
        new(OverlayStyle.VoteGlyph, "Vote", time, null, new[] { new TickerItem("1", text, 0, $"{votes}", false) }, null);

    private static void AssertAnchored(TitleStrip strip)
    {
        Assert.Equal(BottomLeft.X, strip.Left);
        Assert.Equal(BottomLeft.Y, strip.Bottom);
    }

    [Fact]
    public void Title_FadesIn_AtTheWidthOfItsText_TogetherWithThePill() => UiContext.Run(() =>
    {
        using var host = new Host();
        Assert.False(host.Strip.Visible);

        host.Strip.Title = "Some video";
        Assert.True(host.Strip.Visible);
        Assert.True(host.Strip.IsAnimating);

        host.Run(TimeSpan.FromMilliseconds(70));
        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width); // no growing from nothing
        Assert.InRange(host.Strip.BoxAlpha, 0.2, 0.8);
        Assert.Equal(host.Strip.BoxAlpha, host.Strip.ContentAlpha, 6); // the pill and the text together

        host.Finish();

        Assert.Equal(Content.Title, host.Strip.Shown);
        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width);
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.Equal(1, host.Strip.BoxAlpha);
        Assert.False(host.Strip.IsAnimating);
        Assert.False(host.Strip.IsClockRunning);
        AssertAnchored(host.Strip);
    }, sta: true);

    [Fact]
    public void ALongTitle_IsCappedAtTheAvailableWidth() => UiContext.Run(() =>
    {
        using var host = new Host();

        host.ShowTitle(new string('W', 300));

        Assert.Equal(Available, host.Strip.Width);
    }, sta: true);

    [Fact]
    public void ANewTitle_ReplacesTheOldOneAtOnce() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Short");

        host.Strip.Title = "A considerably longer video title";

        Assert.Equal(host.Strip.TitleWidth("A considerably longer video title"), host.Strip.Width);
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.False(host.Strip.IsAnimating);
        AssertAnchored(host.Strip);
    }, sta: true);

    [Fact]
    public void TitleToTicker_FadesOut_Widens_FadesIn_ThenScrolls() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        var titleHeight = host.Strip.Height;

        host.Strip.Ticker = Ticker("VOTE NOW! 1) A (0) · 2) B (0)");
        Assert.True(host.Strip.IsClockRunning);

        host.Run(TimeSpan.FromMilliseconds(70));
        Assert.Equal(Content.Title, host.Strip.Shown);
        Assert.InRange(host.Strip.ContentAlpha, 0.4, 0.6); // fading out
        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width);

        host.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(Content.Ticker, host.Strip.Shown); // swapped while invisible
        Assert.Equal(0, host.Strip.ContentAlpha);

        host.Run(TimeSpan.FromMilliseconds(100));
        Assert.InRange(host.Strip.Width, host.Strip.TitleWidth("Some video") + 1, Available - 1); // widening
        Assert.Equal(0, host.Strip.ContentAlpha);

        host.Run(TimeSpan.FromMilliseconds(130));
        Assert.Equal(Available, host.Strip.Width);
        Assert.Equal(titleHeight, host.Strip.Height); // one style, one height
        AssertAnchored(host.Strip);

        host.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.False(host.Strip.IsAnimating);
        Assert.True(host.Strip.IsClockRunning); // the ticker keeps scrolling

        var offset = host.Strip.ScrollOffset;
        host.Time.Advance(TimeSpan.FromSeconds(1));
        host.Strip.Advance();
        Assert.Equal(offset + 60 * host.Strip.PixelsPerFrame, host.Strip.ScrollOffset);
    }, sta: true);

    [Fact]
    public void EveryRefresh_MovesTheTickerByTheSameWholeNumberOfPixels() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();
        var step = host.Strip.PixelsPerFrame;
        Assert.True(step >= 1);

        var steps = new List<int>();
        for (var i = 0; i < 120; i++)
        {
            var before = host.Strip.ScrollOffset;
            host.Time.Advance(TimeSpan.FromSeconds(1 / 60.0));
            host.Strip.Advance();
            steps.Add(host.Strip.ScrollOffset - before);
        }

        Assert.All(steps.Where(s => s >= 0), s => Assert.Equal(step, s)); // a negative one is the wrap of a loop
    }, sta: true);

    [Fact]
    public void ARefreshTheUiThreadMissed_IsMadeUpInTheNextFrame() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();
        var offset = host.Strip.ScrollOffset;

        host.Strip.Advance(refreshes: 1);
        Assert.Equal(offset + host.Strip.PixelsPerFrame, host.Strip.ScrollOffset);

        host.Strip.Advance(refreshes: 2);
        Assert.Equal(offset + 3 * host.Strip.PixelsPerFrame, host.Strip.ScrollOffset);
    }, sta: true);

    [Fact]
    public void AFasterDisplay_MovesFewerPixelsPerRefresh() => UiContext.Run(() =>
    {
        using var host = new Host();
        var at60 = host.Strip.PixelsPerFrame;

        host.Strip.FrameRate = 240;

        Assert.True(host.Strip.PixelsPerFrame < at60 || host.Strip.PixelsPerFrame == 1);
    }, sta: true);

    [Fact]
    public void NewTickerText_KeepsScrollingFromWhereItIs() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Strip.Ticker = Ticker("A");
        host.Finish();
        var offset = host.Strip.ScrollOffset;

        host.Strip.Ticker = Ticker("A", votes: 1, time: "1:00"); // new counts and time: the same vote

        Assert.Equal(offset, host.Strip.ScrollOffset);
        Assert.Equal(Content.Ticker, host.Strip.Shown);
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.False(host.Strip.IsAnimating);
    }, sta: true);

    [Fact]
    public void AnotherTicker_CrossFades_AndStartsScrollingFromTheRight() => UiContext.Run(() =>
    {
        using var host = new Host();
        var skip = new TickerContent(OverlayStyle.SkipGlyph, "Skip this video?", "0:00", "\"Some video\"",
            new[] { new TickerItem(null, "!skip", 1, "1 of 1", true) }, "Type !skip or !stay");
        host.Strip.Ticker = skip;
        host.Finish();
        Assert.True(host.Strip.ScrollOffset > 0);

        // The vote for the next video takes over (still looking up its options).
        host.Strip.Ticker = new TickerContent(OverlayStyle.VoteGlyph, "Vote", null, null, Array.Empty<TickerItem>(), "Picking ...");
        Assert.True(host.Strip.IsAnimating);

        host.Run(TimeSpan.FromMilliseconds(70));
        Assert.InRange(host.Strip.ContentAlpha, 0.4, 0.6); // the skip vote fades out
        Assert.Equal(Available, host.Strip.Width); // no resize: both are tickers

        host.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(Content.Ticker, host.Strip.Shown);
        Assert.InRange(host.Strip.ScrollOffset, 0, 10 * host.Strip.PixelsPerFrame); // started over

        host.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.False(host.Strip.IsAnimating);

        // Its options are found: the same badge, but other items, so it cross-fades again.
        host.Strip.Ticker = Ticker("Option A");
        Assert.True(host.Strip.IsAnimating);
        host.Finish();
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.Equal(Available, host.Strip.Width);
    }, sta: true);

    [Fact]
    public void TheTicker_StartsOverAfterEachLoop() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();

        host.Run(TimeSpan.FromSeconds(60)); // far more than one loop

        Assert.InRange(host.Strip.ScrollOffset, 0, Available * 2.5);
    }, sta: true);

    [Fact]
    public void TickerToTitle_RunsInReverse_AndStopsTheTimer() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();

        host.Strip.Ticker = null;
        host.Run(TimeSpan.FromMilliseconds(170));
        Assert.Equal(Content.Title, host.Strip.Shown);
        Assert.Equal(0, host.Strip.ContentAlpha);
        Assert.InRange(host.Strip.Width, Available - 10, Available); // the box only starts to shrink now

        host.Finish();

        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width);
        Assert.Equal(1, host.Strip.ContentAlpha);
        Assert.False(host.Strip.IsClockRunning);
        AssertAnchored(host.Strip);
    }, sta: true);

    [Fact]
    public void AnInterruptedTransition_ContinuesFromWhereItIs() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Run(TimeSpan.FromMilliseconds(70));
        var alpha = host.Strip.ContentAlpha;
        Assert.InRange(alpha, 0.4, 0.6);

        host.Strip.Ticker = null; // the vote was cancelled at once
        host.Run(TimeSpan.FromMilliseconds(10));

        Assert.Equal(Content.Title, host.Strip.Shown);
        Assert.InRange(host.Strip.ContentAlpha, alpha, alpha + 0.1); // fading back in, no jump
        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width);
    }, sta: true);

    [Fact]
    public void WithoutAnyText_ThePillFadesOutWithItsText_AndHides() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        var width = host.Strip.Width;

        host.Strip.Title = string.Empty;
        host.Run(TimeSpan.FromMilliseconds(70));
        Assert.True(host.Strip.Visible);
        Assert.Equal(width, host.Strip.Width); // it does not shrink: everything fades out in place ...
        Assert.InRange(host.Strip.BoxAlpha, 0.2, 0.8);
        Assert.Equal(host.Strip.BoxAlpha, host.Strip.ContentAlpha, 6); // ... and at the same pace

        host.Finish();

        Assert.Equal(Content.None, host.Strip.Shown);
        Assert.Equal(0, host.Strip.Width);
        Assert.False(host.Strip.Visible);
        Assert.False(host.Strip.IsClockRunning);
    }, sta: true);

    [Fact]
    public void AfterAVoteWithoutTitle_TheTickerShrinksToNothing() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();

        host.Strip.Ticker = null;
        host.Finish();

        Assert.Equal(0, host.Strip.Width);
        Assert.False(host.Strip.Visible);
    }, sta: true);

    [Fact]
    public void ItIsAPillOfTheCardsHeight_WithTheTextRightOfTheCircle() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        var card = new NowPlayingCard();
        host.Strip.Parent!.Controls.Add(card);

        Assert.Equal(card.CardHeight, host.Strip.Height); // the card and the strip share the place
        Assert.True(host.Strip.TitleWidth("Some video") > host.Strip.Height); // circle, text, round end
    }, sta: true);

    [Fact]
    public void WithoutALayeredWindow_ItAnimatesTheSame() => UiContext.Run(() =>
    {
        using var host = new Host(layered: false);

        host.ShowTitle("Some video");
        Assert.False(host.Strip.IsLayered);
        Assert.Equal(host.Strip.TitleWidth("Some video"), host.Strip.Width);

        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Finish();
        Assert.Equal(Content.Ticker, host.Strip.Shown);
        Assert.Equal(Available, host.Strip.Width);
        Assert.True(host.Strip.ScrollOffset > 0);
    }, sta: true);

    [Fact]
    public void ATickerThatEnds_FadesOutWithItsPill_EvenHalfwayThroughACrossFade() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.ShowTitle("Some video");
        host.Strip.Ticker = Ticker("VOTE NOW!");
        host.Run(TimeSpan.FromMilliseconds(70)); // the title is half faded out, the pill is still there

        host.Strip.Ticker = null;
        host.Strip.Title = string.Empty;
        host.Run(TimeSpan.FromMilliseconds(70));
        Assert.True(host.Strip.BoxAlpha > 0);
        Assert.True(host.Strip.ContentAlpha <= host.Strip.BoxAlpha);

        host.Finish();
        Assert.Equal(0, host.Strip.BoxAlpha);
        Assert.Equal(0, host.Strip.ContentAlpha);
        Assert.False(host.Strip.Visible);
    }, sta: true);
}
