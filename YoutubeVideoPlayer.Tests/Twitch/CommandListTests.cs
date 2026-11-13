using NSubstitute;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class CommandListTests
{
    private static IChatCommand Command(string name, bool moderatorOnly)
    {
        var command = Substitute.For<IChatCommand>();
        command.Name.Returns(name);
        command.RequiresModerator.Returns(moderatorOnly);
        command.Cooldown.Returns(TimeSpan.Zero);
        command.ExecuteAsync(Arg.Any<ChatCommandContext>()).Returns("ok");
        return command;
    }

    /// <summary>Wires the two list commands the way <c>Program</c> does: they ask the manager for its commands.</summary>
    private static ChatCommandManager CreateManager(IChatChannel chat, params IChatCommand[] others)
    {
        ChatCommandManager? manager = null;
        var commands = new List<IChatCommand>(others)
        {
            new VodCommandsCommand(() => manager!.Commands, TimeSpan.Zero),
            new ModVodCommandsCommand(() => manager!.Commands, TimeSpan.Zero)
        };
        manager = new ChatCommandManager(chat, commands, Array.Empty<string>());
        return manager;
    }

    [Fact]
    public void Metadata_VodCommandsIsForEverybody_ModVodCommandsOnlyForModerators()
    {
        IChatCommand everybody = new VodCommandsCommand(() => Array.Empty<IChatCommand>(), TimeSpan.FromSeconds(5));
        IChatCommand moderators = new ModVodCommandsCommand(() => Array.Empty<IChatCommand>(), TimeSpan.FromSeconds(5));

        Assert.Equal("vodcommands", everybody.Name);
        Assert.False(everybody.RequiresModerator);
        Assert.Equal("modvodcommands", moderators.Name);
        Assert.True(moderators.RequiresModerator);
    }

    [Fact]
    public void VodCommands_ListsTheCommandsOfEverybody_IncludingNewOnes() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        // "brandnew" is a command this test class has never heard of: it must show up without any change to the lists.
        using var manager = CreateManager(chat, Command("current", false), Command("skip", true), Command("brandnew", false));

        chat.Say("!VodCommands");
        await Async.SettleAsync();

        Assert.Equal(new[] { "Commands: !current, !brandnew" }, sent);
    });

    [Fact]
    public void ModVodCommands_ListsOnlyTheModeratorCommands_IncludingNewOnes() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        using var manager = CreateManager(chat, Command("current", false), Command("skip", true), Command("brandnewmod", true));

        chat.Say("!modvodcommands", moderator: true);
        await Async.SettleAsync();

        Assert.Equal(new[] { "Moderator commands: !skip, !brandnewmod" }, sent);
    });

    [Fact]
    public void ModVodCommands_IsIgnoredForNormalViewers_AndWorksForPrivilegedUsers() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        ChatCommandManager? manager = null;
        manager = new ChatCommandManager(
            chat,
            new IChatCommand[] { new ModVodCommandsCommand(() => manager!.Commands, TimeSpan.Zero) },
            new[] { "trusted" });

        chat.Say("!modvodcommands", user: "viewer");
        await Async.SettleAsync();
        Assert.Empty(sent);

        chat.Say("!modvodcommands", user: "trusted");
        await Async.SettleAsync();
        Assert.Equal(new[] { "Moderator commands: none." }, sent);
        manager.Dispose();
    });

    [Fact]
    public void AListWithoutCommands_SaysSo() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        using var manager = new ChatCommandManager(
            chat, new IChatCommand[] { new VodCommandsCommand(() => Array.Empty<IChatCommand>(), TimeSpan.Zero) }, Array.Empty<string>());

        chat.Say("!vodcommands");
        await Async.SettleAsync();

        Assert.Equal(new[] { "Commands: none." }, sent);
    });

    [Fact]
    public void ALongList_IsSplitIntoSeveralMessages_ThatFitIntoOneChatMessage() => UiContext.Run(async () =>
    {
        var chat = Chat.Channel(out var sent);
        var many = Enumerable.Range(0, 80).Select(i => Command($"somewhatlongcommandname{i}", false)).ToArray();
        using var manager = CreateManager(chat, many);

        chat.Say("!vodcommands");
        await Async.SettleAsync();

        Assert.True(sent.Count > 1);
        Assert.All(sent, message => Assert.True(message.Length <= ChatCommandManager.MaxMessageLength));
        Assert.Contains("!somewhatlongcommandname79", string.Join(" ", sent)); // nothing got lost
    });

    [Fact]
    public void SplitMessage_KeepsShortMessagesAndCutsAtSpaces()
    {
        Assert.Equal(new[] { "short message" }, ChatCommandManager.SplitMessage("short message"));

        var parts = ChatCommandManager.SplitMessage(string.Join(" ", Enumerable.Repeat("word", 300))).ToList();

        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(part.Length <= ChatCommandManager.MaxMessageLength));
        Assert.All(parts, part => Assert.DoesNotContain("wor ", part + " ")); // no word cut in half
        Assert.Equal(300, parts.Sum(part => part.Split(' ').Length));
    }
}
