using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.Twitch.Voting;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// The main window. Only builds the controls and overlays, shows what the managers report and
    /// forwards buttons and keys to them; the playback logic lives in <see cref="PlaybackManager"/>.
    /// </summary>
    public partial class VideoPlayerForm : Form
    {
        private const int VolumeStep = 5;

        private readonly PlaybackManager _playback;
        private readonly PlaylistManager _playlists;
        private readonly AppCoordinator _coordinator;
        private readonly MediaPlayer? _videoOutput;
        private readonly TwitchChatService? _twitch;
        private readonly TwitchTokenManager? _twitchTokens;
        private readonly string _twitchBotName;
        private readonly AutoVoteManager? _autoVote;

        private readonly VideoView _videoView;
        private readonly Panel _controlPanel;
        private readonly Label _titleLabel;
        private readonly Label _volumeLabel;
        private readonly Label _bufferingLabel;
        private readonly Label _twitchStatusLabel;
        private readonly TrackBar _volumeSlider;
        private readonly Button _muteButton;
        private readonly Button _playPauseButton;
        private readonly CheckBox _autoVoteCheckBox;
        private readonly ToolTip _toolTip = new();
        private readonly WindowModeController _windowModes;

        private readonly System.Windows.Forms.Timer _volumeDisplayTimer;
        private readonly System.Windows.Forms.Timer _bufferingAnimationTimer;
        private int _bufferingAnimationFrame;
        private bool _updatingVolumeSlider;
        private bool _playlistDialogOpen;

        /// <param name="videoOutput">The LibVLC player shown in the video area (<c>null</c> shows nothing, e.g. in tests).</param>
        /// <param name="twitchTokens">Set when the bot signs in through the app; enables the sign-in dialog.</param>
        /// <param name="twitchBotName">The expected bot login from the settings (may be empty).</param>
        /// <param name="autoVote">Set when the Twitch bot is on; enables the "Autovote" checkbox.</param>
        public VideoPlayerForm(
            PlaybackManager playback,
            PlaylistManager playlists,
            AppCoordinator coordinator,
            MediaPlayer? videoOutput,
            TwitchChatService? twitch,
            TwitchTokenManager? twitchTokens = null,
            string twitchBotName = "",
            AutoVoteManager? autoVote = null)
        {
            _autoVote = autoVote;
            _playback = playback;
            _playlists = playlists;
            _coordinator = coordinator;
            _videoOutput = videoOutput;
            _twitch = twitch;
            _twitchTokens = twitchTokens;
            _twitchBotName = twitchBotName;

            InitializeComponent();

            Text = "YouTube Video Player";
            Width = 1920;
            Height = 1080;
            KeyPreview = true;

            // The video view must be added first so it fills the space left by the docked control panel.
            _videoView = new VideoView { Dock = DockStyle.Fill };
            Controls.Add(_videoView);

            _titleLabel = CreateOverlayLabel(600, 25, 10, ContentAlignment.MiddleLeft, AnchorStyles.Left | AnchorStyles.Bottom);
            _titleLabel.AutoEllipsis = true;
            _volumeLabel = CreateOverlayLabel(100, 40, 10, ContentAlignment.MiddleCenter, AnchorStyles.Top | AnchorStyles.Right);
            _bufferingLabel = CreateOverlayLabel(150, 50, 14, ContentAlignment.MiddleLeft, AnchorStyles.None);

            _volumeDisplayTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _volumeDisplayTimer.Tick += (_, __) =>
            {
                _volumeLabel.Visible = false;
                _volumeDisplayTimer.Stop();
            };

            // Spinner animation, 500ms per frame
            _bufferingAnimationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _bufferingAnimationTimer.Tick += (_, __) =>
            {
                var spinnerFrames = new[] { ".", "..", "...", "" };
                _bufferingLabel.Text = $"Buffering {spinnerFrames[_bufferingAnimationFrame % 4]}";
                _bufferingAnimationFrame++;
            };

            _controlPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            Controls.Add(_controlPanel);

            // Fill must be added first so it takes the space left between the left and right docked controls.
            _twitchStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = SystemColors.GrayText
            };
            _controlPanel.Controls.Add(_twitchStatusLabel);
            if (_twitchTokens != null)
            {
                // With app sign-in, the status doubles as the entry point to (re-)connect the bot.
                _twitchStatusLabel.Cursor = Cursors.Hand;
                _twitchStatusLabel.Click += (_, __) => ShowTwitchLoginDialog();
            }

            // Added before the buttons so it sits between the status text and the buttons.
            _autoVoteCheckBox = new CheckBox
            {
                Text = "Autovote",
                Dock = DockStyle.Right,
                AutoSize = true,
                Padding = new Padding(8, 0, 8, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Enabled = _autoVote != null
            };
            _autoVoteCheckBox.CheckedChanged += (_, __) =>
            {
                if (_autoVote != null)
                    _autoVote.Enabled = _autoVoteCheckBox.Checked;
            };
            _toolTip.SetToolTip(
                _autoVoteCheckBox,
                _autoVote != null
                    ? "Starts a chat vote 5 minutes before the video ends. The winner plays after the current video."
                    : "Needs the Twitch bot (see twitchsettings.json).");
            _controlPanel.Controls.Add(_autoVoteCheckBox);

            _playPauseButton = AddControlButton("Play", (_, __) => TogglePlayPause());
            AddControlButton("Playlists", (_, __) => ShowPlaylistDialog());
            AddControlButton("Next", (_, __) => _playback.SkipAsync().FireAndForget("Skipping via the Next button failed."));
            AddControlButton("Fullscreen", (_, __) => _windowModes!.ToggleFullscreen());
            AddControlButton("Borderless", (_, __) => _windowModes!.ToggleBorderless());

            _muteButton = new Button
            {
                Text = "\U0001F50A",
                Dock = DockStyle.Left,
                Width = 40,
                Height = 40,
                Font = new Font(FontFamily.GenericSansSerif, 14)
            };
            _muteButton.Click += (_, __) => _playback.ToggleMute();
            _controlPanel.Controls.Add(_muteButton);

            _volumeSlider = new TrackBar
            {
                Dock = DockStyle.Left,
                Width = 120,
                Height = 40,
                Minimum = 0,
                Maximum = 100,
                Value = _playback.Volume,
                AutoSize = false
            };
            _volumeSlider.ValueChanged += (_, __) =>
            {
                if (!_updatingVolumeSlider)
                    _playback.Volume = _volumeSlider.Value;
            };
            _controlPanel.Controls.Add(_volumeSlider);

            _windowModes = new WindowModeController(this, _videoView, _controlPanel);
            _windowModes.ModeChanged += (_, __) => UpdateTitleLabelPosition();
            _videoView.DoubleClick += (_, __) => _windowModes.ToggleFullscreen();

            Resize += (_, __) =>
            {
                UpdateTitleLabelPosition();
                CenterBufferingLabel();
            };
            _videoView.Resize += (_, __) => UpdateTitleLabelPosition();
            KeyDown += OnKeyDown;
            Load += OnLoad;
            FormClosing += OnFormClosing;

            SubscribeToManagers();
            UpdateTwitchStatus();
            UpdateMuteButtonAppearance();
            UpdatePlayPauseButton();
        }

        private void SubscribeToManagers()
        {
            _playback.LoadingStarted += OnLoadingStarted;
            _playback.StateChanged += OnPlaybackStateChanged;
            _playback.NowPlayingChanged += OnNowPlayingChanged;
            _playback.BufferingChanged += OnBufferingChanged;
            _playback.VolumeChanged += OnVolumeChanged;
            _coordinator.NoVideosAvailable += OnNoVideosAvailable;

            if (_twitch != null)
                _twitch.StateChanged += OnTwitchStateChanged;

            if (_autoVote != null)
                _autoVote.EnabledChanged += OnAutoVoteEnabledChanged;
        }

        /// <summary>The Autovote was switched by a chat command: show it in the checkbox.</summary>
        private void OnAutoVoteEnabledChanged(object? sender, EventArgs e)
        {
            if (_autoVote != null)
                _autoVoteCheckBox.Checked = _autoVote.Enabled;
        }

        private void UnsubscribeFromManagers()
        {
            _playback.LoadingStarted -= OnLoadingStarted;
            _playback.StateChanged -= OnPlaybackStateChanged;
            _playback.NowPlayingChanged -= OnNowPlayingChanged;
            _playback.BufferingChanged -= OnBufferingChanged;
            _playback.VolumeChanged -= OnVolumeChanged;
            _coordinator.NoVideosAvailable -= OnNoVideosAvailable;

            if (_autoVote != null)
                _autoVote.EnabledChanged -= OnAutoVoteEnabledChanged;

            if (_twitch != null)
                _twitch.StateChanged -= OnTwitchStateChanged;
        }

        #region Layout helpers

        private Label CreateOverlayLabel(int width, int height, float fontSize, ContentAlignment alignment, AnchorStyles anchor)
        {
            var label = new Label
            {
                AutoSize = false,
                Width = width,
                Height = height,
                BackColor = Color.FromArgb(200, 0, 0, 0),
                ForeColor = Color.White,
                TextAlign = alignment,
                Padding = new Padding(0),
                Visible = false,
                Font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold),
                Anchor = anchor
            };
            Controls.Add(label);
            label.BringToFront();
            return label;
        }

        private Button AddControlButton(string text, EventHandler onClick)
        {
            var button = new Button { Text = text, Dock = DockStyle.Right, Width = 100, Height = 40 };
            button.Click += onClick;
            _controlPanel.Controls.Add(button);
            return button;
        }

        /// <summary>Starts the prepared first video, pauses the playing video, or continues a paused one.</summary>
        private void TogglePlayPause()
        {
            if (_playback.IsStandby)
                _playback.PlayNextAsync().FireAndForget("Starting playback via the Play button failed.");
            else if (_playback.IsPaused)
                _playback.Resume();
            else
                _playback.Pause();
        }

        /// <summary>
        /// "Play" while standing by or paused, "Pause" while a video plays. Disabled until there is something to
        /// start or pause: the prepared first video, or a video that has been loaded.
        /// </summary>
        private void UpdatePlayPauseButton()
        {
            _playPauseButton.Text = _playback.IsStandby || _playback.IsPaused || _playback.Current == null ? "Play" : "Pause";
            _playPauseButton.Enabled = _playback.IsStandby ? _playback.IsStandbyReady : _playback.Current != null;
        }

        private void UpdateTitleLabelPosition()
        {
            // Measure text width and set label width to content (with padding), clamped to video width
            var paddingHoriz = _titleLabel.Padding.Left + _titleLabel.Padding.Right;
            var measured = TextRenderer.MeasureText(_titleLabel.Text ?? string.Empty, _titleLabel.Font);
            var desiredWidth = measured.Width + paddingHoriz;
            var maxWidth = Math.Max(100, _videoView.ClientSize.Width - 20);
            _titleLabel.Width = Math.Min(desiredWidth, maxWidth);

            // position label 10px from left and 10px from bottom of the video view (relative to form)
            var videoBounds = _videoView.Bounds;
            var x = videoBounds.Left + 10;
            var y = Math.Max(10, videoBounds.Bottom - _titleLabel.Height - 10);
            _titleLabel.Location = new Point(x, y);
            _titleLabel.BringToFront();
        }

        private void CenterBufferingLabel()
        {
            var x = (ClientSize.Width - _bufferingLabel.Width) / 2;
            var y = (ClientSize.Height - _bufferingLabel.Height) / 2;
            _bufferingLabel.Location = new Point(Math.Max(0, x), Math.Max(0, y));
            _bufferingLabel.BringToFront();
        }

        private void ShowTitle(string text)
        {
            _titleLabel.Text = text;
            _titleLabel.Visible = !string.IsNullOrWhiteSpace(text);
            UpdateTitleLabelPosition();
        }

        #endregion

        #region Manager events

        private void OnLoadingStarted(object? sender, EventArgs e) => ShowTitle("Loading next video...");

        private void OnPlaybackStateChanged(object? sender, EventArgs e)
        {
            UpdatePlayPauseButton();
            if (_playback.IsStandby)
                ShowTitle(_playback.IsStandbyReady ? "Ready - press Play to start" : "Loading first video...");
        }

        private void OnNowPlayingChanged(object? sender, NowPlaying nowPlaying)
        {
            ShowTitle(nowPlaying.Title);
            UpdatePlayPauseButton();
        }

        private void OnBufferingChanged(object? sender, bool isBuffering)
        {
            if (isBuffering)
            {
                _bufferingLabel.Text = "Buffering...";
                _bufferingLabel.Visible = true;
                CenterBufferingLabel();
                _bufferingAnimationFrame = 0;
                _bufferingAnimationTimer.Start();
            }
            else
            {
                _bufferingLabel.Visible = false;
                _bufferingAnimationTimer.Stop();
            }
        }

        private void OnVolumeChanged(object? sender, EventArgs e)
        {
            _updatingVolumeSlider = true;
            try
            {
                _volumeSlider.Value = _playback.Volume;
            }
            finally
            {
                _updatingVolumeSlider = false;
            }

            UpdateMuteButtonAppearance();
        }

        private void UpdateMuteButtonAppearance()
        {
            if (_playback.IsMuted)
            {
                _muteButton.Text = "\U0001F507";
                _volumeSlider.Enabled = false;
            }
            else
            {
                var volume = _playback.Volume;
                if (volume == 0)
                    _muteButton.Text = "\U0001F507";
                else if (volume < 50)
                    _muteButton.Text = "\U0001F509";
                else
                    _muteButton.Text = "\U0001F50A";
                _volumeSlider.Enabled = true;
            }
        }

        private void ShowVolumeDisplay(string text)
        {
            _volumeLabel.Text = text;
            _volumeLabel.Location = new Point(ClientSize.Width - _volumeLabel.Width - 10, 10);
            _volumeLabel.Visible = true;
            _volumeLabel.BringToFront();
            _volumeDisplayTimer.Stop();
            _volumeDisplayTimer.Start();
        }

        private void OnNoVideosAvailable(object? sender, EventArgs e)
        {
            ShowTitle(string.Empty);
            if (_playlistDialogOpen)
                return;

            var message = _playlists.Playlists.Count == 0
                ? "There are no playlists yet. Add a YouTube playlist to start playing."
                : "None of the playlists could be loaded. Check your internet connection or update the playlists.";
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowPlaylistDialog();
        }

        private void OnTwitchStateChanged(object? sender, TwitchConnectionState state) => UpdateTwitchStatus();

        private void UpdateTwitchStatus()
        {
            if (_twitch == null)
            {
                _twitchStatusLabel.Text = "Twitch: off";
                return;
            }

            var signInHint = _twitch.UsesAppSignIn ? " – click here to sign in" : string.Empty;
            _twitchStatusLabel.Text = _twitch.State switch
            {
                TwitchConnectionState.Connecting => $"Twitch: connecting to #{_twitch.Channel}...",
                TwitchConnectionState.Connected => $"Twitch: connected to #{_twitch.Channel} as {_twitch.BotLogin}",
                TwitchConnectionState.LoginFailed => _twitch.UsesAppSignIn
                    ? "Twitch: login failed" + signInHint
                    : "Twitch: login failed (check the token)",
                TwitchConnectionState.SignInRequired => "Twitch: not signed in" + signInHint,
                TwitchConnectionState.Disconnected => "Twitch: disconnected",
                _ => "Twitch: off"
            };

            var needsSignIn = _twitch.UsesAppSignIn
                && _twitch.State is TwitchConnectionState.SignInRequired or TwitchConnectionState.LoginFailed;
            _twitchStatusLabel.ForeColor = needsSignIn ? Color.Firebrick : SystemColors.GrayText;
        }

        private void ShowTwitchLoginDialog()
        {
            if (_twitchTokens == null)
                return;

            _windowModes.LeaveCurrentMode();
            using var dialog = new TwitchLoginDialog(_twitchTokens, _twitchBotName);
            dialog.ShowDialog(this);
        }

        #endregion

        #region Playlists

        private void ShowPlaylistDialog()
        {
            if (_playlistDialogOpen)
                return;

            // Leave fullscreen/borderless so the dialog is not hidden behind a topmost window.
            _windowModes.LeaveCurrentMode();

            _playlistDialogOpen = true;
            try
            {
                using var dialog = new PlaylistDialog(_playlists);
                dialog.ShowDialog(this);
            }
            finally
            {
                _playlistDialogOpen = false;
            }
        }

        #endregion

        #region Form events and keyboard input

        private void OnLoad(object? sender, EventArgs e)
        {
            _videoView.MediaPlayer = _videoOutput;
            ShowTitle("Loading playlists...");
            _coordinator.StartAsync().FireAndForget("Starting the player failed.");
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F11:
                    _windowModes.ToggleFullscreen();
                    break;
                case Keys.Escape:
                    _windowModes.LeaveCurrentMode();
                    break;
                case Keys.Oemplus:
                case Keys.Add:
                    _playback.AdjustVolume(VolumeStep);
                    ShowVolumeDisplay($"Volume: {_playback.Volume}%");
                    break;
                case Keys.OemMinus:
                case Keys.Subtract:
                    _playback.AdjustVolume(-VolumeStep);
                    ShowVolumeDisplay($"Volume: {_playback.Volume}%");
                    break;
                case Keys.M:
                    _playback.ToggleMute();
                    ShowVolumeDisplay(_playback.IsMuted ? "MUTE" : $"{_playback.Volume}%");
                    break;
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            UnsubscribeFromManagers();
            _volumeDisplayTimer.Dispose();
            _bufferingAnimationTimer.Dispose();
            _toolTip.Dispose();
            _videoView.MediaPlayer = null;
        }

        #endregion
    }
}
