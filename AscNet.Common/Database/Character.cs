using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using Newtonsoft.Json;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.fashion;

namespace AscNet.Common.Database
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public class Character
    {
        public static readonly List<CharacterLevelUpTemplate> characterLevelUpTemplates;
        public static readonly List<EquipLevelUpTemplate> equipLevelUpTemplates;
        public static readonly IMongoCollection<Character> collection = Common.db.GetCollection<Character>("characters");

        static Character()
        {
            if (File.Exists("Data/CharacterLevelUpTemplate.json"))
                characterLevelUpTemplates = JsonConvert.DeserializeObject<List<CharacterLevelUpTemplate>>(File.ReadAllText("Data/CharacterLevelUpTemplate.json")) ?? new();
            else
                characterLevelUpTemplates = new();
            if (File.Exists("Data/EquipLevelUpTemplate.json"))
                equipLevelUpTemplates = JsonConvert.DeserializeObject<List<EquipLevelUpTemplate>>(File.ReadAllText("Data/EquipLevelUpTemplate.json")) ?? new();
            else
                equipLevelUpTemplates = new();
        }

        private uint NextEquipId => Equips.MaxBy(x => x.Id)?.Id + 1 ?? 1;

        public static Character FromUid(long uid)
        {
            Character character = collection.AsQueryable().FirstOrDefault(x => x.Uid == uid) ?? Create(uid);
            bool changed = false;
            if (character.NormalizeEquipsForCurrentTables())
                changed = true;
            if (character.NormalizeCharactersForCurrentTables())
                changed = true;
            if (changed)
                character.Save();

            return character;
        }


        public static bool IsOwnableEquipTemplate(EquipTable equip)
        {
            return equip.Priority != 100;
        }

        public bool NormalizeEquipsForCurrentTables()
        {
            if (Equips is null)
            {
                Equips = new();
                return true;
            }

            Dictionary<uint, EquipTable> ownableEquipTemplates = TableReaderV2.Parse<EquipTable>()
                .Where(IsOwnableEquipTemplate)
                .ToDictionary(equip => (uint)equip.Id);
            List<EquipData> normalizedEquips = new();
            HashSet<uint> usedIds = new();
            uint nextId = 1;
            bool changed = false;

            foreach (EquipData equip in Equips)
            {
                if (equip.TemplateId == 0 || equip.IsRecycle || !ownableEquipTemplates.ContainsKey(equip.TemplateId))
                {
                    changed = true;
                    continue;
                }

                if (equip.Id == 0 || !usedIds.Add(equip.Id))
                {
                    while (usedIds.Contains(nextId))
                        nextId++;

                    equip.Id = nextId;
                    usedIds.Add(equip.Id);
                    changed = true;
                }

                nextId = Math.Max(nextId, equip.Id + 1);

                if (equip.Level <= 0)
                {
                    equip.Level = 1;
                    changed = true;
                }

                if (equip.ResonanceInfo is null)
                {
                    equip.ResonanceInfo = new();
                    changed = true;
                }

                if (equip.UnconfirmedResonanceInfo is null)
                {
                    equip.UnconfirmedResonanceInfo = new();
                    changed = true;
                }

                if (equip.AwakeSlotList is null)
                {
                    equip.AwakeSlotList = new();
                    changed = true;
                }

                if (equip.WeaponOverrunData is null)
                {
                    equip.WeaponOverrunData = new();
                    changed = true;
                }

                normalizedEquips.Add(equip);
            }

            if (normalizedEquips.Count != Equips.Count)
                changed = true;

            Equips = normalizedEquips;
            return changed;
        }

        public bool NormalizeCharactersForCurrentTables()
        {
            bool changed = false;
            if (Characters is null)
            {
                Characters = new();
                changed = true;
            }

            if (Equips is null)
            {
                Equips = new();
                changed = true;
            }

            if (Fashions is null)
            {
                Fashions = new();
                changed = true;
            }

            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>()
                .ToDictionary(character => character.Id);
            Dictionary<int, CharacterSkillTable> skillRowsByCharacterId = TableReaderV2.Parse<CharacterSkillTable>()
                .ToDictionary(skill => skill.CharacterId);
            ILookup<int, CharacterQualityTable> qualityRowsByCharacterId = TableReaderV2.Parse<CharacterQualityTable>()
                .ToLookup(quality => quality.CharacterId);
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>()
                .ToDictionary(equip => equip.Id);
            Dictionary<int, FashionTable> fashionRowsById = TableReaderV2.Parse<FashionTable>()
                .ToDictionary(fashion => fashion.Id);

            HashSet<int> seenFashionIds = new();
            List<FashionList> normalizedFashions = new();
            foreach (FashionList fashion in Fashions)
            {
                if (fashion.Id <= 0 || !fashionRowsById.ContainsKey((int)fashion.Id) || !seenFashionIds.Add((int)fashion.Id))
                {
                    changed = true;
                    continue;
                }

                normalizedFashions.Add(fashion);
            }
            Fashions = normalizedFashions;

            foreach (CharacterData character in Characters)
            {
                if (!characterRowsById.TryGetValue((int)character.Id, out CharacterTable? characterRow))
                    continue;

                CharacterQualityTable? firstQualityRow = qualityRowsByCharacterId[(int)character.Id]
                    .OrderBy(quality => quality.Quality)
                    .FirstOrDefault();
                if (firstQualityRow is not null)
                {
                    if (character.InitQuality <= 0)
                    {
                        character.InitQuality = firstQualityRow.Quality;
                        changed = true;
                    }

                    if (character.Quality <= 0)
                    {
                        character.Quality = firstQualityRow.Quality;
                        changed = true;
                    }
                }

                if (character.Level <= 0)
                {
                    character.Level = 1;
                    changed = true;
                }

                if (character.Grade <= 0)
                {
                    character.Grade = 1;
                    changed = true;
                }

                if (character.TrustLv <= 0)
                {
                    character.TrustLv = 1;
                    changed = true;
                }

                if (character.LiberateLv <= 0)
                {
                    character.LiberateLv = 1;
                    changed = true;
                }

                if (character.CreateTime <= 0)
                {
                    character.CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                    changed = true;
                }

                if (character.EnhanceSkillList is null)
                {
                    character.EnhanceSkillList = new();
                    changed = true;
                }

                if (skillRowsByCharacterId.TryGetValue((int)character.Id, out CharacterSkillTable? skillRow))
                {
                    Dictionary<uint, CharacterSkill> skillsById = character.SkillList?
                        .Where(skill => skill.Id > 0)
                        .GroupBy(skill => skill.Id)
                        .ToDictionary(group => group.Key, group => group.First()) ?? new();

                    foreach (CharacterSkill expectedSkill in BuildInitialCharacterSkills(skillRow))
                    {
                        if (skillsById.ContainsKey(expectedSkill.Id))
                            continue;

                        skillsById.Add(expectedSkill.Id, expectedSkill);
                        changed = true;
                    }

                    CharacterSkill[] normalizedSkills = skillsById.Values
                        .OrderBy(skill => skill.Id)
                        .ToArray();
                    if (character.SkillList is null || character.SkillList.Count != normalizedSkills.Length || !character.SkillList.Select(skill => skill.Id).OrderBy(id => id).SequenceEqual(normalizedSkills.Select(skill => skill.Id)))
                    {
                        character.SkillList = normalizedSkills.ToList();
                        changed = true;
                    }
                }

                if (characterRow.DefaultNpcFashtionId > 0 && fashionRowsById.ContainsKey(characterRow.DefaultNpcFashtionId))
                {
                    bool hasCompatibleFashion = character.FashionId > 0
                        && fashionRowsById.TryGetValue((int)character.FashionId, out FashionTable? currentFashion)
                        && currentFashion.CharacterId == characterRow.Id;
                    if (!hasCompatibleFashion)
                    {
                        character.FashionId = (uint)characterRow.DefaultNpcFashtionId;
                        changed = true;
                    }

                    if (character.CharacterHeadInfo is null)
                    {
                        character.CharacterHeadInfo = new CharacterData.CharacterHead();
                        changed = true;
                    }

                    bool hasCompatibleHeadFashion = character.CharacterHeadInfo.HeadFashionId > 0
                        && fashionRowsById.TryGetValue((int)character.CharacterHeadInfo.HeadFashionId, out FashionTable? currentHeadFashion)
                        && currentHeadFashion.CharacterId == characterRow.Id;
                    if (!hasCompatibleHeadFashion)
                    {
                        character.CharacterHeadInfo.HeadFashionId = (uint)characterRow.DefaultNpcFashtionId;
                        changed = true;
                    }

                    if (Fashions.All(fashion => fashion.Id != characterRow.DefaultNpcFashtionId))
                    {
                        Fashions.Add(new FashionList
                        {
                            Id = characterRow.DefaultNpcFashtionId,
                            IsLock = false
                        });
                        changed = true;
                    }
                }

                if (characterRow.EquipId > 0
                    && equipRowsById.TryGetValue(characterRow.EquipId, out EquipTable? defaultEquipRow)
                    && IsOwnableEquipTemplate(defaultEquipRow))
                {
                    List<EquipData> assignedEquips = Equips
                        .Where(equip => equip.CharacterId == characterRow.Id)
                        .ToList();
                    bool hasCompatibleAssignedEquip = assignedEquips.Any(equip =>
                        equipRowsById.TryGetValue((int)equip.TemplateId, out EquipTable? assignedEquipRow)
                        && assignedEquipRow.Type == characterRow.EquipType);

                    if (!hasCompatibleAssignedEquip)
                    {
                        foreach (EquipData assignedEquip in assignedEquips)
                        {
                            if (!equipRowsById.TryGetValue((int)assignedEquip.TemplateId, out EquipTable? assignedEquipRow)
                                || assignedEquipRow.Type != characterRow.EquipType)
                            {
                                assignedEquip.CharacterId = 0;
                                changed = true;
                            }
                        }

                        EquipData? existingDefaultEquip = Equips.FirstOrDefault(equip => equip.TemplateId == (uint)characterRow.EquipId && equip.CharacterId == 0);
                        if (existingDefaultEquip is not null)
                        {
                            existingDefaultEquip.CharacterId = characterRow.Id;
                            changed = true;
                        }
                        else
                        {
                            EquipData? equip = AddEquip((uint)characterRow.EquipId, characterRow.Id);
                            changed |= equip is not null;
                        }
                    }
                }
            }

            return changed;
        }

        private static List<CharacterSkill> BuildInitialCharacterSkills(CharacterSkillTable characterSkill)
        {
            return characterSkill.SkillGroupId
                .Where(skillGroupId => skillGroupId > 0)
                .Select(CharacterSkillIdFromGroupId)
                .Distinct()
                .Select(skillId => new CharacterSkill
                {
                    Id = skillId,
                    Level = 1
                })
                .ToList();
        }

        private static uint CharacterSkillIdFromGroupId(int skillGroupId)
        {
            string skillGroupIdText = skillGroupId.ToString();
            return uint.Parse(skillGroupIdText[..Math.Min(6, skillGroupIdText.Length)]);
        }

        private static Character Create(long uid)
        {
            Character character = new()
            {
                Uid = uid,
                Characters = new(),
                Equips = new(),
                Fashions = new()
            };
            // Lucia havers by default
            character.AddCharacter(1021001);

            collection.InsertOne(character);

            return character;
        }

        public static CharacterQualityFragmentTable? GetMinCharacterFragment(int id)
        {
            var characterMinQuality = TableReaderV2
                .Parse<CharacterQualityTable>()
                .Where(x => x.CharacterId == id)
                .Min(x => x.Quality);

            return TableReaderV2
                .Parse<CharacterQualityFragmentTable>()
                .FirstOrDefault(x => x.Quality == characterMinQuality);
        }

        /// <summary>
        /// Don't forget to send Equip, Fashion, and the Character notify after using this!
        /// </summary>
        /// <param name="id"></param>
        /// <exception cref="ServerCodeException"></exception>
        public AddCharacterRet AddCharacter(uint id, int level = 1)
        {
            AddCharacterRet ret = new();
            CharacterTable? character = TableReaderV2.Parse<CharacterTable>().Find(x => x.Id == id);
            CharacterSkillTable? characterSkill = TableReaderV2.Parse<CharacterSkillTable>().Find(x => x.CharacterId == id);
            CharacterQualityTable? characterQuality = TableReaderV2.Parse<CharacterQualityTable>().OrderBy(x => x.Quality).FirstOrDefault(x => x.CharacterId == id);
            
            if (character is null || characterSkill is null || characterQuality is null)
            {
                // CharacterManagerGetCharacterDataNotFound
                throw new ServerCodeException("Invalid character id!", 20009021);
            }
            if (Characters.FirstOrDefault(x => x.Id == character.Id) is not null)
            {
                // CharacterManagerCreateCharacterAlreadyExist
                throw new ServerCodeException("Character already obtained!", 20009022);
            }
            
            CharacterData characterData = new()
            {
                Id = (uint)character.Id,
                Level = level,
                Exp = 0,
                Quality = characterQuality.Quality,
                InitQuality = characterQuality.Quality,
                Star = 0,
                Grade = 1,
                FashionId = (uint)character.DefaultNpcFashtionId,
                CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                TrustLv = 1,
                TrustExp = 0,
                Ability = 0,
                LiberateLv = 1,
                CharacterHeadInfo = new()
                {
                    HeadFashionId = (uint)character.DefaultNpcFashtionId,
                    HeadFashionType = 0
                }
            };

            characterData.SkillList.AddRange(BuildInitialCharacterSkills(characterSkill));

            if (character.DefaultNpcFashtionId > 0)
            {
                FashionList fashion = new()
                {
                    Id = character.DefaultNpcFashtionId,
                    IsLock = false
                };
                Fashions.Add(fashion);
                ret.Fashion = fashion;
            }
            if (character.EquipId > 0)
                ret.Equip = AddEquip((uint)character.EquipId, character.Id);

            Characters.Add(characterData);
            ret.Character = characterData;
            return ret;
        }

        public CharacterData? AddCharacterExp(int characterId, int exp, int maxLvl = 0)
        {
            var characterData = TableReaderV2.Parse<CharacterTable>().FirstOrDefault(x => x.Id == characterId);
            var character = Characters.FirstOrDefault(x => x.Id == characterId);

            if (character is null || characterData is null)
            {
                return character;
            }

            int remainingExp = Math.Max(0, exp);
            while (true)
            {
                CharacterLevelUpTemplate? levelUpTemplate = characterLevelUpTemplates.FirstOrDefault(x => x.Level == character.Level && x.Type == characterData.Type);
                if (levelUpTemplate is null)
                {
                    break;
                }

                bool reachedLevelCap = maxLvl > 0 && character.Level >= maxLvl;
                if (reachedLevelCap)
                {
                    character.Exp = (uint)Math.Min(levelUpTemplate.Exp, (int)character.Exp + remainingExp);
                    break;
                }

                int expNeeded = Math.Max(0, levelUpTemplate.Exp - (int)character.Exp);
                if (expNeeded > remainingExp)
                {
                    character.Exp += (uint)remainingExp;
                    break;
                }

                remainingExp -= expNeeded;
                character.Level++;
                character.Exp = 0;

                if (remainingExp <= 0)
                {
                    break;
                }
            }

            return character;
        }

        public UpgradeCharacterSkillResult UpgradeCharacterSkillGroup(int skillGroupId, int count)
        {
            List<uint> affectedCharacters = new();
            int totalCoinCost = 0;
            int totalSkillPointCost = 0;
            IEnumerable<int> affectedSkills = TableReaderV2.Parse<CharacterSkillGroupTable>().Where(x => x.Id == skillGroupId).SelectMany(x => x.SkillId);

            foreach (var skillId in affectedSkills)
            {
                foreach (var character in Characters.Where(x => x.SkillList.Any(x => x.Id == skillId)))
                {
                    var characterSkill = character.SkillList.First(x => x.Id == skillId);
                    int targetLevel = characterSkill.Level + count;

                    while (characterSkill.Level < targetLevel)
                    {
                        var skillUpgrade = TableReaderV2.Parse<CharacterSkillUpgradeTable>().Find(x => x.SkillId == skillId && x.Level == characterSkill.Level);

                        totalCoinCost += skillUpgrade?.UseCoin ?? 0;
                        totalSkillPointCost += skillUpgrade?.UseSkillPoint ?? 0;

                        characterSkill.Level++;
                    }
                    affectedCharacters.Add(character.Id);
                }
            }

            return new UpgradeCharacterSkillResult()
            {
                AffectedCharacters = affectedCharacters,
                CoinCost = totalCoinCost,
                SkillPointCost = totalSkillPointCost
            };
        }

        public EquipData? AddEquip(uint equipId, int characterId = 0, int level = 1)
        {
            EquipTable? equip = TableReaderV2.Parse<EquipTable>().Find(x => x.Id == equipId && IsOwnableEquipTemplate(x));
            if (equip is null)
                return null;

            EquipData equipData = new()
            {
                Id = NextEquipId,
                TemplateId = equipId,
                CharacterId = characterId,
                Level = level,
                Exp = 0,
                Breakthrough = 0,
                ResonanceInfo = new(),
                UnconfirmedResonanceInfo = new(),
                AwakeSlotList = new(),
                IsLock = false,
                CreateTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                IsRecycle = false
            };
            
            Equips.Add(equipData);
            return equipData;
        }

        public EquipData? AddEquipExp(int equipId, int exp)
        {
            var equip = Equips.FirstOrDefault(x => x.Id == equipId);
            EquipTable? equipData = TableReaderV2.Parse<EquipTable>().FirstOrDefault(x => x.Id == equip?.TemplateId);
            EquipBreakThroughTable? equipBreakThroughTable = TableReaderV2.Parse<EquipBreakThroughTable>().FirstOrDefault(x => x.EquipId == equip?.TemplateId && x.Times == equip?.Breakthrough);

            if (equip is not null && equipData is not null && equipBreakThroughTable is not null)
            {
                EquipLevelUpTemplate? levelUpTemplate = equipLevelUpTemplates.FirstOrDefault(x => x.TemplateId == equipBreakThroughTable.LevelUpTemplateId && x.Level == equip.Level);

                if (levelUpTemplate is not null)
                {
                    if (exp + equip.Exp < levelUpTemplate.Exp)
                    {
                        equip.Exp += Math.Max(0, exp);
                    }
                    else if (equip.Level < equipBreakThroughTable.LevelLimit)
                    {
                        equip.Level++;
                        exp -= levelUpTemplate.Exp - equip.Exp;
                        equip.Exp = 0;
                        return AddEquipExp(equipId, exp);
                    }
                    else
                    {
                        equip.Exp = levelUpTemplate.Exp;
                    }
                }
            }

            return equip;
        }

        public int GetEquipExpRequiredToReach(int equipId, int targetLevel, int targetExp = 0)
        {
            var equip = Equips.FirstOrDefault(x => x.Id == equipId);
            EquipBreakThroughTable? equipBreakThroughTable = TableReaderV2.Parse<EquipBreakThroughTable>().FirstOrDefault(x => x.EquipId == equip?.TemplateId && x.Times == equip?.Breakthrough);
            if (equip is null || equipBreakThroughTable is null)
                return 0;

            int clampedTargetLevel = Math.Clamp(targetLevel, equip.Level, equipBreakThroughTable.LevelLimit);
            int currentExp = Math.Max(0, equip.Exp);
            int requiredExp = 0;

            for (int level = equip.Level; level < clampedTargetLevel; level++)
            {
                EquipLevelUpTemplate? levelUpTemplate = equipLevelUpTemplates.FirstOrDefault(x => x.TemplateId == equipBreakThroughTable.LevelUpTemplateId && x.Level == level);
                if (levelUpTemplate is null)
                    return requiredExp;

                requiredExp += Math.Max(0, levelUpTemplate.Exp - (level == equip.Level ? currentExp : 0));
            }

            if (clampedTargetLevel == equip.Level)
            {
                requiredExp += Math.Max(0, targetExp - currentExp);
            }
            else if (targetExp > 0)
            {
                EquipLevelUpTemplate? targetLevelTemplate = equipLevelUpTemplates.FirstOrDefault(x => x.TemplateId == equipBreakThroughTable.LevelUpTemplateId && x.Level == clampedTargetLevel);
                requiredExp += Math.Min(targetExp, targetLevelTemplate?.Exp ?? targetExp);
            }

            return Math.Max(0, requiredExp);
        }

        public int AddEquipExpUpTo(int equipId, int exp, int targetLevel, int targetExp = 0)
        {
            int appliedExp = Math.Min(Math.Max(0, exp), GetEquipExpRequiredToReach(equipId, targetLevel, targetExp));
            if (appliedExp > 0)
                AddEquipExp(equipId, appliedExp);

            return appliedExp;
        }

        public void Save()
        {
            collection.ReplaceOne(Builders<Character>.Filter.Eq(x => x.Id, Id), this);
        }

        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("uid")]
        [BsonRequired]
        public long Uid { get; set; }

        [BsonElement("characters")]
        [BsonRequired]
        public List<CharacterData> Characters { get; set; }
        
        [BsonElement("equips")]
        [BsonRequired]
        public List<EquipData> Equips { get; set; }
        
        [BsonElement("fashions")]
        [BsonRequired]
        public List<FashionList> Fashions { get; set; }
    }

    public struct UpgradeCharacterSkillResult
    {
        public int CoinCost { get; init; }
        public int SkillPointCost { get; init; }
        public List<uint> AffectedCharacters { get; init; }
    }

    public partial class CharacterLevelUpTemplate
    {
        [JsonProperty("Level")]
        public int Level { get; set; }

        [JsonProperty("Exp")]
        public int Exp { get; set; }

        [JsonProperty("AllExp")]
        public int AllExp { get; set; }

        [JsonProperty("Type")]
        public int Type { get; set; }
    }

    public partial class EquipLevelUpTemplate
    {
        [JsonProperty("Level")]
        public int Level { get; set; }

        [JsonProperty("Exp")]
        public int Exp { get; set; }

        [JsonProperty("AllExp")]
        public int AllExp { get; set; }

        [JsonProperty("TemplateId")]
        public int TemplateId { get; set; }
    }

    public struct AddCharacterRet
    {
        public CharacterData Character { get; set; }
        public EquipData? Equip { get; set; }
        public FashionList Fashion { get; set; }
    }
}
