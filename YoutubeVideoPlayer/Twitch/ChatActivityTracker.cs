using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Keeps a rolling average of the unique chatters: time is cut into buckets of <see cref="BucketLength"/>
    /// (counted from the moment the tracker was created, i.e. the application start), each bucket counts the
    /// different users that wrote anything, and <see cref="AverageUniqueChatters"/> is the mean of the completed
    /// buckets within the last <see cref="Window"/>.
    /// </summary>
    /// <remarks>
    /// Must be used on the UI thread (<see cref="IChatChannel.MessageReceived"/> is raised there). A bucket nobody
    /// wrote in counts as zero; buckets from before the start do not exist, so they cannot lower the average.
    /// </remarks>
    public sealed class ChatActivityTracker : IDisposable
    {
        private readonly IChatChannel _chat;
        private readonly TimeProvider _time;
        private readonly DateTimeOffset _start;
        private readonly long _bucketsInWindow;
        private readonly Dictionary<long, HashSet<string>> _buckets = new();

        /// <param name="window">How far back the average reaches.</param>
        /// <param name="bucketLength">The length of one bucket; the window holds <c>window / bucketLength</c> of them (at least one).</param>
        public ChatActivityTracker(IChatChannel chat, TimeSpan window, TimeSpan bucketLength, TimeProvider? timeProvider = null)
        {
            if (bucketLength <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(bucketLength));

            _chat = chat;
            _time = timeProvider ?? TimeProvider.System;
            _start = _time.GetUtcNow();
            Window = window;
            BucketLength = bucketLength;
            _bucketsInWindow = Math.Max(1, window.Ticks / bucketLength.Ticks);

            _chat.MessageReceived += OnMessageReceived;
        }

        public TimeSpan Window { get; }

        public TimeSpan BucketLength { get; }

        /// <summary>
        /// The mean number of different chatters per bucket over the completed buckets of the last <see cref="Window"/>.
        /// Until the first bucket is complete, the number of chatters so far.
        /// </summary>
        public double AverageUniqueChatters
        {
            get
            {
                var current = CurrentIndex();
                if (current == 0)
                    return CountOf(0);

                var first = Math.Max(0, current - _bucketsInWindow);
                long sum = 0;
                for (var i = first; i < current; i++)
                    sum += CountOf(i);

                return (double)sum / (current - first);
            }
        }

        private void OnMessageReceived(object? sender, ChatMessageInfo message)
        {
            var key = string.IsNullOrEmpty(message.UserId) ? message.Username : message.UserId;
            if (string.IsNullOrEmpty(key))
                return;

            var current = CurrentIndex();
            if (!_buckets.TryGetValue(current, out var users))
            {
                users = new HashSet<string>(StringComparer.Ordinal);
                _buckets[current] = users;
                Prune(current);
            }

            users.Add(key);
        }

        private long CurrentIndex()
        {
            var elapsed = _time.GetUtcNow() - _start;
            return elapsed <= TimeSpan.Zero ? 0 : elapsed.Ticks / BucketLength.Ticks;
        }

        private int CountOf(long index) => _buckets.TryGetValue(index, out var users) ? users.Count : 0;

        private void Prune(long current)
        {
            foreach (var old in _buckets.Keys.Where(i => i < current - _bucketsInWindow).ToList())
                _buckets.Remove(old);
        }

        public void Dispose() => _chat.MessageReceived -= OnMessageReceived;
    }
}
