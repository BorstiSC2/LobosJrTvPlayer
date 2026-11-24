using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// A control drawn over the video with real transparency: a layered child window (<c>WS_EX_LAYERED</c>) that is
    /// filled with <c>UpdateLayeredWindow</c> from a premultiplied 32-bit frame, so its pixels have their own alpha (smooth
    /// round edges, a translucent box over VLC's video window, which a normal child window cannot have).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subclass draws a frame in <see cref="DrawFrame"/> (on transparent, the size of the control) and calls
    /// <see cref="Present"/> whenever it looks different. The frame is a DIB section that GDI+ draws into directly, so a
    /// frame costs no copy or allocation.
    /// </para>
    /// <para>
    /// Layered child windows need Windows 8 or later and a manifest that declares it (<c>app.manifest</c>). Without one
    /// (or with <c>layered: false</c>) the window cannot be created as layered; it is then created as a normal window that
    /// paints the same frame on black. Like any hidden child control, the window is only created when it first shows.
    /// </para>
    /// <para>
    /// An overlay may show an <see cref="Avatar"/> (an animated GIF in its circle). While it is visible and the avatar has
    /// more than one frame, a <see cref="FrameClock"/> runs and the overlay is presented again whenever the avatar's frame
    /// changes (on the injected <see cref="TimeProvider"/>); it stops when the overlay is hidden.
    /// </para>
    /// </remarks>
    public abstract class LayeredOverlay : Control
    {
        private const int WS_EX_LAYERED = 0x80000;
        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_ALPHA = 1;

        private bool _layered;
        private OverlayStyle? _style;

        private IntPtr _dc;
        private IntPtr _dib;
        private IntPtr _oldBitmap;
        private Bitmap? _frame;

        private readonly TimeProvider _animationTime;
        private AnimatedAvatar? _avatar;
        private long _avatarStart;
        private int _avatarFrameShown = -1;
        private FrameClock? _avatarClock;

        /// <param name="layered"><c>false</c> makes a normal window, as where Windows does not allow a layered one.</param>
        /// <param name="animationTime">The clock the avatar plays on; <see cref="TimeProvider.System"/> by default.</param>
        protected LayeredOverlay(bool layered, TimeProvider? animationTime = null)
        {
            _layered = layered;
            _animationTime = animationTime ?? TimeProvider.System;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Visible = false;
        }

        /// <summary>Whether the window is layered (see-through); false where Windows does not allow it.</summary>
        internal bool IsLayered => _layered && IsHandleCreated;

        /// <summary>The animated picture in the circle; <c>null</c> draws the icon. A new one starts from its first frame.</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal AnimatedAvatar? Avatar
        {
            get => _avatar;
            set
            {
                if (ReferenceEquals(value, _avatar))
                    return;

                _avatar = value;
                _avatarStart = _animationTime.GetTimestamp();
                UpdateAvatarClock();
                Present();
            }
        }

        /// <summary>The avatar's frame for now.</summary>
        private protected int AvatarFrame => _avatar?.FrameIndex(_animationTime.GetElapsedTime(_avatarStart)) ?? 0;

        internal bool IsAvatarClockRunning => _avatarClock?.IsRunning == true;

        /// <summary>The avatar frame of the last presented picture (tests).</summary>
        internal int ShownAvatarFrame => _avatarFrameShown;

        /// <summary>Presents again if the avatar has moved on to another frame. The clock calls it; tests call it themselves.</summary>
        internal void TickAvatar()
        {
            if (_avatar != null && AvatarFrame != _avatarFrameShown)
                Present();
        }

        private void UpdateAvatarClock()
        {
            var run = Visible && !IsDisposed && _avatar is { IsAnimated: true };
            if (run)
            {
                _avatarClock ??= new FrameClock(_ =>
                {
                    TickAvatar();
                    if (!IsLayered)
                        Update();
                });
                _avatarClock.Start();
            }
            else
            {
                _avatarClock?.Stop();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateAvatarClock();
        }

        /// <summary>The look, for this display's DPI.</summary>
        private protected OverlayStyle Style => _style ??= new OverlayStyle(DeviceDpi);

        /// <summary>Draws what the control shows now, on transparent, in pixels of the control's size.</summary>
        private protected abstract void DrawFrame(Graphics g);

        /// <summary>A new DPI: the subclass lays itself out and renders again.</summary>
        private protected virtual void OnStyleChanged()
        {
        }

        /// <summary>Draws the frame and puts it on the screen.</summary>
        private protected void Present()
        {
            if (!IsHandleCreated)
                return; // OnHandleCreated presents the first frame

            var width = Width;
            var height = Height;
            if (width <= 0 || height <= 0)
            {
                ReleaseFrame();
                return;
            }

            if (_frame == null || _frame.Width != width || _frame.Height != height)
                CreateFrame(width, height);

            _avatarFrameShown = AvatarFrame;
            using (var g = Graphics.FromImage(_frame!))
            {
                g.Clear(Color.Transparent);
                DrawFrame(g);
            }

            if (IsLayered)
            {
                var size = new NativeSize { Width = width, Height = height };
                var source = new NativePoint();
                var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, _dc, ref source, 0, ref blend, ULW_ALPHA);
            }
            else
            {
                Invalidate();
            }
        }

        private void CreateFrame(int width, int height)
        {
            ReleaseFrame();
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down, like a GDI+ bitmap
                Planes = 1,
                BitCount = 32
            };
            _dib = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero)
                throw new Win32Exception();

            _dc = CreateCompatibleDC(IntPtr.Zero);
            _oldBitmap = SelectObject(_dc, _dib);
            _frame = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        private void ReleaseFrame()
        {
            _frame?.Dispose();
            _frame = null;
            if (_dc != IntPtr.Zero)
            {
                SelectObject(_dc, _oldBitmap);
                DeleteDC(_dc);
                _dc = IntPtr.Zero;
            }

            if (_dib != IntPtr.Zero)
            {
                DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (_layered)
                    parameters.ExStyle |= WS_EX_LAYERED;
                return parameters;
            }
        }

        protected override void CreateHandle()
        {
            if (_layered)
            {
                try
                {
                    base.CreateHandle();
                    return;
                }
                catch (Win32Exception)
                {
                    // Layered child windows need a manifest that declares Windows 8 or later (the app has one). Not
                    // logged: it is no error of the app.
                    _layered = false;
                }
            }

            base.CreateHandle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Present(); // a new window starts empty
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Present(); // a layered window must get a frame of its new size
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            _style?.Dispose();
            _style = null;
            OnStyleChanged();
            Present();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Only without a layered window; a layered one shows what Present gave it.
            e.Graphics.Clear(Color.Black);
            if (_frame is { } frame)
                e.Graphics.DrawImage(frame, new Rectangle(0, 0, frame.Width, frame.Height), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _avatarClock?.Dispose();
                _avatarClock = null;
                _style?.Dispose();
                _style = null;
            }

            ReleaseFrame();
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, IntPtr position, ref NativeSize size,
            IntPtr source, ref NativePoint sourcePosition, int colorKey, ref BlendFunction blend, int flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits,
            IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr item);
    }
}
