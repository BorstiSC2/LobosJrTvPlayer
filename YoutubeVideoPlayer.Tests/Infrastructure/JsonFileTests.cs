using Newtonsoft.Json;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Tests.TestSupport;

namespace YouTubeVideoPlayer.Tests.Infrastructure;

public class JsonFileTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;

        public List<int> Numbers { get; set; } = new();
    }

    [Fact]
    public void Read_ReturnsNullForAMissingFile()
    {
        using var dir = new TempDirectory();

        Assert.Null(JsonFile.Read<Sample>(dir.File("missing.json")));
    }

    [Fact]
    public void WriteAtomic_ThenRead_RoundTrips_AndCreatesTheFolder()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "sub", "folder", "data.json");

        JsonFile.WriteAtomic(path, new Sample { Name = "x", Numbers = { 1, 2, 3 } });
        var read = JsonFile.Read<Sample>(path);

        Assert.NotNull(read);
        Assert.Equal("x", read!.Name);
        Assert.Equal(new[] { 1, 2, 3 }, read.Numbers);
    }

    [Fact]
    public void WriteAtomic_ReplacesAnExistingFile_AndLeavesNoTempFile()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        File.WriteAllText(path, "old content");

        JsonFile.WriteAtomic(path, new Sample { Name = "new" });

        Assert.Equal("new", JsonFile.Read<Sample>(path)!.Name);
        Assert.Equal(new[] { "data.json" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void WriteAtomic_WritesIndentedJson()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");

        JsonFile.WriteAtomic(path, new Sample { Name = "x" });

        Assert.Contains(Environment.NewLine + "  \"Name\": \"x\"", File.ReadAllText(path));
    }

    [Fact]
    public void Read_ThrowsForInvalidJson()
    {
        using var dir = new TempDirectory();
        var path = dir.File("broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.ThrowsAny<JsonException>(() => JsonFile.Read<Sample>(path));
    }

    [Fact]
    public void ReadOrQuarantine_RenamesAFileThatCannotBeParsed_AndReturnsNull()
    {
        using var dir = new TempDirectory();
        var path = dir.File("broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(JsonFile.ReadOrQuarantine<Sample>(path, "it will be recreated."));

        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "broken.json.corrupt-*"));
    }

    [Fact]
    public void ReadOrQuarantine_ReadsAGoodFile_AndIgnoresAMissingOne()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        JsonFile.WriteAtomic(path, new Sample { Name = "ok" });

        Assert.Equal("ok", JsonFile.ReadOrQuarantine<Sample>(path, "x")!.Name);
        Assert.Null(JsonFile.ReadOrQuarantine<Sample>(dir.File("missing.json"), "x"));
    }

    [Fact]
    public void ReadOrQuarantine_LeavesAFileAloneThatCannotBeReadRightNow()
    {
        // A locked file (sync client, virus scanner) says nothing about its content: it must not be renamed.
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        JsonFile.WriteAtomic(path, new Sample { Name = "keep" });

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => JsonFile.ReadOrQuarantine<Sample>(path, "x"));
        }

        Assert.Equal("keep", JsonFile.Read<Sample>(path)!.Name);
        Assert.Equal(new[] { "data.json" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Read_WaitsForAFileThatIsHeldForAMoment()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        JsonFile.WriteAtomic(path, new Sample { Name = "late" });
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var waits = 0;

        // The first attempt fails; while the reader waits, the other program lets go of the file.
        var read = JsonFile.Read<Sample>(path, _ =>
        {
            waits++;
            holder.Dispose();
        });

        Assert.Equal("late", read!.Name);
        Assert.Equal(1, waits);
    }

    [Fact]
    public void Read_GivesUpAfterThreeAttempts()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        JsonFile.WriteAtomic(path, new Sample { Name = "held" });
        var waits = 0;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => JsonFile.Read<Sample>(path, _ => waits++));

        Assert.Equal(2, waits);
    }

    [Fact]
    public void QuarantineCorruptFile_RenamesTheFileWithTimestamp()
    {
        using var dir = new TempDirectory();
        var path = dir.File("broken.json");
        File.WriteAllText(path, "{ not json");

        JsonFile.QuarantineCorruptFile(path);

        Assert.False(File.Exists(path));
        var quarantined = Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Matches(@"broken\.json\.corrupt-\d{8}-\d{6}$", quarantined);
        Assert.Equal("{ not json", File.ReadAllText(quarantined));
    }

    [Fact]
    public void QuarantineCorruptFile_IgnoresAMissingFile()
    {
        using var dir = new TempDirectory();

        JsonFile.QuarantineCorruptFile(dir.File("missing.json"));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}
