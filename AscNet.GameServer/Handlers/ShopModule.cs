using AscNet.Common;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.alarmclock;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;
using AscNet.Table.V2.share.guild;
using ClientShop = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop;
using ClientShopConsume = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods.GetShopInfoResponseClientShopGoodsConsume;
using ClientShopGoods = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods;
using ClientShopRewardGoods = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods.GetShopInfoResponseClientShopGoodsRewardGoods;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GetShopBaseInfoRequest
    {
    }

    [MessagePackObject(true)]
    public class GetShopBaseInfoResponse
    {
        public List<dynamic> ShopBaseInfoList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetShopValidInfoRequest
    {
        public List<uint> IdList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetShopValidInfoResponse
    {
        public int Code { get; set; }
        public List<ShopValidInfo> ShopValidInfos { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ShopValidInfo
    {
        public uint Id { get; set; }
        public int StartTime { get; set; }
        public int EndTime { get; set; }
        public bool IsUnShelve { get; set; }
        public List<int> ConditionIds { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BuyRequest
    {
        public uint ShopId { get; set; }
        public uint GoodsId { get; set; }
        public int Count { get; set; }
    }

    [MessagePackObject(true)]
    public class BuyResponse : GuildResponse
    {
        public bool IsShowBuyResult { get; set; }
        public List<RewardGoods> GoodList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class ShopModule
    {
        private const string ShopSnapshotPath = "Configs/client_shops.json";
        private static readonly Lazy<ClientShop> GuildProcurementShop = new(BuildGuildProcurementShop);

        // Explicit local-server procurement policy, not a retail price or captured offer.
        internal static class LocalGuildProcurementPolicy
        {
            public static int Price { get; } = ReadPrice();
            private static int ReadPrice()
            {
                string? value = Environment.GetEnvironmentVariable("ASCNET_GUILD_PROCUREMENT_PRICE");
                if (value is null) return 100;
                return int.TryParse(value, out int price) && price > 0 ? price
                    : throw new InvalidDataException("ASCNET_GUILD_PROCUREMENT_PRICE must be a positive integer.");
            }
        }

        private static ClientShop BuildGuildProcurementShop()
        {
            ClientShop shop = new() { Id = 9998, Name = "Guild Procurement", ShowIds = [62723] };
            foreach (GuildGoodsTable row in TableReaderV2.Parse<GuildGoodsTable>().OrderBy(row => row.Id))
                shop.GoodsList.Add(new()
                {
                    Id = checked((uint)row.Id), BuyTimesLimit = 1,
                    RewardGoods = new() { TemplateId = checked((uint)row.Id), Count = 1, RewardType = 23 },
                    ConsumeList = [new() { Id = 62723, Count = checked((uint)LocalGuildProcurementPolicy.Price) }]
                });
            return shop;
        }

        private static ClientShop? ShopCatalog(uint shopId) => shopId == 9998
            ? GuildProcurementShop.Value : RetailShopSnapshot.Value.GetValueOrDefault(shopId);

        private const string ShopBaseInfoSnapshotPath = "Configs/shop_base_infos.json";
        private static readonly Lazy<Dictionary<uint, ClientShop>> RetailShopSnapshot = new(LoadShopSnapshot);
        private static readonly Lazy<Dictionary<int, AlarmClockTable>> AlarmClocks = new(() =>
            TableReaderV2.Parse<AlarmClockTable>().GroupBy(clock => clock.ClockId).ToDictionary(group => group.Key, group => group.First()));

        [RequestPacketHandler("GetShopInfoRequest")]
        public static void GetShopInfoRequestHandler(Session session, Packet.Request packet)
        {
            GetShopInfoRequest request = packet.Deserialize<GetShopInfoRequest>();
            int guildCode = GuildShopInfoCode(session.player, request.Id);
            if (guildCode != 0)
            {
                session.SendResponse(new GetShopInfoResponse { Code = guildCode }, packet.Id);
                return;
            }
            session.SendResponse(new GetShopInfoResponse
            {
                Code = 0,
                ClientShop = BuildClientShop(request.Id, session.player)
            }, packet.Id);
        }
        
        [RequestPacketHandler("GetShopInfoReceiveRequest")]
        public static void GetShopInfoReceiveRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetShopInfoResponse
            {
                Code = 0,
                ClientShop = BuildClientShop(0, session.player)
            }, packet.Id);
        }

        [RequestPacketHandler("GetFixedShopListRequest")]
        public static void GetFixedShopListRequestHandler(Session session, Packet.Request packet)
        {
            GetFixedShopListRequest request = packet.Deserialize<GetFixedShopListRequest>();
            GetFixedShopListResponse response = new()
            {
                Code = 0
            };

            foreach (uint shopId in request.IdList.Distinct())
            {
                int code = GuildShopInfoCode(session.player, shopId);
                if (code != 0) { response.Code = code; continue; }
                response.ClientShopList.Add(BuildClientShop(shopId, session.player));
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetShopValidInfoRequest")]
        public static void GetShopValidInfoRequestHandler(Session session, Packet.Request packet)
        {
            GetShopValidInfoRequest request = packet.Deserialize<GetShopValidInfoRequest>();
            GetShopValidInfoResponse response = new()
            {
                Code = 0
            };

            foreach (uint shopId in request.IdList.Distinct())
            {
                response.ShopValidInfos.Add(new ShopValidInfo
                {
                    Id = shopId,
                    StartTime = 0,
                    EndTime = 0,
                    IsUnShelve = GuildShopInfoCode(session.player, shopId) != 0
                });
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("BuyRequest")]
        public static void BuyRequestHandler(Session session, Packet.Request packet)
        {
            BuyRequest request = packet.Deserialize<BuyRequest>();
            if (TheatreModule.IsCommonShop(request.ShopId))
            {
                BuyTheatreGoods(session, packet);
                return;
            }
            if (request.ShopId is 4001 or 9998)
            {
                BuyGuildGoods(session, packet);
                return;
            }
            BuyResponse response = new()
            {
                Code = 1,
                IsShowBuyResult = false
            };
            ClientShopGoods? goods = FindShopGoods(request.ShopId, request.GoodsId);
            if (goods is null)
            {
                session.SendResponse(response, packet.Id);
                return;
            }

            bool resetChanged = ReconcileShopResetPeriods(session.player, [goods]);
            if (!TryPreparePurchase(session, goods, request.Count, out RewardGoods rewardGoods))
            {
                if (resetChanged)
                    session.player.Save();
                session.SendResponse(response, packet.Id);
                return;
            }

            List<(int ConditionType, int? Parameter, int Amount)> progress = ApplyShopCosts(session, goods, request.Count);
            RewardApplicationResult? result = ApplyShopReward(session, rewardGoods);
            session.player.ShopBuyTimes ??= new();
            session.player.ShopBuyTimes[goods.Id] = checked(
                session.player.ShopBuyTimes.GetValueOrDefault(goods.Id) + request.Count);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            result?.SendPushes(session);
            progress.Add((20201, checked((int)request.ShopId), request.Count));
            TaskModule.RecordTableDrivenProgress(session, progress);

            response.Code = 0;
            response.GoodList.Add(rewardGoods);
            session.SendResponse(response, packet.Id);
        }
        
        // TODO: Dorm shop
        [RequestPacketHandler("GetShopBaseInfoRequest")]
        public static void GetShopBaseInfoRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(BuildShopBaseInfoResponse(), packet.Id);
        }

        private static GetShopBaseInfoResponse BuildShopBaseInfoResponse()
        {
            JObject snapshot = JsonSnapshot.LoadObject(ShopBaseInfoSnapshotPath);
            return new GetShopBaseInfoResponse
            {
                ShopBaseInfoList = JsonSnapshot.ReadDynamicList(snapshot["ShopBaseInfoList"])
            };
        }

        private static int GuildShopInfoCode(Player player, uint shopId)
        {
            if (shopId is not (4001 or 9998)) return 0;
            Guild? guild = GuildModule.FindMembership(player.PlayerData.Id);
            if (guild is null || GuildModule.Rank(guild, player.PlayerData.Id) is not (>= 1 and <= 4))
                return 20063226;
            if (shopId == 9998 && GuildModule.Rank(guild, player.PlayerData.Id) is not (1 or 2))
                return 20063224;
            return ShopCatalog(shopId) is not null ? 0 : 20063227;
        }

        private static ClientShop BuildClientShop(uint shopId, Player player)
        {
            if (ShopCatalog(shopId) is { } snapshot)
            {
                ClientShop shop = MessagePackSerializer.Deserialize<ClientShop>(
                    MessagePackSerializer.Serialize(snapshot));
                if (shopId is not (4001 or 9998) && ReconcileShopResetPeriods(player, shop.GoodsList))
                    player.Save();
                player.ShopBuyTimes ??= new();
                Guild? guild = shopId == 9998 ? GuildModule.FindMembership(player.PlayerData.Id) : null;
                foreach (ClientShopGoods goods in shop.GoodsList)
                {
                    goods.TotalBuyTimes = player.ShopBuyTimes.GetValueOrDefault(goods.Id);
                    if (shopId is 4001 or 9998)
                    {
                        goods.TotalBuyTimes = player.GuildState.GuildShopBuyTimes.GetValueOrDefault(checked((int)goods.Id), goods.TotalBuyTimes);
                        if (TryGetAlarmWindow(goods.AutoResetClockId, DateTimeOffset.UtcNow, out long period, out _)
                            && player.GuildState.GuildShopResetPeriods.GetValueOrDefault(goods.AutoResetClockId) != period)
                            goods.TotalBuyTimes = 0;
                        if (guild is not null)
                            goods.TotalBuyTimes = OwnsProcurementGoods(guild, checked((int)goods.RewardGoods.TemplateId)) ? 1 : 0;
                    }
                    goods.RefreshTime = TryGetAlarmWindow(goods.AutoResetClockId, DateTimeOffset.UtcNow, out _, out long next)
                        ? checked((int)next)
                        : 0;
                }
                shop.TotalBuyTimes = shop.GoodsList.Sum(goods => goods.TotalBuyTimes);
                shop.RefreshTime = shop.GoodsList.Where(goods => goods.RefreshTime > 0).Select(goods => goods.RefreshTime).DefaultIfEmpty().Min();
                return shop;
            }

            return new ClientShop
            {
                Id = shopId,
                Name = shopId == 0 ? string.Empty : "Shop",
                RefreshTime = 0,
                ClosedTime = 0,
                ManualRefreshTimes = 0,
                ManualResetTimesLimit = 0,
                RefreshCostId = 0,
                RefreshCostCount = 0,
                TotalBuyTimes = 0,
                BuyTimesLimit = 0,
                RefreshTips = null
            };
        }

        private static Dictionary<uint, ClientShop> LoadShopSnapshot()
        {
            JObject root = JsonSnapshot.LoadObject(ShopSnapshotPath);
            if (!root.HasValues)
            {
                return new Dictionary<uint, ClientShop>();
            }
            Dictionary<uint, ClientShop> shops = new();
            foreach (JProperty shopProperty in root.Properties())
            {
                if (!uint.TryParse(shopProperty.Name, out uint shopId) || shopProperty.Value is not JObject shopObject)
                    continue;

                shops[shopId] = ReadShop(shopObject);
            }

            return shops;
        }

        private static ClientShop ReadShop(JObject data)
        {
            ClientShop shop = new()
            {
                Id = ReadUInt(data, "Id"),
                Name = ReadString(data, "Name"),
                RefreshTime = ReadInt(data, "RefreshTime"),
                ClosedTime = ReadInt(data, "ClosedTime"),
                ManualRefreshTimes = ReadInt(data, "ManualRefreshTimes"),
                ManualResetTimesLimit = ReadInt(data, "ManualResetTimesLimit"),
                RefreshCostId = ReadInt(data, "RefreshCostId"),
                RefreshCostCount = ReadInt(data, "RefreshCostCount"),
                TotalBuyTimes = ReadInt(data, "TotalBuyTimes"),
                BuyTimesLimit = ReadInt(data, "BuyTimesLimit"),
                RefreshTips = ReadDynamic(data["RefreshTips"])
            };

            shop.ShowIds.AddRange(ReadIntList(data["ShowIds"]));
            shop.ScreenGroupList.AddRange(ReadIntList(data["ScreenGroupList"]));
            foreach (int conditionId in ReadIntList(data["ConditionIds"]))
            {
                shop.ConditionIds.Add(conditionId);
            }

            if (data["GoodsList"] is JArray goodsList)
            {
                foreach (JToken goodsToken in goodsList)
                {
                    if (goodsToken is JObject goodsObject)
                    {
                        shop.GoodsList.Add(ReadGoods(goodsObject));
                    }
                }
            }

            return shop;
        }

        private static ClientShopGoods ReadGoods(JObject data)
        {
            ClientShopGoods goods = new()
            {
                Id = ReadUInt(data, "Id"),
                Priority = ReadUInt(data, "Priority"),
                RewardGoods = ReadGoodsReward((JObject)data["RewardGoods"]!),
                TotalBuyTimes = ReadInt(data, "TotalBuyTimes"),
                BuyTimesLimit = ReadInt(data, "BuyTimesLimit"),
                OnSales = ReadDynamic(data["OnSales"]),
                OnSaleTime = ReadInt(data, "OnSaleTime"),
                SelloutTime = ReadInt(data, "SelloutTime"),
                RefreshTime = ReadInt(data, "RefreshTime"),
                Tags = ReadInt(data, "Tags"),
                PayKeySuffix = ReadDynamic(data["PayKeySuffix"]),
                GiftRewardId = ReadInt(data, "GiftRewardId"),
                AutoResetClockId = ReadInt(data, "AutoResetClockId"),
                BuyPriority = ReadInt(data, "BuyPriority"),
                ActivityConsumeCount = ReadInt(data, "ActivityConsumeCount"),
                ActivityDiscount = ReadInt(data, "ActivityDiscount")
            };

            if (data["ConsumeList"] is JArray consumeList)
            {
                foreach (JToken consumeToken in consumeList)
                {
                    if (consumeToken is JObject consumeObject)
                    {
                        goods.ConsumeList.Add(new ClientShopConsume
                        {
                            Id = ReadInt(consumeObject, "Id"),
                            Count = ReadUInt(consumeObject, "Count")
                        });
                    }
                }
            }

            foreach (int conditionId in ReadIntList(data["ConditionIds"]))
            {
                goods.ConditionIds.Add(conditionId);
            }

            return goods;
        }

        private static ClientShopRewardGoods ReadGoodsReward(JObject data)
        {
            return new ClientShopRewardGoods
            {
                RewardType = ReadInt(data, "RewardType"),
                TemplateId = ReadUInt(data, "TemplateId"),
                Count = ReadInt(data, "Count"),
                Level = ReadInt(data, "Level"),
                Quality = ReadInt(data, "Quality"),
                Grade = ReadInt(data, "Grade"),
                Breakthrough = ReadInt(data, "Breakthrough"),
                ConvertFrom = ReadInt(data, "ConvertFrom"),
                IsGift = ReadBool(data, "IsGift"),
                RewardMulti = ReadInt(data, "RewardMulti"),
                Id = ReadInt(data, "Id")
            };
        }

        private static bool ReconcileShopResetPeriods(Player player, IEnumerable<ClientShopGoods> encounteredGoods)
        {
            bool changed = false;
            foreach (int clockId in encounteredGoods.Select(goods => goods.AutoResetClockId).Where(clockId => clockId > 0).Distinct())
            {
                if (!TryGetAlarmWindow(clockId, DateTimeOffset.UtcNow, out long period, out _))
                    continue;

                player.ShopResetPeriods ??= new();
                if (!player.ShopResetPeriods.TryGetValue(clockId, out long storedPeriod))
                {
                    player.ShopResetPeriods[clockId] = period;
                    changed = true;
                    continue;
                }

                if (storedPeriod >= period)
                    continue;

                player.ShopResetPeriods[clockId] = period;
                if (player.ShopBuyTimes is not null)
                {
                    foreach (uint goodsId in RetailShopSnapshot.Value.Values
                                 .SelectMany(shop => shop.GoodsList)
                                 .Where(goods => goods.AutoResetClockId == clockId)
                                 .Select(goods => goods.Id))
                    {
                        changed |= player.ShopBuyTimes.Remove(goodsId);
                    }
                }
                changed = true;
            }

            return changed;
        }

        private static bool TryGetAlarmWindow(int clockId, DateTimeOffset now, out long period, out long next)
        {
            period = next = 0;
            if (!AlarmClocks.Value.TryGetValue(clockId, out AlarmClockTable? clock)
                || clock.AlarmCycle <= 0
                || !HasValidFilters(clock))
            {
                return false;
            }

            long cycle = clock.AlarmCycle;
            long epoch = clock.EpochTime.GetValueOrDefault();
            long current = now.ToUnixTimeSeconds();
            long candidate = epoch + ((current - epoch) / cycle) * cycle;
            while (!MatchesFilters(clock, candidate))
                candidate -= cycle;
            period = candidate;

            candidate += cycle;
            while (!MatchesFilters(clock, candidate))
                candidate += cycle;
            next = candidate;
            return true;
        }

        private static bool HasValidFilters(AlarmClockTable clock)
        {
            return clock.DayOfWeek.All(day => day is >= 1 and <= 7)
                && clock.DayOfMonth.All(day => day is >= 1 and <= 31);
        }

        private static bool MatchesFilters(AlarmClockTable clock, long timestamp)
        {
            DateTimeOffset date = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            int weekday = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
            return (clock.DayOfWeek.Count == 0 || clock.DayOfWeek.Contains(weekday))
                && (clock.DayOfMonth.Count == 0 || clock.DayOfMonth.Contains(date.Day));
        }

        private static ClientShopGoods? FindShopGoods(uint shopId, uint goodsId)
        {
            return ShopCatalog(shopId)?.GoodsList.FirstOrDefault(goods => goods.Id == goodsId);
        }

        private static bool OwnsProcurementGoods(Guild guild, int itemId)
        {
            GuildGoodsTable row = TableReaderV2.Parse<GuildGoodsTable>().Single(row => row.Id == itemId);
            return (row.Type == 1 ? guild.DormThemes : guild.DormBgms).Contains(row.TargetId);
        }

        private static void BuyTheatreGoods(Session session, Packet.Request packet)
        {
            if (session.player.Theatre.PendingMutation is null)
                TaskModule.EnsureMissionResets(session);
            TheatreModule.Handle<BuyRequest, BuyResponse>(session, packet, (mutation, request, response) =>
            {
                ClientShop? shop = ShopCatalog(request.ShopId);
                ClientShopGoods? goods = FindShopGoods(request.ShopId, request.GoodsId);
                if (shop is null || goods is null || goods.AutoResetClockId != 0
                    || shop.ConditionIds.Any(id => !TheatreModule.IsConditionSatisfied(mutation, id))
                    || goods.ConditionIds.Any(id => !TheatreModule.IsConditionSatisfied(mutation, id))
                    || !TryPreparePurchase(session, goods, request.Count, out RewardGoods reward))
                    throw new AscNet.Common.ServerCodeException("Original Theatre purchase is unavailable.", 1);
                int previous = mutation.GetShopBuyTimes(goods.Id);
                if (goods.BuyTimesLimit > 0 && (long)previous + request.Count > goods.BuyTimesLimit)
                    throw new AscNet.Common.ServerCodeException("Original Theatre purchase limit reached.", 1);
                foreach (ClientShopConsume consume in goods.ConsumeList)
                    mutation.Cost(consume.Id, checked((int)((long)consume.Count * request.Count)), 1);
                mutation.Grant(new RewardGrant($"theatre-shop:{request.ShopId}:{goods.Id}:{previous}",
                    [new AscNet.Table.V2.share.reward.RewardGoodsTable
                    {
                        TemplateId = reward.TemplateId, Count = reward.Count, Params = [reward.Level]
                    }], EventCause: TheatreModule.GetCommonShopEventCause(request.ShopId)));
                mutation.RecordShopPurchase(goods.Id, request.Count);
                TaskModule.RecordTableDrivenProgress(session,
                    goods.ConsumeList.Select(consume => (11202, (int?)consume.Id, checked((int)((long)consume.Count * request.Count))))
                        .Append((20201, (int?)checked((int)request.ShopId), request.Count)), theatre: mutation);
                response.GoodList.Add(reward);
            }, static (response, code) => response.Code = code, afterCommit: TaskModule.SendTaskSync);
        }

        private static void BuyGuildGoods(Session session, Packet.Request packet)
        {
            GuildModule.Handle<BuyRequest, BuyResponse>(session, packet, (mutation, request, response) =>
            {
                long uid = session.player.PlayerData.Id;
                GuildModule.PrepareEconomy(mutation);
                if (request.ShopId == 9998 && GuildModule.Rank(mutation.Guild, uid) is not (1 or 2))
                    throw new AscNet.Common.ServerCodeException("Insufficient guild purchase authority.", 20063224);
                ClientShopGoods goods = FindShopGoods(request.ShopId, request.GoodsId)
                    ?? throw new AscNet.Common.ServerCodeException("Guild shop offer is not configured.", 20030008);
                if (request.Count <= 0 || goods.RewardGoods.Count <= 0)
                    throw new AscNet.Common.ServerCodeException("Invalid purchase count.", 20030016);
                GuildPlayerState state = mutation.Player(uid);
                if (TryGetAlarmWindow(goods.AutoResetClockId, DateTimeOffset.UtcNow, out long period, out _)
                    && state.GuildShopResetPeriods.GetValueOrDefault(goods.AutoResetClockId) != period)
                {
                    foreach (ClientShopGoods entry in RetailShopSnapshot.Value.Values.SelectMany(shop => shop.GoodsList)
                        .Where(entry => entry.AutoResetClockId == goods.AutoResetClockId))
                        state.GuildShopBuyTimes[checked((int)entry.Id)] = 0;
                    state.GuildShopResetPeriods[goods.AutoResetClockId] = period;
                }
                int previous = state.GuildShopBuyTimes.GetValueOrDefault(checked((int)goods.Id),
                    session.player.ShopBuyTimes?.GetValueOrDefault(goods.Id) ?? 0);
                if (request.ShopId == 9998)
                    previous = OwnsProcurementGoods(mutation.Guild, checked((int)goods.RewardGoods.TemplateId)) ? 1 : 0;
                if (goods.BuyTimesLimit > 0 && (long)previous + request.Count > goods.BuyTimesLimit)
                    throw new AscNet.Common.ServerCodeException("Purchase limit reached.", 20030012);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (goods.OnSaleTime > now || goods.SelloutTime > 0 && now >= goods.SelloutTime)
                    throw new AscNet.Common.ServerCodeException("Product is no longer sold.", 20030015);
                foreach (int condition in goods.ConditionIds)
                    if (!GuildModule.ConditionSatisfied(session, condition, []))
                        throw new AscNet.Common.ServerCodeException("Purchase condition is not satisfied.", 20063229);
                foreach (ClientShopConsume consume in goods.ConsumeList)
                {
                    int count = checked((int)((long)consume.Count * request.Count));
                    if (consume.Id <= 0 || count <= 0)
                        throw new AscNet.Common.ServerCodeException("Invalid purchase currency.", 20063357);
                    TaskModule.RecordTableDrivenProgress(mutation, 11202, count, consume.Id);
                    GuildModule.AddEconomyCost(mutation, uid, consume.Id, count);
                }
                RewardGoods reward = ToRewardGoods(goods.RewardGoods, request.Count);
                mutation.AddRewardGrant(uid, $"guild-shop:{mutation.Guild.Id}:{request.GoodsId}:{goods.AutoResetClockId}:{state.GuildShopResetPeriods.GetValueOrDefault(goods.AutoResetClockId)}:{previous}",
                    [new AscNet.Table.V2.share.reward.RewardGoodsTable
                    {
                        TemplateId = reward.TemplateId, Count = reward.Count, Params = [reward.Level]
                    }]);
                state.GuildShopBuyTimes[checked((int)goods.Id)] = checked(previous + request.Count);
                TaskModule.RecordTableDrivenProgress(mutation, 20201, request.Count, checked((int)request.ShopId));
                response.GoodList.Add(reward);
            }, membershipCode: 20063228, fallbackCode: 20063229);
        }

        private static bool TryPreparePurchase(
            Session session,
            ClientShopGoods goods,
            int count,
            out RewardGoods rewardGoods)
        {
            rewardGoods = null!;
            if (count <= 0 || goods.RewardGoods.Count <= 0)
                return false;

            session.player.ShopBuyTimes ??= new();
            int previousBuyTimes = session.player.ShopBuyTimes.GetValueOrDefault(goods.Id);
            if (goods.BuyTimesLimit > 0
                && (long)previousBuyTimes + count > goods.BuyTimesLimit)
            {
                return false;
            }

            if (!Enum.IsDefined(typeof(RewardType), goods.RewardGoods.RewardType))
                return false;
            RewardType rewardType = (RewardType)goods.RewardGoods.RewardType;
            if (rewardType == RewardType.Item)
            {
                int rewardItemId = (int)goods.RewardGoods.TemplateId;
                if (!Inventory.IsValidClientItemId(rewardItemId))
                    return false;

                ItemTable? rewardItem = TableReaderV2.Parse<ItemTable>().Find(item => item.Id == rewardItemId);
                if (rewardItem?.ItemType == (int)ItemType.WeaponFashion
                    && !RewardHandler.TryResolveWeaponFashionReward(rewardItemId, out _))
                {
                    return false;
                }
            }
            else if (rewardType == RewardType.Furniture
                && ((long)goods.RewardGoods.Count * count > int.MaxValue
                    || !DormModule.CanGrantFurnitureReward(
                        session.player.Dorm,
                        (int)goods.RewardGoods.TemplateId,
                        goods.RewardGoods.Count * count)))
            {
                return false;
            }

            foreach (ClientShopConsume consume in goods.ConsumeList)
            {
                long totalCost = (long)consume.Count * count;
                long available = session.inventory.Items.FirstOrDefault(item => item.Id == consume.Id)?.Count ?? 0;
                if (consume.Id <= 0
                    || totalCost <= 0
                    || totalCost > int.MaxValue
                    || !Inventory.IsValidClientItemId(consume.Id)
                    || available < totalCost)
                {
                    return false;
                }
            }

            try
            {
                rewardGoods = ToRewardGoods(goods.RewardGoods, count);
                return rewardGoods.Count > 0;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static RewardGoods ToRewardGoods(ClientShopRewardGoods reward, int countMultiplier)
        {
            return new RewardGoods
            {
                RewardType = reward.RewardType,
                TemplateId = (int)reward.TemplateId,
                Count = checked(reward.Count * countMultiplier),
                Level = reward.Level,
                Quality = reward.Quality,
                Grade = reward.Grade,
                Breakthrough = reward.Breakthrough,
                ConvertFrom = reward.ConvertFrom,
                Id = reward.Id,
                IsGift = reward.IsGift,
                RewardMulti = reward.RewardMulti
            };
        }

        private static List<(int ConditionType, int? Parameter, int Amount)> ApplyShopCosts(Session session, ClientShopGoods goods, int count)
        {
            NotifyItemDataList notifyItemDataList = new();
            List<(int ConditionType, int? Parameter, int Amount)> progress = new(goods.ConsumeList.Count + 1);
            foreach (ClientShopConsume consume in goods.ConsumeList)
            {
                int totalCost = checked((int)((long)consume.Count * count));
                long before = session.inventory.Items.FirstOrDefault(item => item.Id == consume.Id)?.Count ?? 0;
                Item updated = session.inventory.Do(consume.Id, -totalCost);
                notifyItemDataList.ItemDataList.Add(updated);
                int paid = checked((int)Math.Min(totalCost, Math.Max(0, before - updated.Count)));
                if (paid > 0)
                    progress.Add((11202, consume.Id, paid));
            }

            if (notifyItemDataList.ItemDataList.Count > 0)
            {
                session.SendPush(notifyItemDataList);
            }
            return progress;
        }

        private static RewardApplicationResult? ApplyShopReward(Session session, RewardGoods reward)
        {
            if (!Enum.IsDefined(typeof(RewardType), reward.RewardType))
                return null;

            return RewardHandler.ApplyRewards(new[]
            {
                new Reward
                {
                    Type = (RewardType)reward.RewardType,
                    Id = reward.TemplateId,
                    Count = reward.Count,
                    Level = reward.Level
                }
            }, session);
        }

        private static int ReadInt(JObject data, string name)
        {
            return ReadInt(data[name]);
        }

        private static int ReadInt(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                return 0;

            return token.Value<int>();
        }

        private static uint ReadUInt(JObject data, string name)
        {
            if (data[name] is null || data[name]!.Type == JTokenType.Null)
                return 0;

            return data[name]!.Value<uint>();
        }

        private static bool ReadBool(JObject data, string name)
        {
            return data[name]?.Value<bool>() ?? false;
        }

        private static string ReadString(JObject data, string name)
        {
            return data[name]?.Value<string>() ?? string.Empty;
        }

        private static List<int> ReadIntList(JToken? token)
        {
            if (token is not JArray array)
                return [];

            return array.Select(value => value.Value<int>()).ToList();
        }

        private static dynamic? ReadDynamic(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                return null;

            return token.Type switch
            {
                JTokenType.Object => ReadDynamicDictionary((JObject)token),
                JTokenType.Array => ((JArray)token).Select(ReadDynamic).ToList(),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<int>(),
                JTokenType.Float => token.Value<double>(),
                JTokenType.String => token.Value<string>(),
                _ => null
            };
        }

        private static Dictionary<dynamic, dynamic> ReadDynamicDictionary(JObject data)
        {
            Dictionary<dynamic, dynamic> result = new();
            foreach (JProperty property in data.Properties())
            {
                dynamic key = int.TryParse(property.Name, out int numericKey)
                    ? numericKey
                    : property.Name;
                result[key] = ReadDynamic(property.Value);
            }

            return result;
        }
    }
}
