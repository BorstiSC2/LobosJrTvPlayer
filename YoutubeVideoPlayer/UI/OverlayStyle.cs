using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The look of everything that is drawn over the video (the "now playing" card, the status strip and vote ticker, the
    /// labels in the middle, the volume): a translucent dark pill with round ends, an orange circle with an icon at its
    /// left, white Segoe UI text and a soft orange caption.
    /// </summary>
    /// <remarks>
    /// Everything is drawn with GDI+ onto the transparent frame of a <see cref="LayeredOverlay"/> (GDI text cannot be drawn
    /// with an alpha channel, so text gets grey-scale smoothing). Sizes are pixels at 96 DPI, scaled with the DPI the style
    /// is made for.
    /// </remarks>
    internal sealed class OverlayStyle : IDisposable
    {
        /// <summary>The circles, the leading option, the bars.</summary>
        public static readonly Color Accent = Color.FromArgb(0xD8, 0x5A, 0x30);

        /// <summary>Captions ("Now playing", "Vote") and counts on the pill.</summary>
        public static readonly Color AccentSoft = Color.FromArgb(0xF5, 0xC4, 0xB3);

        /// <summary>The pill: almost black, see-through.</summary>
        public static readonly Color Back = Color.FromArgb(225, 14, 15, 19);

        /// <summary>Times and hints.</summary>
        public static readonly Color Muted = Color.FromArgb(180, 255, 255, 255);

        /// <summary>Circles of the options that do not lead, the track of the bars, dividers.</summary>
        public static readonly Color Faint = Color.FromArgb(50, 255, 255, 255);

        /// <summary>The track of a ring on an orange circle.</summary>
        public static readonly Color RingTrack = Color.FromArgb(0x71, 0x2B, 0x13);

        // Icons of Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10), which share these code points.
        public const char PlayGlyph = '';
        public const char VoteGlyph = '';
        public const char SkipGlyph = '';
        public const char ClockGlyph = '';
        public const char BufferingGlyph = '';
        public const char ConnectionLostGlyph = '';
        public const char ReloadGlyph = '';
        public const char WaitingGlyph = '';
        public const char VolumeGlyph = '';
        public const char MuteGlyph = '';

        private static readonly Lazy<string> IconFamily = new(() =>
        {
            using var fonts = new InstalledFontCollection();
            var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        });

        private readonly int _dpi;

        public OverlayStyle(int dpi)
        {
            _dpi = dpi;
            Text = new Font("Segoe UI", Points(12.5f), GraphicsUnit.Pixel);
            Strong = new Font("Segoe UI Semibold", Points(12.5f), GraphicsUnit.Pixel);
            Small = new Font("Segoe UI Semibold", Points(9), GraphicsUnit.Pixel);
            Title = new Font("Segoe UI Semibold", Points(16), GraphicsUnit.Pixel);
            Caption = new Font("Segoe UI", Points(10), GraphicsUnit.Pixel);
            Icon = new Font(IconFamily.Value, Points(11.5f), GraphicsUnit.Pixel);
            LargeIcon = new Font(IconFamily.Value, Points(14), GraphicsUnit.Pixel);
        }

        public Font Text { get; }

        public Font Strong { get; }

        /// <summary>The numbers in the option circles.</summary>
        public Font Small { get; }

        /// <summary>The main line of the card and the strip.</summary>
        public Font Title { get; }

        /// <summary>The second line ("Now playing · 12:04", "Vote · 1:20").</summary>
        public Font Caption { get; }

        /// <summary>Icons in the circles of the labels in the middle.</summary>
        public Font Icon { get; }

        /// <summary>Icons in the circles of the card and the strip.</summary>
        public Font LargeIcon { get; }

        /// <summary>The card and the strip at the bottom left (they share the place and the size).</summary>
        public int PillHeight => Scale(64);

        /// <summary>The labels in the middle and the volume.</summary>
        public int CompactHeight => Scale(48);

        /// <summary>The space between the circle and the pill's edge.</summary>
        public int CircleMargin(int height) => height >= PillHeight ? Scale(8) : Scale(6);

        /// <summary>Where the text starts: right of the circle.</summary>
        public int TextLeft(int height) => height - CircleMargin(height) + Scale(height >= PillHeight ? 12 : 10);

        /// <summary>The space after the text, before the round end.</summary>
        public int TextRight(int height) => Scale(height >= PillHeight ? 24 : 20);

        public int Gap => Scale(8);

        public int Scale(double pixels) => (int)Math.Round(pixels * _dpi / 96.0);

        private float Points(float points) => points * _dpi / 72f;

        /// <summary>The colour with its alpha multiplied by <paramref name="alpha"/> (0 to 1).</summary>
        public static Color Fade(Color color, double alpha) =>
            Color.FromArgb((int)Math.Round(color.A * Math.Clamp(alpha, 0, 1)), color);

        private static StringFormat Format(bool ellipsis, StringAlignment alignment = StringAlignment.Near)
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            format.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            return format;
        }

        /// <summary>The width of a text as <see cref="DrawText"/> draws it.</summary>
        public static int Measure(string text, Font font)
        {
            if (text.Length == 0)
                return 0;

            using var bitmap = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bitmap);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = Format(ellipsis: false);
            return (int)Math.Ceiling(g.MeasureString(text, font, PointF.Empty, format).Width);
        }

        /// <summary>Text centred vertically in <paramref name="bounds"/>, cut off with an ellipsis if asked.</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds, bool ellipsis = false,
            StringAlignment alignment = StringAlignment.Near)
        {
            if (text.Length == 0 || color.A == 0 || bounds.Width <= 0)
                return;

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            using var format = Format(ellipsis, alignment);
            g.DrawString(text, font, brush, bounds, format);
        }

        /// <summary>A shape with round ends (a circle where it is not wider than high).</summary>
        public static GraphicsPath PillPath(RectangleF bounds)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(bounds.Width, bounds.Height);
            if (diameter <= 0)
                return path;

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 90, 180);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 180);
            path.CloseFigure();
            return path;
        }

        /// <summary>The translucent pill.</summary>
        public static void FillPill(Graphics g, RectangleF bounds, double alpha = 1)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var path = PillPath(bounds);
            using var brush = new SolidBrush(Fade(Back, alpha));
            g.FillPath(brush, path);
        }

        /// <summary>The orange circle of a pill whose height is <paramref name="height"/>.</summary>
        public RectangleF Circle(int height)
        {
            var margin = CircleMargin(height);
            return new RectangleF(margin, margin, height - 2 * margin, height - 2 * margin);
        }

        public static void FillCircle(Graphics g, RectangleF circle, double alpha = 1)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Fade(Accent, alpha));
            g.FillEllipse(brush, circle);
        }

        /// <summary>An icon glyph, centred in the circle.</summary>
        public static void DrawGlyph(Graphics g, char glyph, Font font, RectangleF circle, double alpha = 1) =>
            DrawText(g, glyph.ToString(), font, Fade(Color.White, alpha), circle, alignment: StringAlignment.Center);

        /// <summary>A solid play triangle with soft corners; it looks centred a little right of the middle.</summary>
        public void DrawPlay(Graphics g, RectangleF circle, double alpha = 1)
        {
            var size = circle.Width * 0.34f;
            var x = circle.X + circle.Width / 2 - size * 0.38f;
            var y = circle.Y + circle.Height / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var triangle = new GraphicsPath();
            using var fill = new SolidBrush(Fade(Color.White, alpha));
            using var corners = new Pen(Fade(Color.White, alpha), Math.Max(1f, Scale(2))) { LineJoin = LineJoin.Round };
            triangle.AddPolygon(new[] { new PointF(x, y - size / 2), new PointF(x + size * 0.9f, y), new PointF(x, y + size / 2) });
            g.FillPath(fill, triangle);
            g.DrawPath(corners, triangle);
        }

        /// <summary>A ring along the inside of the circle that fills clockwise from the top (<paramref name="share"/> 0 to 1).</summary>
        public void DrawRing(Graphics g, RectangleF circle, double share, double alpha = 1)
        {
            var stroke = Math.Max(2f, Scale(3));
            var ring = RectangleF.Inflate(circle, -stroke, -stroke);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new Pen(Fade(RingTrack, alpha), stroke))
                g.DrawEllipse(track, ring);
            var sweep = (float)(360 * Math.Clamp(share, 0, 1));
            if (sweep <= 0)
                return;

            if (sweep < MinimumArc)
            {
                // GDI+ cannot draw a very short arc with round caps (DrawArc fails with "An object could not be created
                // ...", measured up to 0.37°): the ring of a long video is that short in its first seconds. Such an arc
                // looks like a dot anyway: a round cap at the top of the ring.
                using var dot = new SolidBrush(Fade(Color.White, alpha));
                g.FillEllipse(dot, ring.X + ring.Width / 2 - stroke / 2, ring.Y - stroke / 2, stroke, stroke);
                return;
            }

            using var bar = new Pen(Fade(Color.White, alpha), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(bar, ring, -90, sweep);
        }

        /// <summary>Arcs shorter than this (degrees) are drawn as a dot; see <see cref="DrawRing"/>.</summary>
        private const float MinimumArc = 2;

        /// <summary>One option of a vote: number in a circle, title, bar, count (all optional but the title).</summary>
        public int ItemWidth(TickerItem item)
        {
            var width = Scale(14);
            if (item.Number != null)
                width += Scale(20) + Gap;
            width += Measure(item.Text, Text);
            if (item.Share is not null)
                width += Gap + Scale(46);
            if (item.Count != null)
                width += Scale(7) + Measure(item.Count, Strong);
            return width + Scale(14);
        }

        /// <summary>Draws an option at <paramref name="x"/>, centred in the row from <paramref name="top"/> on.</summary>
        public int DrawItem(Graphics g, int x, int top, int height, TickerItem item)
        {
            var at = x + Scale(14);
            if (item.Number != null)
            {
                var diameter = Scale(20);
                var circle = new RectangleF(at, top + (height - diameter) / 2f, diameter, diameter);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(item.Leading ? Accent : Faint))
                    g.FillEllipse(brush, circle);
                DrawText(g, item.Number, Small, Color.White, circle, alignment: StringAlignment.Center);
                at += diameter + Gap;
            }

            var textWidth = Measure(item.Text, Text);
            DrawText(g, item.Text, Text, Color.White, new RectangleF(at, top, textWidth + 1, height));
            at += textWidth;

            if (item.Share is { } share)
            {
                at += Gap;
                var width = Scale(46);
                var barHeight = Math.Max(2, Scale(4));
                DrawBar(g, new RectangleF(at, top + (height - barHeight) / 2f, width, barHeight), share, item.Leading ? Color.White : AccentSoft);
                at += width;
            }

            if (item.Count != null)
                DrawText(g, item.Count, Strong, item.Leading ? Color.White : AccentSoft,
                    new RectangleF(at + Scale(7), top, Measure(item.Count, Strong) + 1, height));

            return ItemWidth(item);
        }

        /// <summary>A rounded bar: the track in <see cref="Faint"/>, the share (0 to 1) of it filled in <paramref name="fill"/>.</summary>
        public static void DrawBar(Graphics g, RectangleF bounds, double share, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Faint))
            using (var path = PillPath(bounds))
                g.FillPath(track, path);

            var filled = (float)(bounds.Width * Math.Clamp(share, 0, 1));
            if (filled > 0.5f)
            {
                using var bar = new SolidBrush(fill);
                using var path = PillPath(bounds with { Width = Math.Max(filled, bounds.Height) });
                g.FillPath(bar, path);
            }
        }

        public void Dispose()
        {
            Text.Dispose();
            Strong.Dispose();
            Small.Dispose();
            Title.Dispose();
            Caption.Dispose();
            Icon.Dispose();
            LargeIcon.Dispose();
        }
    }
}
