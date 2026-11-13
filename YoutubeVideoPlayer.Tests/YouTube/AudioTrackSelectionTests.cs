using YoutubeExplode.Common;
using YoutubeExplode.Videos.ClosedCaptions;
using YoutubeExplode.Videos.Streams;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

/// <summary>
/// Many videos offer one audio track per language: the original one and automatically dubbed translations.
/// The dubbed tracks often have the higher bitrate, so choosing by bitrate alone plays a translation.
/// </summary>
public class AudioTrackSelectionTests
{
    private static StreamCandidate Video() => new(StreamKind.VideoOnly, "video", 5_000_000, "avc1.640028", 1080);

    private static StreamCandidate Audio(string url, long bitrate, string? language = null, bool? isDefault = null, string? name = null) =>
        new(StreamKind.AudioOnly, url, bitrate, "opus", AudioLanguage: language, IsDefaultAudio: isDefault, AudioLanguageName: name);

    private static string? PickedAudio(params StreamCandidate[] audio) =>
        StreamSelector.Select(audio.Prepend(Video())).AudioUrl;

    [Fact]
    public void TheEnglishOriginalBeatsDubbedTracksWithAHigherBitrate()
    {
        // The shape of a real video from the playlists: the dubbed tracks are a little bigger than the original.
        var picked = PickedAudio(
            Audio("indonesian", 143_000, "id", false, "Indonesian"),
            Audio("polish", 140_000, "pl", false, "Polish"),
            Audio("german", 136_000, "de-DE", false, "German (DE)"),
            Audio("english-original", 130_000, "en-US", true, "English (US) original"));

        Assert.Equal("english-original", picked);
    }

    [Fact]
    public void AnEnglishDub_IsPreferredOverAForeignOriginal()
    {
        // "Always English": the video is originally German, and there is an English translation.
        var picked = PickedAudio(
            Audio("german-original", 150_000, "de-DE", true, "German (DE) original"),
            Audio("english-dub", 130_000, "en-US", false, "English (US)"));

        Assert.Equal("english-dub", picked);
    }

    [Fact]
    public void WithoutAnEnglishTrack_TheOriginalIsPlayed_NotTheHighestBitrate()
    {
        var picked = PickedAudio(
            Audio("french-dub", 160_000, "fr-FR", false, "French (FR)"),
            Audio("japanese-original", 120_000, "ja", true, "Japanese original"),
            Audio("hindi-dub", 150_000, "hi", false, "Hindi"));

        Assert.Equal("japanese-original", picked);
    }

    [Fact]
    public void AmongSeveralEnglishTracks_TheOriginalWins_ElseTheHighestBitrate()
    {
        Assert.Equal("original", PickedAudio(
            Audio("dub", 160_000, "en-GB", false),
            Audio("original", 130_000, "en-US", true)));

        Assert.Equal("high", PickedAudio(
            Audio("low", 100_000, "en-US", false),
            Audio("high", 140_000, "en-GB", false)));
    }

    [Fact]
    public void AllQualitiesOfTheChosenTrack_AreConsidered_TheBestOneWins()
    {
        // Every track comes in several bitrates (like "x5" in the manifests of the real videos).
        var picked = PickedAudio(
            Audio("english-low", 50_000, "en-US", true),
            Audio("english-mid", 130_000, "en-US", true),
            Audio("english-high", 150_000, "en-US", true),
            Audio("german-high", 190_000, "de-DE", false));

        Assert.Equal("english-high", picked);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("EN-us")]
    [InlineData("eng")]
    public void EnglishIsRecognizedByItsLanguageCode(string code)
    {
        Assert.Equal("english", PickedAudio(Audio("other", 200_000, "xx", false), Audio("english", 100_000, code, false)));
    }

    [Theory]
    [InlineData("es-US")]   // starts with "e" but is Spanish
    [InlineData("enx")]
    [InlineData("de-DE")]
    [InlineData("hi")]
    public void OtherLanguagesAreNotEnglish(string code)
    {
        // Neither track is English and none is marked as the original: the highest bitrate, as before.
        Assert.Equal("big", PickedAudio(Audio("big", 200_000, code, false), Audio("small", 100_000, "ja", false)));
    }

    [Fact]
    public void WithoutLanguageInformation_TheHighestBitrateIsPlayed_AsBefore()
    {
        Assert.Equal("audio-high", PickedAudio(Audio("audio-low", 64_000), Audio("audio-high", 160_000)));
    }

    [Fact]
    public void AVideoWithoutSeparateAudio_StillFallsBackToTheMuxedStream()
    {
        var selected = StreamSelector.Select(new[] { new StreamCandidate(StreamKind.Muxed, "muxed", 3_000_000, "avc1.4d401f", 720) });

        Assert.Equal(new SelectedStreams("muxed", null), selected);
    }

    [Fact]
    public void TheLogLineNamesTheLanguageOfTheAudioTrack()
    {
        Assert.Equal(
            "opus 130 kbit/s [en-US 'English (US) original', original]",
            Audio("a", 130_000, "en-US", true, "English (US) original").Describe());
        Assert.Equal("opus 143 kbit/s [id 'Indonesian']", Audio("a", 143_000, "id", false, "Indonesian").Describe());
        Assert.Equal("opus 160 kbit/s", Audio("a", 160_000).Describe());
    }

    #region Through the real manifest types

    private static AudioOnlyStreamInfo AudioTrack(string url, long bitrate, string code, string name, bool isDefault) =>
        new(url, new Container("webm"), new FileSize(1_000_000), new Bitrate(bitrate), "opus", new Language(code, name), isDefault);

    [Fact]
    public void AManifestWithDubbedTracks_PlaysTheEnglishOriginal()
    {
        var manifest = new StreamManifest(new IStreamInfo[]
        {
            new VideoOnlyStreamInfo(
                "https://stream.example/video", new Container("mp4"), new FileSize(10_000_000), new Bitrate(5_000_000),
                "avc1.640028", new VideoQuality(1080, 30), new Resolution(1920, 1080)),
            AudioTrack("https://stream.example/id", 143_000, "id", "Indonesian", false),
            AudioTrack("https://stream.example/pl", 140_000, "pl", "Polish", false),
            AudioTrack("https://stream.example/en", 130_000, "en-US", "English (US) original", true)
        });

        var selected = YouTubeService.SelectStreams(manifest, "BepBWodKptk");

        Assert.Equal("https://stream.example/video", selected.VideoUrl);
        Assert.Equal("https://stream.example/en", selected.AudioUrl);
    }

    [Fact]
    public void AManifestWithoutLanguageInformation_StillPlaysTheBestAudio()
    {
        var manifest = new StreamManifest(new IStreamInfo[]
        {
            new VideoOnlyStreamInfo(
                "https://stream.example/video", new Container("mp4"), new FileSize(10_000_000), new Bitrate(5_000_000),
                "avc1.640028", new VideoQuality(1080, 30), new Resolution(1920, 1080)),
            new AudioOnlyStreamInfo("https://stream.example/low", new Container("webm"), new FileSize(1), new Bitrate(64_000), "opus", null, null),
            new AudioOnlyStreamInfo("https://stream.example/high", new Container("webm"), new FileSize(1), new Bitrate(160_000), "opus", null, null)
        });

        Assert.Equal("https://stream.example/high", YouTubeService.SelectStreams(manifest, "v").AudioUrl);
    }

    #endregion
}
