using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class TokenCommandTests
{
    private static ITokenLifetime Lifetime(TimeSpan? remaining)
    {
        var tokens = Substitute.For<ITokenLifetime>();
        tokens.RemainingValidity.Returns(remaining);
        return tokens;
    }

    [Fact]
    public void Metadata_IsForModeratorsBroadcasterAndPrivilegedUsersOnly()
    {
        IChatCommand command = new TokenCommand(Lifetime(TimeSpan.FromHours(1)), TimeSpan.FromSeconds(5));

        Assert.Equal("token", command.Name);
        Assert.True(command.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), command.Cooldown);
    }

    [Theory]
    [InlineData(3 * 3600 + 42 * 60 + 30, "The bot token is valid for another 3 h 42 min. It is renewed automatically shortly before it expires.")]
    [InlineData(4 * 3600, "The bot token is valid for another 4 h 0 min. It is renewed automatically shortly before it expires.")]
    [InlineData(17 * 60 + 59, "The bot token is valid for another 17 min. It is renewed automatically shortly before it expires.")]
    [InlineData(40, "The bot token is valid for less than another minute. It is renewed automatically shortly before it expires.")]
    public async Task ReportsTheRemainingDuration(int seconds, string expected)
    {
        var command = new TokenCommand(Lifetime(TimeSpan.FromSeconds(seconds)), TimeSpan.Zero);

        Assert.Equal(expected, await command.ExecuteAsync(Chat.Command("!token", moderator: true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-120)]
    public async Task AnExpiredToken_SaysSo(int seconds)
    {
        var command = new TokenCommand(Lifetime(TimeSpan.FromSeconds(seconds)), TimeSpan.Zero);

        Assert.Equal("The bot token has expired and is being renewed.", await command.ExecuteAsync(Chat.Command("!token")));
    }

    [Fact]
    public async Task WithoutSignIn_SaysSo()
    {
        var command = new TokenCommand(Lifetime(null), TimeSpan.Zero);

        Assert.Equal("The bot is not signed in.", await command.ExecuteAsync(Chat.Command("!token")));
    }

    [Fact]
    public async Task WithAManualToken_SaysTheDurationIsUnknown()
    {
        var command = new TokenCommand(null, TimeSpan.Zero);

        Assert.Contains("manual token", await command.ExecuteAsync(Chat.Command("!token")));
    }

    [Fact]
    public void TheTokenManager_ReportsTheRemainingValidityOfItsCurrentToken() => UiContext.Run(async () =>
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        var store = new TwitchTokenStore(dir.Path);
        store.Save(new TwitchToken
        {
            AccessToken = "secret-access",
            RefreshToken = "secret-refresh",
            ExpiresAtUtc = time.GetUtcNow().UtcDateTime.AddHours(3),
            Login = "lobosbot",
            UserId = "4711"
        });
        var auth = Substitute.For<ITwitchAuthClient>();
        auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TokenValidation("lobosbot", "4711", TimeSpan.FromHours(3), Array.Empty<string>()));
        using var manager = new TwitchTokenManager(auth, store, timeProvider: time);
        Assert.Null(manager.RemainingValidity);

        await manager.InitializeAsync();
        time.Advance(TimeSpan.FromMinutes(30));
        var reply = await new TokenCommand(manager, TimeSpan.Zero).ExecuteAsync(Chat.Command("!token"));

        Assert.Equal(TimeSpan.FromMinutes(150), manager.RemainingValidity);
        Assert.Equal("The bot token is valid for another 2 h 30 min. It is renewed automatically shortly before it expires.", reply);
        Assert.DoesNotContain("secret", reply);
    });

    [Fact]
    public void OnlyModeratorsBroadcasterAndPrivilegedUsersCanUseIt_ThroughTheCommandManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        using var manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new TokenCommand(Lifetime(TimeSpan.FromHours(2)), TimeSpan.Zero) },
            new[] { "friend" },
            new FakeTimeProvider());

        chat.Say("!token", user: "viewer");
        await Async.SettleAsync();
        Assert.Empty(sent);

        chat.Say("!token", user: "viewer2", moderator: true);
        chat.Say("!token", user: "friend");
        await Async.SettleAsync();

        Assert.Equal(2, sent.Count);
    });
}
