using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class OverlayPillTests
{
    private static OverlayPill Show(Form form, bool layered = true, bool animated = true)
    {
        var pill = new OverlayPill(OverlayStyle.BufferingGlyph, layered) { Text = "Buffering", Animated = animated };
        form.Controls.Add(pill);
        form.ShowHidden();
        pill.Visible = true;
        return pill;
    }

    [Fact]
    public void TheDots_CountUp_WithoutChangingTheWidth() => UiContext.Run(() =>
    {
        using var form = new Form();
        var pill = Show(form);
        var size = pill.Size;
        var shown = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            shown.Add(pill.ShownText);
            pill.NextFrame();
            Assert.Equal(size, pill.Size);
        }

        Assert.Equal(new[] { "Buffering ...", "Buffering .", "Buffering ..", "Buffering ...", "Buffering " }, shown);
    }, sta: true);

    [Fact]
    public void ItsWidth_FollowsTheText() => UiContext.Run(() =>
    {
        using var form = new Form();
        var pill = Show(form, animated: false);
        var width = pill.Width;

        pill.Text = "Connection lost - resuming at 50:31";

        Assert.True(pill.Width > width);
        Assert.Equal(pill.Height, form.LogicalToDeviceUnits(48));
    }, sta: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ItsWindow_IsCreated_LayeredOrNot(bool layered) => UiContext.Run(() =>
    {
        using var form = new Form();
        var pill = Show(form, layered);

        Assert.True(pill.IsHandleCreated);
        if (!layered)
            Assert.False(pill.IsLayered);

        pill.Icon = OverlayStyle.ReloadGlyph; // draws again, in either kind of window
        pill.NextFrame();
    }, sta: true);
}
