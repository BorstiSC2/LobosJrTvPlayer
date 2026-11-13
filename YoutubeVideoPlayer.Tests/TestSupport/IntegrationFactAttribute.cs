namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// A test that talks to real external systems (YouTube, LibVLC natives). Skipped unless the environment
/// variable <c>YTP_INTEGRATION_TESTS</c> is set to <c>1</c>, so the normal test run stays offline and fast.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "YTP_INTEGRATION_TESTS";

    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            Skip = $"Integration test; set {EnvironmentVariable}=1 to run it.";
    }
}
