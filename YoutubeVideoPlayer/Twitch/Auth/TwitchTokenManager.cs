using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch.Auth
{
    /// <summary>
    /// Keeps the bot's access token valid: signs in with the device code flow, refreshes the token
    /// shortly before it expires and validates it every hour (required by Twitch).
    /// </summary>
    /// <remarks>
    /// Must be created and used on the UI thread; timer ticks are marshalled onto it.
    /// Refresh tokens are single use, so a new token is always saved before it is used.
    /// </remarks>
    public sealed class TwitchTokenManager : ITokenLifetime, IDisposable
    {
        private static readonly TimeSpan MinimumRefreshDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

        private readonly ITwitchAuthClient _auth;
        private readonly TwitchTokenStore _store;
        private readonly TimeSpan _refreshMargin;
        private readonly TimeProvider _time;
        private readonly ContextTimer _refreshTimer;
        private readonly ContextTimer _validateTimer;
        private Task<bool>? _refreshTask;
        private bool _currentNotSaved;
        private bool _disposed;

        /// <param name="refreshMargin">How long before expiry the token is refreshed (default 10 minutes).</param>
        /// <param name="validateInterval">How often the token is validated (default 1 hour).</param>
        /// <param name="timeProvider">The clock to use; <see cref="TimeProvider.System"/> by default.</param>
        public TwitchTokenManager(
            ITwitchAuthClient auth,
            TwitchTokenStore store,
            TimeSpan? refreshMargin = null,
            TimeSpan? validateInterval = null,
            TimeProvider? timeProvider = null)
        {
            var context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("TwitchTokenManager must be created on the UI thread.");

            _auth = auth;
            _store = store;
            _refreshMargin = refreshMargin ?? TimeSpan.FromMinutes(10);
            _time = timeProvider ?? TimeProvider.System;

            // The refresh timer is always started with an explicit due time (see ScheduleTimers).
            _refreshTimer = new ContextTimer(context, TimeSpan.FromHours(1), periodic: false,
                () => RefreshAsync().FireAndForget("Refreshing the Twitch token failed."), _time);
            _validateTimer = new ContextTimer(context, validateInterval ?? TimeSpan.FromHours(1), periodic: true,
                () => ValidateAsync().FireAndForget("Validating the Twitch token failed."), _time);
        }

        private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

        /// <summary>The current token, or <c>null</c> if the bot is not signed in.</summary>
        public TwitchToken? Current { get; private set; }

        /// <summary>How long the current token stays valid (negative once it has expired); <c>null</c> if not signed in.</summary>
        public TimeSpan? RemainingValidity => Current == null ? null : Current.ExpiresAtUtc - UtcNow;

        /// <summary>A new token is available (after sign-in or refresh); reconnect with it.</summary>
        public event EventHandler<TwitchToken>? TokenChanged;

        /// <summary>Twitch rejected the stored token; the user has to sign in again.</summary>
        public event EventHandler? SignInRequired;

        /// <summary>
        /// Loads the stored token and makes sure it is valid, refreshing it if necessary.
        /// Returns <c>null</c> if a sign-in is needed.
        /// </summary>
        public async Task<TwitchToken?> InitializeAsync()
        {
            Current = _store.Load();
            if (Current == null)
            {
                Logger.Log("No Twitch sign-in stored; the bot needs to be connected once.");
                return null;
            }

            // EnsureValidAsync schedules the timers itself (a failed refresh schedules a retry instead).
            return await EnsureValidAsync() ? Current : null;
        }

        /// <summary>
        /// Refreshes the token now. Concurrent calls share one refresh. Returns <c>false</c> if no
        /// valid token is available afterwards.
        /// </summary>
        public async Task<bool> RefreshAsync()
        {
            if (_refreshTask != null)
                return await _refreshTask;

            var task = RefreshCoreAsync();
            _refreshTask = task;
            try
            {
                return await task;
            }
            finally
            {
                if (ReferenceEquals(_refreshTask, task))
                    _refreshTask = null;
            }
        }

        /// <summary>
        /// Signs the bot in with the device code flow. <paramref name="showCode"/> is called with the code
        /// the user has to confirm on twitch.tv; the method returns once the user confirmed.
        /// </summary>
        public async Task<TwitchToken> SignInWithDeviceCodeAsync(Action<DeviceCodeInfo> showCode, CancellationToken cancellationToken)
        {
            var device = await _auth.StartDeviceFlowAsync(cancellationToken);
            showCode(device);

            var deadline = UtcNow + device.ExpiresIn;
            var interval = device.Interval;
            TokenResponse? response = null;

            while (response == null)
            {
                await Task.Delay(interval, _time, cancellationToken);
                if (UtcNow > deadline)
                    throw new TimeoutException("The code has expired. Please request a new one.");

                var result = await _auth.PollDeviceTokenAsync(device.DeviceCode, cancellationToken);
                switch (result.Status)
                {
                    case DevicePollStatus.Pending:
                        break;
                    case DevicePollStatus.SlowDown:
                        interval += TimeSpan.FromSeconds(5);
                        break;
                    case DevicePollStatus.Expired:
                        throw new TimeoutException("The code has expired. Please request a new one.");
                    case DevicePollStatus.Denied:
                        throw new TwitchAuthRejectedException("The authorization was declined on twitch.tv.");
                    case DevicePollStatus.Success:
                        response = result.Token;
                        break;
                }
            }

            var validation = await _auth.ValidateAsync(response.AccessToken, cancellationToken)
                ?? throw new InvalidOperationException("Twitch returned a token that is not valid.");

            var token = new TwitchToken
            {
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken,
                ExpiresAtUtc = UtcNow + response.ExpiresIn,
                Login = validation.Login,
                UserId = validation.UserId,
                Scopes = response.Scopes
            };

            Logger.Log($"Twitch sign-in completed as '{token.Login}'.");
            Adopt(token, save: true);
            return token;
        }

        private async Task<bool> EnsureValidAsync()
        {
            var token = Current!;
            if (token.ExpiresAtUtc - UtcNow > _refreshMargin)
            {
                try
                {
                    var validation = await _auth.ValidateAsync(token.AccessToken, CancellationToken.None);
                    if (validation != null)
                    {
                        if (validation.Login != token.Login || validation.UserId != token.UserId)
                        {
                            Current = token with { Login = validation.Login, UserId = validation.UserId };
                            _store.Save(Current);
                        }

                        ScheduleTimers();
                        return true;
                    }

                    Logger.Log("The stored Twitch token is no longer valid; refreshing it.");
                }
                catch (Exception ex)
                {
                    // Offline or Twitch unreachable: keep using the token, it has not expired yet.
                    Logger.Log("Could not validate the Twitch token; using it anyway.", ex);
                    ScheduleTimers();
                    return true;
                }
            }

            return await RefreshAsync();
        }

        private async Task<bool> RefreshCoreAsync()
        {
            // Return to the caller first, so RefreshAsync registers this task before any work is done
            // and concurrent callers always share it.
            await Task.Yield();

            // The app folder may be shared (e.g. a synced folder) with another instance that already
            // refreshed the token. Its refresh token would make ours invalid, so prefer the newer one.
            // If the last token could not be written, the file holds an outdated refresh token that Twitch
            // has already invalidated; the one in memory is the only usable one.
            var stored = _currentNotSaved ? null : _store.Load();
            if (stored != null
                && stored.RefreshToken != Current?.RefreshToken
                && stored.ExpiresAtUtc - UtcNow > _refreshMargin)
            {
                Logger.Log("Using the Twitch token that was refreshed by another instance.");
                Adopt(stored, save: false);
                return true;
            }

            var basis = stored ?? Current;
            if (basis == null)
            {
                RequireSignIn();
                return false;
            }

            try
            {
                var response = await _auth.RefreshAsync(basis.RefreshToken, CancellationToken.None);
                var token = basis with
                {
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ExpiresAtUtc = UtcNow + response.ExpiresIn,
                    Scopes = response.Scopes.Count > 0 ? response.Scopes : basis.Scopes
                };

                Logger.Log($"Twitch token refreshed; valid until {token.ExpiresAtUtc.ToLocalTime():g}.");
                Adopt(token, save: true);
                return true;
            }
            catch (TwitchAuthRejectedException ex)
            {
                Logger.Log("Twitch rejected the refresh token; a new sign-in is needed.", ex);
                RequireSignIn();
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Refreshing the Twitch token failed; retrying in {RetryDelay.TotalMinutes:0} minute(s).", ex);
                if (!_disposed)
                    _refreshTimer.Start(RetryDelay);

                return Current != null && Current.ExpiresAtUtc > UtcNow;
            }
        }

        private async Task ValidateAsync()
        {
            var token = Current;
            if (token == null)
                return;

            TokenValidation? validation;
            try
            {
                validation = await _auth.ValidateAsync(token.AccessToken, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Log("Hourly Twitch token validation could not reach Twitch.", ex);
                return;
            }

            if (validation == null && ReferenceEquals(token, Current))
            {
                Logger.Log("Hourly validation: the Twitch token is no longer valid; refreshing it.");
                await RefreshAsync();
            }
        }

        /// <summary>Makes <paramref name="token"/> the current token (saving it first) and announces it.</summary>
        private void Adopt(TwitchToken token, bool save)
        {
            if (save)
            {
                // Save before use: the previous refresh token is now invalid. If writing fails (locked or
                // read-only file), carry on with the token in memory. Giving up here would retry the refresh
                // with the old refresh token, which Twitch rejects, and the bot would need a new sign-in.
                try
                {
                    _store.Save(token);
                    _currentNotSaved = false;
                }
                catch (Exception ex)
                {
                    _currentNotSaved = true;
                    Logger.Log($"Saving {TwitchTokenStore.FileName} failed; using the new Twitch token from memory only. It will be lost when the app closes.", ex);
                }
            }
            else
            {
                _currentNotSaved = false; // adopted from the file
            }

            Current = token;
            ScheduleTimers();
            TokenChanged?.Invoke(this, token);
        }

        private void RequireSignIn()
        {
            Current = null;
            _refreshTimer.Stop();
            _validateTimer.Stop();
            SignInRequired?.Invoke(this, EventArgs.Empty);
        }

        private void ScheduleTimers()
        {
            if (_disposed || Current == null)
                return;

            var dueIn = Current.ExpiresAtUtc - _refreshMargin - UtcNow;
            _refreshTimer.Start(dueIn < MinimumRefreshDelay ? MinimumRefreshDelay : dueIn);

            if (!_validateTimer.IsRunning)
                _validateTimer.Start();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _refreshTimer.Dispose();
            _validateTimer.Dispose();
            _auth.Dispose();
        }
    }
}
