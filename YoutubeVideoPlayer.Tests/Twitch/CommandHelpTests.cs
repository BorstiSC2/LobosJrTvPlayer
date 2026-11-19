using System.Runtime.CompilerServices;
using NSubstitute;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Commands;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class CommandHelpTests
{
    private static IChatCommand Command(string name, bool moderatorOnly, string description, bool hidden = false)
    {
        var command = Substitute.For<IChatCommand>();
        command.Name.Returns(name);
        command.RequiresModerator.Returns(moderatorOnly);
        command.Description.Returns(description);
        command.IsHidden.Returns(hidden);
        command.Cooldown.Returns(TimeSpan.Zero);
        command.ExecuteAsync(Arg.Any<ChatCommandContext>()).Returns("ran");
        return command;
    }

    /// <summary>Wires the help the way <c>Program</c> does: it asks the manager for its commands.</summary>
    private static ChatCommandManager CreateManager(IChatChannel chat)
    {
        ChatCommandManager? manager = null;
        var commands = new List<IChatCommand>
        {
            Command("playlist", false, "Tells you which playlist is playing."),
            Command("forceskip", true, "Skips the current video."),
            Command("stay", false, "Your vote to keep watching.", hidden: true),
            new CommandHelpCommand(() => manager!.Commands, TimeSpan.Zero)
        };
        manager = new ChatCommandManager(chat, commands, Array.Empty<string>());
        return manager;
    }

    private static List<string> Ask(string text, bool moderator = false) => Run(chat => chat.Say(text, moderator: moderator));

    private static List<string> Run(Action<IChatChannel> say)
    {
        List<string> result = new();
        UiContext.Run(async () =>
        {
            var chat = Chat.Channel(out var sent);
            using var manager = CreateManager(chat);
            say(chat);
            await Async.SettleAsync();
            result = sent;
        });
        return result;
    }

    [Fact]
    public void Metadata_IsForEverybody()
    {
        IChatCommand help = new CommandHelpCommand(() => Array.Empty<IChatCommand>(), TimeSpan.FromSeconds(5));

        Assert.Equal("commandhelp", help.Name);
        Assert.False(help.RequiresModerator);
        Assert.Equal(TimeSpan.FromSeconds(5), help.Cooldown);
    }

    [Fact]
    public void WithoutAName_ExplainsHowToUseIt()
    {
        Assert.Equal(new[] { CommandHelpCommand.Tutorial }, Ask("!commandhelp"));
        Assert.Contains("!commandhelp playlist", CommandHelpCommand.Tutorial);
    }

    [Theory]
    [InlineData("!commandhelp playlist")]
    [InlineData("!commandhelp !playlist")]
    [InlineData("!commandhelp PlayList")]
    [InlineData("!commandhelp playlist and more words")]
    public void WithTheNameOfACommand_DescribesIt(string text)
    {
        Assert.Equal(new[] { "!playlist: Tells you which playlist is playing." }, Ask(text));
    }

    [Fact]
    public void AModeratorCommand_IsDescribedToEverybody_AndMarkedAsModeratorsOnly()
    {
        const string expected = "!forceskip (moderators only): Skips the current video.";

        Assert.Equal(new[] { expected }, Ask("!commandhelp forceskip"));
        Assert.Equal(new[] { expected }, Ask("!commandhelp !forceskip", moderator: true));
    }

    /// <summary>The ballots (<c>!vote</c>, <c>!skip</c>, <c>!stay</c>) are explained by the vote itself, when they are needed.</summary>
    [Theory]
    [InlineData("!commandhelp stay")]
    [InlineData("!commandhelp !stay")]
    public void AHiddenCommand_GetsNoReply(string text)
    {
        Assert.Empty(Ask(text));
    }

    [Fact]
    public void ItDescribesItself()
    {
        var reply = Assert.Single(Ask("!commandhelp commandhelp"));
        Assert.StartsWith("!commandhelp: Explains what a command does.", reply);
    }

    [Theory]
    [InlineData("!commandhelp nosuchcommand")]
    [InlineData("!commandhelp !")]
    [InlineData("!commandhelp play")] // only whole names match
    public void AnUnknownName_GetsNoReply(string text)
    {
        Assert.Empty(Ask(text));
    }

    [Fact]
    public void AnUnknownName_DoesNotRunThatCommand()
    {
        var sent = Run(chat =>
        {
            chat.Say("!commandhelp forceskip");
            chat.Say("!commandhelp xyz");
        });

        Assert.DoesNotContain("ran", sent);
    }

    /// <summary>
    /// Every command of the app has a description for <c>!commandhelp</c> that fits into one chat message with its
    /// prefix. The descriptions are constant, so they are read from uninitialized instances (no constructor arguments needed).
    /// </summary>
    [Fact]
    public void EveryCommandOfTheApp_HasADescriptionThatFitsIntoOneMessage()
    {
        var types = typeof(IChatCommand).Assembly.GetTypes()
            .Where(t => typeof(IChatCommand).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();
        Assert.True(types.Count >= 25, $"Only {types.Count} commands found.");

        foreach (var type in types)
        {
            var command = (IChatCommand)RuntimeHelpers.GetUninitializedObject(type);
            Assert.False(string.IsNullOrWhiteSpace(command.Description), $"!{command.Name} has no description.");
            var reply = $"!{command.Name} (moderators only): {command.Description}";
            Assert.True(reply.Length <= ChatCommandManager.MaxMessageLength, $"The description of !{command.Name} is too long.");
        }

        Assert.True(CommandHelpCommand.Tutorial.Length <= ChatCommandManager.MaxMessageLength);
    }
}
