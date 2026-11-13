using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>Builds the chat text of a command list from the commands that are registered at that moment.</summary>
    internal static class CommandList
    {
        public static string Describe(string heading, IEnumerable<IChatCommand> commands)
        {
            var names = commands.Select(c => "!" + c.Name).ToList();
            return names.Count == 0 ? $"{heading}: none." : $"{heading}: {string.Join(", ", names)}";
        }
    }

    /// <summary>
    /// <c>!vodcommands</c> (everybody): lists the other commands everybody may use (not itself). The list is
    /// built from the registered commands, so a new command shows up without changing this class.
    /// </summary>
    public sealed class VodCommandsCommand : IChatCommand
    {
        private readonly Func<IEnumerable<IChatCommand>> _commands;

        /// <param name="commands">All registered commands, asked for every time (the command manager is created after this command).</param>
        public VodCommandsCommand(Func<IEnumerable<IChatCommand>> commands, TimeSpan cooldown)
        {
            _commands = commands;
            Cooldown = cooldown;
        }

        public string Name => "vodcommands";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(CommandList.Describe("Commands", _commands().Where(c => !c.RequiresModerator && !ReferenceEquals(c, this))));
    }

    /// <summary>
    /// <c>!modvodcommands</c> (moderators, broadcaster and privileged users): lists the commands that only
    /// they may use (not itself). Like <see cref="VodCommandsCommand"/> it is built from the registered commands.
    /// </summary>
    public sealed class ModVodCommandsCommand : IChatCommand
    {
        private readonly Func<IEnumerable<IChatCommand>> _commands;

        /// <param name="commands">All registered commands, asked for every time (the command manager is created after this command).</param>
        public ModVodCommandsCommand(Func<IEnumerable<IChatCommand>> commands, TimeSpan cooldown)
        {
            _commands = commands;
            Cooldown = cooldown;
        }

        public string Name => "modvodcommands";

        public bool RequiresModerator => true;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) =>
            Task.FromResult<string?>(CommandList.Describe("Moderator commands", _commands().Where(c => c.RequiresModerator && !ReferenceEquals(c, this))));
    }
}
