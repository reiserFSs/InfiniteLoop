using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class CreateFurnitureRequest { public List<int>? TypeIds { get; set; } public int Count { get; set; } public int CostA { get; set; } public int CostB { get; set; } public int CostC { get; set; } }
[MessagePackObject(true)] public sealed class CreateFurnitureResponse { public int Code { get; set; } public uint EndTime { get; set; } public int Count { get; set; } public List<DormFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class CheckCreateFurnitureRequest { public int Pos { get; set; } }
[MessagePackObject(true)] public sealed class CheckCreateFurnitureResponse { public int Code { get; set; } public int Count { get; set; } public List<DormFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DecomposeFurnitureRequest { public List<int> FurnitureIds { get; set; } = []; }
[MessagePackObject(true)] public sealed class DecomposeFurnitureResponse { public int Code { get; set; } public List<int> SuccessIds { get; set; } = []; public List<RewardGoods> RewardGoods { get; set; } = []; }
[MessagePackObject(true)] public sealed class RemouldFurnitureParam { public int ItemId { get; set; } public List<int>? FurnitureIds { get; set; } }
[MessagePackObject(true)] public sealed class RemouldFurnitureRequest { public List<RemouldFurnitureParam>? Params { get; set; } }
[MessagePackObject(true)] public sealed class RemouldFurnitureResponse { public int Code { get; set; } public List<int> RemovedIds { get; set; } = []; public List<DormFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class FurnitureRemakeRequest { public List<int> FurnitureIds { get; set; } = []; public int CostA { get; set; } public int CostB { get; set; } public int CostC { get; set; } }
[MessagePackObject(true)] public sealed class FurnitureRemakeResponse { public int Code { get; set; } public List<int> RemovedIds { get; set; } = []; public List<RewardGoods> RewardGoods { get; set; } = []; public List<DormFurnitureData> FurnitureList { get; set; } = []; public int Count { get; set; } }
[MessagePackObject(true)] public sealed class SetFurnitureOptLockRequest { public int FurnitureId { get; set; } public bool IsLocked { get; set; } }
[MessagePackObject(true)] public sealed class SetFurnitureOptLockResponse { public int Code { get; set; } public int FurnitureId { get; set; } }
[MessagePackObject(true)] public sealed class NotifyFurnitureOperate { public List<DormFurnitureOperation> OperateList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormFurnitureOperation { public int OperateType { get; set; } public DormFurnitureData ClientFurniture { get; set; } = new(); }

internal partial class DormModule
{
    private static readonly Lazy<FurnitureTables> FurnitureData = new(() => new FurnitureTables(
        TableReaderV2.Parse<FurnitureTable>(), TableReaderV2.Parse<FurnitureTypeTable>(), TableReaderV2.Parse<FurnitureCreateAttrTable>(),
        TableReaderV2.Parse<FurnitureLevelTable>(), TableReaderV2.Parse<FurnitureRewardTable>(), TableReaderV2.Parse<FurnitureExtraAttrTable>(),
        TableReaderV2.Parse<FurnitureBaseAttrTable>(), TableReaderV2.Parse<FurnitureAdditionalAttrTable>(), TableReaderV2.Parse<FurniturePutNumTable>(), TableReaderV2.Parse<FurnitureSuitTable>()));

    private sealed record FurnitureTables(List<FurnitureTable> Furniture, List<FurnitureTypeTable> Types, List<FurnitureCreateAttrTable> CreateAttrs, List<FurnitureLevelTable> Levels, List<FurnitureRewardTable> Rewards, List<FurnitureExtraAttrTable> ExtraAttrs, List<FurnitureBaseAttrTable> BaseAttrs, List<FurnitureAdditionalAttrTable> AdditionalAttrs, List<FurniturePutNumTable> PutNums, List<FurnitureSuitTable> Suits);

    [RequestPacketHandler("CreateFurnitureRequest")]
    public static void CreateFurnitureRequestHandler(Session session, Packet.Request packet)
    {
        CreateFurnitureRequest request = packet.Deserialize<CreateFurnitureRequest>();
        FurnitureTables tables = FurnitureData.Value;
        long cost = (long)request.CostA + request.CostB + request.CostC;
        int max = Config().GetValueOrDefault("DormMaxCreateCount", 1);
        if (request.TypeIds is not { Count: > 0 } types || request.Count <= 0
            || request.Count > max / types.Count || request.CostA < 0 || request.CostB < 0 || request.CostC < 0
            || cost <= 0 || cost > int.MaxValue / request.Count / types.Count || types.Count != types.Distinct().Count()
            || types.Any(type => !tables.Types.Any(row => row.Id == type)))
        { session.SendResponse(new CreateFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        int totalCost = (int)(cost * request.Count * types.Count);
        if (session.inventory.SpendableCount(Inventory.FurnitureCoin) < totalCost)
        { session.SendResponse(new CreateFurnitureResponse { Code = 20060003 }, packet.Id); return; }

        List<PlayerDormFurniture> created = [];
        foreach (int type in types)
            for (int count = 0; count < request.Count; count++)
            {
                PlayerDormFurniture? furniture = GenerateFurniture(type, (int)cost, [request.CostA, request.CostB, request.CostC]);
                if (furniture is null) { session.SendResponse(new CreateFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
                created.Add(furniture);
            }
        if (!CanStore(session.player.Dorm, created)) { session.SendResponse(new CreateFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id); return; }

        if (!TrySaveFurnitureChange(session, new Dictionary<int, int> { [Inventory.FurnitureCoin] = totalCost },
                () => AddFurniture(session.player.Dorm, created), out NotifyItemDataList payment))
        {
            session.SendResponse(new CreateFurnitureResponse { Code = 1 }, packet.Id);
            return;
        }
        List<(int ConditionType, int? Parameter, int Amount)> progress =
        [
            (29008, null, created.Count),
            (29017, null, 1),
            .. types.GroupBy(type => tables.Types.Single(row => row.Id == type).MinorType)
                .Select(group => (29008, (int?)group.Key, group.Count() * request.Count))
        ];
        session.DormTaskStateDirty = true;
        TaskModule.RecordTableDrivenProgress(session, progress);
        session.SendPush(payment);
        session.SendResponse(new CreateFurnitureResponse { Count = totalCost, FurnitureList = created.Select(Furniture).ToList() }, packet.Id);
    }

    [RequestPacketHandler("CheckCreateFurnitureRequest")]
    public static void CheckCreateFurnitureRequestHandler(Session session, Packet.Request packet)
    {
        CheckCreateFurnitureRequest request = packet.Deserialize<CheckCreateFurnitureRequest>();
        PlayerDormFurnitureCreate? create = session.player.Dorm.FurnitureCreateList.FirstOrDefault(entry => entry.Pos == request.Pos);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (create?.Furniture is null || create.EndTime > now) { session.SendResponse(new CheckCreateFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        session.player.Dorm.FurnitureCreateList.Remove(create);
        session.player.Dorm.Furniture.Add(create.Furniture); Unlock(session.player.Dorm, [create.Furniture]); session.player.Save();
        session.DormTaskStateDirty = true;
        session.SendResponse(new CheckCreateFurnitureResponse { Count = create.Count, FurnitureList = [Furniture(create.Furniture)] }, packet.Id);
    }

    [RequestPacketHandler("DecomposeFurnitureRequest")]
    public static void DecomposeFurnitureRequestHandler(Session session, Packet.Request packet)
    {
        DecomposeFurnitureRequest request = packet.Deserialize<DecomposeFurnitureRequest>();
        int[] ids = request.FurnitureIds.Order().ToArray();
        if (ids.Length == 0 || ids.Length != request.FurnitureIds.Count
            || ids.Length > Config().GetValueOrDefault("DormMaxRecycleCount", int.MaxValue))
        {
            session.SendResponse(new DecomposeFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        string key = $"dorm-decompose:{session.player.PlayerData.Id}:{string.Join(",", ids)}";
        PlayerDormPendingReward? pending = session.player.Dorm.PendingRewards.SingleOrDefault(entry => entry.Key == key);
        List<RewardGoodsTable> rewards;
        if (pending is null)
        {
            if (!OwnedFreeFurniture(session.player.Dorm, request.FurnitureIds,
                    Config().GetValueOrDefault("DormMaxRecycleCount", int.MaxValue),
                    out List<PlayerDormFurniture> furniture))
            {
                session.SendResponse(new DecomposeFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id);
                return;
            }
            rewards = furniture.SelectMany(DecomposeRewards).ToList();
            pending = new PlayerDormPendingReward
            {
                Key = key,
                Goods = rewards.Select(reward => new PlayerDormPendingRewardItem
                {
                    Id = reward.Id,
                    TemplateId = reward.TemplateId,
                    Count = reward.Count,
                    Params = reward.Params.ToList()
                }).ToList()
            };
            session.player.Dorm.PendingRewards.Add(pending);
            session.player.Dorm.Furniture.RemoveAll(entry => ids.Contains(entry.Id));
        }
        else
        {
            rewards = pending.Goods.Select(saved => TableReaderV2.Parse<RewardGoodsTable>()
                .Single(reward => reward.Id == saved.Id && reward.TemplateId == saved.TemplateId
                    && reward.Count == saved.Count && reward.Params.SequenceEqual(saved.Params)))
                .ToList();
        }

        bool cleared = false;
        try
        {
            session.player.SaveChecked();
            RewardApplicationResult? grant = rewards.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(key, rewards)], session);
            session.player.Dorm.PendingRewards.Remove(pending);
            cleared = true;
            session.player.SaveChecked();
            session.DormTaskStateDirty = true;
            TaskModule.RecordTableDrivenProgress(session, [(29015, null, ids.Length)]);
            grant?.SendPushes(session);
            session.SendResponse(new DecomposeFurnitureResponse { SuccessIds = request.FurnitureIds, RewardGoods = grant?.RewardGoods ?? [] }, packet.Id);
        }
        catch
        {
            if (cleared) session.player.Dorm.PendingRewards.Add(pending);
            session.SendResponse(new DecomposeFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id);
        }
    }

    [RequestPacketHandler("RemouldFurnitureRequest")]
    public static void RemouldFurnitureRequestHandler(Session session, Packet.Request packet)
    {
        RemouldFurnitureRequest request = packet.Deserialize<RemouldFurnitureRequest>();
        int max = Config().GetValueOrDefault("DormMaxRemouldCount", 1);
        List<RemouldFurnitureParam> parameters = request.Params ?? [];
        List<int> consumedIds = parameters.SelectMany(parameter => parameter.FurnitureIds ?? []).ToList();
        PlayerDormState dorm = session.player.Dorm;
        if (!OwnedFreeFurniture(dorm, consumedIds, max, out List<PlayerDormFurniture> consumed))
        {
            session.SendResponse(new RemouldFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        FurnitureTables tables = FurnitureData.Value;
        Dictionary<int, PlayerDormFurniture> ownedById = consumed.ToDictionary(furniture => furniture.Id);
        Dictionary<int, int> costs = [];
        List<PlayerDormFurniture> created = [];
        foreach (RemouldFurnitureParam parameter in parameters)
        {
            FurnitureTable? target = tables.Furniture.FirstOrDefault(row => row.PicId == parameter.ItemId);
            if (target is null || parameter.FurnitureIds is not { Count: > 0 } ids)
            {
                session.SendResponse(new RemouldFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id);
                return;
            }
            AddCost(costs, parameter.ItemId, checked(target.PicNum * ids.Count));
            foreach (int id in ids)
            {
                PlayerDormFurniture material = ownedById[id];
                FurnitureTable? source = tables.Furniture.FirstOrDefault(row => row.Id == material.ConfigId);
                if (source?.TypeId != target.TypeId)
                {
                    session.SendResponse(new RemouldFurnitureResponse { Code = 20060037 }, packet.Id);
                    return;
                }
                AddCost(costs, Inventory.FurnitureCoin, source.MoneyNum);
                int score = Score(material);
                int quality = tables.Levels.Where(level => level.FurnitureType == source.TypeId && (level.MinScore ?? 0) <= score)
                    .Select(level => level.Quality).DefaultIfEmpty().Max();
                // AscNet preserves the invested stats and rolls the target's additional effect at the material's tier or higher.
                created.Add(new PlayerDormFurniture
                {
                    ConfigId = (uint)target.Id,
                    AttrList = material.AttrList.ToList(),
                    BaseAttrList = material.BaseAttrList.ToList(),
                    Addition = RollFurnitureAddition(target.RandomGroupId, quality),
                    IsLocked = target.IsDefaultLocked == 1
                });
            }
        }
        if (costs.Any(cost => session.inventory.SpendableCount(cost.Key) < cost.Value))
        {
            session.SendResponse(new RemouldFurnitureResponse { Code = 20060003 }, packet.Id);
            return;
        }
        if (!TrySaveFurnitureChange(session, costs, () =>
            {
                dorm.Furniture.RemoveAll(furniture => ownedById.ContainsKey(furniture.Id));
                AddFurniture(dorm, created);
            }, out NotifyItemDataList payment))
        {
            session.SendResponse(new RemouldFurnitureResponse { Code = 1 }, packet.Id);
            return;
        }
        session.DormTaskStateDirty = true;
        TaskModule.RecordTableDrivenProgress(session, [(29007, null, 1)]);
        session.SendPush(payment);
        session.SendResponse(new RemouldFurnitureResponse
        {
            RemovedIds = consumedIds,
            FurnitureList = created.Select(Furniture).ToList()
        }, packet.Id);
    }

    [RequestPacketHandler("FurnitureRemakeRequest")]
    public static void FurnitureRemakeRequestHandler(Session session, Packet.Request packet)
    {
        FurnitureRemakeRequest request = packet.Deserialize<FurnitureRemakeRequest>();
        long cost = (long)request.CostA + request.CostB + request.CostC;
        if (!OwnedFreeFurniture(session.player.Dorm, request.FurnitureIds, Config().GetValueOrDefault("DormMaxRemakeCount", 1), out List<PlayerDormFurniture> old) || request.CostA < 0 || request.CostB < 0 || request.CostC < 0 || cost <= 0 || cost * old.Count > int.MaxValue)
        { session.SendResponse(new FurnitureRemakeResponse { Code = DormRequestDataInvalid }, packet.Id); return; }

        int totalCost = (int)(cost * old.Count);
        List<RewardGoodsTable> rewards = old.SelectMany(RemakeRewards).ToList();
        int refund = rewards.Where(reward => reward.TemplateId == Inventory.FurnitureCoin).Sum(reward => reward.Count);
        int netCost = Math.Max(0, totalCost - refund);
        if (session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.FurnitureCoin)?.Count < netCost) { session.SendResponse(new FurnitureRemakeResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        List<PlayerDormFurniture> remade = [];
        foreach (PlayerDormFurniture furniture in old)
        {
            FurnitureTable? config = FurnitureData.Value.Furniture.FirstOrDefault(row => row.Id == furniture.ConfigId);
            if (config is null || !CanRemake(furniture, config.TypeId, (int)cost) || GenerateFurniture(config.TypeId, (int)cost, [request.CostA, request.CostB, request.CostC]) is not PlayerDormFurniture next) { session.SendResponse(new FurnitureRemakeResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
            next.ConfigId = furniture.ConfigId; next.DormitoryId = furniture.DormitoryId; next.X = furniture.X; next.Y = furniture.Y; next.Angle = furniture.Angle; remade.Add(next);
        }
        PlayerDormState dorm = BsonSerializer.Deserialize<PlayerDormState>(session.player.Dorm.ToBson());
        Inventory inventory = BsonSerializer.Deserialize<Inventory>(session.inventory.ToBson());
        RewardApplicationResult? grant;
        try
        {
            AssignFurnitureIds(session.player.Dorm, remade);
            session.inventory.Do(Inventory.FurnitureCoin, -netCost);
            session.player.Dorm.Furniture.RemoveAll(entry => old.Any(value => value.Id == entry.Id));
            session.player.Dorm.Furniture.AddRange(remade);
            Unlock(session.player.Dorm, remade);
            grant = rewards.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant($"dorm-remake:{session.player.PlayerData.Id}:{string.Join(",", old.Select(value => value.Id).Order())}", rewards)],
                session);
            if (refund > 0) session.inventory.Do(Inventory.FurnitureCoin, -refund);
            session.inventory.SaveChecked();
        }
        catch (Exception exception)
        {
            session.log.Warn("Could not save furniture remake payment.", exception);
            RestoreFurnitureChange(session, dorm, inventory, saveInventory: true);
            session.SendResponse(new FurnitureRemakeResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }
        if (!TryCommitFurniturePlayer(session))
        {
            RestoreFurnitureChange(session, dorm, inventory, saveInventory: true);
            session.SendResponse(new FurnitureRemakeResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }
        session.DormTaskStateDirty = true;
        session.SendPush(new NotifyItemDataList { ItemDataList = [session.inventory.Items.First(item => item.Id == Inventory.FurnitureCoin)] });
        grant?.SendPushes(session);
        session.SendResponse(new FurnitureRemakeResponse { RemovedIds = old.Select(entry => entry.Id).ToList(), FurnitureList = remade.Select(Furniture).ToList(), RewardGoods = grant?.RewardGoods ?? [], Count = netCost }, packet.Id);
    }

    [RequestPacketHandler("SetFurnitureOptLockRequest")]
    public static void SetFurnitureOptLockRequestHandler(Session session, Packet.Request packet)
    {
        SetFurnitureOptLockRequest request = packet.Deserialize<SetFurnitureOptLockRequest>();
        PlayerDormFurniture? furniture = session.player.Dorm.Furniture.FirstOrDefault(entry => entry.Id == request.FurnitureId);
        if (furniture is null) { session.SendResponse(new SetFurnitureOptLockResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        furniture.IsLocked = request.IsLocked; session.player.Save(); session.SendResponse(new SetFurnitureOptLockResponse { FurnitureId = furniture.Id }, packet.Id);
    }

    private static bool OwnedFreeFurniture(PlayerDormState dorm, List<int> ids, int max, out List<PlayerDormFurniture> furniture)
    {
        furniture = ids.Distinct().Select(id => dorm.Furniture.FirstOrDefault(entry => entry.Id == id)).OfType<PlayerDormFurniture>().ToList();
        return ids.Count > 0 && ids.Count <= max && furniture.Count == ids.Count && furniture.All(entry => !entry.IsLocked && entry.DormitoryId <= 0);
    }
    private static bool CanStore(PlayerDormState dorm, List<PlayerDormFurniture> additions) => dorm.Furniture.Count + additions.Count <= Config().GetValueOrDefault("DormMaxTotalFurnitureCount", int.MaxValue);
    private static void AddCost(Dictionary<int, int> costs, int id, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > 0) costs[id] = checked(costs.GetValueOrDefault(id) + count);
    }

    private static void AssignFurnitureIds(PlayerDormState dorm, IEnumerable<PlayerDormFurniture> furniture)
    {
        dorm.NormalizeFurnitureIds();
        foreach (PlayerDormFurniture entry in furniture) entry.Id = checked(dorm.NextFurnitureId++);
    }

    private static void AddFurniture(PlayerDormState dorm, List<PlayerDormFurniture> furniture)
    {
        AssignFurnitureIds(dorm, furniture);
        dorm.Furniture.AddRange(furniture);
        Unlock(dorm, furniture);
    }

    internal static bool TrySaveFurnitureChange(Session session, IReadOnlyDictionary<int, int> costs,
        Action changeFurniture, out NotifyItemDataList payment)
    {
        PlayerDormState dorm = BsonSerializer.Deserialize<PlayerDormState>(session.player.Dorm.ToBson());
        Inventory inventory = BsonSerializer.Deserialize<Inventory>(session.inventory.ToBson());
        bool dormTaskStateDirty = session.DormTaskStateDirty;
        payment = new();
        bool attemptedInventorySave = false;
        try
        {
            foreach ((int id, int count) in costs)
                payment.ItemDataList.AddRange(session.inventory.Spend(id, count));
            changeFurniture();
            if (payment.ItemDataList.Count > 0)
            {
                attemptedInventorySave = true;
                session.inventory.SaveChecked();
            }
        }
        catch (Exception exception)
        {
            session.log.Warn("Could not save furniture transaction payment.", exception);
            session.DormTaskStateDirty = dormTaskStateDirty;
            RestoreFurnitureChange(session, dorm, inventory, attemptedInventorySave);
            return false;
        }
        if (TryCommitFurniturePlayer(session))
            return true;
        session.DormTaskStateDirty = dormTaskStateDirty;
        RestoreFurnitureChange(session, dorm, inventory, attemptedInventorySave);
        return false;
    }

    private static bool TryCommitFurniturePlayer(Session session)
    {
        Player player = session.player;
        string? previous = player.Dorm.FurnitureWriteId;
        string writeId = Guid.NewGuid().ToString("N");
        player.Dorm.FurnitureWriteId = writeId;
        try
        {
            player.SaveChecked();
            return true;
        }
        catch (Exception exception)
        {
            Player? stored = Player.TryFromPlayerId(player.PlayerData.Id);
            if (stored?.Dorm.FurnitureWriteId == writeId)
            {
                session.log.Warn("Furniture transaction committed despite a lost acknowledgement.", exception);
                return true;
            }
            if (stored is not null && stored.Id == player.Id && stored.Dorm.FurnitureWriteId == previous)
            {
                session.log.Warn("Could not save furniture transaction.", exception);
                return false;
            }
            session.DisconnectProtocol(persistState: false);
            throw;
        }
    }

    private static void RestoreFurnitureChange(Session session, PlayerDormState dorm, Inventory inventory, bool saveInventory)
    {
        session.player.Dorm = dorm;
        session.inventory.Items = inventory.Items;
        session.inventory.SpentTotals = inventory.SpentTotals;
        session.inventory.AppliedRewardClaims = inventory.AppliedRewardClaims;
        session.inventory.RewardClaimTimes = inventory.RewardClaimTimes;
        if (!saveInventory)
            return;
        try { session.inventory.SaveChecked(); }
        catch (Exception exception)
        {
            session.log.Warn("Could not restore furniture transaction payment.", exception);
            session.DisconnectProtocol(persistState: false);
        }
    }

    private static void Unlock(PlayerDormState dorm, IEnumerable<PlayerDormFurniture> furniture) { foreach (uint id in furniture.Select(entry => entry.ConfigId).Distinct()) if (!dorm.FurnitureUnlocks.Contains(id)) dorm.FurnitureUnlocks.Add(id); }
    private static DormFurnitureData Furniture(PlayerDormFurniture value) => new() { Id = value.Id, ConfigId = value.ConfigId, X = value.X, Y = value.Y, Angle = value.Angle, DormitoryId = value.DormitoryId, Addition = value.Addition, AttrList = value.AttrList, BaseAttrList = value.BaseAttrList, IsLocked = value.IsLocked };
    internal static void SendPurchasedFurniture(Session session, int firstIndex)
    {
        session.SendPush(new NotifyFurnitureOperate
        {
            OperateList = session.player.Dorm.Furniture.Skip(firstIndex)
                .Select(furniture => new DormFurnitureOperation { OperateType = 1, ClientFurniture = Furniture(furniture) })
                .ToList()
        });
    }

    internal static bool CanGrantFurnitureReward(PlayerDormState dorm, int rewardId, int count) =>
        TryPrepareFurnitureReward(dorm, rewardId, count, out _);

    internal static bool TryGrantFurnitureReward(Session session, int rewardId, int count)
    {
        if (!TryPrepareFurnitureReward(session.player.Dorm, rewardId, count, out PlayerDormFurniture template)) return false;
        List<PlayerDormFurniture> additions = Enumerable.Range(0, count).Select(_ => new PlayerDormFurniture
        {
            ConfigId = template.ConfigId,
            DormitoryId = 0,
            Addition = template.Addition,
            AttrList = template.AttrList.ToList(),
            BaseAttrList = template.BaseAttrList.ToList(),
            IsLocked = template.IsLocked
        }).ToList();
        AddFurniture(session.player.Dorm, additions);
        session.DormTaskStateDirty = true;
        return true;
    }

    private static bool TryPrepareFurnitureReward(PlayerDormState dorm, int rewardId, int count, out PlayerDormFurniture template)
    {
        template = null!;
        if (count <= 0
            || (long)dorm.NextFurnitureId + count - 1 > int.MaxValue
            || (long)dorm.Furniture.Count + count > Config().GetValueOrDefault("DormMaxTotalFurnitureCount", int.MaxValue))
            return false;
        FurnitureTables tables = FurnitureData.Value;
        FurnitureRewardTable? reward = tables.Rewards.FirstOrDefault(row => row.Id == rewardId);
        FurnitureTable? config = reward is null ? null : tables.Furniture.FirstOrDefault(row => row.Id == reward.FurnitureId);
        FurnitureExtraAttrTable? extra = reward is null ? null : tables.ExtraAttrs.FirstOrDefault(row => row.Id == reward.ExtraAttrId);
        FurnitureBaseAttrTable? baseAttr = extra is null ? null : tables.BaseAttrs.FirstOrDefault(row => row.Id == extra.BaseAttrId);
        if (config is null || extra is null || baseAttr is null) return false;
        List<int> bases = extra.AttrIds.Take(3).Concat(Enumerable.Repeat(0, 3)).Take(3).ToList();
        template = new PlayerDormFurniture
        {
            ConfigId = (uint)config.Id,
            Addition = reward!.AdditionId,
            AttrList = Split(baseAttr.Value, bases).ToList(),
            BaseAttrList = bases,
            IsLocked = config.IsDefaultLocked == 1
        };
        return true;
    }

    private static PlayerDormFurniture? GenerateFurniture(int type, int cost, List<int> bases)
    {
        FurnitureTables tables = FurnitureData.Value;
        FurnitureCreateAttrTable? create = tables.CreateAttrs.FirstOrDefault(row => row.FurnitureType == type && row.MinConsume == cost);
        List<FurnitureTable> candidates = tables.Furniture.Where(row => row.TypeId == type && row.GainType == 1).ToList();
        if (create is null || candidates.Count == 0) return null;
        FurnitureTable config = candidates[Random.Shared.Next(candidates.Count)];
        int total = Weighted(create.AttrTotal, create.AttrWeight); int[] split = Split(total, bases); int addition = RollFurnitureAddition(config.RandomGroupId);
        return new PlayerDormFurniture { ConfigId = (uint)config.Id, Addition = addition, AttrList = split.ToList(), BaseAttrList = bases.Take(3).Concat(Enumerable.Repeat(0, 3)).Take(3).ToList(), IsLocked = config.IsDefaultLocked == 1 };
    }
    private static int RollFurnitureAddition(int groupId, int minimumQuality = 0)
    {
        var candidates = FurnitureData.Value.AdditionalAttrs.Where(row => row.GroupId == groupId
            && row.Weight is > 0 && row.QualityType >= minimumQuality).ToList();
        return Weighted(candidates.Select(row => row.AttributeId).ToList(), candidates.Select(row => row.Weight!.Value).ToList());
    }
    private static int Weighted(List<int> values, List<int> weights) { int total = weights.Sum(); if (total <= 0 || values.Count == 0) return values.FirstOrDefault(); int value = Random.Shared.Next(total); for (int index = 0; index < values.Count && index < weights.Count; index++) { value -= weights[index]; if (value < 0) return values[index]; } return values[^1]; }
    private static int[] Split(int total, List<int> bases) { int[] result = [0, 0, 0]; int sum = bases.Take(3).Sum(); if (sum <= 0) { result[0] = total; return result; } int left = total; for (int index = 0; index < 3; index++) { result[index] = index == 2 ? left : total * bases.ElementAtOrDefault(index) / sum; left -= result[index]; } return result; }
    private static bool CanRemake(PlayerDormFurniture furniture, int type, int cost) { FurnitureCreateAttrTable? row = FurnitureData.Value.CreateAttrs.FirstOrDefault(value => value.FurnitureType == type && value.MinConsume == cost); return row is not null && furniture.AttrList.Sum() - row.ExtraAttrTotal <= row.AttrTotal.Max(); }
    private static IEnumerable<RewardGoodsTable> DecomposeRewards(PlayerDormFurniture furniture) { FurnitureTable? config = FurnitureData.Value.Furniture.FirstOrDefault(row => row.Id == furniture.ConfigId); if (config is null) return []; int score = Score(furniture); FurnitureLevelTable? level = FurnitureData.Value.Levels.Where(row => row.FurnitureType == config.TypeId && score >= (row.MinScore ?? 0) && score < row.MaxScore).FirstOrDefault(); return level?.ReturnId is int id ? RewardHandler.GetRewardGoods(id) : []; }
    private static IEnumerable<RewardGoodsTable> RemakeRewards(PlayerDormFurniture furniture)
    {
        List<RewardGoodsTable> rewards = DecomposeRewards(furniture).ToList();
        if (rewards.Where(reward => reward.TemplateId == Inventory.FurnitureCoin).Sum(reward => reward.Count) < furniture.BaseAttrList.Sum()) return rewards;
        FurnitureTable? config = FurnitureData.Value.Furniture.FirstOrDefault(row => row.Id == furniture.ConfigId);
        FurnitureLevelTable? level = config is null ? null : FurnitureData.Value.Levels.FirstOrDefault(row => row.FurnitureType == config.TypeId && furniture.AttrList.Sum() >= (row.MinScore ?? 0) && furniture.AttrList.Sum() < row.MaxScore);
        return level?.ReturnId is int id ? RewardHandler.GetRewardGoods(id) : rewards;
    }
    private static int Score(PlayerDormFurniture furniture) { FurnitureAdditionalAttrTable? addition = FurnitureData.Value.AdditionalAttrs.FirstOrDefault(row => row.AttributeId == furniture.Addition); return furniture.AttrList.Sum() + (addition?.AddScore ?? 0); }
}
