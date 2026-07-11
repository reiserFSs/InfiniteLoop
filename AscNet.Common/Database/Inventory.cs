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
    public class Inventory
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

        public static readonly IMongoCollection<Inventory> collection = Common.db.GetCollection<Inventory>("inventory");
        private static readonly Lazy<HashSet<int>> ClientItemIds = new(() =>
            TableReaderV2.Parse<ItemTable>().Select(item => item.Id).ToHashSet());

        public static bool IsValidClientItemId(int itemId)
        {
            return itemId > 0 && ClientItemIds.Value.Contains(itemId);
        }

        public static List<Item> FilterClientItems(IEnumerable<Item> items)
        {
            return items.Where(item => IsValidClientItemId(item.Id)).ToList();
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
            if (!IsValidClientItemId(itemId))
                throw new InvalidDataException($"Cannot mutate unknown client item id {itemId}.");

            Item? item = Items.FirstOrDefault(x => x.Id == itemId);
            ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().Find(x => x.Id == itemId);
            long maxCount = GetMaxCount(itemTable);

            if (item is not null && itemTable is not null)
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

        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("uid")]
        [BsonRequired]
        public long Uid { get; set; }

        [BsonElement("items")]
        [BsonRequired]
        public List<Item> Items { get; set; }
    }

    public partial class ItemConfig
    {
        [JsonProperty("Id")]
        public int Id { get; set; }

        [JsonProperty("Count")]
        public long Count { get; set; }
    }
}
