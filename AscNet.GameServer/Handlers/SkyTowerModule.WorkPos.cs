using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Work position requests. Rules not stated by the client contract (AscNet policy): a moved staff member leaves its old
/// position empty (the client's own preview does the same); stopping or changing a position's product returns the inputs
/// reserved for the round in progress; a swap across floor groups returns both positions' reserved inputs, a swap inside a
/// group keeps each round; speed-up orders advance one position by AccelerateTime seconds each and unused orders are returned.
/// </summary>
internal static partial class SkyTowerModule
{
    private static void Clear(SkyTowerWorkPosState wp)
    {
        wp.WorkType = WorkNone;
        wp.ProductId = 0;
        wp.CharacterId = 0;
        wp.State = StateStop;
        wp.ProduceRemainder = 0;
    }

    [RequestPacketHandler("SkyTowerWorkPosAssignRequest")]
    public static void WorkPosAssignRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerWorkPosAssignRequest request = packet.Deserialize<SkyTowerWorkPosAssignRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.WorkPos.TryGetValue(request.WorkPosId, out SkyTowerWorkPosState? wp)) return new SkyTowerWorkPosAssignResponse { Code = WorkPosNotUnlock };
            if (!s.Characters.ContainsKey(request.CharacterId)) return new SkyTowerWorkPosAssignResponse { Code = CharacterNotUnlock };
            (int floor, int index) = ParseKey(request.WorkPosId);
            if (!CharacterInGroup(request.CharacterId, GroupOf(floor))) return new SkyTowerWorkPosAssignResponse { Code = CharacterFloorGroupNotMatch };
            if (wp.WorkType == WorkNone || wp.ProductId <= 0) return new SkyTowerWorkPosAssignResponse { Code = InvalidRequest };
            if (AppointWork(floor, index) is int appoint and > 0 && wp.WorkType != appoint) return new SkyTowerWorkPosAssignResponse { Code = WorkPosAppointWorkNotMatch };
            Settle(s, op.Now, false, out _, out _);
            if (!IsUniversal(request.CharacterId))
                foreach ((int key, SkyTowerWorkPosState other) in s.WorkPos.Where(entry => entry.Key != request.WorkPosId && entry.Value.CharacterId == request.CharacterId).ToList())
                {
                    Refund(s, key, other);
                    Clear(other);
                }
            wp.CharacterId = request.CharacterId;
            if (wp.State == StateStop) wp.State = StateNormal;
            Finalise(s, op.Now, op.At);
            op.PushCharacters = true;
            return new SkyTowerWorkPosAssignResponse { DataUpdate = op.Update() };
        });
    }

    [RequestPacketHandler("SkyTowerWorkPosSetProductRequest")]
    public static void WorkPosSetProductRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerWorkPosSetProductRequest request = packet.Deserialize<SkyTowerWorkPosSetProductRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.WorkPos.TryGetValue(request.WorkPosId, out SkyTowerWorkPosState? wp)) return new SkyTowerWorkPosSetProductResponse { Code = WorkPosNotUnlock };
            if (request.WorkType is < WorkIngredient or > WorkSale || request.ProductType != ProductTypeOf(request.WorkType))
                return new SkyTowerWorkPosSetProductResponse { Code = InvalidRequest };
            (int floor, int index) = ParseKey(request.WorkPosId);
            if (AppointWork(floor, index) is int appoint and > 0 && request.WorkType != appoint) return new SkyTowerWorkPosSetProductResponse { Code = WorkPosAppointWorkNotMatch };
            if (!ProductExists(request.WorkType, request.ProductId)) return new SkyTowerWorkPosSetProductResponse { Code = ConfigNotFound };
            bool unlocked = (request.WorkType == WorkIngredient ? UnlockedIngredients(s.Level) : UnlockedFoods(s.Level)).Contains(request.ProductId);
            if (!unlocked) return new SkyTowerWorkPosSetProductResponse { Code = ConditionNotMet };
            // A position only works products of its own floor group (the storage slot lives there).
            if (!IsFloorGroupProduct(GroupOf(floor), request.ProductType, request.ProductId)) return new SkyTowerWorkPosSetProductResponse { Code = InvalidRequest };
            Settle(s, op.Now, false, out _, out _);
            if (wp.WorkType != request.WorkType || wp.ProductId != request.ProductId)
            {
                Refund(s, request.WorkPosId, wp);
                wp.ProduceRemainder = 0;
            }
            wp.WorkType = request.WorkType;
            wp.ProductId = request.ProductId;
            Finalise(s, op.Now, op.At);
            return new SkyTowerWorkPosSetProductResponse { DataUpdate = op.Update() };
        });
    }

    [RequestPacketHandler("SkyTowerWorkPosStopRequest")]
    public static void WorkPosStopRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerWorkPosStopRequest request = packet.Deserialize<SkyTowerWorkPosStopRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.WorkPos.TryGetValue(request.WorkPosId, out SkyTowerWorkPosState? wp)) return new SkyTowerWorkPosStopResponse { Code = WorkPosNotUnlock };
            if (PosFree(wp)) return new SkyTowerWorkPosStopResponse { Code = WorkPosNoCharacter };
            Settle(s, op.Now, false, out _, out _);
            Refund(s, request.WorkPosId, wp);
            wp.State = StateStop;
            Finalise(s, op.Now, op.At);
            return new SkyTowerWorkPosStopResponse { DataUpdate = op.Update() };
        });
    }

    [RequestPacketHandler("SkyTowerWorkPosSwapRequest")]
    public static void WorkPosSwapRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerWorkPosSwapRequest request = packet.Deserialize<SkyTowerWorkPosSwapRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.WorkPos.TryGetValue(request.FromWorkPosId, out SkyTowerWorkPosState? from) || !s.WorkPos.TryGetValue(request.ToWorkPosId, out SkyTowerWorkPosState? to))
                return new SkyTowerWorkPosSwapResponse { Code = WorkPosNotUnlock };
            if (request.FromWorkPosId == request.ToWorkPosId) return new SkyTowerWorkPosSwapResponse { DataUpdate = op.Update() };
            (int fromFloor, int fromIndex) = ParseKey(request.FromWorkPosId);
            (int toFloor, int toIndex) = ParseKey(request.ToWorkPosId);
            // Both directions are checked: the dragged content must fit the target's assigned work and the target's content the source's.
            if ((AppointWork(toFloor, toIndex) is int toAppoint and > 0 && from.WorkType != WorkNone && from.WorkType != toAppoint)
                || (AppointWork(fromFloor, fromIndex) is int fromAppoint and > 0 && to.WorkType != WorkNone && to.WorkType != fromAppoint))
                return new SkyTowerWorkPosSwapResponse { Code = WorkPosAppointWorkNotMatch };
            if ((from.CharacterId > 0 && !CharacterInGroup(from.CharacterId, GroupOf(toFloor))) || (to.CharacterId > 0 && !CharacterInGroup(to.CharacterId, GroupOf(fromFloor))))
                return new SkyTowerWorkPosSwapResponse { Code = CharacterFloorGroupNotMatch };
            if ((from.ProductId > 0 && !IsFloorGroupProduct(GroupOf(toFloor), ProductTypeOf(from.WorkType), from.ProductId))
                || (to.ProductId > 0 && !IsFloorGroupProduct(GroupOf(fromFloor), ProductTypeOf(to.WorkType), to.ProductId)))
                return new SkyTowerWorkPosSwapResponse { Code = InvalidRequest };
            Settle(s, op.Now, false, out _, out _);
            if (GroupOf(fromFloor) != GroupOf(toFloor))
            {
                Refund(s, request.FromWorkPosId, from);
                Refund(s, request.ToWorkPosId, to);
            }
            // The whole slot (staff, work, round progress) moves.
            s.WorkPos[request.FromWorkPosId] = to;
            s.WorkPos[request.ToWorkPosId] = from;
            Finalise(s, op.Now, op.At);
            op.PushCharacters = true;
            return new SkyTowerWorkPosSwapResponse { DataUpdate = op.Update() };
        });
    }

    [RequestPacketHandler("SkyTowerUseSpeedUpItemRequest")]
    public static void UseSpeedUpItemRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerUseSpeedUpItemRequest request = packet.Deserialize<SkyTowerUseSpeedUpItemRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (request.Count <= 0) return new SkyTowerUseSpeedUpItemResponse { Code = InvalidRequest };
            if (request.Count > ConfigInt("AcceleratePerUseLimit")) return new SkyTowerUseSpeedUpItemResponse { Code = SpeedUpPerUseLimitOver };
            if (!s.WorkPos.TryGetValue(request.WorkPosId, out SkyTowerWorkPosState? wp)) return new SkyTowerUseSpeedUpItemResponse { Code = WorkPosNotUnlock };
            if (s.SpeedUpItem < request.Count) return new SkyTowerUseSpeedUpItemResponse { Code = SpeedUpItemNotEnough };
            int dailyLimit = ConfigInt("AccelerateUseLimit");
            if (dailyLimit > 0 && s.SpeedUpDailyUsedCount + request.Count > dailyLimit) return new SkyTowerUseSpeedUpItemResponse { Code = SpeedUpDailyLimitReached };
            Settle(s, op.Now, false, out _, out _);
            if (PosFree(wp) || wp.State != StateNormal) return new SkyTowerUseSpeedUpItemResponse { Code = WorkPosNotWorking };
            // Each order advances this position alone by AccelerateTime seconds. Once the position stalls (no inputs, full storage)
            // the remaining orders are not spent.
            SimResult total = new();
            int used = 0;
            while (used < request.Count && (used == 0 || wp.State == StateNormal))
            {
                SimResult step = Advance(s, 0, ConfigInt("AccelerateTime"), request.WorkPosId);
                total.Gold += step.Gold;
                foreach (((int Type, int Id) key, long count) in step.Produced) total.Produced[key] = total.Produced.GetValueOrDefault(key) + count;
                foreach ((int id, long count) in step.Sold) total.Sold[id] = total.Sold.GetValueOrDefault(id) + count;
                used++;
            }
            s.SpeedUpItem -= used;
            s.SpeedUpDailyUsedCount += used;
            long gold = AddGold(s, total.Gold, true);
            ApplyProduction(s, total);
            Finalise(s, op.Now, op.At);
            int productType = ProductTypeOf(wp.WorkType);
            long produced = wp.WorkType == WorkSale ? total.Sold.GetValueOrDefault(wp.ProductId) : total.Produced.GetValueOrDefault((productType, wp.ProductId));
            return new SkyTowerUseSpeedUpItemResponse
            {
                SpeedUpDailyUsedCount = s.SpeedUpDailyUsedCount, DataUpdate = op.Update(), GoldCount = gold, ProductType = productType,
                ProductId = wp.ProductId, ProduceCount = produced
            };
        });
    }
}
