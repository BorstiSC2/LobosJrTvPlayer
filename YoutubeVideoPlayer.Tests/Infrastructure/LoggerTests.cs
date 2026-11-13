using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;

namespace YouTubeVideoPlayer.Tests.Infrastructure;

[Collection(LoggerCollection.Name)]
public class LoggerTests
{
    [Fact]
    public void Log_WritesTimestampedMessageWithSeparator()
    {
        using var log = new RedirectedLog();

        Logger.Log("Hello log");

        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] Hello log", log.Content);
        Assert.Contains(new string('-', 80), log.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Log_IgnoresEmptyMessages(string message)
    {
        using var log = new RedirectedLog();

        Logger.Log(message);

        Assert.False(File.Exists(log.Path));
    }

    [Fact]
    public void LogWithException_WritesTypeMessageStackTraceAndInnerException()
    {
        using var log = new RedirectedLog();
        Exception exception;
        try
        {
            throw new InvalidOperationException("outer problem", new TimeoutException("inner problem"));
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        Logger.Log("While testing", exception);

        var content = log.Content;
        Assert.Contains("System.InvalidOperationException", content);
        Assert.Contains("Message: While testing - outer problem", content);
        Assert.Contains("StackTrace:", content);
        Assert.Contains(nameof(LogWithException_WritesTypeMessageStackTraceAndInnerException), content);
        Assert.Contains("Inner Exception:", content);
        Assert.Contains("inner problem", content);
    }

    [Fact]
    public void LogWithNullException_WritesNothing()
    {
        using var log = new RedirectedLog();

        Logger.Log("only a message", null);

        Assert.False(File.Exists(log.Path));
    }

    [Fact]
    public void Clear_EmptiesAnExistingLog_AndIgnoresAMissingOne()
    {
        using var log = new RedirectedLog();
        Logger.Clear(); // no file yet
        Assert.False(File.Exists(log.Path));

        Logger.Log("something");
        Logger.Clear();

        Assert.True(File.Exists(log.Path));
        Assert.Equal(string.Empty, log.Content);
    }

    [Fact]
    public void LogAndClear_DoNotThrowWhileAnotherProgramHoldsTheFile()
    {
        // The log is written from catch blocks and async void handlers: an exception there would end the application.
        using var log = new RedirectedLog();
        File.WriteAllText(log.Path, "existing" + Environment.NewLine);

        using (new FileStream(log.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Logger.Log("while locked");
            Logger.Log("while locked", new InvalidOperationException("while locked"));
            Logger.Clear();
        }

        Assert.Equal("existing" + Environment.NewLine, log.Content);

        Logger.Log("after");
        Assert.Contains("after", log.Content);
    }

    [Fact]
    public void Log_DoesNotThrowIfThePathIsNotAFile()
    {
        using var log = new RedirectedLog();
        Directory.CreateDirectory(log.Path);

        Logger.Log("nowhere to go");
        Logger.Log("nowhere to go", new InvalidOperationException("nowhere to go"));
        Logger.Clear();
    }

    [Fact]
    public void Log_StopsWritingOnceTheFileReachesFiveMegabytes()
    {
        using var log = new RedirectedLog();
        File.WriteAllBytes(log.Path, new byte[5 * 1024 * 1024]);

        Logger.Log("too late");
        Logger.Log("too late", new Exception("too late"));

        Assert.Equal(5 * 1024 * 1024, new FileInfo(log.Path).Length);
    }
}
