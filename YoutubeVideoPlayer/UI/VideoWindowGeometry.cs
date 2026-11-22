using System;
using System.Drawing;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Window sizes for a video area that always keeps 16:9. "Extra" is everything of the window that is not video:
    /// the border and title bar plus the control bar below the video, which is not part of the ratio.
    /// The video height is always derived from its width (<see cref="VideoHeightFor"/>), so every path (dragging,
    /// fitting, correcting another size) arrives at the same sizes.
    /// </summary>
    public static class VideoWindowGeometry
    {
        public const int AspectWidth = 16;
        public const int AspectHeight = 9;

        /// <summary>The smallest video width a drag can reach, at 96 DPI.</summary>
        public const int MinVideoWidth = 640;

        // The edge a window is dragged by (WMSZ_* of WM_SIZING).
        public const int EdgeLeft = 1;
        public const int EdgeRight = 2;
        public const int EdgeTop = 3;
        public const int EdgeTopLeft = 4;
        public const int EdgeTopRight = 5;
        public const int EdgeBottom = 6;
        public const int EdgeBottomLeft = 7;
        public const int EdgeBottomRight = 8;

        public static int VideoHeightFor(int videoWidth) =>
            (int)Math.Round(videoWidth * (double)AspectHeight / AspectWidth, MidpointRounding.AwayFromZero);

        public static int VideoWidthFor(int videoHeight) =>
            (int)Math.Round(videoHeight * (double)AspectWidth / AspectHeight, MidpointRounding.AwayFromZero);

        /// <summary>The window size for a video of <paramref name="videoWidth"/>.</summary>
        public static Size WindowSizeFor(int videoWidth, Size extra) =>
            new(videoWidth + extra.Width, VideoHeightFor(videoWidth) + extra.Height);

        /// <summary>
        /// Corrects the rectangle of a window that is being dragged by <paramref name="edge"/>: a side edge sets the
        /// width, the top or bottom edge the height, a corner whichever of the two changed more (so the window follows
        /// the mouse). The edges opposite the dragged one stay where they are.
        /// </summary>
        public static Rectangle AdjustDrag(Rectangle current, Rectangle proposed, int edge, Size extra, int minVideoWidth)
        {
            var currentWidth = current.Width - extra.Width;
            var fromWidth = proposed.Width - extra.Width;
            var fromHeight = VideoWidthFor(proposed.Height - extra.Height);
            var videoWidth = edge switch
            {
                EdgeLeft or EdgeRight => fromWidth,
                EdgeTop or EdgeBottom => fromHeight,
                _ => Math.Abs(fromWidth - currentWidth) >= Math.Abs(fromHeight - currentWidth) ? fromWidth : fromHeight
            };

            var size = WindowSizeFor(Math.Max(minVideoWidth, videoWidth), extra);
            var keepRight = edge is EdgeLeft or EdgeTopLeft or EdgeBottomLeft;
            var keepBottom = edge is EdgeTop or EdgeTopLeft or EdgeTopRight;
            var x = keepRight ? proposed.Right - size.Width : proposed.Left;
            var y = keepBottom ? proposed.Bottom - size.Height : proposed.Top;
            return new Rectangle(x, y, size.Width, size.Height);
        }

        /// <summary>Keeps the position and the width of <paramref name="bounds"/> and corrects the height.</summary>
        public static Rectangle NormalizeSize(Rectangle bounds, Size extra) =>
            new(bounds.Location, WindowSizeFor(Math.Max(1, bounds.Width - extra.Width), extra));

        /// <summary>
        /// The largest window with a 16:9 video whose visible part fits into <paramref name="workArea"/>, centered in it.
        /// <paramref name="invisibleFrame"/> is the part of the window around the visible frame (the resize borders of
        /// Windows 10/11), which may lie outside the work area.
        /// </summary>
        public static Rectangle Fit(Rectangle workArea, Size extra, Padding invisibleFrame)
        {
            var maxVideoWidth = workArea.Width - (extra.Width - invisibleFrame.Horizontal);
            var maxVideoHeight = workArea.Height - (extra.Height - invisibleFrame.Vertical);
            var videoWidth = Math.Max(1, Math.Min(maxVideoWidth, VideoWidthFor(maxVideoHeight)));
            while (videoWidth > 1 && VideoHeightFor(videoWidth) > maxVideoHeight)
                videoWidth--;

            var size = WindowSizeFor(videoWidth, extra);
            var visibleWidth = size.Width - invisibleFrame.Horizontal;
            var visibleHeight = size.Height - invisibleFrame.Vertical;
            var x = workArea.Left + (workArea.Width - visibleWidth) / 2 - invisibleFrame.Left;
            var y = workArea.Top + (workArea.Height - visibleHeight) / 2 - invisibleFrame.Top;
            return new Rectangle(x, y, size.Width, size.Height);
        }
    }
}
