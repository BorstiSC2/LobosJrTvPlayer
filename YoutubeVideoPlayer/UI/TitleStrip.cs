using System;
using System.ComponentModel;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The strip at the bottom left of the video, a pill like the "now playing" card: a status text ("Loading next
    /// video...") next to an orange circle with the play icon, or during a chat vote a ticker: the vote's icon in the
    /// circle, the options scrolling from right to left in the top row, and below them, standing still, "Vote · !vote 1–3 ·
    /// 1:20". Switching between the two cross-fades inside the pill: the old text fades out, the pill resizes, the
    /// new text fades in. Appearing and disappearing, the whole pill fades in or out with its text, at its size. Another
    /// ticker (another vote, or a vote whose options have just been found) cross-fades the
    /// same way; new counts or a new time in the same ticker do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="LayeredOverlay"/>: see-through, with smooth round ends. Each text is rendered once into a bitmap when it
    /// changes; a frame only composes them. Frames come from a <see cref="FrameClock"/> (one per display refresh), which
    /// runs only while an animation runs or the ticker scrolls.
    /// </para>
    /// <para>
    /// The ticker moves a whole number of pixels per refresh (<see cref="PixelsPerFrame"/>), so every frame moves it by the
    /// same distance; a refresh the UI thread missed is made up in the next frame. Fades and the resize follow elapsed time.
    /// </para>
    /// </remarks>
    public sealed class TitleStrip : LayeredOverlay
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

        private string _title = string.Empty;
        private AnimatedAvatar? _titleAvatar;
        private AnimatedAvatar? _tickerAvatar;
        private TickerContent? _ticker;
        private string? _tickerKey;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Content _shown = Content.None;
        private string? _shownTickerKey;

        // The standing part (the caption row of the ticker: "Vote · !vote 1–3 · 1:20") and the body (the status text, or
        // the items that scroll), each rendered once on transparent; the keys say what they show, so an unchanged part is
        // not rendered again.
        private Bitmap? _standing;
        private Bitmap? _body;
        private string? _standingKey;
        private string? _bodyKey;
        private double _alpha;     // the text (and the circle's icon)
        private double _boxAlpha;  // the pill and its circle: 0 while nothing is shown
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
            : this(timeProvider, layered: true)
        {
        }

        /// <param name="timeProvider">The clock for the animation; <see cref="TimeProvider.System"/> by default.</param>
        /// <param name="layered"><c>false</c> makes a normal window, as where Windows does not allow a layered one.</param>
        internal TitleStrip(TimeProvider? timeProvider, bool layered)
            : base(layered, timeProvider)
        {
            _time = timeProvider ?? TimeProvider.System;
            Size = new Size(0, Style.PillHeight);

            _clock = new FrameClock(refreshes =>
            {
                Advance(refreshes);
                if (!IsLayered)
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
                    Present();
                }

                Retarget();
            }
        }

        /// <summary>The picture next to the status text; <c>null</c> shows the play icon.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal AnimatedAvatar? TitleAvatar
        {
            get => _titleAvatar;
            set
            {
                _titleAvatar = value;
                UpdateShownAvatar();
            }
        }

        /// <summary>The picture of the vote ticker; <c>null</c> shows the vote's icon.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal AnimatedAvatar? TickerAvatar
        {
            get => _tickerAvatar;
            set
            {
                _tickerAvatar = value;
                UpdateShownAvatar();
            }
        }

        /// <summary>
        /// The overlay's <see cref="LayeredOverlay.Avatar"/> is the picture of what is shown: it changes together with the
        /// content (while the text is faded out), not before.
        /// </summary>
        private void UpdateShownAvatar()
        {
            if (_shown == Content.Ticker)
                Avatar = _tickerAvatar;
            else if (_shown == Content.Title)
                Avatar = _titleAvatar;
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
                    Present();
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
                Present();
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

        /// <summary>How visible the pill itself is: 0 to 1. It only fades when the strip appears or disappears.</summary>
        internal double BoxAlpha => _boxAlpha;

        /// <summary>How far the ticker has scrolled since its text entered from the right (wraps after each loop).</summary>
        internal int ScrollOffset => _scrollOffset;

        internal bool IsAnimating =>
            IsSwitching || _resizing || Size != TargetSize(_shown) || (_shown != Content.None && (_alpha < 1 || _boxAlpha < 1));

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

        private int PillHeight => Style.PillHeight;

        private int TextLeft => Style.TextLeft(PillHeight);

        private int TextRight => Style.TextRight(PillHeight);

        /// <summary>Where the items scroll: from right of the circle to just before the round end.</summary>
        private int ScrollLeft => TextLeft;

        private int ScrollWidth => Math.Max(0, Width - Style.Scale(16) - ScrollLeft);

        /// <summary>The items and the gap before they start again.</summary>
        private int LoopLength => (_body?.Width ?? 0) + Math.Max(Style.Scale(50), ScrollWidth / 3);

        /// <summary>The rows of the ticker: the items, and the caption below them; together centred in the pill.</summary>
        private (int Top, int Height, int CaptionTop, int CaptionHeight) Rows
        {
            get
            {
                var top = Style.Title.Height;
                var caption = Style.Caption.Height;
                var y = (PillHeight - top - caption) / 2;
                return (y, top, y + top, caption);
            }
        }

        /// <summary>The width the status text needs: the circle and the text.</summary>
        internal int TitleWidth(string title) => TextLeft + OverlayStyle.Measure(title, Style.Title) + TextRight;

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
            if (IsSwitching && desired == Content.None)
            {
                // Hiding: the pill fades out together with its text; both reach zero at the same moment, in place.
                _resizing = false;
                var box = Math.Max(0, _boxAlpha - fadeStep);
                _alpha = _boxAlpha > 0 ? _alpha * box / _boxAlpha : 0;
                _boxAlpha = box;
                if (_boxAlpha <= 0)
                {
                    _alpha = 0;
                    _shown = Content.None;
                    _shownTickerKey = null;
                    Render();
                    ApplySize(TargetSize(Content.None));
                }
            }
            else if (IsSwitching)
            {
                // The old content fades out first; at zero the new one takes its place (still invisible).
                _alpha = Math.Max(0, _alpha - fadeStep);
                if (_alpha <= 0)
                {
                    var appearing = _shown == Content.None || _boxAlpha <= 0;
                    _shown = desired;
                    UpdateShownAvatar();
                    _shownTickerKey = desired == Content.Ticker ? _tickerKey : null;
                    _scrollOffset = 0;
                    _frameDebt = 0;
                    Render();
                    if (appearing)
                    {
                        // Nothing was shown: the pill takes its size at once and fades in with its text.
                        _resizing = false;
                        ApplySize(TargetSize(desired));
                    }
                }
            }
            else
            {
                var target = TargetSize(_shown);
                if (Size != target || _resizing)
                {
                    ResizeTowards(target, now);
                }
                else if (_shown != Content.None && (_alpha < 1 || _boxAlpha < 1))
                {
                    _alpha = Math.Min(1, _alpha + fadeStep);
                    _boxAlpha = Math.Min(1, _boxAlpha + fadeStep);
                }
            }

            if (_shown == Content.Ticker)
                Scroll(refreshes ?? RefreshesIn(seconds));

            if (_shown == Content.None && desired == Content.None && Width == 0)
                Visible = false;

            Present();
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
            Content.Ticker => new Size(_availableWidth, PillHeight),
            Content.Title => new Size(Math.Min(_availableWidth, TitleWidth(_title)), PillHeight),
            _ => new Size(0, Height)
        };

        private void ApplySize(Size size) =>
            SetBounds(_bottomLeft.X, Math.Max(0, _bottomLeft.Y - size.Height), size.Width, size.Height);

        /// <summary>
        /// Renders what is shown once, on transparent, so a frame only has to compose it: the parts whose content did not
        /// change are kept (unless <paramref name="force"/>).
        /// </summary>
        private void Render(bool force = false)
        {
            var style = Style;
            string? standingKey = null, bodyKey = null;
            Func<Bitmap?>? standing = null, body = null;

            if (_shown == Content.Title)
            {
                var textWidth = Math.Max(0, Math.Min(_availableWidth, TitleWidth(_title)) - TextLeft - TextRight);
                bodyKey = $"{_title}|{textWidth}";
                body = () => RenderBitmap(textWidth, PillHeight, g =>
                    OverlayStyle.DrawText(g, _title, style.Title, Color.White, new RectangleF(0, 0, textWidth, PillHeight), ellipsis: true));
            }
            else if (_shown == Content.Ticker && _ticker is { } ticker)
            {
                var rows = Rows;
                var caption = Caption(ticker);
                var captionWidth = Math.Max(0, _availableWidth - TextLeft - TextRight);
                standingKey = $"{caption.Label}|{caption.Details}|{captionWidth}";
                standing = () => RenderBitmap(captionWidth, rows.CaptionHeight, g =>
                {
                    var label = caption.Details.Length == 0 ? caption.Label : caption.Label + " · ";
                    var x = OverlayStyle.Measure(label, style.Caption);
                    var row = new RectangleF(0, 0, captionWidth, rows.CaptionHeight);
                    OverlayStyle.DrawText(g, label, style.Caption, OverlayStyle.AccentSoft, row, ellipsis: true);
                    OverlayStyle.DrawText(g, caption.Details, style.Caption, OverlayStyle.Muted,
                        row with { X = x, Width = Math.Max(0, captionWidth - x) }, ellipsis: true);
                });
                bodyKey = string.Join("|", ticker.Items.Select(i => i.ToString())) + "|" + ticker.Lead;
                body = () => RenderBitmap(BodyWidth(style, ticker), rows.Height, g => DrawBody(g, style, ticker, rows.Height));
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

        /// <summary>
        /// The caption row: the vote's word ("Vote", "Skip this video?") and, muted, what to type and the time left. The time
        /// is at the end: it may only be known a few seconds after the ticker appears, and must not push the rest aside.
        /// </summary>
        private static (string Label, string Details) Caption(TickerContent ticker) =>
            (ticker.Label, string.Join(" · ", new[] { ticker.Hint, ticker.Time }.Where(part => !string.IsNullOrEmpty(part))));

        private static int BodyWidth(OverlayStyle style, TickerContent ticker)
        {
            var space = style.Scale(14);
            var width = ticker.Items.Sum(style.ItemWidth);
            if (ticker.Lead != null)
                width += space + OverlayStyle.Measure(ticker.Lead, style.Text) + space;
            return width;
        }

        private static void DrawBody(Graphics g, OverlayStyle style, TickerContent ticker, int height)
        {
            var space = style.Scale(14);
            var x = 0;
            if (ticker.Lead != null)
            {
                var width = OverlayStyle.Measure(ticker.Lead, style.Text);
                OverlayStyle.DrawText(g, ticker.Lead, style.Text, OverlayStyle.Muted, new RectangleF(x + space, 0, width + 1, height));
                x += space + width + space;
            }

            foreach (var item in ticker.Items)
                x += style.DrawItem(g, x, 0, height, item);
        }

        private static Bitmap? RenderBitmap(int width, int height, Action<Graphics> draw)
        {
            if (width <= 0 || height <= 0)
                return null;

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                draw(g);
            }

            return bitmap;
        }

        /// <summary>A new DPI: everything is rendered again.</summary>
        private protected override void OnStyleChanged()
        {
            Render(force: true);
            if (!IsAnimating)
                ApplySize(TargetSize(_shown));
        }

        private protected override void DrawFrame(Graphics g)
        {
            if (_boxAlpha <= 0)
                return;

            var style = Style;
            var height = Height;
            OverlayStyle.FillPill(g, new RectangleF(0, 0, Width, height), _boxAlpha);
            var circle = style.Circle(height);
            OverlayStyle.FillCircle(g, circle, _boxAlpha);

            if (_alpha <= 0)
                return;

            if (_shown == Content.Ticker && _ticker is { } ticker)
            {
                // The vote's avatar stays while the ticker cross-fades; only an icon fades with the text.
                if (Avatar is { } avatar)
                    style.DrawAvatar(g, circle, avatar, AvatarFrame);
                else
                    OverlayStyle.DrawGlyph(g, ticker.Icon, style.LargeIcon, circle, _alpha);
                var rows = Rows;

                // The items scroll from the right end to the circle, where they disappear.
                if (_body is { } body)
                {
                    var loop = LoopLength;
                    g.SetClip(new Rectangle(ScrollLeft, 0, ScrollWidth, height));
                    for (var x = ScrollLeft + ScrollWidth - _scrollOffset; x < ScrollLeft + ScrollWidth && loop > 0; x += loop)
                    {
                        if (x + body.Width > ScrollLeft)
                            Draw(g, body, x, rows.Top);
                    }

                    g.ResetClip();
                }

                if (_standing is { } standing)
                {
                    g.SetClip(new Rectangle(0, 0, Math.Max(0, Width - TextRight / 2), height));
                    Draw(g, standing, TextLeft, rows.CaptionTop);
                    g.ResetClip();
                }
            }
            else if (_shown == Content.Title)
            {
                if (Avatar is { } avatar)
                    style.DrawAvatar(g, circle, avatar, AvatarFrame);
                else
                    style.DrawPlay(g, circle, _alpha);
                if (_body is { } body)
                {
                    g.SetClip(new Rectangle(0, 0, Math.Max(0, Width - TextRight / 2), height));
                    Draw(g, body, TextLeft, 0);
                    g.ResetClip();
                }
            }
        }

        private void Draw(Graphics g, Bitmap bitmap, int x, int y)
        {
            // Pixel rectangles on both sides: the bitmap's own DPI must not scale it.
            var target = new Rectangle(x, y, bitmap.Width, bitmap.Height);
            if (_alpha >= 1)
            {
                g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
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
            }

            base.Dispose(disposing);
        }
    }
}
