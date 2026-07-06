using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;
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
    public class BuyResponse
    {
        public int Code { get; set; }
        public bool IsShowBuyResult { get; set; }
        public List<RewardGoods> GoodList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class ShopModule
    {
        private const string ShopSnapshotPath = "Configs/client_shops.json";
        private const string ShopBaseInfoSnapshotPath = "Configs/shop_base_infos.json";
        private static readonly Lazy<Dictionary<uint, ClientShop>> RetailShopSnapshot = new(LoadShopSnapshot);

        [RequestPacketHandler("GetShopInfoRequest")]
        public static void GetShopInfoRequestHandler(Session session, Packet.Request packet)
        {
            GetShopInfoRequest request = MessagePackSerializer.Deserialize<GetShopInfoRequest>(packet.Content);
            session.SendResponse(new GetShopInfoResponse
            {
                Code = 0,
                ClientShop = BuildClientShop(request.Id)
            }, packet.Id);
        }
        
        [RequestPacketHandler("GetShopInfoReceiveRequest")]
        public static void GetShopInfoReceiveRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetShopInfoResponse
            {
                Code = 0,
                ClientShop = BuildClientShop(0)
            }, packet.Id);
        }

        [RequestPacketHandler("GetFixedShopListRequest")]
        public static void GetFixedShopListRequestHandler(Session session, Packet.Request packet)
        {
            GetFixedShopListRequest request = MessagePackSerializer.Deserialize<GetFixedShopListRequest>(packet.Content);
            GetFixedShopListResponse response = new()
            {
                Code = 0
            };

            foreach (uint shopId in request.IdList.Distinct())
            {
                response.ClientShopList.Add(BuildClientShop(shopId));
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetShopValidInfoRequest")]
        public static void GetShopValidInfoRequestHandler(Session session, Packet.Request packet)
        {
            GetShopValidInfoRequest request = MessagePackSerializer.Deserialize<GetShopValidInfoRequest>(packet.Content);
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
                    IsUnShelve = false
                });
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("BuyRequest")]
        public static void BuyRequestHandler(Session session, Packet.Request packet)
        {
            BuyRequest request = MessagePackSerializer.Deserialize<BuyRequest>(packet.Content);
            ClientShopGoods? goods = FindShopGoods(request.ShopId, request.GoodsId);
            BuyResponse response = new()
            {
                Code = 0,
                IsShowBuyResult = false
            };

            if (goods is not null)
            {
                int count = Math.Max(1, request.Count);
                RewardGoods rewardGoods = ToRewardGoods(goods.RewardGoods, count);
                response.GoodList.Add(rewardGoods);

                ApplyShopCosts(session, goods, count);
                ApplyShopReward(session, rewardGoods);
                session.inventory.Save();
                session.character.Save();
            }

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

        private static ClientShop BuildClientShop(uint shopId)
        {
            if (RetailShopSnapshot.Value.TryGetValue(shopId, out ClientShop? shop))
            {
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
            string path = File.Exists(ShopSnapshotPath)
                ? ShopSnapshotPath
                : Path.Combine("Resources", ShopSnapshotPath);

            if (!File.Exists(path))
            {
                return new Dictionary<uint, ClientShop>();
            }

            JObject root = JObject.Parse(File.ReadAllText(path));
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

        private static ClientShopGoods? FindShopGoods(uint shopId, uint goodsId)
        {
            if (RetailShopSnapshot.Value.TryGetValue(shopId, out ClientShop? shop))
            {
                ClientShopGoods? goods = shop.GoodsList.FirstOrDefault(goods => goods.Id == goodsId);
                if (goods is not null)
                    return goods;
            }

            return RetailShopSnapshot.Value.Values
                .SelectMany(shop => shop.GoodsList)
                .FirstOrDefault(goods => goods.Id == goodsId);
        }

        private static RewardGoods ToRewardGoods(ClientShopRewardGoods reward, int countMultiplier)
        {
            return new RewardGoods
            {
                RewardType = reward.RewardType,
                TemplateId = (int)reward.TemplateId,
                Count = reward.Count * countMultiplier,
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

        private static void ApplyShopCosts(Session session, ClientShopGoods goods, int count)
        {
            NotifyItemDataList notifyItemDataList = new();
            foreach (ClientShopConsume consume in goods.ConsumeList)
            {
                long totalCost = (long)consume.Count * count;
                notifyItemDataList.ItemDataList.Add(session.inventory.Do(consume.Id, -(int)Math.Min(totalCost, int.MaxValue)));
            }

            if (notifyItemDataList.ItemDataList.Count > 0)
            {
                session.SendPush(notifyItemDataList);
            }
        }

        private static void ApplyShopReward(Session session, RewardGoods reward)
        {
            if (!Enum.IsDefined(typeof(RewardType), reward.RewardType))
                return;

            RewardHandler.GiveRewards(new[]
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
