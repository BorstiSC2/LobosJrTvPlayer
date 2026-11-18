using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Calls back on the UI thread once per display refresh, right after the desktop compositor (DWM) has shown a frame,
    /// with the number of refreshes since the last call (more than 1 when the UI thread was busy).
    /// </summary>
    /// <remarks>
    /// A <see cref="System.Windows.Forms.Timer"/> is no good for animation: Windows timers tick every 15.6 ms, so an
    /// interval of 16 ms becomes 31 ms, and the ticks drift against the screen's refresh. Here a background thread waits
    /// in <c>DwmFlush</c> (which returns once per composition, i.e. per refresh) and posts each frame to the UI thread;
    /// frames the UI thread has not taken yet are added up rather than queued.
    /// </remarks>
    internal sealed class FrameClock : IDisposable
    {
        /// <summary>The refresh rate assumed until a few refreshes have been measured.</summary>
        internal const double DefaultFramesPerSecond = 60;

        private const int Samples = 31;

        private readonly Action<int> _frame;
        private readonly object _gate = new();
        private readonly double[] _periods = new double[Samples];
        private int _periodCount;
        private int _nextPeriod;

        private SynchronizationContext? _context;
        private Thread? _thread;
        private bool _running;
        private bool _disposed;
        private int _pendingFrames;
        private double _periodMs = 1000 / DefaultFramesPerSecond;

        /// <param name="frame">Called on the UI thread with the number of refreshes since the last call.</param>
        public FrameClock(Action<int> frame) => _frame = frame;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _running;
            }
        }

        /// <summary>The measured refresh rate of the display.</summary>
        public double FramesPerSecond => 1000 / Volatile.Read(ref _periodMs);

        /// <summary>Starts the callbacks; call on the UI thread.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_running || _disposed)
                    return;

                _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                _running = true;
                Interlocked.Exchange(ref _pendingFrames, 0);
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "Frame clock", Priority = ThreadPriority.AboveNormal };
                    _thread.Start();
                }

                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Stops the callbacks; the thread waits until the next <see cref="Start"/>.</summary>
        public void Stop()
        {
            lock (_gate)
                _running = false;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _running = false;
                Monitor.PulseAll(_gate);
            }
        }

        private void Run()
        {
            var watch = Stopwatch.StartNew();
            var last = 0.0;
            var hasLast = false;
            while (true)
            {
                SynchronizationContext context;
                lock (_gate)
                {
                    if (!_running && !_disposed)
                    {
                        hasLast = false;
                        while (!_running && !_disposed)
                            Monitor.Wait(_gate);
                    }

                    if (_disposed)
                        return;

                    context = _context!;
                }

                WaitForRefresh(watch);
                var now = watch.Elapsed.TotalMilliseconds;
                var refreshes = hasLast ? CountRefreshes(now - last) : 1;
                last = now;
                hasLast = true;

                // Only the first frame of a batch is posted; the UI thread takes all that came in until then.
                if (Interlocked.Add(ref _pendingFrames, refreshes) == refreshes)
                    context.Post(_ => Deliver(), null);
            }
        }

        private void Deliver()
        {
            var frames = Interlocked.Exchange(ref _pendingFrames, 0);
            if (frames > 0 && IsRunning)
                _frame(frames);
        }

        private static void WaitForRefresh(Stopwatch watch)
        {
            var started = watch.Elapsed.TotalMilliseconds;

            // DwmFlush blocks until the next composition. Without a compositor, or if it returns without blocking at
            // all, fall back to a short sleep (a timer tick, about 15 ms), so the thread never spins.
            if (DwmFlush() < 0 || watch.Elapsed.TotalMilliseconds - started < 0.3)
                Thread.Sleep(1);
        }

        /// <summary>Learns the refresh period (the median of recent intervals) and says how many refreshes one was.</summary>
        private int CountRefreshes(double intervalMs)
        {
            if (intervalMs is > 2 and < 100)
            {
                _periods[_nextPeriod] = intervalMs;
                _nextPeriod = (_nextPeriod + 1) % Samples;
                _periodCount = Math.Min(Samples, _periodCount + 1);

                if (_periodCount >= 5)
                {
                    var sorted = new double[_periodCount];
                    Array.Copy(_periods, sorted, _periodCount);
                    Array.Sort(sorted);
                    Volatile.Write(ref _periodMs, sorted[_periodCount / 2]);
                }
            }

            return Math.Max(1, (int)Math.Round(intervalMs / Volatile.Read(ref _periodMs)));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}
