using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YouTubeVideoPlayer.Twitch.Commands
{
    /// <summary>
    /// <c>!commandhelp</c> (everybody): without an argument it explains how to use it; with a command name
    /// (<c>!commandhelp playlist</c> or <c>!commandhelp !playlist</c>, any case) it posts that command's
    /// <see cref="IChatCommand.Description"/>, marked "moderators only" for those. Moderator commands are described to
    /// everybody (their names are no secret, and the mark tells a viewer why they do nothing for them). A name that is
    /// not a registered command gets no reply, and so do the hidden ballots (<c>!vote</c>, <c>!skip</c>, <c>!stay</c>):
    /// the vote's chat messages and the ticker explain them whenever they are needed.
    /// </summary>
    public sealed class CommandHelpCommand : IChatCommand
    {
        internal const string Tutorial =
            "Want to know what a command does? Type !commandhelp followed by the command's name, for example: "
            + "!commandhelp playlist. Type !vodcommands to see all the commands you can use.";

        private readonly Func<IEnumerable<IChatCommand>> _commands;

        /// <param name="commands">All registered commands, asked for every time (the command manager is created after this command).</param>
        public CommandHelpCommand(Func<IEnumerable<IChatCommand>> commands, TimeSpan cooldown)
        {
            _commands = commands;
            Cooldown = cooldown;
        }

        public string Name => "commandhelp";

        public string Description =>
            "Explains what a command does. Type !commandhelp and the command's name, for example !commandhelp playlist.";

        public bool RequiresModerator => false;

        public TimeSpan Cooldown { get; }

        public Task<string?> ExecuteAsync(ChatCommandContext context) => Task.FromResult(Describe(context.Arguments));

        private string? Describe(IReadOnlyList<string> arguments)
        {
            if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
                return Tutorial;

            var name = arguments[0].Trim().TrimStart('!');
            var command = _commands().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (command == null || command.IsHidden || string.IsNullOrWhiteSpace(command.Description))
                return null;

            var who = command.RequiresModerator ? " (moderators only)" : "";
            return $"!{command.Name}{who}: {command.Description}";
        }
    }
}
