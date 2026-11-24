using System.Drawing;
using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class NowPlayingCardTests
{
    private static readonly Point BottomLeft = new(10, 300);
    private const int Available = 600;

    private static readonly NowPlayingCardContent Video = new("Some video", TimeSpan.FromSeconds(724), TimeSpan.FromSeconds(2710));

    /// <summary>A card on a hidden form; the test moves the fake clock and calls <see cref="NowPlayingCard.Advance"/> itself.</summary>
    private sealed class Host : IDisposable
    {
        private readonly Form _form = new() { Width = 800, Height = 400 };

        public Host()
        {
            Card = new NowPlayingCard(Time);
            _form.Controls.Add(Card);
            _form.ShowHidden();
            Card.AvailableWidth = Available;
            Card.BottomLeft = BottomLeft;
        }

        public FakeTimeProvider Time { get; } = new();

        public NowPlayingCard Card { get; }

        /// <summary>Lets <paramref name="duration"/> pass in frames of 10 ms.</summary>
        public void Run(TimeSpan duration)
        {
            for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(10))
            {
                Time.Advance(TimeSpan.FromMilliseconds(10));
                Card.Advance();
            }
        }

        /// <summary>Runs the slide to its end.</summary>
        public void Finish() => Run(TimeSpan.FromSeconds(1));

        public void Dispose() => _form.Dispose();
    }

    private static void AssertInPlace(NowPlayingCard card)
    {
        Assert.Equal(BottomLeft.X, card.Left);
        Assert.Equal(BottomLeft.Y, card.Bottom);
    }

    [Fact]
    public void SlidesInFromTheLeftEdge_IntoItsPlace() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Card.Content = Video;
        Assert.False(host.Card.Visible);
        Assert.True(host.Card.Right <= 0); // waiting outside the form

        host.Card.Showing = true;
        Assert.True(host.Card.Visible);
        Assert.True(host.Card.IsClockRunning);

        host.Run(NowPlayingCard.SlideInDuration / 2);
        Assert.InRange(host.Card.Left, -host.Card.Width + 1, BottomLeft.X - 1); // on its way
        Assert.Equal(BottomLeft.Y, host.Card.Bottom);

        host.Finish();
        AssertInPlace(host.Card);
        Assert.False(host.Card.IsAnimating);
        Assert.False(host.Card.IsClockRunning);
    }, sta: true);

    [Fact]
    public void SlidesOut_HidesAndStopsTheClock() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Card.Content = Video;
        host.Card.Showing = true;
        host.Finish();

        host.Card.Showing = false;
        host.Run(NowPlayingCard.SlideOutDuration / 2);
        Assert.True(host.Card.Visible);
        Assert.True(host.Card.Left < BottomLeft.X);

        host.Finish();
        Assert.False(host.Card.Visible);
        Assert.True(host.Card.Right <= 0);
        Assert.False(host.Card.IsClockRunning);
        Assert.Equal(Video, host.Card.Content); // kept: it slid out with its text
    }, sta: true);

    [Fact]
    public void ASlideThatTurnsAround_ContinuesFromWhereTheCardIs() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Card.Content = Video;
        host.Card.Showing = true;
        host.Run(NowPlayingCard.SlideInDuration / 2);
        var left = host.Card.Left;

        host.Card.Showing = false;
        host.Run(TimeSpan.FromMilliseconds(10));

        Assert.InRange(host.Card.Left, left - host.Card.Width / 4, left); // a small step back, no jump
        host.Finish();
        Assert.False(host.Card.Visible);
    }, sta: true);

    [Fact]
    public void WithoutContent_ItStaysOut() => UiContext.Run(() =>
    {
        using var host = new Host();

        host.Card.Showing = true;
        host.Finish();

        Assert.False(host.Card.Visible);
    }, sta: true);

    [Fact]
    public void ItsWidth_FollowsTheTitle_BetweenTheMinimumAndTheAvailableWidth() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Card.Content = Video with { Title = "Hi" };
        var minimum = host.Card.Width;

        host.Card.Content = Video with { Title = "A considerably longer title of a video" };
        var longer = host.Card.Width;

        host.Card.Content = Video with { Title = new string('W', 300) };

        Assert.True(longer > minimum);
        Assert.Equal(Available, host.Card.Width);
    }, sta: true);

    [Fact]
    public void ANewPosition_DoesNotMoveOrResizeTheCard() => UiContext.Run(() =>
    {
        using var host = new Host();
        host.Card.Content = Video;
        host.Card.Showing = true;
        host.Finish();
        var bounds = host.Card.Bounds;

        host.Card.Content = Video with { Position = TimeSpan.FromSeconds(1999) };

        Assert.Equal(bounds, host.Card.Bounds);
        Assert.False(host.Card.IsAnimating);
    }, sta: true);

    [Fact]
    public void PlainText_ShowsThePositionAndTheLength_OrThePositionAlone() => UiContext.Run(() =>
    {
        using var host = new Host();

        host.Card.Content = Video;
        Assert.Equal("NOW PLAYING 12:04 / 45:10 | Some video", host.Card.PlainText);

        host.Card.Content = Video with { Length = null };
        Assert.Equal("NOW PLAYING 12:04 | Some video", host.Card.PlainText);

        host.Card.Content = Video with { Position = TimeSpan.FromHours(2), Length = TimeSpan.FromHours(1) };
        Assert.Equal("NOW PLAYING 1:00:00 / 1:00:00 | Some video", host.Card.PlainText);
    }, sta: true);
}
