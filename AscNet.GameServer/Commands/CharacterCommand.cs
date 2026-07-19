using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;

namespace AscNet.GameServer.Commands
{
    [CommandName("character")]
    internal class CharacterCommand : Command
    {
        public CharacterCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Command to modify characters.";

        [Argument(0, @"^add$", "The operation selected (add)")]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^[0-9]+$|^all$", "The target character, value is character id or 'all'")]
        string Target { get; set; } = string.Empty;

        public override void Execute()
        {
            int id = Miscs.ParseIntOr(Target);

            switch (Op)
            {
                case "add":
                    if (Target == "all")
                    {
                        HashSet<uint> ownedCharacterIds = session.character.Characters
                            .Select(character => character.Id)
                            .ToHashSet();
                        IEnumerable<Reward> rewards = TableReaderV2.Parse<CharacterTable>()
                            .Where(character => Character.IsOwnableCharacter((uint)character.Id)
                                && !ownedCharacterIds.Contains((uint)character.Id))
                            .Select(character => new Reward { Id = character.Id, Type = RewardType.Character });

                        RewardHandler.GiveRewards(rewards, session);
                    }
                    else
                    {
                        RewardHandler.GiveRewards([ new Reward() { Id = id, Type = RewardType.Character } ], session);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Invalid operation!");
            }
        }
    }
}
