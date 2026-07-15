using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fashion;

namespace AscNet.GameServer.Handlers
{
    public class Reward
    {
        public int Id;
        public int Count = 1;
        public int Level = 1;
        public RewardType Type;
        public bool IsRecycle;
        public bool NotifyAsRecycle;
        public int ConvertFrom;
    }

    internal class RewardHandler
    {
        public static RewardType? GetRewardType(RewardGoodsTable reward)
        {
            var idVal = (int)MathF.Floor((reward.TemplateId > 0 ? reward.TemplateId : reward.Id) / 1000000);

            return idVal switch
            {
                3 => RewardType.Equip,
                // Unknown, not used in RewardGoodsTable
                4 or 5 => null,
                6 => RewardType.Fashion,
                7 => RewardType.BaseEquip,
                _ => (RewardType)(idVal + 1),
            };
        }

        public static List<RewardGoodsTable> GetRewardGoods(int rewardId)
        {
            RewardTable? rewardTable = TableReaderV2.Parse<RewardTable>().FirstOrDefault(x => x.Id == rewardId);
            if (rewardTable is null)
            {
                return [];
            }

            HashSet<int> subIds = rewardTable.SubIds.ToHashSet();
            if (subIds.Count == 0)
            {
                return [];
            }

            return TableReaderV2.Parse<RewardGoodsTable>()
                .Where(x => subIds.Contains(x.Id))
                .ToList();
        }

        public static List<RewardGoods> GiveRewards(IEnumerable<RewardGoodsTable> rewardGoods, Session session)
        {
            List<RewardGoods> rewardGoodsList = [];
            var rewards = rewardGoods.Select(x =>
            {
                var rewardType = GetRewardType(x);
                if (rewardType == null)
                {
                    session.log.Error($"Could not get reward type for template id {x.TemplateId} or id {x.Id}");
                    return null;
                }

                rewardGoodsList.Add(new()
                {
                    Id = x.Id,
                    TemplateId = x.TemplateId,
                    Count = x.Count,
                    RewardType = (int)rewardType,
                });

                return new Reward()
                {
                    Id = x.TemplateId,
                    Count = x.Count,
                    Type = (RewardType)rewardType,
                };
            }).OfType<Reward>();

            GiveRewards(rewards, session);
            return rewardGoodsList;
        }

        public static void GiveRewards(IEnumerable<Reward> rewards, Session session)
        {
            List<Reward> resolvedRewards = ResolveRewards(rewards, session);

            NotifyEquipDataList notifyEquipData = new();
            FashionSyncNotify fashionSync = new();
            NotifyCharacterDataList notifyCharacterData = new();
            NotifyItemDataList notifyItemData = new();
            NotifyWeaponFashionInfo notifyWeaponFashionInfo = new();

            foreach (var reward in resolvedRewards)
            {
                HandleReward(
                    reward,
                    session,
                    notifyItemData.ItemDataList,
                    notifyCharacterData.CharacterDataList,
                    fashionSync.FashionList,
                    notifyEquipData.EquipDataList,
                    notifyWeaponFashionInfo.WeaponFashionDataList
                );
            }

            if (notifyItemData.ItemDataList.Count > 0)
            {
                session.SendPush(notifyItemData);
            }

            if (notifyEquipData.EquipDataList.Count > 0)
            {
                session.SendPush(notifyEquipData);
            }

            if (fashionSync.FashionList.Count > 0)
            {
                session.SendPush(fashionSync);
            }

            if (notifyWeaponFashionInfo.WeaponFashionDataList.Count > 0)
            {
                session.SendPush(notifyWeaponFashionInfo);
            }

            if (notifyCharacterData.CharacterDataList.Count > 0)
            {
                session.SendPush(notifyCharacterData);
            }
        }

