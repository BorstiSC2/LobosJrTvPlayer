using System;
using System.ComponentModel;
using System.Drawing;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// A short status over the video ("Buffering", "Connection lost - resuming at 50:31", "Waiting for vote to finish...
    /// 1:20", "Volume 55%") in the overlay style: a translucent pill with an orange circle and an icon at its left, then
    /// the text, optionally with animated dots.
    /// </summary>
    /// <remarks>
    /// A <see cref="LayeredOverlay"/>. It sizes itself to its text (the form places it). The width always leaves room for
    /// three dots, so the animation does not make it twitch.
    /// </remarks>
    internal sealed class OverlayPill : LayeredOverlay
    {
        private static readonly string[] DotFrames = { "...", ".", "..", "...", "" };

        private char _icon;
        private bool _animated;
        private int _frame;

        public OverlayPill(char icon, bool layered = true)
            : base(layered)
        {
            _icon = icon;
            UpdateSize();
        }

        /// <summary>The circle's icon (an <see cref="OverlayStyle"/> glyph).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public char Icon
        {
            get => _icon;
            set
            {
                if (value == _icon)
                    return;

                _icon = value;
                Present();
            }
        }

        /// <summary>Whether dots after the text count up (see <see cref="NextFrame"/>).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Animated
        {
            get => _animated;
            set
            {
                _animated = value;
                _frame = 0;
                UpdateSize();
                Present();
            }
        }

        /// <summary>The text with its dots as it is drawn now.</summary>
        internal string ShownText => _animated ? Text + " " + DotFrames[_frame] : Text;

        /// <summary>The next step of the dots: "...", ".", "..", "...", "" and again from ".".</summary>
        public void NextFrame()
        {
            if (!_animated)
                return;

            _frame = _frame == DotFrames.Length - 1 ? 1 : _frame + 1;
            Present();
        }

        /// <summary>Starts the dots again from "...".</summary>
        public void ResetFrame()
        {
            _frame = 0;
            Present();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            UpdateSize();
            Present();
        }

        private void UpdateSize()
        {
            var style = Style;
            var height = style.CompactHeight;
            var text = _animated ? Text + " ..." : Text;
            Size = new Size(style.TextLeft(height) + OverlayStyle.Measure(text, style.Strong) + style.TextRight(height), height);
        }

        private protected override void OnStyleChanged() => UpdateSize();

        private protected override void DrawFrame(Graphics g)
        {
            var style = Style;
            var height = Height;
            OverlayStyle.FillPill(g, new RectangleF(0, 0, Width, height));
            var circle = style.Circle(height);
            OverlayStyle.FillCircle(g, circle);
            OverlayStyle.DrawGlyph(g, _icon, style.Icon, circle);

            var left = style.TextLeft(height);
            OverlayStyle.DrawText(g, ShownText, style.Strong, Color.White,
                new RectangleF(left, 0, Math.Max(0, Width - left - style.TextRight(height) / 2), height));
        }
    }
}
