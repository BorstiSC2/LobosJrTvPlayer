using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using Newtonsoft.Json;

namespace YoutubeVideoPlayer
{
    public partial class VideoPlayerForm : Form
    {
        private VideoView _videoView;
        private Button _nextButton;
        private Panel _controlPanel;
        private Button _fullscreenButton;
        private Label _titleLabel;
        private Label _volumeLabel;
        private Label _bufferingLabel;
        private TrackBar _volumeSlider;
        private Button _muteButton;
        private bool _isFullscreen = false;
        private FormBorderStyle _prevFormBorderStyle;
        private FormWindowState _prevWindowState;
        private bool _prevTopMost;
        private Rectangle _prevBounds;
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private List<string> _videoIds;
        private Random _rnd = new Random();
        private YoutubeClient _yt;
        private System.Windows.Forms.Timer _volumeDisplayTimer;
        private System.Windows.Forms.Timer _bufferingTimeoutTimer;
        private System.Windows.Forms.Timer _bufferingAnimationTimer;
        private int _bufferingAnimationFrame = 0;
        private bool _isCurrentlyBuffering = false;
        private const int BUFFERING_TIMEOUT_MS = 30000; // 30 seconds

        public VideoPlayerForm()
        {
            InitializeComponent();

            Text = "YouTube Video Player";
            Width = 1920;
            Height = 1080;

            KeyPreview = true;
            KeyDown += Form1_KeyDown;

            _videoView = new VideoView { Dock = DockStyle.Fill };
            Controls.Add(_videoView);

            InitializeTitleLabel();
            InitializeVolumeLabel();
            InitializeBufferingLabel();
            InitializeTimers();
            InitializeButtons();

            Load += Form1_Load;
            FormClosing += Form1_FormClosing;
        }

        #region Title Label

        private void InitializeTitleLabel()
        {
            _titleLabel = new Label
            {
                AutoSize = false,
                Height = 48,
                Width = 600,
                BackColor = Color.FromArgb(200, 0, 0, 0),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8),
                Visible = false,
                Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                AutoEllipsis = true
            };
            Controls.Add(_titleLabel);
            _titleLabel.BringToFront();
            this.Resize += (_, __) => UpdateTitleLabelPosition();
            _videoView.Resize += (_, __) => UpdateTitleLabelPosition();
        }

