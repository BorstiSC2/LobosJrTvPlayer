using NSubstitute;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer.Tests.TestSupport;

/// <summary>Builds chat commands and a substitute chat channel that records sent messages.</summary>
public static class Chat
{
    public static ChatCommandContext Command(
        string text,
        string user = "viewer",
        string? userId = null,
        bool moderator = false,
        bool broadcaster = false)
    {
        var parts = text.TrimStart('!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new ChatCommandContext(
            parts[0].ToLowerInvariant(),
            parts.Skip(1).ToList(),
            userId ?? "id-" + user,
            user,
            user.ToUpperInvariant(),
            moderator,
            broadcaster);
    }

    /// <summary>A substitute <see cref="IChatChannel"/> whose sent messages are collected in <paramref name="sent"/>.</summary>
    public static IChatChannel Channel(out List<string> sent)
    {
        var messages = new List<string>();
        var channel = Substitute.For<IChatChannel>();
        channel.IsConnected.Returns(true);
        channel.SendMessageAsync(Arg.Do<string>(messages.Add)).Returns(Task.CompletedTask);
        sent = messages;
        return channel;
    }

    /// <summary>Raises <see cref="IChatChannel.CommandReceived"/> on the substitute.</summary>
    public static void Say(this IChatChannel channel, ChatCommandContext command) =>
        channel.CommandReceived += Raise.Event<EventHandler<ChatCommandContext>>(channel, command);

    public static void Say(this IChatChannel channel, string text, string user = "viewer", string? userId = null,
        bool moderator = false, bool broadcaster = false) =>
        channel.Say(Command(text, user, userId, moderator, broadcaster));
}
