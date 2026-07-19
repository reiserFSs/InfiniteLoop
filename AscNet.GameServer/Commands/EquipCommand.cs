using AscNet.Common.Util;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Table.V2.share.equip;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;

namespace AscNet.GameServer.Commands
{
    [CommandName("equip")]
    internal class EquipCommand : Command
    {

        public EquipCommand(Session session, string[] args, bool validate = true) : base(session, args, validate)
        {
        }

        public override string Help => "Command to interact with your equips";

        [Argument(0, @"^add$|^prune$|^sync$", "The operation selected (add, prune, sync)", ArgumentFlags.IgnoreCase)]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^[0-9]+$|^all$", "The target equip, value is equip id or 'all'", ArgumentFlags.IgnoreCase | ArgumentFlags.Optional)]
        string Target { get; set; } = string.Empty;

        public override void Execute()
        {
            if (Op.Equals("sync", StringComparison.OrdinalIgnoreCase))
            {
                SyncEquipsFromDatabase();
                return;
            }

            NotifyEquipDataList notifyEquipData = new();


            switch (Op)
            {
                case "add":
                    if (Target == "all")
                    {
                        HashSet<uint> ownedTemplateIds = session.character.Equips
                            .Select(equip => equip.TemplateId)
                            .ToHashSet();
                        foreach (EquipTable equip in TableReaderV2.Parse<EquipTable>()
                                     .Where(equip => !ownedTemplateIds.Contains((uint)equip.Id)))
                        {
                            EquipData? newEquip = session.character.AddEquip((uint)equip.Id);
                            if (newEquip is not null)
                            {
                                ownedTemplateIds.Add(newEquip.TemplateId);
                                notifyEquipData.EquipDataList.Add(newEquip);
                            }
                        }
                    }
                    else
                    {
                        var equip = TableReaderV2.Parse<EquipTable>().Find(x => x.Id == Miscs.ParseIntOr(Target)) ?? throw new ServerCodeException("Equip by id not found", 20021001);
                        var newEquip = session.character.AddEquip((uint)equip.Id);
                        if (newEquip is not null)
                            notifyEquipData.EquipDataList.Add(newEquip);
                    }
                    break;
                case "prune":
                    PruneDuplicateWeapons(notifyEquipData);
                    break;
                default:
                    throw new InvalidOperationException("Invalid operation!");
            }

            if (notifyEquipData.EquipDataList.Count > 0 || notifyEquipData.DeletedEquipIdList.Count > 0)
                session.character.SaveChecked();

            session.SendPush(notifyEquipData);
        }

        private void PruneDuplicateWeapons(NotifyEquipDataList notifyEquipData)
        {
            Dictionary<uint, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>()
                .ToDictionary(row => (uint)row.Id);
            foreach (IGrouping<uint, EquipData> duplicates in session.character.Equips
                         .Where(equip => equipRowsById.TryGetValue(equip.TemplateId, out EquipTable? row)
                             && row.Site == 0)
                         .GroupBy(equip => equip.TemplateId))
            {
                if (duplicates.Count() < 2)
                    continue;

                List<EquipData> removable = duplicates
                    .Where(IsUninvestedDuplicateWeapon)
                    .OrderBy(equip => equip.Id)
                    .ToList();
                int pristineToKeep = duplicates.Count() == removable.Count ? 1 : 0;
                foreach (EquipData equip in removable.Skip(pristineToKeep))
                {
                    session.character.Equips.Remove(equip);
                    notifyEquipData.DeletedEquipIdList.Add(equip.Id);
                }
            }
        }

        private bool IsUninvestedDuplicateWeapon(EquipData equip)
        {
            return equip.CharacterId == 0
                && !equip.IsLock
                && !equip.IsRecycle
                && !session.player.IsEquipInTeamPrefab(equip.Id)
                && equip.Level <= 1
                && equip.Exp <= 0
                && equip.Breakthrough <= 0
                && equip.ResonanceInfo.Count == 0
                && equip.UnconfirmedResonanceInfo.Count == 0
                && equip.AwakeSlotList.Count == 0
                && equip.WeaponOverrunData.Level <= 0
                && equip.WeaponOverrunData.ActiveSuits.Count == 0
                && equip.WeaponOverrunData.ChoseSuit <= 0;
        }

        private void SyncEquipsFromDatabase()
        {
            session.character = Character.FromUid(session.player.PlayerData.Id);
            AccountModule.SendLoginState(session);
        }
    }
}
