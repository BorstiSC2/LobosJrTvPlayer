using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class StreamSelectorTests
{
    [Fact]
    public void PrefersBestVideoOnlyStreamWithBestAudioStream()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "video-720", 2_000_000),
            new StreamCandidate(StreamKind.VideoOnly, "video-1080", 5_000_000),
            new StreamCandidate(StreamKind.AudioOnly, "audio-low", 64_000),
            new StreamCandidate(StreamKind.AudioOnly, "audio-high", 160_000),
            new StreamCandidate(StreamKind.Muxed, "muxed", 9_000_000)
        });

        Assert.Equal(new SelectedStreams("video-1080", "audio-high"), selected);
    }

    [Fact]
    public void AtTheSameHeight_PrefersH264OverHeavierCodecsWithAHigherBitrate()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "av1-2160", 14_000_000, "av01.0.12M.08", 2160),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1080", 8_000_000, "vp09.00.40.08", 1080),
            new StreamCandidate(StreamKind.VideoOnly, "h264-1080", 5_000_000, "avc1.640028", 1080),
            new StreamCandidate(StreamKind.VideoOnly, "h264-720", 2_500_000, "avc1.4d401f", 720),
            new StreamCandidate(StreamKind.AudioOnly, "audio", 128_000, "opus")
        });

        Assert.Equal(new SelectedStreams("h264-1080", "audio"), selected);
    }

    [Fact]
    public void Takes1440pWhenOffered_EvenThoughOnly1080pIsH264()
    {
        // As YouTube really offers it: H.264 stops at 1080p, 1440p is VP9 (and AV1).
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "h264-1080", 5_700_000, "avc1.64002A", 1080),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1440", 8_300_000, "vp9", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "av1-1440", 6_200_000, "av01.0.12M.08", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-2160", 20_000_000, "vp9", 2160),
            new StreamCandidate(StreamKind.AudioOnly, "audio", 128_000, "opus")
        });

        Assert.Equal(new SelectedStreams("vp9-1440", "audio"), selected);
    }

    [Fact]
    public void At1440p_VP9IsPreferredOverAV1_AndH264WouldBePreferredOverBoth()
    {
        Assert.Equal("vp9", StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "av1", 9_000_000, "av01.0.12M.08", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "vp9", 8_000_000, "vp9", 1440)
        }).VideoUrl);

        Assert.Equal("h264", StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9", 8_000_000, "vp9", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "h264", 5_000_000, "avc1.640032", 1440)
        }).VideoUrl);
    }

    [Fact]
    public void HdrStreamsAreAvoided_BecauseVlcShowsThemWashedOut()
    {
        // The 1440p streams of an HDR video: VP9 profile 2 and 10-bit AV1 carry HDR, plain "vp9" is SDR.
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9.2-1440", 16_700_000, "vp9.2", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "av1-10bit-1440", 14_700_000, "av01.0.12M.10.0.110.09.16.09.0", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1440", 13_300_000, "vp9", 1440)
        });

        Assert.Equal("vp9-1440", selected.VideoUrl);
    }

    [Fact]
    public void AnSdrStreamOfLowerHeight_BeatsAnHdrStreamOf1440p()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9.2-1440", 16_700_000, "vp9.2", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "h264-1080", 5_700_000, "avc1.64002A", 1080)
        });

        Assert.Equal("h264-1080", selected.VideoUrl);
    }

    [Fact]
    public void HdrIsUsedWhenItIsTheOnlyChoice()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9.2-720", 4_000_000, "vp9.2", 720),
            new StreamCandidate(StreamKind.VideoOnly, "vp9.2-1080", 7_000_000, "vp9.2", 1080)
        });

        Assert.Equal("vp9.2-1080", selected.VideoUrl);
    }

    [Fact]
    public void Streams_AboveTheMaximumHeight_AreNotTakenWhenSomethingSmallerExists()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9-2160", 20_000_000, "vp09.00.50.08", 2160),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1080", 8_000_000, "vp09.00.40.08", 1080),
            new StreamCandidate(StreamKind.VideoOnly, "av1-720", 3_000_000, "av01.0.05M.08", 720)
        });

        Assert.Equal("vp9-1080", selected.VideoUrl);
    }

    [Fact]
    public void WhenEverythingIsAboveTheMaximumHeight_TakesTheLowestOfThem()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9-4320", 60_000_000, "vp9", 4320),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-2160", 20_000_000, "vp9", 2160)
        });

        Assert.Equal("vp9-2160", selected.VideoUrl);
    }

    [Fact]
    public void AtTheTallestHeight_TheHighestBitrateWins()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1440-30", 5_000_000, "vp9", 1440),
            new StreamCandidate(StreamKind.VideoOnly, "vp9-1440-60", 9_000_000, "vp9", 1440)
        });

        Assert.Equal("vp9-1440-60", selected.VideoUrl);
    }

    [Fact]
    public void TheMaximumHeightIs1440p() => Assert.Equal(1440, StreamSelector.MaxHeight);

    [Fact]
    public void BestVideo_IsNullWithoutVideoOnlyStreams()
    {
        Assert.Null(StreamSelector.BestVideo(new[] { new StreamCandidate(StreamKind.Muxed, "muxed", 1) }));
    }

    [Fact]
    public void Describe_NamesCodecResolutionAndBitrate()
    {
        Assert.Equal("avc1.640028 1080p 4500 kbit/s", new StreamCandidate(StreamKind.VideoOnly, "u", 4_500_000, "avc1.640028", 1080).Describe());
        Assert.Equal("opus 128 kbit/s", new StreamCandidate(StreamKind.AudioOnly, "u", 128_000, "opus").Describe());
        Assert.Equal("unknown codec 0 kbit/s", new StreamCandidate(StreamKind.AudioOnly, "u", 0).Describe());
    }

    [Fact]
    public void VideoOnlyStreamWithoutAudio_HasNoAudioUrl()
    {
        var selected = StreamSelector.Select(new[] { new StreamCandidate(StreamKind.VideoOnly, "video", 1) });

        Assert.Equal("video", selected.VideoUrl);
        Assert.Null(selected.AudioUrl);
    }

    [Fact]
    public void WithoutVideoOnlyStreams_FallsBackToBestMuxedStream()
    {
        var selected = StreamSelector.Select(new[]
        {
            new StreamCandidate(StreamKind.Muxed, "muxed-360", 500_000),
            new StreamCandidate(StreamKind.AudioOnly, "audio", 160_000),
            new StreamCandidate(StreamKind.Muxed, "muxed-720", 1_500_000)
        });

        Assert.Equal(new SelectedStreams("muxed-720", null), selected);
    }

    [Fact]
    public void WithoutVideoOrMuxedStreams_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StreamSelector.Select(new[] { new StreamCandidate(StreamKind.AudioOnly, "audio", 1) }));

        Assert.Equal("No muxed stream found", ex.Message);
    }

    [Fact]
    public void EmptyManifest_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => StreamSelector.Select(Array.Empty<StreamCandidate>()));
    }
}
