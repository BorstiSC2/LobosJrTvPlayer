using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>What the "now playing" card shows.</summary>
    public sealed record NowPlayingCardContent(string Title, TimeSpan Position, TimeSpan? Length);

    /// <summary>
    /// The "now playing" card at the bottom left of the video, a pill: at its left an orange circle with the play icon and
    /// a ring that fills with the position, at its right the title and, below it, "Now playing · 12:04 / 45:10". It
    /// opens in place: the circle pops up, the pill grows out of it to the right, the text fades in. It closes by fading
    /// out as a whole (pill, circle and text together). When it is due is up to <see cref="NowPlayingSchedule"/> and the
    /// form.
    /// </summary>
    /// <remarks>
    /// A <see cref="LayeredOverlay"/>: see-through, with smooth round ends. The window always has the full size; the
    /// animation is drawn into it. The text is rendered once per content (about once a second while it is up); a frame
    /// only composes the pill, the circle and the text. Frames come from a <see cref="FrameClock"/>, which runs only while
    /// it moves.
    /// </remarks>
    public sealed class NowPlayingCard : LayeredOverlay
    {
        internal static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(550);
        internal static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(250);

        internal const string Label = "Now playing";

        // The phases of the opening (0 = nothing, 1 = open). Closing fades the card as it is (see _opacity).
        private const double RingEnd = 0.35;     // the circle pops up (0 .. RingEnd)
        private const double GrowStart = 0.25;   // the pill grows to the right (GrowStart .. GrowEnd)
        private const double GrowEnd = 0.8;
        private const double TextStart = 0.7;    // the text fades in (TextStart .. 1)

        private readonly TimeProvider _time;
        private readonly FrameClock _clock;

        private NowPlayingCardContent? _content;
        private bool _showing;
        private int _availableWidth = 100;
        private Point _bottomLeft;

        private Bitmap? _text;       // title and second row, on transparent
        private string? _textKey;

        private double _progress;      // how far it has opened; it stays while the card fades out
        private double _opacity = 1;   // the whole card: falls to 0 while it closes, then the opening starts over
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
            : base(layered)
        {
            _time = timeProvider ?? TimeProvider.System;
            _clock = new FrameClock(_ =>
            {
                Advance();
                if (!IsLayered)
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

        /// <summary>How far it has opened: 0 to 1. Closing does not change it; the card fades out as it is.</summary>
        internal double Progress => _progress;

        /// <summary>How visible the whole card is: 1, and falling to 0 while it closes.</summary>
        internal double Opacity => _opacity;

        internal bool IsAnimating => Target > 0 ? _progress < 1 || _opacity < 1 : _progress > 0;

        internal bool IsClockRunning => _clock.IsRunning;

        /// <summary>How wide the pill is drawn right now: the circle's width when it starts, <see cref="CardWidth"/> once open.</summary>
        internal int PillWidth => (int)Math.Round(CardHeight + (CardWidth - CardHeight) * Grow(_progress));

        /// <summary>What the card shows, as one line (tests).</summary>
        internal string PlainText => _content == null ? string.Empty : $"{Label} · {TimeText(_content)} | {_content.Title}";

        private double Target => _showing && _content != null ? 1 : 0;

        internal int CardHeight => Style.PillHeight;

        private int TextLeft => Style.TextLeft(CardHeight);

        private int TextRight => Style.TextRight(CardHeight);

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
                var text = Math.Max(OverlayStyle.Measure(content.Title, style.Title), OverlayStyle.Measure(SecondRow(time), style.Caption));
                return Math.Min(_availableWidth, Math.Max(minimum, TextLeft + text + TextRight));
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
            var fade = seconds / CloseDuration.TotalSeconds;
            if (target > 0)
            {
                // Opening, or coming back while it faded out: it continues from where it is.
                _progress = Math.Min(1, _progress + seconds / OpenDuration.TotalSeconds);
                _opacity = Math.Min(1, _opacity + fade);
            }
            else
            {
                // Closing: everything fades out together; once it is gone, the next opening starts from the circle.
                _opacity = Math.Max(0, _opacity - fade);
                if (_opacity <= 0)
                {
                    _progress = 0;
                    _opacity = 1;
                }
            }

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

        /// <summary>The title and the second row, on transparent. Only again when it would look different.</summary>
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
            {
                g.Clear(Color.Transparent);
                OverlayStyle.DrawText(g, content.Title, style.Title, Color.White, new RectangleF(0, top, width, titleHeight), ellipsis: true);

                var label = Label + " · ";
                var x = OverlayStyle.Measure(label, style.Caption);
                var row = new RectangleF(0, top + titleHeight, width, rowHeight);
                OverlayStyle.DrawText(g, label, style.Caption, OverlayStyle.AccentSoft, row);
                OverlayStyle.DrawText(g, time, style.Caption, OverlayStyle.Muted, row with { X = x, Width = Math.Max(0, width - x) }, ellipsis: true);
            }

            _text?.Dispose();
            _text = bitmap;
            _textKey = key;
        }

        private protected override void OnStyleChanged()
        {
            _textKey = null;
            ApplyBounds();
            RenderText();
        }

        private protected override void DrawFrame(Graphics g)
        {
            if (_progress <= 0 || _content is not { } content)
                return;

            var style = Style;
            var height = CardHeight;
            var pop = Phase(_progress, 0, RingEnd);
            var scale = (float)(0.4 + 0.6 * EaseOutBack(pop));
            var alpha = Math.Clamp(pop * 2.5, 0, 1) * _opacity;
            var pill = PillWidth;

            // The pill and the circle grow out of the circle's middle.
            var state = g.Save();
            var centre = height / 2f;
            g.TranslateTransform(centre, centre);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-centre, -centre);

            OverlayStyle.FillPill(g, new RectangleF(0, 0, pill, height), alpha);
            var circle = style.Circle(height);
            OverlayStyle.FillCircle(g, circle, alpha);
            var share = Share(content);
            if (share >= 0)
                style.DrawRing(g, circle, share, alpha);
            style.DrawPlay(g, circle, alpha);

            g.Restore(state);

            var text = Phase(_progress, TextStart, 1);
            var textAlpha = text * _opacity;
            if (textAlpha > 0 && _text is { } bitmap)
            {
                var rise = (int)Math.Round(style.Scale(4) * (1 - EaseOutCubic(text)));
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)textAlpha });
                g.SetClip(new Rectangle(0, 0, Math.Max(0, pill - TextRight / 2), height));
                g.DrawImage(bitmap, new Rectangle(TextLeft, rise, bitmap.Width, bitmap.Height),
                    0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
                g.ResetClip();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                _text?.Dispose();
                _text = null;
            }

            base.Dispose(disposing);
        }
    }
}
