using System.Drawing;
using YouTubeVideoPlayer.UI;
using static YouTubeVideoPlayer.UI.VideoWindowGeometry;

namespace YouTubeVideoPlayer.Tests.UI;

public class VideoWindowGeometryTests
{
    /// <summary>A border of 16 x 39 plus a control bar of 40.</summary>
    private static readonly Size Extra = new(16, 79);

    private static readonly Rectangle Current = new(100, 100, 1280 + 16, 720 + 79);

    private static void AssertVideoIs16To9(Rectangle window)
    {
        var videoWidth = window.Width - Extra.Width;
        Assert.Equal(VideoHeightFor(videoWidth), window.Height - Extra.Height);
    }

    [Fact]
    public void VideoHeight_Is9Of16OfTheWidth()
    {
        Assert.Equal(1080, VideoHeightFor(1920));
        Assert.Equal(720, VideoHeightFor(1280));
        Assert.Equal(1920, VideoWidthFor(1080));
    }

    [Theory]
    [InlineData(EdgeRight)]
    [InlineData(EdgeLeft)]
    public void DraggingASide_SetsTheWidth_AndTheHeightFollowsBelow(int edge)
    {
        var proposed = edge == EdgeRight
            ? Rectangle.FromLTRB(Current.Left, Current.Top, Current.Right + 320, Current.Bottom)
            : Rectangle.FromLTRB(Current.Left - 320, Current.Top, Current.Right, Current.Bottom);

        var result = AdjustDrag(Current, proposed, edge, Extra, MinVideoWidth);

        AssertVideoIs16To9(result);
        Assert.Equal(1600 + Extra.Width, result.Width);
        Assert.Equal(Current.Top, result.Top);
        if (edge == EdgeRight)
            Assert.Equal(Current.Left, result.Left);
        else
            Assert.Equal(Current.Right, result.Right);
    }

    [Theory]
    [InlineData(EdgeBottom)]
    [InlineData(EdgeTop)]
    public void DraggingTopOrBottom_SetsTheHeight_AndTheWidthFollowsToTheRight(int edge)
    {
        var proposed = edge == EdgeBottom
            ? Rectangle.FromLTRB(Current.Left, Current.Top, Current.Right, Current.Bottom + 180)
            : Rectangle.FromLTRB(Current.Left, Current.Top - 180, Current.Right, Current.Bottom);

        var result = AdjustDrag(Current, proposed, edge, Extra, MinVideoWidth);

        AssertVideoIs16To9(result);
        Assert.Equal(900 + Extra.Height, result.Height);
        Assert.Equal(1600 + Extra.Width, result.Width);
        Assert.Equal(Current.Left, result.Left);
        if (edge == EdgeBottom)
            Assert.Equal(Current.Top, result.Top);
        else
            Assert.Equal(Current.Bottom, result.Bottom);
    }

    [Theory]
    [InlineData(EdgeTopLeft)]
    [InlineData(EdgeTopRight)]
    [InlineData(EdgeBottomLeft)]
    [InlineData(EdgeBottomRight)]
    public void DraggingACorner_FollowsTheLargerChange_AndKeepsTheOppositeCorner(int edge)
    {
        // Mostly upwards/downwards: the height changed more than the width, so it decides.
        var left = edge is EdgeTopLeft or EdgeBottomLeft ? Current.Left - 10 : Current.Left;
        var right = edge is EdgeTopRight or EdgeBottomRight ? Current.Right + 10 : Current.Right;
        var top = edge is EdgeTopLeft or EdgeTopRight ? Current.Top - 180 : Current.Top;
        var bottom = edge is EdgeBottomLeft or EdgeBottomRight ? Current.Bottom + 180 : Current.Bottom;

        var result = AdjustDrag(Current, Rectangle.FromLTRB(left, top, right, bottom), edge, Extra, MinVideoWidth);

        AssertVideoIs16To9(result);
        Assert.Equal(1600 + Extra.Width, result.Width);
        if (edge is EdgeTopLeft or EdgeBottomLeft)
            Assert.Equal(Current.Right, result.Right);
        else
            Assert.Equal(Current.Left, result.Left);
        if (edge is EdgeTopLeft or EdgeTopRight)
            Assert.Equal(Current.Bottom, result.Bottom);
        else
            Assert.Equal(Current.Top, result.Top);
    }

    [Fact]
    public void Dragging_StopsAtTheMinimumVideoWidth()
    {
        var proposed = Rectangle.FromLTRB(Current.Left, Current.Top, Current.Left + 200, Current.Bottom);

        var result = AdjustDrag(Current, proposed, EdgeRight, Extra, MinVideoWidth);

        Assert.Equal(MinVideoWidth + Extra.Width, result.Width);
        AssertVideoIs16To9(result);
    }

    [Fact]
    public void NormalizeSize_KeepsPositionAndWidth_FixesTheHeight_AndChangesNothingTheSecondTime()
    {
        var snapped = new Rectangle(0, 0, 960 + Extra.Width, 1040);

        var result = NormalizeSize(snapped, Extra);

        Assert.Equal(snapped.Location, result.Location);
        Assert.Equal(snapped.Width, result.Width);
        AssertVideoIs16To9(result);
        Assert.Equal(result, NormalizeSize(result, Extra));
    }

    [Fact]
    public void Fit_InAWideWorkArea_IsLimitedByTheHeight_AndCenteredHorizontally()
    {
        var workArea = new Rectangle(0, 0, 3440, 1392); // 21:9 monitor minus the taskbar

        var result = Fit(workArea, Extra, Padding.Empty);

        AssertVideoIs16To9(result);
        Assert.Equal(workArea.Height, result.Height);
        Assert.True(result.Width < workArea.Width);
        Assert.Equal(workArea.Left + (workArea.Width - result.Width) / 2, result.Left);
        Assert.Equal(workArea.Top, result.Top);
    }

    [Fact]
    public void Fit_InANarrowWorkArea_IsLimitedByTheWidth_AndCenteredVertically()
    {
        var workArea = new Rectangle(-1280, 0, 1280, 1024); // 5:4 monitor left of the primary one

        var result = Fit(workArea, Extra, Padding.Empty);

        AssertVideoIs16To9(result);
        Assert.Equal(workArea.Width, result.Width);
        Assert.Equal(workArea.Left, result.Left);
        Assert.Equal(workArea.Top + (workArea.Height - result.Height) / 2, result.Top);
    }

    [Fact]
    public void Fit_LetsTheVisibleFrameFillTheWorkArea_TheInvisibleBorderLiesOutside()
    {
        var workArea = new Rectangle(0, 0, 1920, 1032);
        var invisible = new Padding(7, 0, 7, 7);
        var extra = new Size(16 + 14, 79 + 7); // the window's border includes the invisible parts

        var result = Fit(workArea, extra, invisible);

        var videoWidth = result.Width - extra.Width;
        Assert.Equal(VideoHeightFor(videoWidth), result.Height - extra.Height);
        var visible = Rectangle.FromLTRB(
            result.Left + invisible.Left, result.Top + invisible.Top, result.Right - invisible.Right, result.Bottom - invisible.Bottom);
        Assert.Equal(workArea.Height, visible.Height);
        Assert.Equal(workArea.Top, visible.Top);
        Assert.True(workArea.Contains(visible));
    }
}
