using System;

namespace YouTubeVideoPlayer.Twitch.Auth
{
    /// <summary>How long the bot's access token stays valid. Never exposes the token itself.</summary>
    public interface ITokenLifetime
    {
        /// <summary>Time left until the current token expires (negative once expired); <c>null</c> if not signed in.</summary>
        TimeSpan? RemainingValidity { get; }
    }
}
