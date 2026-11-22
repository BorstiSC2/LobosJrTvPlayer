using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Keeps the video area of the main window at 16:9 (<see cref="VideoWindowGeometry"/>); the control bar below the
    /// video is not part of the ratio. Hooks the form's window messages:
    /// <list type="bullet">
    /// <item>dragging an edge or corner (<c>WM_SIZING</c>) scales the window, the opposite edges stay;</item>
    /// <item>any other size change (Aero Snap, code) keeps the width and corrects the height (<c>WM_WINDOWPOSCHANGING</c>);</item>
    /// <item>Maximize (button, title bar double-click) is the largest 16:9 window on the monitor instead of a real
    /// maximize, and pressing it again restores the window from before. A maximize that does not go through the
    /// Maximize command (dragging to the top edge, Win+Up) is turned into the same afterwards.</item>
    /// </list>
    /// Fullscreen is left alone (it fills the monitor), and so is every switch of <see cref="WindowModeController"/>,
    /// whose intermediate states are not meant to be corrected.
    /// </summary>
    public sealed class AspectRatioLock : NativeWindow
    {
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_SIZING = 0x0214;
        private const int SC_MAXIMIZE = 0xF030;
        private const uint SWP_NOSIZE = 0x0001;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>The invisible resize border is a few pixels; anything larger is a coordinate mix-up (DPI virtualization).</summary>
        private const int MaxInvisibleFrame = 32;

        private readonly Form _form;
        private readonly Control _controlPanel;
        private readonly WindowModeController _modes;

        private Rectangle? _boundsBeforeFit;
        private Rectangle _lastFit;
        private bool _maximizeFixPending;

        public AspectRatioLock(Form form, Control controlPanel, WindowModeController modes)
        {
            _form = form;
            _controlPanel = controlPanel;
            _modes = modes;
            WorkArea = () => Screen.FromControl(_form).WorkingArea;

            _form.HandleCreated += (_, _) => AssignHandle(_form.Handle);
            if (_form.IsHandleCreated)
                AssignHandle(_form.Handle);
            _form.Resize += OnFormResize;
        }

        /// <summary>The work area <see cref="ToggleFit"/> fits the window into (tests use an off-screen one).</summary>
        public Func<Rectangle> WorkArea { get; set; }

        /// <summary>The window size minus the video: border, title bar and (outside borderless mode) the control bar.</summary>
        public Size Extra
        {
            get
            {
                var chrome = _form.Size - _form.ClientSize;
                // Not _controlPanel.Visible: that is false as long as the form itself is not shown yet.
                var bar = _modes.IsBorderless || _modes.IsFullscreen ? 0 : _controlPanel.Height;
                return new Size(chrome.Width, chrome.Height + bar);
            }
        }

        private int MinVideoWidth => VideoWindowGeometry.MinVideoWidth * _form.DeviceDpi / 96;

        private bool IsLocked =>
            !_modes.IsFullscreen && !_modes.IsSwitching && _form.IsHandleCreated
            && !NativeMethods.IsIconic(_form.Handle) && !NativeMethods.IsZoomed(_form.Handle);

        /// <summary>
        /// The largest 16:9 window on the monitor (from <see cref="WorkArea"/>); if the window is still where the last
        /// call put it, back to where it was before.
        /// </summary>
        public void ToggleFit()
        {
            if (_form.WindowState != FormWindowState.Normal)
                _form.WindowState = FormWindowState.Normal;

            if (_boundsBeforeFit is { } previous && _form.Bounds == _lastFit)
            {
                _boundsBeforeFit = null;
                _form.Bounds = previous;
                return;
            }

            var before = _form.Bounds;
            FitToWorkArea(WorkArea());
            _boundsBeforeFit = before;
        }

        /// <summary>Sets the window to the largest 16:9 window whose visible frame fits into <paramref name="workArea"/>, centered.</summary>
        public void FitToWorkArea(Rectangle workArea)
        {
            _form.Bounds = VideoWindowGeometry.Fit(workArea, Extra, InvisibleFrame());
            _lastFit = _form.Bounds;
            _boundsBeforeFit = null;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_SYSCOMMAND when ((int)(long)m.WParam & 0xFFF0) == SC_MAXIMIZE && !_modes.IsFullscreen && !_modes.IsSwitching:
                    ToggleFit();
                    return;

                case WM_SIZING when IsLocked:
                {
                    var proposed = Marshal.PtrToStructure<RECT>(m.LParam).ToRectangle();
                    var adjusted = VideoWindowGeometry.AdjustDrag(_form.Bounds, proposed, (int)(long)m.WParam, Extra, MinVideoWidth);
                    Marshal.StructureToPtr(RECT.From(adjusted), m.LParam, false);
                    break;
                }

                case WM_WINDOWPOSCHANGING when IsLocked:
                {
                    var position = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                    if ((position.flags & SWP_NOSIZE) == 0)
                    {
                        var corrected = VideoWindowGeometry.NormalizeSize(
                            new Rectangle(position.x, position.y, position.cx, position.cy), Extra);
                        if (corrected.Height != position.cy)
                        {
                            position.cy = corrected.Height;
                            Marshal.StructureToPtr(position, m.LParam, false);
                        }
                    }

                    break;
                }
            }

            base.WndProc(ref m);
        }

        /// <summary>A maximize that did not come as the Maximize command: turn it into the largest 16:9 window.</summary>
        private void OnFormResize(object? sender, EventArgs e)
        {
            if (_maximizeFixPending || !IsUnwantedMaximize())
                return;

            _maximizeFixPending = true;
            _form.BeginInvoke(() =>
            {
                _maximizeFixPending = false;
                if (!IsUnwantedMaximize())
                    return;

                var before = _form.RestoreBounds;
                _form.WindowState = FormWindowState.Normal;
                FitToWorkArea(WorkArea());
                _boundsBeforeFit = before;
            });
        }

        private bool IsUnwantedMaximize() =>
            !_form.IsDisposed && _form.WindowState == FormWindowState.Maximized && !_modes.IsFullscreen && !_modes.IsSwitching;

        /// <summary>
        /// The invisible resize border around the visible frame (Windows 10/11), so the visible frame can fill the work
        /// area. Empty when it cannot be measured.
        /// </summary>
        private Padding InvisibleFrame()
        {
            if (!_form.IsHandleCreated || _form.FormBorderStyle == FormBorderStyle.None)
                return Padding.Empty;

            if (NativeMethods.DwmGetWindowAttribute(_form.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0
                || !NativeMethods.GetWindowRect(_form.Handle, out var window))
            {
                return Padding.Empty;
            }

            var margins = new Padding(frame.Left - window.Left, frame.Top - window.Top, window.Right - frame.Right, window.Bottom - frame.Bottom);
            return margins.Left is < 0 or > MaxInvisibleFrame || margins.Top is < 0 or > MaxInvisibleFrame
                || margins.Right is < 0 or > MaxInvisibleFrame || margins.Bottom is < 0 or > MaxInvisibleFrame
                ? Padding.Empty
                : margins;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);

            public static RECT From(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern bool IsZoomed(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
        }
    }
}
