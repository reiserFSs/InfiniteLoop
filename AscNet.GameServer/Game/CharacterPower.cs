using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fuben.assign;

namespace AscNet.GameServer.Game;

internal static partial class CharacterPower
{
    private static readonly Dictionary<int, EquipTable> PowerEquips = TableReaderV2.Parse<EquipTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, CharacterTable> PowerCharacters = TableReaderV2.Parse<CharacterTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, CharacterSkillTable> PowerCharacterSkills = TableReaderV2.Parse<CharacterSkillTable>().ToDictionary(row => row.CharacterId);
    private static readonly Dictionary<int, CharacterSkillGroupTable> PowerSkillGroups = TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, CharacterSkillGroupTable> PowerSkillGroupsBySkill = PowerSkillGroups.Values.SelectMany(group => group.SkillId.Where(id => id != 0).Select(id => (id, group))).ToDictionary(pair => pair.id, pair => pair.group);
    private static readonly Dictionary<(int, int), CharacterSkillLevelEffectTable> PowerSkillEffects = TableReaderV2.Parse<CharacterSkillLevelEffectTable>().ToDictionary(row => (row.SkillId, row.Level));
    private static readonly Dictionary<int, CharacterSkillTypeTable> PowerSkillTypes = TableReaderV2.Parse<CharacterSkillTypeTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, CharacterSkillTypePlusTable> PowerSkillPlusTypes = TableReaderV2.Parse<CharacterSkillTypePlusTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, AssignChapterTable> PowerAssignChapters = TableReaderV2.Parse<AssignChapterTable>().ToDictionary(row => row.ChapterId);
    private static readonly Dictionary<int, WeaponSkillTable> PowerWeaponSkills = TableReaderV2.Parse<WeaponSkillTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, EquipSuitTable> PowerSuits = TableReaderV2.Parse<EquipSuitTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, EquipSuitEffectTable> PowerSuitEffects = TableReaderV2.Parse<EquipSuitEffectTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, EnhanceSkillTable> PowerEnhanceSkills = TableReaderV2.Parse<EnhanceSkillTable>().ToDictionary(row => row.CharacterId);
    private static readonly Dictionary<int, EnhanceSkillGroupTable> PowerEnhanceGroups = TableReaderV2.Parse<EnhanceSkillGroupTable>().ToDictionary(row => row.Id);
    private static readonly Dictionary<int, EnhanceSkillLevelEffectTable> PowerEnhanceEffects = TableReaderV2.Parse<EnhanceSkillLevelEffectTable>().ToDictionary(row => row.SkillLevelId);
    private static readonly ILookup<int, WeaponOverrunTable> PowerOverruns = TableReaderV2.Parse<WeaponOverrunTable>().ToLookup(row => row.WeaponId);

