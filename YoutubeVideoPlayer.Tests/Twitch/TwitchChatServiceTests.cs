using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Exceptions;
using TwitchLib.Client.Interfaces;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Events;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TwitchChatServiceTests
{
    private static readonly TwitchSettings ManualSettings = new()
    {
        BotUsername = "lobosbot",
        OAuthToken = "oauth:manual",
        Channel = "lobosjr"
    };

    private static readonly TwitchSettings AppSignInSettings = new() { ClientId = "cid", Channel = "lobosjr" };

    /// <summary>A token manager whose auth client is a substitute, with an optional stored token.</summary>
    private sealed class Tokens : IDisposable
    {
        private readonly TempDirectory _dir = new();

        public Tokens(bool signedIn)
        {
            Store = new TwitchTokenStore(_dir.Path);
            if (signedIn)
            {
                Store.Save(new TwitchToken
                {
                    AccessToken = "acc-1",
                    RefreshToken = "ref-1",
                    ExpiresAtUtc = Time.GetUtcNow().UtcDateTime.AddHours(3),
                    Login = "lobosbot",
                    UserId = "4711"
                });
            }

            Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TokenValidation("lobosbot", "4711", TimeSpan.FromHours(3), Array.Empty<string>()));
            Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TokenResponse("acc-2", "ref-2", TimeSpan.FromHours(4), Array.Empty<string>()));
            Manager = new TwitchTokenManager(Auth, Store, timeProvider: Time);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));

        public ITwitchAuthClient Auth { get; } = Substitute.For<ITwitchAuthClient>();

        public TwitchTokenStore Store { get; }

        public TwitchTokenManager Manager { get; }

        public void Dispose()
        {
            Manager.Dispose();
            _dir.Dispose();
        }
    }

    private static void Raise<TArgs>(ITwitchClient client, Action<ITwitchClient, AsyncEventHandler<TArgs>> subscribe, TArgs args) =>
        subscribe(client, NSubstitute.Raise.Event<AsyncEventHandler<TArgs>>(client, args));

    private static void Joined(ITwitchClient client) =>
        Raise<OnJoinedChannelArgs>(client, (c, h) => c.OnJoinedChannel += h, new OnJoinedChannelArgs("lobosbot", "lobosjr"));

    private static void IncorrectLogin(ITwitchClient client) =>
        Raise<OnIncorrectLoginArgs>(client, (c, h) => c.OnIncorrectLogin += h,
            new OnIncorrectLoginArgs(new ErrorLoggingInException("Login authentication failed", "lobosbot")));

    private static void Disconnected(ITwitchClient client) =>
        Raise<OnDisconnectedArgs>(client, (c, h) => c.OnDisconnected += h, new OnDisconnectedArgs("lobosbot"));

    private static bool IsCredentials(ConnectionCredentials credentials, string user, string token) =>
        credentials.TwitchUsername == user && credentials.TwitchOAuth.EndsWith(token);

    [Fact]
    public void Constructor_RequiresASynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new TwitchChatService(ManualSettings, null, Chat.Client()));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    #region Manual token

    [Fact]
    public void ManualToken_ConnectsWithTheConfiguredCredentials() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        var states = new List<TwitchConnectionState>();
        chat.StateChanged += (_, s) => states.Add(s);

        await chat.StartAsync();

        client.Received(1).Initialize(Arg.Is<ConnectionCredentials>(c => IsCredentials(c, "lobosbot", "manual")), "lobosjr");
        await client.Received(1).ConnectAsync();
        Assert.False(chat.UsesAppSignIn);
        Assert.Equal("lobosbot", chat.BotLogin);
        Assert.Equal("lobosjr", chat.Channel);
        Assert.Equal(TwitchConnectionState.Connecting, chat.State);

        Joined(client);
        await Async.SettleAsync();
        Assert.Equal(new[] { TwitchConnectionState.Connecting, TwitchConnectionState.Connected }, states);
    });

    [Fact]
    public void ManualToken_ARejectedLoginIsReportedAndStays() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();

        IncorrectLogin(client);
        await Async.SettleAsync();
        Disconnected(client); // does not hide the login problem
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.LoginFailed, chat.State);
    });

    [Fact]
    public void ConnectionFailure_IsReportedAsDisconnected() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        client.ConnectAsync().Returns(Task.FromException<bool>(new InvalidOperationException("no network")));
        var chat = new TwitchChatService(ManualSettings, null, client);

        await chat.StartAsync();

        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);
    });

    private static int ConnectCalls(ITwitchClient client) => client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITwitchClient.ConnectAsync));

    /// <summary>
    /// Waits until connect attempt number <paramref name="attempt"/> has been made and its failure has been handled.
    /// A new attempt sets the state to Connecting before it connects, and the failure sets it back to Disconnected
    /// and schedules the next retry in the same step, so both together mean that the retry is waiting.
    /// </summary>
    private static Task WaitForFailedAttemptAsync(ITwitchClient client, TwitchChatService chat, int attempt) =>
        Async.WaitUntilAsync(
            () => ConnectCalls(client) == attempt && chat.State == TwitchConnectionState.Disconnected,
            because: $"connect attempt {attempt} fails");

    [Fact]
    public void ARefusedConnection_IsReportedAsDisconnected_AndTriedAgain() => UiContext.Run(async () =>
    {
        // TwitchLib reports a connection that could not be opened (offline, Twitch unreachable) with false, and raises no event.
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        client.ConnectAsync().Returns(false);
        var chat = new TwitchChatService(ManualSettings, null, client, time);

        await chat.StartAsync();

        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);
        Assert.Equal(1, ConnectCalls(client));

        client.ConnectAsync().Returns(true); // the network is back
        time.Advance(TwitchChatService.ReconnectDelay);
        await Async.WaitUntilAsync(() => ConnectCalls(client) == 2, because: "the connection is tried again");
        Joined(client);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.Connected, chat.State);
        time.Advance(TwitchChatService.MaxReconnectDelay); // a connected bot does not keep trying
        await Async.SettleAsync();
        Assert.Equal(2, ConnectCalls(client));
    });

    [Fact]
    public void AConnectionError_IsTriedAgainToo() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        client.ConnectAsync().Returns(Task.FromException<bool>(new InvalidOperationException("no network")));
        var chat = new TwitchChatService(ManualSettings, null, client, time);
        await chat.StartAsync();
        client.ConnectAsync().Returns(true);

        time.Advance(TwitchChatService.ReconnectDelay);

        await Async.WaitUntilAsync(() => ConnectCalls(client) == 2, because: "the connection is tried again");
    });

    [Fact]
    public void ARefusedConnection_IsTriedAgainWithGrowingPauses_UpToTheMaximum() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        client.ConnectAsync().Returns(false);
        var chat = new TwitchChatService(ManualSettings, null, client, time);
        await chat.StartAsync();

        var attempts = 1;
        foreach (var seconds in new[] { 15, 30, 60, 120, 240, 300, 300 })
        {
            var pause = TimeSpan.FromSeconds(seconds);

            time.Advance(pause - TimeSpan.FromSeconds(1));
            await Async.SettleAsync();
            Assert.Equal(attempts, ConnectCalls(client)); // too early

            time.Advance(TimeSpan.FromSeconds(1));
            attempts++;
            await WaitForFailedAttemptAsync(client, chat, attempts);
        }
    });

    [Fact]
    public void RejectedCredentials_AreNotTriedAgain() => UiContext.Run(async () =>
    {
        // TwitchLib refuses a login name this short, and will refuse it again: retrying would only fill the log.
        var settings = new TwitchSettings { BotUsername = "ab", OAuthToken = "oauth:x", Channel = "lobosjr" };
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        var chat = new TwitchChatService(settings, null, client, time);

        await chat.StartAsync();
        time.Advance(TwitchChatService.MaxReconnectDelay);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);
        await client.DidNotReceive().ConnectAsync();
        client.DidNotReceiveWithAnyArgs().Initialize(default!, default(string)!);
    });

    [Fact]
    public void Dispose_EndsAWaitingRetry() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        client.ConnectAsync().Returns(false);
        var chat = new TwitchChatService(ManualSettings, null, client, time);
        await chat.StartAsync();

        await chat.DisposeAsync();
        time.Advance(TwitchChatService.MaxReconnectDelay);
        await Async.SettleAsync();

        Assert.Equal(1, ConnectCalls(client));
    });

    [Fact]
    public void ARefusedConnectionOnARealTwitchClient_IsTriedAgain_AndTheChannelIsJoinedOnce() => UiContext.Run(async () =>
    {
        // After a failed first connect TwitchLib still has the channel queued, so the retry must not join it by hand as well.
        var time = new FakeTimeProvider();
        var transport = new FakeChatTransport { AcceptConnections = false };
        var client = new TwitchClient(transport, ClientProtocol.WebSocket, null!, null!);
        var chat = new TwitchChatService(ManualSettings, null, client, time);

        await chat.StartAsync();
        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);

        transport.AcceptConnections = true;
        time.Advance(TwitchChatService.ReconnectDelay);
        await Async.WaitUntilAsync(() => transport.IsConnected, because: "the connection is tried again");
        await transport.WelcomeAsync("lobosbot");
        await Async.WaitUntilAsync(() => transport.JoinCommands.Count == 1, because: "TwitchLib joins the channel after the welcome");
        await transport.EchoJoinsAsync("lobosbot");
        await Async.WaitUntilAsync(() => chat.State == TwitchConnectionState.Connected, because: "the join is confirmed");

        Assert.Equal(new[] { "JOIN #lobosjr" }, transport.JoinCommands);
    });

    [Fact]
    public void ConnectionStates_FollowTheClient() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();
        Joined(client);
        await Async.SettleAsync();

        Disconnected(client);
        await Async.SettleAsync();
        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);

        Raise<TwitchLib.Client.Events.OnConnectedEventArgs>(client, (c, h) => c.OnReconnected += h, new TwitchLib.Client.Events.OnConnectedEventArgs("lobosbot"));
        await Async.SettleAsync();
        Assert.Equal(TwitchConnectionState.Connected, chat.State);

        // Errors are only logged.
        Raise<OnConnectionErrorArgs>(client, (c, h) => c.OnConnectionError += h, new OnConnectionErrorArgs("lobosbot", new ErrorEvent("oops")));
        Raise<OnErrorEventArgs>(client, (c, h) => c.OnError += h, new OnErrorEventArgs(new Exception("oops")));
        await Async.SettleAsync();
        Assert.Equal(TwitchConnectionState.Connected, chat.State);
    });

    [Fact]
    public void SendMessage_OnlyWhileConnected() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();

        await chat.SendMessageAsync("too early");
        Joined(client);
        await Async.SettleAsync();
        await chat.SendMessageAsync("hello chat");

        await client.Received(1).SendMessageAsync("lobosjr", "hello chat", Arg.Any<bool>());
        await client.DidNotReceive().SendMessageAsync(Arg.Any<string>(), "too early", Arg.Any<bool>());
    });

    [Fact]
    public void SendMessage_FailuresAreSwallowed() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        client.SendMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns(Task.FromException(new Exception("rate limited")));
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();
        Joined(client);
        await Async.SettleAsync();

        await chat.SendMessageAsync("hello");
    });

    private static Task WaitForMessageAsync(ITwitchClient client, string message) =>
        Async.WaitUntilAsync(() => client.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(ITwitchClient.SendMessageAsync) && (string)c.GetArguments()[1]! == message));

    [Fact]
    public void Connected_PostsTheInitialMessage_AndAReconnectMessageAfterALostConnection() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();

        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ConnectedMessage);

        // After a dropped connection the bot says it is back; the join that follows the reconnect adds nothing.
        Disconnected(client);
        await Async.SettleAsync();
        Raise<TwitchLib.Client.Events.OnConnectedEventArgs>(client, (c, h) => c.OnReconnected += h, new TwitchLib.Client.Events.OnConnectedEventArgs("lobosbot"));
        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ReconnectedMessage);
        await Async.SettleAsync();

        Assert.Equal(2, client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITwitchClient.SendMessageAsync)));
        await client.Received(1).SendMessageAsync("lobosjr", TwitchChatService.ConnectedMessage, Arg.Any<bool>());
        await client.Received(1).SendMessageAsync("lobosjr", TwitchChatService.ReconnectedMessage, Arg.Any<bool>());
    });

    [Fact]
    public void AppSignIn_ARenewedTokenDoesNotGreetAgain() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();
        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ConnectedMessage);
        client.IsConnected.Returns(true);
        client.ClearReceivedCalls();

        await tokens.Manager.RefreshAsync();
        await Async.WaitUntilAsync(() => client.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ITwitchClient.ConnectAsync)));
        Joined(client);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.Connected, chat.State);
        await client.DidNotReceive().SendMessageAsync(Arg.Any<string>(), TwitchChatService.ConnectedMessage, Arg.Any<bool>());
        await client.DidNotReceive().SendMessageAsync(Arg.Any<string>(), TwitchChatService.ReconnectedMessage, Arg.Any<bool>());
    });

    [Fact]
    public void Dispose_WhileConnected_SaysGoodbyeBeforeDisconnecting() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client, time);
        await chat.StartAsync();
        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ConnectedMessage);
        client.IsConnected.Returns(true);
        client.ClearReceivedCalls();

        var disposal = chat.DisposeAsync().AsTask();
        await WaitForMessageAsync(client, TwitchChatService.GoodbyeMessage);
        await Async.SettleAsync();

        // The goodbye gets a moment to leave TwitchLib's send queue; the connection is not closed before that (on the fake clock).
        Assert.False(disposal.IsCompleted);
        await client.DidNotReceive().DisconnectAsync();

        await Async.WaitUntilAsync(() =>
        {
            time.Advance(TwitchChatService.GoodbyeFlushDelay);
            return disposal.IsCompleted;
        }, because: "the goodbye delay is over");
        await disposal;

        Received.InOrder(() =>
        {
            client.SendMessageAsync("lobosjr", TwitchChatService.GoodbyeMessage, Arg.Any<bool>());
            client.DisconnectAsync();
        });
    });

    [Fact]
    public void Dispose_WhileNotConnected_SaysNothing() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        var chat = new TwitchChatService(ManualSettings, null, client);
        await chat.StartAsync();

        await chat.DisposeAsync();

        await client.DidNotReceive().SendMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    });

    [Fact]
    public void Dispose_DisconnectsAndIgnoresLaterEvents() => UiContext.Run(async () =>
    {
        var client = Chat.Client();
        client.IsConnected.Returns(true);
        var chat = new TwitchChatService(ManualSettings, null, client);
        var states = 0;
        chat.StateChanged += (_, _) => states++;

        await chat.DisposeAsync();
        await chat.DisposeAsync();
        Joined(client);
        await Async.SettleAsync();
        await chat.SendMessageAsync("ignored");

        await client.Received(1).DisconnectAsync();
        Assert.Equal(0, states);
    });

    #endregion

    #region App sign-in

    [Fact]
    public void AppSignIn_WithoutStoredToken_AsksForASignIn_AndDoesNotConnect() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: false);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);

        await chat.StartAsync();

        Assert.True(chat.UsesAppSignIn);
        Assert.Equal(TwitchConnectionState.SignInRequired, chat.State);
        await client.DidNotReceive().ConnectAsync();
    });

    [Fact]
    public void AppSignIn_ConnectsWithTheStoredToken() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);

        await chat.StartAsync();

        client.Received(1).Initialize(Arg.Is<ConnectionCredentials>(c => IsCredentials(c, "lobosbot", "acc-1")), "lobosjr");
        await client.Received(1).ConnectAsync();
        Assert.Equal("lobosbot", chat.BotLogin);
    });

    private static bool WasCalled(ITwitchClient client, string method) =>
        client.ReceivedCalls().Any(c => c.GetMethodInfo().Name == method);

    [Fact]
    public void AppSignIn_ARenewedTokenReconnectsWithTheNewCredentials_AndJoinsTheChannelAgain() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();
        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ConnectedMessage);
        client.IsConnected.Returns(true);
        client.ClearReceivedCalls();

        await tokens.Manager.RefreshAsync();
        // The reconnect runs on the thread pool; wait for it instead of hoping that one settle is enough.
        await Async.WaitUntilAsync(() => WasCalled(client, nameof(ITwitchClient.JoinChannelAsync)), because: "the channel is joined again");
        await Async.SettleAsync();

        // TwitchLib forgets its channel when it disconnects, so the bot has to join it again by hand.
        Received.InOrder(() =>
        {
            client.DisconnectAsync();
            client.SetConnectionCredentials(Arg.Is<ConnectionCredentials>(c => IsCredentials(c, "lobosbot", "acc-2")));
            client.ConnectAsync();
            client.JoinChannelAsync("lobosjr", Arg.Any<bool>());
        });
        client.DidNotReceiveWithAnyArgs().Initialize(default!, default(string)!);
    });

    [Fact]
    public void AppSignIn_ARenewalAfterALostConnectionJoinsTheChannelAgain() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();
        Joined(client);
        await WaitForMessageAsync(client, TwitchChatService.ConnectedMessage);
        Disconnected(client); // the connection is gone (client.IsConnected stays false), so there is nothing to close
        await Async.SettleAsync();
        client.ClearReceivedCalls();

        await tokens.Manager.RefreshAsync();
        await Async.WaitUntilAsync(() => WasCalled(client, nameof(ITwitchClient.JoinChannelAsync)), because: "the channel is joined again");

        await client.DidNotReceive().DisconnectAsync();
        await client.Received(1).ConnectAsync();
        await client.Received(1).JoinChannelAsync("lobosjr", Arg.Any<bool>());
    });

    [Fact]
    public void AppSignIn_TheFirstConnectLeavesTheJoinToTwitchLib() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);

        await chat.StartAsync();
        await Async.SettleAsync();

        // Initialize hands the channel to TwitchLib, which joins it once the first connection is welcomed.
        await client.Received(1).ConnectAsync();
        await client.DidNotReceive().JoinChannelAsync(Arg.Any<string>(), Arg.Any<bool>());
    });

    [Fact]
    public void AppSignIn_ARenewalBeforeTheFirstJoinLeavesTheJoinToTwitchLib() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync(); // never joined (e.g. the login was rejected), so TwitchLib still has the channel queued
        client.IsConnected.Returns(true);
        client.ClearReceivedCalls();

        await tokens.Manager.RefreshAsync();
        await Async.WaitUntilAsync(() => WasCalled(client, nameof(ITwitchClient.ConnectAsync)));
        await Async.SettleAsync();

        await client.DidNotReceive().JoinChannelAsync(Arg.Any<string>(), Arg.Any<bool>());
    });

    [Fact]
    public void AppSignIn_ARenewedTokenJoinsTheChannelAgainOnARealTwitchClient() => UiContext.Run(async () =>
    {
        // The substitute tests above only show which calls the service makes. This one runs TwitchLib itself
        // on a fake connection, to show that the channel really is joined again after the credential swap.
        using var tokens = new Tokens(signedIn: true);
        var transport = new FakeChatTransport();
        var client = new TwitchClient(transport, ClientProtocol.WebSocket, null!, null!);
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);

        await chat.StartAsync();
        await transport.WelcomeAsync("lobosbot");
        await Async.WaitUntilAsync(() => transport.JoinCommands.Count == 1, because: "TwitchLib joins the channel after the welcome");
        await transport.EchoJoinsAsync("lobosbot");
        await Async.WaitUntilAsync(() => chat.State == TwitchConnectionState.Connected, because: "the first join is confirmed");
        transport.ClearSent();

        await tokens.Manager.RefreshAsync();
        await Async.WaitUntilAsync(() => transport.JoinCommands.Count == 1, because: "the channel is joined again after the swap");
        await transport.EchoJoinsAsync("lobosbot");
        await Async.WaitUntilAsync(() => chat.State == TwitchConnectionState.Connected, because: "the second join is confirmed");

        Assert.Equal(new[] { "JOIN #lobosjr" }, transport.JoinCommands);
    });

    [Fact]
    public void AppSignIn_ARejectedLoginIsRetriedOnceWithARefreshedToken() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();

        IncorrectLogin(client);
        await Async.SettleAsync();
        Assert.NotEqual(TwitchConnectionState.LoginFailed, chat.State);
        await tokens.Auth.Received(1).RefreshAsync("ref-1", Arg.Any<CancellationToken>());
        client.Received(1).SetConnectionCredentials(Arg.Is<ConnectionCredentials>(c => IsCredentials(c, "lobosbot", "acc-2")));

        IncorrectLogin(client);
        await Async.SettleAsync();
        Assert.Equal(TwitchConnectionState.LoginFailed, chat.State);
    });

    [Fact]
    public void AppSignIn_ASuccessfulJoinResetsTheLoginRetry() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();

        IncorrectLogin(client);
        await Async.SettleAsync();
        Joined(client);
        await Async.SettleAsync();
        IncorrectLogin(client);
        await Async.SettleAsync();

        Assert.NotEqual(TwitchConnectionState.LoginFailed, chat.State);
        await tokens.Auth.Received(2).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    });

    [Fact]
    public void AppSignIn_ARevokedSignInDisconnects() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();
        client.IsConnected.Returns(true);
        tokens.Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<TokenResponse>(new TwitchAuthRejectedException("revoked")));

        await tokens.Manager.RefreshAsync();
        await Async.SettleAsync();
        Disconnected(client);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.SignInRequired, chat.State);
        await client.Received(1).DisconnectAsync();
    });

    [Fact]
    public void AppSignIn_ARenewedTokenConnectsAtOnce_AndEndsTheWaitingRetry() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        client.ConnectAsync().Returns(false);
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client, tokens.Time);
        await chat.StartAsync();
        Assert.Equal(TwitchConnectionState.Disconnected, chat.State);
        client.ConnectAsync().Returns(true);

        await tokens.Manager.RefreshAsync();
        await Async.WaitUntilAsync(() => ConnectCalls(client) == 2, because: "the new token connects at once");
        Joined(client);
        await Async.SettleAsync();
        tokens.Time.Advance(TwitchChatService.ReconnectDelay);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.Connected, chat.State);
        Assert.Equal(2, ConnectCalls(client)); // the retry that was waiting is gone
        client.Received(1).SetConnectionCredentials(Arg.Is<ConnectionCredentials>(c => IsCredentials(c, "lobosbot", "acc-2")));
    });

    [Fact]
    public void AppSignIn_ARevokedSignInEndsTheWaitingRetry() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        client.ConnectAsync().Returns(false);
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client, tokens.Time);
        await chat.StartAsync();
        tokens.Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<TokenResponse>(new TwitchAuthRejectedException("revoked")));

        await tokens.Manager.RefreshAsync();
        await Async.SettleAsync();
        tokens.Time.Advance(TwitchChatService.MaxReconnectDelay);
        await Async.SettleAsync();

        Assert.Equal(TwitchConnectionState.SignInRequired, chat.State);
        Assert.Equal(1, ConnectCalls(client)); // there is no token left to retry with
    });

    [Fact]
    public void AppSignIn_TokenChangesBeforeStartAreIgnored() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        _ = new TwitchChatService(AppSignInSettings, tokens.Manager, client);

        await tokens.Manager.InitializeAsync();
        await tokens.Manager.RefreshAsync();
        await Async.SettleAsync();

        await client.DidNotReceive().ConnectAsync();
    });

    [Fact]
    public void AppSignIn_DisposeUnsubscribesFromTheTokenManager() => UiContext.Run(async () =>
    {
        using var tokens = new Tokens(signedIn: true);
        var client = Chat.Client();
        var chat = new TwitchChatService(AppSignInSettings, tokens.Manager, client);
        await chat.StartAsync();
        await chat.DisposeAsync();
        client.ClearReceivedCalls();

        await tokens.Manager.RefreshAsync();
        await Async.SettleAsync();

        await client.DidNotReceive().ConnectAsync();
    });

    #endregion

    #region Commands (parsed by the real TwitchLib client)

    private const string Tags =
        "badge-info=;color=#FF0000;emotes=;first-msg=0;flags=;id=0f6f2a52-1;returning-chatter=0;room-id=1;" +
        "subscriber=0;tmi-sent-ts=1700000000000;turbo=0";

    private static string PrivMsg(string user, string userId, string badges, string text) =>
        $"@{Tags};badges={badges};display-name={user.ToUpperInvariant()};mod={(badges.Contains("moderator") ? 1 : 0)};" +
        $"user-id={userId};user-type= :{user}!{user}@{user}.tmi.twitch.tv PRIVMSG #lobosjr :{text}";

    private static async Task<List<ChatCommandContext>> ReceiveAsync(params string[] lines)
    {
        var client = new TwitchClient();
        client.Initialize(new ConnectionCredentials("lobosbot", "oauth:x"), "lobosjr");
        var chat = new TwitchChatService(ManualSettings, null, client);
        var received = new List<ChatCommandContext>();
        chat.CommandReceived += (_, c) => received.Add(c);

        foreach (var line in lines)
            await client.OnReadLineTestAsync(line);
        await Async.SettleAsync();

        return received;
    }

    [Fact]
    public void Commands_AreMappedWithSenderAndRole() => UiContext.Run(async () =>
    {
        var received = await ReceiveAsync(
            PrivMsg("somemod", "111", "moderator/1", "!Skip now please"),
            PrivMsg("lobosjr", "222", "broadcaster/1", "!skipvote"),
            PrivMsg("viewer", "333", "", "!vote 3"),
            PrivMsg("viewer", "333", "", "just chatting"));

        Assert.Equal(3, received.Count);
        Assert.Equal(new ChatCommandContext("skip", new[] { "now", "please" }, "111", "somemod", "SOMEMOD", true, false) with { Arguments = received[0].Arguments }, received[0]);
        Assert.Equal(new[] { "now", "please" }, received[0].Arguments);
        Assert.True(received[1].IsBroadcaster);
        Assert.Equal(("vote", "3", false, false), (received[2].Name, received[2].Arguments.Single(), received[2].IsModerator, received[2].IsBroadcaster));
    });

    #endregion
}
