using System;
using System.Collections.Generic;
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
        private readonly IReadOnlyList<IVoteDisplay> _voteDisplays;
        private readonly NowPlayingSchedule _nowPlaying;
        private readonly bool _ownsNowPlaying;
        private readonly AvatarSet _avatars;

        private readonly VideoView _videoView;
        private readonly Panel _controlPanel;
        private readonly TitleStrip _titleStrip;
        private readonly NowPlayingCard _nowPlayingCard;
        private readonly OverlayPill _volumeLabel;
        private readonly OverlayPill _bufferingLabel;
        private readonly OverlayPill _waitingLabel;
        private readonly OverlayPill _resumeLabel;
        private readonly Label _twitchStatusLabel;
        private readonly TrackBar _volumeSlider;
        private readonly Button _muteButton;
        private readonly Button _playPauseButton;
        private readonly CheckBox _autoVoteCheckBox;
        private readonly ToolTip _toolTip = new();
        private readonly WindowModeController _windowModes;
        private readonly AspectRatioLock _aspectLock;

        private readonly System.Windows.Forms.Timer _volumeDisplayTimer;
        private readonly System.Windows.Forms.Timer _bufferingAnimationTimer;
        private readonly System.Windows.Forms.Timer _waitingCountdownTimer;
        private readonly System.Windows.Forms.Timer _voteTickerTimer;
        private readonly System.Windows.Forms.Timer _nowPlayingTimer;
        private bool _showingWaiting;

        // A vote runs (also one that still looks up its options and is not shown yet): it has the card's place.
        private bool _voteRunning;

        // What the strip at the bottom left should show. It is only handed to the strip once the card in the same place
        // is gone, so the two never overlap (see ApplyStrip).
        private string _statusText = string.Empty;
        private AnimatedAvatar? _statusAvatar;
        private TickerContent? _ticker;
        private bool _isBuffering;
        private bool _updatingVolumeSlider;
        private bool _playlistDialogOpen;

        // The YouTube sign-in window for yt-dlp (prototype): opened with the button, or once by itself when YouTube asks.
        private readonly Func<string?, Form>? _createYouTubeSignIn;
        private Form? _youtubeSignIn;
        private bool _youtubeSignInOffered;
        private string? _pendingYouTubeSignIn;
        private bool _shown;

        /// <param name="videoOutput">The LibVLC player shown in the video area (<c>null</c> shows nothing, e.g. in tests).</param>
        /// <param name="twitchTokens">Set when the bot signs in through the app; enables the sign-in dialog.</param>
        /// <param name="twitchBotName">The expected bot login from the settings (may be empty).</param>
        /// <param name="autoVote">Set when the Twitch bot is on; enables the "Autovote" checkbox.</param>
        /// <param name="voteDisplays">The chat votes whose options the ticker at the bottom shows while they run.</param>
        /// <param name="nowPlaying">When the "now playing" card is due; without one the form makes its own.</param>
        /// <param name="youtubeSignIn">
        /// Creates the YouTube sign-in window (with why it opens by itself, or <c>null</c>); set while yt-dlp uses the app's
        /// sign-in. Adds the "YouTube sign-in" button.
        /// </param>
        public VideoPlayerForm(
            PlaybackManager playback,
            PlaylistManager playlists,
            AppCoordinator coordinator,
            MediaPlayer? videoOutput,
            TwitchChatService? twitch,
            TwitchTokenManager? twitchTokens = null,
            string twitchBotName = "",
            AutoVoteManager? autoVote = null,
            IReadOnlyList<IVoteDisplay>? voteDisplays = null,
            NowPlayingSchedule? nowPlaying = null,
            AvatarSet? avatars = null,
            Func<string?, Form>? youtubeSignIn = null)
        {
            _createYouTubeSignIn = youtubeSignIn;
            _autoVote = autoVote;
            _avatars = avatars ?? AvatarSet.None;
            _ownsNowPlaying = nowPlaying == null;
            _nowPlaying = nowPlaying ?? new NowPlayingSchedule(playback);
            _voteDisplays = voteDisplays ?? Array.Empty<IVoteDisplay>();
            _playback = playback;
            _playlists = playlists;
            _coordinator = coordinator;
            _videoOutput = videoOutput;
            _twitch = twitch;
            _twitchTokens = twitchTokens;
            _twitchBotName = twitchBotName;

            InitializeComponent();

            Text = "YouTube Video Player";
            // Only until Load: then the window takes the largest 16:9 size on the monitor with the mouse.
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1280, 760);
            KeyPreview = true;

            // The video view must be added first so it fills the space left by the docked control panel.
            // Black where there is no picture, e.g. while the next video waits for a vote.
            _videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
            Controls.Add(_videoView);

            // Now and then the video that plays: slides in at the bottom left, behind the strip.
            _nowPlayingCard = new NowPlayingCard { NextAvatar = _avatars.NextCurrent };
            Controls.Add(_nowPlayingCard);
            _nowPlayingCard.BringToFront();

            // Status texts ("Loading..."), or during a chat vote a ticker with the options (cross-faded inside one strip).
            _titleStrip = new TitleStrip();
            Controls.Add(_titleStrip);
            _titleStrip.BringToFront();
            _volumeLabel = CreateOverlayPill("volume", OverlayStyle.VolumeGlyph, string.Empty, animated: false);
            // The labels in the middle of the video, in the style of the title strip.
            _bufferingLabel = CreateOverlayPill("buffering", OverlayStyle.BufferingGlyph, "Buffering", animated: true);
            _waitingLabel = CreateOverlayPill("waiting", OverlayStyle.WaitingGlyph, "Waiting for vote to finish...", animated: false);
            _resumeLabel = CreateOverlayPill("resume", OverlayStyle.ConnectionLostGlyph, "Connection lost - resuming", animated: true);
            // The wolf that waits: buffering, waiting for a vote, a lost connection and a reload (the volume keeps its icon).
            _bufferingLabel.Avatar = _avatars.Waiting;
            _waitingLabel.Avatar = _avatars.Waiting;
            _resumeLabel.Avatar = _avatars.Waiting;

            // While the next video waits for a vote, the time left counts down once a second.
            _waitingCountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _waitingCountdownTimer.Tick += (_, __) => UpdateWaitingText();

            // While a vote is shown, its time left counts down once a second.
            _voteTickerTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _voteTickerTimer.Tick += (_, __) => UpdateVoteTicker();

            // While the card is up, its position and bar follow the video once a second.
            _nowPlayingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _nowPlayingTimer.Tick += (_, __) => UpdateNowPlayingContent();

            _volumeDisplayTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _volumeDisplayTimer.Tick += (_, __) =>
            {
                _volumeLabel.Visible = false;
                _volumeDisplayTimer.Stop();
            };

            // The dots of the buffering and the resume label, 500ms per step
            _bufferingAnimationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _bufferingAnimationTimer.Tick += (_, __) =>
            {
                _bufferingLabel.NextFrame();
                _resumeLabel.NextFrame();
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
                    ? $"Starts a chat vote {SkipVoteManager.FormatDuration(_autoVote.LeadTime)} before the video ends. The winner plays after the current video.\n" +
                      "A video too short for a 2-minute vote is followed by a black screen until the vote is over."
                    : "Needs the Twitch bot (see twitchsettings.json).");
            _controlPanel.Controls.Add(_autoVoteCheckBox);

            _playPauseButton = AddControlButton("Play", (_, __) => TogglePlayPause());
            AddControlButton("Playlists", (_, __) => ShowPlaylistDialog());
            AddControlButton("Next", (_, __) =>
            {
                Logger.Log("Next button: skipping the current video.");
                _playback.SkipAsync().FireAndForget("Skipping via the Next button failed.");
            });
            AddControlButton("Fullscreen", (_, __) => _windowModes!.ToggleFullscreen());
            AddControlButton("Borderless", (_, __) => _windowModes!.ToggleBorderless());
            if (_createYouTubeSignIn != null)
            {
                var signIn = AddControlButton("YouTube sign-in", (_, __) => ShowYouTubeSignIn(reason: null));
                signIn.Width = 120;
                _toolTip.SetToolTip(signIn, "Signs yt-dlp in to YouTube with a Google account (a spare one is best),\n" +
                                            "for when YouTube asks this connection to confirm it is not a bot.");
            }

            Shown += (_, __) =>
            {
                _shown = true;
                if (_pendingYouTubeSignIn is { } reason)
                {
                    _pendingYouTubeSignIn = null;
                    ShowYouTubeSignIn(reason);
                }
            };

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
            // The video area keeps 16:9 whatever the window is dragged to (the control bar is not part of it).
            _aspectLock = new AspectRatioLock(this, _controlPanel, _windowModes);
            _videoView.DoubleClick += (_, __) => _windowModes.ToggleFullscreen();

            Resize += (_, __) =>
            {
                UpdateTitleLabelPosition();
                CenterOverlay(_bufferingLabel);
                CenterOverlay(_waitingLabel);
                CenterOverlay(_resumeLabel);
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

            foreach (var votes in _voteDisplays)
                votes.DisplayChanged += OnVoteDisplayChanged;

            _nowPlaying.Changed += OnNowPlayingScheduleChanged;
            _nowPlayingCard.VisibleChanged += OnNowPlayingCardVisibleChanged;
            _titleStrip.VisibleChanged += OnTitleStripVisibleChanged;
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

            foreach (var votes in _voteDisplays)
                votes.DisplayChanged -= OnVoteDisplayChanged;

            _nowPlaying.Changed -= OnNowPlayingScheduleChanged;
            _nowPlayingCard.VisibleChanged -= OnNowPlayingCardVisibleChanged;
            _titleStrip.VisibleChanged -= OnTitleStripVisibleChanged;

            if (_twitch != null)
                _twitch.StateChanged -= OnTwitchStateChanged;
        }

        #region Layout helpers

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
            if (_playback.IsWaitingForHold)
                return; // the next video starts when the vote is over (Next starts it now)

            if (_playback.IsStandby)
            {
                Logger.Log("Play button: starting playback.");
                _playback.PlayNextAsync().FireAndForget("Starting playback via the Play button failed.");
            }
            else if (_playback.IsPaused)
                _playback.Resume();
            else
                _playback.Pause();
        }

        /// <summary>
        /// "Play" while standing by or paused, "Pause" while a video plays. Disabled until there is something to
        /// start or pause: the prepared first video, or a video that has been loaded. Also disabled while the next
        /// video waits for a vote.
        /// </summary>
        private void UpdatePlayPauseButton()
        {
            _playPauseButton.Text = _playback.IsStandby || _playback.IsPaused || _playback.IsWaitingForHold || _playback.Current == null
                ? "Play"
                : "Pause";
            _playPauseButton.Enabled = _playback.IsStandby
                ? _playback.IsStandbyReady
                : _playback.Current != null && !_playback.IsWaitingForHold;
        }

        private void UpdateTitleLabelPosition()
        {
            // 10 px from the left and the bottom of the video; the strip sizes itself to its text (the ticker to all of it).
            // The card sits in the same place, behind the strip; they are never up at the same time.
            var videoBounds = _videoView.Bounds;
            var availableWidth = Math.Max(100, _videoView.ClientSize.Width - 20);
            var bottomLeft = new Point(videoBounds.Left + 10, Math.Max(40, videoBounds.Bottom - 10));
            _nowPlayingCard.AvailableWidth = availableWidth;
            _nowPlayingCard.BottomLeft = bottomLeft;
            _nowPlayingCard.BringToFront();
            _titleStrip.AvailableWidth = availableWidth;
            _titleStrip.BottomLeft = bottomLeft;
            _titleStrip.BringToFront();
        }

        private OverlayPill CreateOverlayPill(string name, char icon, string text, bool animated)
        {
            var pill = new OverlayPill(icon) { Name = name, Text = text, Animated = animated };
            Controls.Add(pill);
            pill.BringToFront();
            return pill;
        }

        private void CenterOverlay(Control label)
        {
            var x = (ClientSize.Width - label.Width) / 2;
            var y = (ClientSize.Height - label.Height) / 2;
            label.Location = new Point(Math.Max(0, x), Math.Max(0, y));
            label.BringToFront();
        }

        /// <summary>
        /// Shows a status text in the strip (empty hides it). During a vote it is kept behind the ticker and comes back
        /// afterwards. The video's own title is on the "now playing" card instead.
        /// </summary>
        private void ShowTitle((string Text, AnimatedAvatar? Avatar) status) => ShowTitle(status.Text, status.Avatar);

        private void ShowTitle(string text, AnimatedAvatar? avatar = null)
        {
            _statusText = string.IsNullOrWhiteSpace(text) ? string.Empty : text;
            _statusAvatar = avatar;
            UpdateTitleLabelPosition();
            UpdateNowPlayingCard();
            ApplyStrip();
        }

        /// <summary>
        /// The bottom left shows one thing at a time. The strip (a status text or a vote ticker) only gets its content once
        /// the card is completely gone: a status text makes the card close at once, a vote waits until the card's time is
        /// up. Called again when the card has disappeared.
        /// </summary>
        private void ApplyStrip()
        {
            if (IsDisposed || Disposing)
                return;

            if ((_ticker != null || _statusText.Length > 0) && _nowPlayingCard.Visible)
                return;

            _titleStrip.Title = _statusText;
            _titleStrip.TitleAvatar = _statusAvatar;
            _titleStrip.Ticker = _ticker;
            if (_ticker != null)
                _titleStrip.TickerAvatar = _avatars.ForVote(_ticker.Ending); // kept while the ticker fades out
        }

        private void OnNowPlayingCardVisibleChanged(object? sender, EventArgs e)
        {
            if (!_nowPlayingCard.Visible)
                ApplyStrip();
        }

        private void OnTitleStripVisibleChanged(object? sender, EventArgs e)
        {
            if (!_titleStrip.Visible)
                UpdateNowPlayingCard();
        }

        private void OnNowPlayingScheduleChanged(object? sender, EventArgs e) => UpdateNowPlayingCard();

        /// <summary>
        /// The card is up while it is due and a video is there to show. A status text takes its place at once. A vote that
        /// starts while the card is up waits until the card's time is over (its ticker appears once the card is gone), and
        /// no vote brings the card up (the schedule is <see cref="NowPlayingSchedule.Blocked"/> meanwhile). Otherwise the
        /// card only appears once the strip in the same place is gone.
        /// </summary>
        private void UpdateNowPlayingCard()
        {
            if (IsDisposed || Disposing)
                return;

            bool show;
            if (!_nowPlaying.IsShown || !_nowPlaying.CanShow || _statusText.Length > 0)
                show = false;
            else if (_voteRunning)
                show = _nowPlayingCard.Showing;
            else
                show = _nowPlayingCard.Showing || !_titleStrip.Visible;
            if (show)
            {
                UpdateNowPlayingContent();
                _nowPlayingTimer.Start();
            }
            else
            {
                _nowPlayingTimer.Stop();
            }

            _nowPlayingCard.Showing = show;
        }

        /// <summary>The playing video, its position and length; kept while the card slides out.</summary>
        private void UpdateNowPlayingContent()
        {
            if (_playback.Current is { } current)
                _nowPlayingCard.Content = new NowPlayingCardContent(current.Title, _playback.Position, _playback.Length);
        }

        private void OnVoteDisplayChanged(object? sender, EventArgs e) => UpdateVoteTicker();

        /// <summary>
        /// While a chat vote runs, the strip at the bottom scrolls its options, counts and time left instead of the title.
        /// Only one vote runs at a time.
        /// </summary>
        private void UpdateVoteTicker()
        {
            var display = _voteDisplays.Select(v => v.Display).FirstOrDefault(d => d != null);
            _voteRunning = display != null;
            _nowPlaying.Blocked = _voteRunning;
            if (display == null)
                _ticker = null;
            else if (VoteTicker.IsComplete(display))
                _ticker = VoteTicker.From(display);
            // Otherwise the vote is still looking up its options: it only shows once they are known. A ticker that is up
            // (a won !voteskip that started this vote) stays until then, so the strip has no gap.

            UpdateNowPlayingCard();
            ApplyStrip();

            if (display == null)
                _voteTickerTimer.Stop();
            else if (!_voteTickerTimer.Enabled)
                _voteTickerTimer.Start();
        }

        #endregion

        #region Manager events

        private void OnLoadingStarted(object? sender, EventArgs e) => ShowTitle("Loading next video...", _avatars.Waiting);

        private void OnPlaybackStateChanged(object? sender, EventArgs e)
        {
            UpdatePlayPauseButton();
            UpdateWaitingDisplay();
            UpdateOverlayLabels();
            if (_playback.IsStandby)
                ShowTitle(_playback.IsStandbyReady
                    ? ("Ready - press Play to start", _avatars.Ok)
                    : ("Loading first video...", _avatars.Waiting));
            UpdateNowPlayingCard();
        }

        /// <summary>
        /// While the next video waits for a vote, the picture is black and a label in the middle counts down the time
        /// left. The title of the finished video is hidden; the next video shows its own.
        /// </summary>
        private void UpdateWaitingDisplay()
        {
            if (_playback.IsWaitingForHold == _showingWaiting)
                return;

            _showingWaiting = _playback.IsWaitingForHold;
            if (_showingWaiting)
            {
                ShowTitle(string.Empty);
                UpdateWaitingText();
                _waitingLabel.Visible = true;
                CenterOverlay(_waitingLabel);
                _waitingCountdownTimer.Start();
            }
            else
            {
                _waitingCountdownTimer.Stop();
                _waitingLabel.Visible = false;
            }
        }

        private void UpdateWaitingText()
        {
            // Rounded up, so the label shows 0:00 only when the vote is really over.
            _waitingLabel.Text = _playback.HoldRemaining is { } remaining
                ? $"Waiting for vote to finish... {FormatCountdown(remaining)}"
                : "Waiting for vote to finish...";
            if (_waitingLabel.Visible)
                CenterOverlay(_waitingLabel); // its width follows the text
        }

        private static string FormatCountdown(TimeSpan remaining)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        /// <summary>A new video: its title goes on the card (the schedule brings it up), the strip's status text goes.</summary>
        private void OnNowPlayingChanged(object? sender, NowPlaying nowPlaying)
        {
            UpdateNowPlayingContent();
            ShowTitle(string.Empty);
            UpdatePlayPauseButton();
        }

        private void OnBufferingChanged(object? sender, bool isBuffering)
        {
            _isBuffering = isBuffering;
            UpdateOverlayLabels();
        }

        /// <summary>
        /// The labels in the middle of the video: while the stream of the video broke off and it is resumed, one says
        /// so, instead of the buffering label, which would sit in the same place (the resumed stream buffers too).
        /// </summary>
        private void UpdateOverlayLabels()
        {
            var resuming = _playback.IsResuming;
            var buffering = _isBuffering && !resuming;

            if (resuming)
                UpdateResumeText(); // also a reload that turns into a resume, or the other way round

            if (resuming && !_resumeLabel.Visible)
            {
                _resumeLabel.ResetFrame();
                _resumeLabel.Visible = true;
                CenterOverlay(_resumeLabel);
            }
            else if (!resuming)
            {
                _resumeLabel.Visible = false;
            }

            if (buffering && !_bufferingLabel.Visible)
            {
                _bufferingLabel.ResetFrame();
                _bufferingLabel.Visible = true;
                CenterOverlay(_bufferingLabel);
            }
            else if (!buffering)
            {
                _bufferingLabel.Visible = false;
            }

            if (resuming || buffering)
                _bufferingAnimationTimer.Start();
            else
                _bufferingAnimationTimer.Stop();
        }

        private void UpdateResumeText()
        {
            var at = _playback.ResumePosition is { } position ? " at " + PositionText.Format(position) : string.Empty;
            _resumeLabel.Icon = _playback.IsReloading ? OverlayStyle.ReloadGlyph : OverlayStyle.ConnectionLostGlyph;
            _resumeLabel.Text = _playback.IsReloading ? $"Reloading - continuing{at}" : $"Connection lost - resuming{at}";
            if (_resumeLabel.Visible)
                CenterOverlay(_resumeLabel); // its width follows the text
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

        private void ShowVolumeDisplay()
        {
            _volumeLabel.Icon = _playback.IsMuted ? OverlayStyle.MuteGlyph : OverlayStyle.VolumeGlyph;
            _volumeLabel.Text = _playback.IsMuted ? "Muted" : $"Volume {_playback.Volume}%";
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
                TwitchConnectionState.Connecting => $"Twitch: connecting to #{_twitch.VodChannel}...",
                TwitchConnectionState.Connected => $"Twitch: connected to #{_twitch.VodChannel} as {_twitch.BotLogin}",
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

        /// <summary>
        /// YouTube asked yt-dlp for a sign-in: opens the sign-in window by itself, once per run (later requests are only
        /// logged by yt-dlp; the button opens it any time). Before the window is shown, it opens once it is.
        /// </summary>
        public void OfferYouTubeSignIn(string reason)
        {
            if (_createYouTubeSignIn == null || _youtubeSignInOffered || IsDisposed)
                return;

            _youtubeSignInOffered = true;
            Logger.Log($"Opening the YouTube sign-in window: {reason}");
            if (_shown)
                ShowYouTubeSignIn(reason);
            else
                _pendingYouTubeSignIn = reason;
        }

        /// <summary>
        /// Opens the sign-in window, or brings it to the front if it is open. Not modal and the window mode stays: the
        /// player goes on (a borderless capture source stays as it is), and an owned window stays above the player.
        /// </summary>
        private void ShowYouTubeSignIn(string? reason)
        {
            if (_createYouTubeSignIn == null || IsDisposed)
                return;

            if (_youtubeSignIn is { IsDisposed: false } open)
            {
                open.Activate();
                return;
            }

            var window = _createYouTubeSignIn(reason);
            window.FormClosed += (_, __) =>
            {
                if (ReferenceEquals(_youtubeSignIn, window))
                    _youtubeSignIn = null;
            };
            _youtubeSignIn = window;
            window.Show(this);
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

        /// <summary>Keeps the video area at 16:9 (exposed for tests).</summary>
        internal AspectRatioLock AspectLock => _aspectLock;

        private void OnLoad(object? sender, EventArgs e)
        {
            // A form placed by its caller (tests show it off-screen) stays where it is.
            if (StartPosition != FormStartPosition.Manual)
            {
                var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                _aspectLock.FitToWorkArea(workArea);
                // The invisible resize border is only measurable once the window is on screen: fit once more, so the
                // visible frame fills the work area.
                var startBounds = Bounds;
                Shown += (_, __) =>
                {
                    if (Bounds == startBounds && WindowState == FormWindowState.Normal && !_windowModes.IsFullscreen)
                        _aspectLock.FitToWorkArea(workArea);
                };
            }

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
                    ShowVolumeDisplay();
                    break;
                case Keys.OemMinus:
                case Keys.Subtract:
                    _playback.AdjustVolume(-VolumeStep);
                    ShowVolumeDisplay();
                    break;
                case Keys.M:
                    _playback.ToggleMute();
                    ShowVolumeDisplay();
                    break;
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            UnsubscribeFromManagers();
            _volumeDisplayTimer.Dispose();
            _bufferingAnimationTimer.Dispose();
            _waitingCountdownTimer.Dispose();
            _voteTickerTimer.Dispose();
            _nowPlayingTimer.Dispose();
            if (_ownsNowPlaying)
                _nowPlaying.Dispose();
            _toolTip.Dispose();
            _videoView.MediaPlayer = null;
        }

        #endregion
    }
}
