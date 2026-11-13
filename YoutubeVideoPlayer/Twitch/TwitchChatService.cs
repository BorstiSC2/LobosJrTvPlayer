using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Interfaces;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Events;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>
    /// Connects the bot account to a Twitch channel's chat, receives chat commands and sends messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be created on the UI thread. <see cref="CommandReceived"/> and <see cref="StateChanged"/>
    /// are raised on that thread, so handlers can safely touch the player and the UI.
    /// </para>
    /// <para>
    /// With a <see cref="TwitchTokenManager"/> the credentials come from the app sign-in and are swapped
    /// whenever the token is renewed (TwitchLib only accepts new credentials while disconnected, so the
    /// bot reconnects briefly). Without it, the manual token from the settings is used.
    /// </para>
    /// </remarks>
    public sealed class TwitchChatService : IChatChannel, IAsyncDisposable
    {
        private readonly TwitchSettings _settings;
        private readonly TwitchTokenManager? _tokens;
        private readonly SynchronizationContext _context;
        private readonly ITwitchClient _client;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private bool _started;
        private bool _initialized;
        private bool _loginRetried;
        private bool _disposed;
        private bool _quietReconnect;
        private bool _hasConnected;

        /// <summary>Posted in chat when the bot connects for the first time after the app started.</summary>
        internal const string ConnectedMessage = "Vod-Bot online. Have fun watching!";

        /// <summary>
        /// Posted in chat when the bot is back after the connection was lost (not for a credential swap
        /// after a token renewal).
        /// </summary>
        internal const string ReconnectedMessage = "Vod-Bot lost its connection and is back online.";

        /// <summary>Posted in chat when the application closes while the bot is connected.</summary>
        internal const string GoodbyeMessage = "Vod-Bot is going offline. See you next time!";

        /// <summary>How long the goodbye gets to leave TwitchLib's send queue before the connection is closed.</summary>
        private static readonly TimeSpan GoodbyeFlushDelay = TimeSpan.FromMilliseconds(500);

        /// <param name="tokens">The app sign-in; <c>null</c> to use the manual token from the settings.</param>
        /// <param name="client">The TwitchLib client; a new <see cref="TwitchClient"/> by default.</param>
        public TwitchChatService(TwitchSettings settings, TwitchTokenManager? tokens, ITwitchClient? client = null)
        {
            _settings = settings;
            _tokens = tokens;
            _context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("TwitchChatService must be created on the UI thread.");

            _client = client ?? new TwitchClient();
            _client.OnJoinedChannel += OnJoinedChannelAsync;
            _client.OnChatCommandReceived += OnChatCommandReceivedAsync;
            _client.OnIncorrectLogin += OnIncorrectLoginAsync;
            _client.OnDisconnected += OnDisconnectedAsync;
            _client.OnReconnected += OnReconnectedAsync;
            _client.OnConnectionError += OnConnectionErrorAsync;
            _client.OnError += OnErrorAsync;

            if (_tokens != null)
            {
                _tokens.TokenChanged += OnTokenChanged;
                _tokens.SignInRequired += OnSignInRequired;
            }
        }

        public string Channel => _settings.Channel;

        /// <summary><c>true</c> if the bot signs in through the app (and can be signed in again from the UI).</summary>
        public bool UsesAppSignIn => _tokens != null;

        /// <summary>The account the bot is connected with.</summary>
        public string BotLogin { get; private set; } = string.Empty;

        public TwitchConnectionState State { get; private set; } = TwitchConnectionState.Disconnected;

        public bool IsConnected => !_disposed && State == TwitchConnectionState.Connected;

        public event EventHandler<TwitchConnectionState>? StateChanged;

        public event EventHandler<ChatCommandContext>? CommandReceived;

        public async Task StartAsync()
        {
            if (_tokens == null)
            {
                _started = true;
                await ConnectAsync(_settings.BotUsername, _settings.OAuthToken);
                return;
            }

            SetState(TwitchConnectionState.Connecting);
            var token = await _tokens.InitializeAsync();
            _started = true;

            if (token == null)
            {
                SetState(TwitchConnectionState.SignInRequired);
                return;
            }

            await ConnectAsync(token.Login, token.AccessToken);
        }

        public async Task SendMessageAsync(string message)
        {
            if (_disposed || State != TwitchConnectionState.Connected)
                return;

            try
            {
                await Task.Run(() => _client.SendMessageAsync(_settings.Channel, message));
            }
            catch (Exception ex)
            {
                Logger.Log("Sending a Twitch chat message failed.", ex);
            }
        }

        /// <summary>
        /// (Re)connects with the given credentials. An existing connection is closed first, because
        /// TwitchLib does not allow changing credentials while connected.
        /// </summary>
        private async Task ConnectAsync(string login, string accessToken)
        {
            await _connectGate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                BotLogin = login;
                var credentials = new ConnectionCredentials(login, accessToken);

                if (_client.IsConnected)
                {
                    // A planned credential swap: the chat should not see a new greeting every few hours.
                    _quietReconnect = true;
                    await Task.Run(() => _client.DisconnectAsync());
                }

                if (!_initialized)
                {
                    _client.Initialize(credentials, _settings.Channel);
                    _initialized = true;
                }
                else
                {
                    _client.SetConnectionCredentials(credentials);
                }

                SetState(TwitchConnectionState.Connecting);
                Logger.Log($"Connecting to Twitch chat #{_settings.Channel} as {login}...");

                // Run outside the UI context; TwitchLib does not need it and must not block it.
                await Task.Run(() => _client.ConnectAsync());
            }
            catch (Exception ex)
            {
                Logger.Log("Connecting to Twitch failed.", ex);
                _quietReconnect = false;
                SetState(TwitchConnectionState.Disconnected);
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private void OnTokenChanged(object? sender, TwitchToken token)
        {
            // During startup the token manager may refresh before StartAsync connects; StartAsync handles that.
            if (!_started || _disposed)
                return;

            ConnectAsync(token.Login, token.AccessToken).FireAndForget("Reconnecting to Twitch with the new token failed.");
        }

        private void OnSignInRequired(object? sender, EventArgs e)
        {
            SetState(TwitchConnectionState.SignInRequired);
            DisconnectAsync().FireAndForget("Disconnecting from Twitch failed.");
        }

        private async Task DisconnectAsync()
        {
            await _connectGate.WaitAsync();
            try
            {
                if (_client.IsConnected)
                    await Task.Run(() => _client.DisconnectAsync());
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private Task OnJoinedChannelAsync(object? sender, OnJoinedChannelArgs e)
        {
            Logger.Log($"Joined Twitch chat #{e.Channel}.");
            Post(() =>
            {
                _loginRetried = false;
                SetState(TwitchConnectionState.Connected);
            });
            return Task.CompletedTask;
        }

        private Task OnChatCommandReceivedAsync(object? sender, OnChatCommandReceivedArgs e)
        {
            var message = e.ChatMessage;
            var command = new ChatCommandContext(
                e.Command.Name.ToLowerInvariant(),
                e.Command.ArgumentsAsList.ToList(),
                message.UserId,
                message.Username.ToLowerInvariant(),
                message.DisplayName,
                message.UserDetail.IsModerator,
                message.IsBroadcaster);

            Post(() => CommandReceived?.Invoke(this, command));
            return Task.CompletedTask;
        }

        private Task OnIncorrectLoginAsync(object? sender, OnIncorrectLoginArgs e)
        {
            Post(() =>
            {
                // With app sign-in, try one token refresh before giving up (TokenChanged reconnects).
                if (_tokens != null && !_loginRetried)
                {
                    _loginRetried = true;
                    Logger.Log("Twitch rejected the bot login; refreshing the token and retrying.", e.Exception);
                    _tokens.RefreshAsync().FireAndForget("Refreshing the Twitch token after a failed login failed.");
                    return;
                }

                Logger.Log(
                    _tokens != null
                        ? "Twitch rejected the bot login again; please sign in again."
                        : "Twitch rejected the bot login. Check the OAuth token in " + TwitchSettings.FileName + ".",
                    e.Exception);
                SetState(TwitchConnectionState.LoginFailed);
            });
            return Task.CompletedTask;
        }

        private Task OnDisconnectedAsync(object? sender, OnDisconnectedArgs e)
        {
            Logger.Log("Disconnected from Twitch chat.");
            Post(() =>
            {
                if (State is not (TwitchConnectionState.LoginFailed or TwitchConnectionState.SignInRequired or TwitchConnectionState.Connecting))
                    SetState(TwitchConnectionState.Disconnected);
            });
            return Task.CompletedTask;
        }

        private Task OnReconnectedAsync(object? sender, TwitchLib.Client.Events.OnConnectedEventArgs e)
        {
            Logger.Log("Reconnected to Twitch chat.");
            Post(() => SetState(TwitchConnectionState.Connected));
            return Task.CompletedTask;
        }

        private Task OnConnectionErrorAsync(object? sender, OnConnectionErrorArgs e)
        {
            Logger.Log("Twitch connection error: " + e.Error.Message);
            return Task.CompletedTask;
        }

        private Task OnErrorAsync(object? sender, OnErrorEventArgs e)
        {
            Logger.Log("Twitch client error.", e.Exception);
            return Task.CompletedTask;
        }

        private void SetState(TwitchConnectionState state)
        {
            if (_disposed || State == state)
                return;

            State = state;
            StateChanged?.Invoke(this, state);

            if (state == TwitchConnectionState.Connected)
            {
                // Joining and reconnecting both end up here; only the first change to Connected announces itself.
                var quiet = _quietReconnect;
                var initial = !_hasConnected;
                _quietReconnect = false;
                _hasConnected = true;
                if (!quiet)
                {
                    SendMessageAsync(initial ? ConnectedMessage : ReconnectedMessage)
                        .FireAndForget("Sending the Twitch connect message failed.");
                }
            }
        }

        private void Post(Action action)
        {
            _context.Post(_ =>
            {
                if (!_disposed)
                    action();
            }, null);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            // Say goodbye while the connection is still up (SendMessageAsync is a no-op once disposed).
            if (State == TwitchConnectionState.Connected)
            {
                await SendMessageAsync(GoodbyeMessage);
                await Task.Delay(GoodbyeFlushDelay);
            }

            _disposed = true;
            if (_tokens != null)
            {
                _tokens.TokenChanged -= OnTokenChanged;
                _tokens.SignInRequired -= OnSignInRequired;
            }

            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Disconnecting from Twitch failed.", ex);
            }
        }
    }
}
