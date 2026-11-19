using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class FrameClockTests
{
    [Fact]
    public void DeliversFramesOnTheUiThread_UntilStopped() => UiContext.Run(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var frames = 0;
        var otherThread = false;
        using var clock = new FrameClock(refreshes =>
        {
            frames += refreshes;
            otherThread |= Environment.CurrentManagedThreadId != uiThread;
        });

        clock.Start();
        Assert.True(clock.IsRunning);
        await Async.WaitUntilAsync(() => frames >= 10, because: "the clock should tick about once per display refresh");

        clock.Stop();
        Assert.False(clock.IsRunning);
        await Async.SettleAsync();
        var stoppedAt = frames;
        await Task.Delay(200);
        await Async.SettleAsync();

        Assert.Equal(stoppedAt, frames);
        Assert.False(otherThread);
        Assert.InRange(clock.FramesPerSecond, 20, 1000);
    });

    [Fact]
    public void ARestartedClock_TicksAgain() => UiContext.Run(async () =>
    {
        var frames = 0;
        using var clock = new FrameClock(refreshes => frames += refreshes);
        clock.Start();
        await Async.WaitUntilAsync(() => frames >= 3);
        clock.Stop();
        await Async.SettleAsync();

        var before = frames;
        clock.Start();

        await Async.WaitUntilAsync(() => frames >= before + 3);
    });

    [Fact]
    public void ADisposedClock_DoesNotStartAgain() => UiContext.Run(() =>
    {
        var clock = new FrameClock(_ => { });
        clock.Dispose();

        clock.Start();

        Assert.False(clock.IsRunning);
    });
}
