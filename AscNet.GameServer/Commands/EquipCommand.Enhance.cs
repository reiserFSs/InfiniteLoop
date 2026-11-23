using System.Text;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;

namespace AscNet.GameServer.Commands
{
    // list / inspect / level / breakthrough / resonance / overrun: every bound comes from the tables the
    // client reads (EquipBreakThrough, EquipResonance, AttribPool, CharacterSkillPool, WeaponSkillPool,
    // WeaponOverrun) and skill bonuses are kept under the client's CharacterSkillLevelEffect cap.
    internal partial class EquipCommand
    {
        private const int ListLimit = 30;

        private static void Reject(string reason) => throw new CommandMessageCallbackException($"Rejected: {reason}");

        private static string NameOf(EquipData equip) =>
            Character.ResolveEquipTemplate(equip.TemplateId)?.Name ?? $"template {equip.TemplateId}";

        private static string Describe(EquipData equip) =>
            $"#{equip.Id} {NameOf(equip)} (template {equip.TemplateId}) Lv{equip.Level} BT{equip.Breakthrough}" +
            (equip.CharacterId > 0 ? $" worn by {equip.CharacterId}" : " unworn");

        private EquipData SingleTarget()
        {
            if (!uint.TryParse(Target, out uint id))
                Reject("give the equip record id (see /equip list).");
            if (session.character.Equips.Find(equip => equip.Id == id) is EquipData byRecord)
                return byRecord;
            // Players often type the weapon's id from the game data: that is the template, not the record.
            List<EquipData> copies = session.character.Equips.Where(equip => equip.TemplateId == id).ToList();
            if (copies.Count == 1)
                return copies[0];
            if (copies.Count > 1)
                Reject($"you own {copies.Count} copies of {NameOf(copies[0])} (template {id}); give one record id: "
                    + string.Join(", ", copies.Select(equip => $"#{equip.Id} Lv{equip.Level}" + (equip.CharacterId > 0 ? $" worn by {equip.CharacterId}" : ""))) + ".");
            throw new CommandMessageCallbackException(Character.ResolveEquipTemplate(id) is EquipTable row
                ? $"Rejected: you don't own {row.Name} (template {id})."
                : $"Rejected: no equip with record id {id}. Use /equip list.");
        }

        private List<EquipData> Targets()
        {
            List<EquipData> equips;
            if (Target.Equals("all", StringComparison.OrdinalIgnoreCase))
                equips = session.character.Equips.Where(equip => equip.CharacterId > 0).ToList();
            else if (Target.Length > 1 && char.ToLowerInvariant(Target[0]) == 'c')
            {
                uint characterId = uint.Parse(Target[1..]);
                if (!session.character.Characters.Any(character => character.Id == characterId))
                    Reject($"character {characterId} is not owned.");
                equips = session.character.Equips.Where(equip => equip.CharacterId == (int)characterId).ToList();
            }
            else
                equips = [SingleTarget()];
            if (equips.Count == 0)
                Reject("no matching equip is worn.");
            return equips;
        }

        private void SaveAndPush(IEnumerable<EquipData> changed)
        {
            NotifyEquipDataList notify = new();
            notify.EquipDataList.AddRange(changed);
            session.character.SaveChecked();
            session.SendPush(notify);
        }

        private void List()
        {
            IEnumerable<EquipData> equips = session.character.Equips;
            string filter = Target.ToLowerInvariant();
            if (filter is "weapons" or "memories")
                equips = equips.Where(equip => (Character.ResolveEquipTemplate(equip.TemplateId)?.Site ?? 0) == 0 == (filter == "weapons"));
            else if (filter.Length > 1 && filter[0] == 'c')
                equips = equips.Where(equip => equip.CharacterId == int.Parse(filter[1..]));
            else if (filter.Length > 0 && filter != "all")
                Reject("list takes weapons, memories, all or c<characterId>; use /equip inspect <recordId> for one equip.");

            List<EquipData> rows = equips.OrderBy(equip => equip.Id).ToList();
            StringBuilder text = new($"{rows.Count} equip(s):");
            foreach (EquipData equip in rows.Take(ListLimit))
                text.Append('\n').Append(Describe(equip));
            if (rows.Count > ListLimit)
                text.Append($"\n... {rows.Count - ListLimit} more, narrow with weapons|memories|c<characterId>.");
            throw new CommandMessageCallbackException(text.ToString());
        }

        private static int MaxBreakthrough(uint templateId) =>
            TableReaderV2.Parse<EquipBreakThroughTable>().Where(row => (uint)row.EquipId == templateId).Select(row => row.Times).DefaultIfEmpty(0).Max();

