using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Asks for a YouTube playlist link. As soon as a valid link is entered, the playlist name is
    /// fetched from YouTube and pre-filled; the user can change it before confirming.
    /// </summary>
    public sealed class AddPlaylistDialog : Form
    {
        private const int LookupDelayMs = 500;

        private readonly PlaylistManager _playlists;
        private readonly TextBox _linkBox;
        private readonly TextBox _nameBox;
        private readonly Label _statusLabel;
        private readonly Button _okButton;
        private readonly System.Windows.Forms.Timer _lookupDelayTimer;
        private CancellationTokenSource? _lookupCancellation;
        private YouTubePlaylistDetails? _details;
        private string? _autoFilledName;

        public AddPlaylistDialog(PlaylistManager playlists)
        {
            _playlists = playlists;

            Text = "Add playlist";
            if (AppIcon.Instance is { } icon)
                Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _linkBox = new TextBox { Width = 460, PlaceholderText = "https://www.youtube.com/playlist?list=..." };
            _nameBox = new TextBox { Width = 460, Enabled = false };
            _statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(460, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Paste the link of a YouTube playlist."
            };

            layout.Controls.Add(new Label { Text = "Link:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(_linkBox, 1, 0);
            layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(_nameBox, 1, 1);
            layout.Controls.Add(_statusLabel, 1, 2);

            _okButton = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 0)
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(_okButton);
            layout.Controls.Add(buttons, 1, 3);

            Controls.Add(layout);
            AcceptButton = _okButton;
            CancelButton = cancelButton;

            _lookupDelayTimer = new System.Windows.Forms.Timer { Interval = LookupDelayMs };
            _lookupDelayTimer.Tick += (_, __) =>
            {
                _lookupDelayTimer.Stop();
                LookUpPlaylistAsync().FireAndForget("Looking up the playlist failed.");
            };

            _linkBox.TextChanged += (_, __) => OnLinkChanged();
            _nameBox.TextChanged += (_, __) => UpdateOkButton();
        }

        /// <summary>The name chosen by the user (pre-filled with the YouTube title).</summary>
        public string PlaylistTitle => _nameBox.Text.Trim();

        /// <summary>The normalized playlist link.</summary>
        public string PlaylistUrl => _details?.NormalizedUrl ?? _linkBox.Text.Trim();

        private void OnLinkChanged()
        {
            _lookupCancellation?.Cancel();
            _details = null;
            UpdateOkButton();
            _lookupDelayTimer.Stop();
            _lookupDelayTimer.Start();
        }

        private async System.Threading.Tasks.Task LookUpPlaylistAsync()
        {
            var link = _linkBox.Text.Trim();

            if (link.Length == 0)
            {
                SetStatus("Paste the link of a YouTube playlist.", isError: false);
                return;
            }

            var playlistId = PlaylistManager.TryParsePlaylistId(link);
            if (playlistId == null)
            {
                SetStatus("This is not a valid YouTube playlist link.", isError: true);
                return;
            }

            if (_playlists.Contains(playlistId.Value.Value))
            {
                SetStatus("This playlist is already in the list.", isError: true);
                return;
            }

            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _lookupCancellation = cancellation;

            SetStatus("Fetching the playlist name from YouTube...", isError: false);

            try
            {
                var details = await _playlists.GetPlaylistDetailsAsync(link, cancellation.Token);

                // Ignore the result if the link was changed in the meantime.
                if (IsDisposed || cancellation.IsCancellationRequested || _linkBox.Text.Trim() != link)
                    return;

                _details = details;

                // Only replace the name if the user has not typed their own one.
                if (string.IsNullOrWhiteSpace(_nameBox.Text) || _nameBox.Text == _autoFilledName)
                    _nameBox.Text = details.Title;

                _autoFilledName = details.Title;
                _nameBox.Enabled = true;
                SetStatus($"Found on YouTube: \"{details.Title}\". You can change the name before adding it.", isError: false);
            }
            catch (OperationCanceledException)
            {
                // A newer link was entered.
            }
            catch (Exception ex)
            {
                if (IsDisposed || cancellation.IsCancellationRequested)
                    return;

                Logger.Log($"Looking up playlist '{link}' failed.", ex);
                SetStatus($"The playlist could not be found on YouTube: {ex.Message}", isError: true);
            }
            finally
            {
                if (!IsDisposed)
                    UpdateOkButton();
            }
        }

        private void UpdateOkButton()
        {
            _okButton.Enabled = _details != null && !string.IsNullOrWhiteSpace(_nameBox.Text);
        }

        private void SetStatus(string text, bool isError)
        {
            if (IsDisposed)
                return;

            _statusLabel.Text = text;
            _statusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lookupDelayTimer.Dispose();
                _lookupCancellation?.Cancel();
                _lookupCancellation?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
