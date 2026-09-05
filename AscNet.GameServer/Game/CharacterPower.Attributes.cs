using System.Globalization;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fight.npc;

namespace AscNet.GameServer.Game;

internal static partial class CharacterPower
{
    private static int GetPowerNpcType(CharacterData character) => AttributeTables.Npcs[
        AttributeTables.Qualities[((int)character.Id, character.Quality)].NpcId].Type;

    private static long CalculateAttributePower(CharacterData character, IReadOnlyList<EquipData> equips)
    {
        CharacterQualityTable quality = AttributeTables.Qualities[((int)character.Id, character.Quality)];
        NpcTable npc = AttributeTables.Npcs[quality.NpcId];
        PowerAttributes attributes = AttributeTables.Numeric[npc.AttribId];
        if (npc.PromotedId > 0 && character.Level > 1)
            attributes += AttributeTables.Promoted[npc.PromotedId].Multiply((long)(character.Level - 1) << 32);

        PowerAttributes numeric = default, growth = default, promoted = default;
        for (int star = 0; star < Math.Min(character.Star, quality.AttrId.Count); star++)
            if (quality.AttrId[star] > 0)
                numeric += AttributeTables.Numeric[quality.AttrId[star]];
        int gradeId = AttributeTables.Grades[((int)character.Id, character.Grade)].AttrId;
        if (gradeId > 0)
            numeric += AttributeTables.Numeric[gradeId];

        foreach (EquipData equip in equips)
        {
            EquipBreakThroughTable breakthrough = AttributeTables.Breakthroughs[((int)equip.TemplateId, equip.Breakthrough)];
            if (breakthrough.AttribId > 0)
                numeric += AttributeTables.Numeric[breakthrough.AttribId];
            foreach (ResonanceInfo resonance in equip.ResonanceInfo ?? [])
            {
                if (resonance.Type != EquipResonanceType.Attrib ||
                    (resonance.CharacterId != 0 && resonance.CharacterId != character.Id))
                    continue;
                AttribPoolTable pool = AttributeTables.Pools[resonance.TemplateId];
                if (pool.AttribId > 0)
                    numeric += AttributeTables.Numeric[pool.AttribId];
                if (pool.AttribGrowRateId > 0)
                    growth += AttributeTables.GrowRates[pool.AttribGrowRateId];
            }
            if (equip.AwakeSlotList is { Count: > 0 })
            {
                EquipAwakeTable awake = AttributeTables.Awake[(int)equip.TemplateId];
                foreach (object value in equip.AwakeSlotList)
                {
                    int slot = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    foreach (ResonanceInfo resonance in equip.ResonanceInfo ?? [])
                        if (resonance.Slot == slot && (resonance.CharacterId == 0 || resonance.CharacterId == character.Id))
                            numeric += AttributeTables.Numeric[awake.AttribId[slot - 1]];
                }
            }
            if (breakthrough.AttribPromotedId > 0 && equip.Level > 1)
                promoted += AttributeTables.Promoted[breakthrough.AttribPromotedId].Multiply((long)(equip.Level - 1) << 32);
        }

        // XCharacterAgency.GetFightNpcData supplies no revision and an empty attribute-group list.
        // Grow rates affect only NPC base + level growth; provider sums are added afterwards.
        attributes += attributes.Multiply(growth);
        attributes += numeric;
        attributes += promoted;
        PowerAttributes weighted = attributes.Multiply(AttributeTables.Weights);
        return AddPowerFixed(AddPowerFixed(AddPowerFixed(weighted.Life, weighted.Attack), weighted.Defense), weighted.Crit) >> 32;
    }

    private readonly record struct PowerAttributes(long Life, long Attack, long Defense, long Crit)
    {
        public static PowerAttributes operator +(PowerAttributes a, PowerAttributes b) => new(
            AddPowerFixed(a.Life, b.Life), AddPowerFixed(a.Attack, b.Attack),
            AddPowerFixed(a.Defense, b.Defense), AddPowerFixed(a.Crit, b.Crit));
        public PowerAttributes Multiply(long value) => Multiply(new PowerAttributes(value, value, value, value));
        public PowerAttributes Multiply(PowerAttributes b) => new(
            MultiplyPowerFixed(Life, b.Life), MultiplyPowerFixed(Attack, b.Attack),
            MultiplyPowerFixed(Defense, b.Defense), MultiplyPowerFixed(Crit, b.Crit));
    }

