using System;
using System.Collections.Generic;

namespace YouTubeVideoPlayer.Twitch.Auth
{
    /// <summary>The bot's user access token, as stored in <c>twitchtoken.json</c>.</summary>
    public sealed record TwitchToken
    {
        public string AccessToken { get; init; } = string.Empty;

        /// <summary>Single use: every refresh returns a new refresh token that replaces this one.</summary>
        public string RefreshToken { get; init; } = string.Empty;

        public DateTime ExpiresAtUtc { get; init; }

        /// <summary>Login name of the account the token belongs to.</summary>
        public string Login { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;

        public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    }

    /// <summary>What the user has to do to sign in with the device code flow.</summary>
    /// <param name="UserCode">The code to enter on the activation page.</param>
    /// <param name="VerificationUri">The activation page (already contains the code).</param>
    public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval);

    /// <summary>A token response from <c>/oauth2/token</c>.</summary>
    public sealed record TokenResponse(string AccessToken, string RefreshToken, TimeSpan ExpiresIn, IReadOnlyList<string> Scopes);

    /// <summary>The result of <c>/oauth2/validate</c> for a valid token.</summary>
    public sealed record TokenValidation(string Login, string UserId, TimeSpan ExpiresIn, IReadOnlyList<string> Scopes);

    public enum DevicePollStatus
    {
        /// <summary>The user has not confirmed yet.</summary>
        Pending,

        /// <summary>Polling too fast; wait longer between requests.</summary>
        SlowDown,

        /// <summary>The device code expired or was already used.</summary>
        Expired,

        /// <summary>The user declined the authorization.</summary>
        Denied,

        Success
    }

    public sealed record DevicePollResult(DevicePollStatus Status, TokenResponse? Token = null);

    /// <summary>The calls to Twitch's OAuth server (implemented by <see cref="TwitchAuthClient"/>).</summary>
    public interface ITwitchAuthClient : IDisposable
    {
        /// <summary>Starts a device code sign-in.</summary>
        System.Threading.Tasks.Task<DeviceCodeInfo> StartDeviceFlowAsync(System.Threading.CancellationToken cancellationToken);

        /// <summary>Asks once whether the user has confirmed the device code.</summary>
        System.Threading.Tasks.Task<DevicePollResult> PollDeviceTokenAsync(string deviceCode, System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// Exchanges a refresh token for new tokens. Throws <see cref="TwitchAuthRejectedException"/>
        /// if Twitch rejects the refresh token.
        /// </summary>
        System.Threading.Tasks.Task<TokenResponse> RefreshAsync(string refreshToken, System.Threading.CancellationToken cancellationToken);

        /// <summary>Validates an access token; <c>null</c> if Twitch reports it as invalid.</summary>
        System.Threading.Tasks.Task<TokenValidation?> ValidateAsync(string accessToken, System.Threading.CancellationToken cancellationToken);
    }

    /// <summary>Twitch rejected a refresh token or device code; the user has to sign in again.</summary>
    public sealed class TwitchAuthRejectedException : Exception
    {
        public TwitchAuthRejectedException(string message) : base(message)
        {
        }
    }
}
