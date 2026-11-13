using System;
using System.Threading;

namespace YouTubeVideoPlayer.Infrastructure
{
    /// <summary>
    /// A timer whose ticks run on a given <see cref="SynchronizationContext"/> (normally the UI thread).
    /// Unlike a raw <see cref="System.Threading.Timer"/>, a tick that was already queued when
    /// <see cref="Stop"/> or <see cref="Start()"/> was called is discarded.
    /// <see cref="Start()"/> and <see cref="Stop"/> must be called on that context.
    /// </summary>
    public sealed class ContextTimer : IDisposable
    {
        // ITimer accepts at most 0xFFFFFFFE milliseconds.
        private static readonly TimeSpan MaxDueTime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private readonly SynchronizationContext _context;
        private readonly Action _tick;
        private readonly TimeSpan _interval;
        private readonly bool _periodic;
        private readonly ITimer _timer;
        private int _generation;
        private bool _disposed;

        /// <param name="interval">Time until the first tick of <see cref="Start()"/> and between periodic ticks.</param>
        /// <param name="timeProvider">The clock to use; <see cref="TimeProvider.System"/> by default.</param>
        public ContextTimer(SynchronizationContext context, TimeSpan interval, bool periodic, Action tick, TimeProvider? timeProvider = null)
        {
            _context = context;
            _interval = interval;
            _periodic = periodic;
            _tick = tick;
            _timer = (timeProvider ?? TimeProvider.System)
                .CreateTimer(OnElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public bool IsRunning { get; private set; }

        /// <summary>Starts (or restarts) the timer.</summary>
        public void Start() => Start(_interval);

        /// <summary>Starts (or restarts) the timer with a custom delay before the first tick.</summary>
        public void Start(TimeSpan dueTime)
        {
            if (_disposed)
                return;

            if (dueTime < TimeSpan.Zero)
                dueTime = TimeSpan.Zero;
            else if (dueTime > MaxDueTime)
                dueTime = MaxDueTime;

            Interlocked.Increment(ref _generation);
            IsRunning = true;
            _timer.Change(dueTime, _periodic ? _interval : Timeout.InfiniteTimeSpan);
        }

        public void Stop()
        {
            if (_disposed)
                return;

            Interlocked.Increment(ref _generation);
            IsRunning = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private void OnElapsed(object? state)
        {
            var generation = Volatile.Read(ref _generation);
            _context.Post(_ =>
            {
                if (_disposed || !IsRunning || generation != Volatile.Read(ref _generation))
                    return;

                if (!_periodic)
                    IsRunning = false;

                _tick();
            }, null);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            IsRunning = false;
            _timer.Dispose();
        }
    }
}
