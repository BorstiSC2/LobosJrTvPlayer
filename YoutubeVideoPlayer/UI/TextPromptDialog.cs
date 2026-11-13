using System.Drawing;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>A minimal modal dialog asking for a single line of text.</summary>
    public sealed class TextPromptDialog : Form
    {
        private readonly TextBox _textBox;

        private TextPromptDialog(string title, string prompt, string initialText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });

            _textBox = new TextBox { Text = initialText, Width = 380 };
            layout.Controls.Add(_textBox);

            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0)
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            layout.Controls.Add(buttons);

            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            _textBox.TextChanged += (_, __) => okButton.Enabled = !string.IsNullOrWhiteSpace(_textBox.Text);
            Shown += (_, __) => _textBox.SelectAll();
        }

        /// <summary>Shows the dialog and returns the entered text, or <c>null</c> if it was cancelled.</summary>
        public static string? Show(IWin32Window owner, string title, string prompt, string initialText)
        {
            using var dialog = new TextPromptDialog(title, prompt, initialText);
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._textBox.Text.Trim() : null;
        }
    }
}
