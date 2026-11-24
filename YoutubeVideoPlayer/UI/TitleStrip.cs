using System;
using System.ComponentModel;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The strip at the bottom left of the video: the video title, or during a chat vote a ticker that scrolls the
    /// options from right to left. Switching between the two cross-fades inside the strip: the old text fades out, the
    /// box resizes, the new text fades in. Another ticker (another vote, or a vote whose options have just been found)
    /// cross-fades the same way; new counts or a new time in the same ticker do not.
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

        private readonly TimeProvider _time;
        private readonly FrameClock _clock;

        private OverlayStyle? _style;
        private string _title = string.Empty;
        private TickerContent? _ticker;
        private string? _tickerKey;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Content _shown = Content.None;
        private string? _shownTickerKey;

        // The standing part (badge, and the time in ticker mode) and the body (the title, or the items that scroll),
        // each rendered once; the keys say what they show, so an unchanged part is not rendered again.
        private Bitmap? _standing;
        private Bitmap? _body;
        private string? _standingKey;
        private string? _bodyKey;
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
            Size = new Size(0, Style.Height);

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
                    Render(force: true);
                    if (!_resizing)
                        ApplySize(TargetSize(Content.Title));
                    Invalidate();
                }

                Retarget();
            }
        }

        /// <summary>What the vote ticker shows; <c>null</c> shows the title.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public TickerContent? Ticker
        {
            get => _ticker;
            set
            {
                if (value == _ticker)
                    return;

                _ticker = value;
                _tickerKey = TickerKey(value);
                if (_shown == Content.Ticker && Desired == Content.Ticker && _tickerKey == _shownTickerKey)
                {
                    // New counts or time: the items keep scrolling from where they are.
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
                    Render(force: true); // the end ellipsis depends on the width

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

        internal bool IsAnimating => IsSwitching || _resizing || Size != TargetSize(_shown) || (_shown != Content.None && _alpha < 1);

        internal bool IsClockRunning => _clock.IsRunning;

        private Content Desired =>
            _ticker != null ? Content.Ticker
            : _title.Length > 0 ? Content.Title
            : Content.None;

        /// <summary>Something else has to be shown: the other content, or another ticker.</summary>
        private bool IsSwitching => _shown != Desired || (_shown == Content.Ticker && _shownTickerKey != _tickerKey);

        /// <summary>
        /// Which ticker it is: the badge and the items, without counts and time. A ticker with another key is another vote
        /// (or its options have just been found) and cross-fades; one with the same key only changes in place.
        /// </summary>
        private static string? TickerKey(TickerContent? ticker) =>
            ticker == null
                ? null
                : string.Join("\n", new[]{ ticker.Icon.ToString(), ticker.Label, ticker.Lead ?? "", ticker.Hint ?? "" }
                    .Concat(ticker.Items.Select(i => $"{i.Number} {i.Text}")));

        /// <summary>The look, for this display's DPI and the form behind the strip.</summary>
        private OverlayStyle Style => _style ??= new OverlayStyle(DeviceDpi, Parent?.BackColor ?? SystemColors.Control);

        /// <summary>Where the items scroll: right of the standing part.</summary>
        private int ScrollLeft => _shown == Content.Ticker ? _standing?.Width ?? 0 : 0;

        private int ScrollWidth => Math.Max(0, Width - ScrollLeft);

        /// <summary>The items and the gap before they start again.</summary>
        private int LoopLength => (_body?.Width ?? 0) + Math.Max(Style.Scale(50), ScrollWidth / 3);

        /// <summary>The width the title needs: the badge with the play icon and the text.</summary>
        internal int TitleWidth(string title) =>
            Style.BadgeWidth(OverlayStyle.PlayGlyph, null) + Style.Padding + OverlayStyle.Measure(title, Style.Strong) + Style.Padding;

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
            if (IsSwitching)
            {
                // The old content fades out first; at zero the new one takes its place (still invisible).
                _alpha = Math.Max(0, _alpha - fadeStep);
                if (_alpha <= 0)
                {
                    _shown = desired;
                    _shownTickerKey = desired == Content.Ticker ? _tickerKey : null;
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

            // Once the items have run through completely, they start over one loop further on.
            var loop = LoopLength;
            while (loop > 0 && ScrollWidth - _scrollOffset <= -loop)
                _scrollOffset -= loop;
        }

        private Size TargetSize(Content content) => content switch
        {
            Content.Ticker => new Size(_availableWidth, Style.Height),
            Content.Title => new Size(Math.Min(_availableWidth, TitleWidth(_title)), Style.Height),
            _ => new Size(0, Height)
        };

        private void ApplySize(Size size) =>
            SetBounds(_bottomLeft.X, Math.Max(0, _bottomLeft.Y - size.Height), size.Width, size.Height);

        /// <summary>
        /// Renders what is shown once, on the box colour, so a frame only has to copy it: the parts whose content did not
        /// change are kept (unless <paramref name="force"/>).
        /// </summary>
        private void Render(bool force = false)
        {
            var style = Style;
            string? standingKey = null, bodyKey = null;
            Func<Bitmap?>? standing = null, body = null;

            if (_shown == Content.Title)
            {
                var badge = style.BadgeWidth(OverlayStyle.PlayGlyph, null);
                var textWidth = Math.Max(0, Math.Min(_availableWidth, TitleWidth(_title)) - badge);
                standingKey = "title";
                standing = () => RenderBitmap(badge, g => style.DrawBadge(g, 0, OverlayStyle.PlayGlyph, null));
                bodyKey = $"{_title}|{textWidth}";
                body = () => RenderBitmap(textWidth, g =>
                    style.DrawText(g, _title, style.Strong, style.Padding, Color.White, style.Box, textWidth - 2 * style.Padding, ellipsis: true));
            }
            else if (_shown == Content.Ticker && _ticker is { } ticker)
            {
                standingKey = ticker.Icon + ticker.StandingText;
                standing = () =>
                {
                    var width = style.BadgeWidth(ticker.Icon, ticker.Label) + (ticker.Time == null ? 0 : style.TimeWidth(ticker.Time));
                    return RenderBitmap(width, g =>
                    {
                        var x = style.DrawBadge(g, 0, ticker.Icon, ticker.Label);
                        if (ticker.Time != null)
                            style.DrawTime(g, x, ticker.Time);
                    });
                };
                bodyKey = string.Join("|", ticker.Items.Select(i => i.ToString())) + "|" + ticker.Lead + "|" + ticker.Hint;
                body = () => RenderBitmap(BodyWidth(style, ticker), g => DrawBody(g, style, ticker));
            }

            if (force || standingKey != _standingKey)
            {
                _standing?.Dispose();
                _standing = standing?.Invoke();
                _standingKey = standingKey;
            }

            if (force || bodyKey != _bodyKey)
            {
                _body?.Dispose();
                _body = body?.Invoke();
                _bodyKey = bodyKey;
            }
        }

        private static int BodyWidth(OverlayStyle style, TickerContent ticker)
        {
            var space = style.Scale(14);
            var width = ticker.Items.Sum(style.ItemWidth);
            if (ticker.Lead != null)
                width += space + OverlayStyle.Measure(ticker.Lead, style.Text) + space;
            if (ticker.Hint != null)
                width += space + OverlayStyle.Measure(ticker.Hint, style.Text) + space;
            return width;
        }

        private static void DrawBody(Graphics g, OverlayStyle style, TickerContent ticker)
        {
            var space = style.Scale(14);
            var x = 0;
            if (ticker.Lead != null)
            {
                style.DrawText(g, ticker.Lead, style.Text, x + space, style.Muted, style.Box);
                x += space + OverlayStyle.Measure(ticker.Lead, style.Text) + space;
            }

            foreach (var item in ticker.Items)
                x += style.DrawItem(g, x, item);

            if (ticker.Hint != null)
                style.DrawText(g, ticker.Hint, style.Text, x + space, style.Muted, style.Box);
        }

        private Bitmap? RenderBitmap(int width, Action<Graphics> draw) => Style.Render(width, Style.Height, draw);

        /// <summary>A new look (another form colour or DPI): everything is rendered again.</summary>
        private void ResetStyle()
        {
            _style?.Dispose();
            _style = null;
            Render(force: true);
            if (!IsAnimating)
                ApplySize(TargetSize(_shown));
            Invalidate();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            ResetStyle(); // the box colour depends on the form
        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            base.OnParentBackColorChanged(e);
            ResetStyle();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ResetStyle();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var box = new SolidBrush(Style.Box))
                g.FillRectangle(box, ClientRectangle);

            if (_alpha <= 0)
                return;

            if (_shown == Content.Ticker)
            {
                // The items scroll from the right edge to the standing part, where they disappear.
                if (_body is { } body)
                {
                    var loop = LoopLength;
                    var clip = g.Clip;
                    g.SetClip(new Rectangle(ScrollLeft, 0, ScrollWidth, Height));
                    for (var x = ScrollLeft + ScrollWidth - _scrollOffset; x < Width && loop > 0; x += loop)
                    {
                        if (x + body.Width > ScrollLeft)
                            Draw(g, body, x);
                    }

                    g.Clip = clip;
                }

                if (_standing is { } standing)
                    Draw(g, standing, 0);
            }
            else
            {
                if (_standing is { } standing)
                    Draw(g, standing, 0);
                if (_body is { } body)
                    Draw(g, body, _standing?.Width ?? 0);
            }

            base.OnPaint(e);
        }

        private void Draw(Graphics g, Bitmap bitmap, int x)
        {
            // Pixel rectangles on both sides: the bitmap's own DPI must not scale it.
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
                _standing?.Dispose();
                _standing = null;
                _body?.Dispose();
                _body = null;
                _style?.Dispose();
                _style = null;
            }

            base.Dispose(disposing);
        }
    }
}
