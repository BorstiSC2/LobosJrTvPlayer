using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>How an avatar's picture is placed in its circle.</summary>
    /// <param name="Fit">
    /// <c>false</c> (cover): the picture fills the circle, what sticks out is cut off. <c>true</c>: the whole picture is
    /// inside the circle (for a picture that is round already).
    /// </param>
    /// <param name="Zoom">Scales the picture on top of that (1 = exactly cover or fit).</param>
    /// <param name="OffsetX">Moves the picture, in parts of the circle's diameter (positive: to the right).</param>
    /// <param name="OffsetY">Moves the picture, in parts of the circle's diameter (positive: down).</param>
    /// <param name="OwnFrame">The picture has its own round frame: no orange disc behind it.</param>
    public sealed record AvatarLook(bool Fit = false, double Zoom = 1, double OffsetX = 0, double OffsetY = 0, bool OwnFrame = false)
    {
        /// <summary>
        /// Covering the circle, a little smaller (0.9): a head that fills its canvas keeps its ears and paws inside the
        /// circle, and the orange shows in the corners (checked on a contact sheet of all the pictures at 40 and 80 px).
        /// </summary>
        public static readonly AvatarLook Default = new(Zoom: 0.9);
    }

    /// <summary>
    /// An animated GIF shown as a round avatar in the overlays' circle. Decoded once; every frame is cropped to the
    /// picture's content, scaled with high quality and masked with a smooth circle once per size, so drawing only copies
    /// a finished bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GIF transparency is on or off, so the edges of the picture itself are hard. Scaling down in premultiplied ARGB
    /// with a high-quality filter turns them into smooth edges; the circle mask is drawn anti-aliased too.
    /// </para>
    /// <para>
    /// The content box is the area of all frames' visible pixels together. Pictures are placed by it, not by their canvas,
    /// so a picture with a lot of empty space looks as big as one that fills its canvas.
    /// </para>
    /// </remarks>
    public sealed class AnimatedAvatar : IDisposable
    {
        /// <summary>Delays shorter than this are taken as <see cref="DefaultDelay"/>, as browsers do (0 ms means "as fast as possible").</summary>
        internal static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);

        internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

        private const int FrameDelayProperty = 0x5100;

        private readonly Bitmap[] _frames;
        private readonly TimeSpan[] _ends; // when each frame ends, from the start of a loop
        private readonly Dictionary<int, Bitmap[]> _round = new();
        private bool _disposed;

        private AnimatedAvatar(string name, Bitmap[] frames, TimeSpan[] delays, Rectangle content, AvatarLook look)
        {
            Name = name;
            _frames = frames;
            Delays = delays;
            Content = content;
            Look = look;

            _ends = new TimeSpan[delays.Length];
            var end = TimeSpan.Zero;
            for (var i = 0; i < delays.Length; i++)
            {
                end += delays[i];
                _ends[i] = end;
            }

            Duration = end;
        }

        public string Name { get; }

        public AvatarLook Look { get; }

        public int FrameCount => _frames.Length;

        public bool IsAnimated => _frames.Length > 1;

        /// <summary>How long each frame shows (at least <see cref="MinimumDelay"/>).</summary>
        public IReadOnlyList<TimeSpan> Delays { get; }

        /// <summary>One loop.</summary>
        public TimeSpan Duration { get; }

        /// <summary>The area of the canvas that any frame draws on.</summary>
        public Rectangle Content { get; }

        /// <summary>Reads a GIF (or any image GDI+ reads; a still image has one frame).</summary>
        /// <exception cref="Exception">The file cannot be read or is not an image.</exception>
        public static AnimatedAvatar Load(string path, AvatarLook? look = null)
        {
            // Read into memory first: Image.FromFile would keep the file locked.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream);
            var dimension = new FrameDimension(image.FrameDimensionsList[0]);
            var count = Math.Max(1, image.GetFrameCount(dimension));
            var delays = ReadDelays(image, count);

            var frames = new Bitmap[count];
            for (var i = 0; i < count; i++)
            {
                // GDI+ gives each frame as it looks on screen (the GIF's disposal of earlier frames applied).
                image.SelectActiveFrame(dimension, i);
                var frame = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(frame))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }

                frames[i] = frame;
            }

            var content = ContentBox(frames);
            return new AnimatedAvatar(Path.GetFileNameWithoutExtension(path), frames, delays, content, look ?? AvatarLook.Default);
        }

        private static TimeSpan[] ReadDelays(Image image, int count)
        {
            var delays = Enumerable.Repeat(DefaultDelay, count).ToArray();
            if (!image.PropertyIdList.Contains(FrameDelayProperty))
                return delays;

            var value = image.GetPropertyItem(FrameDelayProperty)?.Value;
            if (value == null)
                return delays;

            for (var i = 0; i < count && (i + 1) * 4 <= value.Length; i++)
            {
                var delay = TimeSpan.FromMilliseconds(BitConverter.ToInt32(value, i * 4) * 10); // hundredths of a second
                delays[i] = delay < MinimumDelay ? DefaultDelay : delay;
            }

            return delays;
        }

        /// <summary>The bounding box of the pixels any frame draws on (the whole canvas if none does).</summary>
        private static Rectangle ContentBox(Bitmap[] frames)
        {
            int width = frames[0].Width, height = frames[0].Height;
            int left = width, top = height, right = -1, bottom = -1;
            foreach (var frame in frames)
            {
                var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[data.Stride];
                    for (var y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                        for (var x = 0; x < width; x++)
                        {
                            if (row[x * 4 + 3] == 0)
                                continue;

                            left = Math.Min(left, x);
                            right = Math.Max(right, x);
                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                        }
                    }
                }
                finally
                {
                    frame.UnlockBits(data);
                }
            }

            return right < 0 ? new Rectangle(0, 0, width, height) : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        /// <summary>The frame that shows <paramref name="elapsed"/> after the animation started (it loops).</summary>
        public int FrameIndex(TimeSpan elapsed)
        {
            if (!IsAnimated || Duration <= TimeSpan.Zero)
                return 0;

            var within = TimeSpan.FromTicks(Math.Max(0, elapsed.Ticks) % Duration.Ticks);
            for (var i = 0; i < _ends.Length; i++)
            {
                if (within < _ends[i])
                    return i;
            }

            return _ends.Length - 1;
        }

        /// <summary>
        /// Frame <paramref name="index"/> as a round picture of <paramref name="diameter"/> pixels (transparent outside the
        /// circle). Rendered on first use per size and kept.
        /// </summary>
        public Bitmap Frame(int diameter, int index)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            diameter = Math.Max(1, diameter);
            if (!_round.TryGetValue(diameter, out var frames))
            {
                frames = _frames.Select(frame => Render(frame, diameter)).ToArray();
                _round[diameter] = frames;
            }

            return frames[Math.Clamp(index, 0, frames.Length - 1)];
        }

        private Bitmap Render(Bitmap frame, int diameter)
        {
            // Where the content goes: covering (or fitting into) the circle, zoomed and moved as the look says.
            var content = Content;
            var cover = Math.Max(diameter / (double)content.Width, diameter / (double)content.Height);
            var fit = Math.Min(diameter / (double)content.Width, diameter / (double)content.Height);
            var scale = (Look.Fit ? fit : cover) * Look.Zoom;
            var width = content.Width * scale;
            var height = content.Height * scale;
            var target = new RectangleF(
                (float)((diameter - width) / 2 + Look.OffsetX * diameter),
                (float)((diameter - height) / 2 + Look.OffsetY * diameter),
                (float)width,
                (float)height);

            using var scaled = new Bitmap(diameter, diameter, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(scaled))
            using (var attributes = new ImageAttributes())
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark seam where the filter reads past the edge
                g.DrawImage(frame, Rectangle.Round(target), content.X, content.Y, content.Width, content.Height, GraphicsUnit.Pixel, attributes);
            }

            // The circle: the scaled picture as a texture, filled into a smooth ellipse.
            var round = new Bitmap(diameter, diameter, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(round))
            using (var texture = new TextureBrush(scaled, WrapMode.Clamp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.FillEllipse(texture, 0, 0, diameter, diameter);
            }

            return round;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var frame in _frames)
                frame.Dispose();
            foreach (var frame in _round.Values.SelectMany(f => f))
                frame.Dispose();
            _round.Clear();
        }
    }
}