        private void UpdateTitleLabelPosition()
        {
            if (_titleLabel == null || _videoView == null)
                return;

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

        #endregion

        #region Volume Label

        private void InitializeVolumeLabel()
        {
            _volumeLabel = new Label
            {
                AutoSize = false,
                Height = 40,
                Width = 100,
                BackColor = Color.FromArgb(200, 0, 0, 0),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            Controls.Add(_volumeLabel);
            _volumeLabel.BringToFront();
        }

        private void AdjustVolume(int delta)
        {
            if (_mediaPlayer == null)
                return;

            var newVolume = Math.Max(0, Math.Min(100, _mediaPlayer.Volume + delta));
            _mediaPlayer.Volume = newVolume;
            ShowVolumeDisplay(newVolume);
        }

        private void ToggleMute()
        {
            if (_mediaPlayer == null)
                return;

            _mediaPlayer.Mute = !_mediaPlayer.Mute;
            var volumeText = _mediaPlayer.Mute ? "MUTE" : $"{_mediaPlayer.Volume}%";
            ShowVolumeDisplay(_mediaPlayer.Volume, volumeText);
        }

        private void ShowVolumeDisplay(int volume, string customText = null)
        {
            if (_volumeLabel == null)
                return;

            _volumeLabel.Text = customText ?? $"Volume: {volume}%";
            _volumeLabel.Location = new Point(ClientSize.Width - _volumeLabel.Width - 10, 10);
            _volumeLabel.Visible = true;
            _volumeDisplayTimer.Stop();
            _volumeDisplayTimer.Start();
        }

        #endregion

        #region Buffering Label

        private void InitializeBufferingLabel()
        {
            _bufferingLabel = new Label
            {
                AutoSize = false,
                Height = 50,
                Width = 150,
                BackColor = Color.FromArgb(200, 0, 0, 0),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = false,
                Font = new Font(FontFamily.GenericSansSerif, 14, FontStyle.Bold),
                Anchor = AnchorStyles.None
            };
            Controls.Add(_bufferingLabel);
            _bufferingLabel.BringToFront();
        }

        private void CenterBufferingLabel()
        {
            if (_bufferingLabel == null)
                return;

            var x = (ClientSize.Width - _bufferingLabel.Width) / 2;
            var y = (ClientSize.Height - _bufferingLabel.Height) / 2;
            _bufferingLabel.Location = new Point(Math.Max(0, x), Math.Max(0, y));
            _bufferingLabel.BringToFront();
        }

        #endregion

        #region Timers

        private void InitializeTimers()
        {
            // Timer to hide volume label after a few seconds
            _volumeDisplayTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _volumeDisplayTimer.Tick += (_, __) =>
            {
                _volumeLabel.Visible = false;
                _volumeDisplayTimer.Stop();
            };

            // Buffering timeout timer (30 seconds max buffering before skipping to next video)
            _bufferingTimeoutTimer = new System.Windows.Forms.Timer { Interval = BUFFERING_TIMEOUT_MS };
            _bufferingTimeoutTimer.Tick += async (_, __) =>
            {
                _bufferingTimeoutTimer.Stop();
                // Timeout reached; skip to next video silently
                await PlayNextAsync();
            };

            // Buffering animation timer (spinner animation, 500ms per frame)
            _bufferingAnimationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _bufferingAnimationTimer.Tick += (_, __) =>
            {
                if (_isCurrentlyBuffering && _bufferingLabel.Visible)
                {
                    var spinnerFrames = new[] { ".", "..", "...", "" };
                    _bufferingLabel.Text = $"Buffering {spinnerFrames[_bufferingAnimationFrame % 4]}";
                    _bufferingAnimationFrame++;
                }
            };
        }

        #endregion

        #region Buttons

        private void InitializeButtons()
        {
            // Bottom panel to hold control buttons
            _controlPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            Controls.Add(_controlPanel);

            _nextButton = new Button { Text = "Next", Dock = DockStyle.Right, Width = 100, Height = 40 };
            _nextButton.Click += async (_, __) =>
            {
                // Stop current playback immediately to avoid waiting for manifests to load
                try
                {
                    _mediaPlayer?.Stop();
                    _currentMedia?.Dispose();
                }
                catch { }

                await PlayNextAsync();
            };
            _controlPanel.Controls.Add(_nextButton);

            _fullscreenButton = new Button { Text = "Fullscreen", Dock = DockStyle.Right, Width = 100, Height = 40 };
            _fullscreenButton.Click += (_, __) => ToggleFullscreen();
            _controlPanel.Controls.Add(_fullscreenButton);

            _videoView.DoubleClick += (_, __) => ToggleFullscreen();

            InitializeAudioControls();
        }

        #endregion

        #region Audio Controls

        private void InitializeAudioControls()
        {
            // Mute button
            _muteButton = new Button
            {
                Text = "🔊",
                Dock = DockStyle.Left,
                Width = 40,
                Height = 40,
                Font = new Font(FontFamily.GenericSansSerif, 14)
            };
            _muteButton.Click += (_, __) => ToggleMuteButton();
            _controlPanel.Controls.Add(_muteButton);

            // Volume slider (between mute button and next button)
            _volumeSlider = new TrackBar
            {
                Dock = DockStyle.Left,
                Width = 120,
                Height = 40,
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                AutoSize = false
            };
            _volumeSlider.ValueChanged += (_, __) => OnVolumeSliderChanged();
            _controlPanel.Controls.Add(_volumeSlider);
        }

        private void OnVolumeSliderChanged()
        {
            if (_mediaPlayer == null)
                return;

            _mediaPlayer.Volume = _volumeSlider.Value;
            UpdateMuteButtonAppearance();
        }

        private void ToggleMuteButton()
        {
            if (_mediaPlayer == null)
                return;

            _mediaPlayer.Mute = !_mediaPlayer.Mute;
            UpdateMuteButtonAppearance();
        }

        private void UpdateMuteButtonAppearance()
        {
            if (_muteButton == null || _mediaPlayer == null)
                return;

            if (_mediaPlayer.Mute)
            {
                _muteButton.Text = "🔇";
                _volumeSlider.Enabled = false;
            }
            else
            {
                var volume = _mediaPlayer.Volume;
                if (volume == 0)
                    _muteButton.Text = "🔇";
                else if (volume < 50)
                    _muteButton.Text = "🔉";
                else
                    _muteButton.Text = "🔊";
                _volumeSlider.Enabled = true;
            }
        }

        #endregion

        #region Form Initialization

        private async void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                _libVLC = new LibVLC();
                _mediaPlayer = new MediaPlayer(_libVLC);
                _videoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
                _mediaPlayer.Buffering += MediaPlayer_Buffering;
                _mediaPlayer.Playing += MediaPlayer_Playing;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;

                _yt = new YoutubeClient();

                this.Resize += (_, __) => CenterBufferingLabel();

                await LoadVideoListAsync();
                await PlayNextAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Initialisierungsfehler: {ex.Message}");
            }
        }