    // XCharacterAgency.GetCharacterAbility: calculate from current state, never the uploaded/cached Ability.
    internal static long Calculate(Session session, CharacterData character)
    {
        List<EquipData> equips = session.character.Equips.Where(equip => equip.CharacterId == character.Id).ToList();
        Dictionary<int, int> levels = new();
        foreach (CharacterSkill skill in character.SkillList)
            levels[(int)skill.Id] = skill.Level;

        Dictionary<int, int> resonanceLevels = new();
        foreach (EquipData equip in equips)
        {
            foreach (ResonanceInfo resonance in equip.ResonanceInfo)
            {
                if (resonance.Type != EquipResonanceType.CharacterSkill || resonance.CharacterId != character.Id
                    || !PowerSkillGroupsBySkill.TryGetValue(resonance.TemplateId, out CharacterSkillGroupTable? group))
                    continue;
                foreach (int skillId in group.SkillId)
                    if (skillId != 0)
                        resonanceLevels[skillId] = resonanceLevels.GetValueOrDefault(skillId) + 1;
            }
        }
        AddActiveLevels(levels, resonanceLevels);
        foreach (EquipData equip in equips)
        {
            foreach (WeaponOverrunTable overrun in GetPowerOverruns(equip))
            {
                if (overrun.UpSkillGroupId <= 0 || overrun.UpSkillGroupLevel <= 0)
                    continue;
                foreach (int skillId in PowerSkillGroups[overrun.UpSkillGroupId].SkillId)
                    if (levels.ContainsKey(skillId))
                        levels[skillId] += overrun.UpSkillGroupLevel;
            }
        }

        Dictionary<int, int> plusLevels = GetAssignmentLevels(session, character);
        AddActiveLevels(levels, plusLevels);
        long skillAbility = 0;
        long resonanceAbility = 0;
        long plusAbility = 0;
        foreach ((int skillId, int level) in levels)
        {
            if (PowerSkillEffects.TryGetValue((skillId, level), out CharacterSkillLevelEffectTable? effect))
                skillAbility += effect.Ability ?? 0;
            if (resonanceLevels.TryGetValue(skillId, out int resonanceLevel)
                && PowerSkillEffects.TryGetValue((skillId, resonanceLevel), out effect))
                resonanceAbility += effect.ResonanceAbility ?? 0;
            if (plusLevels.TryGetValue(skillId, out int plusLevel)
                && PowerSkillEffects.TryGetValue((skillId, plusLevel), out effect))
                plusAbility += effect.PlusAbility ?? 0;
        }

        EquipData? weapon = equips.FirstOrDefault(equip => PowerEquips[(int)equip.TemplateId].Site == 0);
        return CalculateAttributePower(character, equips) + skillAbility + resonanceAbility + plusAbility
            + GetEquipmentSkillPower(character, equips) + GetEnhancementPower(character)
            + GetPartnerPower() + (weapon is null ? 0 : GetOverrunPower(character, weapon));
    }

    private static void AddActiveLevels(Dictionary<int, int> levels, Dictionary<int, int> additions)
    {
        foreach ((int id, int level) in additions)
            if (levels.ContainsKey(id))
                levels[id] += level;
    }

    private static Dictionary<int, int> GetAssignmentLevels(Session session, CharacterData character)
    {
        Dictionary<int, int> result = new();
        if (!PowerCharacterSkills.TryGetValue((int)character.Id, out CharacterSkillTable? skills))
            return result;
        int npcType = GetPowerNpcType(character);
        foreach (var chapter in session.player.Assign.Chapters)
        {
            if (chapter.CharacterId <= 0 || !PowerAssignChapters.TryGetValue(chapter.ChapterId, out AssignChapterTable? chapterRow)
                || !PowerSkillPlusTypes.TryGetValue(chapterRow.SkillPlusId, out CharacterSkillTypePlusTable? plus)
                || !plus.CharacterType.Contains(npcType))
                continue;
            foreach (int groupId in skills.SkillGroupId)
            {
                if (groupId == 0)
                    continue;
                foreach (int id in PowerSkillGroups[groupId].SkillId)
                {
                    if (id == 0 || !PowerSkillTypes.TryGetValue(id, out CharacterSkillTypeTable? type)
                        || type.Type == 0 || !plus.SkillType.Contains(type.Type)
                        || !character.SkillList.Any(skill => skill.Id == id && skill.Level > 0))
                        continue;
                    result[id] = result.GetValueOrDefault(id) + 1;
                }
            }
        }
        return result;
    }

