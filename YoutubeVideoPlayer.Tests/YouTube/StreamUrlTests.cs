using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class StreamUrlTests
{
    [Fact]
    public void TheExpiryIsTheUnixTimeInTheExpireParameter()
    {
        var url = "https://rr3---sn-x.googlevideo.com/videoplayback?expire=1794598666&ei=abc&ip=1.2.3.4&itag=137";

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794598666), StreamUrl.GetExpiry(url));
    }

    [Theory]
    [InlineData("https://x.example/videoplayback?itag=137")]            // no expire parameter
    [InlineData("https://x.example/videoplayback?expire=soon")]         // not a number
    [InlineData("https://x.example/videoplayback?expire=0")]            // not a time
    [InlineData("https://x.example/videoplayback?expire=-5")]
    [InlineData("https://x.example/videoplayback?expire=99999999999999999")] // out of range
    [InlineData("https://x.example/videoplayback?notexpire=1794598666")]
    [InlineData("https://x.example/videoplayback")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void WithoutAUsableExpireParameter_TheExpiryIsUnknown(string? url)
    {
        Assert.Null(StreamUrl.GetExpiry(url));
    }
}