        #endregion

        #region Core Events (MediaPlayer & Form)

        private void MediaPlayer_EndReached(object sender, EventArgs e)
        {
            // EndReached may be raised on a libvlc thread; marshal to UI thread
            BeginInvoke(async () => await PlayNextAsync());
        }

        private void MediaPlayer_Buffering(object sender, MediaPlayerBufferingEventArgs e)
        {
            // Called on libvlc thread; marshal to UI thread
            BeginInvoke(() =>
            {
                if (e.Cache < 100)
                {
                    // Still buffering
                    _isCurrentlyBuffering = true;
                    _bufferingLabel.Text = "Buffering...";
                    _bufferingLabel.Visible = true;
                    CenterBufferingLabel();
                    _bufferingAnimationFrame = 0;
                    _bufferingAnimationTimer.Start();

                    // Start timeout timer if not already running
                    if (!_bufferingTimeoutTimer.Enabled)
                        _bufferingTimeoutTimer.Start();
                }
                else
                {
                    // Buffering complete (100%)
                    _isCurrentlyBuffering = false;
                    _bufferingLabel.Visible = false;
                    _bufferingAnimationTimer.Stop();
                    _bufferingTimeoutTimer.Stop();
                }
            });
        }

        private void MediaPlayer_Playing(object sender, EventArgs e)
        {
            // Called on libvlc thread; marshal to UI thread
            BeginInvoke(() =>
            {
                _isCurrentlyBuffering = false;
                _bufferingLabel.Visible = false;
                _bufferingAnimationTimer.Stop();
                _bufferingTimeoutTimer.Stop();
            });
        }

        private void MediaPlayer_EncounteredError(object sender, EventArgs e)
        {
            // Stream error; skip to next video silently
            BeginInvoke(async () =>
            {
                _bufferingTimeoutTimer.Stop();
                _bufferingAnimationTimer.Stop();
                _bufferingLabel.Visible = false;
                await PlayNextAsync();
            });
        }

        #endregion

        #region Video Playback

