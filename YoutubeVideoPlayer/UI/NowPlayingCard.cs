using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>What the "now playing" card shows.</summary>
    public sealed record NowPlayingCardContent(string Title, TimeSpan Position, TimeSpan? Length);

    /// <summary>
    /// The "now playing" card at the bottom left of the video: a "NOW PLAYING" badge with the position, the title, and a
    /// thin progress bar along the bottom. It slides in from the left edge and back out (when it is due is up to
    /// <see cref="NowPlayingSchedule"/> and the form).
    /// </summary>
    /// <remarks>
    /// Like <see cref="TitleStrip"/> it is a solid box (a child window cannot be see-through over VLC's video window), so
    /// only its position is animated. The card is rendered into one bitmap when its content changes (about once a second
    /// while it is up); a frame only copies it. Frames come from a <see cref="FrameClock"/>, which runs only while it moves.
    /// </remarks>
    public sealed class NowPlayingCard : Control
    {
        internal static readonly TimeSpan SlideInDuration = TimeSpan.FromMilliseconds(350);
        internal static readonly TimeSpan SlideOutDuration = TimeSpan.FromMilliseconds(250);

        internal const string Label = "NOW PLAYING";

        private readonly TimeProvider _time;
        private readonly FrameClock _clock;

        private OverlayStyle? _style;
        private NowPlayingCardContent? _content;
        private bool _showing;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Bitmap? _bitmap;
        private string? _bitmapKey;

        // 0 = fully out (left of the form), 1 = in place. The position follows one easing curve of it in both directions,
        // so a slide that turns around midway continues from where the card is.
        private double _progress;
        private long _lastTick;

        /// <param name="timeProvider">The clock for the slide; <see cref="TimeProvider.System"/> by default.</param>
        public NowPlayingCard(TimeProvider? timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Visible = false;

            _clock = new FrameClock(_ =>
            {
                Advance();
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
                Render();
                ApplyBounds(); // a new title may change the width
                Invalidate();
                Retarget();
            }
        }

        /// <summary>Whether the card should be up; it slides to that state.</summary>
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
                Render();
                ApplyBounds();
                Invalidate();
            }
        }

        /// <summary>Where the card's bottom-left corner is once it is in place.</summary>
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

        /// <summary>0 (out) to 1 (in place).</summary>
        internal double Progress => _progress;

        internal bool IsAnimating => _progress != Target;

        internal bool IsClockRunning => _clock.IsRunning;

        /// <summary>What the card shows, as one line (tests).</summary>
        internal string PlainText => _content == null ? string.Empty : $"{Label} {TimeText(_content)} | {_content.Title}";

        private double Target => _showing && _content != null ? 1 : 0;

        private OverlayStyle Style => _style ??= new OverlayStyle(DeviceDpi, Parent?.BackColor ?? SystemColors.Control);

        private int HeaderHeight => Style.Height;

        private int TitleHeight => Style.Scale(40);

        private int BarHeight => Math.Max(2, Style.Scale(4));

        private int CardHeight => HeaderHeight + TitleHeight + BarHeight;

        /// <summary>The width of the card for its title: at least <c>320</c> px (scaled), at most <see cref="AvailableWidth"/>.</summary>
        internal int CardWidth
        {
            get
            {
                if (_content is not { } content)
                    return Math.Min(_availableWidth, Style.Scale(320));

                var style = Style;
                // The time is measured with zeros, so the width does not change every second.
                var time = new string(TimeText(content).Select(c => char.IsDigit(c) ? '0' : c).ToArray());
                var header = style.BadgeWidth(OverlayStyle.PlayGlyph, Label) + style.Padding + OverlayStyle.Measure(time, style.Strong) + style.Padding;
                var title = style.Padding + OverlayStyle.Measure(content.Title, style.Title) + style.Padding;
                return Math.Min(_availableWidth, Math.Max(style.Scale(320), Math.Max(header, title)));
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

        private static double Share(NowPlayingCardContent content) =>
            content.Length is { } length && length > TimeSpan.Zero
                ? Math.Clamp(content.Position.TotalSeconds / length.TotalSeconds, 0, 1)
                : -1;

        /// <summary>
        /// Moves the slide to the current time. The frame clock calls it once per refresh; tests call it themselves.
        /// </summary>
        internal void Advance()
        {
            var now = _time.GetTimestamp();
            var seconds = Math.Max(0, _time.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;

            var target = Target;
            _progress = target > _progress
                ? Math.Min(target, _progress + seconds / SlideInDuration.TotalSeconds)
                : Math.Max(target, _progress - seconds / SlideOutDuration.TotalSeconds);

            ApplyBounds();
            if (_progress <= 0 && target <= 0)
                Visible = false;

            if (!IsAnimating)
                _clock.Stop();
        }

        private void Retarget()
        {
            if (Target > 0)
                Visible = true;

            if (IsAnimating && !_clock.IsRunning)
            {
                _lastTick = _time.GetTimestamp();
                _clock.Start();
            }
        }

        private void ApplyBounds()
        {
            var width = CardWidth;
            var height = CardHeight;
            var eased = 1 - Math.Pow(1 - _progress, 3); // decelerates into place, accelerates away
            var outside = -width;
            var x = (int)Math.Round(outside + (_bottomLeft.X - outside) * eased);
            SetBounds(x, Math.Max(0, _bottomLeft.Y - height), width, height);
        }

        /// <summary>Renders the card once, unless it shows the same as before.</summary>
        private void Render(bool force = false)
        {
            if (_content is not { } content)
            {
                _bitmap?.Dispose();
                _bitmap = null;
                _bitmapKey = null;
                return;
            }

            var style = Style;
            var width = CardWidth;
            var time = TimeText(content);
            var share = Share(content);
            var filled = share < 0 ? -1 : (int)Math.Round(width * share);
            var key = $"{content.Title}|{time}|{filled}|{width}";
            if (!force && key == _bitmapKey)
                return;

            _bitmap?.Dispose();
            _bitmap = style.Render(width, CardHeight, g =>
            {
                style.DrawBadge(g, 0, OverlayStyle.PlayGlyph, Label);
                style.DrawText(g, time, style.Strong, width - style.Padding - OverlayStyle.Measure(time, style.Strong), OverlayStyle.AccentSoft, style.Box);

                OverlayStyle.DrawText(g, content.Title, style.Title,
                    new Rectangle(style.Padding, HeaderHeight, Math.Max(0, width - 2 * style.Padding), TitleHeight),
                    Color.White, style.Box, ellipsis: true);

                if (filled >= 0)
                {
                    var bar = new Rectangle(0, CardHeight - BarHeight, width, BarHeight);
                    using (var track = new SolidBrush(style.Faint))
                        g.FillRectangle(track, bar);
                    using (var fill = new SolidBrush(OverlayStyle.Accent))
                        g.FillRectangle(fill, bar with { Width = filled });
                }
            });
            _bitmapKey = key;
        }

        /// <summary>A new look (another form colour or DPI): everything is rendered again.</summary>
        private void ResetStyle()
        {
            _style?.Dispose();
            _style = null;
            Render(force: true);
            ApplyBounds();
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
            if (_bitmap is { } bitmap)
            {
                // Opaque, in pixels on both sides: a plain copy, and the bitmap's own DPI must not scale it.
                var mode = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                g.CompositingMode = mode;
            }
            else
            {
                using var box = new SolidBrush(Style.Box);
                g.FillRectangle(box, ClientRectangle);
            }

            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                _bitmap?.Dispose();
                _bitmap = null;
                _style?.Dispose();
                _style = null;
            }

            base.Dispose(disposing);
        }
    }
}
