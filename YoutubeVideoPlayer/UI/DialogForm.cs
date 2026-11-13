using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The window of a small modal dialog: a fixed border, as large as its content, centered on the window that opened it,
    /// with the application icon and without a taskbar entry. <see cref="AddPlaylistDialog"/>, <see cref="TextPromptDialog"/>
    /// and <see cref="TwitchLoginDialog"/> share it, so a change to how dialogs look is made in one place.
    /// </summary>
    public abstract class DialogForm : Form
    {
        /// <param name="title">The caption of the window.</param>
        /// <param name="padding">The space between the window border and the content.</param>
        protected DialogForm(string title, int padding = 12)
        {
            Text = title;
            if (AppIcon.Instance is { } icon)
                Icon = icon;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(padding);
        }

        /// <summary>A row of buttons at the right edge, in the order given from right to left.</summary>
        /// <param name="topMargin">The space above the row.</param>
        protected static FlowLayoutPanel CreateButtonRow(int topMargin, params Button[] buttonsFromTheRight)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, topMargin, 0, 0)
            };
            row.Controls.AddRange(buttonsFromTheRight);
            return row;
        }
    }
}
