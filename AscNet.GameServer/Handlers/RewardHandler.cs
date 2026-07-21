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
using AscNet.Table.V2.share.headportrait;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

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

    internal sealed record RewardGrant(
        string ClaimKey,
        IReadOnlyList<RewardGoodsTable> Goods);

    internal sealed class RewardApplicationResult
    {
        public List<RewardGoods> RewardGoods { get; } = [];
        internal NotifyEquipDataList EquipData { get; } = new();
        internal FashionSyncNotify FashionData { get; } = new();
        internal NotifyCharacterDataList CharacterData { get; } = new();
        internal NotifyItemDataList ItemData { get; } = new();
        internal NotifyWeaponFashionInfo WeaponFashionData { get; } = new();
        internal NotifyHeadPortraitInfos HeadPortraitData { get; } = new();
        internal bool DormFurnitureChanged { get; set; }


        public void SendPushes(Session session)
        {
            if (ItemData.ItemDataList.Count > 0)
                session.SendPush(ItemData);
            if (EquipData.EquipDataList.Count > 0)
                session.SendPush(EquipData);
            if (FashionData.FashionList.Count > 0 || FashionData.FashionColors.Count > 0)
                session.SendPush(FashionData);
            if (WeaponFashionData.WeaponFashionDataList.Count > 0)
                session.SendPush(WeaponFashionData);
            if (CharacterData.CharacterDataList.Count > 0)
                session.SendPush(CharacterData);
            if (HeadPortraitData.Heads.Count > 0)
                session.SendPush(HeadPortraitData);
        }

        internal void AddPushes(RewardApplicationResult source)
        {
            ItemData.ItemDataList.AddRange(source.ItemData.ItemDataList);
            EquipData.EquipDataList.AddRange(source.EquipData.EquipDataList);
            FashionData.FashionList.AddRange(source.FashionData.FashionList);
            foreach ((int fashionId, List<int> colorIds) in source.FashionData.FashionColors)
            {
                if (!FashionData.FashionColors.TryGetValue(fashionId, out List<int>? merged))
                {
                    merged = [];
                    FashionData.FashionColors.Add(fashionId, merged);
                }
                foreach (int colorId in colorIds)
                {
                    if (!merged.Contains(colorId))
                        merged.Add(colorId);
                }
            }
            WeaponFashionData.WeaponFashionDataList.AddRange(
                source.WeaponFashionData.WeaponFashionDataList);
            CharacterData.CharacterDataList.AddRange(source.CharacterData.CharacterDataList);
            foreach (HeadPortraitList head in source.HeadPortraitData.Heads)
            {
                if (!HeadPortraitData.Heads.Any(existing => existing.Id == head.Id))
                    HeadPortraitData.Heads.Add(head);
            }
            DormFurnitureChanged |= source.DormFurnitureChanged;

        }
    }

    internal class RewardHandler
    {
        private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<RewardGoodsTable>>> RewardGoodsByRewardId = new(() =>
        {
            Dictionary<int, List<RewardGoodsTable>> goodsByRewardId = [];
            Dictionary<int, List<List<RewardGoodsTable>>> rewardsByGoodsId = [];
            foreach (RewardTable reward in TableReaderV2.Parse<RewardTable>())
            {
                if (!goodsByRewardId.TryAdd(reward.Id, []))
                    continue;

                List<RewardGoodsTable> goods = goodsByRewardId[reward.Id];
                foreach (int subId in reward.SubIds.Distinct())
                {
                    if (!rewardsByGoodsId.TryGetValue(subId, out List<List<RewardGoodsTable>>? rewards))
                    {
                        rewards = [];
                        rewardsByGoodsId.Add(subId, rewards);
                    }
                    rewards.Add(goods);
                }
            }

            foreach (RewardGoodsTable goods in TableReaderV2.Parse<RewardGoodsTable>())
            {
                if (rewardsByGoodsId.TryGetValue(goods.Id, out List<List<RewardGoodsTable>>? rewards))
                    foreach (List<RewardGoodsTable> rewardGoods in rewards)
                        rewardGoods.Add(goods);
            }

            return goodsByRewardId.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<RewardGoodsTable>)entry.Value.ToArray());
        });

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
            return RewardGoodsByRewardId.Value.TryGetValue(rewardId, out IReadOnlyList<RewardGoodsTable>? rewardGoods)
                ? new List<RewardGoodsTable>(rewardGoods)
                : [];
        }

        public static RewardApplicationResult ApplyRewards(
            IEnumerable<RewardGoodsTable> rewardGoods,
            Session session)
        {
            RewardApplicationResult result = new();
            List<Reward> rewards = PrepareRewards(rewardGoods, session, result);
            ApplyRewards(rewards, session, result);
            return result;
        }

        public static RewardApplicationResult ApplyRewards(
            IEnumerable<Reward> rewards,
            Session session)
        {
            RewardApplicationResult result = new();
            ApplyRewards(rewards, session, result);
            return result;
        }

        public static RewardApplicationResult ApplyRewardsOnceAndPersist(
            IReadOnlyList<RewardGrant> grants,
            Session session)
        {
            if (grants.Count == 0)
                throw new ArgumentException("At least one reward grant is required.", nameof(grants));
            if (grants.Any(grant =>
                    string.IsNullOrWhiteSpace(grant.ClaimKey)
                    || grant.ClaimKey.Length > 128
                    || grant.Goods.Count == 0))
                throw new ArgumentException("Reward grants require a bounded claim key and configured goods.", nameof(grants));
            if (grants.Select(grant => grant.ClaimKey).Distinct(StringComparer.Ordinal).Count() != grants.Count)
                throw new ArgumentException("Reward claim keys must be unique within a grant batch.", nameof(grants));

            Inventory originalInventory = session.inventory;
            Character originalCharacter = session.character;
            List<HeadPortraitList> originalHeadPortraits = session.player.HeadPortraits
                .Select(head => new HeadPortraitList
                {
                    Id = head.Id,
                    LeftCount = head.LeftCount,
                    BeginTime = head.BeginTime
                })
                .ToList();
            PlayerDormState? originalDorm = grants.SelectMany(grant => grant.Goods)
                .Any(goods => GetRewardType(goods) == RewardType.Furniture)
                ? BsonSerializer.Deserialize<PlayerDormState>(session.player.Dorm.ToBson())
                : null;

            Inventory stagedInventory =
                BsonSerializer.Deserialize<Inventory>(originalInventory.ToBson());
            Character stagedCharacter =
                BsonSerializer.Deserialize<Character>(originalCharacter.ToBson());
            stagedInventory.AppliedRewardClaims ??= [];
            stagedCharacter.AppliedRewardClaims ??= [];
            session.inventory = stagedInventory;
            session.character = stagedCharacter;

            bool inventoryDirty = false;
            bool characterDirty = false;
            bool inventoryPersisted = false;
            bool characterPersisted = false;
            bool playerPersisted = false;
            try
            {
                RewardApplicationResult result = new();
                foreach (RewardGrant grant in grants)
                {
                    bool inventoryClaimed = stagedInventory.AppliedRewardClaims.Contains(
                        grant.ClaimKey,
                        StringComparer.Ordinal);
                    bool characterClaimed = stagedCharacter.AppliedRewardClaims.Contains(
                        grant.ClaimKey,
                        StringComparer.Ordinal);

                    RewardApplicationResult grantResult = new();
                    List<Reward> prepared = PrepareRewards(grant.Goods, session, grantResult);
                    if (prepared.Count != grant.Goods.Count)
                        throw new InvalidDataException(
                            $"Reward claim {grant.ClaimKey} contains an unsupported reward type.");
                    result.RewardGoods.AddRange(grantResult.RewardGoods);

                    foreach (Reward reward in prepared.Where(reward =>
                                 (inventoryClaimed && IsInventoryDocumentReward(reward))
                                 || (characterClaimed && IsCharacterDocumentReward(reward))))
                    {
                        AddCurrentStatePush(reward, session, grantResult);
                    }
                    List<Reward> resolved = ResolveRewards(prepared, session);
                    foreach (Reward reward in resolved.Where(reward =>
                                 (inventoryClaimed && IsInventoryDocumentReward(reward))
                                 || (characterClaimed && IsCharacterDocumentReward(reward))))
                    {
                        AddCurrentStatePush(reward, session, grantResult);
                    }
                    ApplyResolvedRewards(
                        resolved.Where(reward =>
                            (!inventoryClaimed && IsInventoryDocumentReward(reward))
                            || (!characterClaimed && IsCharacterDocumentReward(reward))),
                        session,
                        grantResult);
                    result.AddPushes(grantResult);

                    if (!inventoryClaimed)
                    {
                        stagedInventory.AppliedRewardClaims.Add(grant.ClaimKey);
                        inventoryDirty = true;
                    }
                    if (!characterClaimed)
                    {
                        stagedCharacter.AppliedRewardClaims.Add(grant.ClaimKey);
                        characterDirty = true;
                    }
                }

                if (inventoryDirty)
                {
                    stagedInventory.SaveChecked();
                    inventoryPersisted = true;
                }
                if (characterDirty)
                {
                    stagedCharacter.SaveChecked();
                    characterPersisted = true;
                }
                if (result.DormFurnitureChanged
                    || session.player.HeadPortraits.Count != originalHeadPortraits.Count)
                {
                    session.player.SaveChecked();
                    playerPersisted = true;
                }


                CopyInventory(originalInventory, stagedInventory);
                CopyCharacter(originalCharacter, stagedCharacter);
                return result;
            }
            catch
            {
                if (inventoryPersisted)
                    CopyInventory(originalInventory, stagedInventory);
                if (characterPersisted)
                    CopyCharacter(originalCharacter, stagedCharacter);
                if (!playerPersisted)
                {
                    session.player.HeadPortraits = originalHeadPortraits;
                    if (originalDorm is not null)
                        session.player.Dorm = originalDorm;
                }
                throw;
            }
            finally
            {
                session.inventory = originalInventory;
                session.character = originalCharacter;
            }
        }

        private static List<Reward> PrepareRewards(
            IEnumerable<RewardGoodsTable> rewardGoods,
            Session session,
            RewardApplicationResult result)
        {
            List<Reward> rewards = [];
            foreach (RewardGoodsTable row in rewardGoods)
            {
                RewardType? rewardType = GetRewardType(row);
                if (rewardType is null)
                {
                    session.log.Error(
                        $"Could not get reward type for template id {row.TemplateId} or id {row.Id}");
                    continue;
                }

                result.RewardGoods.Add(new RewardGoods
                {
                    Id = row.Id,
                    TemplateId = row.TemplateId,
                    Count = row.Count,
                    RewardType = (int)rewardType
                });
                rewards.Add(new Reward
                {
                    Id = row.TemplateId,
                    Count = row.Count,
                    Type = rewardType.Value
                });
            }
            return rewards;
        }
        private static bool IsInventoryDocumentReward(Reward reward) =>
            reward.Type == RewardType.Item;

        private static bool IsCharacterDocumentReward(Reward reward) =>
            reward.Type is RewardType.Character
                or RewardType.Equip
                or RewardType.Fashion
                or RewardType.WeaponFashion
                or RewardType.FashionColor
                or RewardType.Furniture
                or RewardType.HeadPortrait;

        private static void AddCurrentStatePush(
            Reward reward,
            Session session,
            RewardApplicationResult result)
        {
            switch (reward.Type)
            {
                case RewardType.Item:
                    Item? item = session.inventory.Items.FirstOrDefault(entry => entry.Id == reward.Id);
                    if (item is not null
                        && result.ItemData.ItemDataList.All(entry => entry.Id != item.Id))
                        result.ItemData.ItemDataList.Add(item);
                    break;
                case RewardType.Character:
                    CharacterData? character = session.character.Characters
                        .FirstOrDefault(entry => entry.Id == (uint)reward.Id);
                    if (character is null)
                        break;
                    if (result.CharacterData.CharacterDataList.All(entry => entry.Id != character.Id))
                        result.CharacterData.CharacterDataList.Add(character);
                    FashionList? characterFashion = session.character.Fashions
                        .FirstOrDefault(entry => entry.Id == character.FashionId);
                    if (characterFashion is not null
                        && result.FashionData.FashionList.All(entry => entry.Id != characterFashion.Id))
                        result.FashionData.FashionList.Add(characterFashion);
                    foreach (EquipData equip in session.character.Equips.Where(
                                 entry => entry.CharacterId == character.Id))
                    {
                        if (result.EquipData.EquipDataList.All(entry => entry.Id != equip.Id))
                            result.EquipData.EquipDataList.Add(equip);
                    }
                    break;
                case RewardType.Equip:
                    foreach (EquipData equip in session.character.Equips.Where(
                                 entry => entry.TemplateId == (uint)reward.Id))
                    {
                        if (result.EquipData.EquipDataList.All(entry => entry.Id != equip.Id))
                            result.EquipData.EquipDataList.Add(equip);
                    }
                    break;
                case RewardType.Fashion:
                    FashionList? fashion = session.character.Fashions
                        .FirstOrDefault(entry => entry.Id == reward.Id);
                    if (fashion is not null
                        && result.FashionData.FashionList.All(entry => entry.Id != fashion.Id))
                        result.FashionData.FashionList.Add(fashion);
                    FashionTable? fashionTable = TableReaderV2.Parse<FashionTable>()
                        .FirstOrDefault(entry => entry.Id == reward.Id);
                    AddCurrentHeadPortraitPush(fashionTable?.GiftId ?? 0, session, result);
                    break;
                case RewardType.HeadPortrait:
                    AddCurrentHeadPortraitPush(reward.Id, session, result);
                    break;
                case RewardType.FashionColor:
                    AddOwnedFashionColorPush(reward.Id, session.character, result.FashionData);
                    break;
                case RewardType.WeaponFashion:
                    WeaponFashionData? weaponFashion = session.character.WeaponFashions
                        .FirstOrDefault(entry => entry.Id == reward.Id);
                    if (weaponFashion is not null
                        && result.WeaponFashionData.WeaponFashionDataList.All(
                            entry => entry.Id != weaponFashion.Id))
                        result.WeaponFashionData.WeaponFashionDataList.Add(weaponFashion);
                    break;
            }
        }
        private static void AddCurrentHeadPortraitPush(
            int headPortraitId,
            Session session,
            RewardApplicationResult result)
        {
            UnlockHeadPortraitReward(headPortraitId, session, result.HeadPortraitData.Heads);
            HeadPortraitList? head = session.player.HeadPortraits
                .FirstOrDefault(entry => entry.Id == headPortraitId);
            if (head is not null
                && result.HeadPortraitData.Heads.All(entry => entry.Id != head.Id))
            {
                result.HeadPortraitData.Heads.Add(head);
            }
        }


        private static void CopyInventory(Inventory target, Inventory source)
        {
            target.Id = source.Id;
            target.Uid = source.Uid;
            target.Items = source.Items;
            target.AppliedRewardClaims = source.AppliedRewardClaims;
        }

        private static void CopyCharacter(Character target, Character source)
        {
            target.Id = source.Id;
            target.Uid = source.Uid;
            target.Characters = source.Characters;
            target.Equips = source.Equips;
            target.Fashions = source.Fashions;
            target.WeaponFashions = source.WeaponFashions;
            target.Partners = source.Partners;
            target.AppliedRewardClaims = source.AppliedRewardClaims;
            target.FashionColors = source.FashionColors;
        }

        private static void ApplyRewards(
            IEnumerable<Reward> rewards,
            Session session,
            RewardApplicationResult result)
        {
            ApplyResolvedRewards(ResolveRewards(rewards, session), session, result);
        }

        private static void ApplyResolvedRewards(
            IEnumerable<Reward> rewards,
            Session session,
            RewardApplicationResult result)
        {
            List<Reward> resolved = rewards.ToList();
            PlayerDormState? dorm = resolved.Any(reward => reward.Type == RewardType.Furniture)
                ? BsonSerializer.Deserialize<PlayerDormState>(session.player.Dorm.ToBson())
                : null;
            try
            {
                foreach (Reward reward in resolved)
                {
                    HandleReward(
                        reward,
                        session,
                        result,
                        result.ItemData.ItemDataList,
                        result.CharacterData.CharacterDataList,
                        result.FashionData,
                        result.EquipData.EquipDataList,
                        result.WeaponFashionData.WeaponFashionDataList,
                        result.HeadPortraitData.Heads);
                }
            }
            catch
            {
                if (dorm is not null) session.player.Dorm = dorm;
                throw;
            }
        }

        public static List<RewardGoods> GiveRewards(
            IEnumerable<RewardGoodsTable> rewardGoods,
            Session session)
        {
            RewardApplicationResult result = ApplyRewards(rewardGoods, session);
            if (result.DormFurnitureChanged || result.HeadPortraitData.Heads.Count > 0)
                session.player.Save();
            result.SendPushes(session);
            return result.RewardGoods;
        }

        public static void GiveRewards(IEnumerable<Reward> rewards, Session session)
        {
            RewardApplicationResult result = ApplyRewards(rewards, session);
            if (result.DormFurnitureChanged || result.HeadPortraitData.Heads.Count > 0)
                session.player.Save();
            result.SendPushes(session);
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

        public static bool UnlockFashionReward(
            int fashionId,
            Session session,
            List<FashionList>? fashionList = null,
            List<HeadPortraitList>? headPortraits = null)
        {
            FashionTable? fashion = TableReaderV2.Parse<FashionTable>().Find(x => x.Id == fashionId);
            if (fashion is null)
                return false;

            FashionList? existingFashion = session.character.Fashions.Find(x => x.Id == fashionId);
            bool changed = existingFashion is null || existingFashion.IsLock;
            if (existingFashion is null)
            {
                existingFashion = new FashionList
                {
                    Id = fashionId,
                    IsLock = false
                };
                session.character.Fashions.Add(existingFashion);
            }
            else if (existingFashion.IsLock)
            {
                existingFashion.IsLock = false;
            }

            if (changed)
                fashionList?.Add(existingFashion);
            UnlockHeadPortraitReward(fashion.GiftId ?? 0, session, headPortraits);
            return changed;
        }

        public static bool UnlockHeadPortraitReward(
            int headPortraitId,
            Session session,
            List<HeadPortraitList>? headPortraits = null)
        {
            if (headPortraitId <= 0
                || !TableReaderV2.Parse<HeadPortraitTable>().Any(row => row.Id == headPortraitId)
                || session.player.HeadPortraits.Any(head => head.Id == headPortraitId))
            {
                return false;
            }

            session.player.AddHead(headPortraitId);
            headPortraits?.Add(session.player.HeadPortraits[^1]);
            return true;
        }

        public static bool UnlockFashionColorReward(
            int colorId,
            Session session,
            FashionSyncNotify? fashionSync = null)
        {
            FashionColorTable? color = TableReaderV2.Parse<FashionColorTable>()
                .FirstOrDefault(row => row.Id == colorId);
            if (color is null)
                return false;

            session.character.FashionColors ??= [];
            if (!session.character.FashionColors.TryGetValue(
                    color.OriginalFashionId,
                    out List<int>? ownedColors))
            {
                ownedColors = [];
                session.character.FashionColors.Add(color.OriginalFashionId, ownedColors);
            }

            bool changed = !ownedColors.Contains(color.Id);
            if (changed)
                ownedColors.Add(color.Id);
            AddOwnedFashionColorPush(color.Id, session.character, fashionSync);
            return changed;
        }

        private static void AddOwnedFashionColorPush(
            int colorId,
            Character character,
            FashionSyncNotify? fashionSync)
        {
            if (fashionSync is null)
                return;

            FashionColorTable? color = TableReaderV2.Parse<FashionColorTable>()
                .FirstOrDefault(row => row.Id == colorId);
            if (color is null
                || character.FashionColors is null
                || !character.FashionColors.TryGetValue(
                    color.OriginalFashionId,
                    out List<int>? ownedColors)
                || !ownedColors.Contains(color.Id))
            {
                return;
            }

            if (!fashionSync.FashionColors.TryGetValue(
                    color.OriginalFashionId,
                    out List<int>? pushedColors))
            {
                pushedColors = [];
                fashionSync.FashionColors.Add(color.OriginalFashionId, pushedColors);
            }
            if (!pushedColors.Contains(color.Id))
                pushedColors.Add(color.Id);
        }


        private static void HandleReward(
            Reward reward,
            Session session,
            RewardApplicationResult result,
            List<Item> itemDataList,
            List<CharacterData> characterDataList,
            FashionSyncNotify fashionData,
            List<EquipData> equipDataList,
            List<WeaponFashionData> weaponFashionDataList,
            List<HeadPortraitList> headPortraits
        ) {
            switch (reward.Type)
            {
                case RewardType.Item:
                    itemDataList.Add(session.inventory.Do(reward.Id, reward.Count));
                    break;
                case RewardType.Character:

                    var characterRet = session.character.AddCharacter((uint)reward.Id, level: reward.Level);
                    characterDataList.Add(characterRet.Character);
                    fashionData.FashionList.Add(characterRet.Fashion);
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
                    UnlockFashionReward(reward.Id, session, fashionData.FashionList, headPortraits);
                    break;
                case RewardType.BaseEquip:
                    break;
                case RewardType.Furniture:
                    if (!DormModule.TryGrantFurnitureReward(session, reward.Id, reward.Count))
                        throw new InvalidDataException($"Invalid furniture reward {reward.Id}.");
                    result.DormFurnitureChanged = true;
                    break;
                case RewardType.HeadPortrait:
                    UnlockHeadPortraitReward(reward.Id, session, headPortraits);
                    break;
                case RewardType.DormCharacter:
                    break;
                case RewardType.ChatEmoji:
                    break;
                case RewardType.WeaponFashion:
                    UnlockWeaponFashionReward(reward.Id, session, weaponFashionDataList);
                    break;
                case RewardType.FashionColor:
                    UnlockFashionColorReward(reward.Id, session, fashionData);
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
