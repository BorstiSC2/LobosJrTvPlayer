using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;

namespace YouTubeVideoPlayer.Tests.Infrastructure;

public class ContextTimerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void OneShotTimer_TicksOnceAfterInterval_OnTheContextThread() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var testThread = Environment.CurrentManagedThreadId;
        var ticks = new List<int>();
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: false,
            () => ticks.Add(Environment.CurrentManagedThreadId), time);

        timer.Start();
        Assert.True(timer.IsRunning);

        time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        await Async.SettleAsync();
        Assert.Empty(ticks);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Async.SettleAsync();
        Assert.Equal(new[] { testThread }, ticks);
        Assert.False(timer.IsRunning);

        time.Advance(Interval * 3);
        await Async.SettleAsync();
        Assert.Single(ticks);
    });

    [Fact]
    public void PeriodicTimer_TicksEveryInterval() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: true, () => ticks++, time);

        timer.Start();
        for (var i = 1; i <= 3; i++)
        {
            time.Advance(Interval);
            await Async.SettleAsync();
            Assert.Equal(i, ticks);
        }

        Assert.True(timer.IsRunning);
    });

    [Fact]
    public void Stop_PreventsFurtherTicks() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: true, () => ticks++, time);

        timer.Start();
        timer.Stop();
        time.Advance(Interval * 5);
        await Async.SettleAsync();

        Assert.Equal(0, ticks);
        Assert.False(timer.IsRunning);
    });

    [Fact]
    public void Stop_DiscardsATickThatWasAlreadyQueued() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: false, () => ticks++, time);

        timer.Start();
        time.Advance(Interval); // the tick is now posted to the context, but has not run yet
        timer.Stop();
        await Async.SettleAsync();

        Assert.Equal(0, ticks);
    });

    [Fact]
    public void Restart_DiscardsTheQueuedTickAndWaitsTheFullIntervalAgain() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: false, () => ticks++, time);

        timer.Start();
        time.Advance(Interval);
        timer.Start(); // restart before the queued tick ran
        await Async.SettleAsync();
        Assert.Equal(0, ticks);

        time.Advance(Interval);
        await Async.SettleAsync();
        Assert.Equal(1, ticks);
    });

    [Fact]
    public void StartWithDueTime_UsesTheGivenDelayForTheFirstTick() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: true, () => ticks++, time);

        timer.Start(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));
        await Async.SettleAsync();
        Assert.Equal(1, ticks);

        // Afterwards the regular interval applies.
        time.Advance(Interval - TimeSpan.FromSeconds(1));
        await Async.SettleAsync();
        Assert.Equal(1, ticks);
        time.Advance(TimeSpan.FromSeconds(1));
        await Async.SettleAsync();
        Assert.Equal(2, ticks);
    });

    [Fact]
    public void StartWithNegativeDueTime_TicksImmediately() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: false, () => ticks++, time);

        timer.Start(TimeSpan.FromSeconds(-5));
        time.Advance(TimeSpan.Zero);
        await Async.SettleAsync();

        Assert.Equal(1, ticks);
    });

    [Fact]
    public void StartWithHugeDueTime_IsClampedInsteadOfThrowing() => UiContext.Run(() =>
    {
        using var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: false, () => { }, new FakeTimeProvider());

        timer.Start(TimeSpan.FromDays(365));

        Assert.True(timer.IsRunning);
    });

    [Fact]
    public void Dispose_StopsTheTimerAndIgnoresStartAndStop() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var ticks = 0;
        var timer = new ContextTimer(SynchronizationContext.Current!, Interval, periodic: true, () => ticks++, time);
        timer.Start();
        time.Advance(Interval); // queued tick

        timer.Dispose();
        timer.Dispose(); // idempotent
        timer.Start();
        timer.Stop();
        time.Advance(Interval * 3);
        await Async.SettleAsync();

        Assert.Equal(0, ticks);
        Assert.False(timer.IsRunning);
    });

    [Fact]
    public void WorksWithTheSystemClockByDefault() => UiContext.Run(async () =>
    {
        var ticked = false;
        using var timer = new ContextTimer(SynchronizationContext.Current!, TimeSpan.FromMilliseconds(10), periodic: false, () => ticked = true);

        timer.Start();

        await Async.WaitUntilAsync(() => ticked);
    });
}
