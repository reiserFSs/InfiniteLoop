using System.Globalization;

namespace AscNet.GameServer.Commands
{
    [CommandName("level")]
    internal class LevelCommand : Command
    {
        public LevelCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Command to change the Commandant level";

        [Argument(0, @"^[0-9]+$|^max$", "The target level, value is number or 'max'")]
        string Level { get; set; } = string.Empty;

        public override void Execute()
        {
            int level = Level == "max"
                ? SessionExtensions.MaximumConfiguredPlayerLevel ?? throw new InvalidDataException("Player level table is empty.")
                : int.Parse(Level, NumberStyles.None, CultureInfo.InvariantCulture);
            GmPlayerService.SetLevel(session, level);
        }
    }
}
