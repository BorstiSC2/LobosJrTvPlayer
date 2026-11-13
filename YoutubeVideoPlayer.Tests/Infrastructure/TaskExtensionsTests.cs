using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;

namespace YouTubeVideoPlayer.Tests.Infrastructure;

[Collection(LoggerCollection.Name)]
public class TaskExtensionsTests
{
    [Fact]
    public void FireAndForget_LogsAFailureWithTheGivenContext() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();

        Task.FromException(new InvalidOperationException("boom")).FireAndForget("Doing work failed.");
        await Async.SettleAsync();

        Assert.Contains("Doing work failed. - boom", log.Content);
    });

    [Fact]
    public void FireAndForget_TreatsCancellationAsNormalEnd() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();

        Task.FromCanceled(new CancellationToken(canceled: true)).FireAndForget("Should not be logged.");
        await Async.SettleAsync();

        Assert.DoesNotContain("Should not be logged.", log.Content);
    });

    [Fact]
    public void FireAndForget_DoesNotLogASuccessfulTask() => UiContext.Run(async () =>
    {
        using var log = new RedirectedLog();
        var completion = new TaskCompletionSource();

        completion.Task.FireAndForget("Should not be logged.");
        completion.SetResult();
        await Async.SettleAsync();

        Assert.Equal(string.Empty, log.Content);
    });
}
