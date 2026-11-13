using System.Diagnostics;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>Helpers for awaiting work that the code under test posts to the <see cref="UiContext"/>.</summary>
public static class Async
{
    /// <summary>
    /// Waits (yielding to the context, so posted callbacks can run) until <paramref name="condition"/> is true.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within " + timeoutMs + " ms" + (because == null ? "." : ": " + because));

            await Task.Delay(5);
        }
    }

    /// <summary>Lets everything that is currently queued on the context run (a few rounds deep).</summary>
    public static async Task SettleAsync(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
            await Task.Yield();

        // Give thread-pool work (Task.Run, file I/O) a moment to post its continuation, then drain again.
        await Task.Delay(20);
        for (var i = 0; i < rounds; i++)
            await Task.Yield();
    }
}
