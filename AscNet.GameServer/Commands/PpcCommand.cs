using AscNet.GameServer.Handlers;

namespace AscNet.GameServer.Commands
{
    [CommandName("ppc")]
    internal sealed class PpcCommand : Command
    {
        public PpcCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Use /ppc tiers, /ppc tier <LevelType>, /ppc reset, /ppc codex reset, /ppc attempts <count|reset>, "
            + "or /ppc intensive [bosses | boss <name|SectionId> | weaknesses | weakness <group> | reset].";

        private const string IntensiveHelp = "Usage: /ppc intensive (current selection), /ppc intensive bosses, /ppc intensive boss <name|SectionId>, "
            + "/ppc intensive weaknesses, /ppc intensive weakness <group>, /ppc intensive reset (weekly default).";

        [Argument(0, @"^(tiers|tier|reset|codex|attempts|intensive)$", "PPC command", ArgumentFlags.IgnoreCase)]
        string Action { get; set; } = string.Empty;

        [Argument(1, @"^(reset|[0-9]+|status|bosses|boss|weaknesses|weakness|zones|zone)$", "PPC command argument", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Value { get; set; } = string.Empty;

        [Argument(2, @"^\S+$", "Intensive boss name/SectionId or weakness group", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string ExtraValue { get; set; } = string.Empty;

        public override void Execute()
        {
            if (Action.Equals("intensive", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteIntensive();
                return;
            }

            if (Action.Equals("tiers", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(Value))
                    throw new CommandMessageCallbackException("Usage: /ppc tiers");
                IReadOnlyList<int> levels = BossModule.GetTierTypesForCommand(session);
                throw new CommandMessageCallbackException(levels.Count == 0
                    ? "No PPC tiers are available for your Commandant level in this activity."
                    : $"Valid PPC LevelType IDs: {string.Join(", ", levels)}. Use /ppc tier <LevelType>.");
            }

            if (Action.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(Value))
                    throw new CommandMessageCallbackException("Usage: /ppc reset");
                BossModule.SetChallengeAttemptsFromCommand(session, 0);
                return;
            }

            if (Action.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                if (!Value.Equals("reset", StringComparison.OrdinalIgnoreCase))
                    throw new CommandMessageCallbackException("Usage: /ppc codex reset");
                BossModule.ResetCodexScoresFromCommand(session);
                return;
            }

            if (Action.Equals("attempts", StringComparison.OrdinalIgnoreCase))
            {
                if (Value.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    BossModule.SetChallengeAttemptsFromCommand(session, 0);
                    return;
                }
                if (!int.TryParse(Value, out int attempts))
                    throw new CommandMessageCallbackException("Usage: /ppc attempts <count|reset>");
                BossModule.SetChallengeAttemptsFromCommand(session, attempts);
                return;
            }

            if (!int.TryParse(Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int levelType))
                throw new CommandMessageCallbackException("Usage: /ppc tier <LevelType>. Use /ppc tiers to list valid IDs.");

            BossModule.SetTierFromCommand(session, levelType);
        }

        private void ExecuteIntensive()
        {
            // Boss names can contain spaces ("Iron Maiden: Phantom"), so the boss query is every remaining word.
            string query = string.Join(' ', args.Skip(2)).Trim();
            string action = Value.ToLowerInvariant();
            switch (action)
            {
                case "" or "status" when query.Length == 0:
                    throw new CommandMessageCallbackException(BossModule.DescribeIntensiveForCommand(session));
                case "bosses" or "zones" when query.Length == 0:
                    throw new CommandMessageCallbackException(BossModule.ListIntensiveBossesForCommand(session));
                case "weaknesses" when query.Length == 0:
                    throw new CommandMessageCallbackException(BossModule.ListIntensiveWeaknessesForCommand(session));
                case "reset" when query.Length == 0:
                    BossModule.ResetIntensiveFromCommand(session);
                    return;
                case "boss" or "zone" when query.Length > 0:
                    BossModule.SetIntensiveBossFromCommand(session, query);
                    return;
                case "weakness" when query.Length > 0:
                    if (!int.TryParse(query, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int featureGroupId))
                        throw new CommandMessageCallbackException("Usage: /ppc intensive weakness <group>. Use /ppc intensive weaknesses to list groups.");
                    BossModule.SetIntensiveWeaknessFromCommand(session, featureGroupId);
                    return;
                default:
                    throw new CommandMessageCallbackException(IntensiveHelp);
            }
        }
    }
}
