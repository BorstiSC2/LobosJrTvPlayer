using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;
using TwitchLib.Communication.Models;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>
/// The connection under a real <c>TwitchClient</c>: records every line the client sends and lets a test play the
/// part of the chat server (welcome, join confirmations). For tests that need TwitchLib's own behaviour, such as
/// which channels it joins after a reconnect, which a substitute <c>ITwitchClient</c> cannot show.
/// </summary>
public sealed class FakeChatTransport : IClient
{
    private readonly object _gate = new();
    private readonly List<string> _sent = new();
    private int _echoedJoins;

    public bool IsConnected { get; private set; }

    /// <summary>When <c>false</c>, <see cref="OpenAsync"/> fails like a connection to an unreachable server.</summary>
    public bool AcceptConnections { get; set; } = true;

    public IClientOptions Options { get; } = new ClientOptions();

    public event AsyncEventHandler<OnConnectedEventArgs>? OnConnected;

    public event AsyncEventHandler<OnDisconnectedEventArgs>? OnDisconnected;

    public event AsyncEventHandler<OnMessageEventArgs>? OnMessage;

    // TwitchLib subscribes to these, the fake never raises them.
    public event AsyncEventHandler<OnErrorEventArgs>? OnError { add { } remove { } }

    public event AsyncEventHandler<OnFatalErrorEventArgs>? OnFatality { add { } remove { } }

    public event AsyncEventHandler<OnSendFailedEventArgs>? OnSendFailed { add { } remove { } }

    public event AsyncEventHandler<OnConnectedEventArgs>? OnReconnected { add { } remove { } }

    /// <summary>The <c>JOIN</c> commands sent since the last <see cref="ClearSent"/>, e.g. <c>JOIN #lobosjr</c>.</summary>
    public IReadOnlyList<string> JoinCommands
    {
        get
        {
            lock (_gate)
                return _sent.Where(line => line.StartsWith("JOIN ", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        }
    }

    public void ClearSent()
    {
        lock (_gate)
        {
            _sent.Clear();
            _echoedJoins = 0;
        }
    }

    public async Task<bool> OpenAsync()
    {
        if (!AcceptConnections)
            return false;

        IsConnected = true;
        if (OnConnected != null)
            await OnConnected.Invoke(this, new OnConnectedEventArgs());

        return true;
    }

    public Task<bool> ReconnectAsync() => Task.FromResult(true);

    public async Task CloseAsync()
    {
        IsConnected = false;
        if (OnDisconnected != null)
            await OnDisconnected.Invoke(this, new OnDisconnectedEventArgs());
    }

    public Task<bool> SendAsync(string message)
    {
        lock (_gate)
            _sent.Add(message);

        return Task.FromResult(true);
    }

    /// <summary>The server accepts the login (the first lines of a connection).</summary>
    public async Task WelcomeAsync(string login)
    {
        await FeedAsync($":tmi.twitch.tv 001 {login} :Welcome, GLHF!");
        await FeedAsync($":tmi.twitch.tv 376 {login} :>");
    }

    /// <summary>The server confirms every <c>JOIN</c> that was sent and not confirmed yet.</summary>
    public async Task EchoJoinsAsync(string login)
    {
        string[] joins;
        lock (_gate)
        {
            joins = _sent.Where(line => line.StartsWith("JOIN ", StringComparison.Ordinal)).Skip(_echoedJoins).ToArray();
            _echoedJoins += joins.Length;
        }

        foreach (var join in joins)
            await FeedAsync($":{login}!{login}@{login}.tmi.twitch.tv {join.Trim()}");
    }

    private async Task FeedAsync(string line)
    {
        if (OnMessage != null)
            await OnMessage.Invoke(this, new OnMessageEventArgs(line));
    }

    public void Dispose()
    {
    }
}