        private async Task LoadVideoListAsync()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "videos.json");
            if (!File.Exists(path))
            {
                // create a sample file with one well-known id
                var sample = new List<string> { "dQw4w9WgXcQ" }; // sample id
                File.WriteAllText(path, JsonConvert.SerializeObject(sample, Formatting.Indented));
            }

            var json = await File.ReadAllTextAsync(path);
            try
            {
                _videoIds = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                _videoIds = new List<string>();
            }
        }

        private async Task PlayNextAsync()
        {
            if (_videoIds == null || _videoIds.Count == 0)
            {
                MessageBox.Show("Keine Videos in videos.json gefunden.");
                return;
            }

            // Try items until one plays successfully. On failure remove the id and try the next without showing a message box.
            while (_videoIds != null && _videoIds.Count > 0)
            {
                var index = _rnd.Next(_videoIds.Count);
                var id = _videoIds[index];
                try
                {
                    await PlayVideoByIdAsync(id);
                    // success
                    return;
                }
                catch
                {
                    // remove problematic id and continue with next
                    try
                    {
                        _videoIds.RemoveAt(index);
                    }
                    catch { }

                    // continue loop without showing a message box
                }
            }

            // If we exit loop, no playable videos remain; quietly return (no message box per request)
            return;
        }

        private async Task PlayVideoByIdAsync(string idOrUrl)
        {
            // Accept either raw ID or full URL
            var url = idOrUrl;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = $"https://www.youtube.com/watch?v={idOrUrl}";

            // Show loading overlay while resolving manifest
            if (_titleLabel != null)
            {
                _titleLabel.Text = "Loading next video...";
                _titleLabel.Visible = true;
                UpdateTitleLabelPosition();
            }

            // Get video metadata (to show title) and resolve stream manifest
            var videoMeta = await _yt.Videos.GetAsync(url);
            var manifest = await _yt.Videos.Streams.GetManifestAsync(url);

            // Show title overlay (replace loading)
            if (_titleLabel != null)
            {
                _titleLabel.Text = videoMeta?.Title ?? string.Empty;
                _titleLabel.Visible = !string.IsNullOrWhiteSpace(_titleLabel.Text);
                UpdateTitleLabelPosition();
            }

            // Prefer highest-quality video-only stream and attach best audio-only stream as input slave.
            // Fall back to a muxed stream if no video-only streams are available.
            IStreamInfo videoStream = null;
            IStreamInfo audioStream = null;

            try
            {
                videoStream = manifest.GetVideoOnlyStreams().OrderByDescending(s => s.Bitrate).FirstOrDefault();
                audioStream = manifest.GetAudioOnlyStreams().OrderByDescending(s => s.Bitrate).FirstOrDefault();
            }
            catch
            {
                // If API throws for any reason, leave streams null and fallback to muxed below
                videoStream = null;
                audioStream = null;
            }

            string videoUrl = null;
            string audioUrl = null;

            if (videoStream != null)
            {
                videoUrl = videoStream.Url;
                audioUrl = audioStream?.Url;
            }
            else
            {
                // No separate video stream available; try highest-quality muxed stream
                try
                {
                    var muxed = manifest.GetMuxedStreams().OrderByDescending(s => s.Bitrate).FirstOrDefault();
                    if (muxed == null)
                        throw new Exception("Kein abspielbarer (muxed) Stream gefunden.");

                    videoUrl = muxed.Url;
                }
                catch
                {
                    throw new Exception("Kein abspielbarer Stream gefunden.");
                }
            }

            // Stop current playback and set new media
            try
            {
                if (_mediaPlayer.State == VLCState.Playing)
                    _mediaPlayer?.Stop(); 

                _currentMedia?.Dispose();
            }
            catch { }

            _currentMedia = new Media(_libVLC, videoUrl, FromType.FromLocation);

            // If there is a separate audio stream, add it as an input-slave so libVLC plays video + audio together
            if (!string.IsNullOrEmpty(audioUrl))
            {
                // use input-slave option; ensure proper escaping if necessary
                _currentMedia.AddOption($":input-slave={audioUrl}");
            }

            _mediaPlayer.Play(_currentMedia);
        }

        #endregion

        #region Fullscreen

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _prevFormBorderStyle = FormBorderStyle;
                _prevWindowState = WindowState;
                _prevTopMost = TopMost;
                _prevBounds = Bounds;

                if (_controlPanel != null) _controlPanel.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                TopMost = true;
                _isFullscreen = true;
            }
            else
            {
                if (_controlPanel != null) _controlPanel.Visible = true;
                FormBorderStyle = _prevFormBorderStyle;
                WindowState = _prevWindowState;
                TopMost = _prevTopMost;
                Bounds = _prevBounds;
                _isFullscreen = false;
            }

            // Ensure title label position updates when toggling fullscreen
            UpdateTitleLabelPosition();
        }

        #endregion

        #region Keyboard Input

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
                ToggleFullscreen();
            else if (e.KeyCode == Keys.Escape && _isFullscreen)
                ToggleFullscreen();
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)
                AdjustVolume(5); // Increase volume by 5%
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)
                AdjustVolume(-5); // Decrease volume by 5%
            else if (e.KeyCode == Keys.M)
                ToggleMute();
        }

        #endregion

        #region Cleanup

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _volumeDisplayTimer?.Dispose();
                _bufferingTimeoutTimer?.Dispose();
                _bufferingAnimationTimer?.Dispose();
                _mediaPlayer?.Stop();
                _currentMedia?.Dispose();
                _mediaPlayer?.Dispose();
                _libVLC?.Dispose();
                _yt?.Dispose();
            }
            catch { }
        }

        #endregion
    }
}
