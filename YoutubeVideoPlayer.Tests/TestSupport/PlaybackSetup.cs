using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// A <see cref="PlaybackManager"/> with substitutes for the player, YouTube and the video source,
/// and a fake clock. Must be created inside <see cref="UiContext.Run(Func{Task}, bool, TimeSpan?)"/>.
/// </summary>
public sealed class PlaybackSetup : IDisposable
{
    private readonly Queue<string> _nextIds = new();

    public PlaybackSetup()
    {
        Source.TryGetNextVideoId(null, out _).ReturnsForAnyArgs(call =>
        {
            NextRequests.Add((string?)call[0]);
            if (_nextIds.Count == 0)
            {
                call[1] = null;
                return false;
            }

            call[1] = _nextIds.Dequeue();
            return true;
        });

        // Every video resolves to "<id>.video" / "<id>.audio" unless configured otherwise.
        YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Resolved((string)call[0])));

        Manager = new PlaybackManager(YouTube, Source, Player, Time);
    }

    public FakeTimeProvider Time { get; } = new();

    /// <summary>The "video that the next one follows" that the manager passed on each request to the video source.</summary>
    public List<string?> NextRequests { get; } = new();

    public IMediaPlayer Player { get; } = Substitute.For<IMediaPlayer>();

    public IYouTubeService YouTube { get; } = Substitute.For<IYouTubeService>();

    public IVideoSource Source { get; } = Substitute.For<IVideoSource>();

    public PlaybackManager Manager { get; }

    public static ResolvedVideo Resolved(string id) =>
        new("Title " + id, TimeSpan.FromMinutes(3), id + ".video", id + ".audio");

    /// <summary>The video source hands out these IDs, in order; afterwards it is empty.</summary>
    public PlaybackSetup Queue(params string[] ids)
    {
        foreach (var id in ids)
            _nextIds.Enqueue(id);
        return this;
    }

    public void FailToResolve(string id, Exception? error = null) =>
        YouTube.ResolveAsync(id, Arg.Any<CancellationToken>()).ThrowsAsync(error ?? new HttpRequestException("unavailable"));

    /// <summary>The video URLs passed to <see cref="IMediaPlayer.Play"/>, in order.</summary>
    public List<string> PlayedVideos() => Player.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play))
        .Select(c => (string)c.GetArguments()[0]!)
        .ToList();

    /// <summary>Lets queued work run, moves the clock forward and lets the resulting work run.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await Async.SettleAsync();
        Time.Advance(by);
        await Async.SettleAsync();
    }

    /// <summary>Starts an operation and drives the one-second delay before a random video.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        var task = operation();
        await AdvanceAsync(PlaybackManager.DelayBeforeNextVideo);
        await task;
    }

    public void Dispose() => Manager.Dispose();
}
