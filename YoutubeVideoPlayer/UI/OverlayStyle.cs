using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The look of everything that is drawn over the video (the title, the vote ticker, the buffering, resume and waiting
    /// labels): a dark box, an orange badge with an icon at its left, Segoe UI text, and the vote items with a numbered
    /// circle, a small bar and the count.
    /// </summary>
    /// <remarks>
    /// Sizes are pixels at 96 DPI, scaled with the DPI the style is made for. Text is drawn with GDI
    /// (<see cref="TextRenderer"/>) on the opaque colours, shapes with GDI+.
    /// </remarks>
    internal sealed class OverlayStyle : IDisposable
    {
        /// <summary>The badge colour.</summary>
        public static readonly Color Accent = Color.FromArgb(0xD8, 0x5A, 0x30);

        /// <summary>Time, counts and bars on the dark box.</summary>
        public static readonly Color AccentSoft = Color.FromArgb(0xF5, 0xC4, 0xB3);

        /// <summary>The box: black at 200/255 over the form.</summary>
        private static readonly Color BoxOverlay = Color.FromArgb(200, 0, 0, 0);

        // Icons of Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10), which share these code points.
        public const char PlayGlyph = '';
        public const char VoteGlyph = '';
        public const char SkipGlyph = '';
        public const char ClockGlyph = '';
        public const char BufferingGlyph = '';
        public const char ConnectionLostGlyph = '';
        public const char ReloadGlyph = '';
        public const char WaitingGlyph = '';

        private const TextFormatFlags Flags =
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        private static readonly Lazy<string> IconFamily = new(() =>
        {
            using var fonts = new InstalledFontCollection();
            var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        });

        private readonly int _dpi;

        public OverlayStyle(int dpi, Color under)
        {
            _dpi = dpi;
            Box = Blend(BoxOverlay, under);
            Muted = Blend(Color.FromArgb(140, 255, 255, 255), Box);
            Faint = Blend(Color.FromArgb(40, 255, 255, 255), Box);
            Text = new Font("Segoe UI", Points(12.5f), GraphicsUnit.Pixel);
            Strong = new Font("Segoe UI Semibold", Points(12.5f), GraphicsUnit.Pixel);
            Small = new Font("Segoe UI Semibold", Points(9), GraphicsUnit.Pixel);
            Title = new Font("Segoe UI Semibold", Points(16), GraphicsUnit.Pixel);
            Caption = new Font("Segoe UI", Points(10), GraphicsUnit.Pixel);
            Icon = new Font(IconFamily.Value, Points(11.5f), GraphicsUnit.Pixel);
        }

        /// <summary>The dark box, opaque (the overlay colour blended over what is behind it).</summary>
        public Color Box { get; }

        /// <summary>Hints.</summary>
        public Color Muted { get; }

        /// <summary>Circles of the options that do not lead, the track of the bars, dividers.</summary>
        public Color Faint { get; }

        public Font Text { get; }

        public Font Strong { get; }

        public Font Small { get; }

        /// <summary>The title on the "now playing" card.</summary>
        public Font Title { get; }

        /// <summary>The second row of the "now playing" card.</summary>
        public Font Caption { get; }

        public Font Icon { get; }

        public int Height => Scale(36);

        public int Padding => Scale(12);

        public int Gap => Scale(8);

        public int Scale(double pixels) => (int)Math.Round(pixels * _dpi / 96.0);

        private float Points(float points) => points * _dpi / 72f;

        public static int Measure(string text, Font font) =>
            text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, Flags).Width;

        public void DrawText(Graphics g, string text, Font font, int x, Color color, Color back, int width = int.MaxValue / 2, bool ellipsis = false) =>
            DrawText(g, text, font, new Rectangle(x, 0, width, Height), color, back, ellipsis);

        /// <summary>Text centred vertically in <paramref name="bounds"/> (for rows other than the strip's one row).</summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, Color back, bool ellipsis = false) =>
            TextRenderer.DrawText(g, text, font, bounds, color, back,
                Flags | (ellipsis ? TextFormatFlags.EndEllipsis : TextFormatFlags.Default));

        /// <summary>
        /// Renders something once into a bitmap on the box colour, so a frame only has to copy it. <c>null</c> for an empty
        /// width.
        /// </summary>
        public Bitmap? Render(int width, int height, Action<Graphics> draw)
        {
            if (width <= 0 || height <= 0)
                return null;

            // No alpha channel: GDI text written into an ARGB bitmap would come out transparent.
            using var rgb = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(rgb))
            {
                g.Clear(Box);
                draw(g);
            }

            // Premultiplied ARGB (opaque) is the format GDI+ copies fastest; anything else is converted on every frame.
            return rgb.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppPArgb);
        }

        /// <summary>The orange badge: an icon and, if given, a word.</summary>
        public int BadgeWidth(char icon, string? label) =>
            Padding + Measure(icon.ToString(), Icon) + (string.IsNullOrEmpty(label) ? 0 : Scale(7) + Measure(label, Strong)) + Padding;

        public int DrawBadge(Graphics g, int x, char icon, string? label)
        {
            var width = BadgeWidth(icon, label);
            using (var brush = new SolidBrush(Accent))
                g.FillRectangle(brush, x, 0, width, Height);

            var at = x + Padding;
            DrawText(g, icon.ToString(), Icon, at, Color.White, Accent);
            if (!string.IsNullOrEmpty(label))
                DrawText(g, label, Strong, at + Measure(icon.ToString(), Icon) + Scale(7), Color.White, Accent);

            return width;
        }

        /// <summary>The time left, with a clock, and a divider after it.</summary>
        public int TimeWidth(string time) =>
            Padding + Measure(ClockGlyph.ToString(), Icon) + Scale(6) + Measure(time, Strong) + Padding + 1;

        public int DrawTime(Graphics g, int x, string time)
        {
            var width = TimeWidth(time);
            var clock = ClockGlyph.ToString();
            DrawText(g, clock, Icon, x + Padding, AccentSoft, Box);
            DrawText(g, time, Strong, x + Padding + Measure(clock, Icon) + Scale(6), AccentSoft, Box);
            using (var pen = new Pen(Faint))
                g.DrawLine(pen, x + width - 1, 0, x + width - 1, Height);

            return width;
        }

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

        public int DrawItem(Graphics g, int x, TickerItem item)
        {
            var at = x + Scale(14);
            if (item.Number != null)
            {
                var diameter = Scale(20);
                var top = (Height - diameter) / 2;
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(item.Leading ? Accent : Faint))
                    g.FillEllipse(brush, at, top, diameter, diameter);
                g.SmoothingMode = smoothing;

                TextRenderer.DrawText(g, item.Number, Small, new Rectangle(at, top, diameter, diameter), Color.White,
                    item.Leading ? Accent : Faint,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                        | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                at += diameter + Gap;
            }

            DrawText(g, item.Text, Text, at, Color.White, Box);
            at += Measure(item.Text, Text);

            if (item.Share is { } share)
            {
                at += Gap;
                var width = Scale(46);
                var height = Math.Max(2, Scale(4));
                DrawBar(g, new Rectangle(at, (Height - height) / 2, width, height), share, item.Leading ? Color.White : AccentSoft);
                at += width;
            }

            if (item.Count != null)
                DrawText(g, item.Count, Strong, at + Scale(7), item.Leading ? Color.White : AccentSoft, Box);

            return ItemWidth(item);
        }

        /// <summary>A rounded bar: the track in <see cref="Faint"/>, the share (0 to 1) of it filled in <paramref name="fill"/>.</summary>
        public void DrawBar(Graphics g, Rectangle bounds, double share, Color fill)
        {
            using (var track = new SolidBrush(Faint))
                FillRounded(g, track, bounds);

            var filled = (float)(bounds.Width * Math.Clamp(share, 0, 1));
            if (filled > 0.5f)
            {
                using var bar = new SolidBrush(fill);
                FillRounded(g, bar, new RectangleF(bounds.X, bounds.Y, Math.Max(filled, bounds.Height), bounds.Height));
            }
        }

        private static void FillRounded(Graphics g, Brush brush, RectangleF rect)
        {
            var radius = rect.Height / 2;
            using var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, radius * 2, rect.Height, 90, 180);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, rect.Height, 270, 180);
            path.CloseFigure();
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            g.SmoothingMode = smoothing;
        }

        /// <summary><paramref name="over"/> (with its alpha) on top of the opaque <paramref name="under"/>.</summary>
        public static Color Blend(Color over, Color under)
        {
            var a = over.A / 255.0;
            return Color.FromArgb(
                (int)Math.Round(over.R * a + under.R * (1 - a)),
                (int)Math.Round(over.G * a + under.G * (1 - a)),
                (int)Math.Round(over.B * a + under.B * (1 - a)));
        }

        public void Dispose()
        {
            Text.Dispose();
            Strong.Dispose();
            Small.Dispose();
            Title.Dispose();
            Caption.Dispose();
            Icon.Dispose();
        }
    }
}
