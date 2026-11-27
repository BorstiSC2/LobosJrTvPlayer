using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.YouTube;
using YouTubeVideoPlayer.YouTube.SignIn;
using YouTubeVideoPlayer.YouTube.YtDlp;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Prototype: signs in to YouTube with Google in a WebView2 window with a profile of its own, and saves YouTube's
    /// cookies for yt-dlp (<see cref="YouTubeSignIn"/>). The window is a plain WebView2: Google may refuse sign-in in it
    /// ("This browser or app may not be secure"), which this prototype is meant to find out. The password is typed into
    /// Google's page; the app only reads the cookies afterwards.
    /// </summary>
    /// <remarks>
    /// After the sign-in Google returns to youtube.com. The window then goes to <see cref="YouTubeSignIn.QuietYouTubePage"/>
    /// (no YouTube scripts, which would rotate the cookies), saves the cookies, and shows a page of its own, so YouTube is
    /// not left open. When the profile is signed in already, the cookies are saved again without loading any page.
    /// </remarks>
    public sealed class YouTubeSignInDialog : Form
    {
        /// <summary>The video "Test with yt-dlp" looks up (public and long-standing; the same as in the tests).</summary>
        private const string TestVideoId = "0_twHqFV6Lo";

        private readonly string? _ytDlpPath;
        private readonly string? _reason;
        private readonly string _cookiesFile;
        private readonly string _profileFolder;
        private readonly WebView2 _web;
        private readonly Label _status;
        private readonly Button _testButton;
        private readonly Button _signOutButton;
        private readonly Button _closeButton;
        private bool _signedIn;
        private bool _busy;

        /// <param name="ytDlpPath">yt-dlp.exe for "Test with yt-dlp"; <c>null</c> disables the test.</param>
        /// <param name="reason">Why the window opened by itself (YouTube asked for a sign-in); <c>null</c> when opened by hand.</param>
        public YouTubeSignInDialog(string? ytDlpPath, string? cookiesFile = null, string? profileFolder = null, string? reason = null)
        {
            _ytDlpPath = ytDlpPath;
            _reason = reason;
            _cookiesFile = cookiesFile ?? YouTubeSignIn.CookiesFile;
            _profileFolder = profileFolder ?? YouTubeSignIn.ProfileFolder;

            Text = "Sign in to YouTube (prototype)";
            if (AppIcon.Instance is { } icon)
                Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;

            _web = new WebView2 { Dock = DockStyle.Fill };

            _status = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 0, 4),
                Text = "Starting the sign-in window..."
            };
            _testButton = new Button { Text = "Test with yt-dlp", AutoSize = true, Enabled = false };
            _testButton.Click += async (_, _) => await TestWithYtDlpAsync();
            _signOutButton = new Button { Text = "Sign out", AutoSize = true, Enabled = false };
            _signOutButton.Click += (_, _) => SignOut();
            // Not a DialogResult: that only closes a modal dialog, and this window is not one (the player keeps running).
            _closeButton = new Button { Text = "Close", AutoSize = true };
            _closeButton.Click += (_, _) => Close();
            CancelButton = _closeButton;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { _closeButton, _signOutButton, _testButton });

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 6, 10, 8) };
            bottom.Controls.Add(_status);
            bottom.Controls.Add(buttons);

            Controls.Add(_web);
            Controls.Add(bottom);

            Load += async (_, _) =>
            {
                ClientSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(780));
                _status.MaximumSize = new Size(ClientSize.Width - LogicalToDeviceUnits(20), 0);
                await StartAsync();
            };
        }

        private async Task StartAsync()
        {
            try
            {
                Directory.CreateDirectory(_profileFolder);
                var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _profileFolder);
                await _web.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                Logger.Log("YouTube sign-in: the WebView2 window could not be started.", ex);
                SetStatus($"The sign-in window could not be started: {ex.Message} (Is the Microsoft Edge WebView2 Runtime installed?)");
                return;
            }

            if (IsDisposed)
                return;

            _web.CoreWebView2.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(e);

            var cookies = await GetCookiesAsync();
            if (YouTubeSignIn.IsSignedIn(cookies))
            {
                // Signed in earlier: save the cookies again without opening YouTube (its scripts would rotate them).
                Save(cookies, "Signed in (from earlier).");
                return;
            }

            SetStatus((_reason != null ? _reason + " " : string.Empty) +
                      "Sign in with the Google account yt-dlp should use – a spare account is best. If Google says " +
                      "\"This browser or app may not be secure\", it does not allow signing in in this window.");
            _web.CoreWebView2.Navigate(YouTubeSignIn.SignInUrl);
        }

        private async Task OnNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs e)
        {
            if (IsDisposed || _web.Source is not { } page || page.Scheme is not ("https" or "http"))
                return;

            // Host and path only: the query of Google's pages is of no use in the log.
            Logger.Log($"YouTube sign-in: {(e.IsSuccess ? "showing" : $"failed to load ({e.WebErrorStatus})")} {page.Host}{page.AbsolutePath}");

            if (page.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)
                && page.AbsolutePath.Contains("rejected", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Google refuses the sign-in in this window (\"This browser or app may not be secure\"). " +
                          "Use the cookies of Firefox instead, as the README describes (--cookies-from-browser firefox).");
                return;
            }

            if (!page.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || _signedIn)
                return;

            var cookies = await GetCookiesAsync();
            // Another page event may have saved meanwhile (seen: two events at the end of a sign-in both saved).
            if (_signedIn || IsDisposed || !YouTubeSignIn.IsSignedIn(cookies))
                return;

            if (!string.Equals(page.AbsoluteUri, YouTubeSignIn.QuietYouTubePage, StringComparison.OrdinalIgnoreCase))
            {
                // Back on YouTube after the sign-in: leave YouTube's own page before its scripts rotate the cookies.
                _web.CoreWebView2.Navigate(YouTubeSignIn.QuietYouTubePage);
                return;
            }

            Save(cookies, "Signed in.");
        }

        private void Save(IReadOnlyList<SignInCookie> cookies, string what)
        {
            try
            {
                YouTubeSignIn.SaveCookies(cookies, _cookiesFile);
            }
            catch (Exception ex)
            {
                Logger.Log($"YouTube sign-in: the cookies could not be saved to {_cookiesFile}.", ex);
                SetStatus($"Signed in, but the sign-in could not be saved: {ex.Message}");
                return;
            }

            _signedIn = true;
            Logger.Log($"YouTube sign-in: saved {cookies.Count} cookies of youtube.com for yt-dlp to {_cookiesFile}.");
            SetStatus($"{what} The sign-in is saved for yt-dlp ({_cookiesFile}), which uses it from its next lookup on. " +
                      "You can close this window.");
            UpdateButtons();
            _web.CoreWebView2.NavigateToString(DonePage);
        }

        private async Task<IReadOnlyList<SignInCookie>> GetCookiesAsync()
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(YouTubeSignIn.CookieUrl);
            return cookies.Select(c => new SignInCookie(
                    c.Domain,
                    c.Path,
                    c.Name,
                    c.Value,
                    c.IsSecure,
                    c.IsHttpOnly,
                    c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime(), TimeSpan.Zero)))
                .ToList();
        }

        private void SignOut()
        {
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
            try
            {
                File.Delete(_cookiesFile);
            }
            catch (Exception ex)
            {
                Logger.Log($"YouTube sign-in: {_cookiesFile} could not be deleted.", ex);
            }

            _signedIn = false;
            Logger.Log("YouTube sign-in: signed out; the saved sign-in is deleted.");
            UpdateButtons();
            SetStatus("Signed out. Sign in again, or close the window to let yt-dlp run without a sign-in.");
            _web.CoreWebView2.Navigate(YouTubeSignIn.SignInUrl);
        }

        private async Task TestWithYtDlpAsync()
        {
            if (_ytDlpPath == null || _busy)
                return;

            _busy = true;
            UpdateButtons();
            SetStatus("Looking up a video with yt-dlp and the sign-in...");
            var watch = Stopwatch.StartNew();
            string result;
            try
            {
                using var service = new YtDlpService(new YtDlpProcess(_ytDlpPath), cookiesFile: _cookiesFile);
                var video = await Task.Run(() => service.ResolveAsync(TestVideoId, CancellationToken.None));
                result = $"yt-dlp found \"{video.Title}\" with the sign-in in {watch.Elapsed.TotalSeconds:0.0} s " +
                         "(problems with the cookies would be in errorlog.txt as a yt-dlp warning).";
            }
            catch (Exception ex)
            {
                result = $"yt-dlp failed with the sign-in: {ex.Message}";
            }

            Logger.Log($"YouTube sign-in test: {result}");
            _busy = false;
            if (IsDisposed)
                return;

            UpdateButtons();
            SetStatus(result);
        }

        private void UpdateButtons()
        {
            _signOutButton.Enabled = _signedIn && !_busy;
            _testButton.Enabled = _signedIn && !_busy && _ytDlpPath != null;
        }

        private void SetStatus(string text)
        {
            if (!IsDisposed)
                _status.Text = text;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _web.Dispose();
            base.Dispose(disposing);
        }

        private const string DonePage = """
            <!doctype html><html><head><meta charset="utf-8"><title>Signed in</title>
            <style>body{font-family:Segoe UI,sans-serif;margin:48px 32px;color:#222}h1{font-size:22px;color:#D85A30}</style></head>
            <body><h1>Signed in</h1><p>The sign-in is saved for yt-dlp. You can close this window.</p>
            <p>YouTube is not left open here on purpose: its page would replace the saved cookies.</p></body></html>
            """;
    }
}
