using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class EquipUpdateLockRequest
    {
        public int EquipId;
        public bool IsLock;
    }

    [MessagePackObject(true)]
    public class EquipUpdateLockResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipBreakthroughRequest
    {
        public int EquipId;
    }

    [MessagePackObject(true)]
    public class EquipBreakthroughResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipResonanceRequest
    {
        public int EquipId;
        public int Slot;
        public int? UseItemId;
        public int? UseEquipId;
        public int? SelectSkillId;
        public int? CharacterId;
        public EquipResonanceType? SelectType;
    }

    [MessagePackObject(true)]
    public class EquipResonanceResponse
    {
        public int Code;
        public ResonanceInfo ResonanceData;
    }

    [MessagePackObject(true)]
    public class EquipPutOnRequest
    {
        public int CharacterId;
        public int EquipId;
        public int Site;
    }

    [MessagePackObject(true)]
    public class EquipPutOnResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipTakeOffRequest
    {
        public List<int> EquipIds;
    }

    [MessagePackObject(true)]
    public class EquipTakeOffResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipLevelUpRequest
    {
        public int EquipId;
        public Dictionary<int, int>? UseItems;
        public List<int>? UseEquipIdList;
    }

    [MessagePackObject(true)]
    public class EquipLevelUpResponse
    {
        public int Code;
        public int Level;
        public int Exp;
    }

    [MessagePackObject(true)]
    public class EquipFeedOperationInfo
    {
        public List<int>? UseEquipIdList;
        public List<int>? UseItemIdList;
        public int OperationType;
        public List<int>? UseItemCountList;
    }

    [MessagePackObject(true)]
    public class EquipOneKeyFeedRequest
    {
        public int TargetBreakthrough;
        public int EquipId;
        public List<EquipFeedOperationInfo> OperationInfos = new();
        public int TargetLevel;
    }

    [MessagePackObject(true)]
    public class EquipOneKeyFeedResponse
    {
        public int Code;
        public int Breakthrough;
        public int Level;
        public int Exp;
        public int SuccessTimes;
    }

    [MessagePackObject(true)]
    public class EquipDecomposeRequest
    {
        public List<int> EquipIds;
    }

    [MessagePackObject(true)]
    public class EquipDecomposeResponse
    {
        public int Code;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class EquipModule
    {
        private const int EquipFeedOperationTypeLevelUp = 1;
        private const int EquipFeedOperationTypeBreakthrough = 2;

        [RequestPacketHandler("EquipLevelUpRequest")]
        public static void EquipLevelUpRequestHandler(Session session, Packet.Request packet)
        {
            EquipLevelUpRequest request = packet.Deserialize<EquipLevelUpRequest>();

            NotifyItemDataList notifyItemData = new();
            int totalExp = 0;
            int totalCost = 0;
            foreach (var item in request.UseItems ?? [])
            {
                ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().FirstOrDefault(x => x.Id == item.Key);
                if (itemTable is not null)
                {
                    var upgradeInfo = itemTable.GetEquipUpgradeInfo() * item.Value;
                    totalExp += upgradeInfo.Exp;
                    totalCost += upgradeInfo.Cost;
                    notifyItemData.ItemDataList.Add(session.inventory.Do(item.Key, item.Value * -1));
                }
            }

            // TODO: Handle equip enchantment with equip cost
            /*foreach (var costEquipId in request.UseEquipIdList ?? [])
            {

            }*/

            notifyItemData.ItemDataList.Add(session.inventory.Do(Inventory.Coin, totalCost * -1));
            session.SendPush(notifyItemData);

            EquipLevelUpResponse rsp = new()
            {
                Code = 0
            };

            var upEquip = session.character.AddEquipExp(request.EquipId, totalExp);
            if (upEquip != null)
            {
                rsp.Level = upEquip.Level;
                rsp.Exp = upEquip.Exp;

                NotifyEquipDataList notifyEquipDataList = new();
                notifyEquipDataList.EquipDataList.Add(upEquip);
                session.SendPush(notifyEquipDataList);
            }

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("EquipOneKeyFeedRequest")]
        public static void EquipOneKeyFeedRequestHandler(Session session, Packet.Request packet)
        {
            EquipOneKeyFeedRequest request = packet.Deserialize<EquipOneKeyFeedRequest>();
            EquipOneKeyFeedResponse response = new()
            {
                Code = 0,
                SuccessTimes = request.OperationInfos?.Count ?? 0
            };

            EquipData? targetEquip = session.character.Equips.Find(x => x.Id == request.EquipId);
            if (targetEquip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                response.Code = 20021012;
                session.SendResponse(response, packet.Id);
                return;
            }

            List<ItemTable> itemTables = TableReaderV2.Parse<ItemTable>();
            List<EquipBreakThroughTable> equipBreakThroughTables = TableReaderV2.Parse<EquipBreakThroughTable>();
            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            EquipTable? targetEquipTable = equipTables.FirstOrDefault(x => x.Id == targetEquip.TemplateId);
            if (targetEquipTable is null)
            {
                response.Code = 20021012;
                session.SendResponse(response, packet.Id);
                return;
            }

            Dictionary<int, int> itemDeltas = new();
            NotifyEquipDataList notifyEquipDataList = new();

            foreach (EquipFeedOperationInfo operationInfo in request.OperationInfos ?? [])
            {
                switch (operationInfo.OperationType)
                {
                    case EquipFeedOperationTypeLevelUp:
                    {
                        int targetLevel = GetOperationTargetLevel(targetEquip, request.TargetBreakthrough, request.TargetLevel, equipBreakThroughTables);
                        if (ShouldUseCogOnlyEnhancement(targetEquipTable) && !HasFeedMaterials(operationInfo))
                        {
                            int requiredExp = session.character.GetEquipExpRequiredToReach(request.EquipId, targetLevel);
                            int appliedExp = session.character.AddEquipExpUpTo(request.EquipId, requiredExp, targetLevel);
                            AddItemDelta(itemDeltas, Inventory.Coin, appliedExp * -10);
                            break;
                        }

                        ConsumeFeedItems(session, itemTables, request.EquipId, operationInfo, itemDeltas);
                        ConsumeFeedEquips(session, targetEquip, targetEquipTable, equipTables, equipBreakThroughTables, operationInfo, itemDeltas, notifyEquipDataList);
                        break;
                    }
                    case EquipFeedOperationTypeBreakthrough:
                    {
                        ApplyEquipBreakthrough(targetEquip, equipBreakThroughTables, itemDeltas);
                        break;
                    }
                }
            }

            response.Breakthrough = targetEquip.Breakthrough;
            response.Level = targetEquip.Level;
            response.Exp = targetEquip.Exp;

            NotifyArchiveEquip notifyArchiveEquip = new();
            notifyArchiveEquip.Equips.Add(new NotifyArchiveEquip.NotifyArchiveEquipEquip()
            {
                Id = targetEquip.TemplateId,
                Level = targetEquip.Level,
                Breakthrough = targetEquip.Breakthrough,
                ResonanceCount = targetEquip.ResonanceInfo?.Count ?? 0
            });
            session.SendPush(notifyArchiveEquip);

            NotifyItemDataList notifyItemDataList = new();
            ApplyItemDeltas(session, itemDeltas, notifyItemDataList);
            if (notifyItemDataList.ItemDataList.Count > 0)
                session.SendPush(notifyItemDataList);

            if (notifyEquipDataList.DeletedEquipIdList.Count > 0 || notifyEquipDataList.EquipDataList.Count > 0)
                session.SendPush(notifyEquipDataList);

            session.character.Save();
            session.inventory.Save();

            session.SendResponse(response, packet.Id);
        }

        private static int ConsumeFeedItems(Session session, List<ItemTable> itemTables, int targetEquipId, EquipFeedOperationInfo operationInfo, Dictionary<int, int> itemDeltas)
        {
            if (operationInfo.UseItemIdList is null || operationInfo.UseItemCountList is null)
                return 0;

            int totalFeedExp = 0;
            for (int i = 0; i < Math.Min(operationInfo.UseItemIdList.Count, operationInfo.UseItemCountList.Count); i++)
            {
                int itemId = operationInfo.UseItemIdList[i];
                int requestedCount = operationInfo.UseItemCountList[i];
                if (requestedCount <= 0)
                    continue;

                ItemTable? itemTable = itemTables.FirstOrDefault(x => x.Id == itemId);
                if (itemTable is null)
                    continue;

                var perItemUpgradeInfo = itemTable.GetEquipUpgradeInfo();
                if (perItemUpgradeInfo.Exp <= 0)
                    continue;

                var upgradeInfo = perItemUpgradeInfo * requestedCount;
                session.character.AddEquipExp(targetEquipId, upgradeInfo.Exp);

                totalFeedExp += upgradeInfo.Exp;
                AddItemDelta(itemDeltas, itemId, requestedCount * -1);
                AddItemDelta(itemDeltas, Inventory.Coin, upgradeInfo.Cost * -1);
            }

            return totalFeedExp;
        }

        private static int ConsumeFeedEquips(Session session, EquipData targetEquip, EquipTable? targetEquipTable, List<EquipTable> equipTables, List<EquipBreakThroughTable> equipBreakThroughTables, EquipFeedOperationInfo operationInfo, Dictionary<int, int> itemDeltas, NotifyEquipDataList notifyEquipDataList)
        {
            if (operationInfo.UseEquipIdList is null)
                return 0;

            int totalFeedExp = 0;
            foreach (int equipId in operationInfo.UseEquipIdList)
            {
                if (equipId == targetEquip.Id || notifyEquipDataList.DeletedEquipIdList.Contains((uint)equipId))
                    continue;

                EquipData? feedEquip = session.character.Equips.Find(x => x.Id == equipId);
                if (feedEquip is null || feedEquip.IsLock || feedEquip.CharacterId != 0)
                    continue;

                EquipTable? feedEquipTable = equipTables.FirstOrDefault(x => x.Id == feedEquip.TemplateId);
                if (!CanFeedEquipIntoTarget(targetEquipTable, feedEquipTable))
                    continue;

                int feedExp = GetEquipFeedExp(feedEquip, equipBreakThroughTables);
                if (feedExp <= 0)
                    continue;

                if (!session.character.Equips.Remove(feedEquip))
                    continue;

                session.character.AddEquipExp((int)targetEquip.Id, feedExp);
                totalFeedExp += feedExp;
                AddItemDelta(itemDeltas, Inventory.Coin, feedExp * -10);
                notifyEquipDataList.DeletedEquipIdList.Add(feedEquip.Id);
            }

            return totalFeedExp;
        }

        private static int GetOperationTargetLevel(EquipData targetEquip, int requestedBreakthrough, int requestedLevel, List<EquipBreakThroughTable> equipBreakThroughTables)
        {
            if (targetEquip.Breakthrough == requestedBreakthrough)
                return Math.Max(1, requestedLevel);

            EquipBreakThroughTable? currentBreakThrough = equipBreakThroughTables.FirstOrDefault(x => x.EquipId == targetEquip.TemplateId && x.Times == targetEquip.Breakthrough);
            return currentBreakThrough?.LevelLimit ?? targetEquip.Level;
        }

        private static bool ShouldUseCogOnlyEnhancement(EquipTable? targetEquipTable)
        {
            return targetEquipTable is not null
                && targetEquipTable.Type == 1
                && targetEquipTable.Quality <= 3;
        }

        private static bool HasFeedMaterials(EquipFeedOperationInfo operationInfo)
        {
            return operationInfo.UseEquipIdList?.Count > 0
                || (operationInfo.UseItemIdList?.Count > 0 && operationInfo.UseItemCountList?.Any(count => count > 0) == true);
        }

        private static bool CanFeedEquipIntoTarget(EquipTable? targetEquipTable, EquipTable? feedEquipTable)
        {
            if (targetEquipTable is null || feedEquipTable is null)
                return false;

            if (feedEquipTable.Type == 99)
                return feedEquipTable.Site == targetEquipTable.Site;

            if (targetEquipTable.Site == 0)
                return feedEquipTable.Site == 0 && feedEquipTable.Type != 99;

            return targetEquipTable.Type == 0
                && feedEquipTable.Type == 0
                && feedEquipTable.Site == targetEquipTable.Site;
        }

        private static bool IsSameEquipSlot(EquipTable? equippedTable, EquipTable targetTable)
        {
            if (equippedTable is null)
                return false;

            if (targetTable.Type == 1)
                return equippedTable.Type == 1;

            return equippedTable.Type == targetTable.Type
                && equippedTable.Site == targetTable.Site;
        }

        private static int GetEquipFeedExp(EquipData equip, List<EquipBreakThroughTable> equipBreakThroughTables)
        {
            EquipBreakThroughTable? feedEquipBreakThrough = equipBreakThroughTables.FirstOrDefault(x => x.EquipId == equip.TemplateId && x.Times == equip.Breakthrough);
            return (feedEquipBreakThrough?.Exp ?? 0) + Math.Max(0, equip.Exp);
        }

        private static void ApplyEquipBreakthrough(EquipData equip, List<EquipBreakThroughTable> equipBreakThroughTables, Dictionary<int, int> itemDeltas)
        {
            EquipBreakThroughTable? equipBreakThrough = equipBreakThroughTables.FirstOrDefault(x => x.EquipId == equip.TemplateId && x.Times == equip.Breakthrough);
            if (equipBreakThrough is null || !equipBreakThroughTables.Any(x => x.EquipId == equip.TemplateId && x.Times == equip.Breakthrough + 1))
                return;

            for (int i = 0; i < Math.Min(equipBreakThrough.ItemId.Count, equipBreakThrough.ItemCount.Count); i++)
            {
                AddItemDelta(itemDeltas, equipBreakThrough.ItemId[i], equipBreakThrough.ItemCount[i] * -1);
            }

            if (equipBreakThrough.UseItemId is not null && equipBreakThrough.UseMoney is not null && equipBreakThrough.UseMoney > 0)
                AddItemDelta(itemDeltas, equipBreakThrough.UseItemId.Value, equipBreakThrough.UseMoney.Value * -1);

            equip.Breakthrough++;
            equip.Level = 1;
            equip.Exp = 0;
        }

        private static void ApplyItemDeltas(Session session, Dictionary<int, int> itemDeltas, NotifyItemDataList notifyItemDataList)
        {
            foreach ((int itemId, int delta) in itemDeltas)
            {
                if (delta == 0)
                    continue;

                notifyItemDataList.ItemDataList.Add(session.inventory.Do(itemId, delta));
            }
        }

        private static void AddItemDelta(Dictionary<int, int> itemDeltas, int itemId, int delta)
        {
            if (delta == 0)
                return;

            itemDeltas[itemId] = itemDeltas.GetValueOrDefault(itemId) + delta;
        }

        [RequestPacketHandler("EquipBreakthroughRequest")]
        public static void EquipBreakthroughRequestHandler(Session session, Packet.Request packet)
        {
            EquipBreakthroughRequest request = packet.Deserialize<EquipBreakthroughRequest>();
            var response = new EquipBreakthroughResponse();
            var equip = session.character.Equips.Find(x => x.Id == request.EquipId);
            if (equip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                response.Code = 20021012;
            }
            else
            {
                EquipBreakThroughTable? equipBreakThrough = TableReaderV2.Parse<EquipBreakThroughTable>().Find(x => x.EquipId == equip.TemplateId && equip.Breakthrough == x.Times);
                if (equipBreakThrough is not null && TableReaderV2.Parse<EquipBreakThroughTable>().Any(x => x.EquipId == equip.TemplateId && equip.Breakthrough + 1 == x.Times))
                {
                    NotifyItemDataList notifyItemData = new();

                    for (int i = 0; i < Math.Min(equipBreakThrough.ItemId.Count, equipBreakThrough.ItemCount.Count); i++)
                    {
                        notifyItemData.ItemDataList.Add(session.inventory.Do(equipBreakThrough.ItemId[i], equipBreakThrough.ItemCount[i] * -1));
                    }
                    if (equipBreakThrough.UseMoney is not null && equipBreakThrough.UseMoney > 0)
                        notifyItemData.ItemDataList.Add(session.inventory.Do(equipBreakThrough.UseItemId ?? 1, (equipBreakThrough.UseMoney ?? 0) * -1));

                    session.SendPush(notifyItemData);

                    equip.Breakthrough += 1;
                    equip.Level = 1;
                    equip.Exp = 0;
                }
                else if (equipBreakThrough is not null)
                {
                    // EquipManagerBreakthroughMaxBreakthrough
                    response.Code = 20021010;
                }
                else
                {
                    // EquipBreakthroughTemplateNotFound
                    response.Code = 20021002;
                }
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("EquipUpdateLockRequest")]
        public static void EquipUpdateLockRequestHandler(Session session, Packet.Request packet)
        {
            EquipUpdateLockRequest request = packet.Deserialize<EquipUpdateLockRequest>();
            var response = new EquipUpdateLockResponse();
            var equip = session.character.Equips.Find(x => x.Id == request.EquipId);
            if (equip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                response.Code = 20021012;
            }
            else
            {
                equip.IsLock = request.IsLock;
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("EquipPutOnRequest")]
        public static void EquipPutOnRequestHandler(Session session, Packet.Request packet)
        {
            EquipPutOnRequest request = packet.Deserialize<EquipPutOnRequest>();

            EquipData? toEquip = session.character.Equips.Find(x => x.Id == request.EquipId);
            if (toEquip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                session.SendResponse(new EquipPutOnResponse() { Code = 20021012 }, packet.Id);
                return;
            }

            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            EquipTable? toEquipTable = equipTables.FirstOrDefault(x => x.Id == toEquip.TemplateId);
            if (toEquipTable is null)
            {
                // EquipBreakthroughTemplateNotFound
                session.SendResponse(new EquipPutOnResponse() { Code = 20021002 }, packet.Id);
                return;
            }

            List<EquipData> previousEquips = session.character.Equips
                .Where(equip => equip.Id != toEquip.Id && equip.CharacterId == request.CharacterId)
                .Where(equip =>
                {
                    EquipTable? equippedTable = equipTables.FirstOrDefault(table => table.Id == equip.TemplateId);
                    return IsSameEquipSlot(equippedTable, toEquipTable);
                })
                .ToList();

            foreach (EquipData previousEquip in previousEquips)
            {
                previousEquip.CharacterId = 0;
            }

            toEquip.CharacterId = request.CharacterId;

            if (previousEquips.Count > 0)
            {
                NotifyEquipDataList notifyEquipData = new();
                notifyEquipData.EquipDataList.AddRange(previousEquips);
                session.SendPush(notifyEquipData);
            }

            session.SendResponse(new EquipPutOnResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipTakeOffRequest")]
        public static void EquipTakeOffRequestHandler(Session session, Packet.Request packet)
        {
            EquipTakeOffRequest request = packet.Deserialize<EquipTakeOffRequest>();

            foreach (var equipId in request.EquipIds)
            {
                var equip = session.character.Equips.Find(x => x.Id == equipId);
                if (equip is not null)
                    equip.CharacterId = 0;
            }

            session.SendResponse(new EquipTakeOffResponse(), packet.Id);
        }

        // TODO: Swapping equip resonance is broken, this is only partially implemented!
        [RequestPacketHandler("EquipResonanceRequest")]
        public static void EquipResonanceRequestHandler(Session session, Packet.Request packet)
        {
            EquipResonanceRequest request = packet.Deserialize<EquipResonanceRequest>();

            var equip = session.character.Equips.Find(x => x.Id == request.EquipId);

            if (equip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                session.SendResponse(new EquipResonanceResponse() { Code = 20021012 }, packet.Id);
                return;
            }

            #region Pools
            EquipResonanceTable? equipResonance = TableReaderV2.Parse<EquipResonanceTable>().Find(x => x.Id == equip.TemplateId);
            List<ResonanceInfo> resonancePool = new();
            foreach (var attribPoolId in equipResonance?.AttribPoolId ?? [])
            {
                var attribPool = TableReaderV2.Parse<AttribPoolTable>().Where(x => x.PoolId == attribPoolId);
                foreach (var attrib in attribPool)
                {
                    resonancePool.Add(new()
                    {
                        Slot = request.Slot,
                        Type = EquipResonanceType.Attrib,
                        TemplateId = attrib.Id
                    });
                }
            }
            foreach (var characterSkillPoolId in equipResonance?.CharacterSkillPoolId ?? [])
            {
                throw new NotImplementedException();
            }
            foreach (var weaponSkillPoolId in equipResonance?.WeaponSkillPoolId ?? [])
            {
                throw new NotImplementedException();
            }
            #endregion

            if (request.UseItemId is not null && request.UseItemId > 0)
            {
                EquipResonanceUseItemTable? resonanceUseItem = TableReaderV2.Parse<EquipResonanceUseItemTable>().Find(x => x.Id == equip.TemplateId);
                if (resonanceUseItem is not null)
                {
                    NotifyItemDataList notifyItemData = new();
                    for (int i = 0; i < Math.Min(resonanceUseItem.ItemId.Count, resonanceUseItem.ItemCount.Count); i++)
                    {
                        notifyItemData.ItemDataList.Add(session.inventory.Do(resonanceUseItem.ItemId[i], resonanceUseItem.ItemCount[i] * -1));
                    }

                    session.SendPush(notifyItemData);
                }
                else
                {
                    session.log.Error($"EquipResonanceUseItem for template {equip.TemplateId} not found!");
                    // EquipResonanceUseItemTemplateNotFound
                    session.SendResponse(new EquipResonanceResponse() { Code = 20021038 }, packet.Id);
                    return;
                }
            }
            else if (request.UseEquipId is not null && request.UseEquipId > 0)
            {
                throw new NotImplementedException();
            }

            ResonanceInfo resonance = resonancePool[Random.Shared.Next(resonancePool.Count)];
            equip.ResonanceInfo.Add(resonance);

            session.SendResponse(new EquipResonanceResponse() { ResonanceData = resonance }, packet.Id);
        }

        // TODO: Equipment scrapping
        [RequestPacketHandler("EquipDecomposeRequest")]
        public static void EquipDecomposeRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new EquipDecomposeResponse() { Code = 1 }, packet.Id);
        }
    }
}