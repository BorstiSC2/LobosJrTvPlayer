using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YouTubeVideoPlayer.Twitch.Auth
{
    /// <summary>
    /// HTTP calls to Twitch's OAuth server for a public client: device code sign-in, token refresh
    /// (no client secret needed) and token validation.
    /// </summary>
    public sealed class TwitchAuthClient : ITwitchAuthClient
    {
        public const string Scopes = "chat:read chat:edit";

        private const string BaseUrl = "https://id.twitch.tv/oauth2/";

        private readonly HttpClient _http;
        private readonly string _clientId;

        public TwitchAuthClient(string clientId, HttpMessageHandler? handler = null)
        {
            _clientId = clientId;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>Starts a device code sign-in (<c>POST /oauth2/device</c>).</summary>
        public async Task<DeviceCodeInfo> StartDeviceFlowAsync(CancellationToken cancellationToken)
        {
            var (status, json) = await PostAsync("device", new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scopes"] = Scopes
            }, cancellationToken);

            if (status != HttpStatusCode.OK)
                throw new InvalidOperationException($"Twitch refused to start the sign-in: {Message(json)} (is the client ID correct and the application of type 'Public'?)");

            return new DeviceCodeInfo(
                (string?)json["device_code"] ?? throw new InvalidOperationException("Twitch returned no device code."),
                (string?)json["user_code"] ?? string.Empty,
                (string?)json["verification_uri"] ?? "https://www.twitch.tv/activate",
                TimeSpan.FromSeconds((int?)json["expires_in"] ?? 1800),
                TimeSpan.FromSeconds(Math.Max(1, (int?)json["interval"] ?? 5)));
        }

        /// <summary>Asks once whether the user has confirmed the device code (<c>POST /oauth2/token</c>).</summary>
        public async Task<DevicePollResult> PollDeviceTokenAsync(string deviceCode, CancellationToken cancellationToken)
        {
            var (status, json) = await PostAsync("token", new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scopes"] = Scopes,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, cancellationToken);

            if (status == HttpStatusCode.OK)
                return new DevicePollResult(DevicePollStatus.Success, ParseToken(json));

            var message = Message(json).ToLowerInvariant();
            if (message.Contains("authorization_pending"))
                return new DevicePollResult(DevicePollStatus.Pending);
            if (message.Contains("slow_down"))
                return new DevicePollResult(DevicePollStatus.SlowDown);
            if (message.Contains("denied"))
                return new DevicePollResult(DevicePollStatus.Denied);
            if (message.Contains("invalid device code") || message.Contains("expired"))
                return new DevicePollResult(DevicePollStatus.Expired);

            throw new InvalidOperationException($"Twitch sign-in failed: {Message(json)}");
        }

        /// <summary>
        /// Exchanges a refresh token for a new access and refresh token. Throws
        /// <see cref="TwitchAuthRejectedException"/> if Twitch rejects the refresh token.
        /// </summary>
        public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            var (status, json) = await PostAsync("token", new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }, cancellationToken);

            if (status == HttpStatusCode.OK)
                return ParseToken(json);

            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TwitchAuthRejectedException($"Twitch rejected the refresh token: {Message(json)}");

            throw new HttpRequestException($"Refreshing the Twitch token failed with {(int)status}: {Message(json)}");
        }

        /// <summary>Validates an access token. Returns <c>null</c> if Twitch reports it as invalid.</summary>
        public async Task<TokenValidation?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await ReadJsonAsync(response, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Validating the Twitch token failed with {(int)response.StatusCode}: {Message(json)}");

            return new TokenValidation(
                (string?)json["login"] ?? string.Empty,
                (string?)json["user_id"] ?? string.Empty,
                TimeSpan.FromSeconds((int?)json["expires_in"] ?? 0),
                ReadScopes(json["scopes"]));
        }

        private async Task<(HttpStatusCode Status, JObject Json)> PostAsync(
            string endpoint,
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(BaseUrl + endpoint, content, cancellationToken);
            return (response.StatusCode, await ReadJsonAsync(response, cancellationToken));
        }

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new JObject { ["message"] = body };
            }
        }

        private static TokenResponse ParseToken(JObject json) => new(
            (string?)json["access_token"] ?? throw new InvalidOperationException("Twitch returned no access token."),
            (string?)json["refresh_token"] ?? throw new InvalidOperationException("Twitch returned no refresh token."),
            TimeSpan.FromSeconds((int?)json["expires_in"] ?? 0),
            ReadScopes(json["scope"]));

        private static IReadOnlyList<string> ReadScopes(JToken? token) =>
            token is JArray array ? array.Select(t => (string?)t ?? string.Empty).ToList() : Array.Empty<string>();

        private static string Message(JObject json) =>
            (string?)json["message"] ?? (string?)json["error"] ?? json.ToString(Newtonsoft.Json.Formatting.None);

        public void Dispose() => _http.Dispose();
    }
}
