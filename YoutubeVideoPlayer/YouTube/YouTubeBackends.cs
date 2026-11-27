using System;
using System.IO;
using System.Linq;
using YoutubeExplode;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.YouTube
{
    /// <summary>
    /// The <see cref="IYouTubeService"/> the settings ask for (<c>youtube</c> in <c>appsettings.json</c>), and what it owns.
    /// <list type="bullet">
    /// <item><c>ytDlp</c>: yt-dlp, with <c>fallback</c> then YoutubeExplode. Without the exe YoutubeExplode only, and
    /// <see cref="Problem"/> says so.</item>
    /// <item><c>youtubeExplode</c>: YoutubeExplode, with <c>fallback</c> then yt-dlp if the exe is there (so putting
    /// yt-dlp.exe next to the app is enough for a fallback).</item>
    /// </list>
    /// </summary>
    public sealed class YouTubeBackends : IDisposable
    {
        private readonly YoutubeClient _youtubeClient;
        private readonly YtDlpService? _ytDlp;

        private YouTubeBackends(IYouTubeService service, string description, YoutubeClient youtubeClient, YtDlpService? ytDlp,
            IYtDlpRunner? ytDlpRunner, string? problem)
        {
            Service = service;
            Description = description;
            _youtubeClient = youtubeClient;
            _ytDlp = ytDlp;
            YtDlpRunner = ytDlpRunner;
            Problem = problem;
            if (ytDlp != null)
                ytDlp.SignInRequested += (_, reason) => SignInRequested?.Invoke(this, reason);
        }

        /// <summary>
        /// yt-dlp is used and takes the sign-in of the app's YouTube sign-in window (not cookies of the user's own from
        /// <c>ytDlp.extraArguments</c>): the window is worth offering.
        /// </summary>
        public bool OffersSignIn => _ytDlp?.CookiesFile != null;

        /// <summary>YouTube asked yt-dlp for a sign-in (see <see cref="YtDlpService.SignInRequested"/>); on a thread-pool thread.</summary>
        public event EventHandler<string>? SignInRequested;

        /// <summary>What the app looks videos and playlists up with.</summary>
        public IYouTubeService Service { get; }

        /// <summary>What <see cref="Service"/> is, for the log (e.g. "yt-dlp (C:\...\yt-dlp.exe), falling back to YoutubeExplode").</summary>
        public string Description { get; }

        /// <summary>yt-dlp, if it is used (first or as fallback); <c>null</c> otherwise.</summary>
        public IYtDlpRunner? YtDlpRunner { get; }

        /// <summary>
        /// yt-dlp was chosen but its exe was not found, so YoutubeExplode is used: a message for the user (an
        /// initialization failure); <c>null</c> if everything is as configured.
        /// </summary>
        public string? Problem { get; }

        /// <param name="baseDirectory">The app folder, where a relative <c>ytDlp.path</c> is looked for.</param>
        /// <param name="pathVariable">The folders searched for a bare file name; the process's <c>PATH</c> by default.</param>
        /// <param name="createRunner">Runs the found exe; a <see cref="YtDlpProcess"/> by default (tests pass a fake).</param>
        /// <param name="cookiesFile">
        /// The saved YouTube sign-in that yt-dlp gets while it exists (<see cref="SignIn.YouTubeSignIn.CookiesFile"/> by
        /// default). Not used when <c>ytDlp.extraArguments</c> pass cookies of their own.
        /// </param>
        public static YouTubeBackends Create(
            AppSettings.YouTubeSettings settings,
            string? baseDirectory = null,
            TimeProvider? time = null,
            string? pathVariable = null,
            Func<string, IYtDlpRunner>? createRunner = null,
            string? cookiesFile = null)
        {
            var youtubeClient = new YoutubeClient();
            var explode = new YoutubeExplodeService(youtubeClient);
            var explodeName = YouTubeBackend.YoutubeExplode.DisplayName();
            var ytDlpName = YouTubeBackend.YtDlp.DisplayName();

            var wantsYtDlp = settings.SelectedBackend == YouTubeBackend.YtDlp || settings.Fallback;
            var exe = wantsYtDlp
                ? YtDlpLocator.Find(settings.YtDlp.Path, baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, pathVariable)
                : null;
            var runner = exe == null ? null : (createRunner ?? (path => new YtDlpProcess(path)))(exe);
            var ownCookies = settings.YtDlp.ExtraArguments.Any(a =>
                a.Equals("--cookies", StringComparison.OrdinalIgnoreCase) || a.Equals("--cookies-from-browser", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--no-cookies", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-cookies-from-browser", StringComparison.OrdinalIgnoreCase));
            var signIn = ownCookies ? null : cookiesFile ?? SignIn.YouTubeSignIn.CookiesFile;
            if (runner != null && signIn != null)
                YtDlpService.DeleteLeftoverCookieCopies(signIn);
            var ytDlp = runner == null ? null : new YtDlpService(runner, settings.YtDlp.ExtraArguments, time, signIn);

            if (settings.SelectedBackend == YouTubeBackend.YtDlp)
            {
                if (ytDlp == null)
                {
                    var where = Path.IsPathRooted(settings.YtDlp.Path) ? string.Empty : " in the app folder or on PATH";
                    var problem = $"yt-dlp is chosen in {AppSettings.FileName} (youtube.backend), but '{settings.YtDlp.Path}' was not found{where}. " +
                                  $"Videos and playlists are looked up with {explodeName} instead.";
                    return new YouTubeBackends(explode, $"{explodeName} (yt-dlp.exe not found)", youtubeClient, null, null, problem);
                }

                return settings.Fallback
                    ? new YouTubeBackends(new FallbackYouTubeService(ytDlp, ytDlpName, explode, explodeName, time),
                        $"{ytDlpName} ({exe}), falling back to {explodeName}", youtubeClient, ytDlp, runner, null)
                    : new YouTubeBackends(ytDlp, $"{ytDlpName} ({exe})", youtubeClient, ytDlp, runner, null);
            }

            if (ytDlp != null)
            {
                return new YouTubeBackends(new FallbackYouTubeService(explode, explodeName, ytDlp, ytDlpName, time),
                    $"{explodeName}, falling back to {ytDlpName} ({exe})", youtubeClient, ytDlp, runner, null);
            }

            var description = settings.Fallback ? $"{explodeName} (no fallback: yt-dlp.exe not found)" : explodeName;
            return new YouTubeBackends(explode, description, youtubeClient, null, null, null);
        }

        public void Dispose()
        {
            _ytDlp?.Dispose();
            _youtubeClient.Dispose();
        }
    }
}
