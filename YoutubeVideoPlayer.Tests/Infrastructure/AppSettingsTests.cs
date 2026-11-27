using Newtonsoft.Json.Linq;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.Infrastructure;

public sealed class AppSettingsTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private string FilePath => _folder.File(AppSettings.FileName);

    private AppSettings LoadFrom(string? json)
    {
        if (json != null)
            File.WriteAllText(FilePath, json);
        return AppSettings.Load(_folder.Path);
    }

    private JObject Saved() => JObject.Parse(File.ReadAllText(FilePath));

    [Fact]
    public void WithoutAFile_TheDefaultsAreUsed_AndWrittenWithEverySetting()
    {
        var settings = LoadFrom(null);

        Assert.Equal(YouTubeBackend.YoutubeExplode, settings.YouTube.SelectedBackend);
        Assert.True(settings.YouTube.Fallback);
        Assert.Equal("yt-dlp.exe", settings.YouTube.YtDlp.Path);
        Assert.Empty(settings.YouTube.YtDlp.ExtraArguments);
        Assert.True(settings.YouTube.YtDlp.CheckForUpdates);

        Assert.True(JToken.DeepEquals(JObject.Parse("""
            {
              "version": 1,
              "youtube": {
                "backend": "youtubeExplode",
                "fallback": true,
                "ytDlp": { "path": "yt-dlp.exe", "extraArguments": [], "checkForUpdates": true }
              }
            }
            """), Saved()), Saved().ToString());
    }

    [Theory]
    [InlineData("ytDlp", YouTubeBackend.YtDlp)]
    [InlineData("YTDLP", YouTubeBackend.YtDlp)]
    [InlineData("yt-dlp", YouTubeBackend.YtDlp)]
    [InlineData("youtubeExplode", YouTubeBackend.YoutubeExplode)]
    [InlineData("YoutubeExplode", YouTubeBackend.YoutubeExplode)]
    [InlineData("vlc", YouTubeBackend.YoutubeExplode)]
    [InlineData("", YouTubeBackend.YoutubeExplode)]
    public void TheBackend_IsReadWhateverItsCase_AndAnUnknownOneMeansYoutubeExplode(string value, YouTubeBackend expected)
    {
        var settings = LoadFrom($$"""{ "youtube": { "backend": "{{value}}" } }""");

        Assert.Equal(expected, settings.YouTube.SelectedBackend);
        Assert.Equal(value, (string)Saved()["youtube"]!["backend"]!); // the file keeps what the user wrote
    }

    [Fact]
    public void MissingSettings_AreAdded_AndTheFilesOwnValuesAndNotesAreKept()
    {
        var settings = LoadFrom("""{ "_note": "mine", "youtube": { "fallback": false, "ytDlp": { "path": "tools\\yt-dlp.exe" } } }""");

        Assert.False(settings.YouTube.Fallback);
        Assert.Equal(@"tools\yt-dlp.exe", settings.YouTube.YtDlp.Path);
        var saved = Saved();
        Assert.Equal(new[] { "version", "youtube", "_note" }, saved.Properties().Select(p => p.Name));
        Assert.Equal(new[] { "path", "extraArguments", "checkForUpdates" }, ((JObject)saved["youtube"]!["ytDlp"]!).Properties().Select(p => p.Name));
        Assert.False((bool)saved["youtube"]!["fallback"]!);
    }

    [Fact]
    public void ASectionSetToNull_GetsTheDefaults()
    {
        var settings = LoadFrom("""{ "version": 1, "youtube": null }""");

        Assert.Equal(YouTubeBackend.YoutubeExplode, settings.YouTube.SelectedBackend);
        Assert.Equal("yt-dlp.exe", settings.YouTube.YtDlp.Path);
    }

    [Fact]
    public void ExtraArguments_AreTrimmed_AndEmptyOnesDropped_AndAnEmptyPathIsTheDefault()
    {
        var settings = LoadFrom("""{ "youtube": { "ytDlp": { "path": "  ", "extraArguments": [ "--cookies-from-browser", " firefox ", "", null ] } } }""");

        Assert.Equal(new[] { "--cookies-from-browser", "firefox" }, settings.YouTube.YtDlp.ExtraArguments);
        Assert.Equal("yt-dlp.exe", settings.YouTube.YtDlp.Path);
    }

    [Fact]
    public void ACompleteFile_IsNotWrittenAgain()
    {
        const string json = """{"version":1,"youtube":{"backend":"ytDlp","fallback":true,"ytDlp":{"path":"yt-dlp.exe","extraArguments":[],"checkForUpdates":false}}}""";

        var settings = LoadFrom(json);

        Assert.Equal(YouTubeBackend.YtDlp, settings.YouTube.SelectedBackend);
        Assert.False(settings.YouTube.YtDlp.CheckForUpdates);
        Assert.Equal(json, File.ReadAllText(FilePath)); // not reformatted
    }

    [Theory]
    [InlineData("{ this is not JSON")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("""{ "youtube": { "fallback": "sometimes" } }""")]
    public void AFileThatCannotBeUnderstood_GivesTheDefaults_AndStaysAsItIs(string json)
    {
        var settings = LoadFrom(json);

        Assert.Equal(YouTubeBackend.YoutubeExplode, settings.YouTube.SelectedBackend);
        Assert.True(settings.YouTube.Fallback);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void ALockedFile_GivesTheDefaults_AndIsNotReplaced()
    {
        const string json = """{ "youtube": { "backend": "ytDlp" } }""";
        File.WriteAllText(FilePath, json);

        AppSettings settings;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            settings = AppSettings.Load(_folder.Path);

        Assert.Equal(YouTubeBackend.YoutubeExplode, settings.YouTube.SelectedBackend);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void AFileOfANewerVersion_IsReadButNotChanged()
    {
        const string json = """{ "version": 7, "youtube": { "backend": "ytDlp" } }""";

        var settings = LoadFrom(json);

        Assert.Equal(YouTubeBackend.YtDlp, settings.YouTube.SelectedBackend);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }
}
