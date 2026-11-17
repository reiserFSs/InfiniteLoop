using AscNet.GameServer.Handlers;

namespace AscNet.GameServer.Commands
{
    [CommandName("warzone")]
    internal class WarzoneCommand : Command
    {
        public WarzoneCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Use /warzone reset, /warzone tiers, /warzone tier <Id|name>, /warzone zones [element|zone name] [boss|duo|mob], "
            + "/warzone set <element|zone name|AreaId> [boss|duo|mob] (e.g. /warzone set ice, /warzone set plasma duo), /warzone zone <AreaId|auto>, /warzone fluctuation <level>, "
            + "or /warzone dmg [percent|reset] [boss|duo|mob] (install-wide damage-score tuning for every player on this server, 100 = default; e.g. /warzone dmg 120 boss).";

        [Argument(0, @"^[A-Za-z]+$|^[0-9]+$", "Tier ID/name or 'reset', 'tiers', 'tier', 'zones', 'set', 'zone', 'fluctuation', or 'dmg' action", ArgumentFlags.IgnoreCase)]
        protected string Operation { get; set; } = string.Empty;

        [Argument(1, @"^[A-Za-z0-9_\-]+%?$", "Tier ID/name, element or zone name, AreaId, fluctuation level, damage percent, or 'auto'", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string Value { get; set; } = string.Empty;

        [Argument(2, @"^[A-Za-z0-9_\-]+$", "For set/zones: encounter type (boss|duo|mob) or the next word of a zone name", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string Type { get; set; } = string.Empty;

        // Words after the action (zone names may contain spaces, e.g. "crimson frostland boss").
        private string[] Terms => args.Skip(1).ToArray();

        public override void Execute()
        {
            if (Operation.Equals("set", StringComparison.OrdinalIgnoreCase))
                throw new CommandMessageCallbackException(ArenaModule.SetZoneByQueryFromCommand(session, Terms));

            if (Operation.Equals("dmg", StringComparison.OrdinalIgnoreCase))
                throw new CommandMessageCallbackException(ArenaModule.DamageScoreFromCommand(Terms));

            if (Operation.Equals("zones", StringComparison.OrdinalIgnoreCase))
                throw new CommandMessageCallbackException(ArenaModule.ListZonesForCommand(session, Terms));

            if (Operation.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(Value))
                    throw new CommandMessageCallbackException("Usage: /warzone reset");
                ArenaModule.ResetFromCommand(session);
                return;
            }

            if (Operation.Equals("tiers", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(Value))
                    throw new CommandMessageCallbackException("Usage: /warzone tiers");
                IReadOnlyList<(int Id, string Name)> tiers = ArenaModule.GetTiersForCommand(session);
                throw new CommandMessageCallbackException(tiers.Count == 0
                    ? "No Warzone tiers are available at your Commandant level."
                    : "Available Warzone tiers: " + string.Join(", ", tiers.Select(tier => $"{tier.Id} ({tier.Name})"))
                        + ". Use /warzone tier <Id|name>.");
            }

            if (Operation.Equals("zone", StringComparison.OrdinalIgnoreCase))
            {
                if (Value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    ArenaModule.SetZoneFromCommand(session, 0);
                    return;
                }
                if (!int.TryParse(Value, out int areaId))
                    throw new CommandMessageCallbackException("Usage: /warzone zone <AreaId|auto>. Use /warzone zones to list IDs, or /warzone set <element> [boss|duo|mob].");
                ArenaModule.SetZoneFromCommand(session, areaId);
                return;
            }

            if (Operation.Equals("fluctuation", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(Value, out int fluctuationLevel))
                    throw new CommandMessageCallbackException(
                        $"Usage: /warzone fluctuation <level> (0 = off; 1-{ArenaModule.MaxFluctuationLevel}; {ArenaModule.BaselineFluctuationLevel} = the tier's base enemy level).");
                ArenaModule.SetFluctuationFromCommand(session, fluctuationLevel);
                throw new CommandMessageCallbackException(fluctuationLevel == 0
                    ? "Warzone fluctuation turned off (tier-scaled enemy levels)."
                    : $"Warzone fluctuation set to {fluctuationLevel}: enemy levels x{fluctuationLevel / (double)ArenaModule.BaselineFluctuationLevel:0.###} from the next fight.");
            }

            if (Operation.Equals("tier", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(Value))
                    throw new CommandMessageCallbackException("Usage: /warzone tier <Id|name>");
                ArenaModule.SetTierFromCommand(session, Value);
                return;
            }

            if (!string.IsNullOrEmpty(Value))
                throw new CommandMessageCallbackException("Usage: /warzone tiers, /warzone tier <Id|name>, /warzone zones [filter], /warzone set <element> [boss|duo|mob], /warzone zone <AreaId|auto>, or /warzone fluctuation <level>");
            ArenaModule.SetTierFromCommand(session, Operation);
        }
    }

    // Short alias: /wz set ice == /warzone set ice.
    [CommandName("wz")]
    internal sealed class WzCommand : WarzoneCommand
    {
        public WzCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }
    }
}
