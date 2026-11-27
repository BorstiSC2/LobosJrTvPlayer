using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace YouTubeVideoPlayer.YouTube.SignIn
{
    /// <summary>One cookie of the sign-in, as the browser has it.</summary>
    /// <param name="Expires"><c>null</c> for a session cookie.</param>
    public sealed record SignInCookie(
        string Domain, string Path, string Name, string Value, bool Secure, bool HttpOnly, DateTimeOffset? Expires);

    /// <summary>
    /// The YouTube sign-in for yt-dlp (prototype): YouTube asks some connections to sign in ("Sign in to confirm you're
    /// not a bot"). <see cref="UI.YouTubeSignInDialog"/> signs in with Google in a window of its own (WebView2, with its
    /// own profile in <see cref="ProfileFolder"/>) and saves YouTube's cookies to <see cref="CookiesFile"/>, which
    /// <see cref="YtDlp.YtDlpService"/> passes to yt-dlp. Both are in the Windows profile, not in the app folder, so they
    /// are never copied along with the app.
    /// </summary>
    public static class YouTubeSignIn
    {
        /// <summary>Google's sign-in for YouTube, which comes back to youtube.com when done.</summary>
        public const string SignInUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

        /// <summary>
        /// A page of YouTube without YouTube's scripts: the cookies are read there. On a YouTube page the scripts rotate
        /// the sign-in cookies, and a copy taken before that stops working (yt-dlp's wiki recommends the same).
        /// </summary>
        public const string QuietYouTubePage = "https://www.youtube.com/robots.txt";

        /// <summary>The cookies of youtube.com are read for this address.</summary>
        public const string CookieUrl = "https://www.youtube.com/";

        /// <summary>Cookies that only a signed-in session has.</summary>
        private static readonly string[] SessionCookieNames = { "LOGIN_INFO", "__Secure-3PSID", "SAPISID" };

        /// <summary><c>%LOCALAPPDATA%\YouTubeVideoPlayer</c>.</summary>
        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeVideoPlayer");

        /// <summary>The sign-in window's own browser profile (WebView2 user data).</summary>
        public static string ProfileFolder => Path.Combine(Folder, "YouTubeSignIn");

        /// <summary>The saved cookies, in the Netscape format yt-dlp reads with <c>--cookies</c>.</summary>
        public static string CookiesFile => Path.Combine(Folder, "youtube-cookies.txt");

        /// <summary>Whether the cookies belong to a signed-in Google account.</summary>
        public static bool IsSignedIn(IEnumerable<SignInCookie> cookies) =>
            cookies.Any(c => SessionCookieNames.Contains(c.Name, StringComparer.Ordinal) && c.Value.Length > 0);

        /// <summary>
        /// The cookies as a Netscape cookie file (the format of curl and yt-dlp): one line per cookie, tab-separated
        /// domain, include-subdomains, path, secure, expiry (Unix seconds, 0 for a session cookie), name and value;
        /// HttpOnly cookies with the <c>#HttpOnly_</c> prefix. Cookies with a tab or line break are left out (the format
        /// cannot hold them).
        /// </summary>
        public static string ToNetscapeFile(IEnumerable<SignInCookie> cookies)
        {
            var text = new StringBuilder();
            text.Append("# Netscape HTTP Cookie File\n");
            text.Append("# Written by YouTubeVideoPlayer's YouTube sign-in. It signs in to a Google account: do not share it.\n\n");
            foreach (var cookie in cookies)
            {
                if (new[] { cookie.Domain, cookie.Path, cookie.Name, cookie.Value }.Any(v => v.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0)
                    || cookie.Domain.Length == 0 || cookie.Name.Length == 0)
                    continue;

                var expires = cookie.Expires is { } at && at > DateTimeOffset.UnixEpoch ? at.ToUnixTimeSeconds() : 0;
                text.Append(cookie.HttpOnly ? "#HttpOnly_" : string.Empty)
                    .Append(cookie.Domain).Append('\t')
                    .Append(cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                    .Append(cookie.Path.Length == 0 ? "/" : cookie.Path).Append('\t')
                    .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                    .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(cookie.Name).Append('\t')
                    .Append(cookie.Value).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>Writes the cookie file (first to a temporary file, so a reader never sees half of it).</summary>
        public static void SaveCookies(IEnumerable<SignInCookie> cookies, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToNetscapeFile(cookies), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
