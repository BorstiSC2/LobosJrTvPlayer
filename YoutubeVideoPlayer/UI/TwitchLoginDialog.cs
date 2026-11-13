using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Signs the bot in with Twitch's device code flow: shows a code that the user confirms on
    /// twitch.tv/activate while logged in as the bot account.
    /// </summary>
    public sealed class TwitchLoginDialog : DialogForm
    {
        private readonly TwitchTokenManager _tokens;
        private readonly string _expectedBotLogin;
        private readonly Label _instructionsLabel;
        private readonly TextBox _codeBox;
        private readonly Button _openBrowserButton;
        private readonly Button _copyButton;
        private readonly Button _retryButton;
        private readonly Button _closeButton;
        private readonly Label _statusLabel;
        private readonly System.Windows.Forms.Timer _countdownTimer;
        private CancellationTokenSource? _signInCancellation;
        private DeviceCodeInfo? _device;
        private DateTime _expiresAtUtc;

        public TwitchLoginDialog(TwitchTokenManager tokens, string expectedBotLogin) : base("Connect Twitch bot", padding: 14)
        {
            _tokens = tokens;
            _expectedBotLogin = expectedBotLogin.Trim().ToLowerInvariant();

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

            var botHint = string.IsNullOrEmpty(_expectedBotLogin) ? "the bot account" : $"the bot account \"{_expectedBotLogin}\"";
            _instructionsLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                Text = $"1. Open the activation page (button below).\n" +
                       $"2. Log in to Twitch with {botHint} – not with your own account.\n" +
                       $"3. Confirm the code shown below and authorize the app.",
                Margin = new Padding(0, 0, 0, 10)
            };
            layout.Controls.Add(_instructionsLabel);

            _codeBox = new TextBox
            {
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font(FontFamily.GenericMonospace, 20, FontStyle.Bold),
                Width = 440,
                Text = "..."
            };
            layout.Controls.Add(_codeBox);

            _openBrowserButton = new Button { Text = "Open activation page", AutoSize = true, Enabled = false };
            _openBrowserButton.Click += (_, __) => OpenBrowser();
            _copyButton = new Button { Text = "Copy code", AutoSize = true, Enabled = false };
            _copyButton.Click += (_, __) =>
            {
                if (_device != null)
                    Clipboard.SetText(_device.UserCode);
            };
            var actionButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            actionButtons.Controls.Add(_openBrowserButton);
            actionButtons.Controls.Add(_copyButton);
            layout.Controls.Add(actionButtons);

            _statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Requesting a code from Twitch...",
                Margin = new Padding(0, 10, 0, 0)
            };
            layout.Controls.Add(_statusLabel);

            _retryButton = new Button { Text = "New code", AutoSize = true, Visible = false };
            _retryButton.Click += (_, __) => StartSignIn();
            _closeButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            layout.Controls.Add(CreateButtonRow(12, _closeButton, _retryButton));

            Controls.Add(layout);
            CancelButton = _closeButton;

            _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _countdownTimer.Tick += (_, __) => UpdateCountdown();

            Shown += (_, __) => StartSignIn();
            FormClosing += (_, __) => _signInCancellation?.Cancel();
        }

        private async void StartSignIn()
        {
            _signInCancellation?.Cancel();
            _signInCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _signInCancellation = cancellation;

            _retryButton.Visible = false;
            _device = null;
            _codeBox.Text = "...";
            SetStatus("Requesting a code from Twitch...", isError: false);

            try
            {
                var token = await _tokens.SignInWithDeviceCodeAsync(ShowCode, cancellation.Token);
                if (IsDisposed)
                    return;

                _countdownTimer.Stop();
                _openBrowserButton.Enabled = false;
                _copyButton.Enabled = false;

                if (!string.IsNullOrEmpty(_expectedBotLogin) && token.Login != _expectedBotLogin)
                {
                    SetStatus(
                        $"Signed in as \"{token.Login}\", but botUsername in the settings is \"{_expectedBotLogin}\". " +
                        "The bot will chat as \"" + token.Login + "\". If that is wrong, request a new code and log in with the bot account.",
                        isError: true);
                    _retryButton.Visible = true;
                }
                else
                {
                    SetStatus($"Signed in as \"{token.Login}\". The token is renewed automatically from now on.", isError: false);
                }

                _closeButton.Text = "Close";
                _closeButton.DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException)
            {
                // Dialog closed or a new code was requested.
            }
            catch (Exception ex)
            {
                Logger.Log("Twitch sign-in failed.", ex);
                if (IsDisposed || cancellation.IsCancellationRequested)
                    return;

                _countdownTimer.Stop();
                _openBrowserButton.Enabled = false;
                _copyButton.Enabled = false;
                SetStatus(ex.Message, isError: true);
                _retryButton.Visible = true;
            }
        }

        private void ShowCode(DeviceCodeInfo device)
        {
            if (IsDisposed)
                return;

            _device = device;
            _expiresAtUtc = DateTime.UtcNow + device.ExpiresIn;
            _codeBox.Text = device.UserCode;
            _openBrowserButton.Enabled = true;
            _copyButton.Enabled = true;
            UpdateCountdown();
            _countdownTimer.Start();
        }

        private void UpdateCountdown()
        {
            var left = _expiresAtUtc - DateTime.UtcNow;
            if (left < TimeSpan.Zero)
                left = TimeSpan.Zero;

            SetStatus($"Waiting for the confirmation on twitch.tv... (code valid for {left:m\\:ss})", isError: false);
        }

        private void OpenBrowser()
        {
            if (_device == null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(_device.VerificationUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Log("Opening the browser failed.", ex);
                Clipboard.SetText(_device.VerificationUri);
                SetStatus($"The browser could not be opened. The link was copied to the clipboard: {_device.VerificationUri}", isError: true);
            }
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
                _countdownTimer.Dispose();
                _signInCancellation?.Cancel();
                _signInCancellation?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
