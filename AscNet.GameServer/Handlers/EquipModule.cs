using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.equip.equipguide;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.item;
using MessagePack;
using MongoDB.Bson;

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
        public List<int> Slots = new();
        public int UseItemId;
        public int UseEquipId;
        public List<int>? SelectSkillIds;
        public EquipResonanceType? SelectType;
        public int? CharacterId;
    }

    [MessagePackObject(true)]
    public class EquipResonanceResponse
    {
        public int Code;
        public List<ResonanceInfo> ResonanceDatas = new();
    }

    [MessagePackObject(true)]
    public class EquipQuickResonanceChipRequest
    {
        public int Slot;
        public EquipResonanceType SelectType;
        public List<int> EquipIds = new();
        public int SelectSkillId;
        public int UseItemId;
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class EquipQuickResonanceChipResponse
    {
        public int Code;
        public List<int> SuccessEquipIds = new();
    }

    [MessagePackObject(true)]
    public class EquipResonanceConfirmRequest
    {
        public int EquipId;
        public int Slot;
        public bool IsUse;
    }

    [MessagePackObject(true)]
    public class EquipResonanceConfirmResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipAwakeRequest
    {
        public int CostType;
        public int Slot;
        public int EquipId;
    }

    [MessagePackObject(true)]
    public class EquipAwakeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipQuickAwakeInfo
    {
        public int EquipId;
        public List<int> Slots = new();
    }

    [MessagePackObject(true)]
    public class EquipQuickAwakeRequest
    {
        public List<EquipQuickAwakeInfo> EquipQuickAwakeInfos = new();
    }

    [MessagePackObject(true)]
    public class EquipQuickAwakeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipWeaponOverrunLevelUpRequest
    {
        public int EquipId;
    }

    [MessagePackObject(true)]
    public class EquipWeaponOverrunLevelUpResponse
    {
        public int Code;
        public WeaponOverrunData WeaponOverrunData = new();
    }

    [MessagePackObject(true)]
    public class EquipWeaponActiveOverrunSuitRequest
    {
        public int EquipId;
        public int SuitId;
    }

    [MessagePackObject(true)]
    public class EquipWeaponActiveOverrunSuitResponse
    {
        public int Code;
        public WeaponOverrunData WeaponOverrunData = new();
    }

    [MessagePackObject(true)]
    public class EquipWeaponChoseOverrunSuitRequest
    {
        public int EquipId;
        public int SuitId;
    }

    [MessagePackObject(true)]
    public class EquipWeaponChoseOverrunSuitResponse
    {
        public int Code;
        public WeaponOverrunData WeaponOverrunData = new();
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
    public class EquipAddChipGroupRequest
    {
        public string Name = "";
        public List<int> ChipIds = new();
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class EquipAddChipGroupResponse
    {
        public int Code;
        public EquipChipGroupData? ChipGroupData;
    }

    [MessagePackObject(true)]
    public class EquipDeleteChipGroupRequest
    {
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class EquipDeleteChipGroupResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipUpdateChipGroupRequest
    {
        public EquipChipGroupData? GroupData;
    }

    [MessagePackObject(true)]
    public class EquipUpdateChipGroupResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EquipPutOnChipGroupRequest
    {
        public int CharacterId;
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class EquipPutOnChipGroupResponse
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
    public class EquipChipRecycleRequest
    {
        public List<int> ChipIds = new();
    }

    [MessagePackObject(true)]
    public class EquipChipRecycleResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class EquipChipSiteAutoRecycleRequest
    {
        public List<int> StarList = new();
        public int Days;
    }

    [MessagePackObject(true)]
    public class EquipChipSiteAutoRecycleResponse
    {
        public int Code;
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
        public List<RewardGoods> RewardGoodsList = new();
    }

    [MessagePackObject(true)]
    public class EquipGuideSetTargetRequest
    {
        public int TargetId;
        public List<int> PutOnPosList = new();
    }

    [MessagePackObject(true)]
    public class EquipGuideSetTargetResponse
    {
        public int Code;
        public EquipGuideData EquipGuideData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class EquipGuideAddOrClearPutOnPosRequest
    {
        public int CharacterId;
        public List<int> Sites = new();
        public List<int> EquipIds = new();
        public bool IsAdd;
    }

    [MessagePackObject(true)]
    public class EquipGuideAddOrClearPutOnPosResponse
    {
        public int Code;
        public EquipGuideData EquipGuideData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class EquipGuideTargetFinishRequest
    {
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class EquipGuideTargetFinishResponse
    {
        public int Code;
        public EquipGuideData EquipGuideData { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class EquipModule
    {
        private const int EquipFeedOperationTypeLevelUp = 1;
        private const int EquipFeedOperationTypeBreakthrough = 2;
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
            EquipTable? targetEquipTable = Character.ResolveEquipTemplate(targetEquip.TemplateId);
            (int Level, int Exp) beforeEnhancement = (targetEquip.Level, targetEquip.Exp);
            NotifyEquipDataList notifyEquipDataList = new();
            Dictionary<int, int> equipItemDeltas = new();
            if (!TryConsumeValidatedFeedEquips(
                    session,
                    targetEquip,
                    targetEquipTable,
                    request.UseEquipIdList,
                    TableReaderV2.Parse<EquipTable>(),
                    TableReaderV2.Parse<EquipBreakThroughTable>(),
                    equipItemDeltas,
                    notifyEquipDataList,
                    out int equipFeedExp))
            {
                session.SendResponse(new EquipLevelUpResponse { Code = 20021012 }, packet.Id);
                return;
            }

            var balancesBefore = (request.UseItems?.Keys.AsEnumerable() ?? Enumerable.Empty<int>())
                .Append(Inventory.Coin).Distinct()
                .ToDictionary(id => id, id => session.inventory.Items.Find(item => item.Id == id)?.Count ?? 0);
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

            totalExp += equipFeedExp;
            if (equipItemDeltas.TryGetValue(Inventory.Coin, out int equipCoinDelta))
                totalCost -= equipCoinDelta;

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

                notifyEquipDataList.EquipDataList.Add(upEquip);
            }

            if (notifyEquipDataList.DeletedEquipIdList.Count > 0 || notifyEquipDataList.EquipDataList.Count > 0)
                session.SendPush(notifyEquipDataList);
                session.character.Save();
                session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, balancesBefore.Select(balance =>
                (11202, (int?)balance.Key, (int)Math.Clamp(balance.Value -
                    (session.inventory.Items.Find(item => item.Id == balance.Key)?.Count ?? 0), 0, int.MaxValue))));
            if (totalExp > 0 && (targetEquip.Level, targetEquip.Exp) != beforeEnhancement)
                TaskModule.RecordEquipmentProgress(session, 12202, [targetEquip]);

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("EquipGuideSetTargetRequest")]
        public static void EquipGuideSetTargetRequestHandler(Session session, Packet.Request packet)
        {
            EquipGuideSetTargetRequest request = packet.Deserialize<EquipGuideSetTargetRequest>();
            EquipGuideSetTargetResponse response = new()
            {
                Code = 0
            };

            List<int> positions = request.PutOnPosList ?? [];
            bool validPositions = positions.Distinct().Count() == positions.Count
                && positions.All(position => position is >= 0 and <= 6);
            if (!validPositions || request.TargetId == 0 && positions.Count > 0)
            {
                // ponytail: generic nonzero code; retail invalid-code for equip guide is unproven.
                response.Code = 1;
                session.SendResponse(response, packet.Id);
                return;
            }


            int characterId = 0;
            if (request.TargetId != 0)
            {
                EquipTargetTable? target = TableReaderV2.Parse<EquipTargetTable>()
                    .FirstOrDefault(row => row.Id == request.TargetId);
                if (target is null)
                {
                    response.Code = 1;
                    session.SendResponse(response, packet.Id);
                    return;
                }
                characterId = target.CharacterId;
                if (!session.character.Characters.Any(character => character.Id == characterId))
                {
                    response.Code = 20021098; // EquipGuideCharacterIdInvalid
                    session.SendResponse(response, packet.Id);
                    return;
                }
            }

            EquipGuideData original = session.player.EquipGuideData ?? new EquipGuideData();
            Dictionary<int, int>? originalCounters = request.TargetId > 0 && request.TargetId != original.TargetId
                ? new(session.player.MissionProgress.ConditionCounters)
                : null;
            session.player.EquipGuideData = new EquipGuideData
            {
                TargetId = request.TargetId,
                CharacterId = characterId,
                PutOnPosList = request.TargetId != 0 ? new List<int>(positions) : new List<int>(),
                FinishedTargets = new List<int>(original.FinishedTargets ?? [])
            };
            try
            {
                if (originalCounters is not null)
                    TaskModule.AddConditionTypeProgress(session, 12208, 1);
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.EquipGuideData = original;
                if (originalCounters is not null)
                    session.player.MissionProgress.ConditionCounters = originalCounters;
                session.log.Error($"Failed to persist equip guide set target: {exception}");
                response.Code = 1;
            }
            response.EquipGuideData = session.player.EquipGuideData;
            session.SendResponse(response, packet.Id);
            if (response.Code == 0 && originalCounters is not null)
                TaskModule.SendConditionTypeSync(session, 12208);
        }

        [RequestPacketHandler("EquipGuideAddOrClearPutOnPosRequest")]
        public static void EquipGuideAddOrClearPutOnPosRequestHandler(Session session, Packet.Request packet)
        {
            EquipGuideAddOrClearPutOnPosRequest request = packet.Deserialize<EquipGuideAddOrClearPutOnPosRequest>();
            EquipGuideData original = session.player.EquipGuideData;
            EquipGuideAddOrClearPutOnPosResponse response = new()
            {
                EquipGuideData = original ?? new()
            };
            EquipTargetTable? target = original is null || original.TargetId <= 0
                ? null
                : TableReaderV2.Parse<EquipTargetTable>().FirstOrDefault(row => row.Id == original.TargetId);
            EquipRecommendTable? recommendation = target is null
                ? null
                : TableReaderV2.Parse<EquipRecommendTable>().FirstOrDefault(row => row.Id == target.EquipRecommendId);

            // EN CodeText supplies these names/values; their assignment and precedence here
            // are inferred from client gates, not observed retail failure responses.
            if (original is null || original.TargetId <= 0)
                response.Code = 20021096; // EquipGuideNotSetTargetId
            else if (target is null)
                response.Code = 20021097; // EquipGuideTargetCfgNotFound
            else if (request.CharacterId != target.CharacterId || request.CharacterId != original.CharacterId
                || !session.character.Characters.Any(character => character.Id == request.CharacterId))
                response.Code = 20021098; // EquipGuideCharacterIdInvalid
            else if (recommendation is null)
                response.Code = 20021099; // EquipGuideRecommendCfgNotFound
            else if (request.Sites is null || request.EquipIds is null || request.Sites.Count == 0
                || request.Sites.Count != request.EquipIds.Count
                || request.Sites.Distinct().Count() != request.Sites.Count
                || request.EquipIds.Distinct().Count() != request.EquipIds.Count)
                response.Code = 5; // ParamsError
            if (response.Code != 0)
            {
                session.SendResponse(response, packet.Id);
                return;
            }

            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            for (int index = 0; index < request.EquipIds!.Count; index++)
            {
                EquipData? equip = session.character.Equips.Find(row => row.Id == request.EquipIds[index]);
                EquipTable? template = equip is null
                    ? null
                    : equipTables.FirstOrDefault(row => row.Id == equip.TemplateId);
                if (equip is null || template is null
                    || request.IsAdd && equip.CharacterId != request.CharacterId)
                    response.Code = 20021012; // EquipManagerGetCharEquipBySiteNotFound
                else if (template.Site != request.Sites![index])
                    response.Code = 20021014; // EquipManagerPutOnSiteError
                else if (template.Id != recommendation!.EquipRecomend
                    && !recommendation.SuitId.Contains(template.SuitId))
                    response.Code = 20021105; // EquipGuideTargetInvalid
                if (response.Code != 0)
                {
                    session.SendResponse(response, packet.Id);
                    return;
                }
            }

            List<int> positions = new(original!.PutOnPosList ?? []);
            if (request.IsAdd)
            {
                foreach (int site in request.Sites!)
                    if (!positions.Contains(site))
                        positions.Add(site);
            }
            else
            {
                // Clear is sent after takeoff/transfer; the equipment need not retain its former wearer.
                positions.RemoveAll(request.Sites!.Contains);
            }
            session.player.EquipGuideData = new EquipGuideData
            {
                TargetId = original.TargetId,
                CharacterId = original.CharacterId,
                PutOnPosList = positions,
                FinishedTargets = new List<int>(original.FinishedTargets ?? [])
            };
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.EquipGuideData = original;
                session.log.Error($"Failed to persist equip guide positions: {exception}");
                response.Code = 2; // ServerInternalError; persistence-failure assignment is inferred.
            }
            response.EquipGuideData = session.player.EquipGuideData;
            session.SendResponse(response, packet.Id);
        }

        internal static bool IsCurrentGoalComplete(Session session) =>
            GetEquipGuideFinishCode(session, session.player.EquipGuideData.CharacterId) == 0;

        private static int GetEquipGuideFinishCode(Session session, int characterId)
        {
            EquipGuideData guide = session.player.EquipGuideData;
            // EN CodeText supplies the named failures; their precedence is not a retail oracle.
            if (guide.TargetId <= 0)
                return 20021096; // EquipGuideNotSetTargetId
            EquipTargetTable? target = TableReaderV2.Parse<EquipTargetTable>()
                .FirstOrDefault(row => row.Id == guide.TargetId);
            if (target is null)
                return 20021097;
            if (characterId != target.CharacterId || characterId != guide.CharacterId
                || !session.character.Characters.Any(character => character.Id == characterId))
                return 20021098;
            EquipRecommendTable? recommendation = TableReaderV2.Parse<EquipRecommendTable>()
                .FirstOrDefault(row => row.Id == target.EquipRecommendId);
            if (recommendation is null)
                return 20021099;
            EquipJudgeTable? judge = TableReaderV2.Parse<EquipJudgeTable>().FirstOrDefault(row => row.Id == 1);
            if (judge is null)
                return 20021100;
            if (recommendation.SuitId.Count != recommendation.Number.Count
                || recommendation.Number.Sum() != 6)
                return 20021102;

            List<EquipTable> templates = TableReaderV2.Parse<EquipTable>();
            int[] suitCounts = new int[recommendation.SuitId.Count];
            HashSet<int> sites = [];
            long score = 0;
            foreach (EquipData equip in session.character.Equips)
            {
                if (equip.CharacterId != characterId)
                    continue;
                EquipTable? template = templates.FirstOrDefault(row => row.Id == equip.TemplateId);
                if (template is null || template.Site is < 0 or > 6 || !sites.Add(template.Site))
                    return 20021105;
                // XEquipTarget.UpdateProgress scores only the matching template worn at its site.
                if (template.Site == 0)
                {
                    if (template.Id != recommendation.EquipRecomend)
                        return 20021103;
                    score += judge.WeaponPutOnScore + (long)equip.Breakthrough * judge.WeaponBreakThroughScore
                        + (long)equip.Level * judge.WeaponUpLevelScore;
                }
                else
                {
                    int suitIndex = recommendation.SuitId.IndexOf(template.SuitId);
                    if (suitIndex < 0)
                        return 20021102;
                    suitCounts[suitIndex]++;
                    score += judge.ChipPutOnScore + (long)equip.Breakthrough * judge.ChipBreakThroughScore
                        + (long)equip.Level * judge.ChipUpLevelScore;
                }
            }
            for (int index = 0; index < suitCounts.Length; index++)
                if (suitCounts[index] != recommendation.Number[index])
                    return 20021102;
            return score >= judge.GrossScore ? 0 : 20021103;
        }

        [RequestPacketHandler("EquipGuideTargetFinishRequest")]
        public static void EquipGuideTargetFinishRequestHandler(Session session, Packet.Request packet)
        {
            EquipGuideTargetFinishRequest request = packet.Deserialize<EquipGuideTargetFinishRequest>();
            EquipGuideData original = session.player.EquipGuideData;
            EquipGuideTargetFinishResponse response = new()
            {
                Code = GetEquipGuideFinishCode(session, request.CharacterId),
                EquipGuideData = original
            };
            if (response.Code != 0)
            {
                session.SendResponse(response, packet.Id);
                return;
            }

            List<int> finished = new(original.FinishedTargets);
            if (!finished.Contains(original.TargetId))
                finished.Add(original.TargetId);
            // Retail 20260715: completing/re-completing a selected target clears active fields,
            // preserves previous finishes, and never appends the same target twice.
            session.player.EquipGuideData = new EquipGuideData { FinishedTargets = finished };
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.EquipGuideData = original;
                session.log.Error($"Failed to persist equip guide completion: {exception}");
                response.Code = 2; // ServerInternalError; persistence failure is not observed in retail.
            }
            response.EquipGuideData = session.player.EquipGuideData;
            session.SendResponse(response, packet.Id);
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

            int enhancementCount = ApplyFeedOperations(
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
            var balancesBefore = itemDeltas.Where(delta => delta.Value < 0)
                .ToDictionary(delta => delta.Key, delta => session.inventory.Items.Find(item => item.Id == delta.Key)?.Count ?? 0);
            ApplyItemDeltas(session, itemDeltas, notifyItemDataList);
            if (notifyItemDataList.ItemDataList.Count > 0)
                session.SendPush(notifyItemDataList);

            if (notifyEquipDataList.DeletedEquipIdList.Count > 0 || notifyEquipDataList.EquipDataList.Count > 0)
                session.SendPush(notifyEquipDataList);

            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, balancesBefore.Select(balance =>
                (11202, (int?)balance.Key, (int)Math.Clamp(balance.Value -
                    (session.inventory.Items.Find(item => item.Id == balance.Key)?.Count ?? 0), 0, int.MaxValue))));
            if (enhancementCount > 0)
                TaskModule.RecordEquipmentProgress(session, 12202, Enumerable.Repeat(targetEquip, enhancementCount).ToArray());

            session.SendResponse(response, packet.Id);
        }

        private static int ApplyFeedOperations(
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
            int enhancementCount = 0;
            foreach (EquipFeedOperationInfo operationInfo in request.OperationInfos ?? [])
            {
                switch (operationInfo.OperationType)
                {
                    case EquipFeedOperationTypeLevelUp:
                    {
                        (int Level, int Exp) beforeEnhancement = (targetEquip.Level, targetEquip.Exp);
                        int targetLevel = GetOperationTargetLevel(targetEquip, request.TargetBreakthrough, request.TargetLevel, equipBreakThroughTables);

                        ConsumeFeedItems(session, itemTables, request.EquipId, targetLevel, operationInfo, itemDeltas);
                        ConsumeFeedEquips(session, targetEquip, targetEquipTable, equipTables, equipBreakThroughTables, targetLevel, operationInfo, itemDeltas, notifyEquipDataList);
                        if ((targetEquip.Level, targetEquip.Exp) != beforeEnhancement)
                            enhancementCount++;
                        break;
                    }
                    case EquipFeedOperationTypeBreakthrough:
                    {
                        ApplyEquipBreakthrough(targetEquip, equipBreakThroughTables, itemDeltas);
                        break;
                    }
                }
            }
            return enhancementCount;
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

                if (!TryResolveFeedEquip(
                        session,
                        targetEquip,
                        targetEquipTable,
                        equipId,
                        equipTables,
                        equipBreakThroughTables,
                        out EquipData feedEquip,
                        out int feedExp))
                {
                    continue;
                }

                if (totalFeedExp > int.MaxValue - feedExp
                    || feedExp > int.MaxValue / 10
                    || !CanAddItemDelta(itemDeltas, Inventory.Coin, feedExp * -10))
                {
                    break;
                }

                if (!session.character.Equips.Remove(feedEquip))
                    continue;

                session.character.AddEquipExp((int)targetEquip.Id, feedExp);
                totalFeedExp += feedExp;
                AddItemDelta(itemDeltas, Inventory.Coin, feedExp * -10);
                notifyEquipDataList.DeletedEquipIdList.Add(feedEquip.Id);
            }

            return totalFeedExp;
        }

        private static bool TryConsumeValidatedFeedEquips(
            Session session,
            EquipData targetEquip,
            EquipTable? targetEquipTable,
            List<int>? requestedEquipIds,
            List<EquipTable> equipTables,
            List<EquipBreakThroughTable> equipBreakThroughTables,
            Dictionary<int, int> itemDeltas,
            NotifyEquipDataList notifyEquipDataList,
            out int totalFeedExp)
        {
            totalFeedExp = 0;
            if (requestedEquipIds is null || requestedEquipIds.Count == 0)
                return true;

            HashSet<int> requestedIds = [];
            List<(EquipData Equip, int Exp)> validated = [];
            long projectedExp = 0;
            foreach (int equipId in requestedEquipIds)
            {
                if (equipId <= 0 || equipId == targetEquip.Id || !requestedIds.Add(equipId))
                    return false;

                if (!TryResolveFeedEquip(
                        session,
                        targetEquip,
                        targetEquipTable,
                        equipId,
                        equipTables,
                        equipBreakThroughTables,
                        out EquipData feedEquip,
                        out int feedExp)
                    || projectedExp + feedExp > int.MaxValue / 10L)
                {
                    return false;
                }

                projectedExp += feedExp;
                validated.Add((feedEquip, feedExp));
            }
            int aggregateCoinDelta = checked((int)(projectedExp * -10L));
            if (!CanAddItemDelta(itemDeltas, Inventory.Coin, aggregateCoinDelta))
                return false;
            totalFeedExp = (int)projectedExp;

            foreach ((EquipData feedEquip, _) in validated)
            {
                if (!session.character.Equips.Remove(feedEquip))
                    throw new InvalidOperationException($"Validated feed equip {feedEquip.Id} disappeared before consumption.");
                notifyEquipDataList.DeletedEquipIdList.Add(feedEquip.Id);
            }
            AddItemDelta(itemDeltas, Inventory.Coin, aggregateCoinDelta);

            return true;
        }

        private static bool TryResolveFeedEquip(
            Session session,
            EquipData targetEquip,
            EquipTable? targetEquipTable,
            int equipId,
            List<EquipTable> equipTables,
            List<EquipBreakThroughTable> equipBreakThroughTables,
            out EquipData feedEquip,
            out int feedExp)
        {
            EquipData? resolvedEquip = session.character.Equips.Find(equip => equip.Id == equipId);
            feedExp = 0;
            if (resolvedEquip is null
                || resolvedEquip.IsLock
                || resolvedEquip.CharacterId != 0
                || session.player.IsEquipInTeamPrefab(resolvedEquip.Id))
            {
                feedEquip = null!;
                return false;
            }

            feedEquip = resolvedEquip;
            EquipTable? feedEquipTable = equipTables.FirstOrDefault(table => table.Id == resolvedEquip.TemplateId);
            if (!CanFeedEquipIntoTarget(targetEquipTable, feedEquipTable))
                return false;

            feedExp = GetEquipFeedExp(feedEquip, equipBreakThroughTables);
            return feedExp > 0;
        }

        private static bool CanAddItemDelta(Dictionary<int, int> itemDeltas, int itemId, int delta)
        {
            int current = itemDeltas.GetValueOrDefault(itemId);
            return delta >= 0
                ? current <= int.MaxValue - delta
                : current >= int.MinValue - delta;
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

            bool targetIsWeapon = targetEquipTable.Site == 0;
            bool feedIsWeapon = feedEquipTable.Site == 0;
            return targetIsWeapon == feedIsWeapon;
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
            long feedExp = (long)(feedEquipBreakThrough?.Exp ?? 0) + Math.Max(0, equip.Exp);
            return feedExp is > 0 and <= int.MaxValue ? (int)feedExp : 0;
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
                    var balancesBefore = equipBreakThrough.ItemId.Append(equipBreakThrough.UseItemId)
                        .Where(id => id > 0).Distinct()
                        .ToDictionary(id => id, id => session.inventory.Items.Find(item => item.Id == id)?.Count ?? 0);

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
                    TaskModule.RecordTableDrivenProgress(session, balancesBefore.Select(balance =>
                        (11202, (int?)balance.Key, (int)Math.Clamp(balance.Value -
                            (session.inventory.Items.Find(item => item.Id == balance.Key)?.Count ?? 0), 0, int.MaxValue))));
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
            else if (equip.IsLock != request.IsLock)
            {
                equip.IsLock = request.IsLock;
                session.character.Save();
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
            CharacterData? targetCharacter = session.character.Characters
                .Find(character => character.Id == request.CharacterId);
            CharacterTable? targetCharacterTable = targetCharacter is null
                ? null
                : TableReaderV2.Parse<CharacterTable>().FirstOrDefault(character => character.Id == request.CharacterId);
            if (targetCharacterTable is null
                || (toEquipTable.Site == 0 && toEquipTable.Type != targetCharacterTable.EquipType)
                || (toEquipTable.CharacterId != 0 && toEquipTable.CharacterId != request.CharacterId)
                || toEquipTable.Site != request.Site)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                session.SendResponse(new EquipPutOnResponse() { Code = 20021012 }, packet.Id);
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
            bool changed = toEquip.CharacterId != request.CharacterId || previousEquips.Count > 0;


            foreach (EquipData previousEquip in previousEquips)
            {
                previousEquip.CharacterId = 0;
            }

            toEquip.CharacterId = request.CharacterId;
            if (changed)
                session.character.Save();
            session.AppliedTeamPrefabId = null;


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

            bool changed = false;
            foreach (int equipId in request.EquipIds)
            {
                EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == equipId);
                if (equip?.CharacterId is not > 0)
                    continue;
                equip.CharacterId = 0;
                changed = true;
            }
            if (changed)
            {
                session.character.Save();
                session.AppliedTeamPrefabId = null;
            }


            session.SendResponse(new EquipTakeOffResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipAddChipGroupRequest")]
        public static void EquipAddChipGroupRequestHandler(Session session, Packet.Request packet)
        {
            EquipAddChipGroupRequest request = packet.Deserialize<EquipAddChipGroupRequest>();
            EquipChipGroupData group = new()
            {
                GroupId = session.player.EquipChipGroups.Count == 0
                    ? 1
                    : checked(session.player.EquipChipGroups.Max(value => value.GroupId) + 1),
                Name = request.Name.Trim(),
                ChipIdList = request.ChipIds.ToList(),
                CharacterId = request.CharacterId
            };
            if (!IsValidChipGroup(session, group))
            {
                session.SendResponse(new EquipAddChipGroupResponse { Code = 20021012 }, packet.Id);
                return;
            }

            session.player.EquipChipGroups.Add(group);
            session.player.Save();
            session.SendResponse(new EquipAddChipGroupResponse { ChipGroupData = group }, packet.Id);
        }

        [RequestPacketHandler("EquipDeleteChipGroupRequest")]
        public static void EquipDeleteChipGroupRequestHandler(Session session, Packet.Request packet)
        {
            EquipDeleteChipGroupRequest request = packet.Deserialize<EquipDeleteChipGroupRequest>();
            int removed = session.player.EquipChipGroups.RemoveAll(value => value.GroupId == request.GroupId);
            if (removed == 0)
            {
                session.SendResponse(new EquipDeleteChipGroupResponse { Code = 20021012 }, packet.Id);
                return;
            }

            session.player.Save();
            session.SendResponse(new EquipDeleteChipGroupResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipUpdateChipGroupRequest")]
        public static void EquipUpdateChipGroupRequestHandler(Session session, Packet.Request packet)
        {
            EquipUpdateChipGroupRequest request = packet.Deserialize<EquipUpdateChipGroupRequest>();
            EquipChipGroupData? source = request.GroupData;
            int index = source is null
                ? -1
                : session.player.EquipChipGroups.FindIndex(value => value.GroupId == source.GroupId);
            if (index < 0 || !IsValidChipGroup(session, source!))
            {
                session.SendResponse(new EquipUpdateChipGroupResponse { Code = 20021012 }, packet.Id);
                return;
            }

            session.player.EquipChipGroups[index] = new EquipChipGroupData
            {
                GroupId = source!.GroupId,
                Name = source.Name.Trim(),
                ChipIdList = source.ChipIdList.ToList(),
                CharacterId = source.CharacterId
            };
            session.player.Save();
            session.SendResponse(new EquipUpdateChipGroupResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipPutOnChipGroupRequest")]
        public static void EquipPutOnChipGroupRequestHandler(Session session, Packet.Request packet)
        {
            EquipPutOnChipGroupRequest request = packet.Deserialize<EquipPutOnChipGroupRequest>();
            EquipChipGroupData? group = session.player.EquipChipGroups
                .FirstOrDefault(value => value.GroupId == request.GroupId);
            if (group is null
                || !session.character.Characters.Any(value => value.Id == request.CharacterId)
                || (group.CharacterId != 0 && group.CharacterId != request.CharacterId)
                || !IsValidChipGroup(session, group))
            {
                session.SendResponse(new EquipPutOnChipGroupResponse { Code = 20021012 }, packet.Id);
                return;
            }

            HashSet<uint> selectedIds = group.ChipIdList.Select(value => (uint)value).ToHashSet();
            foreach (EquipData equip in session.character.Equips)
            {
                EquipTable? row = Character.ResolveEquipTemplate(equip.TemplateId);
                if (row?.Site > 0 && equip.CharacterId == request.CharacterId)
                    equip.CharacterId = 0;
                if (selectedIds.Contains(equip.Id))
                    equip.CharacterId = request.CharacterId;
            }
            session.character.Save();
            session.AppliedTeamPrefabId = null;

            session.SendResponse(new EquipPutOnChipGroupResponse(), packet.Id);
        }

        private static bool IsValidChipGroup(Session session, EquipChipGroupData group)
        {
            if (string.IsNullOrWhiteSpace(group.Name)
                || group.ChipIdList is not { Count: > 0 and <= 6 }
                || group.ChipIdList.Distinct().Count() != group.ChipIdList.Count
                || (group.CharacterId != 0
                    && !session.character.Characters.Any(value => value.Id == group.CharacterId)))
            {
                return false;
            }

            HashSet<int> sites = new();
            foreach (int chipId in group.ChipIdList)
            {
                EquipData? equip = session.character.Equips.FirstOrDefault(value => value.Id == chipId);
                EquipTable? row = equip is null ? null : Character.ResolveEquipTemplate(equip.TemplateId);
                if (equip?.IsRecycle == true || row is null || row.Site <= 0 || !sites.Add(row.Site))
                    return false;
            }
            return true;
        }

        [RequestPacketHandler("EquipResonanceRequest")]
        public static void EquipResonanceRequestHandler(Session session, Packet.Request packet)
        {
            EquipResonanceRequest request = packet.Deserialize<EquipResonanceRequest>();
            session.log.Info(
                $"EquipResonanceRequest received: EquipId={request.EquipId} Slot={request.Slots.FirstOrDefault()} " +
                $"UseItemId={request.UseItemId} UseEquipId={request.UseEquipId} CharacterId={request.CharacterId ?? 0} " +
                $"SelectType={(int?)request.SelectType} SelectSkillCount={request.SelectSkillIds?.Count ?? 0}.");

            var equip = session.character.Equips.Find(x => x.Id == request.EquipId);

            if (equip is null)
            {
                // EquipManagerGetCharEquipBySiteNotFound
                session.SendResponse(new EquipResonanceResponse() { Code = 20021012 }, packet.Id);
                return;
            }


            int slot = request.Slots.FirstOrDefault();
            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            List<EquipResonanceTable> resonanceTables = TableReaderV2.Parse<EquipResonanceTable>();
            List<EquipResonanceUseItemTable> useItemTables =
                TableReaderV2.Parse<EquipResonanceUseItemTable>();
            EquipTable? equipTable = equipTables.Find(x => x.Id == equip.TemplateId);
            EquipResonanceTable? equipResonance = resonanceTables.Find(x => x.Id == equip.TemplateId);
            EquipResonanceUseItemTable? configuredUseItem =
                useItemTables.Find(x => x.Id == equip.TemplateId);
            if (slot <= 0 || equipTable is null)
            {
                session.SendResponse(new EquipResonanceResponse() { Code = 20021038 }, packet.Id);
                return;
            }

            bool isMemory = equipTable.Site > 0;
            bool usesEquipMaterial = request.UseEquipId != 0;
            EquipData? materialEquip = null;
            if (request.UseEquipId < 0 || request.UseItemId < 0
                || (usesEquipMaterial && request.UseItemId != 0))
            {
                session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                return;
            }
            if (usesEquipMaterial)
            {
                materialEquip = session.character.Equips.Find(candidate => candidate.Id == request.UseEquipId);
                EquipTable? materialTable = equipTables.Find(row => row.Id == materialEquip?.TemplateId);
                if (isMemory || equip.IsRecycle
                    || request.Slots.Count != 1 || slot is < 1 or > 3
                    || materialEquip is null || materialEquip.Id == equip.Id
                    || materialEquip.IsLock || materialEquip.IsRecycle || materialEquip.CharacterId != 0
                    || session.player.IsEquipInTeamPrefab(materialEquip.Id)
                    || materialTable is null || materialTable.Site != 0
                    || materialTable.Star != equipTable.Star)
                {
                    session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                    return;
                }
            }
            bool hasSelectionFields = request.SelectSkillIds is not null;
            bool usesSelectMaterial = configuredUseItem?.SelectSkillItemId == request.UseItemId;
            bool isSelectedMemoryRequest = isMemory && usesSelectMaterial;
            if (isMemory
                && (request.Slots.Count != 1
                    || slot is not (1 or 2)
                    || (usesSelectMaterial
                        && (request.SelectSkillIds is not { Count: 1 }
                            || request.SelectSkillIds[0] <= 0
                            || request.SelectType is not (EquipResonanceType.Attrib
                                or EquipResonanceType.CharacterSkill)))
                    || (!usesSelectMaterial && hasSelectionFields)
                    || (request.CharacterId is not int memoryCharacterId
                        || memoryCharacterId <= 0
                        || !session.character.Characters.Any(character =>
                            character.Id == memoryCharacterId))))
            {
                session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                return;
            }

            List<AttribPoolTable> attribPools = TableReaderV2.Parse<AttribPoolTable>();
            List<CharacterSkillPoolTable> characterSkillPools =
                TableReaderV2.Parse<CharacterSkillPoolTable>();
            List<CharacterSkillTable> characterSkills = TableReaderV2.Parse<CharacterSkillTable>();
            List<CharacterSkillGroupTable> characterSkillGroups =
                TableReaderV2.Parse<CharacterSkillGroupTable>();
            List<ResonanceInfo> resonancePool = new();
            bool usesWeaponSkillResonance = (equipResonance?.WeaponSkillPoolId.Count ?? 0) > 0
                || (equipTable.Site == 0 && equipTable.Quality >= 6);
            bool usesCharacterSkillResonance =
                (equipResonance?.CharacterSkillPoolId.Count ?? 0) > 0;
            bool isSelectedWeaponSkillRequest = usesWeaponSkillResonance
                && request.SelectSkillIds is { Count: > 0 };
            if (usesEquipMaterial
                && (usesWeaponSkillResonance
                    ? request.SelectSkillIds is not { Count: 1 }
                        || request.SelectType != EquipResonanceType.WeaponSkill
                        || request.CharacterId is not int weaponCharacterId
                        || !session.character.Characters.Any(character => character.Id == weaponCharacterId)
                        || !TableReaderV2.Parse<WeaponSkillPoolTable>().Any(row =>
                            row.PoolId == equipResonance?.WeaponSkillPoolId.ElementAtOrDefault(slot - 1)
                            && row.CharacterId == weaponCharacterId
                            && row.SkillId.Contains(request.SelectSkillIds[0]))
                    : hasSelectionFields || request.SelectType is not (null or EquipResonanceType.Attrib)))
            {
                session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                return;
            }

            if (isSelectedMemoryRequest)
            {
                int selectedId = request.SelectSkillIds![0];
                bool selectionIsValid;
                if (request.SelectType == EquipResonanceType.Attrib)
                {
                    int poolId = equipResonance?.AttribPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    selectionIsValid = poolId > 0
                        && attribPools.Any(row => row.PoolId == poolId && row.Id == selectedId);
                }
                else
                {
                    int poolId =
                        equipResonance?.CharacterSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    CharacterSkillTable? ownedSkills =
                        characterSkills.Find(row => row.CharacterId == request.CharacterId);
                    selectionIsValid = poolId > 0
                        && CharacterOwnsResonanceSkill(
                            ownedSkills, poolId, selectedId, characterSkillPools,
                            characterSkillGroups);
                }

                if (!selectionIsValid)
                {
                    session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                    return;
                }
                resonancePool.Add(new ResonanceInfo
                {
                    Slot = slot,
                    Type = request.SelectType!.Value,
                    CharacterId = request.CharacterId!.Value,
                    TemplateId = selectedId,
                    UseItemId = request.UseItemId
                });
            }
            else if (isSelectedWeaponSkillRequest)
            {
                int selectedSkillId = request.SelectSkillIds!.FirstOrDefault(skillId => skillId > 0);
                if (selectedSkillId > 0)
                {
                    resonancePool.Add(new ResonanceInfo
                    {
                        Slot = slot,
                        Type = request.SelectType ?? EquipResonanceType.WeaponSkill,
                        CharacterId = request.CharacterId ?? 0,
                        TemplateId = selectedSkillId,
                        UseItemId = request.UseItemId
                    });
                }
            }
            else if (isMemory)
            {
                int characterId = request.CharacterId!.Value;
                if (request.SelectType is null or EquipResonanceType.Attrib)
                {
                    int poolId = equipResonance?.AttribPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    foreach (AttribPoolTable attrib in attribPools.Where(row => row.PoolId == poolId))
                    {
                        resonancePool.Add(new ResonanceInfo
                        {
                            Slot = slot,
                            Type = EquipResonanceType.Attrib,
                            CharacterId = characterId,
                            TemplateId = attrib.Id,
                            UseItemId = request.UseItemId
                        });
                    }
                }
                if ((request.SelectType is null or EquipResonanceType.CharacterSkill)
                    && usesCharacterSkillResonance)
                {
                    int poolId =
                        equipResonance!.CharacterSkillPoolId.ElementAtOrDefault(slot - 1);
                    CharacterSkillTable? ownedSkills =
                        characterSkills.Find(row => row.CharacterId == characterId);
                    foreach (CharacterSkillPoolTable skill in characterSkillPools.Where(row =>
                        row.PoolId == poolId
                        && CharacterOwnsResonanceSkill(
                            ownedSkills, poolId, row.SkillId, characterSkillPools,
                            characterSkillGroups)))
                    {
                        resonancePool.Add(new ResonanceInfo
                        {
                            Slot = slot,
                            Type = EquipResonanceType.CharacterSkill,
                            CharacterId = characterId,
                            TemplateId = skill.SkillId,
                            UseItemId = request.UseItemId
                        });
                    }
                }
            }
            else
            {
                IEnumerable<int> attribPoolIds = equipResonance?.AttribPoolId ?? [];
                if (!attribPoolIds.Any() && equipTable.Site == 0 && equipTable.Quality == 5)
                    attribPoolIds = [5, 8, 9];
                foreach (int attribPoolId in attribPoolIds)
                {
                    foreach (AttribPoolTable attrib in attribPools.Where(x => x.PoolId == attribPoolId))
                    {
                        resonancePool.Add(new ResonanceInfo
                        {
                            Slot = slot,
                            Type = EquipResonanceType.Attrib,
                            CharacterId = request.CharacterId ?? 0,
                            TemplateId = attrib.Id,
                            UseItemId = request.UseItemId
                        });
                    }
                }
            }

            int materialCost = configuredUseItem is null
                ? 0
                : ResolveEquipResonanceCost(equipTable, configuredUseItem, request.UseItemId);
            bool hasMaterial = materialCost > 0
                && session.inventory.Items.Any(item =>
                    item.Id == request.UseItemId && item.Count >= materialCost);
            bool hasActiveResonance = equip.ResonanceInfo?.Any(candidate =>
                candidate.Slot == slot) == true;
            bool isSkillSwap = isSelectedWeaponSkillRequest && hasActiveResonance;
            if (resonancePool.Count == 0)
            {
                session.log.Warn(
                    $"EquipResonanceRequest rejected: resonance pool empty; EquipId={request.EquipId} " +
                    $"TemplateId={equip.TemplateId} Slot={slot} UseItemId={request.UseItemId} UseEquipId={request.UseEquipId} " +
                    $"SelectType={(int?)request.SelectType} SelectSkillCount={request.SelectSkillIds?.Count ?? 0}.");
                session.SendResponse(new EquipResonanceResponse { Code = 20021038 }, packet.Id);
                return;
            }
            if (!usesEquipMaterial && !hasMaterial && !isSkillSwap)
            {
                long availableMaterial = session.inventory.Items
                    .Find(item => item.Id == request.UseItemId)?.Count ?? 0;
                session.log.Warn(
                    $"EquipResonanceRequest rejected: resonance material unavailable; EquipId={request.EquipId} " +
                    $"TemplateId={equip.TemplateId} Slot={slot} UseItemId={request.UseItemId} UseEquipId={request.UseEquipId} " +
                    $"MaterialCost={materialCost} AvailableMaterial={availableMaterial} " +
                    $"SelectType={(int?)request.SelectType} SelectSkillCount={request.SelectSkillIds?.Count ?? 0}.");
                session.SendResponse(new EquipResonanceResponse { Code = 20012004 }, packet.Id);
                return;
            }

            ResonanceInfo resonance = resonancePool[Random.Shared.Next(resonancePool.Count)];
            if (usesEquipMaterial)
            {
                resonance.IsUseEquip = true;
                List<ResonanceInfo>? originalResonances = equip.ResonanceInfo;
                List<ResonanceInfo>? originalUnconfirmed = equip.UnconfirmedResonanceInfo;
                int materialIndex = session.character.Equips.IndexOf(materialEquip!);
                bool commitsWeaponResonance = isSelectedWeaponSkillRequest || !hasActiveResonance;
                try
                {
                    if (commitsWeaponResonance)
                    {
                        Character.NormalizeEquipResonances(equip);
                        equip.ResonanceInfo ??= [];
                        equip.ResonanceInfo.RemoveAll(candidate => candidate.Slot == resonance.Slot);
                        equip.ResonanceInfo.Add(resonance);
                    }
                    session.character.Equips.RemoveAt(materialIndex);
                    session.character.SaveChecked();
                }
                catch (Exception exception)
                {
                    equip.ResonanceInfo = originalResonances!;
                    equip.UnconfirmedResonanceInfo = originalUnconfirmed!;
                    if (!session.character.Equips.Contains(materialEquip!))
                        session.character.Equips.Insert(materialIndex, materialEquip!);
                    session.log.Error($"EquipResonanceRequest save failed: EquipId={request.EquipId} " +
                        $"UseItemId={request.UseItemId} UseEquipId={request.UseEquipId}; {exception}");
                    session.SendResponse(new EquipResonanceResponse { Code = 1 }, packet.Id);
                    return;
                }
                if (commitsWeaponResonance)
                {
                    session.PendingEquipResonances.Remove((equip.Id, resonance.Slot));
                    session.AppliedTeamPrefabId = null;
                    TaskModule.RecordEquipmentProgress(session, 12205, [equip]);
                }
                else
                {
                    session.PendingEquipResonances[(equip.Id, resonance.Slot)] = resonance;
                }
                session.SendPush(new NotifyEquipDataList { DeletedEquipIdList = [materialEquip!.Id] });
                session.SendResponse(new EquipResonanceResponse { ResonanceDatas = [resonance] }, packet.Id);
                return;
            }
            if (hasMaterial)
            {
                NotifyItemDataList notifyItemData = new();
                notifyItemData.ItemDataList.Add(session.inventory.Do(request.UseItemId, -materialCost));
                session.SendPush(notifyItemData);
            }
            AscNet.Common.Database.Character.NormalizeEquipResonances(equip);
            equip.ResonanceInfo ??= [];
            bool commitsImmediately = isSelectedWeaponSkillRequest || !hasActiveResonance;
            if (commitsImmediately)
            {
                equip.ResonanceInfo.RemoveAll(candidate => candidate.Slot == resonance.Slot);
                equip.ResonanceInfo.Add(resonance);
                session.PendingEquipResonances.Remove((equip.Id, resonance.Slot));
                session.character.Save();
                session.AppliedTeamPrefabId = null;

            }
            else
            {
                session.PendingEquipResonances[(equip.Id, resonance.Slot)] = resonance;
            }
            if (hasMaterial)
                session.inventory.Save();
            if (hasMaterial)
                TaskModule.RecordTableDrivenProgress(session, [(11202, (int?)request.UseItemId, materialCost)]);
            if (commitsImmediately && hasMaterial)
                TaskModule.RecordEquipmentProgress(session, 12205, [equip]);
            session.SendResponse(new EquipResonanceResponse() { ResonanceDatas = [resonance] }, packet.Id);
        }

        [RequestPacketHandler("EquipQuickResonanceChipRequest")]
        public static void EquipQuickResonanceChipRequestHandler(Session session, Packet.Request packet)
        {
            EquipQuickResonanceChipRequest request = packet.Deserialize<EquipQuickResonanceChipRequest>();
            session.log.Info(
                $"EquipQuickResonanceChipRequest received: Slot={request.Slot} " +
                $"SelectType={(int)request.SelectType} EquipCount={request.EquipIds.Count} " +
                $"SelectSkillId={request.SelectSkillId} UseItemId={request.UseItemId} " +
                $"CharacterId={request.CharacterId}.");
            const int paramError = 20021114;
            const int equipIsNotChip = 20021115;
            void RejectParameter(string reason, long equipId = 0, long templateId = 0,
                int skillPoolId = 0, int materialCost = 0)
            {
                session.log.Warn(
                    $"EquipQuickResonanceChipRequest rejected: {reason}; " +
                    $"Slot={request.Slot} SelectType={(int)request.SelectType} EquipId={equipId} " +
                    $"TemplateId={templateId} SelectSkillId={request.SelectSkillId} " +
                    $"UseItemId={request.UseItemId} CharacterId={request.CharacterId} " +
                    $"SkillPoolId={skillPoolId} MaterialCost={materialCost}.");
                session.SendResponse(new EquipQuickResonanceChipResponse { Code = paramError }, packet.Id);
            }

            bool isAttributeSelection = request.SelectType == EquipResonanceType.Attrib;
            bool isCharacterSkillSelection = request.SelectType == EquipResonanceType.CharacterSkill;
            if (request.Slot is not (1 or 2)
                || (!isAttributeSelection && !isCharacterSkillSelection)
                || request.SelectSkillId <= 0
                || request.UseItemId <= 0
                || request.EquipIds.Count == 0
                || request.EquipIds.Count != request.EquipIds.Distinct().Count()
                || request.CharacterId <= 0
                || !session.character.Characters.Any(character => character.Id == request.CharacterId))
            {
                RejectParameter("invalid request envelope");
                return;
            }

            CharacterSkillTable? characterSkills = isCharacterSkillSelection
                ? TableReaderV2.Parse<CharacterSkillTable>()
                    .Find(row => row.CharacterId == request.CharacterId)
                : null;
            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            List<EquipResonanceTable> resonanceTables = TableReaderV2.Parse<EquipResonanceTable>();
            List<AttribPoolTable> attribPoolTables = TableReaderV2.Parse<AttribPoolTable>();
            List<EquipResonanceUseItemTable> resonanceUseItemTables =
                TableReaderV2.Parse<EquipResonanceUseItemTable>();
            List<CharacterSkillPoolTable> characterSkillPoolTables =
                isCharacterSkillSelection
                    ? TableReaderV2.Parse<CharacterSkillPoolTable>()
                    : [];
            List<CharacterSkillGroupTable> characterSkillGroupTables =
                isCharacterSkillSelection
                    ? TableReaderV2.Parse<CharacterSkillGroupTable>()
                    : [];
            List<EquipData> equips = new(request.EquipIds.Count);
            int totalMaterialCost = 0;

            foreach (int equipId in request.EquipIds)
            {
                EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == equipId);
                if (equip is null)
                {
                    RejectParameter("equipment instance not found", equipId);
                    return;
                }

                EquipTable? equipTable = equipTables.Find(row => row.Id == equip.TemplateId);
                if (equipTable is null || equipTable.Site is < 1 or > 6)
                {
                    session.SendResponse(new EquipQuickResonanceChipResponse { Code = equipIsNotChip }, packet.Id);
                    return;
                }

                int resonancePoolId;
                if (isAttributeSelection)
                {
                    resonancePoolId = ResolveAttributePool(equipTable, request.Slot, resonanceTables);
                    if (resonancePoolId <= 0
                        || !attribPoolTables.Any(row =>
                            row.PoolId == resonancePoolId && row.Id == request.SelectSkillId))
                    {
                        RejectParameter("selected attribute is not in the resolved attribute pool",
                            equip.Id, equip.TemplateId, resonancePoolId);
                        return;
                    }
                }
                else
                {
                    resonancePoolId = ResolveCharacterSkillPool(
                        equipTable, request.Slot, resonanceTables);
                    if (resonancePoolId <= 0)
                    {
                        RejectParameter("character skill pool not resolved",
                            equip.Id, equip.TemplateId, resonancePoolId);
                        return;
                    }
                    if (!CharacterOwnsResonanceSkill(
                        characterSkills, resonancePoolId, request.SelectSkillId,
                        characterSkillPoolTables, characterSkillGroupTables))
                    {
                        RejectParameter("selected skill is not in the resolved character skill pool",
                            equip.Id, equip.TemplateId, resonancePoolId);
                        return;
                    }
                }
                EquipResonanceUseItemTable? configuredMaterial =
                    resonanceUseItemTables.Find(row => row.Id == equipTable.Id);
                int equipMaterialCost = configuredMaterial?.SelectSkillItemId == request.UseItemId
                    ? configuredMaterial.SelectSkillItemCount ?? 0
                    : 0;
                if (equipMaterialCost <= 0)
                {
                    RejectParameter("select resonance material recipe not resolved",
                        equip.Id, equip.TemplateId, resonancePoolId, equipMaterialCost);
                    return;
                }

                try
                {
                    totalMaterialCost = checked(totalMaterialCost + equipMaterialCost);
                }
                catch (OverflowException)
                {
                    RejectParameter("total material cost overflow",
                        equip.Id, equip.TemplateId, resonancePoolId, equipMaterialCost);
                    return;
                }
                equips.Add(equip);
            }

            Item? material = session.inventory.Items.Find(item => item.Id == request.UseItemId);
            if (material is null || material.Count < totalMaterialCost)
            {
                session.SendResponse(new EquipQuickResonanceChipResponse { Code = 20012004 }, packet.Id);
                return;
            }

            foreach (EquipData equip in equips)
            {
                AscNet.Common.Database.Character.NormalizeEquipResonances(equip);
                equip.ResonanceInfo.RemoveAll(candidate => candidate.Slot == request.Slot);
                equip.ResonanceInfo.Add(new ResonanceInfo
                {
                    Slot = request.Slot,
                    Type = request.SelectType,
                    CharacterId = request.CharacterId,
                    TemplateId = request.SelectSkillId,
                    UseItemId = request.UseItemId
                });
                session.PendingEquipResonances.Remove((equip.Id, request.Slot));
            }

            NotifyArchiveEquip archivePush = new();
            foreach (EquipData equip in equips)
            {
                archivePush.Equips.Add(new NotifyArchiveEquip.NotifyArchiveEquipEquip
                {
                    Id = equip.TemplateId,
                    Level = equip.Level,
                    Breakthrough = equip.Breakthrough,
                    ResonanceCount = equip.ResonanceInfo.Count
                });
            }
            NotifyItemDataList itemPush = new();
            itemPush.ItemDataList.Add(session.inventory.Do(request.UseItemId, -totalMaterialCost));
            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, [(11202, (int?)request.UseItemId, totalMaterialCost)]);
            TaskModule.RecordEquipmentProgress(session, 12205, equips);
            session.AppliedTeamPrefabId = null;

            session.SendPush(archivePush);
            session.SendPush(itemPush);
            session.SendResponse(new EquipQuickResonanceChipResponse
            {
                SuccessEquipIds = request.EquipIds.ToList()
            }, packet.Id);
        }

        private static int ResolveAttributePool(
            EquipTable equip,
            int slot,
            List<EquipResonanceTable> resonanceTables)
        {
            return resonanceTables
                .Find(row => row.Id == equip.Id)?
                .AttribPoolId.ElementAtOrDefault(slot - 1) ?? 0;
        }

        private static int ResolveCharacterSkillPool(
            EquipTable equip,
            int slot,
            List<EquipResonanceTable> resonanceTables)
        {
            return resonanceTables
                .Find(row => row.Id == equip.Id)?
                .CharacterSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
        }

        private static bool CharacterOwnsResonanceSkill(
            CharacterSkillTable? characterSkills,
            int skillPoolId,
            int selectedSkillId,
            List<CharacterSkillPoolTable> skillPools,
            List<CharacterSkillGroupTable> skillGroups)
        {
            if (!skillPools.Any(row =>
                row.PoolId == skillPoolId && row.SkillId == selectedSkillId))
                return false;

            return characterSkills?.SkillGroupId.Any(groupId =>
                skillGroups.Find(group => group.Id == groupId)?
                    .SkillId.Contains(selectedSkillId) == true) == true;
        }

        private static int ResolveEquipResonanceCost(
            EquipTable equip,
            EquipResonanceUseItemTable configured,
            int useItemId)
        {
            int recipeCost = GetConfiguredResonanceMaterialCost(configured, useItemId);
            if (recipeCost <= 0)
                return recipeCost;
            EquipConfigTable? discount = TableReaderV2.Parse<EquipConfigTable>()
                .FirstOrDefault(row => row.SuitId == equip.SuitId && row.ItemId == useItemId);
            return discount is not null && discount.DiscountCount > 0
                ? discount.DiscountCount
                : recipeCost;
        }

        private static int GetConfiguredResonanceMaterialCost(
            EquipResonanceUseItemTable configured,
            int useItemId)
        {
            int configuredIndex = configured.ItemId.IndexOf(useItemId);
            if (configuredIndex >= 0)
                return configured.ItemCount.ElementAtOrDefault(configuredIndex);
            return configured.SelectSkillItemId == useItemId
                ? configured.SelectSkillItemCount ?? 0
                : 0;
        }

        private const int EquipAwakeInvalidCode = 20021038;
        private const int EquipAwakeInsufficientItemsCode = 20012004;

        private static bool TryResolveAwakeCosts(
            EquipData equip,
            int costType,
            out List<(int Id, int Count)> costs)
        {
            costs = new();
            EquipAwakeTable? recipe = TableReaderV2.Parse<EquipAwakeTable>()
                .Find(row => row.Id == equip.TemplateId);
            if (recipe is null)
                return false;

            List<int> itemIds;
            List<int> itemCounts;
            switch (costType)
            {
                case 1:
                    itemIds = recipe.ItemId;
                    itemCounts = recipe.ItemCount;
                    break;
                case 2:
                    itemIds = recipe.ItemCrystalId;
                    itemCounts = recipe.ItemCrystalCount;
                    break;
                default:
                    return false;
            }

            if (itemIds.Count == 0 || itemIds.Count != itemCounts.Count)
                return false;
            for (int index = 0; index < itemIds.Count; index++)
            {
                if (itemIds[index] <= 0 || itemCounts[index] <= 0)
                    return false;
                costs.Add((itemIds[index], itemCounts[index]));
            }
            return true;
        }

        private static bool CanAwakeEquipSlot(EquipData equip, int slot)
        {
            if (slot is not (1 or 2)
                || equip.ResonanceInfo?.Any(info => info.Slot == slot) != true)
                return false;

            EquipBreakThroughTable? current = Character.ResolveEquipBreakThrough(
                equip.TemplateId,
                equip.Breakthrough);
            return current is not null
                && equip.Level == current.LevelLimit
                && Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough + 1) is null;
        }

        private static bool HasAwakeSlot(EquipData equip, int slot)
        {
            return equip.AwakeSlotList?.Any(value =>
                Convert.ToInt32(value, CultureInfo.InvariantCulture) == slot) == true;
        }

        private static bool HasAwakeCosts(Session session, IEnumerable<(int Id, long Count)> costs)
        {
            return costs.All(cost =>
                cost.Count <= int.MaxValue
                && session.inventory.Items.Any(item => item.Id == cost.Id && item.Count >= cost.Count));
        }

        private static NotifyItemDataList ConsumeAwakeCosts(
            Session session,
            IEnumerable<(int Id, long Count)> costs)
        {
            NotifyItemDataList notify = new();
            List<(int Id, long Count)> orderedCosts = costs.ToList();
            if (orderedCosts.Count > 0)
            {
                (int id, long count) = orderedCosts[0];
                notify.ItemDataList.Add(session.inventory.Do(id, checked(-(int)count)));
                for (int index = orderedCosts.Count - 1; index > 0; index--)
                {
                    (id, count) = orderedCosts[index];
                    notify.ItemDataList.Add(session.inventory.Do(id, checked(-(int)count)));
                }
            }
            return notify;
        }

        [RequestPacketHandler("EquipAwakeRequest")]
        public static void EquipAwakeRequestHandler(Session session, Packet.Request packet)
        {
            EquipAwakeRequest request = packet.Deserialize<EquipAwakeRequest>();
            EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == request.EquipId);
            if (equip is null)
            {
                session.SendResponse(new EquipAwakeResponse { Code = 20021012 }, packet.Id);
                return;
            }

            equip.AwakeSlotList ??= new();
            if (HasAwakeSlot(equip, request.Slot))
            {
                session.SendResponse(new EquipAwakeResponse(), packet.Id);
                return;
            }
            if (!CanAwakeEquipSlot(equip, request.Slot)
                || !TryResolveAwakeCosts(equip, request.CostType, out List<(int Id, int Count)> resolvedCosts))
            {
                session.SendResponse(new EquipAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                return;
            }

            List<(int Id, long Count)> costs = resolvedCosts
                .Select(cost => (cost.Id, (long)cost.Count))
                .ToList();
            if (!HasAwakeCosts(session, costs))
            {
                session.SendResponse(new EquipAwakeResponse { Code = EquipAwakeInsufficientItemsCode }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = ConsumeAwakeCosts(session, costs);
            equip.AwakeSlotList.Add(request.Slot);
            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, costs.Select(cost => (11202, (int?)cost.Id, checked((int)cost.Count))));
            TaskModule.RecordEquipmentProgress(session, 12203, [equip]);
            session.SendPush(notifyItemData);
            session.SendResponse(new EquipAwakeResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipQuickAwakeRequest")]
        public static void EquipQuickAwakeRequestHandler(Session session, Packet.Request packet)
        {
            EquipQuickAwakeRequest request = packet.Deserialize<EquipQuickAwakeRequest>();
            if (request.EquipQuickAwakeInfos.Count == 0)
            {
                session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                return;
            }

            List<(EquipData Equip, int Slot)> targets = new();
            HashSet<(int EquipId, int Slot)> uniqueTargets = new();
            Dictionary<int, long> totalCosts = new();
            foreach (EquipQuickAwakeInfo awakeInfo in request.EquipQuickAwakeInfos)
            {
                EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == awakeInfo.EquipId);
                if (equip is null)
                {
                    session.SendResponse(new EquipQuickAwakeResponse { Code = 20021012 }, packet.Id);
                    return;
                }
                if (awakeInfo.Slots.Count == 0
                    || !TryResolveAwakeCosts(equip, 2, out List<(int Id, int Count)> recipeCosts))
                {
                    session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                    return;
                }

                foreach (int slot in awakeInfo.Slots)
                {
                    if (!uniqueTargets.Add((checked((int)equip.Id), slot)))
                    {
                        session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                        return;
                    }
                    if (HasAwakeSlot(equip, slot))
                        continue;
                    if (!CanAwakeEquipSlot(equip, slot))
                    {
                        session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                        return;
                    }
                    targets.Add((equip, slot));
                    foreach ((int id, int count) in recipeCosts)
                    {
                        try
                        {
                            totalCosts[id] = checked(totalCosts.GetValueOrDefault(id) + count);
                        }
                        catch (OverflowException)
                        {
                            session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInvalidCode }, packet.Id);
                            return;
                        }
                    }
                }
            }

            if (targets.Count == 0)
            {
                session.SendResponse(new EquipQuickAwakeResponse(), packet.Id);
                return;
            }

            List<(int Id, long Count)> costs = totalCosts.Select(cost => (cost.Key, cost.Value)).ToList();
            if (!HasAwakeCosts(session, costs))
            {
                session.SendResponse(new EquipQuickAwakeResponse { Code = EquipAwakeInsufficientItemsCode }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = ConsumeAwakeCosts(session, costs);
            foreach ((EquipData equip, int slot) in targets)
            {
                equip.AwakeSlotList ??= new();
                equip.AwakeSlotList.Add(slot);
            }
            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, costs.Select(cost => (11202, (int?)cost.Id, checked((int)cost.Count))));
            TaskModule.RecordEquipmentProgress(session, 12203, targets.Select(target => target.Equip).ToArray());
            session.SendPush(notifyItemData);
            session.SendResponse(new EquipQuickAwakeResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipResonanceConfirmRequest")]
        public static void EquipResonanceConfirmRequestHandler(Session session, Packet.Request packet)
        {
            EquipResonanceConfirmRequest request = packet.Deserialize<EquipResonanceConfirmRequest>();
            var equip = session.character.Equips.Find(candidate => candidate.Id == request.EquipId);
            ResonanceInfo? pending = equip is null
                ? null
                : session.PendingEquipResonances.GetValueOrDefault((equip.Id, request.Slot));
            if (equip is null)
            {
                session.SendResponse(new EquipResonanceConfirmResponse { Code = 20021012 }, packet.Id);
                return;
            }
            if (pending is null)
            {
                session.SendResponse(new EquipResonanceConfirmResponse { Code = 20021038 }, packet.Id);
                return;
            }

            if (request.IsUse)
            {
                equip.ResonanceInfo.RemoveAll(candidate => candidate.Slot == request.Slot);
                equip.ResonanceInfo.Add(pending);
            }

            session.PendingEquipResonances.Remove((equip.Id, request.Slot));
            if (request.IsUse)
            {
                session.character.Save();
                TaskModule.RecordEquipmentProgress(session, 12205, [equip]);
                session.AppliedTeamPrefabId = null;
            }

            session.SendResponse(new EquipResonanceConfirmResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipWeaponOverrunLevelUpRequest")]
        public static void EquipWeaponOverrunLevelUpRequestHandler(Session session, Packet.Request packet)
        {
            EquipWeaponOverrunLevelUpRequest request = packet.Deserialize<EquipWeaponOverrunLevelUpRequest>();
            EquipData? equip = FindWeapon(session, request.EquipId);
            WeaponOverrunTable? progression = equip is null
                ? null
                : TableReaderV2.Parse<WeaponOverrunTable>()
                    .Where(row => row.WeaponId == equip.TemplateId
                        && row.Level == equip.WeaponOverrunData.Level + 1)
                    .OrderByDescending(row => row.CharacterId > 0)
                    .FirstOrDefault();
            int itemId = progression?.ConsumeItemIds ?? 0;
            int itemCount = progression?.ConsumeItemCounts ?? 0;
            long materialCount = session.inventory.Items
                .FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;

            if (equip is null
                || progression is null
                || itemId <= 0
                || itemCount <= 0
                || materialCount < itemCount)
            {
                session.SendResponse(new EquipWeaponOverrunLevelUpResponse { Code = 1 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            notifyItems.ItemDataList.Add(session.inventory.Do(itemId, -itemCount));

            equip.WeaponOverrunData.Level = progression.Level;
            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, [(11202, (int?)itemId, itemCount)]);
            session.SendPush(notifyItems);
            session.SendResponse(new EquipWeaponOverrunLevelUpResponse
            {
                WeaponOverrunData = equip.WeaponOverrunData
            }, packet.Id);
        }

        [RequestPacketHandler("EquipWeaponActiveOverrunSuitRequest")]
        public static void EquipWeaponActiveOverrunSuitRequestHandler(Session session, Packet.Request packet)
        {
            EquipWeaponActiveOverrunSuitRequest request = packet.Deserialize<EquipWeaponActiveOverrunSuitRequest>();
            EquipData? equip = FindWeapon(session, request.EquipId);
            WeaponOverrunTable? overrun = equip is null
                ? null
                : TableReaderV2.Parse<WeaponOverrunTable>()
                    .FirstOrDefault(row => row.WeaponId == equip.TemplateId
                        && row.ActiveSuitItemId > 0
                        && row.ActiveSuitItemCount > 0);
            int suitItemId = overrun?.ActiveSuitItemId ?? 0;
            int suitItemCount = overrun?.ActiveSuitItemCount ?? 0;
            long materialCount = session.inventory.Items
                .FirstOrDefault(item => item.Id == suitItemId)?.Count ?? 0;
            bool validSuit = TableReaderV2.Parse<EquipTable>()
                .Any(row => row.SuitId == request.SuitId && row.Quality == 6);
            if (equip is null
                || overrun is null
                || equip.WeaponOverrunData.Level <= 0
                || equip.WeaponOverrunData.ActiveSuits.Contains(request.SuitId)
                || !validSuit
                || materialCount < suitItemCount)
            {
                session.SendResponse(new EquipWeaponActiveOverrunSuitResponse { Code = 1 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            notifyItems.ItemDataList.Add(
                session.inventory.Do(suitItemId, -suitItemCount));
            equip.WeaponOverrunData.ActiveSuits.Add(request.SuitId);
            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordTableDrivenProgress(session, [(11202, (int?)suitItemId, suitItemCount)]);
            session.SendPush(notifyItems);
            session.SendResponse(new EquipWeaponActiveOverrunSuitResponse
            {
                WeaponOverrunData = equip.WeaponOverrunData
            }, packet.Id);
        }

        [RequestPacketHandler("EquipWeaponChoseOverrunSuitRequest")]
        public static void EquipWeaponChoseOverrunSuitRequestHandler(Session session, Packet.Request packet)
        {
            EquipWeaponChoseOverrunSuitRequest request = packet.Deserialize<EquipWeaponChoseOverrunSuitRequest>();
            EquipData? equip = FindWeapon(session, request.EquipId);
            if (equip is null || !equip.WeaponOverrunData.ActiveSuits.Contains(request.SuitId))
            {
                session.SendResponse(new EquipWeaponChoseOverrunSuitResponse { Code = 1 }, packet.Id);
                return;
            }

            equip.WeaponOverrunData.ChoseSuit = request.SuitId;
            session.character.Save();
            session.AppliedTeamPrefabId = null;
            session.SendResponse(new EquipWeaponChoseOverrunSuitResponse
            {
                WeaponOverrunData = equip.WeaponOverrunData
            }, packet.Id);
        }

        private static EquipData? FindWeapon(Session session, int equipId)
        {
            EquipData? equip = session.character.Equips.Find(candidate => candidate.Id == equipId);
            if (equip is null)
                return null;

            EquipTable? equipTable = TableReaderV2.Parse<EquipTable>()
                .Find(row => row.Id == equip.TemplateId);
            if (equipTable is not { Site: 0, WeaponSkillId: > 0 })
                return null;

            equip.WeaponOverrunData ??= new();
            equip.WeaponOverrunData.ActiveSuits ??= [];
            return equip;
        }

        [RequestPacketHandler("EquipChipSiteAutoRecycleRequest")]
        public static void EquipChipSiteAutoRecycleRequestHandler(Session session, Packet.Request packet)
        {
            EquipChipSiteAutoRecycleRequest request = packet.Deserialize<EquipChipSiteAutoRecycleRequest>();
            if (request.StarList is null
                || request.StarList.Distinct().Count() != request.StarList.Count
                || request.StarList.Any(star => star is < 1 or > 5)
                || request.Days is not (0 or 1 or 3 or 14))
            {
                session.SendResponse(new EquipChipSiteAutoRecycleResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.EquipChipAutoRecycleSite = new ChipRecycleSite
            {
                RecycleStar = request.StarList.Order().ToList(),
                Days = request.Days,
                SetRecycleTime = (int)Math.Min(
                    int.MaxValue,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            };
            session.player.Save();
            session.SendResponse(new EquipChipSiteAutoRecycleResponse(), packet.Id);
        }

        [RequestPacketHandler("EquipChipRecycleRequest")]
        public static void EquipChipRecycleRequestHandler(Session session, Packet.Request packet)
        {
            EquipChipRecycleRequest request = packet.Deserialize<EquipChipRecycleRequest>();
            if (!TryBuildChipRecycle(
                    session,
                    request.ChipIds,
                    out List<EquipData> chips,
                    out int recycleItemId,
                    out int recycleItemCount))
            {
                session.SendResponse(new EquipChipRecycleResponse { Code = 1 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            if (recycleItemCount > 0)
                notifyItems.ItemDataList.Add(session.inventory.Do(recycleItemId, recycleItemCount));

            NotifyEquipDataList notifyEquips = new();
            foreach (EquipData chip in chips)
            {
                if (!session.character.Equips.Remove(chip))
                    throw new InvalidDataException($"Validated recyclable chip {chip.Id} disappeared before consumption.");
                notifyEquips.DeletedEquipIdList.Add(chip.Id);
            }

            session.character.Save();
            session.inventory.Save();
            TaskModule.RecordEquipmentProgress(session, 12206, chips);
            if (notifyItems.ItemDataList.Count > 0)
                session.SendPush(notifyItems);
            session.SendPush(notifyEquips);
            session.SendResponse(new EquipChipRecycleResponse
            {
                RewardGoodsList = recycleItemCount > 0
                    ? [new RewardGoods
                    {
                        RewardType = (int)RewardType.Item,
                        TemplateId = recycleItemId,
                        Count = recycleItemCount
                    }]
                    : []
            }, packet.Id);
        }

        private static bool TryBuildChipRecycle(
            Session session,
            List<int>? chipIds,
            out List<EquipData> chips,
            out int recycleItemId,
            out int recycleItemCount)
        {
            chips = [];
            recycleItemId = 0;
            recycleItemCount = 0;
            if (chipIds is not { Count: > 0 } || chipIds.Distinct().Count() != chipIds.Count)
                return false;

            Dictionary<string, string> config = TableReaderV2.Parse<ConfigTable>()
                .Where(row => row.Key is "EquipRecycleItemId" or "EquipRecycleItemPercent" or "EquipExpInheritPercent")
                .ToDictionary(row => row.Key, row => row.Value);
            if (!int.TryParse(config.GetValueOrDefault("EquipRecycleItemId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out recycleItemId)
                || !int.TryParse(config.GetValueOrDefault("EquipRecycleItemPercent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int recyclePercent)
                || !int.TryParse(config.GetValueOrDefault("EquipExpInheritPercent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int inheritPercent)
                || recyclePercent < 0
                || inheritPercent < 0
                || !Inventory.IsValidClientItemId(recycleItemId))
            {
                return false;
            }

            IGrouping<uint, EquipData>[] equipGroups = session.character.Equips
                .GroupBy(equip => equip.Id)
                .ToArray();
            if (equipGroups.Any(group => group.Count() != 1))
                return false;
            Dictionary<uint, EquipData> equipsById = equipGroups
                .ToDictionary(group => group.Key, group => group.Single());
            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            decimal totalExp = 0;
            foreach (int chipId in chipIds)
            {
                EquipData? chip = chipId > 0 && equipsById.TryGetValue((uint)chipId, out EquipData? found)
                    ? found
                    : null;
                EquipTable? template = chip is null
                    ? null
                    : equipTables.FirstOrDefault(row => row.Id == chip.TemplateId);
                if (chip is null
                    || template is null
                    || template.Site <= 0
                    || template.Star > 5
                    || !Character.IsOwnableEquipTemplate(template)
                    || chip.CharacterId != 0
                    || chip.Level != 1
                    || chip.Exp != 0
                    || chip.Breakthrough != 0
                    || chip.IsLock
                    || chip.ResonanceInfo is { Count: > 0 }
                    || chip.UnconfirmedResonanceInfo is { Count: > 0 }
                    || chip.AwakeSlotList is { Count: > 0 }
                    || session.player.IsEquipInTeamPrefab(chip.Id))
                {
                    return false;
                }

                EquipBreakThroughTable? breakthrough = Character.ResolveEquipBreakThrough(chip.TemplateId, chip.Breakthrough);
                EquipLevelUpTemplate? level = breakthrough is null
                    ? null
                    : Character.equipLevelUpTemplates.FirstOrDefault(row =>
                        row.TemplateId == breakthrough.LevelUpTemplateId
                        && row.Level == chip.Level);
                if (breakthrough is null || level is null)
                    return false;
                totalExp += ((decimal)chip.Exp + level.AllExp) * inheritPercent / 100m + breakthrough.Exp;
                chips.Add(chip);
            }

            decimal countDecimal = decimal.Floor(totalExp * recyclePercent / 100m);
            int itemId = recycleItemId;
            ItemTable? recycleItem = TableReaderV2.Parse<ItemTable>()
                .FirstOrDefault(row => row.Id == itemId);
            long currentCount = session.inventory.Items
                .FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;
            if (countDecimal > int.MaxValue
                || currentCount > Inventory.GetMaxCount(recycleItem) - (long)countDecimal)
            {
                return false;
            }

            recycleItemCount = (int)countDecimal;
            return true;
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

            Character originalCharacter = session.character;
            Inventory originalInventory = session.inventory;
            Character stagedCharacter = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Character>(
                originalCharacter.ToBson());
            Inventory stagedInventory = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Inventory>(
                originalInventory.ToBson());
            NotifyItemDataList notifyItemData = new();
            NotifyEquipDataList notifyEquipData = new();
            bool characterPersisted = false;
            session.character = stagedCharacter;
            session.inventory = stagedInventory;
            try
            {
                foreach (IGrouping<int, EquipDecomposeReward> itemRewards in rewardSpecs
                             .Where(reward => reward.Type == RewardType.Item)
                             .GroupBy(reward => reward.TemplateId))
                {
                    notifyItemData.ItemDataList.Add(
                        stagedInventory.Do(itemRewards.Key, itemRewards.Sum(reward => reward.Count)));
                }

                foreach (EquipDecomposeReward reward in rewardSpecs.Where(reward => reward.Type == RewardType.Equip))
                {
                    for (int i = 0; i < reward.Count; i++)
                    {
                        EquipData? returnedEquip = stagedCharacter.AddEquip(
                            (uint)reward.TemplateId, level: Math.Max(1, reward.Level));
                        if (returnedEquip is null)
                            throw new InvalidDataException($"Unable to grant decomposed equipment template {reward.TemplateId}.");

                        notifyEquipData.EquipDataList.Add(returnedEquip);
                    }
                }

                foreach (uint sourceId in sourceEquips.Select(equip => equip.Id))
                {
                    EquipData? sourceEquip = stagedCharacter.Equips.SingleOrDefault(equip => equip.Id == sourceId);
                    if (sourceEquip is null || !stagedCharacter.Equips.Remove(sourceEquip))
                        throw new InvalidDataException($"Unable to remove decomposed equipment UID {sourceId}.");

                    notifyEquipData.DeletedEquipIdList.Add(sourceId);
                }

                stagedCharacter.SaveChecked();
                characterPersisted = true;
                stagedInventory.SaveChecked();
                CopyEquipDecomposeState(originalCharacter, stagedCharacter, originalInventory, stagedInventory);
            }
            catch
            {
                if (characterPersisted)
                    originalCharacter.SaveChecked();
                session.SendResponse(new EquipDecomposeResponse { Code = 1 }, packet.Id);
                return;
            }
            finally
            {
                session.character = originalCharacter;
                session.inventory = originalInventory;
            }

            TaskModule.RecordEquipmentProgress(session, 12206, sourceEquips);

            if (notifyItemData.ItemDataList.Count > 0)
                session.SendPush(notifyItemData);
            if (notifyEquipData.EquipDataList.Count > 0 || notifyEquipData.DeletedEquipIdList.Count > 0)
                session.SendPush(notifyEquipData);
            session.SendResponse(new EquipDecomposeResponse
            {
                Code = 0,
                RewardGoodsList = BuildEquipDecomposeResponseRewards(rewardSpecs)
            }, packet.Id);
        }

        private static void CopyEquipDecomposeState(
            Character originalCharacter,
            Character stagedCharacter,
            Inventory originalInventory,
            Inventory stagedInventory)
        {
            originalCharacter.Equips = stagedCharacter.Equips;
            originalInventory.Items = stagedInventory.Items;
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
            if (equipIds is null || equipIds.Count == 0)
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
            List<ItemTable> itemTables = TableReaderV2.Parse<ItemTable>();
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
                    || equip.IsRecycle
                    || session.player.IsEquipInTeamPrefab(equip.Id)
                    || session.player.IsEquipInChipGroup(equip.Id)
                    || equip.ResonanceInfo?.Count > 0
                    || equip.UnconfirmedResonanceInfo?.Count > 0
                    || session.PendingEquipResonances.Keys.Any(key => key.Item1 == equip.Id))
                    return false;

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

                ItemTable? foodItemTable = itemTables.FirstOrDefault(table => table.Id == decomposeTable.ExpToItemId);
                int foodExp = foodItemTable is null ? 0 : foodItemTable.GetEquipUpgradeInfo().Exp;
                if (foodExp <= 0)
                    return false;

                decimal foodCountDecimal;
                try
                {
                    foodCountDecimal = checked(
                        totalExp * returnRateConfig.Value
                        / (foodExp * 10_000m));
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

                    AddEquipDecomposeReward(
                        rewardByKey,
                        RewardType.Item,
                        decomposeTable.ExpToItemId,
                        foodCount,
                        level: 0,
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

            Dictionary<int, ItemTable> itemTablesById = itemTables.ToDictionary(table => table.Id);
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
                if (finalCount > Inventory.GetMaxCount(itemTable))
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