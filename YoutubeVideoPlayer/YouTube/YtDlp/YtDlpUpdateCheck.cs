using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>
    /// Looks whether a newer yt-dlp has been released than the one installed (<c>ytDlp.checkForUpdates</c>). The app never
    /// updates yt-dlp itself; it only tells the user, because an old yt-dlp stops finding videos when YouTube changes.
    /// </summary>
    public static class YtDlpUpdateCheck
    {
        /// <summary>The latest stable release, from GitHub's API (no key needed for one request per start).</summary>
        public const string LatestReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        public const string ReleasesPage = "https://github.com/yt-dlp/yt-dlp/releases";

        /// <summary>How long the whole check may take (yt-dlp's first start unpacks it, which can take a few seconds).</summary>
        public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The text to show when a newer yt-dlp than the installed one has been released; <c>null</c> when it is current
        /// (a nightly build newer than the latest release counts as current) or when the check failed (only logged).
        /// </summary>
        public static async Task<string?> CheckAsync(IYtDlpRunner runner, HttpClient http, TimeProvider time, CancellationToken cancellationToken)
        {
            using var limit = new CancellationTokenSource(TimeLimit, time);
            using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
            var installedTask = InstalledVersionAsync(runner, check.Token);
            var latestTask = LatestVersionAsync(http, check.Token);
            try
            {
                var installed = await installedTask.WaitAsync(check.Token).ConfigureAwait(false);
                var latest = await latestTask.WaitAsync(check.Token).ConfigureAwait(false);

                var installedVersion = Parse(installed) ?? throw new FormatException($"yt-dlp --version printed '{installed}'.");
                var latestVersion = Parse(latest) ?? throw new FormatException($"The latest yt-dlp release is called '{latest}'.");
                if (Compare(installedVersion, latestVersion) >= 0)
                {
                    Logger.Log($"yt-dlp {installed} is installed; the latest release is {latest}.");
                    return null;
                }

                Logger.Log($"yt-dlp {installed} is installed, but {latest} has been released; the user is told at startup.");
                var folder = Path.GetDirectoryName(runner.ExecutablePath);
                return $"A newer yt-dlp is available: {latest} (installed: {installed}).{Environment.NewLine}{Environment.NewLine}" +
                       "YouTube changes often, and an old yt-dlp can stop finding videos. To update it, run \"yt-dlp -U\" " +
                       $"in {(string.IsNullOrEmpty(folder) ? "the app folder" : folder)}, or download yt-dlp.exe from {ReleasesPage}." +
                       $"{Environment.NewLine}{Environment.NewLine}The app keeps using the installed version until then.";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _ = installedTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = latestTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                Logger.Log(limit.IsCancellationRequested
                    ? $"Could not check for a newer yt-dlp: no answer within {TimeLimit.TotalSeconds:0} seconds."
                    : $"Could not check for a newer yt-dlp: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> InstalledVersionAsync(IYtDlpRunner runner, CancellationToken cancellationToken)
        {
            var output = await runner.RunAsync(new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (output.ExitCode != 0)
                throw new YtDlpException($"yt-dlp --version failed: {YtDlpService.ErrorMessage(output)}");

            return output.StandardOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
                   ?? throw new YtDlpException("yt-dlp --version printed nothing.");
        }

        private static async Task<string> LatestVersionAsync(HttpClient http, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            // GitHub refuses requests without a User-Agent; a generic one names nothing but the app.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("YouTubeVideoPlayer", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var tag = json.Value<string>("tag_name");
            return string.IsNullOrWhiteSpace(tag) ? throw new FormatException("GitHub's answer has no tag_name.") : tag.Trim();
        }

        /// <summary><c>2026.11.23</c> → [2026, 11, 23]; a nightly <c>2026.11.25.232953</c> has a fourth part. <c>null</c> if it is no version.</summary>
        internal static long[]? Parse(string version)
        {
            var parts = version.Trim().TrimStart('v', 'V').Split('.');
            var numbers = new long[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                    return null;
            }

            return numbers;
        }

        internal static int Compare(long[] a, long[] b)
        {
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var difference = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (difference != 0)
                    return difference;
            }

            return 0;
        }
    }
}
