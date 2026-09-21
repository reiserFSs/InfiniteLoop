using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.partner.leveluptemplate;
using AscNet.Table.V2.share.archive;
using AscNet.Table.V2.share.config;
using ArchiveCondition = AscNet.Table.V2.share.condition.ConditionTable;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class PartnerComposeRequest
    {
        public List<int> TemplateIds;
        public bool IsOneKey;
    }

    [MessagePackObject(true)]
    public class PartnerComposeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerDecomposeRequest
    {
        public List<int> PartnerIds;
    }

    [MessagePackObject(true)]
    public class PartnerDecomposeResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList;
    }

    [MessagePackObject(true)]
    public class PartnerLevelUpRequest
    {
        public int PartnerId;
        public Dictionary<int, int> UseItems;
    }

    [MessagePackObject(true)]
    public class PartnerLevelUpResponse
    {
        public int Level;
        public int Exp;
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerBreakThroughRequest
    {
        public int PartnerId;
    }

    [MessagePackObject(true)]
    public class PartnerBreakThroughResponse
    {
        public int Code;
        public int BreakTimes;
    }

    [MessagePackObject(true)]
    public class PartnerSkillUpRequest
    {
        public int PartnerId;
        public int SkillId;
        public int Times;
    }

    [MessagePackObject(true)]
    public class PartnerSkillUpInfo
    {
        public int SkillId;
        public int OriginLevel;
        public int CurrentLevel;
    }

    [MessagePackObject(true)]
    public class PartnerSkillUpResponse
    {
        public int Code;
        public List<PartnerSkillUpInfo> SkillUpInfo;
    }

    [MessagePackObject(true)]
    public class PartnerSkillWearRequest
    {
        public int PartnerId;
        public List<PartnerSkillWearInfo> SkillIdToWear;
        public int SkillType;
    }

    [MessagePackObject(true)]
    public class PartnerSkillWearInfo
    {
        public bool IsWear;
        public int SkillId;
    }

    [MessagePackObject(true)]
    public class PartnerSkillWearResponse
    {
        public int Code;
    }
    [MessagePackObject(true)]
    public class PartnerStarActivateRequest
    {
        public int PartnerId;
        public List<int> UsePartnerIdList = new();
    }

    [MessagePackObject(true)]
    public class PartnerStarActivateResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerEvolutionRequest
    {
        public int PartnerId;
    }

    [MessagePackObject(true)]
    public class PartnerEvolutionResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerCarryRequest
    {
        public int PartnerId;
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class PartnerCarryResponse
    {
        public int Code;
    }
    [MessagePackObject(true)]
    public class PartnerBreakAwayRequest
    {
        public int PartnerId;
    }

    [MessagePackObject(true)]
    public class PartnerBreakAwayResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class PartnerUpdateLockRequest
    {
        public int PartnerId;
        public bool IsLock;
    }

    [MessagePackObject(true)]
    public class PartnerUpdateLockResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class NotifyArchivePartners
    {
        public List<int> PartnerUnlockIds { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class NotifyPartnerSettings
    {
        public List<int> PartnerSettings { get; set; } = new();
    }


#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class PartnerModule
    {
        private const int ErrorCode = 1;
        private static readonly Lazy<Dictionary<int, ArchiveCondition>> ArchiveConditions = new(() =>
            TableReaderV2.Parse<ArchiveCondition>().ToDictionary(row => row.Id));

        internal static bool RefreshArchive(Player player, Character character)
        {
            bool changed = false;
            foreach (PartnerData partner in character.Partners)
            {
                if (!player.ArchivePartnerUnlockIds.Contains(partner.TemplateId))
                {
                    player.ArchivePartnerUnlockIds.Add(partner.TemplateId);
                    changed = true;
                }
            }
            foreach (PartnerSettingTable setting in TableReaderV2.Parse<PartnerSettingTable>())
            {
                if (player.ArchivePartnerSettings.Contains(setting.Id)
                    || !player.ArchivePartnerUnlockIds.Contains(setting.GroupId)
                    || !MeetsArchiveCondition(character, setting.Condition))
                    continue;
                player.ArchivePartnerSettings.Add(setting.Id);
                changed = true;
            }
            return changed;
        }

        private static bool MeetsArchiveCondition(Character character, int conditionId)
        {
            if (conditionId == 0)
                return true;
            if (!ArchiveConditions.Value.TryGetValue(conditionId, out ArchiveCondition? condition))
                return false;
            List<int> parameters = condition.Params;
            return character.Partners.Any(partner => parameters.Count > 0
                && partner.TemplateId == parameters[0]
                && (condition.Type switch
                {
                    // An overclock resets level; a later overclock has already passed the earlier tier.
                    10136 => parameters.Count >= 3 && (partner.BreakThrough > parameters[1]
                        || partner.BreakThrough == parameters[1] && partner.Level >= parameters[2]),
                    10137 => parameters.Count >= 2 && partner.Quality >= parameters[1],
                    // XPartner:GetTotalSkillLevel counts one shared main level and every passive.
                    10138 => parameters.Count >= 2
                        && (partner.SkillList.FirstOrDefault(skill => skill.Type == 1)?.Level ?? 0)
                            + partner.SkillList.Where(skill => skill.Type == 2).Sum(skill => skill.Level) >= parameters[1],
                    _ => false
                }));
        }

        internal static void SyncArchive(Session session)
        {
            int partnerCount = session.player.ArchivePartnerUnlockIds.Count;
            int settingCount = session.player.ArchivePartnerSettings.Count;
            if (!RefreshArchive(session.player, session.character))
                return;
            try
            {
                session.player.Save();
            }
            catch
            {
                session.player.ArchivePartnerUnlockIds.RemoveRange(partnerCount,
                    session.player.ArchivePartnerUnlockIds.Count - partnerCount);
                session.player.ArchivePartnerSettings.RemoveRange(settingCount,
                    session.player.ArchivePartnerSettings.Count - settingCount);
                throw;
            }
            if (session.player.ArchivePartnerUnlockIds.Count > partnerCount)
                session.SendPush(new NotifyArchivePartners
                {
                    PartnerUnlockIds = session.player.ArchivePartnerUnlockIds.Skip(partnerCount).ToList()
                });
            if (session.player.ArchivePartnerSettings.Count > settingCount)
                session.SendPush(new NotifyPartnerSettings
                {
                    PartnerSettings = session.player.ArchivePartnerSettings.Skip(settingCount).ToList()
                });
        }


        [RequestPacketHandler("PartnerComposeRequest")]
        public static void PartnerComposeRequestHandler(Session session, Packet.Request packet)
        {
            PartnerComposeRequest request = packet.Deserialize<PartnerComposeRequest>();
            List<int> requestedIds = request.TemplateIds ?? [];
            List<(int TemplateId, int ShardId, int ShardCount)> compositions = requestedIds
                .Distinct()
                .Select(ResolveComposition)
                .Where(composition => composition is not null)
                .Select(composition => composition!.Value)
                .ToList();
            if (compositions.Count == 0 || compositions.Count != requestedIds.Distinct().Count())
            {
                session.SendResponse(new PartnerComposeResponse { Code = 1 }, packet.Id);
                return;
            }

            Dictionary<int, int> shardCosts = compositions
                .GroupBy(composition => composition.ShardId)
                .ToDictionary(group => group.Key, group => checked(group.Sum(composition => composition.ShardCount)));
            if (shardCosts.Any(cost =>
                    (session.inventory.Items.FirstOrDefault(item => item.Id == cost.Key)?.Count ?? 0) < cost.Value))
            {
                session.SendResponse(new PartnerComposeResponse { Code = 1 }, packet.Id);
                return;
            }

            session.character.Partners ??= [];
            int nextPartnerId = session.character.Partners.Count == 0
                ? 1
                : checked(session.character.Partners.Max(partner => partner.Id) + 1);
            List<PartnerData> partners = compositions
                .Select(composition => CreatePartner(nextPartnerId++, composition.TemplateId))
                .ToList();

            NotifyItemDataList notifyItems = new();
            foreach ((int shardId, int count) in shardCosts)
                notifyItems.ItemDataList.Add(session.inventory.Do(shardId, -count));
            session.SendPush(notifyItems);

            session.character.Partners.AddRange(partners);
            session.inventory.Save();
            session.character.Save();
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = partners,
                OperateTypes = Enumerable.Repeat(1, partners.Count).ToList()
            });
            SyncArchive(session);
            session.SendResponse(new PartnerComposeResponse(), packet.Id);
        }

        // ponytail: keep the latest 16 receipts; add a request journal only if this replay window proves insufficient.
        private const int MaxPartnerDecomposeCompletions = 16;

        [RequestPacketHandler("PartnerDecomposeRequest")]
        public static void PartnerDecomposeRequestHandler(Session session, Packet.Request packet)
        {
            PartnerDecomposeRequest request = packet.Deserialize<PartnerDecomposeRequest>();
            if (session.player.PendingItemUse is not null || session.player.PendingPurchase is not null)
            {
                session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                return;
            }

            List<int> ids = request.PartnerIds ?? [];
            if (session.player.PendingPartnerDecompose is null)
            {
                try
                {
                    if (TryReplayCompletedPartnerDecompose(session, packet, ids))
                        return;
                }
                catch
                {
                    session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                    return;
                }
            }

            PartnerDecomposePendingOperation? pending = session.player.PendingPartnerDecompose;
            if (pending is not null)
            {
                if (!pending.PartnerIds.SequenceEqual(ids))
                {
                    session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                    return;
                }
            }
            else
            {
                List<PartnerData> partners = session.character.Partners?
                    .Where(partner => ids.Contains(partner.Id))
                    .ToList() ?? [];
                if (ids.Count == 0 || ids.Distinct().Count() != ids.Count || partners.Count != ids.Count
                    || partners.Any(partner => partner.IsLock || partner.CharacterId != 0
                        || (session.player.TeamPrefabs?.Any(prefab => prefab?.PartnerData?.Values
                            .Any(data => data?.PartnerId == partner.Id) == true) ?? false))
                    || !TryGetPartnerDecomposeRewards(partners, out List<RewardGoods> rewards))
                {
                    session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                    return;
                }

                Dictionary<int, ItemTable> itemTables = TableReaderV2.Parse<ItemTable>()
                    .ToDictionary(item => item.Id);
                Dictionary<int, long> inventoryCounts = session.inventory.Items
                    .GroupBy(item => item.Id)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
                foreach (IGrouping<int, RewardGoods> rewardGroup in rewards.GroupBy(reward => reward.TemplateId))
                {
                    if (!itemTables.TryGetValue(rewardGroup.Key, out ItemTable? itemTable)
                        || !Inventory.IsValidClientItemId(rewardGroup.Key))
                    {
                        session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                        return;
                    }

                    long rewardCount = rewardGroup.Sum(reward => (long)reward.Count);
                    long currentCount = inventoryCounts.GetValueOrDefault(rewardGroup.Key);
                    // AscNet policy: reject grants that exceed the authoritative inventory headroom.
                    if (rewardCount <= 0 || currentCount < 0
                        || rewardCount > Inventory.GetMaxCount(itemTable) - currentCount)
                    {
                        session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                        return;
                    }
                }

                pending = new PartnerDecomposePendingOperation
                {
                    ClaimKey = $"partner-decompose:{session.player.PlayerData.Id}:{Guid.NewGuid():N}",
                    PartnerIds = ids.ToList(),
                    RewardGoodsList = rewards.Select(reward => new RewardGoods
                    {
                        Id = reward.Id,
                        RewardType = reward.RewardType,
                        TemplateId = reward.TemplateId,
                        Count = reward.Count
                    }).ToList()
                };
                session.player.PendingPartnerDecompose = pending;
            }
            PartnerDecomposePendingOperation operation = pending!;

            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                return;
            }

            RewardApplicationResult result;
            try
            {
                result = CompletePendingPartnerDecompose(session);
            }
            catch
            {
                session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                return;
            }

            result.SendPushes(session);
            session.SendResponse(new PartnerDecomposeResponse
            {
                RewardGoodsList = operation.RewardGoodsList
            }, packet.Id);
        }

        public static void ResumePendingPartnerDecompose(Session session)
        {
            if (session.player.PendingPartnerDecompose is not null)
                CompletePendingPartnerDecompose(session);
        }
        private static bool TryReplayCompletedPartnerDecompose(
            Session session,
            Packet.Request packet,
            IReadOnlyList<int> ids)
        {
            if (ids.Count == 0
                || session.player.PartnerDecomposeCompletions is not { Count: > 0 } completions)
                return false;

            PartnerDecomposeCompletion? completion = completions.LastOrDefault(
                value => value.PartnerIds.SequenceEqual(ids));
            if (completion is null
                || session.character.Partners?.Any(partner => ids.Contains(partner.Id)) == true)
                return false;

            if (!session.character.AppliedRewardClaims.Contains(completion.ClaimKey, StringComparer.Ordinal)
                || !session.inventory.AppliedRewardClaims.Contains(completion.ClaimKey, StringComparer.Ordinal))
            {
                session.SendResponse(new PartnerDecomposeResponse { Code = ErrorCode, RewardGoodsList = [] }, packet.Id);
                return true;
            }

            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(completion.ClaimKey, completion.RewardGoodsList.Select(reward => new RewardGoodsTable
                {
                    Id = reward.Id,
                    TemplateId = reward.TemplateId,
                    Count = reward.Count,
                    Params = []
                }).ToList())], session);
            result.SendPushes(session);
            session.SendResponse(new PartnerDecomposeResponse
            {
                RewardGoodsList = completion.RewardGoodsList.Select(reward => new RewardGoods
                {
                    Id = reward.Id,
                    RewardType = reward.RewardType,
                    TemplateId = reward.TemplateId,
                    Count = reward.Count
                }).ToList()
            }, packet.Id);
            return true;
        }

        private static RewardApplicationResult CompletePendingPartnerDecompose(Session session)
        {
            PartnerDecomposePendingOperation pending = session.player.PendingPartnerDecompose
                ?? throw new InvalidOperationException("No pending partner decomposition.");
            Character originalCharacter = session.character;
            Character stagedCharacter = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Character>(
                originalCharacter.ToBsonDocument());
            stagedCharacter.AppliedRewardClaims ??= [];
            bool characterClaimed = stagedCharacter.AppliedRewardClaims.Contains(
                pending.ClaimKey, StringComparer.Ordinal);
            if (characterClaimed)
            {
                if (pending.PartnerIds.Any(id => stagedCharacter.Partners?.Any(partner => partner.Id == id) == true))
                    throw new InvalidDataException($"Partner decomposition claim {pending.ClaimKey} contradicts persisted partners.");
            }
            else
            {
                foreach (int id in pending.PartnerIds)
                {
                    PartnerData? partner = stagedCharacter.Partners?.SingleOrDefault(value => value.Id == id);
                    if (partner is null || !stagedCharacter.Partners!.Remove(partner))
                        throw new InvalidDataException($"Unable to remove decomposed partner {id}.");
                }
                stagedCharacter.AppliedRewardClaims.Add(pending.ClaimKey);
                stagedCharacter.SaveChecked();
            }

            originalCharacter.Partners = stagedCharacter.Partners;
            originalCharacter.AppliedRewardClaims = stagedCharacter.AppliedRewardClaims;
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(pending.ClaimKey, pending.RewardGoodsList.Select(reward => new RewardGoodsTable
                {
                    Id = reward.Id,
                    TemplateId = reward.TemplateId,
                    Count = reward.Count,
                    Params = []
                }).ToList())], session);

            List<PartnerDecomposeCompletion>? previousCompletions =
                session.player.PartnerDecomposeCompletions;
            List<PartnerDecomposeCompletion> completions = (previousCompletions ?? [])
                .Where(completion => completion.ClaimKey != pending.ClaimKey)
                .ToList();
            completions.Add(new PartnerDecomposeCompletion
            {
                ClaimKey = pending.ClaimKey,
                PartnerIds = pending.PartnerIds.ToList(),
                RewardGoodsList = pending.RewardGoodsList.Select(reward => new RewardGoods
                {
                    Id = reward.Id,
                    RewardType = reward.RewardType,
                    TemplateId = reward.TemplateId,
                    Count = reward.Count
                }).ToList()
            });
            if (completions.Count > MaxPartnerDecomposeCompletions)
                completions.RemoveRange(0, completions.Count - MaxPartnerDecomposeCompletions);

            session.player.PartnerDecomposeCompletions = completions;
            session.player.PendingPartnerDecompose = null;
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.player.PartnerDecomposeCompletions = previousCompletions;
                session.player.PendingPartnerDecompose = pending;
                throw;
            }
            return result;
        }

        [RequestPacketHandler("PartnerLevelUpRequest")]
        public static void PartnerLevelUpRequestHandler(Session session, Packet.Request packet)
        {
            PartnerLevelUpRequest request = packet.Deserialize<PartnerLevelUpRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerBreakThroughTable? breakthrough = partner is null ? null : FindBreakthrough(partner);
            if (partner is null || breakthrough is null || partner.Level >= breakthrough.LevelLimit)
            {
                session.SendResponse(new PartnerLevelUpResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            Dictionary<int, int> requestedItems = request.UseItems ?? [];
            List<(int ItemId, int Count, int Exp, int CoinCost)> materials = requestedItems
                .Where(item => item.Value > 0)
                .Select(item =>
                {
                    ItemTable? config = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == item.Key);
                    int exp = config?.SubTypeParams.FirstOrDefault() ?? 0;
                    int coinCost = config?.SubTypeParams.Skip(1).FirstOrDefault() ?? 0;
                    return (ItemId: item.Key, Count: item.Value, Exp: exp, CoinCost: coinCost);
                })
                .Where(item => item.ItemId is 30111 or 30113 && item.Exp > 0)
                .ToList();
            int totalCoinCost = materials.Sum(item => checked(item.CoinCost * item.Count));
            if (materials.Count == 0 || materials.Count != requestedItems.Count
                || ItemCount(session, Inventory.Coin) < totalCoinCost
                || materials.Any(item => ItemCount(session, item.ItemId) < item.Count))
            {
                session.SendResponse(new PartnerLevelUpResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            List<(int Level, int AllExp)> levels = GetLevelTemplate(breakthrough.LevelUpTemplateId);
            int currentAllExp = levels.First(row => row.Level == partner.Level).AllExp;
            int totalExp = checked(currentAllExp + partner.Exp
                + materials.Sum(item => checked(item.Exp * item.Count)));
            int newLevel = levels.Where(row => row.AllExp <= totalExp).Select(row => row.Level).DefaultIfEmpty(1).Max();
            newLevel = Math.Min(newLevel, breakthrough.LevelLimit);
            int expAtLevel = levels.First(row => row.Level == newLevel).AllExp;
            partner.Level = newLevel;
            partner.Exp = newLevel == breakthrough.LevelLimit ? 0 : totalExp - expAtLevel;

            NotifyItemDataList notifyItems = new();
            if (totalCoinCost > 0)
                notifyItems.ItemDataList.Add(session.inventory.Do(Inventory.Coin, -totalCoinCost));
            foreach ((int itemId, int count, _, _) in materials)
                notifyItems.ItemDataList.Add(session.inventory.Do(itemId, -count));
            session.SendPush(notifyItems);
            session.inventory.Save();
            session.character.Save();
            SyncArchive(session);
            session.SendResponse(new PartnerLevelUpResponse
            {
                Level = partner.Level,
                Exp = partner.Exp
            }, packet.Id);
        }

        [RequestPacketHandler("PartnerBreakThroughRequest")]
        public static void PartnerBreakThroughRequestHandler(Session session, Packet.Request packet)
        {
            PartnerBreakThroughRequest request = packet.Deserialize<PartnerBreakThroughRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerBreakThroughTable? config = partner is null ? null : FindBreakthrough(partner);
            if (partner is null || config is null || partner.Level != config.LevelLimit ||
                config.CostItemId.Count == 0 || config.CostItemId
                    .Zip(config.CostItemCount)
                    .Any(cost => ItemCount(session, cost.First) < cost.Second))
            {
                session.SendResponse(new PartnerBreakThroughResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            foreach ((int itemId, int count) in config.CostItemId.Zip(config.CostItemCount))
                notifyItems.ItemDataList.Add(session.inventory.Do(itemId, -count));
            partner.BreakThrough++;
            partner.Level = 1;
            partner.Exp = 0;
            session.SendPush(notifyItems);
            session.inventory.Save();
            session.character.Save();
            SyncArchive(session);
            session.SendResponse(new PartnerBreakThroughResponse
            {
                BreakTimes = partner.BreakThrough
            }, packet.Id);
        }

        [RequestPacketHandler("PartnerSkillUpRequest")]
        public static void PartnerSkillUpRequestHandler(Session session, Packet.Request packet)
        {
            PartnerSkillUpRequest request = packet.Deserialize<PartnerSkillUpRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerSkillData? skill = partner?.SkillList.Find(row => row.Id == request.SkillId);
            PartnerSkillTable? config = partner is null
                ? null
                : TableReaderV2.Parse<PartnerSkillTable>().Find(row => row.PartnerId == partner.TemplateId);
            int times = request.Times;
            if (partner is null || skill is null || config is null || times <= 0 ||
                config.UpgradeCostItemId.Count != config.UpgradeCostItemCount.Count)
            {
                session.SendResponse(new PartnerSkillUpResponse { Code = ErrorCode, SkillUpInfo = [] }, packet.Id);
                return;
            }

            int maxLevel = TableReaderV2.Parse<PartnerSkillEffectTable>()
                .Where(effect => effect.SkillId == skill.Id)
                .Select(effect => effect.Level)
                .DefaultIfEmpty()
                .Max();
            if (maxLevel == 0 || skill.Level > maxLevel - times)
            {
                session.SendResponse(new PartnerSkillUpResponse { Code = ErrorCode, SkillUpInfo = [] }, packet.Id);
                return;
            }

            List<(int ItemId, int Count)> costs = config.UpgradeCostItemId.Zip(config.UpgradeCostItemCount)
                .Select(cost => (cost.First, checked(cost.Second * times)))
                .ToList();
            if (costs.Any(cost => ItemCount(session, cost.ItemId) < cost.Count))
            {
                session.SendResponse(new PartnerSkillUpResponse { Code = ErrorCode, SkillUpInfo = [] }, packet.Id);
                return;
            }

            int originLevel = skill.Level;
            NotifyItemDataList notifyItems = new();
            foreach ((int itemId, int count) in costs)
                notifyItems.ItemDataList.Add(session.inventory.Do(itemId, -count));
            skill.Level = checked(skill.Level + times);
            session.SendPush(notifyItems);
            session.inventory.Save();
            session.character.Save();
            SendPartnerUpdate(session, partner);
            SyncArchive(session);
            session.SendResponse(new PartnerSkillUpResponse
            {
                SkillUpInfo =
                [
                    new PartnerSkillUpInfo
                    {
                        SkillId = skill.Id,
                        OriginLevel = originLevel,
                        CurrentLevel = skill.Level
                    }
                ]
            }, packet.Id);
        }

        [RequestPacketHandler("PartnerSkillWearRequest")]
        public static void PartnerSkillWearRequestHandler(Session session, Packet.Request packet)
        {
            PartnerSkillWearRequest request = packet.Deserialize<PartnerSkillWearRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            if (partner is null || request.SkillType is not (1 or 2)
                || !TryNormalizeSkillWearPlan(request.SkillIdToWear, out Dictionary<int, bool> plan))
            {
                session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            if (request.SkillType == 1)
            {
                if (plan.Count != 1)
                {
                    session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                    return;
                }

                int mainSkillId = 0;
                bool hasMainSkill = false;
                foreach ((int skillId, bool isWear) in plan)
                {
                    if (!isWear)
                        continue;
                    if (hasMainSkill)
                    {
                        session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                        return;
                    }
                    mainSkillId = skillId;
                    hasMainSkill = true;
                }

                HashSet<int> available = TableReaderV2.Parse<PartnerMainSkillGroupTable>()
                    .Where(group => partner.UnlockSkillGroup.Contains(group.Id))
                    .SelectMany(group => group.SkillId)
                    .ToHashSet();
                PartnerSkillData? current = partner.SkillList.Find(skill => skill.Type == 1);
                if (!hasMainSkill || !available.Contains(mainSkillId) || current is null)
                {
                    session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                    return;
                }

                current.Id = mainSkillId;
                current.IsWear = true;
            }
            else
            {
                PartnerQualityTable? qualityConfig = null;
                foreach (PartnerQualityTable row in TableReaderV2.Parse<PartnerQualityTable>())
                {
                    if (row.PartnerId != partner.TemplateId || row.Quality != partner.Quality)
                        continue;
                    if (qualityConfig is not null)
                    {
                        session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                        return;
                    }
                    qualityConfig = row;
                }

                if (qualityConfig is null || qualityConfig.SkillColumnCount < 0)
                {
                    session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                    return;
                }

                int wornCount = partner.SkillList.Count(skill => skill.Type == 2 && skill.IsWear);
                foreach ((int skillId, bool isWear) in plan)
                {
                    PartnerSkillData? resolved = null;
                    foreach (PartnerSkillData skill in partner.SkillList)
                    {
                        if (skill.Type != 2 || skill.Id != skillId)
                            continue;
                        if (resolved is not null)
                        {
                            session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                            return;
                        }
                        resolved = skill;
                    }

                    if (resolved is null)
                    {
                        session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                        return;
                    }
                    if (resolved.IsWear != isWear)
                        wornCount += isWear ? 1 : -1;
                }

                if (wornCount > qualityConfig.SkillColumnCount)
                {
                    session.SendResponse(new PartnerSkillWearResponse { Code = ErrorCode }, packet.Id);
                    return;
                }

                foreach ((int skillId, bool isWear) in plan)
                    partner.SkillList.Find(skill => skill.Type == 2 && skill.Id == skillId)!.IsWear = isWear;
            }

            SendPartnerUpdate(session, partner);
            session.character.Save();
            session.SendResponse(new PartnerSkillWearResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerStarActivateRequest")]
        public static void PartnerStarActivateRequestHandler(Session session, Packet.Request packet)
        {
            PartnerStarActivateRequest request = packet.Deserialize<PartnerStarActivateRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            List<int> materialIds = ReadMaterialPartnerIds(packet.Content);
            List<PartnerData> materials = session.character.Partners?
                .Where(candidate => materialIds.Contains(candidate.Id))
                .ToList() ?? [];
            PartnerQualityTable? qualityConfig = partner is null
                ? null
                : TableReaderV2.Parse<PartnerQualityTable>()
                    .Find(row => row.PartnerId == partner.TemplateId && row.Quality == partner.Quality);
            int targetSchedule = qualityConfig?.StarCostChipCount.LastOrDefault(value => value > 0) ?? 0;
            if (partner is null || qualityConfig is null || materialIds.Count == 0
                || materials.Count != materialIds.Count
                || materials.Any(material => material.Id == partner.Id
                    || material.TemplateId != partner.TemplateId
                    || material.IsLock
                    || material.CharacterId != 0)
                || partner.StarSchedule < 0 || partner.StarSchedule % 30 != 0
                || partner.StarSchedule >= targetSchedule
                || materials.Count > (targetSchedule - partner.StarSchedule) / 30)
            {
                session.SendResponse(new PartnerStarActivateResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            foreach (PartnerData material in materials)
                session.character.Partners!.Remove(material);
            partner.StarSchedule += materials.Count * 30;
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = [.. materials, partner],
                OperateTypes = [.. Enumerable.Repeat(3, materials.Count), 2]
            });
            session.character.Save();
            session.SendResponse(new PartnerStarActivateResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerEvolutionRequest")]
        public static void PartnerEvolutionRequestHandler(Session session, Packet.Request packet)
        {
            PartnerEvolutionRequest request = packet.Deserialize<PartnerEvolutionRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerQualityTable? qualityConfig = partner is null
                ? null
                : TableReaderV2.Parse<PartnerQualityTable>()
                    .Find(row => row.PartnerId == partner.TemplateId && row.Quality == partner.Quality);
            bool hasNextQuality = partner is not null && TableReaderV2.Parse<PartnerQualityTable>()
                .Any(row => row.PartnerId == partner.TemplateId && row.Quality == partner.Quality + 1);
            int evolutionItemId = qualityConfig?.EvolutionCostItemId ?? 0;
            int evolutionItemCount = qualityConfig?.EvolutionCostItemCount ?? 0;
            if (partner is null || qualityConfig is null || !hasNextQuality
                || partner.StarSchedule != qualityConfig.StarCostChipCount.LastOrDefault(value => value > 0)
                || evolutionItemId <= 0 || evolutionItemCount <= 0
                || ItemCount(session, evolutionItemId) < evolutionItemCount)
            {
                session.SendResponse(new PartnerEvolutionResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            notifyItems.ItemDataList.Add(session.inventory.Do(evolutionItemId, -evolutionItemCount));
            partner.Quality++;
            session.SendPush(notifyItems);
            session.inventory.Save();
            session.character.Save();
            SendPartnerUpdate(session, partner);
            SyncArchive(session);
            session.SendResponse(new PartnerEvolutionResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerUpdateLockRequest")]
        public static void PartnerUpdateLockRequestHandler(Session session, Packet.Request packet)
        {
            PartnerUpdateLockRequest request = packet.Deserialize<PartnerUpdateLockRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            if (partner is null)
            {
                session.SendResponse(new PartnerUpdateLockResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            partner.IsLock = request.IsLock;
            session.character.Save();
            session.SendResponse(new PartnerUpdateLockResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerBreakAwayRequest")]
        public static void PartnerBreakAwayRequestHandler(Session session, Packet.Request packet)
        {
            PartnerBreakAwayRequest request = packet.Deserialize<PartnerBreakAwayRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerTable? partnerConfig = partner is null
                ? null
                : TableReaderV2.Parse<PartnerTable>().Find(row => row.Id == partner.TemplateId);
            if (partner is null || partnerConfig is null || partner.CharacterId == 0)
            {
                session.SendResponse(new PartnerBreakAwayResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            partner.CharacterId = 0;
            session.character.NormalizePartnerMainSkillForCarrier(partner);
            session.character.Save();
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = [partner],
                OperateTypes = [3]
            });
            session.SendResponse(new PartnerBreakAwayResponse(), packet.Id);
        }

        [RequestPacketHandler("PartnerCarryRequest")]
        public static void PartnerCarryRequestHandler(Session session, Packet.Request packet)
        {
            PartnerCarryRequest request = packet.Deserialize<PartnerCarryRequest>();
            PartnerData? partner = FindPartner(session, request.PartnerId);
            PartnerTable? partnerConfig = partner is null
                ? null
                : TableReaderV2.Parse<PartnerTable>().Find(row => row.Id == partner.TemplateId);
            bool ownsCharacter = request.CharacterId == 0
                || session.character.Characters.Any(character => character.Id == request.CharacterId);
            if (partner is null || partnerConfig is null || !ownsCharacter)
            {
                session.SendResponse(new PartnerCarryResponse { Code = ErrorCode }, packet.Id);
                return;
            }

            int previousCharacterId = partner.CharacterId;
            PartnerData? displaced = request.CharacterId == 0
                ? null
                : session.character.Partners?
                    .FirstOrDefault(candidate => candidate.Id != partner.Id
                        && candidate.CharacterId == request.CharacterId);
            if (displaced is not null)
                displaced.CharacterId = previousCharacterId;
            partner.CharacterId = request.CharacterId;
            if (displaced is not null)
                session.character.NormalizePartnerMainSkillForCarrier(displaced);
            session.character.NormalizePartnerMainSkillForCarrier(partner);

            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = displaced is null ? [partner] : [displaced, partner],
                OperateTypes = displaced is null ? [3] : [2, 3]
            });
            session.character.Save();
            session.SendResponse(new PartnerCarryResponse(), packet.Id);
        }

        private static List<int> ReadMaterialPartnerIds(byte[] content)
        {
            MessagePackReader reader = new(content);
            int fieldCount = reader.ReadMapHeader();
            for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                string? fieldName = reader.ReadString();
                if (fieldName != nameof(PartnerStarActivateRequest.PartnerId)
                    && reader.NextMessagePackType == MessagePackType.Array)
                {
                    int count = reader.ReadArrayHeader();
                    List<int> ids = new(count);
                    for (int index = 0; index < count; index++)
                        ids.Add(reader.ReadInt32());
                    return ids;
                }
                reader.Skip();
            }
            return [];
        }


        private static PartnerData? FindPartner(Session session, int partnerId) =>
            session.character.Partners?.Find(partner => partner.Id == partnerId);
        internal static int AllocatePartnerId(AscNet.Common.Database.Character character)
        {
            character.Partners ??= [];
            return character.Partners.Count == 0
                ? 1
                : checked(character.Partners.Max(partner => partner.Id) + 1);
        }

        private static PartnerBreakThroughTable? FindBreakthrough(PartnerData partner) =>
            TableReaderV2.Parse<PartnerBreakThroughTable>()
                .Find(row => row.PartnerId == partner.TemplateId && row.BreakTimes == partner.BreakThrough);

        private static long ItemCount(Session session, int itemId) =>
            session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;

        private static bool TryNormalizeSkillWearPlan(
            List<PartnerSkillWearInfo>? requested,
            out Dictionary<int, bool> plan)
        {
            if (requested is null || requested.Count == 0)
            {
                plan = null!;
                return false;
            }
            plan = new Dictionary<int, bool>(requested.Count);

            foreach (PartnerSkillWearInfo wear in requested)
            {
                if (plan.TryGetValue(wear.SkillId, out bool isWear))
                {
                    if (isWear != wear.IsWear)
                        return false;
                    continue;
                }
                plan.Add(wear.SkillId, wear.IsWear);
            }
            return true;
        }

        private static void SendPartnerUpdate(Session session, PartnerData partner) =>
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = [partner],
                OperateTypes = [2]
            });

        private static List<(int Level, int AllExp)> GetLevelTemplate(int templateId) => templateId switch
        {
            501 => TableReaderV2.Parse<PartnerLevelUpTemplate501Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            502 => TableReaderV2.Parse<PartnerLevelUpTemplate502Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            503 => TableReaderV2.Parse<PartnerLevelUpTemplate503Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            504 => TableReaderV2.Parse<PartnerLevelUpTemplate504Table>().Select(row => (row.Level, row.AllExp)).ToList(),
            _ => []
        };

        // Authoritative producer: EN client XPartnerManager.GetPartnerDecomposeRewards; server rewards are table-driven.
        private static bool TryGetPartnerDecomposeRewards(
            IReadOnlyList<PartnerData> partners,
            out List<RewardGoods> rewards)
        {
            Dictionary<string, string> config = TableReaderV2.Parse<ConfigTable>()
                .Where(row => row.Key is "PartnerDecomposeExpItemRebate"
                    or "PartnerDecomposeLevelBreakRebate"
                    or "PartnerDecomposeEvolutionRebate"
                    or "PartnerDecomposeSkillRebate")
                .ToDictionary(row => row.Key, row => row.Value);
            if (!float.TryParse(config.GetValueOrDefault("PartnerDecomposeLevelBreakRebate"),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out float levelRateValue)
                || !float.TryParse(config.GetValueOrDefault("PartnerDecomposeEvolutionRebate"),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out float evolutionRateValue)
                || !float.TryParse(config.GetValueOrDefault("PartnerDecomposeSkillRebate"),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out float skillRateValue))
            {
                rewards = [];
                return false;
            }
            // Client config crosses a float boundary; Lua widens to double and floors once per item.
            double levelRate = levelRateValue;
            double evolutionRate = evolutionRateValue;
            double skillRate = skillRateValue;
            Dictionary<int, ItemTable> items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
            List<(int Id, int Exp, int Coin)> expItems = (config.GetValueOrDefault(
                    "PartnerDecomposeExpItemRebate") ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out int id) ? id : 0)
                .Where(id => id > 0 && items.TryGetValue(id, out _))
                .Select(id => (Id: id, Exp: items[id].SubTypeParams.FirstOrDefault(),
                    Coin: items[id].SubTypeParams.Skip(1).FirstOrDefault()))
                .Where(item => item.Exp > 0)
                .OrderByDescending(item => item.Exp)
                .ToList();
            Dictionary<int, PartnerTable> partnerRows = TableReaderV2.Parse<PartnerTable>()
                .ToDictionary(row => row.Id);
            ILookup<int, PartnerBreakThroughTable> breakthroughs = TableReaderV2.Parse<PartnerBreakThroughTable>()
                .ToLookup(row => row.PartnerId);
            ILookup<int, PartnerQualityTable> qualities = TableReaderV2.Parse<PartnerQualityTable>()
                .ToLookup(row => row.PartnerId);
            Dictionary<int, PartnerSkillTable> skills = TableReaderV2.Parse<PartnerSkillTable>()
                .ToDictionary(row => row.PartnerId);
            Dictionary<int, double> totals = [];
            void Add(int itemId, double count)
            {
                if (itemId > 0 && count > 0 && items.ContainsKey(itemId))
                    totals[itemId] = totals.GetValueOrDefault(itemId) + count;
            }

            foreach (PartnerData partner in partners)
            {
                PartnerTable? partnerRow = partnerRows.GetValueOrDefault(partner.TemplateId);
                PartnerSkillTable? skillRow = skills.GetValueOrDefault(partner.TemplateId);
                if (partnerRow is null || skillRow is null || partner.Level < 1 || partner.Exp < 0
                    || partner.BreakThrough < 0 || partner.Quality < partnerRow.InitQuality
                    || partner.StarSchedule < 0 || partner.SkillList.Count == 0
                    || partner.SkillList.Any(skill => skill.Level < 1)
                    || partner.SkillList.Count(skill => skill.Type == 1) != 1
                    || skillRow.UpgradeCostItemId.Count != skillRow.UpgradeCostItemCount.Count)
                {
                    rewards = [];
                    return false;
                }

                Add(partnerRow.DecomposeItemId, partnerRow.DecomposeItemCount);

                int breakThroughExp = 0;
                for (int breakTimes = 0; breakTimes <= partner.BreakThrough; breakTimes++)
                {
                    PartnerBreakThroughTable? row = breakthroughs[partner.TemplateId]
                        .SingleOrDefault(value => value.BreakTimes == breakTimes);
                    List<(int Level, int AllExp)> levels = row is null ? [] : GetLevelTemplate(row.LevelUpTemplateId);
                    int level = breakTimes == partner.BreakThrough ? partner.Level : row?.LevelLimit ?? 0;
                    if (row is null || !levels.Any(value => value.Level == level)
                        || row.CostItemId.Count != row.CostItemCount.Count)
                    {
                        rewards = [];
                        return false;
                    }
                    if (breakTimes < partner.BreakThrough)
                    {
                        breakThroughExp = checked(breakThroughExp
                            + levels.Single(value => value.Level == level).AllExp);
                        foreach ((int itemId, int count) in row.CostItemId.Zip(row.CostItemCount))
                            Add(itemId, count * levelRate);
                    }
                    else
                    {
                        double exp = (partner.Exp
                            + levels.Single(value => value.Level == level).AllExp
                            + breakThroughExp) * levelRate;
                        while (true)
                        {
                            (int Id, int Exp, int Coin) item = expItems.FirstOrDefault(value => exp >= value.Exp);
                            if (item.Exp == 0)
                                break;
                            Add(item.Id, 1);
                            Add(Inventory.Coin, item.Coin);
                            exp -= item.Exp;
                        }
                    }
                }

                for (int quality = partnerRow.InitQuality + 1; quality <= partner.Quality; quality++)
                {
                    PartnerQualityTable? row = qualities[partner.TemplateId]
                        .SingleOrDefault(value => value.Quality == quality - 1);
                    if (row is null)
                    {
                        rewards = [];
                        return false;
                    }
                    Add(row.EvolutionCostItemId ?? 0, (row.EvolutionCostItemCount ?? 0) * evolutionRate);
                }
                Add(partnerRow.ChipItemId, partner.StarSchedule * evolutionRate);

                int skillLevels = partner.SkillList.Sum(skill => skill.Level);
                for (int level = partner.SkillList.Count + 1; level <= skillLevels; level++)
                    foreach ((int itemId, int count) in skillRow.UpgradeCostItemId.Zip(skillRow.UpgradeCostItemCount))
                        Add(itemId, count * skillRate);
            }

            rewards = totals
                .Select(total => (Id: total.Key, Count: checked((int)Math.Floor(total.Value))))
                .Where(total => total.Count > 0)
                .OrderByDescending(total => total.Id)
                .Select(total => new RewardGoods
                {
                    Id = total.Id,
                    TemplateId = total.Id,
                    Count = total.Count,
                    RewardType = (int)RewardType.Item
                })
                .ToList();
            return true;
        }

        private static (int TemplateId, int ShardId, int ShardCount)? ResolveComposition(int requestedId)
        {
            PartnerTable? partner = TableReaderV2.Parse<PartnerTable>()
                .Find(row => row.Id == requestedId || row.ChipItemId == requestedId);
            ItemTable? shard = partner is null
                ? null
                : TableReaderV2.Parse<ItemTable>().Find(item => item.Id == partner.ChipItemId);
            return partner is not null && partner.ChipNeedCount > 0 && shard?.ItemType == 8
                ? (partner.Id, partner.ChipItemId, partner.ChipNeedCount)
                : null;
        }

        internal static PartnerData CreatePartner(int id, int templateId)
        {
            PartnerTable partnerConfig = TableReaderV2.Parse<PartnerTable>()
                .First(row => row.Id == templateId);
            PartnerSkillTable skillConfig = TableReaderV2.Parse<PartnerSkillTable>()
                .First(row => row.PartnerId == templateId);
            PartnerMainSkillGroupTable mainSkillGroup = TableReaderV2.Parse<PartnerMainSkillGroupTable>()
                .First(group => group.Id == skillConfig.DefaultMainSkillGroupId);
            ILookup<int, PartnerPassiveSkillGroupTable> passiveSkillGroups =
                TableReaderV2.Parse<PartnerPassiveSkillGroupTable>().ToLookup(group => group.Id);

            List<PartnerSkillData> skills =
            [
                new PartnerSkillData
                {
                    Id = mainSkillGroup.SkillId.First(),
                    Level = 1,
                    IsWear = true,
                    Type = 1
                }
            ];
            skills.AddRange(skillConfig.PassiveSkillGroupId.Select(groupId => new PartnerSkillData
            {
                Id = passiveSkillGroups[groupId].Single().SkillId,
                Level = 1,
                IsWear = false,
                Type = 2
            }));

            return new PartnerData
            {
                Id = id,
                TemplateId = templateId,
                Level = 1,
                Quality = partnerConfig.InitQuality,
                SkillList = skills,
                UnlockSkillGroup = skillConfig.MainSkillGroupId.ToList(),
                CreateTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }
    }
}
