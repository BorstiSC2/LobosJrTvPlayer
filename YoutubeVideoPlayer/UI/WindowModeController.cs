using System;
using System.Drawing;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Switches a form between its normal state, fullscreen (maximized, topmost, no controls)
    /// and borderless (window shrunk to the video area, no border, no controls).
    /// </summary>
    public sealed class WindowModeController
    {
        private readonly Form _form;
        private readonly Control _videoArea;
        private readonly Control _controlPanel;

        private FormBorderStyle _fullscreenPrevBorderStyle;
        private FormWindowState _fullscreenPrevWindowState;
        private bool _fullscreenPrevTopMost;
        private Rectangle _fullscreenPrevBounds;

        private FormBorderStyle _borderlessPrevBorderStyle;
        private FormWindowState _borderlessPrevWindowState;
        private Rectangle _borderlessPrevBounds;

        public WindowModeController(Form form, Control videoArea, Control controlPanel)
        {
            _form = form;
            _videoArea = videoArea;
            _controlPanel = controlPanel;
        }

        public bool IsFullscreen { get; private set; }

        public bool IsBorderless { get; private set; }

        /// <summary>Raised after the window layout changed, so overlays can be repositioned.</summary>
        public event EventHandler? ModeChanged;

        public void ToggleFullscreen()
        {
            if (!IsFullscreen)
            {
                _fullscreenPrevBorderStyle = _form.FormBorderStyle;
                _fullscreenPrevWindowState = _form.WindowState;
                _fullscreenPrevTopMost = _form.TopMost;
                _fullscreenPrevBounds = _form.Bounds;

                _controlPanel.Visible = false;
                _form.FormBorderStyle = FormBorderStyle.None;
                _form.WindowState = FormWindowState.Maximized;
                _form.TopMost = true;
                IsFullscreen = true;
            }
            else
            {
                _controlPanel.Visible = true;
                _form.FormBorderStyle = _fullscreenPrevBorderStyle;
                _form.WindowState = _fullscreenPrevWindowState;
                _form.TopMost = _fullscreenPrevTopMost;
                _form.Bounds = _fullscreenPrevBounds;
                IsFullscreen = false;
            }

            ModeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleBorderless()
        {
            if (!IsBorderless)
                EnterBorderless();
            else
                ExitBorderless();

            ModeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Leaves fullscreen or borderless mode. Returns <c>true</c> if a mode was left.</summary>
        public bool LeaveCurrentMode()
        {
            if (IsFullscreen)
            {
                ToggleFullscreen();
                return true;
            }

            if (IsBorderless)
            {
                ToggleBorderless();
                return true;
            }

            return false;
        }

        private void EnterBorderless()
        {
            _borderlessPrevBorderStyle = _form.FormBorderStyle;
            _borderlessPrevWindowState = _form.WindowState;
            _borderlessPrevBounds = _form.Bounds;

            var videoBounds = _videoArea.RectangleToScreen(_videoArea.ClientRectangle);

            _form.WindowState = FormWindowState.Normal;
            _form.FormBorderStyle = FormBorderStyle.None;
            _form.Bounds = videoBounds;

            _controlPanel.Visible = false;
            _videoArea.Dock = DockStyle.Fill;
            IsBorderless = true;
        }

        private void ExitBorderless()
        {
            _form.FormBorderStyle = _borderlessPrevBorderStyle;
            _form.WindowState = _borderlessPrevWindowState;
            _form.Bounds = _borderlessPrevBounds;

            _videoArea.Dock = DockStyle.Fill;
            _controlPanel.Visible = true;
            IsBorderless = false;
        }
    }
}