        public static List<Reward> ResolveRewards(IEnumerable<Reward> rewards, Session session)
        {
            List<Reward> resolvedRewards = [];
            HashSet<int> ownedCharacterIds = session.character.Characters.Select(x => (int)x.Id).ToHashSet();
            foreach (Reward reward in rewards)
            {
                List<Reward> resolved = ResolveReward(reward, session, ownedCharacterIds).ToList();
                resolvedRewards.AddRange(resolved);
                if (resolved.Count == 1 && resolved[0].Type == RewardType.Character)
                    ownedCharacterIds.Add(resolved[0].Id);
            }

            return resolvedRewards;
        }

        private static IEnumerable<Reward> ResolveReward(Reward reward, Session session, HashSet<int> ownedCharacterIds)
        {
            switch (reward.Type)
            {
                case RewardType.Item:
                    var itemData = TableReaderV2.Parse<ItemTable>().Find(x => x.Id == reward.Id);
                    if (itemData is not null)
                    {
                        if (itemData.ItemType == (int)ItemType.WeaponFashion)
                        {
                            return TryResolveWeaponFashionReward(itemData, out int weaponFashionId)
                                ? [new Reward
                                {
                                    Id = weaponFashionId,
                                    Count = 1,
                                    Type = RewardType.WeaponFashion,
                                }]
                                : [];
                        }
                        // Custom handler for some items that aren't meant to be in the inventory.
                        DropHandlerDelegate? dropHandler = DropsHandlerFactory.GetDropHandler(itemData.Id);
                        if (itemData.IsHidden() && dropHandler is not null)
                        {
                            return dropHandler.Invoke(session, reward.Count).Select(x => new Reward()
                            {
                                Id = x.TemplateId,
                                Count = x.Count,
                                Type = x.Type,
                                Level = x.Level,
                            });
                        }
                    }
                    break;
                case RewardType.Character:
                    if (ownedCharacterIds.Contains(reward.Id))
                    {
                        var characterData = TableReaderV2.Parse<CharacterTable>().Find(x => x.Id == reward.Id);
                        if (characterData == null) return [];

                        var decomposeCount = Character.GetMinCharacterFragment(reward.Id)?.DecomposeCount ?? 18;
                        if (!Inventory.IsValidClientItemId(characterData.ItemId))
                            return [];

                        return [new()
                        {
                            Id = characterData.ItemId,
                            Count = decomposeCount,
                            Type = RewardType.Item,
                            ConvertFrom = reward.Id,
                        }];
                    }
                    break;
            }

            return [reward];
        }

        internal static bool TryResolveWeaponFashionReward(int itemId, out int weaponFashionId)
        {
            ItemTable? item = TableReaderV2.Parse<ItemTable>().Find(x => x.Id == itemId);
            return TryResolveWeaponFashionReward(item, out weaponFashionId);
        }

        private static bool TryResolveWeaponFashionReward(ItemTable? item, out int weaponFashionId)
        {
            weaponFashionId = 0;
            if (item is null
                || item.ItemType != (int)ItemType.WeaponFashion
                || item.SubTypeParams.Count == 0
                || item.SubTypeParams[0] <= 0)
            {
                return false;
            }

            weaponFashionId = item.SubTypeParams[0];
            return true;
        }

        public static bool UnlockWeaponFashionReward(
            int weaponFashionId,
            Session session,
            List<WeaponFashionData>? weaponFashionDataList = null)
        {
            bool isCatalogWeaponFashion = TableReaderV2.Parse<ItemTable>()
                .Any(item => TryResolveWeaponFashionReward(item, out int mappedId)
                    && mappedId == weaponFashionId);
            if (!isCatalogWeaponFashion)
                return false;

            WeaponFashionData? existing = session.character.WeaponFashions
                .Find(x => x.Id == weaponFashionId);
            if (existing is null)
            {
                existing = new WeaponFashionData
                {
                    Id = weaponFashionId,
                    ExpireTime = 0,
                    UseCharacterList = []
                };
                session.character.WeaponFashions.Add(existing);
                weaponFashionDataList?.Add(existing);
                return true;
            }

            if (existing.ExpireTime == 0)
                return false;

            existing.ExpireTime = 0;
            weaponFashionDataList?.Add(existing);
            return true;
        }

