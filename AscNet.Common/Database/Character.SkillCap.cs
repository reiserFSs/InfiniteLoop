using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;

namespace AscNet.Common.Database
{
    public partial class Character
    {
        private static readonly Lazy<Dictionary<int, int>> skillEffectMaxLevels = new(() =>
            TableReaderV2.Parse<CharacterSkillLevelEffectTable>()
                .GroupBy(row => row.SkillId)
                .ToDictionary(group => group.Key, group => group.Max(row => row.Level)));
        // Normal play stops at the first level >= 1 whose CharacterSkillUpgrade row is missing or costless
        // (UpgradeCharacterSkillGroup: "a missing or cost-less transition row marks the terminal level").
        private static readonly Lazy<Dictionary<int, int>> skillUpgradeTerminalLevels = new(() =>
            TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                .GroupBy(row => row.SkillId)
                .ToDictionary(group => group.Key, group =>
                {
                    HashSet<int> paid = group.Where(row => row.UseCoin.GetValueOrDefault() != 0
                        || row.UseSkillPoint.GetValueOrDefault() != 0).Select(row => row.Level).ToHashSet();
                    int level = 1;
                    while (paid.Contains(level))
                        level++;
                    return level;
                }));

        private static readonly Lazy<ILookup<int, WeaponOverrunTable>> overrunsByWeapon = new(() =>
            TableReaderV2.Parse<WeaponOverrunTable>().ToLookup(row => row.WeaponId));

        /// <summary>Highest level of the skill the client has a CharacterSkillLevelEffect row for (0 = unknown skill).</summary>
        public static int SkillEffectMaxLevel(int skillId) => skillEffectMaxLevels.Value.GetValueOrDefault(skillId);

        /// <summary>
        /// Highest base level normal play can reach (first missing/costless CharacterSkillUpgrade row);
        /// int.MaxValue when the skill has no upgrade rows.
        /// </summary>
        public static int SkillUpgradeTerminalLevel(int skillId) =>
            skillUpgradeTerminalLevels.Value.GetValueOrDefault(skillId, int.MaxValue);

        /// <summary>
        /// Levels the client adds on top of a base skill level (XFightCharacterManager.GetCharSkillLevelMap):
        /// one per awareness CharacterSkill resonance bound to this character whose skill group contains
        /// the skill, every active overrun row's UpSkillGroupLevel whose group contains the skill, and
        /// <paramref name="skillPlus"/> (Border Pact, NpcData.CharacterSkillPlus). Only equips carried by
        /// the character count, as in NpcData.Equips.
        /// </summary>
        public static int SkillBonusLevels(IEnumerable<EquipData> equips, CharacterData character, int skillId,
            IReadOnlyDictionary<int, int>? skillPlus = null)
        {
            Dictionary<int, IReadOnlyList<uint>> groups = SkillGroupsById.Value;
            int bonus = skillPlus?.GetValueOrDefault(skillId) ?? 0;
            foreach (EquipData equip in equips.Where(equip => equip.CharacterId == character.Id))
            {
                foreach (ResonanceInfo resonance in equip.ResonanceInfo ?? [])
                {
                    if (resonance.Type == EquipResonanceType.CharacterSkill && resonance.CharacterId == character.Id
                        && groups.Values.Any(skills => skills.Contains((uint)resonance.TemplateId) && skills.Contains((uint)skillId)))
                        bonus++;
                }

                int overrunLevel = equip.WeaponOverrunData?.Level ?? 0;
                if (overrunLevel <= 0)
                    continue;
                foreach (IGrouping<int, WeaponOverrunTable> level in overrunsByWeapon.Value[(int)equip.TemplateId]
                             .Where(row => row.Level <= overrunLevel).GroupBy(row => row.Level))
                {
                    WeaponOverrunTable? row = level.FirstOrDefault(row => row.CharacterId == equip.CharacterId)
                        ?? level.FirstOrDefault(row => row.CharacterId == 0);
                    if (row is { UpSkillGroupId: > 0, UpSkillGroupLevel: > 0 }
                        && groups.TryGetValue(row.UpSkillGroupId, out IReadOnlyList<uint>? skills)
                        && skills.Contains((uint)skillId))
                        bonus += row.UpSkillGroupLevel;
                }
            }
            return bonus;
        }

        private static readonly Lazy<Dictionary<int, IReadOnlyList<uint>>> SkillGroupsById = new(() =>
            BuildCharacterSkillIdsByGroupId(TableReaderV2.Parse<AscNet.Table.V2.share.character.skill.CharacterSkillGroupTable>()));

        /// <summary>
        /// Highest base level for the skill such that base + resonance + overrun + Border Pact never exceeds
        /// the client's CharacterSkillLevelEffect maximum (the battle hangs above it), also bounded by
        /// <see cref="SkillUpgradeTerminalLevel"/>. May be below 1 when bonuses alone already reach the maximum:
        /// callers adding a bonus must reject when the skill's current base level exceeds the result.
        /// </summary>
        public static int EffectiveSkillCap(IEnumerable<EquipData> equips, CharacterData character, int skillId,
            IReadOnlyDictionary<int, int>? skillPlus = null)
        {
            int effectMax = SkillEffectMaxLevel(skillId);
            if (effectMax <= 0)
                return SkillUpgradeTerminalLevel(skillId);
            return Math.Min(SkillUpgradeTerminalLevel(skillId),
                effectMax - SkillBonusLevels(equips, character, skillId, skillPlus));
        }
    }
}