        // Levels 1..N all present for this weapon as worn by `characterId` (row CharacterId 0 = any wearer).
        private static int MaxOverrunLevel(uint templateId, int characterId)
        {
            HashSet<int> levels = TableReaderV2.Parse<WeaponOverrunTable>()
                .Where(row => row.WeaponId == templateId && (row.CharacterId == 0 || row.CharacterId == characterId))
                .Select(row => row.Level)
                .ToHashSet();
            int max = 0;
            while (levels.Contains(max + 1))
                max++;
            return max;
        }

        private void Inspect()
        {
            EquipData equip = SingleTarget();
            EquipTable? row = Character.ResolveEquipTemplate(equip.TemplateId);
            EquipBreakThroughTable? step = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough);
            bool weapon = row is { Site: 0 };
            StringBuilder text = new(Describe(equip));
            text.Append($"\n{(weapon ? "Weapon" : $"Memory site {row?.Site}")}, {row?.Star}-star");
            text.Append($"\nLevel {equip.Level}/{step?.LevelLimit}, breakthrough {equip.Breakthrough}/{MaxBreakthrough(equip.TemplateId)}");

            EquipResonanceTable? resonance = TableReaderV2.Parse<EquipResonanceTable>().Find(r => r.Id == equip.TemplateId);
            int slots = weapon ? 3 : 2;
            for (int slot = 1; slot <= slots; slot++)
            {
                int attribPool = resonance?.AttribPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                int skillPool = resonance?.CharacterSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                int weaponPool = resonance?.WeaponSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                if (attribPool + skillPool + weaponPool <= 0)
                    continue;
                ResonanceInfo? current = equip.ResonanceInfo.Find(info => info.Slot == slot);
                text.Append($"\nResonance slot {slot}: ")
                    .Append(current is null ? "empty" : $"{current.Type} {current.TemplateId} (character {current.CharacterId})")
                    .Append($" [pools: attrib {attribPool}, character skill {skillPool}, weapon skill {weaponPool}]");
            }
            if (weapon)
                text.Append($"\nOverrun level {equip.WeaponOverrunData.Level}/{MaxOverrunLevel(equip.TemplateId, equip.CharacterId)}, " +
                    $"active suits [{string.Join(",", equip.WeaponOverrunData.ActiveSuits)}], chosen suit {equip.WeaponOverrunData.ChoseSuit}");
            throw new CommandMessageCallbackException(text.ToString());
        }

        private int ParseValue(string what)
        {
            if (Value.Length == 0)
                Reject($"give {what} (a number or max).");
            return Value.Equals("max", StringComparison.OrdinalIgnoreCase) ? int.MaxValue
                : int.TryParse(Value, out int number) ? number : throw new CommandMessageCallbackException($"Rejected: {what} is out of range.");
        }