        public static bool UnlockFashionReward(int fashionId, Session session, List<FashionList>? fashionList = null)
        {
            FashionTable? fashion = TableReaderV2.Parse<FashionTable>().Find(x => x.Id == fashionId);
            if (fashion is null)
                return false;

            FashionList? existingFashion = session.character.Fashions.Find(x => x.Id == fashionId);
            if (existingFashion is null)
            {
                existingFashion = new FashionList
                {
                    Id = fashionId,
                    IsLock = false
                };
                session.character.Fashions.Add(existingFashion);
                fashionList?.Add(existingFashion);
                return true;
            }

            if (!existingFashion.IsLock)
                return false;

            existingFashion.IsLock = false;
            fashionList?.Add(existingFashion);
            return true;
        }


        private static void HandleReward(
            Reward reward,
            Session session,
            List<Item> itemDataList,
            List<CharacterData> characterDataList,
            List<FashionList> fashionList,
            List<EquipData> equipDataList,
            List<WeaponFashionData> weaponFashionDataList
        ) {
            switch (reward.Type)
            {
                case RewardType.Item:
                    itemDataList.Add(session.inventory.Do(reward.Id, reward.Count));
                    break;
                case RewardType.Character:

                    var characterRet = session.character.AddCharacter((uint)reward.Id, level: reward.Level);
                    characterDataList.Add(characterRet.Character);
                    fashionList.Add(characterRet.Fashion);
                    if (characterRet.Equip is not null)
                        equipDataList.Add(characterRet.Equip);

                    break;
                case RewardType.Equip:
                    EquipData? equip = session.character.AddEquip((uint)reward.Id, level: reward.Level);
                    if (equip is not null)
                    {
                        equip.IsRecycle = reward.IsRecycle;
                        equipDataList.Add(reward.NotifyAsRecycle
                            ? CloneEquipForNotification(equip, isRecycle: true)
                            : equip);
                    }
                    break;
                case RewardType.Fashion:
                    UnlockFashionReward(reward.Id, session, fashionList);
                    break;
                case RewardType.BaseEquip:
                    break;
                case RewardType.Furniture:
                    break;
                case RewardType.HeadPortrait:
                    break;
                case RewardType.DormCharacter:
                    break;
                case RewardType.ChatEmoji:
                    break;
                case RewardType.WeaponFashion:
                    UnlockWeaponFashionReward(reward.Id, session, weaponFashionDataList);
                    break;
                case RewardType.Collection:
                    break;
                case RewardType.Background:
                    break;
                case RewardType.Pokemon:
                    break;
                case RewardType.Partner:
                    break;
                case RewardType.Nameplate:
                    break;
                case RewardType.RankScore:
                    break;
                case RewardType.Medal:
                    break;
                case RewardType.DrawTicket:
                    break;
            }
        }

        private static EquipData CloneEquipForNotification(EquipData equip, bool isRecycle)
        {
            return new EquipData
            {
                Id = equip.Id,
                TemplateId = equip.TemplateId,
                CharacterId = equip.CharacterId,
                Level = equip.Level,
                Exp = equip.Exp,
                Breakthrough = equip.Breakthrough,
                ResonanceInfo = equip.ResonanceInfo.ToList(),
                UnconfirmedResonanceInfo = equip.UnconfirmedResonanceInfo.ToList(),
                AwakeSlotList = equip.AwakeSlotList.ToList(),
                IsLock = equip.IsLock,
                CreateTime = equip.CreateTime,
                WeaponOverrunData = new WeaponOverrunData
                {
                    Level = equip.WeaponOverrunData.Level,
                    ActiveSuits = equip.WeaponOverrunData.ActiveSuits.ToList(),
                    ChoseSuit = equip.WeaponOverrunData.ChoseSuit
                },
                IsRecycle = isRecycle
            };
        }
    }
}
