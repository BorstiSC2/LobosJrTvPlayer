using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.YouTube.YtDlp
{
    /// <summary>
    /// <see cref="IYouTubeService"/> on top of yt-dlp.exe: every call runs it once and reads its JSON output. yt-dlp is
    /// updated within days when YouTube changes something, so it keeps working where a library that is released less often
    /// breaks.
    /// </summary>
    /// <remarks>
    /// Every call takes a few seconds (the exe unpacks itself, then asks YouTube), so at most <see cref="MaxParallelRuns"/>
    /// run at once. A run that takes longer than <see cref="RunTimeLimit"/> is ended (playlist downloads have no other
    /// limit; stream lookups end earlier through <see cref="VideoLookup"/>).
    /// </remarks>
    public sealed class YtDlpService : IYouTubeService, IDisposable
    {
        /// <summary>How many yt-dlp processes may run at the same time.</summary>
        public const int MaxParallelRuns = 3;

        /// <summary>How long one run may take before it is ended and counts as failed.</summary>
        public static readonly TimeSpan RunTimeLimit = TimeSpan.FromMinutes(3);

        /// <summary>The longest <see cref="ResolveAsync"/> waits for the streams to become available (<c>available_at</c>).</summary>
        public static readonly TimeSpan MaxAvailabilityWait = TimeSpan.FromSeconds(30);

        /// <summary>The arguments of every run: JSON on stdout, and no settings from a yt-dlp config file of the user.</summary>
        private static readonly string[] JsonArguments = { "-J", "--ignore-config" };

        private static readonly Regex WarningSubject = new(@"^WARNING:\s*\[[^\]]+\]\s*[^\s:]+:\s*", RegexOptions.Compiled);

        private readonly IYtDlpRunner _runner;
        private readonly IReadOnlyList<string> _extraArguments;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _slots = new(MaxParallelRuns);
        private readonly CancellationTokenSource _disposal = new();
        private readonly HashSet<string> _loggedWarnings = new(StringComparer.Ordinal);
        private readonly string? _cookiesFile;
        private bool? _usedCookies;

        /// <param name="extraArguments">Added to every run after the app's own arguments (<c>ytDlp.extraArguments</c>).</param>
        /// <param name="cookiesFile">
        /// The saved YouTube sign-in (<see cref="SignIn.YouTubeSignIn.CookiesFile"/>): while it exists, every run gets a copy
        /// of it with <c>--cookies</c>. A copy, because yt-dlp writes the file back when it ends and several runs go at once.
        /// </param>
        public YtDlpService(IYtDlpRunner runner, IReadOnlyList<string>? extraArguments = null, TimeProvider? time = null, string? cookiesFile = null)
        {
            _runner = runner;
            _extraArguments = extraArguments ?? Array.Empty<string>();
            _time = time ?? TimeProvider.System;
            _cookiesFile = cookiesFile;
        }

        /// <summary>The name of a run's copy of the cookies: <c>run-&lt;guid&gt;.cookies.txt</c> next to the saved ones.</summary>
        private const string CookieCopyPattern = "run-*.cookies.txt";

        /// <summary>Deletes copies of the cookies that a run left behind (the app was ended while yt-dlp ran).</summary>
        public static void DeleteLeftoverCookieCopies(string cookiesFile)
        {
            try
            {
                var folder = Path.GetDirectoryName(cookiesFile);
                if (folder == null || !Directory.Exists(folder))
                    return;

                foreach (var copy in Directory.EnumerateFiles(folder, CookieCopyPattern))
                    File.Delete(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Log("Could not delete the leftover copies of the YouTube sign-in.", ex);
            }
        }

        public string ExecutablePath => _runner.ExecutablePath;

        /// <summary>The saved sign-in that runs get while it exists; <c>null</c> if the app's sign-in is not used.</summary>
        public string? CookiesFile => _cookiesFile;

        /// <summary>
        /// YouTube asked for a sign-in (its bot check, "Sign in to confirm you're not a bot") or yt-dlp says the saved
        /// sign-in no longer works; the text says which, for the user. Raised on a thread-pool thread, for every run that
        /// says so.
        /// </summary>
        public event EventHandler<string>? SignInRequested;

        /// <summary>Why yt-dlp's messages ask for a sign-in, or <c>null</c> if they do not.</summary>
        internal static string? SignInReason(string standardError)
        {
            if (standardError.Contains("Sign in to confirm you", StringComparison.OrdinalIgnoreCase))
                return "YouTube asks this connection to sign in (\"Sign in to confirm you're not a bot\").";

            return standardError.Contains("cookies are no longer valid", StringComparison.OrdinalIgnoreCase)
                ? "The saved YouTube sign-in no longer works."
                : null;
        }

        public async Task<YouTubeVideoDetails> GetVideoAsync(string videoIdOrUrl, CancellationToken cancellationToken)
        {
            var video = await GetVideoInfoAsync(videoIdOrUrl, cancellationToken).ConfigureAwait(false);
            return new YouTubeVideoDetails(video.Title ?? string.Empty, video.Length);
        }

        public async Task<ResolvedVideo> ResolveAsync(string videoIdOrUrl, CancellationToken cancellationToken)
        {
            var video = await GetVideoInfoAsync(videoIdOrUrl, cancellationToken).ConfigureAwait(false);
            var streams = YtDlpFormats.ToStreams(video.Formats);
            var (selected, userAgent, availableFrom) = SelectStreams(streams, videoIdOrUrl);

            // YouTube answers 403 until the streams are available (measured: 6 s after the lookup), and the player gives
            // up on a 403. So the URLs are only handed out once they work.
            var wait = availableFrom - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait.Value < MaxAvailabilityWait ? wait.Value : MaxAvailabilityWait, _time, cancellationToken).ConfigureAwait(false);

            return new ResolvedVideo(video.Title ?? string.Empty, video.Length, selected.VideoUrl, selected.AudioUrl, userAgent);
        }

        public async Task<string> GetPlaylistTitleAsync(string playlistId, CancellationToken cancellationToken)
        {
            // The first entry is enough for the title; the rest of a long playlist is not asked for.
            var playlist = await RunJsonAsync<YtDlpPlaylistInfo>(
                new[] { "--flat-playlist", "--playlist-items", "1" }, YouTubeLinks.Playlist(playlistId), cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(playlist.Title)
                ? throw new YtDlpException($"yt-dlp did not return a title for playlist '{playlistId}'.")
                : playlist.Title;
        }

        public async Task<IReadOnlyList<string>> GetPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken)
        {
            var playlist = await RunJsonAsync<YtDlpPlaylistInfo>(
                new[] { "--flat-playlist" }, YouTubeLinks.Playlist(playlistId), cancellationToken).ConfigureAwait(false);

            return (playlist.Entries ?? new List<YtDlpPlaylistEntry?>())
                .Select(entry => entry?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }

        /// <summary>Ends the runs that are still going; later calls fail at once. (The token source is not disposed, so they can still link to it.)</summary>
        public void Dispose() => _disposal.Cancel();

        /// <summary>
        /// The streams <see cref="StreamSelector"/> picks, with the User-Agent of the video stream and the time from which
        /// YouTube serves both. Formats of different YouTube player clients can need different User-Agents, and VLC fetches
        /// the audio with the video's, so the audio comes from the same client as the video when there is one.
        /// </summary>
        internal static (SelectedStreams Streams, string? UserAgent, DateTimeOffset? AvailableFrom) SelectStreams(
            IReadOnlyList<YtDlpStream> streams, string videoIdOrUrl)
        {
            var candidates = streams.Select(s => s.Candidate).ToList();
            var userAgents = new Dictionary<string, string?>(StringComparer.Ordinal);
            var availability = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            foreach (var stream in streams)
            {
                userAgents.TryAdd(stream.Candidate.Url, stream.UserAgent);
                availability.TryAdd(stream.Candidate.Url, stream.AvailableFrom);
            }

            var pool = candidates;
            var video = StreamSelector.BestVideo(candidates);
            if (video != null)
            {
                var userAgent = userAgents[video.Url];
                var audio = candidates.Where(c => c.Kind == StreamKind.AudioOnly).ToList();
                var sameClient = audio.Where(c => userAgents[c.Url] == userAgent).ToList();
                if (sameClient.Count > 0 && sameClient.Count < audio.Count)
                    pool = candidates.Where(c => c.Kind != StreamKind.AudioOnly || sameClient.Contains(c)).ToList();
            }

            var selected = StreamSelector.Select(pool);
            StreamSelector.LogSelection(candidates, selected, videoIdOrUrl, YouTubeBackend.YtDlp.DisplayName());

            var availableFrom = availability.GetValueOrDefault(selected.VideoUrl);
            if (selected.AudioUrl != null && availability.GetValueOrDefault(selected.AudioUrl) is { } audioFrom
                                          && (availableFrom == null || audioFrom > availableFrom))
                availableFrom = audioFrom;

            return (selected, userAgents.GetValueOrDefault(selected.VideoUrl), availableFrom);
        }

        private Task<YtDlpVideoInfo> GetVideoInfoAsync(string videoIdOrUrl, CancellationToken cancellationToken) =>
            RunJsonAsync<YtDlpVideoInfo>(new[] { "--no-playlist" }, YouTubeLinks.Video(videoIdOrUrl), cancellationToken);

        /// <summary>Runs yt-dlp with <c>-J</c> for <paramref name="url"/> and reads what it printed.</summary>
        private async Task<T> RunJsonAsync<T>(IReadOnlyList<string> arguments, string url, CancellationToken cancellationToken)
            where T : class
        {
            // The URL comes last, after "--", so nothing in it is taken as an option.
            var cookies = CopyCookiesForRun();
            YtDlpOutput output;
            try
            {
                var signIn = cookies == null ? Array.Empty<string>() : new[] { "--cookies", cookies };
                var all = JsonArguments.Concat(arguments).Concat(signIn).Concat(_extraArguments).Append("--").Append(url).ToList();
                output = await RunAsync(all, url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (cookies != null)
                    TryDelete(cookies);
            }

            LogWarnings(output.StandardError);
            if (SignInReason(output.StandardError) is { } reason)
                SignInRequested?.Invoke(this, reason);

            if (output.ExitCode != 0)
                throw new YtDlpException(ErrorMessage(output));

            try
            {
                return JsonConvert.DeserializeObject<T>(output.StandardOutput)
                       ?? throw new YtDlpException($"yt-dlp returned nothing for '{url}'.");
            }
            catch (JsonException ex)
            {
                throw new YtDlpException($"yt-dlp returned no usable data for '{url}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// A copy of the saved sign-in for one run, or <c>null</c> without one (or if it cannot be copied). Whether the
        /// sign-in is used is logged when it changes (a sign-in while the app runs is used from the next lookup on).
        /// </summary>
        private string? CopyCookiesForRun()
        {
            string? copy = null;
            if (_cookiesFile != null && File.Exists(_cookiesFile))
            {
                var candidate = Path.Combine(Path.GetDirectoryName(_cookiesFile)!, CookieCopyPattern.Replace("*", Guid.NewGuid().ToString("N")));
                try
                {
                    File.Copy(_cookiesFile, candidate);
                    copy = candidate;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Log($"Could not copy the YouTube sign-in ({_cookiesFile}); this lookup runs without it.", ex);
                }
            }

            var used = copy != null;
            lock (_loggedWarnings)
            {
                if (_usedCookies == used)
                    return copy;
                _usedCookies = used;
            }

            if (used)
                Logger.Log($"yt-dlp uses the YouTube sign-in saved in {_cookiesFile}.");
            else if (_cookiesFile != null)
                Logger.Log("yt-dlp runs without a YouTube sign-in (none saved).");
            return copy;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Log($"Could not delete {path}; it is deleted on the next start.", ex);
            }
        }

        /// <summary>Waits for a free slot and runs yt-dlp, ended after <see cref="RunTimeLimit"/>, on <see cref="Dispose"/>, or when cancelled.</summary>
        private async Task<YtDlpOutput> RunAsync(IReadOnlyList<string> arguments, string url, CancellationToken cancellationToken)
        {
            using var limit = new CancellationTokenSource(RunTimeLimit, _time);
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token, _disposal.Token);
            try
            {
                await _slots.WaitAsync(run.Token).ConfigureAwait(false);
                try
                {
                    return await _runner.RunAsync(arguments, run.Token).ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"yt-dlp took longer than {RunTimeLimit.TotalMinutes:0} minutes for '{url}'.");
            }
        }

        /// <summary>
        /// Logs each kind of warning once per run of the app. The video ID is cut off for the comparison, so a warning that
        /// comes with every video (e.g. that no JavaScript runtime was found) is logged only for the first one.
        /// </summary>
        private void LogWarnings(string standardError)
        {
            foreach (var line in Lines(standardError).Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal)))
            {
                // yt-dlp needs ffmpeg only to merge what it downloads; the app downloads nothing, so this one says nothing.
                if (line.Contains("ffmpeg not found", StringComparison.OrdinalIgnoreCase))
                    continue;


                bool first;
                lock (_loggedWarnings)
                    first = _loggedWarnings.Add(WarningSubject.Replace(line, "WARNING: "));

                if (first)
                    Logger.Log($"yt-dlp: {line}");
            }
        }

        /// <summary>yt-dlp's last <c>ERROR:</c> line without the prefix, else its last line, else the exit code.</summary>
        internal static string ErrorMessage(YtDlpOutput output)
        {
            var lines = Lines(output.StandardError).ToList();
            var error = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
            if (error != null)
                return error["ERROR:".Length..].Trim();

            return lines.Count > 0 ? lines[^1] : $"yt-dlp ended with exit code {output.ExitCode}.";
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
    }
}
