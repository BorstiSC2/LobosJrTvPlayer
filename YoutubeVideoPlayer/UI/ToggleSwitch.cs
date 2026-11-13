using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// A check box drawn as an on/off switch (a knob that slides in a rounded track), like the switches in the
    /// Windows settings. It is a real <see cref="CheckBox"/>, so <see cref="CheckBox.Checked"/>, the space key and
    /// accessibility work as usual. The switch grows with the font.
    /// </summary>
    public sealed class ToggleSwitch : CheckBox
    {
        private static readonly Color OnColor = Color.SeaGreen;
        private static readonly Color OffBorderColor = Color.FromArgb(110, 110, 110);
        private static readonly Color DisabledColor = Color.FromArgb(190, 190, 190);

        public ToggleSwitch()
        {
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
                true);
            AutoSize = true;
            Cursor = Cursors.Hand;
        }

        /// <summary>Track size: twice as wide as high, a bit higher than a line of text.</summary>
        private Size TrackSize
        {
            get
            {
                var height = Math.Max(16, Font.Height + 2);
                return new Size(height * 2, height);
            }
        }

        private int Gap => Font.Height / 2;

        public override Size GetPreferredSize(Size proposedSize)
        {
            var track = TrackSize;
            var text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font);
            return new Size(
                Padding.Horizontal + track.Width + (text.Width > 0 ? Gap + text.Width : 0),
                Padding.Vertical + Math.Max(track.Height, text.Height) + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(BackColor))
                g.FillRectangle(background, ClientRectangle);

            var client = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            var size = TrackSize;
            var track = new Rectangle(client.Left + 1, client.Top + (client.Height - size.Height) / 2, size.Width - 2, size.Height - 1);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var trackColor = Checked ? (Enabled ? OnColor : DisabledColor) : Color.White;
            var borderColor = Checked ? trackColor : Enabled ? OffBorderColor : DisabledColor;
            var knobColor = Checked ? Color.White : borderColor;
            using (var path = Pill(track))
            using (var fill = new SolidBrush(trackColor))
            using (var border = new Pen(borderColor, 1.5f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            var inset = Math.Max(3, track.Height / 5);
            var diameter = track.Height - 2 * inset;
            var knobX = Checked ? track.Right - inset - diameter : track.Left + inset;
            using (var knob = new SolidBrush(knobColor))
                g.FillEllipse(knob, knobX, track.Top + inset, diameter, diameter);
            g.SmoothingMode = SmoothingMode.Default;

            var textLeft = track.Right + Gap;
            var textBounds = new Rectangle(textLeft, client.Top, Math.Max(0, client.Right - textLeft), client.Height);
            TextRenderer.DrawText(
                g, Text, Font, textBounds, Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues && Text.Length > 0)
            {
                var measured = TextRenderer.MeasureText(g, Text, Font, textBounds.Size, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                var focus = new Rectangle(textLeft - 1, textBounds.Top + (textBounds.Height - measured.Height) / 2, measured.Width + 2, measured.Height);
                ControlPaint.DrawFocusRectangle(g, focus);
            }
        }

        private static GraphicsPath Pill(Rectangle bounds)
        {
            var path = new GraphicsPath();
            var diameter = bounds.Height;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
            path.CloseFigure();
            return path;
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }
    }
}
