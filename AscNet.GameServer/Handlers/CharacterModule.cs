using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.trust;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.item;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class CharacterUpgradeEnhanceSkillRequest
    {
        public int Count;
        public int SkillGroupId;
    }

    [MessagePackObject(true)]
    public class CharacterUpgradeEnhanceSkillResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterUnlockEnhanceSkillRequest
    {
        public int SkillGroupId;
    }

    [MessagePackObject(true)]
    public class CharacterUnlockEnhanceSkillResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterEnhanceSkillNoticeRequest
    {
        public int CharacterId;
    }
    [MessagePackObject(true)]
    public class CharacterEnhanceSkillNoticeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchEnhanceSkillRequest
    {
        public int SkillId;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchEnhanceSkillResponse
    {
        public int Code;
    }
    [MessagePackObject(true)]
    public class CharacterResetNewFlagRequest
    {
        public List<int> CharacterIds = new();
    }

    [MessagePackObject(true)]
    public class CharacterResetNewFlagResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterLevelUpRequest
    {
        public uint TemplateId;
        public Dictionary<int, int> UseItems;
    }

    [MessagePackObject(true)]
    public class CharacterLevelUpResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterUnlockSkillGroupRequest
    {
        public int SkillGroupId;
    }

    [MessagePackObject(true)]
    public class CharacterUnlockSkillGroupResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchSkillRequest
    {
        public int SkillId;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchSkillResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterPromoteQualityRequest
    {
        public int TemplateId;
    }

    [MessagePackObject(true)]
    public class CharacterPromoteQualityResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterActivateStarRequest
    {
        public int TemplateId;
    }

    [MessagePackObject(true)]
    public class CharacterActivateStarResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterPromoteGradeRequest
    {
        public int TemplateId;
    }

    [MessagePackObject(true)]
    public class CharacterPromoteGradeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterExchangeRequest
    {
        public int TemplateId;
    }

    [MessagePackObject(true)]
    public class CharacterExchangeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterSetCollectStateRequest
    {
        public int TemplateId;
        public int CharacterId;
        public int Id;
        public bool CollectState;

        [IgnoreMember]
        public int TargetCharacterId => TemplateId != 0 ? TemplateId : CharacterId != 0 ? CharacterId : Id;
    }

    [MessagePackObject(true)]
    public class CharacterSetCollectStateResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchLiberateMagicIdRequest
    {
        public int MagicId;
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class CharacterSwitchLiberateMagicIdResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterSetHeadInfoRequest
    {
        public int TemplateId;
        public CharacterData.CharacterHead CharacterHeadInfo;
    }

    [MessagePackObject(true)]
    public class CharacterSetHeadInfoResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class FashionSyncNotify
    {
        public List<FashionList> FashionList = new();
        public Dictionary<int, List<int>> FashionColors = new();
    }

    [MessagePackObject(true)]
    public class CharacterSendGiftRequest
    {
        public int TemplateId;
        public Dictionary<int, int> GiftItems;
    }

    [MessagePackObject(true)]
    public class CharacterSendGiftResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class NotifyCharacterTrustInfo
    {
        public int TemplateId;
        public int TrustLv;
        public int TrustExp;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class CharacterModule
    {
        private static void SaveCharacterProgress(Session session)
        {
            session.character.Save();
            session.inventory.Save();
        }

        private static void AccumulateEnhanceSkillCosts(
            Dictionary<int, int> costs,
            EnhanceSkillUpgradeTable upgrade)
        {
            if (upgrade.CostItem is null || upgrade.CostItemCount is null)
                return;
            int costCount = Math.Min(upgrade.CostItem.Count, upgrade.CostItemCount.Count);
            for (int index = 0; index < costCount; index++)
            {
                int itemId = upgrade.CostItem[index];
                int count = upgrade.CostItemCount[index];
                if (itemId <= 0 || count <= 0)
                    continue;
                costs[itemId] = costs.GetValueOrDefault(itemId) + count;
            }
        }
        private static bool HasEnoughItems(Session session, int itemId, int required)
        {
            if (required <= 0)
                return true;
            Item? item = session.inventory.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            return item is not null && item.Count >= required;
        }
        private static bool HasEnhanceCost(EnhanceSkillUpgradeTable upgrade)
        {
            if (upgrade.CostItem is null || upgrade.CostItemCount is null)
                return false;
            int costCount = Math.Min(upgrade.CostItem.Count, upgrade.CostItemCount.Count);
            for (int index = 0; index < costCount; index++)
            {
                if (upgrade.CostItem[index] > 0 && upgrade.CostItemCount[index] > 0)
                    return true;
            }
            return false;
        }

        private static bool HasEnoughInventory(Session session, IReadOnlyDictionary<int, int> costs)
        {
            foreach ((int itemId, int count) in costs)
            {
                if (count <= 0)
                    continue;
                Item? item = session.inventory.Items.FirstOrDefault(candidate => candidate.Id == itemId);
                if (item is null || item.Count < count)
                    return false;
            }
            return true;
        }

        [RequestPacketHandler("CharacterLevelUpRequest")]
        public static void CharacterLevelUpRequestHandler(Session session, Packet.Request packet)
        {
            CharacterLevelUpRequest request = packet.Deserialize<CharacterLevelUpRequest>();
            CharacterTable? characterData = TableReaderV2.Parse<CharacterTable>().FirstOrDefault(x => x.Id == request.TemplateId);

            CharacterData? character = session.character.Characters.FirstOrDefault(x => x.Id == characterData?.Id);
            if (characterData is null || character is null)
            {
                // CharacterManagerGetCharacterTemplateNotFound
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009001 }, packet.Id);
                return;
            }

            if (character.Level >= session.player.PlayerData.Level)
            {
                // CharacterManagerLevelUpMaxLevel
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009014 }, packet.Id);
                return;
            }

            int? highestConfiguredLevel = Character.characterLevelUpTemplates
                .Where(x => x.Type == characterData.LevelUpTemplateId)
                .Select(x => (int?)x.Level)
                .Max();
            if (highestConfiguredLevel is null)
            {
                // CharacterManagerGetLevelUpTemplateNotFound
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009002 }, packet.Id);
                return;
            }

            if (character.Level >= highestConfiguredLevel.Value)
            {
                // CharacterManagerLevelUpMaxLevel
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009014 }, packet.Id);
                return;
            }

            CharacterLevelUpTemplate? levelUpTemplate = Character.characterLevelUpTemplates.FirstOrDefault(x => x.Level == character.Level && x.Type == characterData.LevelUpTemplateId);
            if (levelUpTemplate is null)
            {
                // CharacterManagerGetLevelUpTemplateNotFound
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009002 }, packet.Id);
                return;
            }

            bool hasNextLevelTemplate = Character.characterLevelUpTemplates.Any(x =>
                x.Level == character.Level + 1 && x.Type == characterData.LevelUpTemplateId);
            if (!hasNextLevelTemplate)
            {
                // CharacterManagerGetLevelUpTemplateNotFound
                session.SendResponse(new CharacterLevelUpResponse() { Code = 20009002 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = new();
            int totalExp = 0;
            foreach (var item in request.UseItems)
            {
                ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().FirstOrDefault(x => x.Id == item.Key);
                int itemExp = itemTable?.GetCharacterExp(characterData.Type) ?? 0;
                if (itemExp <= 0 || item.Value <= 0)
                {
                    continue;
                }

                totalExp += itemExp * item.Value;
                notifyItemData.ItemDataList.Add(session.inventory.Do(item.Key, item.Value * -1));
            }

            if (notifyItemData.ItemDataList.Count > 0)
            {
                session.SendPush(notifyItemData);
            }

            var characterUp = session.character.AddCharacterExp(characterData.Id, totalExp, (int)session.player.PlayerData.Level);
            if (characterUp is not null)
            {
                NotifyCharacterDataList notifyCharacterData = new();
                notifyCharacterData.CharacterDataList.Add(characterUp);
                session.SendPush(notifyCharacterData);
            }

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterLevelUpResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSendGiftRequest")]
        public static void CharacterSendGiftRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSendGiftRequest request = packet.Deserialize<CharacterSendGiftRequest>();
            CharacterData? character = session.character.Characters.Find(candidate => candidate.Id == request.TemplateId);
            if (character is null)
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009001 }, packet.Id);
                return;
            }

            if (request.GiftItems is null || request.GiftItems.Count == 0 || request.GiftItems.Any(gift => gift.Value <= 0))
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009037 }, packet.Id);
                return;
            }

            Dictionary<int, CharacterTrustItemTable> gifts = TableReaderV2.Parse<CharacterTrustItemTable>()
                .ToDictionary(gift => gift.Id);
            Dictionary<int, CharacterTrustExpTable> levels = TableReaderV2.Parse<CharacterTrustExpTable>()
                .Where(level => level.CharacterId == request.TemplateId)
                .GroupBy(level => level.TrustLv)
                .ToDictionary(group => group.Key, group => group.First());

            if (request.GiftItems.Keys.Any(itemId => !gifts.ContainsKey(itemId)))
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009036 }, packet.Id);
                return;
            }

            if (request.GiftItems.Any(gift => !session.inventory.Items.Any(item => item.Id == gift.Key && item.Count >= gift.Value)))
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009037 }, packet.Id);
                return;
            }

            int trustLevel;
            int trustExp;
            int totalExp = 0;
            try
            {
                trustLevel = checked((int)character.TrustLv);
                trustExp = checked((int)character.TrustExp);
                if (!levels.ContainsKey(trustLevel))
                    throw new KeyNotFoundException();

                foreach ((int itemId, int count) in request.GiftItems)
                {
                    CharacterTrustItemTable gift = gifts[itemId];
                    int itemExp = gift.FavorCharacterId.Contains(request.TemplateId) ? gift.FavorExp ?? gift.Exp : gift.Exp;
                    totalExp = checked(totalExp + checked(itemExp * count));
                }

                trustExp = checked(trustExp + totalExp);
                while (levels[trustLevel].Exp is int requiredExp && requiredExp > 0)
                {
                    if (trustExp < requiredExp)
                        break;

                    trustExp -= requiredExp;
                    trustLevel = checked(trustLevel + 1);
                    if (!levels.ContainsKey(trustLevel))
                        throw new KeyNotFoundException();
                }

                if (levels[trustLevel].Exp.GetValueOrDefault() == 0)
                    trustExp = 0;
            }
            catch (OverflowException)
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009037 }, packet.Id);
                return;
            }
            catch (KeyNotFoundException)
            {
                session.SendResponse(new CharacterSendGiftResponse() { Code = 20009038 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItems = new();
            foreach ((int itemId, int count) in request.GiftItems)
                notifyItems.ItemDataList.Add(session.inventory.Do(itemId, -count));

            character.TrustLv = trustLevel;
            character.TrustExp = trustExp;
            session.SendPush(new NotifyCharacterTrustInfo()
            {
                TemplateId = request.TemplateId,
                TrustLv = trustLevel,
                TrustExp = trustExp
            });
            session.SendPush(notifyItems);
            session.SendPush(new NotifyCharacterDataList() { CharacterDataList = { character } });
            SaveCharacterProgress(session);
            session.SendResponse(new CharacterSendGiftResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSetCollectStateRequest")]
        public static void CharacterSetCollectStateRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSetCollectStateRequest request = packet.Deserialize<CharacterSetCollectStateRequest>();
            CharacterData? character = session.character.Characters.Find(c => c.Id == request.TargetCharacterId);
            if (character is null)
            {
                // CharacterManagerGetCharacterByIdNotFound
                session.SendResponse(new CharacterSetCollectStateResponse() { Code = 20009011 }, packet.Id);
                return;
            }

            character.CollectState = request.CollectState;
            session.SendPush(new NotifyCharacterDataList()
            {
                CharacterDataList = { character }
            });

            session.character.Save();

            session.SendResponse(new CharacterSetCollectStateResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSwitchLiberateMagicIdRequest")]
        public static void CharacterSwitchLiberateMagicIdRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSwitchLiberateMagicIdRequest request = packet.Deserialize<CharacterSwitchLiberateMagicIdRequest>();
            CharacterData? character = session.character.Characters.Find(c => c.Id == request.CharacterId);
            if (character is null)
            {
                session.SendResponse(new CharacterSwitchLiberateMagicIdResponse() { Code = 20009011 }, packet.Id);
                return;
            }

            character.MagicList = new List<CharacterSkill>()
            {
                new() { Id = (uint)request.MagicId, Level = 1 }
            };
            session.SendPush(new NotifyCharacterDataList()
            {
                CharacterDataList = { character }
            });
            session.character.Save();
            session.SendResponse(new CharacterSwitchLiberateMagicIdResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterPromoteGradeRequest")]
        public static void CharacterPromoteGradeRequestHandler(Session session, Packet.Request packet)
        {
            CharacterPromoteGradeRequest req = packet.Deserialize<CharacterPromoteGradeRequest>();
            CharacterData? character = session.character.Characters.Find(c => c.Id == req.TemplateId);


            try
            {
                if (character is null)
                {
                    // CharacterManagerGetCharacterByIdNotFound
                    throw new ServerCodeException("Character data not found!", 20009011);
                }

                List<CharacterGradeTable> gradeRows = TableReaderV2.Parse<CharacterGradeTable>()
                    .Where(x => x.CharacterId == req.TemplateId)
                    .ToList();
                CharacterGradeTable? currentGrade = gradeRows.Find(x => x.Grade == character.Grade);
                if (currentGrade is null)
                {
                    // CharacterManagerGetGradeTemplateNotFound
                    throw new ServerCodeException("Character grade table data not found!", 20009003);
                }

                int nextGrade = gradeRows
                    .Where(x => x.Grade > character.Grade)
                    .OrderBy(x => x.Grade)
                    .FirstOrDefault()
                    ?.Grade ?? character.Grade;
                if (character.Grade == nextGrade)
                {
                    // CharacterManagerMaxGrade
                    throw new ServerCodeException("Character grade already maxed!", 20009019);
                }

                NotifyItemDataList notifyItemData = new();
                if (currentGrade.UseItemKey is not null && currentGrade.UseItemCount is not null && currentGrade.UseItemCount > 0)
                {
                    notifyItemData.ItemDataList.Add(session.inventory.Do(currentGrade.UseItemKey.Value, currentGrade.UseItemCount.Value * -1));
                    session.SendPush(notifyItemData);
                }

                character.Grade = nextGrade;
            }
            catch (ServerCodeException ex)
            {
                session.SendResponse(new CharacterPromoteGradeResponse() { Code = ex.Code }, packet.Id);
                return;
            }

            session.SendPush(new NotifyCharacterDataList()
            {
                CharacterDataList = { character }
            });

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterPromoteGradeResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterActivateStarRequest")]
        public static void CharacterActivateStarRequestHandler(Session session, Packet.Request packet)
        {
            CharacterActivateStarRequest req = packet.Deserialize<CharacterActivateStarRequest>();
            var character = session.character.Characters.Find(c => c.Id == req.TemplateId);
            var characterData = TableReaderV2.Parse<CharacterTable>().Find(x => x.Id == req.TemplateId);
            var characterQualityFragment = TableReaderV2.Parse<CharacterQualityFragmentTable>().Find(x => x.Type == characterData?.Type && x.Quality == character?.Quality);

            try
            {
                if (character is null)
                {
                    // CharacterManagerGetCharacterByIdNotFound
                    throw new ServerCodeException("Character data not found!", 20009011);
                }
                if (characterData is null)
                {
                    // CharacterManagerGetCharacterDataNotFound
                    throw new ServerCodeException("Character table data not found!", 20009021);
                }
                if (characterQualityFragment is null)
                {
                    // CharacterManagerGetQualityFragmentTemplateNotFound
                    throw new ServerCodeException("Character quality fragment table data not found!", 20009004);
                }

                if (character.Star < characterQualityFragment.StarUseCount.Count)
                {
                    if (characterQualityFragment.StarUseCount[character.Star] > 0)
                    {
                        NotifyItemDataList notifyItemData = new();
                        notifyItemData.ItemDataList.Add(session.inventory.Do(characterData.ItemId, characterQualityFragment.StarUseCount[character.Star] * -1));
                        session.SendPush(notifyItemData);
                    }
                    character.Star++;
                }
                else
                {
                    // CharacterManagerActivateStarMaxStar
                    throw new ServerCodeException("Character star already maxed!", 20009015);
                }
            }
            catch (ServerCodeException ex)
            {
                session.SendResponse(new CharacterActivateStarResponse() { Code = ex.Code }, packet.Id);
                return;
            }

            session.SendPush(new NotifyCharacterDataList()
            {
                CharacterDataList = { character }
            });

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterActivateStarResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterPromoteQualityRequest")]
        public static void CharacterPromoteQualityRequestHandler(Session session, Packet.Request packet)
        {
            CharacterPromoteQualityRequest req = packet.Deserialize<CharacterPromoteQualityRequest>();
            var character = session.character.Characters.Find(c => c.Id == req.TemplateId);
            var characterData = TableReaderV2.Parse<CharacterTable>().Find(x => x.Id == req.TemplateId);
            var characterQualityFragment = TableReaderV2.Parse<CharacterQualityFragmentTable>().Find(x => x.Type == characterData?.Type && x.Quality == character?.Quality);

            try
            {
                if (character is null)
                {
                    // CharacterManagerGetCharacterByIdNotFound
                    throw new ServerCodeException("Character data not found!", 20009011);
                }
                if (characterData is null)
                {
                    // CharacterManagerGetCharacterDataNotFound
                    throw new ServerCodeException("Character table data not found!", 20009021);
                }
                if (characterQualityFragment is null)
                {
                    // CharacterManagerGetQualityFragmentTemplateNotFound
                    throw new ServerCodeException("Character quality fragment table data not found!", 20009004);
                }

                if (TableReaderV2.Parse<CharacterQualityFragmentTable>().Any(x => x.Type == characterData?.Type && x.Quality == character?.Quality + 1))
                {
                    if (characterQualityFragment.PromoteUseCoin is not null && characterQualityFragment.PromoteUseCoin > 0)
                    {
                        NotifyItemDataList notifyItemData = new();
                        notifyItemData.ItemDataList.Add(session.inventory.Do(characterQualityFragment.PromoteItemId ?? 1, (characterQualityFragment.PromoteUseCoin ?? 0) * -1));
                        session.SendPush(notifyItemData);
                    }

                    character.Star = 0;
                    character.Quality++;
                    session.character.UnlockQualityGatedSkills(character, session.player.GatherRewards);
                }
                else
                {
                    // CharacterManagerMaxQuality
                    throw new ServerCodeException("Character quality already maxed!", 20009016);
                }
            }
            catch (ServerCodeException ex)
            {
                session.SendResponse(new CharacterPromoteQualityResponse() { Code = ex.Code }, packet.Id);
                return;
            }

            session.SendPush(new NotifyCharacterDataList()
            {
                CharacterDataList = { character }
            });

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterPromoteQualityResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterUnlockSkillGroupRequest")]
        public static void CharacterUnlockSkillGroupRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUnlockSkillGroupRequest request = packet.Deserialize<CharacterUnlockSkillGroupRequest>();

            uint[] skillIds = Character.ResolveCharacterSkillIdsForGroupId(request.SkillGroupId)
                .Where(skillId => skillId > 0)
                .Distinct()
                .ToArray();
            int ownerCharacterId = TableReaderV2.Parse<CharacterSkillTable>()
                .Where(skill => skill.SkillGroupId.Contains(request.SkillGroupId))
                .Select(skill => skill.CharacterId)
                .FirstOrDefault();
            uint defaultSkillId = skillIds.FirstOrDefault();
            CharacterData? character = ownerCharacterId > 0
                ? session.character.Characters.Find(candidate => candidate.Id == (uint)ownerCharacterId)
                : null;
            CharacterSkillUpgradeTable? initialUpgrade = defaultSkillId > 0
                ? TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                    .FirstOrDefault(upgrade => upgrade.SkillId == (int)defaultSkillId && upgrade.Level == 0)
                : null;
            if (character is null || defaultSkillId <= 0
                || initialUpgrade is not null
                    && !Character.MeetsCharacterSkillCondition(character, initialUpgrade.ConditionId,
                        session.player.GatherRewards, session.player.PlayerData.Level))
            {
                session.SendResponse(new CharacterUnlockSkillGroupResponse { Code = 20009021 }, packet.Id);
                return;
            }
            if (character.SkillList.Any(skill => skill.Id == defaultSkillId))
            {
                session.SendResponse(new CharacterUnlockSkillGroupResponse { Code = 20009047 }, packet.Id);
                return;
            }

            character.SkillList.Add(new CharacterSkill { Id = defaultSkillId, Level = 1 });
            NotifyCharacterDataList notifyCharacterData = new();
            notifyCharacterData.CharacterDataList.Add(character);
            session.SendPush(notifyCharacterData);
            SaveCharacterProgress(session);
            session.SendResponse(new CharacterUnlockSkillGroupResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSwitchSkillRequest")]
        public static void CharacterSwitchSkillRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSwitchSkillRequest request = packet.Deserialize<CharacterSwitchSkillRequest>();

            if (!session.character.TrySwitchCharacterSkill(request.SkillId, out bool changed))
            {
                // CharacterSkillIsNotFoundOrLock
                session.SendResponse(new CharacterSwitchSkillResponse() { Code = 20009048 }, packet.Id);
                return;
            }

            if (changed)
            {
                session.character.Save();
                session.AppliedTeamPrefabId = null;
            }

            session.SendResponse(new CharacterSwitchSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterUpgradeSkillGroupRequest")]
        public static void CharacterUpgradeSkillGroupRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUpgradeSkillGroupRequest request = packet.Deserialize<CharacterUpgradeSkillGroupRequest>();

            UpgradeCharacterSkillResult upgradeResult;
            try
            {
                upgradeResult = session.character.UpgradeCharacterSkillGroup(request.SkillGroupId, request.Count, session.player.GatherRewards);
            }
            catch (ServerCodeException ex)
            {
                session.SendResponse(new CharacterUpgradeSkillGroupResponse { Code = ex.Code }, packet.Id);
                return;
            }

            if (!HasEnoughItems(session, Inventory.Coin, upgradeResult.CoinCost)
                || !HasEnoughItems(session, Inventory.SkillPoint, upgradeResult.SkillPointCost))
            {
                // ItemCountNotEnough
                session.SendResponse(new CharacterUpgradeSkillGroupResponse { Code = 20012004 }, packet.Id);
                return;
            }

            // Apply the fully-preflighted level deltas atomically with inventory consumption.
            foreach (uint skillId in Character.ResolveCharacterSkillIdsForGroupId(request.SkillGroupId))
            {
                foreach (CharacterData character in session.character.Characters.Where(c => c.SkillList.Any(s => s.Id == skillId)))
                    character.SkillList.First(s => s.Id == skillId).Level += request.Count;
            }

            NotifyCharacterDataList notifyCharacterData = new();
            notifyCharacterData.CharacterDataList.AddRange(session.character.Characters.Where(x => upgradeResult.AffectedCharacters.Contains(x.Id)));

            NotifyItemDataList notifyItemData = new();
            notifyItemData.ItemDataList.AddRange(new Item[] {
                session.inventory.Do(Inventory.Coin, upgradeResult.CoinCost * -1),
                session.inventory.Do(Inventory.SkillPoint, upgradeResult.SkillPointCost * -1)
            });

            session.SendPush(notifyCharacterData);
            session.SendPush(notifyItemData);

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUpgradeSkillGroupResponse() { Level = upgradeResult.Level }, packet.Id);
        }

        [RequestPacketHandler("CharacterUnlockEnhanceSkillRequest")]
        public static void CharacterUnlockEnhanceSkillRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUnlockEnhanceSkillRequest request = packet.Deserialize<CharacterUnlockEnhanceSkillRequest>();

            EnhanceSkillGroupTable? enhanceSkillGroup = TableReaderV2.Parse<EnhanceSkillGroupTable>()
                .SingleOrDefault(x => x.Id == request.SkillGroupId);
            int ownerCharacterId = TableReaderV2.Parse<EnhanceSkillTable>()
                .Where(x => x.SkillGroupId.Contains(request.SkillGroupId))
                .Select(x => x.CharacterId)
                .FirstOrDefault();
            List<int> enhanceSkillIds = enhanceSkillGroup?.SkillId
                .Where(skillId => skillId > 0)
                .Distinct()
                .ToList() ?? [];
            int defaultSkillId = enhanceSkillIds.FirstOrDefault();
            CharacterData? character = enhanceSkillGroup is not null && ownerCharacterId > 0
                ? session.character.Characters.Find(candidate => candidate.Id == (uint)ownerCharacterId)
                : null;

            if (character is null || defaultSkillId <= 0)
            {
                // CharacterManagerGetCharacterDataNotFound. Never acknowledge an unlock that table data cannot fulfill.
                session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20009021 }, packet.Id);
                return;
            }

            if (character.EnhanceSkillList.Any(skill => enhanceSkillIds.Contains((int)skill.Id)))
            {
                // CharacterSkillUnlocked
                session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20009047 }, packet.Id);
                return;
            }

            // Unlock exactly the table-defined default active skill (one active per enhance group).
            List<EnhanceSkillUpgradeTable> defaultRows = Character.OrderedEnhanceSkillUpgrades(defaultSkillId);
            if (defaultRows.Count == 0
                || !Character.MeetsCharacterSkillCondition(character, defaultRows[0].ConditionId,
                    session.player.GatherRewards, session.player.PlayerData.Level))
            {
                // CharacterSkillConditionNotMet
                session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20009021 }, packet.Id);
                return;
            }

            Dictionary<int, int> costs = [];
            AccumulateEnhanceSkillCosts(costs, defaultRows[0]);
            if (!HasEnoughInventory(session, costs))
            {
                // ItemCountNotEnough
                session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20012004 }, packet.Id);
                return;
            }

            character.EnhanceSkillList.Add(new CharacterSkill
            {
                Id = (uint)defaultSkillId,
                Level = 1
            });

            NotifyItemDataList notifyItemData = new();
            foreach ((int itemId, int count) in costs)
                notifyItemData.ItemDataList.Add(session.inventory.Do(itemId, -count));

            session.SendPush(notifyItemData);
            session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUnlockEnhanceSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterUpgradeEnhanceSkillRequest")]
        public static void CharacterUpgradeEnhanceSkillRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUpgradeEnhanceSkillRequest request = packet.Deserialize<CharacterUpgradeEnhanceSkillRequest>();

            EnhanceSkillGroupTable? enhanceSkillGroup = TableReaderV2.Parse<EnhanceSkillGroupTable>()
                .SingleOrDefault(x => x.Id == request.SkillGroupId);
            int ownerCharacterId = TableReaderV2.Parse<EnhanceSkillTable>()
                .Where(x => x.SkillGroupId.Contains(request.SkillGroupId))
                .Select(x => x.CharacterId)
                .FirstOrDefault();
            List<int> enhanceSkillIds = enhanceSkillGroup?.SkillId
                .Where(skillId => skillId > 0)
                .Distinct()
                .ToList() ?? [];
            CharacterData? character = enhanceSkillGroup is not null && ownerCharacterId > 0
                ? session.character.Characters.Find(candidate => candidate.Id == (uint)ownerCharacterId)
                : null;

            if (character is null || request.Count <= 0)
            {
                session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20012001 }, packet.Id);
                return;
            }

            // Upgrade the single active skill of this group.
            CharacterSkill? activeSkill = character.EnhanceSkillList
                .FirstOrDefault(skill => enhanceSkillIds.Contains((int)skill.Id));
            if (activeSkill is null)
            {
                // CharacterSkillIsNotFoundOrLock
                session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20009048 }, packet.Id);
                return;
            }

            List<EnhanceSkillUpgradeTable> rows = Character.OrderedEnhanceSkillUpgrades((int)activeSkill.Id);
            int targetLevel = activeSkill.Level + request.Count;
            Dictionary<int, int> costs = [];
            for (int level = activeSkill.Level; level < targetLevel; level++)
            {
                if (level >= rows.Count)
                {
                    // CharacterSkillMaxLevel
                    session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20009014 }, packet.Id);
                    return;
                }
                EnhanceSkillUpgradeTable upgrade = rows[level];
                if (!HasEnhanceCost(upgrade))
                {
                    // CharacterSkillMaxLevel
                    session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20009014 }, packet.Id);
                    return;
                }
                if (!Character.MeetsCharacterSkillCondition(character, upgrade.ConditionId,
                    session.player.GatherRewards, session.player.PlayerData.Level))
                {
                    // CharacterSkillConditionNotMet
                    session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20009021 }, packet.Id);
                    return;
                }
                AccumulateEnhanceSkillCosts(costs, upgrade);
            }

            if (!HasEnoughInventory(session, costs))
            {
                // ItemCountNotEnough
                session.SendResponse(new CharacterUpgradeEnhanceSkillResponse() { Code = 20012004 }, packet.Id);
                return;
            }

            activeSkill.Level = targetLevel;

            NotifyItemDataList notifyItemData = new();
            foreach ((int itemId, int count) in costs)
                notifyItemData.ItemDataList.Add(session.inventory.Do(itemId, -count));

            session.SendPush(notifyItemData);
            session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUpgradeEnhanceSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSwitchEnhanceSkillRequest")]
        public static void CharacterSwitchEnhanceSkillRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSwitchEnhanceSkillRequest request = packet.Deserialize<CharacterSwitchEnhanceSkillRequest>();
            if (request.SkillId <= 0)
            {
                // CharacterSkillIsNotFoundOrLock
                session.SendResponse(new CharacterSwitchEnhanceSkillResponse() { Code = 20009048 }, packet.Id);
                return;
            }

            // The requested skill must be a configured alternate of an enhance group.
            EnhanceSkillGroupTable? enhanceSkillGroup = TableReaderV2.Parse<EnhanceSkillGroupTable>()
                .SingleOrDefault(x => x.SkillId.Contains(request.SkillId));
            int ownerCharacterId = enhanceSkillGroup is not null
                ? TableReaderV2.Parse<EnhanceSkillTable>()
                    .Where(x => x.SkillGroupId.Contains(enhanceSkillGroup.Id))
                    .Select(x => x.CharacterId)
                    .FirstOrDefault()
                : 0;
            CharacterData? character = enhanceSkillGroup is not null && ownerCharacterId > 0
                ? session.character.Characters.Find(candidate => candidate.Id == (uint)ownerCharacterId)
                : null;
            if (character is null)
            {
                // CharacterSkillIsNotFoundOrLock
                session.SendResponse(new CharacterSwitchEnhanceSkillResponse() { Code = 20009048 }, packet.Id);
                return;
            }

            // Switch only within the same owned/unlocked group, preserving the active skill level.
            List<int> groupSkillIds = enhanceSkillGroup!.SkillId.Where(id => id > 0).Distinct().ToList();
            CharacterSkill? activeSkill = character.EnhanceSkillList
                .FirstOrDefault(skill => groupSkillIds.Contains((int)skill.Id));
            if (activeSkill is null || !groupSkillIds.Contains(request.SkillId))
            {
                // CharacterSkillIsNotFoundOrLock
                session.SendResponse(new CharacterSwitchEnhanceSkillResponse() { Code = 20009048 }, packet.Id);
                return;
            }

            if (activeSkill.Id == (uint)request.SkillId)
            {
                // Idempotent: already the active skill of the group.
                session.SendResponse(new CharacterSwitchEnhanceSkillResponse(), packet.Id);
                return;
            }

            activeSkill.Id = (uint)request.SkillId;

            session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });
            session.character.Save();

            session.SendResponse(new CharacterSwitchEnhanceSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterEnhanceSkillNoticeRequest")]
        public static void CharacterEnhanceSkillNoticeRequestHandler(Session session, Packet.Request packet)
        {
            CharacterEnhanceSkillNoticeRequest request = packet.Deserialize<CharacterEnhanceSkillNoticeRequest>();
            int characterId = request.CharacterId;

            // Capture-proven schema is a single owned CharacterId; reject unknown/unowned.
            CharacterData? character = characterId > 0
                ? session.character.Characters.Find(candidate => candidate.Id == (uint)characterId)
                : null;
            if (character is null)
            {
                // CharacterManagerGetCharacterDataNotFound
                session.SendResponse(new CharacterEnhanceSkillNoticeResponse { Code = 20009021 }, packet.Id);
                return;
            }

            if (character.IsEnhanceSkillNotice)
            {
                // Idempotent: already acknowledged, emit no push.
                session.SendResponse(new CharacterEnhanceSkillNoticeResponse(), packet.Id);
                return;
            }

            character.IsEnhanceSkillNotice = true;
            session.SendPush(new NotifyCharacterDataList { CharacterDataList = { character } });
            SaveCharacterProgress(session);
            session.SendResponse(new CharacterEnhanceSkillNoticeResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterResetNewFlagRequest")]
        public static void CharacterResetNewFlagRequestHandler(Session session, Packet.Request packet)
        {
            CharacterResetNewFlagRequest request = packet.Deserialize<CharacterResetNewFlagRequest>();
            NotifyCharacterDataList notifyCharacterData = new();
            foreach (CharacterData character in session.character.Characters)
            {
                if (character.Id > (uint)int.MaxValue || character.NewFlag == 0 || !request.CharacterIds.Contains((int)character.Id))
                    continue;

                character.NewFlag = 0;
                notifyCharacterData.CharacterDataList.Add(character);
            }

            if (notifyCharacterData.CharacterDataList.Count > 0)
            {
                session.SendPush(notifyCharacterData);
                SaveCharacterProgress(session);
            }

            session.SendResponse(new CharacterResetNewFlagResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterSetHeadInfoRequest")]
        public static void CharacterSetHeadInfoRequestHandler(Session session, Packet.Request packet)
        {
            CharacterSetHeadInfoRequest request = packet.Deserialize<CharacterSetHeadInfoRequest>();
            CharacterData? character = session.character.Characters.Find(candidate => candidate.Id == request.TemplateId);
            CharacterData.CharacterHead? requestedHead = request.CharacterHeadInfo;
            if (character is null || requestedHead is null)
            {
                session.SendResponse(new CharacterSetHeadInfoResponse { Code = 20009001 }, packet.Id);
                return;
            }

            CharacterTable? characterRow = TableReaderV2.Parse<CharacterTable>()
                .Find(candidate => candidate.Id == (int)character.Id);
            if (characterRow is null || !IsValidCharacterHeadSelection(session, character, characterRow, requestedHead))
            {
                // CharacterHeadInvalid
                session.SendResponse(new CharacterSetHeadInfoResponse { Code = 20012001 }, packet.Id);
                return;
            }

            bool changed = character.CharacterHeadInfo is null
                || character.CharacterHeadInfo.HeadFashionId != requestedHead.HeadFashionId
                || character.CharacterHeadInfo.HeadFashionType != requestedHead.HeadFashionType;
            character.CharacterHeadInfo = new CharacterData.CharacterHead
            {
                HeadFashionId = requestedHead.HeadFashionId,
                HeadFashionType = requestedHead.HeadFashionType
            };
            if (changed)
                session.character.Save();

            session.SendPush(new NotifyCharacterDataList
            {
                CharacterDataList = [character]
            });
            session.SendResponse(new CharacterSetHeadInfoResponse(), packet.Id);
        }

        /// <summary>
        /// HeadFashionType: 0 Default (character default fashion), 1 Liberation (default fashion + Higher
        /// liberation), 2 Fashion (an unlocked owned fashion of the same character). Mirrors
        /// XFashionManager.IsFashionHeadUnLock. The alternate-color target is presentation-only and is
        /// not an owned FashionList row, so it can never satisfy the Fashion head type.
        /// </summary>
        private static bool IsValidCharacterHeadSelection(Session session, CharacterData character, CharacterTable characterRow, CharacterData.CharacterHead head)
        {
            if (head.HeadFashionType is < 0 or > 2 || head.HeadFashionId <= 0)
                return false;

            if (head.HeadFashionType == 0)
                return head.HeadFashionId == characterRow.DefaultNpcFashtionId;
            if (head.HeadFashionType == 1)
                return head.HeadFashionId == characterRow.DefaultNpcFashtionId
                    && character.LiberateLv >= 4;

            FashionTable? fashionRow = TableReaderV2.Parse<FashionTable>()
                .Find(candidate => candidate.Id == head.HeadFashionId);
            return fashionRow is not null
                && fashionRow.CharacterId == character.Id
                && session.character.Fashions.Any(candidate =>
                    candidate.Id == head.HeadFashionId && !candidate.IsLock);
        }

        [RequestPacketHandler("CharacterExchangeRequest")]
        public static void CharacterExchangeRequestHandler(Session session, Packet.Request packet)
        {
            CharacterExchangeRequest request = packet.Deserialize<CharacterExchangeRequest>();
            CharacterTable? characterData = TableReaderV2.Parse<CharacterTable>().FirstOrDefault(x => x.Id == request.TemplateId);

            if (characterData is null)
            {
                CharacterExchangeResponse rsp = new()
                {
                    // CharacterManagerGetCharacterTemplateNotFound
                    Code = 20009001
                };
                session.SendResponse(rsp, packet.Id);
                return;
            }

            var composeCount = Character.GetMinCharacterFragment(characterData.Id)?.ComposeCount ?? 50;

            if (!session.inventory.Items.Any(x => x.Id == characterData.ItemId && x.Count >= composeCount))
            {
                CharacterExchangeResponse rsp = new()
                {
                    // ItemCountNotEnough
                    Code = 20012004
                };
                session.SendResponse(rsp, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = new();
            notifyItemData.ItemDataList.Add(session.inventory.Do(characterData.ItemId, composeCount * -1));
            session.SendPush(notifyItemData);

            try
            {
                RewardHandler.GiveRewards([ new Reward() { Id = request.TemplateId, Type = RewardType.Character } ], session);
            }
            catch (ServerCodeException ex)
            {
                CharacterExchangeResponse rsp = new() { Code = ex.Code };
                session.SendResponse(rsp, packet.Id);
                return;
            }

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterExchangeResponse(), packet.Id);
        }
    }
}
