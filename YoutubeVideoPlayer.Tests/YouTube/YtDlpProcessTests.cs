using System.Diagnostics;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.Tests.YouTube;

/// <summary><see cref="YtDlpProcess"/> with real processes (cmd.exe stands in for yt-dlp.exe; no network).</summary>
public class YtDlpProcessTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task CollectsOutput_Errors_AndTheExitCode()
    {
        var output = await new YtDlpProcess(Cmd).RunAsync(
            new[] { "/c", "echo hello& echo oops 1>&2& exit 3" }, CancellationToken.None);

        Assert.Equal(3, output.ExitCode);
        Assert.Equal("hello", output.StandardOutput.Trim());
        Assert.Equal("oops", output.StandardError.Trim());
    }

    [Fact]
    public async Task TextInTheWindowsCodePage_IsReadAsSuch()
    {
        // What the standalone yt-dlp.exe writes for "you’re" (Windows-1252: ’ = 92), which read as UTF-8 became "you�re".
        var bytes = new byte[] { 0x79, 0x6F, 0x75, 0x92, 0x72, 0x65 };
        var file = Path.Combine(Path.GetTempPath(), "ytp-tests-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllBytesAsync(file, bytes);
        try
        {
            var output = await new YtDlpProcess(Cmd).RunAsync(new[] { "/c", "type", file, "1>&2" }, CancellationToken.None);

            Assert.Equal(YtDlpProcess.AnsiCodePage.Value.GetString(bytes), output.StandardError);
            if (YtDlpProcess.AnsiCodePage.Value.CodePage == 1252)
                Assert.Equal("you’re", output.StandardError);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Decode_ReadsUtf8AsUtf8_AndAnythingElseInTheWindowsCodePage()
    {
        Assert.Equal("you’re ä", YtDlpProcess.Decode(System.Text.Encoding.UTF8.GetBytes("you’re ä")));
        Assert.Equal("""{"title": "PokèSouls"}""", YtDlpProcess.Decode("""{"title": "PokèSouls"}"""u8.ToArray()));
        Assert.DoesNotContain('�', YtDlpProcess.Decode(new byte[] { 0x61, 0xE4, 0x92 }));
    }

    [Fact]
    public async Task Cancelling_EndsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new YtDlpProcess(Cmd).RunAsync(
            new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task AMissingExe_FailsWithItsPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "ytp-tests-" + Guid.NewGuid().ToString("N"), "yt-dlp.exe");

        var error = await Assert.ThrowsAsync<YtDlpException>(() => new YtDlpProcess(path).RunAsync(new[] { "--version" }, CancellationToken.None));

        Assert.Contains(path, error.Message);
    }
}
