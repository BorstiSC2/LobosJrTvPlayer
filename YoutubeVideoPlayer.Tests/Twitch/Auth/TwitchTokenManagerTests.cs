using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Auth;

namespace YouTubeVideoPlayer.Tests.Twitch.Auth;

public class TwitchTokenManagerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);

    private sealed class Setup : IDisposable
    {
        private readonly TempDirectory _dir = new();

        public Setup()
        {
            Store = new TwitchTokenStore(_dir.Path);
            Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TokenValidation("lobosbot", "4711", TimeSpan.FromHours(3), new[] { "chat:read" }));
            Manager = new TwitchTokenManager(Auth, Store, timeProvider: Time);
            Manager.TokenChanged += (_, t) => Changes.Add((t, Store.Load()));
            Manager.SignInRequired += (_, _) => SignInRequests++;
        }

        public FakeTimeProvider Time { get; } = new(Start);

        public ITwitchAuthClient Auth { get; } = Substitute.For<ITwitchAuthClient>();

        public TwitchTokenStore Store { get; }

        public TwitchTokenManager Manager { get; }

        /// <summary>Each new token together with what was stored on disk when it was announced.</summary>
        public List<(TwitchToken Token, TwitchToken? Stored)> Changes { get; } = new();

        public int SignInRequests { get; private set; }

        public string StorePath => Path.Combine(_dir.Path, TwitchTokenStore.FileName);

        public DateTime Now => Time.GetUtcNow().UtcDateTime;

        public TwitchToken Token(string name, TimeSpan validFor) => new()
        {
            AccessToken = "acc-" + name,
            RefreshToken = "ref-" + name,
            ExpiresAtUtc = Now + validFor,
            Login = "lobosbot",
            UserId = "4711",
            Scopes = new[] { "chat:read", "chat:edit" }
        };

        public Setup Stored(string name, TimeSpan validFor)
        {
            Store.Save(Token(name, validFor));
            return this;
        }

        public void RefreshReturns(string name) =>
            Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TokenResponse("acc-" + name, "ref-" + name, TimeSpan.FromHours(4), new[] { "chat:read", "chat:edit" }));

        public int Refreshes => Auth.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITwitchAuthClient.RefreshAsync));

        public int Validations => Auth.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITwitchAuthClient.ValidateAsync));

        public async Task AdvanceAsync(TimeSpan by)
        {
            await Async.SettleAsync();
            Time.Advance(by);
            await Async.SettleAsync();
        }

        /// <summary>Moves the clock in one-second steps until the task completes (device code polling).</summary>
        public async Task<T> DriveAsync<T>(Task<T> task, int maxSeconds = 300)
        {
            for (var i = 0; i < maxSeconds && !task.IsCompleted; i++)
                await AdvanceAsync(TimeSpan.FromSeconds(1));

            return await task;
        }

        public void Dispose()
        {
            Manager.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public void Constructor_RequiresASynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var dir = new TempDirectory();
            Assert.Throws<InvalidOperationException>(() =>
                new TwitchTokenManager(Substitute.For<ITwitchAuthClient>(), new TwitchTokenStore(dir.Path)));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    #region Startup

    [Fact]
    public void Initialize_WithoutStoredSignIn_ReturnsNull() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        Assert.Null(await setup.Manager.InitializeAsync());
        Assert.Null(setup.Manager.Current);
        Assert.Empty(setup.Auth.ReceivedCalls());
    });

    [Fact]
    public void Initialize_WithAValidToken_OnlyValidatesIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(3));

        var token = await setup.Manager.InitializeAsync();

        Assert.Equal("acc-1", token!.AccessToken);
        await setup.Auth.Received(1).ValidateAsync("acc-1", Arg.Any<CancellationToken>());
        Assert.Equal(0, setup.Refreshes);
        Assert.Empty(setup.Changes);
    });

    [Fact]
    public void Initialize_TakesOverARenamedAccountFromValidation() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(3));
        setup.Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TokenValidation("newname", "4711", TimeSpan.FromHours(3), Array.Empty<string>()));

        var token = await setup.Manager.InitializeAsync();

        Assert.Equal("newname", token!.Login);
        Assert.Equal("newname", setup.Store.Load()!.Login);
    });

    [Fact]
    public void Initialize_RefreshesATokenThatExpiresSoon_AndSavesTheNewOneBeforeAnnouncingIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromMinutes(5));
        setup.RefreshReturns("2");

        var token = await setup.Manager.InitializeAsync();

        Assert.Equal("acc-2", token!.AccessToken);
        Assert.Equal("ref-2", token.RefreshToken);
        Assert.Equal(setup.Now + TimeSpan.FromHours(4), token.ExpiresAtUtc);
        Assert.Equal("lobosbot", token.Login); // account data is kept
        await setup.Auth.Received(1).RefreshAsync("ref-1", Arg.Any<CancellationToken>());
        await setup.Auth.DidNotReceive().ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var (announced, storedAtThatMoment) = Assert.Single(setup.Changes);
        Assert.Equal(token, announced);
        Assert.Equal("ref-2", storedAtThatMoment!.RefreshToken);
    });

    [Fact]
    public void Initialize_RefreshesATokenTwitchNoLongerAccepts() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(3));
        setup.Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((TokenValidation?)null);
        setup.RefreshReturns("2");

        var token = await setup.Manager.InitializeAsync();

        Assert.Equal("acc-2", token!.AccessToken);
    });

    [Fact]
    public void Initialize_WhenTwitchIsUnreachable_KeepsUsingTheUnexpiredToken() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(3));
        setup.Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var token = await setup.Manager.InitializeAsync();

        Assert.Equal("acc-1", token!.AccessToken);
        Assert.Equal(0, setup.Refreshes);
    });

    [Fact]
    public void Initialize_WhenTheRefreshTokenIsRejected_RequiresASignIn() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromMinutes(1));
        setup.Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TwitchAuthRejectedException("revoked"));

        var token = await setup.Manager.InitializeAsync();

        Assert.Null(token);
        Assert.Null(setup.Manager.Current);
        Assert.Equal(1, setup.SignInRequests);

        await setup.AdvanceAsync(TimeSpan.FromHours(5));
        Assert.Equal(1, setup.Refreshes); // no more timers after giving up
    });

    [Fact]
    public void Refresh_ThatFailsTransiently_IsRetriedAfterAMinute() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromMinutes(5));
        setup.Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var token = await setup.Manager.InitializeAsync();
        Assert.Equal("acc-1", token!.AccessToken); // still valid for a few minutes

        setup.RefreshReturns("2");
        await setup.AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.Equal(1, setup.Refreshes);
        await setup.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, setup.Refreshes);
        Assert.Equal("acc-2", setup.Manager.Current!.AccessToken);
    });

    [Fact]
    public void Refresh_ThatFailsTransientlyForAnExpiredToken_ReportsNoValidToken() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromMinutes(-1));
        setup.Auth.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        Assert.Null(await setup.Manager.InitializeAsync());
        Assert.Equal(0, setup.SignInRequests); // not rejected, only offline
    });

    #endregion

    #region Refreshing

    [Fact]
    public void ConcurrentRefreshes_ShareOneRequest() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(3));
        await setup.Manager.InitializeAsync();
        setup.RefreshReturns("2");

        var results = await Task.WhenAll(setup.Manager.RefreshAsync(), setup.Manager.RefreshAsync(), setup.Manager.RefreshAsync());

        Assert.All(results, Assert.True);
        Assert.Equal(1, setup.Refreshes);
    });

    [Fact]
    public void Refresh_AdoptsATokenAnotherInstanceAlreadyRenewed() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("mine", TimeSpan.FromHours(3));
        await setup.Manager.InitializeAsync();
        setup.Stored("other", TimeSpan.FromHours(4)); // written by another computer sharing the folder

        var ok = await setup.Manager.RefreshAsync();

        Assert.True(ok);
        Assert.Equal("acc-other", setup.Manager.Current!.AccessToken);
        Assert.Equal(0, setup.Refreshes);
        Assert.Equal("acc-other", Assert.Single(setup.Changes).Token.AccessToken);
    });

    [Fact]
    public void Refresh_UsesTheStoredRefreshTokenWhenTheOtherInstancesTokenIsAboutToExpire() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("mine", TimeSpan.FromHours(3));
        await setup.Manager.InitializeAsync();
        setup.Stored("other", TimeSpan.FromMinutes(2));
        setup.RefreshReturns("new");

        await setup.Manager.RefreshAsync();

        await setup.Auth.Received(1).RefreshAsync("ref-other", Arg.Any<CancellationToken>());
        Assert.Equal("acc-new", setup.Manager.Current!.AccessToken);
    });

    [Fact]
    public void Refresh_WhenTheNewTokenCannotBeSaved_KeepsUsingItFromMemory() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromMinutes(5));
        File.SetAttributes(setup.StorePath, FileAttributes.ReadOnly); // the file can be read but not replaced
        try
        {
            setup.RefreshReturns("2");

            var token = await setup.Manager.InitializeAsync();

            // Twitch already invalidated ref-1, so the new token must not be dropped just because it is not on disk.
            Assert.Equal("acc-2", token!.AccessToken);
            Assert.Equal("acc-2", setup.Manager.Current!.AccessToken);
            Assert.Equal(0, setup.SignInRequests);
            Assert.Equal("ref-1", setup.Store.Load()!.RefreshToken);

            // The next refresh must use the refresh token from memory, not the outdated one in the file.
            setup.RefreshReturns("3");
            Assert.True(await setup.Manager.RefreshAsync());

            await setup.Auth.Received(1).RefreshAsync("ref-2", Arg.Any<CancellationToken>());
            await setup.Auth.Received(1).RefreshAsync("ref-1", Arg.Any<CancellationToken>());
            Assert.Equal("acc-3", setup.Manager.Current!.AccessToken);
            Assert.Equal(0, setup.SignInRequests);
        }
        finally
        {
            File.SetAttributes(setup.StorePath, FileAttributes.Normal);
        }
    });

    [Fact]
    public void Refresh_WithoutAnyToken_RequiresASignIn() => UiContext.Run(async () =>
    {
        using var setup = new Setup();

        Assert.False(await setup.Manager.RefreshAsync());
        Assert.Equal(1, setup.SignInRequests);
    });

    [Fact]
    public void TheTokenIsRefreshedAutomatically_TenMinutesBeforeItExpires() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(1));
        await setup.Manager.InitializeAsync();
        setup.RefreshReturns("2");

        await setup.AdvanceAsync(TimeSpan.FromMinutes(50) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, setup.Refreshes);
        await setup.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, setup.Refreshes);
        Assert.Equal("acc-2", setup.Manager.Current!.AccessToken);

        // The next refresh is scheduled for the new token: 4 h validity minus the margin.
        setup.RefreshReturns("3");
        await setup.AdvanceAsync(TimeSpan.FromHours(4) - Margin);
        Assert.Equal("acc-3", setup.Manager.Current!.AccessToken);
    });

    [Fact]
    public void TheRefreshIsNeverScheduledSoonerThan30Seconds() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", Margin + TimeSpan.FromSeconds(5));
        await setup.Manager.InitializeAsync();
        setup.RefreshReturns("2");

        await setup.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Equal(0, setup.Refreshes);
        await setup.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, setup.Refreshes);
    });

    #endregion

    #region Hourly validation

    [Fact]
    public void TheTokenIsValidatedEveryHour() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(5));
        await setup.Manager.InitializeAsync();

        await setup.AdvanceAsync(TimeSpan.FromHours(1));
        await setup.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(3, setup.Validations); // startup + 2 hourly checks
        Assert.Equal(0, setup.Refreshes);
    });

    [Fact]
    public void AnHourlyValidationThatFails_TriggersARefresh() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(5));
        await setup.Manager.InitializeAsync();
        setup.Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((TokenValidation?)null);
        setup.RefreshReturns("2");

        await setup.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal("acc-2", setup.Manager.Current!.AccessToken);
    });

    [Fact]
    public void AnHourlyValidationWithoutNetwork_ChangesNothing() => UiContext.Run(async () =>
    {
        using var setup = new Setup().Stored("1", TimeSpan.FromHours(5));
        await setup.Manager.InitializeAsync();
        setup.Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        await setup.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal("acc-1", setup.Manager.Current!.AccessToken);
        Assert.Equal(0, setup.Refreshes);
    });

    #endregion

    #region Device code sign-in

    private static DeviceCodeInfo Device(int expiresInSeconds = 120, int intervalSeconds = 1) =>
        new("dev", "ABCD-1234", "https://www.twitch.tv/activate?device-code=ABCD-1234",
            TimeSpan.FromSeconds(expiresInSeconds), TimeSpan.FromSeconds(intervalSeconds));

    private static readonly TokenResponse NewTokens = new("acc-dev", "ref-dev", TimeSpan.FromHours(4), new[] { "chat:read", "chat:edit" });

    [Fact]
    public void SignIn_ShowsTheCode_PollsUntilConfirmed_AndStoresTheToken() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device());
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(
            new DevicePollResult(DevicePollStatus.Pending),
            new DevicePollResult(DevicePollStatus.Pending),
            new DevicePollResult(DevicePollStatus.Success, NewTokens));
        DeviceCodeInfo? shown = null;

        var token = await setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(d => shown = d, CancellationToken.None));

        Assert.Equal("ABCD-1234", shown!.UserCode);
        Assert.Equal(("acc-dev", "ref-dev", "lobosbot", "4711"), (token.AccessToken, token.RefreshToken, token.Login, token.UserId));
        Assert.Equal(token, setup.Manager.Current);
        Assert.Equal("ref-dev", setup.Store.Load()!.RefreshToken);
        Assert.Equal(token, Assert.Single(setup.Changes).Token);
        await setup.Auth.Received(3).PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>());
    });

    [Fact]
    public void SignIn_SlowsDownWhenTwitchAsksForIt() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device(intervalSeconds: 1));
        var pollTimes = new List<DateTimeOffset>();
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(
            _ => { pollTimes.Add(setup.Time.GetUtcNow()); return new DevicePollResult(DevicePollStatus.SlowDown); },
            _ => { pollTimes.Add(setup.Time.GetUtcNow()); return new DevicePollResult(DevicePollStatus.Pending); },
            _ => { pollTimes.Add(setup.Time.GetUtcNow()); return new DevicePollResult(DevicePollStatus.Success, NewTokens); });

        await setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(_ => { }, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(1), pollTimes[0] - Start);
        Assert.Equal(TimeSpan.FromSeconds(6), pollTimes[1] - pollTimes[0]); // interval grew by 5 seconds
        Assert.Equal(TimeSpan.FromSeconds(6), pollTimes[2] - pollTimes[1]);
    });

    [Theory]
    [InlineData(DevicePollStatus.Expired, typeof(TimeoutException))]
    [InlineData(DevicePollStatus.Denied, typeof(TwitchAuthRejectedException))]
    public void SignIn_ReportsExpiredOrDeclinedCodes(DevicePollStatus status, Type expected) => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device());
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(status));

        var ex = await Record.ExceptionAsync(() => setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(_ => { }, CancellationToken.None)));

        Assert.IsType(expected, ex);
        Assert.Null(setup.Manager.Current);
        Assert.Null(setup.Store.Load());
    });

    [Fact]
    public void SignIn_GivesUpWhenTheCodeRunsOut() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device(expiresInSeconds: 10, intervalSeconds: 4));
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Pending));

        await Assert.ThrowsAsync<TimeoutException>(() => setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(_ => { }, CancellationToken.None)));

        await setup.Auth.Received(2).PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()); // at 4 s and 8 s
    });

    [Fact]
    public void SignIn_CanBeCancelled() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device());
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Pending));
        using var cancellation = new CancellationTokenSource();

        var signIn = setup.Manager.SignInWithDeviceCodeAsync(_ => { }, cancellation.Token);
        await setup.AdvanceAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
    });

    [Fact]
    public void SignIn_WithATokenTwitchDoesNotAccept_Fails() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device());
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Success, NewTokens));
        setup.Auth.ValidateAsync("acc-dev", Arg.Any<CancellationToken>()).Returns((TokenValidation?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(_ => { }, CancellationToken.None)));
    });

    [Fact]
    public void SignIn_SchedulesTheAutomaticRefresh() => UiContext.Run(async () =>
    {
        using var setup = new Setup();
        setup.Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(Device());
        setup.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Success, NewTokens));
        await setup.DriveAsync(setup.Manager.SignInWithDeviceCodeAsync(_ => { }, CancellationToken.None));
        setup.RefreshReturns("2");

        await setup.AdvanceAsync(TimeSpan.FromHours(4) - Margin);

        Assert.Equal("acc-2", setup.Manager.Current!.AccessToken);
    });

    #endregion

    [Fact]
    public void Dispose_StopsTheTimersAndDisposesTheAuthClient() => UiContext.Run(async () =>
    {
        var setup = new Setup().Stored("1", TimeSpan.FromHours(1));
        await setup.Manager.InitializeAsync();

        setup.Dispose();
        setup.Manager.Dispose(); // idempotent
        await setup.AdvanceAsync(TimeSpan.FromHours(2));

        setup.Auth.Received(1).Dispose();
        Assert.Equal(0, setup.Refreshes);
    });
}
