using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class OverlayStyleTests
{
    /// <summary>
    /// GDI+ cannot draw a very short arc with round caps: <c>DrawArc</c> fails with "An object could not be created ..."
    /// (measured up to 0.37°). The ring of a long video is that short in its first seconds (5 hours: 0.02° after 1 s),
    /// also while the card pops up (scaled), and at every DPI.
    /// </summary>
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void TheRing_CanBeDrawnForAnyShare_EvenTheTinyOnesOfALongVideo(int dpi)
    {
        using var style = new OverlayStyle(dpi);
        using var bitmap = new Bitmap(style.PillHeight * 2, style.PillHeight * 2, PixelFormat.Format32bppPArgb);
        var circle = style.Circle(style.PillHeight);

        foreach (var scale in new[] { 0.4f, 0.7f, 1f, 1.06f })
        {
            for (var share = 1e-7; share <= 1; share *= 1.3)
            {
                using var g = Graphics.FromImage(bitmap);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(circle.Width / 2, circle.Height / 2);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-circle.Width / 2, -circle.Height / 2);

                var exception = Record.Exception(() => style.DrawRing(g, circle, share));

                Assert.True(exception == null, $"share {share:E2} at scale {scale}: {exception?.Message}");
            }

            using var full = Graphics.FromImage(bitmap);
            style.DrawRing(full, circle, 1);
        }
    }
}
