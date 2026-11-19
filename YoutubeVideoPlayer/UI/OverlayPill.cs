using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// A status in the middle of the video ("Buffering", "Connection lost - resuming at 50:31", "Waiting for vote to
    /// finish... 1:20") in the overlay style: an orange badge with an icon, then the text, optionally with animated dots.
    /// </summary>
    /// <remarks>
    /// It sizes itself to its text (the form centres it). The width always leaves room for three dots, so the animation
    /// does not make it twitch.
    /// </remarks>
    internal sealed class OverlayPill : Control
    {
        private static readonly string[] DotFrames = { "...", ".", "..", "...", "" };

        private OverlayStyle? _style;
        private char _icon;
        private bool _animated;
        private int _frame;

        public OverlayPill(char icon)
        {
            _icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Visible = false;
            UpdateSize();
        }

        /// <summary>The badge's icon (an <see cref="OverlayStyle"/> glyph).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public char Icon
        {
            get => _icon;
            set
            {
                if (value == _icon)
                    return;

                _icon = value;
                UpdateSize();
                Invalidate();
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
                Invalidate();
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
            Invalidate();
        }

        /// <summary>Starts the dots again from "...".</summary>
        public void ResetFrame()
        {
            _frame = 0;
            Invalidate();
        }

        private OverlayStyle Style => _style ??= new OverlayStyle(DeviceDpi, Parent?.BackColor ?? SystemColors.Control);

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            UpdateSize();
            Invalidate();
        }

        private void UpdateSize()
        {
            var style = Style;
            var text = _animated ? Text + " ..." : Text;
            Size = new Size(
                style.BadgeWidth(_icon, null) + style.Padding + OverlayStyle.Measure(text, style.Strong) + style.Padding,
                style.Height);
        }

        private void ResetStyle()
        {
            _style?.Dispose();
            _style = null;
            UpdateSize();
            Invalidate();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            ResetStyle();
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
            var style = Style;
            var g = e.Graphics;
            using (var box = new SolidBrush(style.Box))
                g.FillRectangle(box, ClientRectangle);

            var x = style.DrawBadge(g, 0, _icon, null);
            style.DrawText(g, ShownText, style.Strong, x + style.Padding, Color.White, style.Box);
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _style?.Dispose();
                _style = null;
            }

            base.Dispose(disposing);
        }
    }
}
