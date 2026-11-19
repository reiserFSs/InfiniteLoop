using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.teamrecommend;
using AscNet.Table.V2.share.trust;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    // --character-command-only: /character max|set validated against the tables, the client skill cap, pushes and BSON reload.
    private static void ValidateCharacterCommandCompatibility()
    {
        CommandFactory.commands.Clear();
        CommandFactory.LoadCommands();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out _, out _, out _, out _);

        List<TeamRecommendBaseCharacterTable> bases = TableReaderV2.Parse<TeamRecommendBaseCharacterTable>();
        List<int> ids = bases.Select(row => row.CharacterId).Distinct()
            .Where(id => AscNet.Common.Database.Character.IsOwnableCharacter((uint)id)).Take(2).ToList();
        const long uid = 99_880;
        AscNet.Common.Database.Character roster = CreateDrawCompatibilityCharacter(uid);
        roster.Partners = [];
        foreach (int id in ids)
            roster.AddCharacter((uint)id);
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 100;
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "character-command");
        h.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        string Run(params string[] args)
        {
            try { CommandFactory.CreateCommand("character", h.Session, args)!.Execute(); return string.Empty; }
            catch (CommandMessageCallbackException message) { return message.Message; }
        }
        void Rejected(string expectedText, params string[] args)
        {
            string before = Convert.ToHexString(MessagePackSerializer.Serialize(roster.Characters));
            string message = Run(args);
            AssertEqual(true, message.Contains(expectedText, StringComparison.OrdinalIgnoreCase), $"/character {string.Join(' ', args)} rejected ({message})");
            AssertEqual(before, Convert.ToHexString(MessagePackSerializer.Serialize(roster.Characters)), $"/character {string.Join(' ', args)} leaves state untouched");
        }
        List<string> ReadPushNames(int count)
        {
            List<string> names = [];
            while (names.Count < count)
            {
                Packet packet = h.ReadPacket("character command push");
                names.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content).Name);
            }
            return names;
        }
        CharacterData Owned(int id) => roster.Characters.Single(row => row.Id == (uint)id);
        int First = ids[0], Second = ids[1];

        // ---- set: two distinct inputs per field, each pushing one NotifyCharacterDataList ----
        foreach (int id in ids)
        {
            CharacterTable template = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == id);
            int maxLevel = AscNet.Common.Database.Character.characterLevelUpTemplates
                .Where(row => row.Type == template.LevelUpTemplateId).Max(row => row.Level);
            foreach (int level in new[] { 20, 45 })
            {
                AssertEqual("", Run("set", id.ToString(), "level", level.ToString()), $"set level {level}");
                AssertEqual(level, Owned(id).Level, $"{id} level {level}");
                AssertEqual("NotifyCharacterDataList", ReadPushNames(1)[0], "set pushes the character");
            }
            Rejected("Level must be", "set", id.ToString(), "level", (maxLevel + 1).ToString());
            Rejected("Level must be", "set", id.ToString(), "level", "0");

            List<int> grades = TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == id).Select(row => row.Grade).Order().ToList();
            foreach (int grade in new[] { grades.Min(), grades.Max() })
            {
                AssertEqual("", Run("set", id.ToString(), "grade", grade.ToString()), $"set grade {grade}");
                AssertEqual(grade, Owned(id).Grade, $"{id} grade {grade}");
                ReadPushNames(1);
            }
            Rejected("Grade must be", "set", id.ToString(), "grade", (grades.Max() + 1).ToString());

            List<int> qualities = TableReaderV2.Parse<CharacterQualityTable>().Where(row => row.CharacterId == id).Select(row => row.Quality).Order().ToList();
            foreach (int quality in new[] { qualities.Min(), qualities.Max() })
            {
                AssertEqual("", Run("set", id.ToString(), "quality", quality.ToString()), $"set quality {quality}");
                AssertEqual((quality, 0), (Owned(id).Quality, Owned(id).Star), $"{id} quality {quality} resets star");
                ReadPushNames(1);
            }
            Rejected("Quality must be", "set", id.ToString(), "quality", (qualities.Max() + 1).ToString());
            AssertEqual("", Run("set", id.ToString(), "quality", qualities.Min().ToString()), "back to the lowest quality");
            ReadPushNames(1);
            int maxStar = TableReaderV2.Parse<AscNet.Table.V2.share.character.quality.CharacterQualityFragmentTable>()
                .Single(row => row.Type == template.Type && row.Quality == qualities.Min()).StarUseCount.Count;
            foreach (int star in new[] { 0, maxStar })
            {
                AssertEqual("", Run("set", id.ToString(), "star", star.ToString()), $"set star {star}");
                AssertEqual(star, Owned(id).Star, $"{id} star {star}");
                ReadPushNames(1);
            }
            Rejected("Star must be", "set", id.ToString(), "star", (maxStar + 1).ToString());

            List<int> trust = TableReaderV2.Parse<CharacterTrustExpTable>().Where(row => row.CharacterId == id).Select(row => row.TrustLv).Order().ToList();
            foreach (int level in new[] { 2, trust.Max() })
            {
                AssertEqual("", Run("set", id.ToString(), "trust", level.ToString()), $"set trust {level}");
                AssertEqual(level, Owned(id).TrustLv, $"{id} trust {level}");
                ReadPushNames(1);
            }
            Rejected("Trust level must be", "set", id.ToString(), "trust", (trust.Max() + 1).ToString());
        }
        Rejected("not owned", "set", "1021999", "level", "5");
        Rejected("Usage", "set", "all", "level", "5");
        Rejected("Usage", "set", First.ToString(), "skill", "5");

        // ---- skill: bounded by the upgrade terminal and the client battle cap ----
        CharacterData subject = Owned(First);
        CharacterSkill skill = subject.SkillList.First(row => AscNet.Common.Database.Character.SkillEffectMaxLevel((int)row.Id) >= 18
            && AscNet.Common.Database.Character.SkillUpgradeTerminalLevel((int)row.Id) >= 10);
        int terminal = AscNet.Common.Database.Character.SkillUpgradeTerminalLevel((int)skill.Id);
        foreach (int level in new[] { 3, terminal })
        {
            AssertEqual("", Run("set", First.ToString(), "skill", skill.Id.ToString(), level.ToString()), $"set skill {level}");
            AssertEqual(level, subject.SkillList.Single(row => row.Id == skill.Id).Level, $"skill {skill.Id} level {level}");
            ReadPushNames(1);
        }
        Rejected("level must be", "set", First.ToString(), "skill", skill.Id.ToString(), (terminal + 1).ToString());
        Rejected("level must be", "set", First.ToString(), "skill", skill.Id.ToString(), "34");
        Rejected("level must be", "set", First.ToString(), "skill", skill.Id.ToString(), "0");
        Rejected("no unlocked skill", "set", First.ToString(), "skill", "1", "1");

        // ---- max: stored base 34 + six awareness resonances on one skill must stay within the client cap ----
        List<EquipTable> memories = Enumerable.Range(1, 6).Select(site => TableReaderV2.Parse<EquipTable>()
            .First(row => row.Site == site && AscNet.Common.Database.Character.IsOwnableEquipTemplate(row))).ToList();
        CharacterSkillTable skillRow = TableReaderV2.Parse<CharacterSkillTable>().Single(row => row.CharacterId == First);
        int resonantSkill = (int)subject.SkillList.First(row => AscNet.Common.Database.Character.SkillEffectMaxLevel((int)row.Id) == 35).Id;
        foreach (EquipTable memory in memories)
        {
            EquipData equip = roster.AddEquip((uint)memory.Id, First)!;
            equip.ResonanceInfo.Add(new ResonanceInfo
            {
                Slot = 1, Type = EquipResonanceType.CharacterSkill, CharacterId = First, TemplateId = resonantSkill
            });
        }
        subject.SkillList.Single(row => row.Id == resonantSkill).Level = 34;
        AssertEqual("", Run("max", First.ToString()), "max");
        List<string> pushes = ReadPushNames(1 + TableReaderV2.Parse<ExhibitionRewardTable>().Count(row => row.CharacterId == First));
        AssertEqual("NotifyCharacterDataList", pushes[^1], "max pushes the character last");
        AssertEqual(true, pushes[..^1].All(name => name == "NotifyGatherReward"), "max pushes each claimed liberation milestone");
        int effectMax = AscNet.Common.Database.Character.SkillEffectMaxLevel(resonantSkill);
        int bonus = roster.Equips.Where(row => row.CharacterId == First).Sum(row => row.ResonanceInfo.Count(r => r.Type == EquipResonanceType.CharacterSkill && r.TemplateId == resonantSkill));
        AssertEqual(6, bonus, "six resonances on the skill");
        int resonantBase = subject.SkillList.Single(row => row.Id == resonantSkill).Level;
        AssertEqual(Math.Min(AscNet.Common.Database.Character.SkillUpgradeTerminalLevel(resonantSkill), effectMax - bonus), resonantBase, "max picks the highest base within the cap");
        foreach (CharacterSkill each in subject.SkillList)
        {
            int eachBonus = roster.Equips.Where(row => row.CharacterId == First)
                .Sum(row => row.ResonanceInfo.Count(r => r.Type == EquipResonanceType.CharacterSkill && r.TemplateId == each.Id));
            AssertEqual(true, each.Level + eachBonus <= Math.Max(AscNet.Common.Database.Character.SkillEffectMaxLevel((int)each.Id), 1),
                $"skill {each.Id}: base {each.Level} + {eachBonus} stays within the client battle cap");
            AssertEqual(AscNet.Common.Database.Character.SkillUpgradeTerminalLevel((int)each.Id) is var t && each.Level <= t, true, $"skill {each.Id} <= terminal");
        }

        // Table maxima for the first character; the second keeps its own distinct values.
        AssertEqual("", Run("max", "all"), "max all");
        foreach (int id in ids)
        {
            CharacterTable template = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == id);
            CharacterData data = Owned(id);
            AssertEqual(AscNet.Common.Database.Character.characterLevelUpTemplates.Where(row => row.Type == template.LevelUpTemplateId).Max(row => row.Level), data.Level, $"{id} max level");
            AssertEqual(TableReaderV2.Parse<CharacterQualityTable>().Where(row => row.CharacterId == id).Max(row => row.Quality), data.Quality, $"{id} max quality");
            AssertEqual(TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == id).Max(row => row.Grade), data.Grade, $"{id} max grade");
            AssertEqual(TableReaderV2.Parse<CharacterTrustExpTable>().Where(row => row.CharacterId == id).Max(row => row.TrustLv), data.TrustLv, $"{id} max trust");
            AssertEqual(TableReaderV2.Parse<ExhibitionRewardTable>().Where(row => row.CharacterId == id).Max(row => row.LevelId), data.LiberateLv, $"{id} max liberation");
            AssertEqual(true, TableReaderV2.Parse<ExhibitionRewardTable>().Where(row => row.CharacterId == id).All(row => player.GatherRewards.Contains(row.Id)), $"{id} milestones claimed");
        }

        // ---- gear: the recommended weapon and memories, reused when owned and not duplicated on repeat ----
        int gearId = ids.First(id => id != First);
        TeamRecommendBaseCharacterTable recommended = bases.Where(row => row.CharacterId == gearId)
            .OrderByDescending(row => row.CharacterQualityStar).ThenByDescending(row => row.Id).First();
        string report = Run("max", gearId.ToString(), "gear");
        AssertEqual(true, report.Contains("gear", StringComparison.Ordinal), $"gear report ({report})");
        int worn(int templateId) => roster.Equips.Count(row => row.TemplateId == (uint)templateId && row.CharacterId == gearId);
        AssertEqual(1, worn(recommended.WeaponId), "recommended weapon worn");
        foreach (int memory in recommended.EquipIds.Where(id => id > 0))
            AssertEqual(1, worn(memory), $"recommended memory {memory} worn");
        int equipCount = roster.Equips.Count;
        Run("max", gearId.ToString(), "gear");
        AssertEqual(equipCount, roster.Equips.Count, "a repeat grants no duplicate gear");

        // ---- awaken: only characters with an unlocked Enhance group ----
        int awakenId = ids.FirstOrDefault(id => Owned(id).EnhanceSkillList.Count > 0);
        Console.WriteLine($"character-command awaken fixture: {(awakenId == 0 ? "none of the tested characters has an awakening group" : awakenId)}");
        if (awakenId != 0)
        {
            int limit = Owned(awakenId).EnhanceSkillList.Min(row => AscNet.Common.Database.Character.EnhanceSkillMaxLevel((int)row.Id));
            foreach (int level in new[] { 1, limit })
            {
                AssertEqual("", Run("set", awakenId.ToString(), "awaken", level.ToString()), $"set awaken {level}");
                AssertEqual(true, Owned(awakenId).EnhanceSkillList.All(row => row.Level == level), $"awaken {level}");
            }
            Rejected("Awaken level must be", "set", awakenId.ToString(), "awaken", (limit + 1).ToString());
        }

        // ---- persistence ----
        AscNet.Common.Database.Character reload = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(roster.ToBson());
        foreach (int id in ids)
            AssertEqual(Convert.ToHexString(MessagePackSerializer.Serialize(Owned(id))),
                Convert.ToHexString(MessagePackSerializer.Serialize(reload.Characters.Single(row => row.Id == (uint)id))), $"{id} survives a BSON reload");
    }
}