        private void Level()
        {
            int wanted = ParseValue("a level");
            List<EquipData> equips = Targets();
            List<(EquipData Equip, int Level)> plan = [];
            foreach (EquipData equip in equips)
            {
                EquipBreakThroughTable step = Character.ResolveEquipBreakThrough(equip.TemplateId, equip.Breakthrough)
                    ?? throw new CommandMessageCallbackException($"Rejected: #{equip.Id} has no breakthrough table row.");
                int level = wanted == int.MaxValue ? step.LevelLimit : wanted;
                if (level < 1 || level > step.LevelLimit)
                    Reject($"#{equip.Id} {NameOf(equip)} level must be 1..{step.LevelLimit} at breakthrough {equip.Breakthrough} (raise it with /equip breakthrough).");
                plan.Add((equip, level));
            }
            foreach ((EquipData equip, int level) in plan)
            {
                if (equip.Level != level)
                    equip.Exp = 0;
                equip.Level = level;
                equip.IsRecycle = false;
            }
            SaveAndPush(equips);
            throw new CommandMessageCallbackException($"Set level on {equips.Count} equip(s): {string.Join(", ", plan.Select(p => $"#{p.Equip.Id}=Lv{p.Level}"))}.");
        }

        private void Breakthrough()
        {
            int wanted = ParseValue("a breakthrough step");
            List<EquipData> equips = Targets();
            List<(EquipData Equip, EquipBreakThroughTable Step)> plan = [];
            foreach (EquipData equip in equips)
            {
                int max = MaxBreakthrough(equip.TemplateId);
                int step = wanted == int.MaxValue ? max : wanted;
                EquipBreakThroughTable? row = step < 0 ? null : Character.ResolveEquipBreakThrough(equip.TemplateId, step);
                if (row is null)
                    Reject($"#{equip.Id} {NameOf(equip)} breakthrough must be 0..{max}.");
                plan.Add((equip, row!));
            }
            foreach ((EquipData equip, EquipBreakThroughTable step) in plan)
            {
                if (equip.Breakthrough != step.Times)
                    equip.Exp = 0;
                equip.Breakthrough = step.Times;
                equip.Level = Math.Clamp(equip.Level, 1, step.LevelLimit);
                equip.IsRecycle = false;
            }
            SaveAndPush(equips);
            throw new CommandMessageCallbackException($"Set breakthrough on {equips.Count} equip(s): {string.Join(", ", plan.Select(p => $"#{p.Equip.Id}=BT{p.Step.Times} (Lv{p.Equip.Level}/{p.Step.LevelLimit})"))}.");
        }

        // A candidate copy of the equip worn by `characterId`, with the proposed resonance/overrun state.
        private static EquipData Simulate(EquipData equip, int characterId, List<ResonanceInfo> resonances, WeaponOverrunData overrun) => new()
        {
            Id = equip.Id,
            TemplateId = equip.TemplateId,
            CharacterId = characterId,
            ResonanceInfo = resonances,
            WeaponOverrunData = overrun
        };

        // Rejects when the proposal raises a skill's bonus so base + bonuses passes the client's
        // CharacterSkillLevelEffect maximum (the battle hangs above it).
        private void RejectSkillCapOverflow(EquipData equip, int characterId, List<ResonanceInfo> resonances, WeaponOverrunData overrun)
        {
            CharacterData? character = session.character.Characters.Find(c => c.Id == (uint)characterId);
            if (character is null)
                return;
            IReadOnlyDictionary<int, int> plus = FightModule.BuildCharacterSkillPlus(session, character);
            List<EquipData> others = session.character.Equips.Where(other => other.Id != equip.Id).ToList();
            List<EquipData> before = [.. others, Simulate(equip, characterId, equip.ResonanceInfo, equip.WeaponOverrunData)];
            List<EquipData> after = [.. others, Simulate(equip, characterId, resonances, overrun)];
            foreach (CharacterSkill skill in character.SkillList)
            {
                int skillId = (int)skill.Id;
                if (Character.SkillBonusLevels(after, character, skillId, plus) <= Character.SkillBonusLevels(before, character, skillId, plus))
                    continue;
                int cap = Character.EffectiveSkillCap(after, character, skillId, plus);
                if (skill.Level > cap)
                    Reject($"character {characterId} skill {skillId} is level {skill.Level}; with this change the client allows at most level {cap} " +
                        $"(max {Character.SkillEffectMaxLevel(skillId)} including resonance/overrun bonuses). Lower the skill level first.");
            }
        }

        private void Resonance()
        {
            EquipData equip = SingleTarget();
            EquipTable row = Character.ResolveEquipTemplate(equip.TemplateId)
                ?? throw new CommandMessageCallbackException($"Rejected: #{equip.Id} has no equip table row.");
            bool memory = row.Site > 0;
            if (!int.TryParse(Value, out int slot) || slot < 1 || slot > (memory ? 2 : 3))
                Reject($"resonance slot must be 1..{(memory ? 2 : 3)} for this equip.");
            string kind = Option.ToLowerInvariant();
            List<ResonanceInfo> resonances = equip.ResonanceInfo.Where(info => info.Slot != slot).ToList();

            if (kind != "clear")
            {
                int id = 0;
                if (kind is not ("attrib" or "skill" or "weaponskill") || !int.TryParse(ResonanceId, out id) || id <= 0)
                    Reject("use: resonance <recordId> <slot> clear | attrib|skill|weaponskill <id> [characterId].");
                EquipResonanceTable? table = TableReaderV2.Parse<EquipResonanceTable>().Find(r => r.Id == equip.TemplateId);
                int characterId = BoundCharacter.Length > 0 ? int.Parse(BoundCharacter) : equip.CharacterId;
                if ((memory || kind != "attrib") && characterId == 0)
                    Reject("this resonance needs a character: wear the equip or pass characterId.");
                if (characterId != 0 && !session.character.Characters.Any(c => c.Id == (uint)characterId))
                    Reject($"character {characterId} is not owned.");

                EquipResonanceType type;
                if (kind == "attrib")
                {
                    int pool = table?.AttribPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    if (pool <= 0 || !TableReaderV2.Parse<AttribPoolTable>().Any(entry => entry.PoolId == pool && entry.Id == id))
                        Reject($"attribute entry {id} is not in this equip's slot {slot} pool (AttribPool).");
                    type = EquipResonanceType.Attrib;
                }
                else if (kind == "skill")
                {
                    int pool = table?.CharacterSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    if (!memory || pool <= 0 || !EquipModule.CharacterOwnsResonanceSkill(
                            TableReaderV2.Parse<CharacterSkillTable>().Find(skills => skills.CharacterId == characterId), pool, id,
                            TableReaderV2.Parse<CharacterSkillPoolTable>(), TableReaderV2.Parse<CharacterSkillGroupTable>()))
                        Reject($"skill {id} is not in this memory's slot {slot} skill pool for character {characterId}.");
                    type = EquipResonanceType.CharacterSkill;
                }
                else
                {
                    int pool = table?.WeaponSkillPoolId.ElementAtOrDefault(slot - 1) ?? 0;
                    if (memory)
                        Reject("weapon skill resonance applies to weapons only.");
                    if (pool <= 0 || !TableReaderV2.Parse<WeaponSkillPoolTable>().Any(entry =>
                            entry.PoolId == pool && entry.CharacterId == characterId && entry.SkillId.Contains(id)))
                        Reject($"weapon skill {id} is not in this weapon's slot {slot} pool for character {characterId}.");
                    type = EquipResonanceType.WeaponSkill;
                }
                resonances.Add(new ResonanceInfo { Slot = slot, Type = type, CharacterId = characterId, TemplateId = id });
                if (type == EquipResonanceType.CharacterSkill)
                    RejectSkillCapOverflow(equip, characterId, resonances, equip.WeaponOverrunData);
            }

            equip.ResonanceInfo = resonances.OrderBy(info => info.Slot).ToList();
            equip.UnconfirmedResonanceInfo.RemoveAll(info => info.Slot == slot);
            session.PendingEquipResonances.Remove((equip.Id, slot));
            equip.IsRecycle = false;
            SaveAndPush([equip]);
            throw new CommandMessageCallbackException(kind == "clear"
                ? $"Cleared resonance slot {slot} on #{equip.Id}."
                : $"Set resonance slot {slot} on #{equip.Id} to {kind} {ResonanceId}.");
        }

        private void Overrun()
        {
            EquipData equip = SingleTarget();
            EquipTable? row = Character.ResolveEquipTemplate(equip.TemplateId);
            if (row is not { Site: 0, WeaponSkillId: > 0 })
                Reject($"#{equip.Id} is not a weapon.");
            equip.WeaponOverrunData ??= new();
            equip.WeaponOverrunData.ActiveSuits ??= [];
            int max = MaxOverrunLevel(equip.TemplateId, equip.CharacterId);

            if (Value.Equals("suit", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(Option, out int suitId))
                    Reject("use: overrun <recordId> suit <suitId>.");
                if (equip.WeaponOverrunData.Level <= 0)
                    Reject("raise the overrun level first.");
                if (!TableReaderV2.Parse<EquipTable>().Any(suit => suit.SuitId == suitId && suit.Quality == 6))
                    Reject($"suit {suitId} is not a 6-star memory suit.");
                if (!equip.WeaponOverrunData.ActiveSuits.Contains(suitId))
                    equip.WeaponOverrunData.ActiveSuits.Add(suitId);
                equip.WeaponOverrunData.ChoseSuit = suitId;
                SaveAndPush([equip]);
                throw new CommandMessageCallbackException($"Activated and chose overrun suit {suitId} on #{equip.Id}.");
            }

            int level = ParseValue("an overrun level");
            if (level == int.MaxValue)
                level = max;
            if (level < 0 || level > max)
                Reject($"overrun level must be 0..{max} for this weapon{(equip.CharacterId == 0 ? " (character-specific levels need it worn by that character)" : "")}.");

            WeaponOverrunData overrun = new() { Level = level, ActiveSuits = level > 0 ? [.. equip.WeaponOverrunData.ActiveSuits] : [], ChoseSuit = level > 0 ? equip.WeaponOverrunData.ChoseSuit : 0 };
            if (equip.CharacterId > 0)
                RejectSkillCapOverflow(equip, equip.CharacterId, equip.ResonanceInfo, overrun);
            equip.WeaponOverrunData = overrun;
            equip.IsRecycle = false;
            SaveAndPush([equip]);
            throw new CommandMessageCallbackException($"Set overrun level {level} on #{equip.Id}.");
        }
    }
}
