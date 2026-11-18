using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The strip at the bottom left of the video: the video title, or during a chat vote a ticker that scrolls the
    /// options from right to left. Switching between the two cross-fades inside the strip: the old text fades out, the
    /// box resizes, the new text fades in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A child window cannot be see-through over VLC's video window (a "transparent" background is painted from the
    /// form behind it, not from the video), so the box itself stays solid and only its contents and size are animated.
    /// </para>
    /// <para>
    /// Each text is rendered once into a bitmap when it changes; a frame only copies it. Frames come from a
    /// <see cref="FrameClock"/> (one per display refresh), which runs only while an animation runs or the ticker scrolls.
    /// </para>
    /// <para>
    /// The ticker moves a whole number of pixels per refresh (<see cref="PixelsPerFrame"/>), so every frame moves it by the
    /// same distance; a refresh the UI thread missed is made up in the next frame. Fades and the resize follow elapsed time.
    /// </para>
    /// </remarks>
    public sealed class TitleStrip : Control
    {
        internal enum Content
        {
            None,
            Title,
            Ticker
        }

        internal static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
        internal static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(200);

        /// <summary>The ticker's speed at 96 DPI; scaled with the display, and rounded to whole pixels per refresh.</summary>
        private const double BasePixelsPerSecond = 120;

        private const int TitleHeight = 25;
        private const int TickerPadding = 8;

        /// <summary>The box colour of the overlay labels (black at 200/255 over the form).</summary>
        private static readonly Color BoxColor = Color.FromArgb(200, 0, 0, 0);

        private readonly TimeProvider _time;
        private readonly FrameClock _clock;
        private readonly Font _titleFont = new(FontFamily.GenericSansSerif, 10, FontStyle.Bold);
        private readonly Font _tickerFont = new(FontFamily.GenericSansSerif, 14, FontStyle.Bold);

        private string _title = string.Empty;
        private string? _tickerText;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Content _shown = Content.None;
        private Bitmap? _bitmap;
        private double _alpha;
        private int _scrollOffset;
        private double _frameDebt;
        private double? _frameRate;
        private long _lastTick;

        private Size _resizeFrom;
        private Size _resizeTo;
        private long _resizeStart;
        private bool _resizing;

        /// <param name="timeProvider">The clock for the animation; <see cref="TimeProvider.System"/> by default.</param>
        public TitleStrip(TimeProvider? timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Visible = false;
            Size = new Size(0, TitleHeight);

            _clock = new FrameClock(refreshes =>
            {
                Advance(refreshes);
                Update(); // paint now, before the compositor's next frame
            });
        }

        /// <summary>The video title (or a status such as "Loading next video..."); shown while no ticker runs.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _title;
            set
            {
                value ??= string.Empty;
                if (value == _title)
                    return;

                _title = value;
                if (_shown == Content.Title && Desired == Content.Title)
                {
                    // A new title is not a transition: it replaces the old one at once.
                    Render();
                    if (!_resizing)
                        ApplySize(TargetSize(Content.Title));
                    Invalidate();
                }

                Retarget();
            }
        }

        /// <summary>The text the ticker scrolls through; <c>null</c> shows the title.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? TickerText
        {
            get => _tickerText;
            set
            {
                if (value == _tickerText)
                    return;

                _tickerText = value;
                if (_shown == Content.Ticker && Desired == Content.Ticker)
                {
                    // New counts or time: the text keeps scrolling from where it is.
                    Render();
                    Invalidate();
                }

                Retarget();
            }
        }

        /// <summary>The widest the strip may be (the ticker uses all of it).</summary>
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
                if (_shown == Content.Title)
                    Render(); // the end ellipsis depends on the width

                if (!IsAnimating)
                    ApplySize(TargetSize(_shown));
                Invalidate();
                Retarget();
            }
        }

        /// <summary>Where the strip's bottom-left corner is; it grows upwards and to the right from there.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Point BottomLeft
        {
            get => _bottomLeft;
            set
            {
                _bottomLeft = value;
                ApplySize(Size);
            }
        }

        /// <summary>The display's refresh rate (measured by the frame clock; tests set it).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal double FrameRate
        {
            get => _frameRate ?? _clock.FramesPerSecond;
            set => _frameRate = value;
        }

        /// <summary>How far the ticker moves per refresh: the same whole number of pixels every frame.</summary>
        internal int PixelsPerFrame => Math.Max(1, (int)Math.Round(BasePixelsPerSecond * DeviceDpi / 96.0 / FrameRate));

        internal Content Shown => _shown;

        /// <summary>How visible the text is: 0 (faded out) to 1.</summary>
        internal double ContentAlpha => _alpha;

        /// <summary>How far the ticker has scrolled since its text entered from the right (wraps after each loop).</summary>
        internal int ScrollOffset => _scrollOffset;

        internal bool IsAnimating => _shown != Desired || _resizing || Size != TargetSize(_shown) || (_shown != Content.None && _alpha < 1);

        internal bool IsClockRunning => _clock.IsRunning;

        private Content Desired =>
            _tickerText != null ? Content.Ticker
            : _title.Length > 0 ? Content.Title
            : Content.None;

        private int TickerHeight => _tickerFont.Height + TickerPadding;

        /// <summary>The ticker text and the gap before it starts again.</summary>
        private int LoopLength => (_bitmap?.Width ?? 0) + Math.Max(50, Width / 3);

        /// <summary>
        /// Moves the animation and the ticker to the current time. The frame clock passes the number of refreshes since
        /// the last frame; without it (tests), they are counted from the elapsed time.
        /// </summary>
        internal void Advance(int? refreshes = null)
        {
            var now = _time.GetTimestamp();
            var seconds = Math.Max(0, _time.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;
            var fadeStep = seconds / FadeDuration.TotalSeconds;

            var desired = Desired;
            if (_shown != desired)
            {
                // The old content fades out first; at zero the new one takes its place (still invisible).
                _alpha = Math.Max(0, _alpha - fadeStep);
                if (_alpha <= 0)
                {
                    _shown = desired;
                    _scrollOffset = 0;
                    _frameDebt = 0;
                    Render();
                }
            }
            else
            {
                var target = TargetSize(_shown);
                if (Size != target || _resizing)
                {
                    ResizeTowards(target, now);
                }
                else if (_shown != Content.None && _alpha < 1)
                {
                    _alpha = Math.Min(1, _alpha + fadeStep);
                }
            }

            if (_shown == Content.Ticker)
                Scroll(refreshes ?? RefreshesIn(seconds));

            if (_shown == Content.None && desired == Content.None && Width == 0)
                Visible = false;

            Invalidate();
            if (!IsAnimating && _shown != Content.Ticker)
                _clock.Stop();
        }

        private void Retarget()
        {
            if (Desired != Content.None)
                Visible = true;

            if ((IsAnimating || Desired == Content.Ticker) && !_clock.IsRunning)
            {
                _lastTick = _time.GetTimestamp();
                _frameDebt = 0;
                _clock.Start();
            }
        }

        private void ResizeTowards(Size target, long now)
        {
            if (!_resizing || _resizeTo != target)
            {
                _resizing = true;
                _resizeFrom = Size;
                _resizeTo = target;
                _resizeStart = now;
            }

            var progress = Math.Min(1, _time.GetElapsedTime(_resizeStart, now).TotalSeconds / ResizeDuration.TotalSeconds);
            var eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2; // ease in-out
            ApplySize(new Size(
                (int)Math.Round(_resizeFrom.Width + (_resizeTo.Width - _resizeFrom.Width) * eased),
                (int)Math.Round(_resizeFrom.Height + (_resizeTo.Height - _resizeFrom.Height) * eased)));

            if (progress >= 1)
                _resizing = false;
        }

        /// <summary>Whole refreshes in <paramref name="seconds"/>; the fraction is carried over, so nothing is lost.</summary>
        private int RefreshesIn(double seconds)
        {
            var exact = _frameDebt + seconds * FrameRate;
            var refreshes = Math.Max(0, (int)Math.Round(exact));
            _frameDebt = exact - refreshes;
            return refreshes;
        }

        private void Scroll(int refreshes)
        {
            _scrollOffset += refreshes * PixelsPerFrame;

            // Once the text has run through completely, it starts over one loop further on.
            var loop = LoopLength;
            while (loop > 0 && Width - _scrollOffset <= -loop)
                _scrollOffset -= loop;
        }

        private Size TargetSize(Content content) => content switch
        {
            Content.Ticker => new Size(_availableWidth, TickerHeight),
            Content.Title => new Size(Math.Min(_availableWidth, TextRenderer.MeasureText(_title, _titleFont).Width), TitleHeight),
            _ => new Size(0, Height)
        };

        private void ApplySize(Size size) =>
            SetBounds(_bottomLeft.X, Math.Max(0, _bottomLeft.Y - size.Height), size.Width, size.Height);

        /// <summary>Renders the shown text once, on the box colour, so a frame only has to copy it.</summary>
        private void Render()
        {
            _bitmap?.Dispose();
            _bitmap = null;

            var (text, font, width, height, flags) = _shown switch
            {
                Content.Title => (_title, _titleFont, Math.Min(_availableWidth, TextRenderer.MeasureText(_title, _titleFont).Width),
                    TitleHeight, TextFormatFlags.EndEllipsis),
                Content.Ticker => (_tickerText ?? string.Empty, _tickerFont, TextRenderer.MeasureText(_tickerText ?? string.Empty, _tickerFont).Width,
                    TickerHeight, TextFormatFlags.Default),
                _ => (string.Empty, _titleFont, 0, 0, TextFormatFlags.Default)
            };

            if (width <= 0 || height <= 0)
                return;

            // No alpha channel: GDI text written into an ARGB bitmap would come out transparent.
            using var rgb = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(rgb))
            {
                g.Clear(OpaqueBoxColor);
                TextRenderer.DrawText(
                    g, text, font, new Rectangle(0, 0, width, height), Color.White, OpaqueBoxColor,
                    flags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            // Premultiplied ARGB (opaque) is the format GDI+ copies fastest; anything else is converted on every frame.
            _bitmap = rgb.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppPArgb);
        }

        /// <summary>The box colour blended over the form, as the overlay labels look.</summary>
        private Color OpaqueBoxColor
        {
            get
            {
                var under = Parent?.BackColor ?? SystemColors.Control;
                var a = BoxColor.A / 255.0;
                return Color.FromArgb(
                    (int)Math.Round(BoxColor.R * a + under.R * (1 - a)),
                    (int)Math.Round(BoxColor.G * a + under.G * (1 - a)),
                    (int)Math.Round(BoxColor.B * a + under.B * (1 - a)));
            }
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            Render(); // the box colour depends on the form
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var box = new SolidBrush(OpaqueBoxColor))
                g.FillRectangle(box, ClientRectangle);

            if (_bitmap == null || _alpha <= 0)
                return;

            if (_shown == Content.Ticker)
            {
                var loop = LoopLength;
                for (var x = Width - _scrollOffset; x < Width && loop > 0; x += loop)
                {
                    if (x + _bitmap.Width > 0)
                        Draw(g, x);
                }
            }
            else
            {
                Draw(g, 0);
            }

            base.OnPaint(e);
        }

        private void Draw(Graphics g, int x)
        {
            // Pixel rectangles on both sides: the bitmap's own DPI must not scale it.
            var bitmap = _bitmap!;
            var target = new Rectangle(x, (Height - bitmap.Height) / 2, bitmap.Width, bitmap.Height);
            if (_alpha >= 1)
            {
                // The bitmap is opaque: a plain copy, no blending.
                var mode = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                g.CompositingMode = mode;
                return;
            }

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)_alpha });
            g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                _bitmap?.Dispose();
                _bitmap = null;
                _titleFont.Dispose();
                _tickerFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
