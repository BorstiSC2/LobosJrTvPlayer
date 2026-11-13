using YoutubeExplode;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.YouTube;

namespace YouTubeVideoPlayer.Tests.YouTube;

public class YouTubeServiceTests
{
    // A long-standing public video and the uploads playlist of its channel (used by the integration tests).
    private const string VideoId = "0_twHqFV6Lo";

    [Theory]
    [InlineData("dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("HTTPS://www.youtube.com/watch?v=x", "HTTPS://www.youtube.com/watch?v=x")]
    public void ToUrl_TurnsIdsIntoWatchLinks_AndKeepsUrls(string input, string expected)
    {
        Assert.Equal(expected, YouTubeService.ToUrl(input));
    }

    [IntegrationFact]
    public async Task GetVideo_ReturnsTitleAndDuration()
    {
        using var client = new YoutubeClient();
        var service = new YouTubeService(client);

        var video = await service.GetVideoAsync(VideoId, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(video.Title));
        Assert.True(video.Duration > TimeSpan.Zero);
    }

    [IntegrationFact]
    public async Task Resolve_ReturnsPlayableStreams()
    {
        using var client = new YoutubeClient();
        var service = new YouTubeService(client);

        var video = await service.ResolveAsync(VideoId, CancellationToken.None);

        Assert.StartsWith("http", video.VideoUrl);
        Assert.False(string.IsNullOrWhiteSpace(video.Title));
    }

    [IntegrationFact]
    public async Task Playlist_TitleAndVideoIds()
    {
        using var client = new YoutubeClient();
        var service = new YouTubeService(client);
        var channelId = (await client.Videos.GetAsync(VideoId)).Author.ChannelId.Value;
        var uploadsPlaylist = "UU" + channelId[2..];

        var title = await service.GetPlaylistTitleAsync(uploadsPlaylist, CancellationToken.None);
        var ids = await service.GetPlaylistVideoIdsAsync(uploadsPlaylist, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Contains(VideoId, ids);
    }
}
