using System;
using System.IO;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch.Auth
{
    /// <summary>
    /// Keeps the bot's token in <c>twitchtoken.json</c> next to the executable (not encrypted, so the
    /// app folder can be moved to another computer). Writes are atomic.
    /// </summary>
    public sealed class TwitchTokenStore
    {
        public const string FileName = "twitchtoken.json";

        private readonly string _path;

        public TwitchTokenStore(string? baseDirectory = null)
        {
            _path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, FileName);
        }

        /// <summary>Returns the stored token, or <c>null</c> if there is none (or it cannot be read).</summary>
        public TwitchToken? Load()
        {
            try
            {
                var token = JsonFile.ReadOrQuarantine<TwitchToken>(_path, "a new Twitch sign-in is needed.");
                return token == null || string.IsNullOrEmpty(token.RefreshToken) ? null : token;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not readable right now (locked, no access) is no sign of a damaged file: leave it alone.
                Logger.Log($"Could not read {FileName} right now; the file is left as it is.", ex);
                return null;
            }
        }

        public void Save(TwitchToken token) => JsonFile.WriteAtomic(_path, token);
    }
}
