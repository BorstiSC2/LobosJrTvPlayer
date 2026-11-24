using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>What the "now playing" card shows.</summary>
    public sealed record NowPlayingCardContent(string Title, TimeSpan Position, TimeSpan? Length);

    /// <summary>
    /// The "now playing" card at the bottom left of the video, a pill: at its left an orange circle with the play icon and
    /// a ring that fills with the position, at its right the title and, below it, "Now playing · 12:04 / 45:10". It
    /// opens in place: the circle pops up, the pill grows out of it to the right, the text fades in; closing runs the same
    /// backwards. When it is due is up to <see cref="NowPlayingSchedule"/> and the form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the other overlays it is a layered child window (<c>WS_EX_LAYERED</c>, filled with
    /// <c>UpdateLayeredWindow</c>): its pixels have their own alpha, so the round ends are smooth and the box is see-through
    /// over VLC's video window, which a normal child window cannot be. Layered child windows need Windows 8 or later and a
    /// manifest that declares it (<c>app.manifest</c>). Without one (the test host) the window cannot be created as layered;
    /// it is then created as a normal window and paints the same frame on black.
    /// </para>
    /// <para>
    /// The window always has the full size; the animation is drawn into it. The text is rendered once per content (about
    /// once a second while it is up); a frame only composes the pill, the ring and the text. Frames come from a
    /// <see cref="FrameClock"/>, which runs only while it moves.
    /// </para>
    /// </remarks>
    public sealed class NowPlayingCard : Control
    {
        internal static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(550);
        internal static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(350);

        internal const string Label = "Now playing";

        // The phases of the progress (0 = closed, 1 = open). Closing runs them backwards.
        private const double RingEnd = 0.35;     // the circle pops up (0 .. RingEnd)
        private const double GrowStart = 0.25;   // the pill grows to the right (GrowStart .. GrowEnd)
        private const double GrowEnd = 0.8;
        private const double TextStart = 0.7;    // the text fades in (TextStart .. 1)

        /// <summary>The pill: almost black, see-through.</summary>
        private static readonly Color Back = Color.FromArgb(225, 14, 15, 19);

        /// <summary>The ring's track on the orange circle.</summary>
        private static readonly Color RingTrack = Color.FromArgb(0x71, 0x2B, 0x13);

        /// <summary>The time on the second row.</summary>
        private static readonly Color Muted = Color.FromArgb(180, 255, 255, 255);

        private const int WS_EX_LAYERED = 0x80000;

        private readonly TimeProvider _time;
        private readonly FrameClock _clock;

        private bool _layered = true;
        private OverlayStyle? _style;
        private NowPlayingCardContent? _content;
        private bool _showing;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Bitmap? _text;       // title and second row, on transparent
        private string? _textKey;
        private Bitmap? _frame;      // what the window shows

        private double _progress;
        private long _lastTick;

        /// <param name="timeProvider">The clock for the animation; <see cref="TimeProvider.System"/> by default.</param>
        public NowPlayingCard(TimeProvider? timeProvider = null)
            : this(timeProvider, layered: true)
        {
        }

        /// <param name="timeProvider">The clock for the animation; <see cref="TimeProvider.System"/> by default.</param>
        /// <param name="layered">
        /// <c>false</c> makes a normal window, as where Windows does not allow a layered one (tests; the test host allows it).
        /// </param>
        internal NowPlayingCard(TimeProvider? timeProvider, bool layered)
        {
            _time = timeProvider ?? TimeProvider.System;
            _layered = layered;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Visible = false;

            _clock = new FrameClock(_ =>
            {
                Advance();
                if (!_layered)
                    Update(); // paint now, before the compositor's next frame
            });
        }

        /// <summary>The video, its position and length; <c>null</c> shows nothing.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public NowPlayingCardContent? Content
        {
            get => _content;
            set
            {
                if (value == _content)
                    return;

                _content = value;
                Relayout(); // a new title may change the width
                Retarget();
            }
        }

        /// <summary>Whether the card should be up; it opens or closes to that state.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Showing
        {
            get => _showing;
            set
            {
                if (value == _showing)
                    return;

                _showing = value;
                Retarget();
            }
        }

        /// <summary>The widest the card may be.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int AvailableWidth
        {
            get => _availableWidth;
            set
            {
                value = Math.Max(1, value);
                if (value == _availableWidth)
                    return;

                _availableWidth = value;
                Relayout();
            }
        }

        /// <summary>Where the card's bottom-left corner is.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Point BottomLeft
        {
            get => _bottomLeft;
            set
            {
                _bottomLeft = value;
                ApplyBounds();
            }
        }

        /// <summary>0 (closed) to 1 (open).</summary>
        internal double Progress => _progress;

        internal bool IsAnimating => _progress != Target;

        internal bool IsClockRunning => _clock.IsRunning;

        /// <summary>Whether the window is layered (see-through); false where Windows does not allow it (the test host).</summary>
        internal bool IsLayered => _layered && IsHandleCreated;

        /// <summary>How wide the pill is drawn right now: the circle's width when it starts, <see cref="CardWidth"/> once open.</summary>
        internal int PillWidth => (int)Math.Round(CardHeight + (CardWidth - CardHeight) * Grow(_progress));

        /// <summary>What the card shows, as one line (tests).</summary>
        internal string PlainText => _content == null ? string.Empty : $"{Label} · {TimeText(_content)} | {_content.Title}";

        private double Target => _showing && _content != null ? 1 : 0;

        private OverlayStyle Style => _style ??= new OverlayStyle(DeviceDpi, Color.Black);

        internal int CardHeight => Style.Scale(64);

        private int CircleMargin => Style.Scale(8);

        private int TextLeft => CardHeight - CircleMargin + Style.Scale(12);

        private int TextRight => Style.Scale(24);

        private int TextWidth => Math.Max(0, CardWidth - TextLeft - TextRight);

        /// <summary>The width of the card for its title: at least <c>280</c> px (scaled), at most <see cref="AvailableWidth"/>.</summary>
        internal int CardWidth
        {
            get
            {
                var style = Style;
                var minimum = Math.Min(_availableWidth, style.Scale(280));
                if (_content is not { } content)
                    return minimum;

                // The time is measured with zeros, so the width does not change every second.
                var time = new string(TimeText(content).Select(c => char.IsDigit(c) ? '0' : c).ToArray());
                var text = Math.Max(Measure(content.Title, style.Title), Measure(SecondRow(time), style.Caption));
                return Math.Min(_availableWidth, Math.Max(minimum, TextLeft + (int)Math.Ceiling(text) + TextRight));
            }
        }

        /// <summary><c>12:04 / 45:10</c>, or the position alone when the length is unknown.</summary>
        internal static string TimeText(NowPlayingCardContent content)
        {
            if (content.Length is not { } length || length <= TimeSpan.Zero)
                return PositionText.Format(content.Position);

            var position = content.Position > length ? length : content.Position;
            return $"{PositionText.Format(position)} / {PositionText.Format(length)}";
        }

        private static string SecondRow(string time) => $"{Label} · {time}";

        private static double Share(NowPlayingCardContent content) =>
            content.Length is { } length && length > TimeSpan.Zero
                ? Math.Clamp(content.Position.TotalSeconds / length.TotalSeconds, 0, 1)
                : -1;

        private static double Phase(double progress, double start, double end) => Math.Clamp((progress - start) / (end - start), 0, 1);

        private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

        /// <summary>Overshoots a little and settles: the circle "pops".</summary>
        private static double EaseOutBack(double x)
        {
            const double c1 = 1.70158;
            const double c3 = c1 + 1;
            return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
        }

        private static double Grow(double progress) => EaseOutCubic(Phase(progress, GrowStart, GrowEnd));

        /// <summary>
        /// Moves the animation to the current time. The frame clock calls it once per refresh; tests call it themselves.
        /// </summary>
        internal void Advance()
        {
            var now = _time.GetTimestamp();
            var seconds = Math.Max(0, _time.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;

            var target = Target;
            _progress = target > _progress
                ? Math.Min(target, _progress + seconds / OpenDuration.TotalSeconds)
                : Math.Max(target, _progress - seconds / CloseDuration.TotalSeconds);

            Present();
            if (_progress <= 0 && target <= 0)
                Visible = false;

            if (!IsAnimating)
                _clock.Stop();
        }

        private void Retarget()
        {
            if (Target > 0 && !Visible)
            {
                Present(); // nothing to see yet, but no stale frame either
                Visible = true;
            }

            if (IsAnimating && !_clock.IsRunning)
            {
                _lastTick = _time.GetTimestamp();
                _clock.Start();
            }
        }

        /// <summary>A new size or content: new bounds, text and frame.</summary>
        private void Relayout()
        {
            ApplyBounds();
            RenderText();
            Present();
        }

        private void ApplyBounds()
        {
            var height = CardHeight;
            SetBounds(_bottomLeft.X, Math.Max(0, _bottomLeft.Y - height), CardWidth, height);
        }

        private static float Measure(string text, Font font)
        {
            if (text.Length == 0)
                return 0;

            using var bitmap = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bitmap);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = TextFormat(ellipsis: false);
            return g.MeasureString(text, font, PointF.Empty, format).Width;
        }

        private static StringFormat TextFormat(bool ellipsis)
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            format.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            format.LineAlignment = StringAlignment.Center;
            return format;
        }

        /// <summary>
        /// The title and the second row, on transparent (GDI+ text with grey-scale smoothing: GDI text cannot be drawn with
        /// an alpha channel). Only again when it would look different.
        /// </summary>
        private void RenderText()
        {
            if (_content is not { } content || TextWidth <= 0)
            {
                _text?.Dispose();
                _text = null;
                _textKey = null;
                return;
            }

            var style = Style;
            var width = TextWidth;
            var time = TimeText(content);
            var key = $"{content.Title}|{time}|{width}|{DeviceDpi}";
            if (key == _textKey)
                return;

            var titleHeight = style.Title.Height;
            var rowHeight = style.Caption.Height;
            var top = (CardHeight - titleHeight - rowHeight) / 2;

            var bitmap = new Bitmap(width, CardHeight, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var clipped = TextFormat(ellipsis: true))
            using (var plain = TextFormat(ellipsis: false))
            using (var white = new SolidBrush(Color.White))
            using (var accent = new SolidBrush(OverlayStyle.AccentSoft))
            using (var muted = new SolidBrush(Muted))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(content.Title, style.Title, white, new RectangleF(0, top, width, titleHeight), clipped);

                var row = new RectangleF(0, top + titleHeight, width, rowHeight);
                var label = Label + " · ";
                g.DrawString(label, style.Caption, accent, row, plain);
                var x = Measure(label, style.Caption);
                g.DrawString(time, style.Caption, muted, row with { X = x, Width = Math.Max(0, width - x) }, clipped);
            }

            _text?.Dispose();
            _text = bitmap;
            _textKey = key;
        }

        /// <summary>Draws the frame for the current progress and puts it on the screen.</summary>
        private void Present()
        {
            var width = CardWidth;
            var height = CardHeight;
            if (_frame == null || _frame.Width != width || _frame.Height != height)
            {
                _frame?.Dispose();
                _frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            }

            using (var g = Graphics.FromImage(_frame))
                DrawFrame(g);

            if (IsLayered)
                PushLayered();
            else
                Invalidate();
        }

        private void DrawFrame(Graphics g)
        {
            g.Clear(Color.Transparent);
            if (_progress <= 0 || _content is not { } content)
                return;

            var style = Style;
            var height = CardHeight;
            var pop = Phase(_progress, 0, RingEnd);
            var scale = (float)(0.4 + 0.6 * EaseOutBack(pop));
            var alpha = Math.Clamp(pop * 2.5, 0, 1);
            var pill = PillWidth;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // The pill and the circle grow out of the circle's middle.
            var state = g.Save();
            var centre = height / 2f;
            g.TranslateTransform(centre, centre);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-centre, -centre);

            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, height, height, 90, 180);
                path.AddArc(pill - height, 0, height, height, 270, 180);
                path.CloseFigure();
                using var back = new SolidBrush(Fade(Back, alpha));
                g.FillPath(back, path);
            }

            var margin = CircleMargin;
            var circle = new RectangleF(margin, margin, height - 2 * margin, height - 2 * margin);
            using (var fill = new SolidBrush(Fade(OverlayStyle.Accent, alpha)))
                g.FillEllipse(fill, circle);

            var share = Share(content);
            if (share >= 0)
            {
                var stroke = Math.Max(2f, style.Scale(3));
                var ring = RectangleF.Inflate(circle, -stroke, -stroke);
                using (var track = new Pen(Fade(RingTrack, alpha), stroke))
                    g.DrawEllipse(track, ring);
                if (share > 0)
                {
                    using var bar = new Pen(Fade(Color.White, alpha), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(bar, ring, -90, (float)(360 * share));
                }
            }

            // A solid play triangle with soft corners; it looks centred a little right of the middle.
            var size = circle.Width * 0.34f;
            var x = circle.X + circle.Width / 2 - size * 0.38f;
            var y = circle.Y + circle.Height / 2;
            using (var triangle = new GraphicsPath())
            using (var icon = new SolidBrush(Fade(Color.White, alpha)))
            using (var corners = new Pen(Fade(Color.White, alpha), Math.Max(1f, style.Scale(2))) { LineJoin = LineJoin.Round })
            {
                triangle.AddPolygon(new[] { new PointF(x, y - size / 2), new PointF(x + size * 0.9f, y), new PointF(x, y + size / 2) });
                g.FillPath(icon, triangle);
                g.DrawPath(corners, triangle);
            }

            g.Restore(state);

            var text = Phase(_progress, TextStart, 1);
            if (text > 0 && _text is { } bitmap)
            {
                var rise = (int)Math.Round(style.Scale(4) * (1 - EaseOutCubic(text)));
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)text });
                g.SetClip(new Rectangle(0, 0, Math.Max(0, pill - TextRight / 2), height));
                g.DrawImage(bitmap, new Rectangle(TextLeft, rise, bitmap.Width, bitmap.Height),
                    0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
                g.ResetClip();
            }
        }

        private static Color Fade(Color color, double alpha) => Color.FromArgb((int)Math.Round(color.A * alpha), color);

        private void PushLayered()
        {
            if (_frame is not { } frame)
                return;

            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var bitmap = frame.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memory, bitmap);
            try
            {
                var size = new NativeSize { Width = frame.Width, Height = frame.Height };
                var source = new NativePoint();
                var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (_layered)
                    parameters.ExStyle |= WS_EX_LAYERED;
                return parameters;
            }
        }

        protected override void CreateHandle()
        {
            if (_layered)
            {
                try
                {
                    base.CreateHandle();
                    return;
                }
                catch (Win32Exception)
                {
                    // Layered child windows need a manifest that declares Windows 8 or later (the app has one, the test
                    // host does not). Not logged: it is no error of the app.
                    _layered = false;
                }
            }

            base.CreateHandle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Present(); // a new window starts empty
        }

        /// <summary>A new DPI: everything is rendered again.</summary>
        private void ResetStyle()
        {
            _style?.Dispose();
            _style = null;
            _textKey = null;
            Relayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ResetStyle();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Only without a layered window; a layered one shows what PushLayered gave it.
            e.Graphics.Clear(Color.Black);
            if (_frame is { } frame)
                e.Graphics.DrawImage(frame, new Rectangle(0, 0, frame.Width, frame.Height), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                _text?.Dispose();
                _text = null;
                _frame?.Dispose();
                _frame = null;
                _style?.Dispose();
                _style = null;
            }

            base.Dispose(disposing);
        }

        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_ALPHA = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, IntPtr position, ref NativeSize size,
            IntPtr source, ref NativePoint sourcePosition, int colorKey, ref BlendFunction blend, int flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr item);
    }
}
