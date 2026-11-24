using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class ChatCommandManagerTests
{
    private static IChatCommand Command(string name, bool moderatorOnly = false, TimeSpan? cooldown = null, string? reply = "ok",
        string? group = null)
    {
        var command = Substitute.For<IChatCommand>();
        command.Name.Returns(name);
        command.RequiresModerator.Returns(moderatorOnly);
        command.Cooldown.Returns(cooldown ?? TimeSpan.Zero);
        command.CooldownGroup.Returns(group);
        command.ExecuteAsync(Arg.Any<ChatCommandContext>()).Returns(reply);
        return command;
    }

    [Fact]
    public void RoutesCommandsByName_AndPostsTheReply() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var ping = Command("ping", reply: "pong");
        using var manager = new ChatCommandManager(chat, new[] { ping, Command("other") }, Array.Empty<string>());

        chat.Say("!PING now");
        await Async.SettleAsync();

        await ping.Received(1).ExecuteAsync(Arg.Is<ChatCommandContext>(c => c.Arguments.SequenceEqual(new[] { "now" })));
        Assert.Equal(new[] { "pong" }, sent);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyReplies_AreNotSent(string? reply) => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        using var manager = new ChatCommandManager(chat, new[] { Command("quiet", reply: reply) }, Array.Empty<string>());

        chat.Say("!quiet");
        await Async.SettleAsync();

        Assert.Empty(sent);
    });

    [Fact]
    public void UnknownCommands_AreIgnored() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var known = Command("known");
        using var manager = new ChatCommandManager(chat, new[] { known }, Array.Empty<string>());

        chat.Say("!unknown");
        await Async.SettleAsync();

        await known.DidNotReceiveWithAnyArgs().ExecuteAsync(default!);
        Assert.Empty(sent);
    });

    [Fact]
    public void ModeratorCommands_AreIgnoredSilentlyForViewers() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var skip = Command("skip", moderatorOnly: true);
        using var manager = new ChatCommandManager(chat, new[] { skip }, new[] { "tester" });

        chat.Say("!skip", user: "viewer");
        await Async.SettleAsync();

        await skip.DidNotReceiveWithAnyArgs().ExecuteAsync(default!);
        Assert.Empty(sent);
    });

    public static TheoryData<ChatCommandContext> PrivilegedSenders => new()
    {
        Chat.Command("!skip", user: "mod", moderator: true),
        Chat.Command("!skip", user: "streamer", broadcaster: true),
        Chat.Command("!skip", user: "tester"),                   // listed by login name
        Chat.Command("!skip", user: "TESTER"),                   // case-insensitive
        Chat.Command("!skip", user: "renamed", userId: "4711")   // listed by user ID
    };

    [Theory]
    [MemberData(nameof(PrivilegedSenders))]
    public void ModeratorCommands_RunForModeratorsBroadcasterAndPrivilegedUsers(ChatCommandContext sender) => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out _);
        var skip = Command("skip", moderatorOnly: true);
        using var manager = new ChatCommandManager(chat, new[] { skip }, new[] { "tester", "4711" });

        Assert.True(manager.IsPrivileged(sender));
        chat.Say(sender);
        await Async.SettleAsync();

        await skip.Received(1).ExecuteAsync(sender);
    });

    [Fact]
    public void IsPrivileged_IgnoresAnEmptyUserId()
    {
        var manager = new ChatCommandManager(Chat.Channel(out _), Array.Empty<IChatCommand>(), new[] { "" });

        Assert.False(manager.IsPrivileged(Chat.Command("!x", user: "viewer", userId: "")));
    }

    [Fact]
    public void Cooldown_IsSharedByAllUsers_AndEndsAfterItsDuration() => UiContext.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var chat = Chat.Channel(out var sent);
        var current = Command("current", cooldown: TimeSpan.FromSeconds(5), reply: "03:21");
        using var manager = new ChatCommandManager(chat, new[] { current }, Array.Empty<string>(), time);

        chat.Say("!current", user: "a");
        chat.Say("!current", user: "b");
        time.Advance(TimeSpan.FromSeconds(4.9));
        chat.Say("!current", user: "c");
        await Async.SettleAsync();
        Assert.Single(sent);

        time.Advance(TimeSpan.FromSeconds(0.1));
        chat.Say("!current", user: "d");
        await Async.SettleAsync();
        Assert.Equal(2, sent.Count);
    });

    [Fact]
    public void IgnoredViewers_DoNotStartTheCooldown() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out _);
        var skip = Command("skip", moderatorOnly: true, cooldown: TimeSpan.FromMinutes(1));
        using var manager = new ChatCommandManager(chat, new[] { skip }, Array.Empty<string>(), new FakeTimeProvider());

        chat.Say("!skip", user: "viewer");
        chat.Say("!skip", user: "mod", moderator: true);
        await Async.SettleAsync();

        await skip.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());
    });

    [Fact]
    public void CommandsOfOneCooldownGroup_ShareOneTimer() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out _);
        var time = new FakeTimeProvider();
        var cooldown = TimeSpan.FromSeconds(3);
        var skip = Command("forceskip", cooldown: cooldown, group: "playbackControl");
        var pause = Command("pause", cooldown: cooldown, group: "playbackControl");
        var current = Command("current", cooldown: cooldown);
        using var manager = new ChatCommandManager(chat, new[] { skip, pause, current }, Array.Empty<string>(), time);

        chat.Say("!forceskip", user: "mod1");
        chat.Say("!pause", user: "mod2");     // blocked: the same group just ran
        chat.Say("!current", user: "viewer"); // another timer
        await Async.SettleAsync();

        await skip.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());
        await pause.DidNotReceiveWithAnyArgs().ExecuteAsync(default!);
        await current.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());

        time.Advance(cooldown);
        chat.Say("!pause", user: "mod2");
        await Async.SettleAsync();

        await pause.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());
    });

    [Fact]
    public void AnEmptyCooldownGroup_MeansAnOwnTimer() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out _);
        var a = Command("a", cooldown: TimeSpan.FromMinutes(1), group: "");
        var b = Command("b", cooldown: TimeSpan.FromMinutes(1), group: "");
        using var manager = new ChatCommandManager(chat, new[] { a, b }, Array.Empty<string>(), new FakeTimeProvider());

        chat.Say("!a");
        chat.Say("!b");
        await Async.SettleAsync();

        await a.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());
        await b.Received(1).ExecuteAsync(Arg.Any<ChatCommandContext>());
    });

    [Fact]
    public void TheCommandsThatChangeWhatPlays_ShareThePlaybackControlCooldown()
    {
        var grouped = typeof(IChatCommand).Assembly.GetTypes()
            .Where(t => typeof(IChatCommand).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .Select(t => (IChatCommand)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t))
            .Where(c => c.CooldownGroup == ChatCommandManager.PlaybackControlCooldownGroup)
            .Select(c => c.Name)
            .OrderBy(n => n);

        Assert.Equal(new[] { "forceskip", "pause", "play", "reload", "skipplaylist" }, grouped);
    }

    [Fact]
    public void CommandsWithoutCooldown_RunEveryTime() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out _);
        var vote = Command("vote", reply: null);
        using var manager = new ChatCommandManager(chat, new[] { vote }, Array.Empty<string>(), new FakeTimeProvider());

        for (var i = 0; i < 5; i++)
            chat.Say("!vote 1", user: "u" + i);
        await Async.SettleAsync();

        await vote.Received(5).ExecuteAsync(Arg.Any<ChatCommandContext>());
    });

    [Fact]
    public void AFailingCommand_DoesNotBreakTheManager() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var broken = Command("broken");
        broken.ExecuteAsync(Arg.Any<ChatCommandContext>()).ThrowsAsync(new InvalidOperationException("bug"));
        using var manager = new ChatCommandManager(chat, new[] { broken, Command("ping", reply: "pong") }, Array.Empty<string>());

        chat.Say("!broken");
        chat.Say("!ping");
        await Async.SettleAsync();

        Assert.Equal(new[] { "pong" }, sent);
    });

    [Fact]
    public void Dispose_StopsListening() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var manager = new ChatCommandManager(chat, new[] { Command("ping") }, Array.Empty<string>());

        manager.Dispose();
        chat.Say("!ping");
        await Async.SettleAsync();

        Assert.Empty(sent);
    });
}
