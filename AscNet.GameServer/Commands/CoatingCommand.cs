using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.item;

namespace AscNet.GameServer.Commands
{
    [CommandName("coating")]
    internal class CoatingCommand : Command
    {
        public CoatingCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Unlock character coatings for one character, or all owned character coatings and weapon coatings with 'all'.";

        [Argument(0, @"^unlock$", "The operation selected (unlock)")]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^[0-9]+$|^all$", "Character id, or 'all' for all owned character coatings and catalog weapon coatings")]
        string Target { get; set; } = string.Empty;

        public override void Execute()
        {
            int characterId = Miscs.ParseIntOr(Target);

            switch (Op)
            {
                case "unlock":
                    List<FashionList> changedFashions = new();
                    bool changed = false;
                    HashSet<int>? ownedCharacterIds = Target == "all"
                        ? session.character.Characters.Select(character => (int)character.Id).ToHashSet()
                        : null;

                    IEnumerable<int> fashionIds = Target == "all"
                        ? TableReaderV2.Parse<FashionTable>()
                            .Where(fashion => ownedCharacterIds!.Contains(fashion.CharacterId))
                            .Select(fashion => fashion.Id)
                            .Distinct()
                        : TableReaderV2.Parse<FashionTable>()
                            .Where(fashion => fashion.CharacterId == characterId)
                            .Select(fashion => fashion.Id)
                            .Distinct();

                    foreach (int fashionId in fashionIds)
                        changed |= RewardHandler.UnlockFashionReward(fashionId, session, changedFashions);

                    List<WeaponFashionData> changedWeaponFashions = new();
                    if (Target == "all")
                    {
                        IEnumerable<int> weaponFashionIds = TableReaderV2.Parse<ItemTable>()
                            .Where(item => item.ItemType == (int)ItemType.WeaponFashion)
                            .Select(item => item.SubTypeParams.FirstOrDefault())
                            .Where(id => id > 0)
                            .Distinct();

                        foreach (int weaponFashionId in weaponFashionIds)
                            changed |= RewardHandler.UnlockWeaponFashionReward(
                                weaponFashionId,
                                session,
                                changedWeaponFashions
                            );
                    }

                    if (changedFashions.Count > 0)
                        session.SendPush(new FashionSyncNotify { FashionList = changedFashions });

                    if (changedWeaponFashions.Count > 0)
                    {
                        session.SendPush(new NotifyWeaponFashionInfo
                        {
                            WeaponFashionDataList = changedWeaponFashions
                        });
                    }

                    if (changed)
                        session.character.Save();
                    break;
                default:
                    throw new InvalidOperationException("Invalid operation!");
            }
        }
    }
}
