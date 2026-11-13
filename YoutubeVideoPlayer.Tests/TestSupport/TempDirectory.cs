namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>A unique temporary folder that is deleted again when disposed.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ytp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open briefly; leftovers in %TEMP% are harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
