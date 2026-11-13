using System;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!token</c> (moderators, broadcaster and privileged users): tells how long the bot's current
    /// access token stays valid. The token itself is never posted.
    /// </summary>
    public sealed class TokenCommand : IChatCommand
    {
        private readonly ITokenLifetime? _tokens;

        /// <param name="tokens">The app sign-in; <c>null</c> if the bot uses a manual token.</param>
        public TokenCommand(ITokenLifetime? tokens, TimeSpan cooldown)
        {
            _tokens = tokens;
            Cooldown = cooldown;
        }

        public string Name => "token";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) => Task.FromResult<string?>(Describe());

        private string Describe()
        {
            if (_tokens == null)
                return "The bot uses a manual token that is not renewed automatically; its remaining time is unknown.";

            var remaining = _tokens.RemainingValidity;
            if (remaining == null)
                return "The bot is not signed in.";

            if (remaining <= TimeSpan.Zero)
                return "The bot token has expired and is being renewed.";

            return $"The bot token is valid for {Format(remaining.Value)}. It is renewed automatically shortly before it expires.";
        }

        private static string Format(TimeSpan time)
        {
            var hours = (int)time.TotalHours;
            var minutes = time.Minutes;
            if (hours > 0)
                return $"another {hours} h {minutes} min";

            return minutes > 0 ? $"another {minutes} min" : "less than another minute";
        }
    }
}
