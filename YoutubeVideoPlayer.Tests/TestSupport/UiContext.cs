using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// A single-threaded <see cref="SynchronizationContext"/> that stands in for the WinForms UI thread.
/// The managers capture <see cref="SynchronizationContext.Current"/> and post every event and timer tick
/// onto it, so running a test inside <see cref="Run(Func{Task}, bool, TimeSpan?)"/> reproduces the app's
/// threading model: everything after an <c>await</c> runs on one thread, one callback at a time.
/// </summary>
/// <remarks>
/// Without <c>sta</c>, posted callbacks go to a queue that the test thread drains.
/// With <c>sta</c> (WinForms tests), they are delivered through the Win32 message queue, exactly like
/// <see cref="WindowsFormsSynchronizationContext"/> does in the app, so they also run while a modal
/// dialog has its own message loop.
/// </remarks>
public sealed class UiContext : SynchronizationContext
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private Control? _marshal;
    private int _threadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_marshal != null)
        {
            try
            {
                _marshal.BeginInvoke(d, state);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The test has finished and its thread is gone; late callbacks are dropped.
            }

            return;
        }

        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The test has finished; late callbacks (e.g. from disposed timers) are dropped.
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new NotSupportedException("Send from another thread is not supported in tests.");

        d(state);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs <paramref name="test"/> with a <see cref="UiContext"/> installed and processes posted
    /// callbacks until the test's task completes.
    /// </summary>
    /// <param name="sta">Run on a new STA thread with a Win32 message loop (needed for WinForms controls).</param>
    public static void Run(Func<Task> test, bool sta = false, TimeSpan? timeout = null)
    {
        if (!sta)
        {
            RunWithQueue(test, timeout ?? DefaultTimeout);
            return;
        }

        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunWithMessageLoop(test, timeout ?? DefaultTimeout);
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>Synchronous variant for tests that only need the context to construct objects.</summary>
    public static void Run(Action test, bool sta = false) => Run(() =>
    {
        test();
        return Task.CompletedTask;
    }, sta);

    private static void RunWithQueue(Func<Task> test, TimeSpan timeout)
    {
        var previous = Current;
        var context = new UiContext { _threadId = Environment.CurrentManagedThreadId };
        SetSynchronizationContext(context);

        try
        {
            var task = test();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);

            var stopwatch = Stopwatch.StartNew();
            while (!context._queue.IsCompleted)
            {
                if (context._queue.TryTake(out var item, 50))
                    item.Callback(item.State);
                else if (stopwatch.Elapsed > timeout)
                    throw new TimeoutException($"The test did not finish within {timeout.TotalSeconds:0} seconds.");
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    private static void RunWithMessageLoop(Func<Task> test, TimeSpan timeout)
    {
        var previous = Current;
        using var marshal = new Control();
        _ = marshal.Handle; // create the window that receives the posted callbacks on this thread
        var context = new UiContext { _threadId = Environment.CurrentManagedThreadId, _marshal = marshal };
        SetSynchronizationContext(context);

        // Safety net: if the test hangs (e.g. in a modal dialog or MessageBox), close this thread's windows
        // so the test fails instead of waiting for someone to click a button.
        var nativeThreadId = GetCurrentThreadId();
        var timedOut = false;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            timedOut = true;
            CloseWindowsOfThread(nativeThreadId);
        }, null, timeout, TimeSpan.FromSeconds(1));

        try
        {
            var task = test();
            while (!task.IsCompleted)
            {
                if (timedOut)
                    throw new TimeoutException($"The test did not finish within {timeout.TotalSeconds:0} seconds; its windows were closed.");

                Application.DoEvents();
                if (!task.IsCompleted)
                    Thread.Sleep(1);
            }

            task.GetAwaiter().GetResult();
            if (timedOut)
                throw new TimeoutException($"The test did not finish within {timeout.TotalSeconds:0} seconds; its windows were closed.");
        }
        finally
        {
            SetSynchronizationContext(previous);

            // Dispose forms a failed test left open on this thread (other test threads keep theirs).
            foreach (var form in Application.OpenForms.Cast<Form>().Where(f => !f.InvokeRequired).ToList())
                form.Dispose();
        }
    }

    /// <summary>
    /// Makes exceptions in WinForms callbacks propagate (and fail the test) instead of opening
    /// the "unhandled exception" dialog. Must run before the first window is created.
    /// </summary>
#pragma warning disable CA2255 // ModuleInitializer is the right tool here: it runs before any test creates a window.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ConfigureWinForms() =>
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

    private static void CloseWindowsOfThread(uint threadId)
    {
        const uint WM_CLOSE = 0x0010;
        EnumThreadWindows(threadId, (hwnd, _) =>
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
