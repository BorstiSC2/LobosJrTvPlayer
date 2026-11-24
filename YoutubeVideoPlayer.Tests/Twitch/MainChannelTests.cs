using System.Net;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TwitchStreamClientTests
{
    private static (TwitchStreamClient Client, FakeHttpHandler Http) Create(
        HttpStatusCode status, string json, TwitchApiCredentials? credentials = null)
    {
        var http = new FakeHttpHandler { Respond = _ => (status, json) };
        var given = credentials ?? new TwitchApiCredentials("cid", "oauth:tok");
        return (new TwitchStreamClient(_ => Task.FromResult<TwitchApiCredentials?>(given), http), http);
    }

    [Fact]
    public async Task AsksForTheStreamsOfTheChannel_WithClientIdAndBearerToken()
    {
        var (client, http) = Create(HttpStatusCode.OK, """{ "data": [ { "user_login": "lobosjr", "type": "live" } ] }""");

        var status = await client.GetStatusAsync("lobosjr", CancellationToken.None);

        Assert.True(status.IsLive);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/helix/streams", request.Path);
        Assert.Equal("?user_login=lobosjr", request.Query);
        Assert.Equal("cid", request.ClientId);
        Assert.Equal("Bearer tok", request.Authorization); // without the "oauth:" of a manual chat token
    }

    [Fact]
    public async Task WithoutAStream_TheChannelIsOffline()
    {
        var (client, _) = Create(HttpStatusCode.OK, """{ "data": [], "pagination": {} }""");

        Assert.False((await client.GetStatusAsync("lobosjr", CancellationToken.None)).IsLive);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{ "error": "?" }""")]
    public async Task AnAnswerThatDoesNotTell_IsUnknown_WithTheReason(HttpStatusCode status, string json)
    {
        var (client, _) = Create(status, json);

        var result = await client.GetStatusAsync("lobosjr", CancellationToken.None);

        Assert.Null(result.IsLive);
        Assert.False(string.IsNullOrEmpty(result.Problem));
    }

    [Fact]
    public async Task WithoutCredentials_TwitchIsNotAsked()
    {
        var http = new FakeHttpHandler();
        using var client = new TwitchStreamClient(_ => Task.FromResult<TwitchApiCredentials?>(null), http);

        var result = await client.GetStatusAsync("lobosjr", CancellationToken.None);

        Assert.Null(result.IsLive);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task AManualToken_GetsItsClientId_FromTwitch_Once()
    {
        var auth = Substitute.For<ITwitchAuthClient>();
        auth.ValidateAsync("tok", Arg.Any<CancellationToken>()).Returns(
            (TokenValidation?)null, // Twitch not reachable the first time
            new TokenValidation("lobosbot", "1", TimeSpan.FromHours(1), Array.Empty<string>(), "app-123"));
        var settings = new TwitchSettings { SignIn = { BotLogin = "lobosbot", OAuthToken = "oauth:tok" } };
        var credentials = TwitchStreamClient.CredentialsFor(settings, tokens: null, auth);

        Assert.Null(await credentials(CancellationToken.None));
        Assert.Equal(new TwitchApiCredentials("app-123", "tok"), await credentials(CancellationToken.None));
        Assert.Equal(new TwitchApiCredentials("app-123", "tok"), await credentials(CancellationToken.None));
        await auth.Received(2).ValidateAsync("tok", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_ReadsTheClientId()
    {
        var http = new FakeHttpHandler
        {
            Respond = _ => (HttpStatusCode.OK, """{ "client_id": "app-123", "login": "lobosbot", "user_id": "1", "expires_in": 60 }""")
        };
        using var auth = new TwitchAuthClient(string.Empty, http);

        Assert.Equal("app-123", (await auth.ValidateAsync("tok", CancellationToken.None))!.ClientId);
    }
}

public class MainChannelWatcherTests
{
    private sealed class Setup : IDisposable
    {
        public Setup()
        {
            Chat = TestSupport.Chat.Channel(out var sent);
            Sent = sent;
            Streams.GetStatusAsync("lobosjr", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Status));
            Watcher = new MainChannelWatcher(Chat, Streams, "lobosjr", Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public IChatChannel Chat { get; }

        public List<string> Sent { get; }

        public ITwitchStreamClient Streams { get; } = Substitute.For<ITwitchStreamClient>();

        public MainChannelWatcher Watcher { get; }

        /// <summary>What Twitch answers.</summary>
        public LiveStatus Status { get; set; } = new(false);

        public async Task CheckAsync(bool? live, TimeSpan? after = null)
        {
            Time.Advance(after ?? MainChannelWatcher.CheckInterval);
            Status = live == null ? LiveStatus.Unknown("network") : new LiveStatus(live);
            await Watcher.CheckAsync();
        }

        public void Dispose() => Watcher.Dispose();
    }

    private const string Alert = "ALERT LIVE ON MAIN ALERT – twitch.tv/lobosjr";

    [Fact]
    public void GoingLive_PostsTheAlertWithTheLink_InTheVodChat() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        Assert.Empty(setup.Sent);

        await setup.CheckAsync(true);

        Assert.Equal(new[] { Alert }, setup.Sent);
        Assert.Equal(Alert, setup.Watcher.Message);
    });

    [Fact]
    public void LiveAlreadyAtTheFirstCheck_GetsNoAlert() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        await setup.CheckAsync(true);
        await setup.CheckAsync(true);

        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void WhileItStaysLive_TheAlertIsNotRepeated() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        await setup.CheckAsync(true);

        for (var i = 0; i < 30; i++)
            await setup.CheckAsync(true);

        Assert.Single(setup.Sent);
    });

    [Fact]
    public void AShortDrop_IsTheSameStream_ALongerBreakANewOne() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        await setup.CheckAsync(true);
        Assert.Single(setup.Sent);

        await setup.CheckAsync(false); // the stream drops ...
        await setup.CheckAsync(true, MainChannelWatcher.NewStreamAfter - TimeSpan.FromMinutes(1)); // ... and is back in 9 minutes
        Assert.Single(setup.Sent);

        await setup.CheckAsync(false); // the stream ends
        await setup.CheckAsync(false, MainChannelWatcher.NewStreamAfter);
        await setup.CheckAsync(true); // a new stream, more than 10 minutes later
        Assert.Equal(2, setup.Sent.Count);
    });

    [Fact]
    public void OfflineAtTheStart_ThenLiveWithinTenMinutes_IsANewStream() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false); // not seen live before: no "drop"

        await setup.CheckAsync(true, TimeSpan.FromMinutes(2));

        Assert.Single(setup.Sent);
    });

    [Fact]
    public void AnswersThatDoNotTell_ChangeNothing() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(null); // nothing known yet
        await setup.CheckAsync(true); // so this is the first answer: live already, no alert
        Assert.Empty(setup.Sent);

        await setup.CheckAsync(false);
        await setup.CheckAsync(null);
        await setup.CheckAsync(null);
        await setup.CheckAsync(true, MainChannelWatcher.NewStreamAfter);
        Assert.Single(setup.Sent);
    });

    [Fact]
    public void WhileTheChatIsDisconnected_TheAlertWaits_AndIsPostedOnce() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        setup.Chat.IsConnected.Returns(false);

        await setup.CheckAsync(true);
        await setup.CheckAsync(true);
        Assert.Empty(setup.Sent);

        setup.Chat.IsConnected.Returns(true);
        await setup.CheckAsync(true);
        await setup.CheckAsync(true);

        Assert.Equal(new[] { Alert }, setup.Sent);
    });

    [Fact]
    public void AnAlertThatWaited_IsDropped_WhenTheStreamEndsFirst() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        setup.Chat.IsConnected.Returns(false);
        await setup.CheckAsync(true);
        await setup.CheckAsync(false);

        setup.Chat.IsConnected.Returns(true);
        await setup.CheckAsync(false);

        Assert.Empty(setup.Sent);
    });

    [Fact]
    public void Start_ChecksAtOnce_ThenEveryMinute_UntilDisposed() => UiContext.Run(async () =>
    {
        var setup = new Setup();
        setup.Watcher.Start();
        await Async.SettleAsync();
        await setup.Streams.Received(1).GetStatusAsync("lobosjr", Arg.Any<CancellationToken>());

        setup.Time.Advance(MainChannelWatcher.CheckInterval);
        await Async.SettleAsync();
        await setup.Streams.Received(2).GetStatusAsync("lobosjr", Arg.Any<CancellationToken>());

        setup.Dispose();
        setup.Time.Advance(MainChannelWatcher.CheckInterval * 5);
        await Async.SettleAsync();
        await setup.Streams.Received(2).GetStatusAsync("lobosjr", Arg.Any<CancellationToken>());
    });

    [Fact]
    public void TheBotOnlyEverPosts_ThroughTheVodChat() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        await setup.CheckAsync(false);
        await setup.CheckAsync(true);

        // The one channel object is the bot's chat (TwitchChatService posts to the VOD channel only); the main
        // channel is only ever passed to the API.
        await setup.Chat.Received(1).SendMessageAsync(Alert);
        await setup.Streams.Received().GetStatusAsync("lobosjr", Arg.Any<CancellationToken>());
    });
}
