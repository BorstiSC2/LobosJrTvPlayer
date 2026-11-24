using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>What the Twitch API needs to answer: the application's client ID and a token of the bot.</summary>
    public sealed record TwitchApiCredentials(string ClientId, string AccessToken);

    /// <summary>Whether a channel is live; <see cref="IsLive"/> is <c>null</c> when it could not be told (then <see cref="Problem"/> says why).</summary>
    public sealed record LiveStatus(bool? IsLive, string? Problem = null)
    {
        public static LiveStatus Unknown(string problem) => new(null, problem);
    }

    /// <summary>Asks Twitch whether a channel is live (behind an interface, so the watcher can be tested).</summary>
    public interface ITwitchStreamClient : IDisposable
    {
        Task<LiveStatus> GetStatusAsync(string login, CancellationToken cancellationToken);
    }

    /// <summary>
    /// <c>GET https://api.twitch.tv/helix/streams?user_login=...</c>: the channel is live if Twitch lists a stream for it.
    /// It needs the application's client ID and a user (or app) token, but no scope, so the bot's chat token is enough.
    /// It does not touch the chat: the bot never joins the channel it checks.
    /// </summary>
    public sealed class TwitchStreamClient : ITwitchStreamClient
    {
        private const string StreamsUrl = "https://api.twitch.tv/helix/streams?user_login=";

        private readonly Func<CancellationToken, Task<TwitchApiCredentials?>> _credentials;
        private readonly HttpClient _http;

        /// <param name="credentials">The client ID and the current token, asked for on every check; <c>null</c> while there is none.</param>
        public TwitchStreamClient(Func<CancellationToken, Task<TwitchApiCredentials?>> credentials, HttpMessageHandler? handler = null)
        {
            _credentials = credentials;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<LiveStatus> GetStatusAsync(string login, CancellationToken cancellationToken)
        {
            TwitchApiCredentials? credentials;
            try
            {
                credentials = await _credentials(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return LiveStatus.Unknown($"no client ID for the Twitch API ({ex.Message})");
            }

            if (credentials == null || credentials.ClientId.Length == 0 || credentials.AccessToken.Length == 0)
                return LiveStatus.Unknown("no client ID or token for the Twitch API yet");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, StreamsUrl + Uri.EscapeDataString(login));
                request.Headers.Add("Client-Id", credentials.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WithoutPrefix(credentials.AccessToken));

                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return LiveStatus.Unknown("the Twitch API did not accept the token (it is renewed, the next check tries again)");
                if (!response.IsSuccessStatusCode)
                    return LiveStatus.Unknown($"the Twitch API answered {(int)response.StatusCode}");

                var data = JObject.Parse(body)["data"] as JArray;
                if (data == null)
                    return LiveStatus.Unknown("the Twitch API answered without a list of streams");

                return new LiveStatus(data.Count > 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Network errors, the timeout, an answer that is not JSON: try again next time.
                return LiveStatus.Unknown($"the Twitch API could not be asked ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// The credentials of the bot's sign-in: with app sign-in the configured client ID and the token manager's current
        /// (renewed) token; with a manual token, the client ID Twitch reports for it (<c>/oauth2/validate</c>), asked once
        /// and kept (asked again while it could not be found out).
        /// </summary>
        public static Func<CancellationToken, Task<TwitchApiCredentials?>> CredentialsFor(
            TwitchSettings settings, TwitchTokenManager? tokens, ITwitchAuthClient? validator = null)
        {
            if (tokens != null)
            {
                return _ => Task.FromResult(tokens.Current is { } token
                    ? new TwitchApiCredentials(settings.SignIn.ClientId, token.AccessToken)
                    : null);
            }

            var manualToken = WithoutPrefix(settings.SignIn.OAuthToken);
            validator ??= new TwitchAuthClient(string.Empty); // validating needs no client ID
            string? clientId = null;
            return async cancellationToken =>
            {
                if (manualToken.Length == 0)
                    return null;

                if (clientId == null)
                {
                    var validation = await validator.ValidateAsync(manualToken, cancellationToken);
                    if (validation is { ClientId.Length: > 0 })
                        clientId = validation.ClientId;
                }

                return clientId == null ? null : new TwitchApiCredentials(clientId, manualToken);
            };
        }

        /// <summary>A manual chat token may start with <c>oauth:</c>; the API wants the token alone.</summary>
        internal static string WithoutPrefix(string token) =>
            token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token["oauth:".Length..] : token;

        public void Dispose() => _http.Dispose();
    }
}