    private static long GetEquipmentSkillPower(CharacterData character, IReadOnlyList<EquipData> equips)
    {
        long ability = 0;
        Dictionary<int, int> suits = new();
        foreach (EquipData equip in equips)
        {
            if (!PowerEquips.TryGetValue((int)equip.TemplateId, out EquipTable? template))
                return 0;
            if (template.Site == 0)
            {
                if (template.WeaponSkillId > 0)
                    ability += PowerWeaponSkills[template.WeaponSkillId].Ability;
                foreach (ResonanceInfo resonance in equip.ResonanceInfo)
                    if (resonance.Type == EquipResonanceType.WeaponSkill
                        && (resonance.CharacterId == 0 || resonance.CharacterId == character.Id))
                        ability += PowerWeaponSkills[resonance.TemplateId].Ability;
            }
            if (template.SuitId > 0)
                suits[template.SuitId] = suits.GetValueOrDefault(template.SuitId) + 1;
        }
        foreach ((int suitId, int count) in suits)
        {
            if (!PowerSuits.TryGetValue(suitId, out EquipSuitTable? suit))
                return 0;
            for (int i = 0; i < Math.Min(count, suit.SkillEffect.Count); i++)
            {
                int effectId = suit.SkillEffect[i];
                if (effectId <= 0)
                    continue;
                if (!PowerSuitEffects.TryGetValue(effectId, out EquipSuitEffectTable? effect))
                    return 0;
                ability += effect.Ability;
            }
        }
        return ability;
    }

    private static long GetEnhancementPower(CharacterData character)
    {
        if (!PowerEnhanceSkills.TryGetValue((int)character.Id, out EnhanceSkillTable? template))
            return 0;
        Dictionary<int, CharacterSkill> activeGroups = new();
        foreach (CharacterSkill skill in character.EnhanceSkillList)
            foreach (int groupId in template.SkillGroupId)
                if (groupId != 0 && PowerEnhanceGroups[groupId].SkillId.Contains((int)skill.Id))
                    activeGroups[groupId] = skill;
        long ability = 0;
        foreach (CharacterSkill skill in activeGroups.Values)
            ability += PowerEnhanceEffects[checked((int)skill.Id * 100 + skill.Level)].Ability;
        return ability;
    }

    private static IEnumerable<WeaponOverrunTable> GetPowerOverruns(EquipData equip)
    {
        if (equip.WeaponOverrunData.Level <= 0)
            yield break;
        foreach (var level in PowerOverruns[(int)equip.TemplateId].GroupBy(row => row.Level))
        {
            if (level.Key > equip.WeaponOverrunData.Level)
                continue;
            WeaponOverrunTable? row = level.FirstOrDefault(row => row.CharacterId == equip.CharacterId)
                ?? level.FirstOrDefault(row => row.CharacterId == 0);
            if (row is not null)
                yield return row;
        }
    }

    private static long GetOverrunPower(CharacterData character, EquipData weapon)
    {
        int suitId = weapon.WeaponOverrunData.ChoseSuit;
        int suitType = 0;
        if (suitId is 1 or 2) // XEnumConst.EQUIP.DEFAULT_SUIT_ID: NORMAL / ISOMER.
            suitType = suitId;
        else if (suitId != 0 && PowerSuits.TryGetValue(suitId, out EquipSuitTable? suit))
        {
            int templateId = suit.EquipIds.FirstOrDefault(id => id != 0);
            if (templateId != 0)
                suitType = PowerEquips[templateId].CharacterType;
        }
        bool matches = suitType == 0 || suitType == PowerCharacters[(int)character.Id].Type;
        long ability = 0;
        foreach (WeaponOverrunTable row in GetPowerOverruns(weapon))
            if (row.OverrunType != 1 || (suitId != 0 && matches))
                ability += row.Ability ?? 0;
        return ability;
    }

    private static long GetPartnerPower()
    {
        // EN XPartnerManager applies this coefficient after partner BP. A zero coefficient
        // makes the partner's attributes and skills irrelevant, without trusting its cached Ability.
        string conversion = TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "PartnerAbilityConvert").Value;
        if (decimal.Parse(conversion, System.Globalization.CultureInfo.InvariantCulture) == 0)
            return 0;
        throw new InvalidOperationException("Nonzero PartnerAbilityConvert requires the corresponding partner power calculation.");
    }
}
