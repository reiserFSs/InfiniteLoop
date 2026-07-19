using System;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AscNet.Logging;
using Newtonsoft.Json;

namespace AscNet.GameServer.Commands
{
    public abstract class Command
    {
        protected Session session;
        protected string[] args;

        public abstract string Help { get; }

        /// <summary>
        /// Make sure to handle me well...
        /// </summary>
        /// <param name="session"></param>
        /// <param name="args"></param>
        /// <exception cref="ArgumentException"></exception>
        public Command(Session session, string[] args, bool validate = true)
        {
            this.session = session;
            this.args = args;

            string? ret = Validate();
            if (ret is not null && validate)
                throw new ArgumentException(ret);
        }

        public string? Validate()
        {
            List<PropertyInfo> argsProperties = GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => x.GetCustomAttribute(typeof(ArgumentAttribute)) is not null).ToList();
            if (argsProperties.Where(x => (((ArgumentAttribute)x.GetCustomAttribute(typeof(ArgumentAttribute))!).Flags & ArgumentFlags.Optional) != ArgumentFlags.Optional).Count() > args.Length)
                return "Invalid args length!";

            foreach (var argProp in argsProperties)
            {
                ArgumentAttribute attr = (ArgumentAttribute)argProp.GetCustomAttribute(typeof(ArgumentAttribute))!;
                if (attr.Position + 1 > args.Length && (attr.Flags & ArgumentFlags.Optional) != ArgumentFlags.Optional)
                    return $"Argument {argProp.Name} is required!";
                else if (attr.Position + 1 > args.Length)
                    return null;

                if (!attr.Pattern.IsMatch(args[attr.Position]))
                    return $"Argument {argProp.Name} is invalid!";

                argProp.SetValue(this, args[attr.Position]);
            }
            return null;
        }

        public abstract void Execute();
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ArgumentAttribute : Attribute
    {
        public int Position { get; }
        public Regex Pattern { get; set; }
        public string? Description { get; }
        public ArgumentFlags Flags { get; }

        public ArgumentAttribute(int position, string pattern, string? description = null, ArgumentFlags flags = ArgumentFlags.None)
        {
            Position = position;

            if ((flags & ArgumentFlags.IgnoreCase) != ArgumentFlags.IgnoreCase)
                Pattern = new(pattern);
            else
                Pattern = new(pattern, RegexOptions.IgnoreCase);

            Description = description;
            Flags = flags;
        }
    }

    public enum ArgumentFlags
    {
        None = 0,
        Optional = 1,
        IgnoreCase = 2
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class CommandNameAttribute : Attribute
    {
        public string Name { get; }

        public CommandNameAttribute(string name)
        {
            Name = name;
        }
    }

    [CommandName("help")]
    internal class HelpCommand : Command
    {
        public HelpCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "List commands or show details for one command.";

        [Argument(0, @"^[^\s]+$", "Command to describe", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string CommandName { get; set; } = string.Empty;

        public override void Execute()
        {
            if (!string.IsNullOrEmpty(CommandName))
            {
                string commandName = CommandName.ToLowerInvariant();
                Command? command = CommandFactory.CreateCommand(commandName, session, [], false);
                if (command is null)
                    throw new CommandMessageCallbackException($"Unknown command /{CommandName}.");

                List<(PropertyInfo Property, ArgumentAttribute Argument)> arguments = GetArguments(command);
                StringBuilder details = new();
                details.AppendLine(BuildUsage(commandName, arguments));
                details.AppendLine(command.Help);
                foreach ((PropertyInfo property, ArgumentAttribute argument) in arguments)
                    details.AppendLine($"{property.Name}: {argument.Description ?? "No description available."}");

                throw new CommandMessageCallbackException(details.ToString().TrimEnd());
            }

            StringBuilder overview = new("Available commands:\n");
            foreach (string commandName in CommandFactory.commands.Keys.Order(StringComparer.Ordinal))
            {
                Command? command = CommandFactory.CreateCommand(commandName, session, [], false);
                if (command is null)
                    continue;

                overview.Append(BuildUsage(commandName, GetArguments(command)));
                overview.Append(" — ");
                overview.AppendLine(command.Help);
            }
            overview.Append("Use /help <command> for details.");

            throw new CommandMessageCallbackException(overview.ToString());
        }

        private static List<(PropertyInfo Property, ArgumentAttribute Argument)> GetArguments(Command command)
        {
            return command.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(property => (Property: property, Argument: property.GetCustomAttribute<ArgumentAttribute>()))
                .Where(entry => entry.Argument is not null)
                .Select(entry => (Property: entry.Property, Argument: entry.Argument!))
                .OrderBy(entry => entry.Argument.Position)
                .ToList();
        }

        private static string BuildUsage(
            string commandName,
            IReadOnlyList<(PropertyInfo Property, ArgumentAttribute Argument)> arguments)
        {
            if (arguments.Count == 0)
                return $"/{commandName}";

            string argumentList = string.Join(" ", arguments.Select(entry =>
                (entry.Argument.Flags & ArgumentFlags.Optional) == ArgumentFlags.Optional
                    ? $"[{entry.Property.Name}]"
                    : $"<{entry.Property.Name}>"));
            return $"/{commandName} {argumentList}";
        }
    }

    public static class CommandFactory
    {
        public static readonly Dictionary<string, Type> commands = new();

        internal static readonly Logger log = new(typeof(CommandFactory), LogLevel.DEBUG, LogLevel.DEBUG);
        
        public static void LoadCommands()
        {
            log.LogLevelColor[LogLevel.INFO] = ConsoleColor.White;
            log.Info("Loading commands...");

            IEnumerable<Type> classes = from t in Assembly.GetExecutingAssembly().GetTypes()
                                        where t.IsClass && t.GetCustomAttribute<CommandNameAttribute>() is not null
                                        select t;

            foreach (var command in classes)
            {
                CommandNameAttribute nameAttr = command.GetCustomAttribute<CommandNameAttribute>()!;
                commands.Add(nameAttr.Name, command);
            }

            log.Info("Finished loading commands");
        }

        public static Command? CreateCommand(string name, Session session, string[] args, bool validate = true)
        {
            Type? command = commands.GetValueOrDefault(name);
            if (command is null)
                return null;

            return (Command)Activator.CreateInstance(command, new object[] { session, args, validate })!;
        }
    }

    public class CommandMessageCallbackException : Exception
    {
        public CommandMessageCallbackException(string message)
            : base(message) { }
    }
}
