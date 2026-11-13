using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YouTubeVideoPlayer.Infrastructure;

namespace YouTubeVideoPlayer.Twitch
{
    /// <summary>A chat command the bot reacts to.</summary>
    public interface IChatCommand
    {
        /// <summary>Command name in lower case, without the <c>!</c>.</summary>
        string Name { get; }

        /// <summary>
        /// If <c>true</c>, only moderators, the broadcaster and the users listed in
        /// <see cref="TwitchSettings.PrivilegedUsers"/> may use the command.
        /// </summary>
        bool RequiresModerator { get; }

        /// <summary>Minimum time between two executions (shared by all users).</summary>
        TimeSpan Cooldown { get; }

        /// <summary>Runs the command and returns the chat reply, or <c>null</c> for no reply.</summary>
        Task<string?> ExecuteAsync(ChatCommandContext context);
    }

    /// <summary>
    /// Routes chat commands from an <see cref="IChatChannel"/> to their <see cref="IChatCommand"/>,
    /// checking permissions and cooldowns, and posts the replies.
    /// </summary>
    public sealed class ChatCommandManager : IDisposable
    {
        private readonly IChatChannel _chat;
        private readonly HashSet<string> _privilegedUsers;
        private readonly Dictionary<string, IChatCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _lastExecutionUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeProvider _time;

        /// <param name="privilegedUsers">Login names or user IDs that count as moderators.</param>
        /// <param name="timeProvider">Clock for the cooldowns; <see cref="TimeProvider.System"/> by default.</param>
        public ChatCommandManager(
            IChatChannel chat,
            IEnumerable<IChatCommand> commands,
            IEnumerable<string> privilegedUsers,
            TimeProvider? timeProvider = null)
        {
            _chat = chat;
            _time = timeProvider ?? TimeProvider.System;
            _privilegedUsers = new HashSet<string>(privilegedUsers, StringComparer.OrdinalIgnoreCase);
            foreach (var command in commands)
                _commands[command.Name] = command;

            _chat.CommandReceived += OnCommandReceived;
        }

        /// <summary>The registered commands, in the order they were registered (used to build the command lists).</summary>
        public IReadOnlyCollection<IChatCommand> Commands => _commands.Values;

        /// <summary>Moderators, the broadcaster and the users from the settings' allow list.</summary>
        public bool IsPrivileged(ChatCommandContext context) =>
            context.IsModerator
            || context.IsBroadcaster
            || _privilegedUsers.Contains(context.Username)
            || (!string.IsNullOrEmpty(context.UserId) && _privilegedUsers.Contains(context.UserId));

        private void OnCommandReceived(object? sender, ChatCommandContext context)
        {
            HandleAsync(context).FireAndForget($"Chat command !{context.Name} failed.");
        }

        private async Task HandleAsync(ChatCommandContext context)
        {
            if (!_commands.TryGetValue(context.Name, out var command))
                return;

            // Commands that need moderator rights are ignored silently for everybody else.
            if (command.RequiresModerator && !IsPrivileged(context))
            {
                Logger.Log($"Ignored !{command.Name} from {context.Username}: not a moderator.");
                return;
            }

            if (command.Cooldown > TimeSpan.Zero)
            {
                var now = _time.GetUtcNow();
                if (_lastExecutionUtc.TryGetValue(command.Name, out var last) && now - last < command.Cooldown)
                    return;

                _lastExecutionUtc[command.Name] = now;
                Logger.Log($"Executing !{command.Name} for {context.Username}.");
            }

            var reply = await command.ExecuteAsync(context);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                foreach (var part in SplitMessage(reply))
                    await _chat.SendMessageAsync(part);
            }
        }

        /// <summary>Twitch accepts 500 characters per message; a longer reply (e.g. a long command list) is split at spaces.</summary>
        internal const int MaxMessageLength = 450;

        internal static IEnumerable<string> SplitMessage(string message)
        {
            var rest = message.Trim();
            while (rest.Length > MaxMessageLength)
            {
                var cut = rest.LastIndexOf(' ', MaxMessageLength);
                if (cut <= 0)
                    cut = MaxMessageLength;

                yield return rest[..cut].TrimEnd();
                rest = rest[cut..].TrimStart();
            }

            if (rest.Length > 0)
                yield return rest;
        }

        public void Dispose()
        {
            _chat.CommandReceived -= OnCommandReceived;
        }
    }
}