    private static long AddPowerFixed(long x, long y)
    {
        long sum = unchecked(x + y);
        return ((sum ^ x) & ~(x ^ y)) < 0 ? (x > 0 ? long.MaxValue : long.MinValue) : sum;
    }

    // Mathematics.fix.op_Multiply, matching native Q32.32 limb/carry and saturation guards.
    private static long MultiplyPowerFixed(long x, long y)
    {
        unchecked
        {
            long high = (x >> 32) * (y >> 32);
            ulong low = (ulong)(uint)x * (uint)y;
            long a = (long)(uint)x * (y >> 32), b = (long)(uint)y * (x >> 32);
            long first = (long)(low >> 32) + a, second = first + b, result = second + (high << 32);
            bool carry = (first ^ a) < 0 || (second ^ first ^ b) < 0 || (result ^ second ^ (high << 32)) < 0;
            bool sameSign = (x ^ y) >= 0;
            if (sameSign && (result < 0 || (carry && x > 0))) return long.MaxValue;
            if (!sameSign && result > 0) return long.MinValue;
            if ((high >> 32) != 0 && (high >> 32) != -1) return sameSign ? long.MaxValue : long.MinValue;
            if (!sameSign && result > Math.Min(x, y) && Math.Min(x, y) < -(1L << 32) && Math.Max(x, y) > (1L << 32))
                return long.MinValue;
            return result;
        }
    }

    // Imported hexadecimal values preserve the binary table's original mantissa/exponent:
    // ParseEx(m, e) = m * (2^32 / 10^e), with integer division BEFORE multiplication.
    private static long ParsePowerFixed(string text) =>
        long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static class AttributeTables
    {
        internal static readonly Dictionary<(int, int), CharacterQualityTable> Qualities = TableReaderV2.Parse<CharacterQualityTable>().ToDictionary(r => (r.CharacterId, r.Quality));
        internal static readonly Dictionary<(int, int), CharacterGradeTable> Grades = TableReaderV2.Parse<CharacterGradeTable>().ToDictionary(r => (r.CharacterId, r.Grade));
        internal static readonly Dictionary<int, NpcTable> Npcs = TableReaderV2.Parse<NpcTable>().ToDictionary(r => r.Id);
        internal static readonly Dictionary<(int, int), EquipBreakThroughTable> Breakthroughs = TableReaderV2.Parse<EquipBreakThroughTable>().ToDictionary(r => (r.EquipId, r.Times));
        internal static readonly Dictionary<int, EquipAwakeTable> Awake = TableReaderV2.Parse<EquipAwakeTable>().ToDictionary(r => r.Id);
        internal static readonly Dictionary<int, AttribPoolTable> Pools = TableReaderV2.Parse<AttribPoolTable>().ToDictionary(r => r.Id);
        internal static readonly Dictionary<int, PowerAttributes> Numeric = TableReaderV2.Parse<AttribTable>().ToDictionary(r => r.Id,
            r => new PowerAttributes(ParsePowerFixed(r.Life), ParsePowerFixed(r.AttackNormal), ParsePowerFixed(r.DefenseNormal), ParsePowerFixed(r.Crit)));
        internal static readonly Dictionary<int, PowerAttributes> Promoted = TableReaderV2.Parse<AttribPromotedTable>().ToDictionary(r => r.Id,
            r => new PowerAttributes(ParsePowerFixed(r.Life), ParsePowerFixed(r.AttackNormal), ParsePowerFixed(r.DefenseNormal), ParsePowerFixed(r.Crit)));
        internal static readonly Dictionary<int, PowerAttributes> GrowRates = TableReaderV2.Parse<AttribGrowRateTable>().ToDictionary(r => r.Id,
            r => new PowerAttributes(ParsePowerFixed(r.Life), ParsePowerFixed(r.AttackNormal), ParsePowerFixed(r.DefenseNormal), ParsePowerFixed(r.Crit)));
        private static readonly Dictionary<string, long> Ability = TableReaderV2.Parse<AttribAbilityTable>().ToDictionary(r => r.Key, r => Math.Max(0, ParsePowerFixed(r.Ability)));
        internal static readonly PowerAttributes Weights = new(Ability["Life"], Ability["AttackNormal"], Ability["DefenseNormal"], Ability["Crit"]);
    }
}
