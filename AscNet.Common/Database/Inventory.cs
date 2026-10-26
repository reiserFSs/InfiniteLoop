using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Newtonsoft.Json;

namespace AscNet.Common.Database
{
    #pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public partial class Inventory
    {
        public const long GlobalItemMaxCount = 999;
        public const long MoneyItemMaxCount = 999_999_999;

        #region CommonItems
        public const int Coin = 1;
        public const int PaidGem = 2;
        public const int FreeGem = 3;
        public const int ActionPoint = 4;
        public const int HongKa = 5;
        public const int TeamExp = 7;
        public const int AndroidHongKa = 8;
        public const int IosHongKa = 10;
        public const int SkillPoint = 12;
        public const int DailyActiveness = 13;
        public const int WeeklyActiveness = 14;
        public const int HostelElectric = 15;
        public const int HostelMat = 16;
        public const int OnlineBossTicket = 17;
        public const int BountyTaskExp = 18;
        public const int DormCoin = 30;
        public const int FurnitureCoin = 31;
        public const int AClassInverMaterial = 34;
        public const int SClassInverMaterial = 35;
        public const int DormEnterIcon = 36;
        public const int SRankUniframeMaterial = 48;
        public const int BaseEquipCoin = 300;
        public const int InfestorActionPoint = 50;
        public const int InfestorMoney = 51;
        public const int PokemonLevelUpItem = 56;
        public const int PokemonStarUpItem = 57;
        public const int PokemonLowStarUpItem = 58;
        public const int PassportExp = 60;
        #endregion

        public static IMongoCollection<Inventory> collection = Common.db.GetCollection<Inventory>("inventory");
        // BigWorldItem rows are delivered through the main item channel (XBigWorldServiceAgency:OnInitItemData/IsDlcItem).
        private static readonly Lazy<HashSet<int>> ClientItemIds = new(() =>
            TableReaderV2.Parse<ItemTable>().Select(item => item.Id)
                .Concat(TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.item.BigWorldItemTable>().Select(item => item.Id))
                .ToHashSet());

        public static bool IsValidClientItemId(int itemId)
        {
            return itemId > 0 && ClientItemIds.Value.Contains(itemId);
        }

        public static List<Item> FilterClientItems(IEnumerable<Item> items)
        {
            return items.Where(item => IsValidClientItemId(item.Id)).ToList();
        }

        // Source ItemCombine: one client-visible balance across physical family members.
        // AscNet policy: spend lower-priority (earned) stacks first, then by ascending Id.
        private static readonly Lazy<Dictionary<int, int[]>> CombinedSpendOrder = new(() =>
        {
            List<ItemCombineTable> rows = TableReaderV2.Parse<ItemCombineTable>();
            return rows.GroupBy(row => row.GroupId)
                .SelectMany(group => group.Select(row => (row.ItemId, Ids: group
                    .OrderBy(member => member.Priority).ThenBy(member => member.ItemId)
                    .Select(member => member.ItemId).ToArray())))
                .ToDictionary(pair => pair.ItemId, pair => pair.Ids);
        });

        public static IReadOnlyList<int> CombinedItemIds(int itemId) =>
            CombinedSpendOrder.Value.TryGetValue(itemId, out int[]? ids) ? ids : [itemId];

        public long CombinedCount(int itemId, DateTimeOffset now) => CombinedItemIds(itemId).Sum(id => UsableCount(id, now));

        /// <summary>Physical debits covering <paramref name="cost"/> of <paramref name="itemId"/>'s combined
        /// balance at <paramref name="now"/>; null when invalid or insufficient. Callers apply each debit
        /// and record per-Id progress.</summary>
        public List<(int ItemId, int Count)>? PlanCombinedCost(int itemId, long cost, DateTimeOffset now)
        {
            if (cost <= 0 || cost > int.MaxValue || !IsValidClientItemId(itemId)) return null;
            List<(int ItemId, int Count)> plan = [];
            long remaining = cost;
            foreach (int id in CombinedItemIds(itemId))
            {
                long take = Math.Min(remaining, UsableCount(id, now));
                if (take <= 0) continue;
                plan.Add((id, (int)take));
                remaining -= take;
                if (remaining == 0) return plan;
            }
            return null;
        }

        // Source Item timeliness: FromConfig(1) = StartTime+Duration, AfterGet(2) = CreateTime+Duration;
        // StartTime is authored "yyyy/M/d H:mm" read as UTC like other table times.
        // ponytail: Batch(3) has no server batch model, so it stays unrestricted; add per-batch expiry with that model.
        private long UsableCount(int id, DateTimeOffset now) =>
            Items.Where(item => item.Id == id && !IsExpired(item, now)).Sum(item => item.Count);

        private static bool IsExpired(Item item, DateTimeOffset now)
        {
            ItemTable? table = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == item.Id);
            if (table?.Duration is not > 0) return false;
            long? start = table.TimelinessType switch
            {
                1 when DateTimeOffset.TryParseExact(table.StartTime, "yyyy/M/d H:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed) => parsed.ToUnixTimeSeconds(),
                2 => item.CreateTime,
                _ => null,
            };
            return start is long begin && now.ToUnixTimeSeconds() >= begin + table.Duration.Value;
        }


        public static Inventory FromUid(long uid)
        {
            return collection.AsQueryable().FirstOrDefault(x => x.Uid == uid) ?? Create(uid);
        }

        private static Inventory Create(long uid)
        {
            Inventory inventory = new()
            {
                Uid = uid,
                Items = new()
            };

            List<ItemConfig>? defaultItems = JsonConvert.DeserializeObject<List<ItemConfig>>(File.ReadAllText("./Configs/default_items.json"));
            if (defaultItems is not null)
            {
                inventory.Items.AddRange(defaultItems.Select(item => new Item()
                {
                    Id = item.Id,
                    Count = item.Count,
                    RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
                }));
            }

            collection.InsertOne(inventory);

            return inventory;
        }

        public Item Do(int itemId, int amount)
        {
            long before = Items.FirstOrDefault(x => x.Id == itemId)?.Count ?? 0;
            Item result = ApplyDelta(itemId, amount);
            if (amount < 0) RecordSpent(itemId, before - result.Count);
            return result;
        }

        private Item ApplyDelta(int itemId, int amount)
        {
            if (!IsValidClientItemId(itemId))
                throw new InvalidDataException($"Cannot mutate unknown client item id {itemId}.");

            Item? item = Items.FirstOrDefault(x => x.Id == itemId);
            ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().Find(x => x.Id == itemId);
            long maxCount = GetMaxCount(itemTable);

            if (item is not null)
            {
                if (item.Count + amount <= maxCount && item.Count + amount >= 0)
                {
                    item.Count += amount;
                    item.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                }
                else if (item.Count + amount < 0)
                {
                    item.Count = 0;
                    item.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                }
                else
                {
                    item.Count = maxCount;
                    item.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                }
            }
            else
            {
                item = new Item()
                {
                    Id = itemId,
                    Count = Math.Min(Math.Max(0, amount), maxCount),
                    RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
                };
                Items.Add(item);
            }

            return item;
        }
        public static long GetMaxCount(ItemTable? itemTable)
        {
            if (itemTable?.ItemType == (int)ItemType.Money)
                return Math.Min(itemTable.MaxCount ?? MoneyItemMaxCount, MoneyItemMaxCount);

            return itemTable?.MaxCount is > 0
                ? itemTable.MaxCount.Value
                : GlobalItemMaxCount;
        }

        public void Save()
        {
            collection.ReplaceOne(Builders<Inventory>.Filter.Eq(x => x.Id, Id), this);
        }

        public void SaveChecked()
        {
            ReplaceOneResult result = collection.ReplaceOne(
                Builders<Inventory>.Filter.Eq(x => x.Id, Id),
                this);
            if (!result.IsAcknowledged || result.MatchedCount != 1)
            {
                string matchCount = result.IsAcknowledged ? result.MatchedCount.ToString() : "unacknowledged";
                throw new MongoException($"Inventory save for uid {Uid} matched {matchCount} documents.");
            }
        }

        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("uid")]
        [BsonRequired]
        public long Uid { get; set; }

        [BsonElement("items")]
        [BsonRequired]
        public List<Item> Items { get; set; }

        [BsonElement("applied_reward_claims")]
        public List<string> AppliedRewardClaims { get; set; } = new();

        [BsonElement("reward_claim_times")]
        public Dictionary<string, long> RewardClaimTimes { get; set; } = new();

        // Cumulative debits per item id (string keys for BSON); the accumulate-expend shop counts Rainbow Card spend from this.
        [BsonElement("spent_totals")]
        public Dictionary<string, long> SpentTotals { get; set; } = new();

        internal void RecordSpent(int itemId, long amount)
        {
            if (amount <= 0) return;
            string key = itemId.ToString();
            SpentTotals[key] = SpentTotals.GetValueOrDefault(key) + amount;
        }
    }

    public partial class ItemConfig
    {
        [JsonProperty("Id")]
        public int Id { get; set; }

        [JsonProperty("Count")]
        public long Count { get; set; }
    }
}
