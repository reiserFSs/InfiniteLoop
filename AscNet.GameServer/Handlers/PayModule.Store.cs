using System.Collections;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.client.config;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.pay;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class PurchaseGetDailyRewardRequest
{
    public uint Id { get; set; }
}

[MessagePackObject(true)]
public sealed class PurchaseGetDailyRewardResponse
{
    public int Code { get; set; }
    public dynamic? PurchaseInfo { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
}

internal partial class PayModule
{
    private sealed class PurchaseLimitException(string message) : InvalidOperationException(message) { }

    // The client attaches analytics metadata when entering Store from a tab.
    private static bool ValidPurchaseParam(object? value)
    {
        if (value is null) return true;
        if (value is IDictionary map)
            return map.Keys.Cast<object>().All(key => key is string name && name == "FromMsg"
                && map[key] is sbyte or byte or short or ushort or int or uint or long or ulong);
        return value is ICollection collection && collection.Count == 0;
    }

    private static int ResetOffset => int.Parse(TableReaderV2.Parse<ConfigTable>()
        .Single(row => row.Key == "DailyResetTimestamp").Value);

    internal static long PurchaseDay(long? timestamp = null) =>
        ((timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - ResetOffset) / 86400;

    internal static int RemainingDays(Player? player, uint id) => checked((int)Math.Max(0,
        (player?.PurchaseDailyPasses.GetValueOrDefault(id)?.EndDay ?? 0) - PurchaseDay()));

    private static int CurrentBuyTimes(Player player, Dictionary<dynamic, dynamic> info)
    {
        uint id = ReadDynamicUInt(info, "Id");
        int bought = player.PurchaseBuyTimes.GetValueOrDefault(id);
        long last = player.PurchaseLastBuyTimes.GetValueOrDefault(id);
        if (bought == 0 || last == 0 || !info.TryGetValue("ClientResetInfo", out dynamic? raw)
            || raw is not Dictionary<dynamic, dynamic> reset) return bought;
        DateTime date = DateTimeOffset.FromUnixTimeSeconds(last - ResetOffset).UtcDateTime;
        DateTime today = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ResetOffset).UtcDateTime;
        bool elapsed = ReadDynamicInt(reset, "ResetType") switch
        {
            0 => date.Date < today.Date,
            1 => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7))
                < today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            2 => date.Year < today.Year || (date.Year == today.Year && date.Month < today.Month),
            3 => ReadDynamicInt(reset, "DayCount") > 0
                && PurchaseDay() - PurchaseDay(last) >= ReadDynamicInt(reset, "DayCount"),
            _ => false
        };
        // Lifetime counts remain monotonic for durable reward receipt identities.
        return elapsed ? 0 : player.PurchasePeriodBuyTimes.GetValueOrDefault(id, bought);
    }

    private static void AddBonusRewards(Dictionary<dynamic, dynamic> info, bool first, int count, List<RewardGoods> goods)
    {
        string field = first && HasValue(info, "FirstRewardGoods") ? "FirstRewardGoods" : "ExtraRewardGoods";
        if (info.TryGetValue(field, out dynamic? value) && value is Dictionary<dynamic, dynamic> bonus)
            goods.AddRange(ReadPurchaseRewards(new() { ["RewardGoodsList"] = new List<dynamic> { bonus } },
                field == "FirstRewardGoods" ? 1 : count));
        if (field == "FirstRewardGoods" && count > 1)
            AddBonusRewards(info, false, count - 1, goods);
    }

    private static void PreparePassUpdates(Player player, Dictionary<dynamic, dynamic> info, int count,
        PlayerPendingPurchase pending, HashSet<uint>? visiting = null)
    {
        uint id = ReadDynamicUInt(info, "Id");
        visiting ??= new();
        if (!visiting.Add(id)) throw new InvalidOperationException("Cyclic companion package");
        if (info.TryGetValue("PurchaseSignInInfo", out dynamic? signRaw) && signRaw is Dictionary<dynamic, dynamic> sign)
        {
            int[] rewards = ReadIds(sign, "PurchaseSignInRewardInfos").ToArray();
            if (count != 1 || rewards.Length == 0 || rewards.Any(reward => RewardHandler.GetRewardGoods(reward).Count == 0))
                throw new InvalidOperationException("Invalid sign-in package rewards");
            pending.DailyPasses[id] = new() { StartDay = PurchaseDay(), EndDay = PurchaseDay() + rewards.Length };
        }
        if (HasValue(info, "DailyRewardGoodsList"))
        {
            int duration = GetDailyPackageDuration(info);
            var old = player.PurchaseDailyPasses.GetValueOrDefault(id);
            long end = checked(Math.Max(PurchaseDay(), old?.EndDay ?? 0) + checked(duration * count));
            // A companion is granted by the purchased bundle, not bought directly.
            // Client Text.PurchaseMonthPlusDesc explicitly allows this grant even
            // when the recipient already holds the maximum number of monthly passes.
            if (visiting.Count == 1 && info.TryGetValue("ClientResetInfo", out dynamic? raw) && raw is Dictionary<dynamic, dynamic> reset
                && ReadDynamicInt(reset, "ResetType") == 5 && ReadDynamicInt(reset, "DayCount") > 0
                && RemainingDays(player, id) > ReadDynamicInt(reset, "DayCount"))
                throw new PurchaseLimitException($"Monthly pass {id} renewal limit exceeded: {RemainingDays(player, id)} days remaining");
            pending.DailyPasses[id] = new() { EndDay = end, LastClaimDay = old?.LastClaimDay ?? -1 };
        }
        int company = ReadDynamicInt(info, "CompanyPackage");
        if (company > 0)
        {
            if (!TryFindPurchaseInfo((uint)company, null, out var companion))
                throw new InvalidOperationException("Missing companion package");
            PreparePassUpdates(player, companion!, checked(count * ReadDynamicInt(info, "CompanyPackageCount")), pending, visiting);
        }
        visiting.Remove(id);
    }

    private static int GetDailyPackageDuration(Dictionary<dynamic, dynamic> info)
    {
        int catalogDays = Catalog.Value.DailyRewardDays(ReadDynamicUInt(info, "Id"));
        if (catalogDays > 0) return catalogDays;
        var configured = TableReaderV2.Parse<PurchaseDailyDurationTable>()
            .SingleOrDefault(row => row.Id == ReadDynamicUInt(info, "Id"));
        // Explicit local-server durations take precedence; monthly passes use
        // the same client configuration as XYKPurchasePackage.
        int duration = configured is not null ? configured.Days
            : ReadDynamicInt(info, "UiType") == 2
                ? TableReaderV2.Parse<StoreClientConfigTable>().Single(row => row.Key == "PurchaseYKLimtCount").Value
                : 0;
        if (duration <= 0) throw new InvalidOperationException("Missing or invalid duration source for daily package");
        return duration;
    }

    [RequestPacketHandler("PurchaseGetDailyRewardRequest")]
    public static void PurchaseGetDailyRewardRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<PurchaseGetDailyRewardRequest>();
        PurchaseGetDailyRewardResponse response = new() { Code = 20053031 };
        lock (session.player)
        {
            try
            {
                if (TryFindPurchaseInfo(request.Id, null, out var info) && RemainingDays(session.player, request.Id) > 0)
                {
                    var result = ClaimDailyRewards(session, request.Id, info!, out int claimCode);
                    response.Code = claimCode;
                    if (claimCode == 0)
                    {
                        ApplyPurchaseState([info!], session.player);
                        response.PurchaseInfo = info;
                        response.RewardList = result?.RewardGoods ?? [];
                        result?.SendPushes(session);
                    }
                }
            }
            catch (Exception error) { session.log.Error($"Daily purchase reward failed: {error}"); response.Code = 2; }
            session.SendResponse(response, packet.Id);
        }
    }

    private static RewardApplicationResult? ClaimDailyRewards(Session session, uint id,
        Dictionary<dynamic, dynamic> info, out int code)
    {
        code = 0;
        var pass = session.player.PurchaseDailyPasses[id];
        long day = PurchaseDay();
        if (pass.LastClaimDay >= day) return null;
        bool signPackage = info.TryGetValue("PurchaseSignInInfo", out dynamic? raw) && raw is Dictionary<dynamic, dynamic>;
        int index = checked((int)(day - pass.StartDay));
        List<RewardGoods> goods = signPackage ? new() : ReadPurchaseRewards(new() { ["RewardGoodsList"] = info["DailyRewardGoodsList"] }, 1);
        var rows = goods.Select(g => new RewardGoodsTable { Id = g.Id, TemplateId = g.TemplateId, Count = g.Count,
            Params = g.Level > 0 ? [g.Level] : [] }).ToList();
        if (signPackage)
        {
            int[] rewards = ReadIds((Dictionary<dynamic, dynamic>)raw!, "PurchaseSignInRewardInfos").ToArray();
            if (index < 0 || index >= rewards.Length) throw new InvalidOperationException("Sign-in day out of range");
            rows = RewardHandler.GetRewardGoods(rewards[index]);
        }
        string claimKey = $"purchase-daily:{session.player.PlayerData.Id}:{id}:{day}";
        if (!session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
            && !HasItemCapacity(session, rows))
        {
            code = 20027011;
            return null;
        }
        var result = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(claimKey, rows)], session);
        long before = pass.LastClaimDay;
        pass.LastClaimDay = day;
        bool added = signPackage && !pass.RewardIndexList.Contains(index + 1);
        if (added) pass.RewardIndexList.Add(index + 1);
        try { session.player.SaveChecked(); }
        catch { pass.LastClaimDay = before; if (added) pass.RewardIndexList.Remove(index + 1); throw; }
        return result;
    }

    private static bool HasItemCapacity(Session session, IEnumerable<RewardGoodsTable> goods)
    {
        foreach (IGrouping<int, RewardGoodsTable> group in goods
                     .Where(goods => RewardHandler.GetRewardType(goods) == RewardType.Item)
                     .GroupBy(goods => goods.TemplateId))
        {
            ItemTable? table = TableReaderV2.Parse<ItemTable>().FirstOrDefault(item => item.Id == group.Key);
            if (table is null || !Inventory.IsValidClientItemId(group.Key))
                return false;
            long remaining = Inventory.GetMaxCount(table);
            foreach (Item item in session.inventory.Items.Where(item => item.Id == group.Key))
            {
                if (item.Count < 0 || item.Count > remaining)
                    return false;
                remaining -= item.Count;
            }
            foreach (RewardGoodsTable reward in group)
            {
                if (reward.Count < 0 || reward.Count > remaining)
                    return false;
                remaining -= reward.Count;
            }
        }
        return true;
    }

    internal static void GrantMailDailyRewards(Session session, bool sendPush = true)
    {
        lock (session.player)
        {
            bool changed = false;
            foreach (uint id in session.player.PurchaseDailyPasses.Keys.ToArray())
                if (RemainingDays(session.player, id) > 0 && TryFindPurchaseInfo(id, null, out var info)
                    && ReadDynamicBool(info!, "IsUseMail"))
                {
                    var pass = session.player.PurchaseDailyPasses[id];
                    long day = PurchaseDay();
                    if (pass.LastClaimDay >= day) continue;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var rewards = ReadPurchaseRewards(new() { ["RewardGoodsList"] = info!["DailyRewardGoodsList"] }, 1);
                    PlayerMail mail = new()
                    {
                        Id = $"purchase-daily:{session.player.PlayerData.Id}:{id}:{day}",
                        Title = Convert.ToString(info["Name"]) ?? "",
                        Content = Convert.ToString(info["Desc"]) ?? "",
                        CreateTime = now, SendTime = now,
                        RewardGoodsList = rewards.Select(g => new PlayerMailRewardGoods
                        {
                            Id = g.Id, RewardType = g.RewardType, TemplateId = checked((uint)g.TemplateId),
                            Count = g.Count, Level = g.Level, Quality = g.Quality, Grade = g.Grade
                        }).ToList()
                    };
                    long oldClaim = pass.LastClaimDay;
                    session.player.Mails.Add(mail);
                    pass.LastClaimDay = day;
                    try { session.player.SaveChecked(); }
                    catch { session.player.Mails.Remove(mail); pass.LastClaimDay = oldClaim; throw; }
                    changed = true;
                }
            if (changed && sendPush)
                session.SendPush(MailModule.BuildNotifyMails(session.player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
    }

    internal static void AddSignInNotifications(PurchaseDailyNotify notify, Player player)
    {
        foreach (uint id in player.PurchaseDailyPasses.Keys)
        {
            if (RemainingDays(player, id) <= 0 || !TryFindPurchaseInfo(id, null, out var info)) continue;
            if (!info!.TryGetValue("PurchaseSignInInfo", out dynamic? raw) || raw is not Dictionary<dynamic, dynamic> sign) continue;
            ApplyPurchaseState([info], player);
            var data = new Dictionary<dynamic, dynamic>(info);
            foreach (var entry in sign) data[entry.Key] = entry.Value;
            data["SignRound"] = 1;
            data["RewardIndexList"] = player.PurchaseDailyPasses[id].RewardIndexList;
            notify.PurchaseSignInInfoList.Add(data);
        }
    }

    private static void CompleteLocalRecharge(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<PayInitiatedRequest>();
        PayInitiatedResponse response = new() { Code = 20053031 };
        lock (session.player)
        {
            try
            {
                PayTable? product = TableReaderV2.Parse<PayTable>().SingleOrDefault(row => row.Key == request.Key);
                if (product is not null && product.ShowUIType == 1 && product.MoneyCard > 0
                    && string.IsNullOrEmpty(request.TargetParam)
                    && (session.player.PendingRecharge is null || session.player.PendingRecharge.Key == request.Key))
                {
                    var pending = session.player.PendingRecharge;
                    if (pending is null)
                    {
                        pending = new() { Key = request.Key, Count = product.MoneyCard,
                            Order = $"local:{session.player.PlayerData.Id}:{session.player.RechargeSequence}" };
                        session.player.PendingRecharge = pending;
                        try { session.player.SaveChecked(); }
                        catch { session.player.PendingRecharge = null; throw; }
                    }
                    var result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant(pending.Order,
                        [new RewardGoodsTable { TemplateId = Inventory.HongKa, Count = pending.Count }])], session);
                    long sequence = session.player.RechargeSequence;
                    session.player.RechargeSequence = checked(sequence + 1);
                    session.player.PendingRecharge = null;
                    try { session.player.SaveChecked(); }
                    catch { session.player.RechargeSequence = sequence; session.player.PendingRecharge = pending; throw; }
                    response.Code = 0;
                    response.GameOrder = pending.Order;
                    response.LocalCompleted = true;
                    response.RewardList = result.RewardGoods;
                    result.SendPushes(session);
                }
            }
            catch (Exception error) { session.log.Error($"Local recharge failed: {error}"); response.Code = 2; }
            session.SendResponse(response, packet.Id);
        }
    }
}
