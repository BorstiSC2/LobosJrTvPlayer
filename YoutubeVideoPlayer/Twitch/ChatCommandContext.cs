using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>A chat command received from Twitch, independent of the Twitch library used.</summary>
    /// <param name="Name">Command name in lower case, without the <c>!</c>.</param>
    /// <param name="UserId">The numeric Twitch user ID of the sender.</param>
    /// <param name="Username">The login name of the sender (lower case).</param>
    public sealed record ChatCommandContext(
        string Name,
        IReadOnlyList<string> Arguments,
        string UserId,
        string Username,
        string DisplayName,
        bool IsModerator,
        bool IsBroadcaster);

    /// <summary>A chat that delivers commands and accepts messages (implemented by <see cref="TwitchChatService"/>).</summary>
    public interface IChatChannel
    {
        /// <summary>Raised on the UI thread for every chat command.</summary>
        event EventHandler<ChatCommandContext>? CommandReceived;

        /// <summary>The bot is connected to the chat and can send messages.</summary>
        bool IsConnected { get; }

        Task SendMessageAsync(string message);
    }

    public enum TwitchConnectionState
    {
        Disabled,
        Connecting,
        Connected,
        Disconnected,
        LoginFailed,

        /// <summary>App sign-in is used, but there is no valid token; the user has to sign in.</summary>
        SignInRequired
    }
}
