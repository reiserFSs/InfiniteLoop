using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.skill;
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
        public int TemplateId;
        public int CharacterId;
        public int Id;
    }

    [MessagePackObject(true)]
    public class CharacterEnhanceSkillNoticeResponse
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
    public class FashionSyncNotify
    {
        public List<FashionList> FashionList = new();
    }

	[MessagePackObject(true)]
    public class CharacterSetHeadInfoRequest
    {
        public int TemplateId;
        public CharacterHeadInfo CharacterHeadInfo { get; set; }
    }

	[MessagePackObject(true)]
    public class CharacterSetHeadInfoResponse
    {
        public int Code;
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

            CharacterLevelUpTemplate? levelUpTemplate = Character.characterLevelUpTemplates.FirstOrDefault(x => x.Level == character.Level && x.Type == characterData.Type);
            if (levelUpTemplate is null)
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

            NotifyCharacterDataList notifyCharacterData = new();
            uint[] skillIds = Character.ResolveCharacterSkillIdsForGroupId(request.SkillGroupId).ToArray();
            HashSet<int> affectedChars = TableReaderV2.Parse<CharacterSkillTable>()
                .Where(skill => skill.SkillGroupId.Contains(request.SkillGroupId))
                .Select(skill => skill.CharacterId)
                .ToHashSet();
            foreach (CharacterData character in session.character.Characters.Where(character => affectedChars.Contains((int)character.Id)))
            {
                foreach (uint skillId in skillIds.Where(skillId => character.SkillList.All(skill => skill.Id != skillId)))
                {
                    character.SkillList.Add(new CharacterSkill() { Id = skillId, Level = 1 });
                }
                notifyCharacterData.CharacterDataList.Add(character);
            }
            session.SendPush(notifyCharacterData);

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUnlockSkillGroupResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterUpgradeSkillGroupRequest")]
        public static void CharacterUpgradeSkillGroupRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUpgradeSkillGroupRequest request = packet.Deserialize<CharacterUpgradeSkillGroupRequest>();

            var upgradeResult = session.character.UpgradeCharacterSkillGroup(request.SkillGroupId, request.Count);

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
            int[] affectedChars = TableReaderV2.Parse<EnhanceSkillTable>()
                .Where(x => x.SkillGroupId.Contains(request.SkillGroupId))
                .Select(x => x.CharacterId)
                .ToArray();
            int[] enhanceSkillIds = enhanceSkillGroup?.SkillId.Where(skillId => skillId > 0).ToArray() ?? [];
            Dictionary<int, EnhanceSkillUpgradeTable> unlockRows = TableReaderV2.Parse<EnhanceSkillUpgradeTable>()
                .Where(x => enhanceSkillIds.Contains(x.SkillId) && x.Level == 0)
                .ToDictionary(x => x.SkillId);

            if (enhanceSkillIds.Length == 0
                || unlockRows.Count != enhanceSkillIds.Length
                || !session.character.Characters.Any(character => affectedChars.Contains((int)character.Id)))
            {
                // CharacterManagerGetCharacterDataNotFound. Never acknowledge an unlock that table data cannot fulfill.
                session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20009021 }, packet.Id);
                return;
            }

            NotifyItemDataList notifyItemData = new();
            NotifyCharacterDataList notifyCharacterData = new();
            foreach (int enhanceSkillId in enhanceSkillIds)
            {
                EnhanceSkillUpgradeTable upgradeTable = unlockRows[enhanceSkillId];

                bool unlockedAnyCharacter = false;
                foreach (var character in session.character.Characters.Where(x => affectedChars.Contains((int)x.Id)))
                {
                    if (character.EnhanceSkillList.Any(x => x.Id == enhanceSkillId))
                    {
                        // CharacterSkillUnlocked
                        session.SendResponse(new CharacterUnlockEnhanceSkillResponse() { Code = 20009047 }, packet.Id);
                        return;
                    }
                    character.EnhanceSkillList.Add(new()
                    {
                        Id = (uint)enhanceSkillId,
                        Level = 1
                    });
                    notifyCharacterData.CharacterDataList.Add(character);
                    unlockedAnyCharacter = true;
                }

                if (!unlockedAnyCharacter)
                    continue;

                for (int i = 0; i < Math.Min(upgradeTable.CostItem.Count, upgradeTable.CostItemCount.Count); i++)
                {
                    notifyItemData.ItemDataList.Add(session.inventory.Do(upgradeTable.CostItem[i], upgradeTable.CostItemCount[i] * -1));
                }
            }
            session.SendPush(notifyItemData);
            session.SendPush(notifyCharacterData);

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUnlockEnhanceSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterUpgradeEnhanceSkillRequest")]
        public static void CharacterUpgradeEnhanceSkillRequestHandler(Session session, Packet.Request packet)
        {
            CharacterUpgradeEnhanceSkillRequest request = packet.Deserialize<CharacterUpgradeEnhanceSkillRequest>();

            var enhanceSkillIds = TableReaderV2.Parse<EnhanceSkillGroupTable>().Where(x => x.Id == request.SkillGroupId).SelectMany(x => x.SkillId);

            NotifyItemDataList notifyItemData = new();
            NotifyCharacterDataList notifyCharacterData = new();
            foreach (var enhanceSkillId in enhanceSkillIds)
            {
                foreach (var character in session.character.Characters.Where(x => x.EnhanceSkillList.Any(x => x.Id == enhanceSkillId)))
                {
                    for (int j = 0; j < request.Count; j++)
                    {
                        var skill = character.EnhanceSkillList.Find(x => x.Id == enhanceSkillId);
                        if (skill is not null)
                        {
                            EnhanceSkillUpgradeTable? upgradeTable = TableReaderV2.Parse<EnhanceSkillUpgradeTable>().Find(x => x.SkillId == enhanceSkillId && x.Level == skill.Level);
                            if (upgradeTable is null)
                                continue;

                            skill.Level++;

                            for (int i = 0; i < Math.Min(upgradeTable.CostItem.Count, upgradeTable.CostItemCount.Count); i++)
                            {
                                notifyItemData.ItemDataList.Add(session.inventory.Do(upgradeTable.CostItem[i], upgradeTable.CostItemCount[i] * -1));
                            }
                        }
                    }

                    notifyCharacterData.CharacterDataList.Add(character);
                }
            }
            session.SendPush(notifyItemData);
            session.SendPush(notifyCharacterData);

            SaveCharacterProgress(session);

            session.SendResponse(new CharacterUpgradeEnhanceSkillResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterEnhanceSkillNoticeRequest")]
        public static void CharacterEnhanceSkillNoticeRequestHandler(Session session, Packet.Request packet)
        {
            CharacterEnhanceSkillNoticeRequest request = packet.Deserialize<CharacterEnhanceSkillNoticeRequest>();
            int characterId = request.TemplateId > 0 ? request.TemplateId
                : request.CharacterId > 0 ? request.CharacterId
                : request.Id;

            IEnumerable<CharacterData> affectedCharacters = characterId > 0
                ? session.character.Characters.Where(character => character.Id == characterId)
                : session.character.Characters;

            NotifyCharacterDataList notifyCharacterData = new();
            foreach (CharacterData character in affectedCharacters)
            {
                if (!character.IsEnhanceSkillNotice)
                    continue;

                character.IsEnhanceSkillNotice = false;
                notifyCharacterData.CharacterDataList.Add(character);
            }

            if (notifyCharacterData.CharacterDataList.Count > 0)
            {
                session.SendPush(notifyCharacterData);
                SaveCharacterProgress(session);
            }

            session.SendResponse(new CharacterEnhanceSkillNoticeResponse(), packet.Id);
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

		[RequestPacketHandler("CharacterSetHeadInfoRequest")]
		public static void CharacterSetHeadInfoRequestHandler(Session session, Packet.Request packet)
		{
			CharacterSetHeadInfoRequest request = packet.Deserialize<CharacterSetHeadInfoRequest>();
            var character = session.character.Characters.Find(x => x.Id == request.TemplateId);
            if (character is not null)
            {
                character.CharacterHeadInfo.HeadFashionId = (uint)request.CharacterHeadInfo.HeadFashionId;
                character.CharacterHeadInfo.HeadFashionType = (int)request.CharacterHeadInfo.HeadFashionType;
				
            
                NotifyCharacterDataList notifyCharacterData = new();
                notifyCharacterData.CharacterDataList.Add(character);
                session.SendPush(notifyCharacterData);
            }
			
            session.SendResponse(new CharacterSetHeadInfoResponse(), packet.Id);
		}
    }
}
