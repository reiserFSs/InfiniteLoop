using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.reward;
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
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class EquipModule
    {
        private const int EquipFeedOperationTypeLevelUp = 1;
        private const int EquipFeedOperationTypeBreakthrough = 2;
        private const int MaxDecomposedEquipCount = 100;
        private const int MaxReturnedEquipCount = 10_000;

        [RequestPacketHandler("EquipLevelUpRequest")]
        public static void EquipLevelUpRequestHandler(Session session, Packet.Request packet)
        {
            EquipLevelUpRequest request = packet.Deserialize<EquipLevelUpRequest>();
            EquipData? targetEquip = session.character.Equips.Find(equip => equip.Id == request.EquipId);
            EquipBreakThroughTable? progression = targetEquip is null
                ? null
                : Character.ResolveEquipBreakThrough(targetEquip.TemplateId, targetEquip.Breakthrough);
            if (targetEquip is null || progression is null)
            {
                session.SendResponse(new EquipLevelUpResponse { Code = 20021012 }, packet.Id);
                return;
            }

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
                session.character.Save();
                session.inventory.Save();

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
            EquipTable? targetEquipTable = Character.ResolveEquipTemplate(targetEquip.TemplateId);
            if (targetEquipTable is null)
            {
                response.Code = 20021012;
                session.SendResponse(response, packet.Id);
                return;
            }

            Dictionary<int, int> itemDeltas = new();
            NotifyEquipDataList notifyEquipDataList = new();

            ApplyFeedOperations(
                session,
                request,
                targetEquip,
                targetEquipTable,
                itemTables,
                equipBreakThroughTables,
                equipTables,
                itemDeltas,
                notifyEquipDataList);

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

        private static void ApplyFeedOperations(
            Session session,
            EquipOneKeyFeedRequest request,
            EquipData targetEquip,
            EquipTable targetEquipTable,
            List<ItemTable> itemTables,
            List<EquipBreakThroughTable> equipBreakThroughTables,
            List<EquipTable> equipTables,
            Dictionary<int, int> itemDeltas,
            NotifyEquipDataList notifyEquipDataList)
        {
            foreach (EquipFeedOperationInfo operationInfo in request.OperationInfos ?? [])
            {
                switch (operationInfo.OperationType)
                {
                    case EquipFeedOperationTypeLevelUp:
                    {
                        int targetLevel = GetOperationTargetLevel(targetEquip, request.TargetBreakthrough, request.TargetLevel, equipBreakThroughTables);

                        ConsumeFeedItems(session, itemTables, request.EquipId, targetLevel, operationInfo, itemDeltas);
                        ConsumeFeedEquips(session, targetEquip, targetEquipTable, equipTables, equipBreakThroughTables, targetLevel, operationInfo, itemDeltas, notifyEquipDataList);
                        break;
                    }
                    case EquipFeedOperationTypeBreakthrough:
                    {
                        ApplyEquipBreakthrough(targetEquip, equipBreakThroughTables, itemDeltas);
                        break;
                    }
                }
            }
        }

        private static int ConsumeFeedItems(Session session, List<ItemTable> itemTables, int targetEquipId, int targetLevel, EquipFeedOperationInfo operationInfo, Dictionary<int, int> itemDeltas)
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

        private static int ConsumeFeedEquips(Session session, EquipData targetEquip, EquipTable? targetEquipTable, List<EquipTable> equipTables, List<EquipBreakThroughTable> equipBreakThroughTables, int targetLevel, EquipFeedOperationInfo operationInfo, Dictionary<int, int> itemDeltas, NotifyEquipDataList notifyEquipDataList)
        {
            if (operationInfo.UseEquipIdList is null)
                return 0;

            int totalFeedExp = 0;
            foreach (int equipId in operationInfo.UseEquipIdList)
            {
                if (equipId == targetEquip.Id || notifyEquipDataList.DeletedEquipIdList.Contains((uint)equipId))
                    continue;
                if (Character.ResolveEquipBreakThrough(targetEquip.TemplateId, targetEquip.Breakthrough) is null)
                    break;

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
            EquipBreakThroughTable? currentBreakThrough = Character.ResolveEquipBreakThrough(
                targetEquip.TemplateId,
                targetEquip.Breakthrough);
            if (currentBreakThrough is null)
                return targetEquip.Level;

            int targetLevel = targetEquip.Breakthrough == requestedBreakthrough
                ? Math.Max(1, requestedLevel)
                : currentBreakThrough.LevelLimit;
            return Math.Clamp(targetLevel, targetEquip.Level, currentBreakThrough.LevelLimit);
        }



        private static bool CanFeedEquipIntoTarget(EquipTable? targetEquipTable, EquipTable? feedEquipTable)
        {
            if (targetEquipTable is null || feedEquipTable is null)
                return false;

            if (targetEquipTable.Type == 1)
                return feedEquipTable.Type == 1
                    || (feedEquipTable.Type == 99 && feedEquipTable.Site == 0);

            if (targetEquipTable.Type == 0)
                return feedEquipTable.Type == 0
                    || (feedEquipTable.Type == 99 && feedEquipTable.Site != 0);

            return false;
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
            EquipBreakThroughTable? feedEquipBreakThrough = Character.ResolveEquipBreakThrough(
                equip.TemplateId,
                equip.Breakthrough);
            return (feedEquipBreakThrough?.Exp ?? 0) + Math.Max(0, equip.Exp);
        }

        private static void ApplyEquipBreakthrough(EquipData equip, List<EquipBreakThroughTable> equipBreakThroughTables, Dictionary<int, int> itemDeltas)
        {
            EquipBreakThroughTable? equipBreakThrough = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough);
            if (equipBreakThrough is null
                || equip.Level < equipBreakThrough.LevelLimit
                || Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough + 1) is null)
                return;

            for (int i = 0; i < Math.Min(equipBreakThrough.ItemId.Count, equipBreakThrough.ItemCount.Count); i++)
            {
                AddItemDelta(itemDeltas, equipBreakThrough.ItemId[i], equipBreakThrough.ItemCount[i] * -1);
            }

            if (equipBreakThrough.UseItemId != 0 && equipBreakThrough.UseMoney > 0)
                AddItemDelta(itemDeltas, equipBreakThrough.UseItemId, equipBreakThrough.UseMoney * -1);

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
                EquipBreakThroughTable? equipBreakThrough = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough);
                if (equipBreakThrough is not null
                    && Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough + 1) is not null)
                {
                    if (equip.Level < equipBreakThrough.LevelLimit)
                    {
                        response.Code = 20021011;
                        session.SendResponse(response, packet.Id);
                        return;
                    }

                    NotifyItemDataList notifyItemData = new();

                    for (int i = 0; i < Math.Min(equipBreakThrough.ItemId.Count, equipBreakThrough.ItemCount.Count); i++)
                    {
                        notifyItemData.ItemDataList.Add(session.inventory.Do(equipBreakThrough.ItemId[i], equipBreakThrough.ItemCount[i] * -1));
                    }
                    if (equipBreakThrough.UseItemId != 0 && equipBreakThrough.UseMoney > 0)
                        notifyItemData.ItemDataList.Add(session.inventory.Do(equipBreakThrough.UseItemId, equipBreakThrough.UseMoney * -1));

                    session.SendPush(notifyItemData);

                    equip.Breakthrough += 1;
                    equip.Level = 1;
                    equip.Exp = 0;
                    NotifyEquipDataList notifyEquipDataList = new();
                    notifyEquipDataList.EquipDataList.Add(equip);
                    session.SendPush(notifyEquipDataList);
                    session.character.Save();
                    session.inventory.Save();
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

        [RequestPacketHandler("EquipDecomposeRequest")]
        public static void EquipDecomposeRequestHandler(Session session, Packet.Request packet)
        {
            EquipDecomposeRequest request = packet.Deserialize<EquipDecomposeRequest>();
            if (!TryBuildEquipDecomposeRewards(
                    session,
                    request.EquipIds,
                    out List<EquipData> sourceEquips,
                    out List<EquipDecomposeReward> rewardSpecs))
            {
                session.SendResponse(new EquipDecomposeResponse { Code = 1 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = new();
            NotifyEquipDataList notifyEquipData = new();

            // Add returned equipment before deleting sources so Character.AddEquip's
            // max-based allocator cannot reuse a deleted source UID.
            foreach (IGrouping<int, EquipDecomposeReward> itemRewards in rewardSpecs
                         .Where(reward => reward.Type == RewardType.Item)
                         .GroupBy(reward => reward.TemplateId))
            {
                notifyItemData.ItemDataList.Add(
                    session.inventory.Do(itemRewards.Key, itemRewards.Sum(reward => reward.Count)));
            }

            foreach (EquipDecomposeReward reward in rewardSpecs.Where(reward => reward.Type == RewardType.Equip))
            {
                for (int i = 0; i < reward.Count; i++)
                {
                    EquipData? returnedEquip = session.character.AddEquip(
                        (uint)reward.TemplateId,
                        level: Math.Max(1, reward.Level));
                    if (returnedEquip is null)
                        throw new InvalidDataException($"Unable to grant decomposed equipment template {reward.TemplateId}.");

                    notifyEquipData.EquipDataList.Add(returnedEquip);
                }
            }

            foreach (EquipData sourceEquip in sourceEquips)
            {
                if (!session.character.Equips.Remove(sourceEquip))
                    throw new InvalidDataException($"Unable to remove decomposed equipment UID {sourceEquip.Id}.");

                notifyEquipData.DeletedEquipIdList.Add(sourceEquip.Id);
            }

            if (notifyItemData.ItemDataList.Count > 0)
                session.SendPush(notifyItemData);
            if (notifyEquipData.EquipDataList.Count > 0 || notifyEquipData.DeletedEquipIdList.Count > 0)
                session.SendPush(notifyEquipData);

            session.character.Save();
            session.inventory.Save();
            session.SendResponse(new EquipDecomposeResponse
            {
                Code = 0,
                RewardGoodsList = BuildEquipDecomposeResponseRewards(rewardSpecs)
            }, packet.Id);
        }

        private sealed class EquipDecomposeReward
        {
            public RewardType Type { get; init; }
            public int TemplateId { get; init; }
            public int Count { get; set; }
            public int Level { get; init; }
            public int Id { get; init; }
        }


        private static List<RewardGoods> BuildEquipDecomposeResponseRewards(
            IEnumerable<EquipDecomposeReward> rewardSpecs)
        {
            return rewardSpecs
                .GroupBy(reward => (reward.Type, reward.TemplateId))
                .OrderBy(group => group.Key.Type)
                .ThenBy(group => group.Key.TemplateId)
                .Select(group => new RewardGoods
                {
                    RewardType = (int)group.Key.Type,
                    TemplateId = group.Key.TemplateId,
                    Count = group.Sum(reward => reward.Count),
                    Level = group.Max(reward => reward.Level),
                    Id = 0
                })
                .ToList();
        }


        private static bool TryBuildEquipDecomposeRewards(
            Session session,
            List<int>? equipIds,
            out List<EquipData> sourceEquips,
            out List<EquipDecomposeReward> rewardSpecs)
        {
            sourceEquips = [];
            rewardSpecs = [];
            if (equipIds is null || equipIds.Count == 0 || equipIds.Count > MaxDecomposedEquipCount)
                return false;

            HashSet<int> requestedIds = [];
            IGrouping<uint, EquipData>[] equipGroups = session.character.Equips
                .GroupBy(equip => equip.Id)
                .ToArray();
            if (equipGroups.Any(group => group.Count() != 1))
                return false;

            Dictionary<uint, EquipData> equipsById = equipGroups
                .ToDictionary(group => group.Key, group => group.Single());
            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            List<EquipBreakThroughTable> breakthroughTables = TableReaderV2.Parse<EquipBreakThroughTable>();
            List<EquipDecomposeTable> decomposeTables = TableReaderV2.Parse<EquipDecomposeTable>();
            EquipDecomposeConfigTable? returnRateConfig = TableReaderV2
                .Parse<EquipDecomposeConfigTable>()
                .FirstOrDefault(config => config.Key == "EquipDecomposeReturnRate");
            if (returnRateConfig is null || returnRateConfig.Value <= 0)
                return false;

            Dictionary<(RewardType Type, int TemplateId, int RewardId), EquipDecomposeReward> rewardByKey = [];
            foreach (int requestedId in equipIds)
            {
                if (requestedId <= 0 || !requestedIds.Add(requestedId))
                    return false;
                if (!equipsById.TryGetValue((uint)requestedId, out EquipData? equip)
                    || equip.IsLock
                    || equip.CharacterId != 0
                    || equip.IsRecycle)
                {
                    return false;
                }

                EquipTable? equipTable = equipTables.FirstOrDefault(table => table.Id == equip.TemplateId);
                if (equipTable is null || !Character.IsOwnableEquipTemplate(equipTable))
                    return false;

                EquipDecomposeTable? decomposeTable = decomposeTables.FirstOrDefault(table =>
                    table.Site == equipTable.Site
                    && table.Star == equipTable.Star
                    && table.Breakthrough == equip.Breakthrough);
                EquipBreakThroughTable? breakthroughTable = breakthroughTables.FirstOrDefault(table =>
                    table.EquipId == equip.TemplateId
                    && table.Times == equip.Breakthrough);
                EquipLevelUpTemplate? levelUpTemplate = breakthroughTable is null
                    ? null
                    : Character.equipLevelUpTemplates.FirstOrDefault(template =>
                        template.TemplateId == breakthroughTable.LevelUpTemplateId
                        && template.Level == equip.Level);
                if (decomposeTable is null
                    || breakthroughTable is null
                    || levelUpTemplate is null
                    || equip.Exp < 0
                    || (levelUpTemplate.Exp > 0 && equip.Exp > levelUpTemplate.Exp)
                    || !decimal.TryParse(
                        decomposeTable.ExpToOneCoin,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out decimal expToOneCoin)
                    || expToOneCoin <= 0)
                {
                    return false;
                }

                decimal totalExp;
                try
                {
                    totalExp = checked((decimal)equip.Exp + levelUpTemplate.AllExp + breakthroughTable.Exp);
                }
                catch (OverflowException)
                {
                    return false;
                }

                decimal coinCountDecimal = totalExp / expToOneCoin;
                if (coinCountDecimal > int.MaxValue)
                    return false;

                int coinCount = FloorToInt(coinCountDecimal);
                if (coinCount > 0)
                    AddEquipDecomposeReward(rewardByKey, RewardType.Item, Inventory.Coin, coinCount, level: 0, rewardId: 0);

                EquipBreakThroughTable? foodBreakthroughTable = breakthroughTables.FirstOrDefault(table =>
                    table.EquipId == decomposeTable.ExpToEquipId
                    && table.Times == 0);
                if (foodBreakthroughTable is null || foodBreakthroughTable.Exp <= 0)
                    return false;

                decimal foodCountDecimal;
                try
                {
                    foodCountDecimal = checked(
                        totalExp * returnRateConfig.Value
                        / (foodBreakthroughTable.Exp * 10_000m));
                }
                catch (OverflowException)
                {
                    return false;
                }

                if (foodCountDecimal > MaxReturnedEquipCount)
                    return false;
                int foodCount = FloorToInt(foodCountDecimal);
                if (foodCount > 0)
                {
                    EquipTable? foodEquipTable = equipTables.FirstOrDefault(table => table.Id == decomposeTable.ExpToEquipId);
                    if (foodEquipTable is null || !Character.IsOwnableEquipTemplate(foodEquipTable))
                        return false;

                    AddEquipDecomposeReward(
                        rewardByKey,
                        RewardType.Equip,
                        decomposeTable.ExpToEquipId,
                        foodCount,
                        level: 1,
                        rewardId: 0);
                }

                foreach (RewardGoodsTable rewardGoods in RewardHandler.GetRewardGoods(decomposeTable.RewardId))
                {
                    RewardType? rewardType = RewardHandler.GetRewardType(rewardGoods);
                    if (rewardType is null
                        || (rewardType != RewardType.Item && rewardType != RewardType.Equip)
                        || rewardGoods.Count <= 0)
                    {
                        return false;
                    }

                    AddEquipDecomposeReward(
                        rewardByKey,
                        rewardType.Value,
                        rewardGoods.TemplateId,
                        rewardGoods.Count,
                        rewardType == RewardType.Equip ? 1 : 0,
                        rewardGoods.Id);
                }

                sourceEquips.Add(equip);
            }

            if (rewardByKey.Count == 0)
                return false;

            Dictionary<int, long> inventoryCounts = session.inventory.Items
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));

            long returnedEquipCount = rewardByKey.Values
                .Where(reward => reward.Type == RewardType.Equip)
                .Sum(reward => (long)reward.Count);
            if (returnedEquipCount > MaxReturnedEquipCount)
                return false;

            Dictionary<int, ItemTable> itemTablesById = TableReaderV2.Parse<ItemTable>()
                .ToDictionary(table => table.Id);
            foreach (EquipDecomposeReward reward in rewardByKey.Values.Where(reward => reward.Type == RewardType.Equip))
            {
                EquipTable? rewardEquipTable = equipTables.FirstOrDefault(table => table.Id == reward.TemplateId);
                if (rewardEquipTable is null
                    || !Character.IsOwnableEquipTemplate(rewardEquipTable)
                    || reward.Level < 1)
                {
                    return false;
                }
            }

            Dictionary<int, long> requestedItemCounts = [];
            foreach (EquipDecomposeReward reward in rewardByKey.Values.Where(reward => reward.Type == RewardType.Item))
            {
                if (!itemTablesById.TryGetValue(reward.TemplateId, out ItemTable? itemTable)
                    || !Inventory.IsValidClientItemId(reward.TemplateId))
                {
                    return false;
                }

                long requestedCount;
                try
                {
                    requestedCount = checked(
                        requestedItemCounts.GetValueOrDefault(reward.TemplateId)
                        + reward.Count);
                }
                catch (OverflowException)
                {
                    return false;
                }

                if (requestedCount > int.MaxValue)
                    return false;
                requestedItemCounts[reward.TemplateId] = requestedCount;
                long finalCount;
                try
                {
                    finalCount = checked(
                        inventoryCounts.GetValueOrDefault(reward.TemplateId)
                        + requestedCount);
                }
                catch (OverflowException)
                {
                    return false;
                }

                if (finalCount > int.MaxValue)
                    return false;
                if (itemTable.MaxCount is int maxCount && finalCount > maxCount)
                    return false;
            }

            rewardSpecs = rewardByKey.Values
                .OrderBy(reward => reward.Type)
                .ThenBy(reward => reward.TemplateId)
                .ThenBy(reward => reward.Id)
                .ToList();
            return true;
        }

        private static void AddEquipDecomposeReward(
            Dictionary<(RewardType Type, int TemplateId, int RewardId), EquipDecomposeReward> rewardByKey,
            RewardType type,
            int templateId,
            int count,
            int level,
            int rewardId)
        {
            (RewardType Type, int TemplateId, int RewardId) key = (type, templateId, rewardId);
            if (rewardByKey.TryGetValue(key, out EquipDecomposeReward? existingReward))
            {
                existingReward.Count = checked(existingReward.Count + count);
                return;
            }

            rewardByKey[key] = new EquipDecomposeReward
            {
                Type = type,
                TemplateId = templateId,
                Count = count,
                Level = level,
                Id = rewardId
            };
        }

        private static int FloorToInt(decimal value)
        {
            if (value <= 0)
                return 0;
            if (value >= int.MaxValue)
                return int.MaxValue;
            return decimal.ToInt32(decimal.Floor(value));
        }
    }
}