using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.attrib;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // --equip-command-only: /equip list|inspect|level|breakthrough|resonance|overrun against the Equip* tables.
    private static void ValidateEquipCommand()
    {
        const long playerId = 99_670;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters.Clear();
        roster.Equips = [];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "equip-command");

        string Run(params string[] args)
        {
            string message = RunRaw(args);
            if (message.StartsWith("Rejected:", StringComparison.Ordinal))
                throw new InvalidDataException($"/equip {string.Join(' ', args)} unexpectedly rejected: {message}");
            return message;
        }
        string RunRaw(params string[] args)
        {
            try { new EquipCommand(harness.Session, args).Execute(); }
            catch (CommandMessageCallbackException ex) { return ex.Message; }
            throw new InvalidDataException($"/equip {string.Join(' ', args)}: expected command feedback.");
        }
        void Drain()
        {
            Thread.Sleep(150);
            while (harness.TryReadAvailablePacket("equip drain", out _)) { }
        }
        NotifyEquipDataList ReadPush(string name) =>
            ReadPushPayload<NotifyEquipDataList>(harness, nameof(NotifyEquipDataList), name, maxPacketsToRead: 4);
        Character Reload() => BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson());
        EquipData Reloaded(uint id) => Reload().Equips.Single(e => e.Id == id);
        void Rejected(string expected, string name, params string[] args)
        {
            string before = roster.ToBsonDocument().ToJson();
            string message = RunRaw(args);
            AssertEqual(true, message.StartsWith("Rejected:", StringComparison.Ordinal) && message.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"{name}: '{message}' is a rejection mentioning '{expected}'");
            AssertEqual(before, roster.ToBsonDocument().ToJson(), $"{name} leaves state untouched");
            AssertEqual(false, harness.TryReadAvailablePacket($"{name} no push", out _), $"{name} pushes nothing");
        }
        void Own(uint id, int level) { if (roster.Characters.All(c => c.Id != id)) roster.AddCharacter(id, level); }
        EquipData Add(int templateId) => roster.AddEquip((uint)templateId) ?? throw new InvalidDataException($"template {templateId} not ownable");

        List<EquipTable> equipRows = TableReaderV2.Parse<EquipTable>().Where(Character.IsOwnableEquipTemplate).OrderBy(e => e.Id).ToList();
        Dictionary<int, EquipResonanceTable> resonance = TableReaderV2.Parse<EquipResonanceTable>().ToDictionary(r => r.Id);
        List<EquipBreakThroughTable> breaks = TableReaderV2.Parse<EquipBreakThroughTable>();
        int MaxBt(int templateId) => breaks.Where(r => r.EquipId == templateId).Max(r => r.Times);
        int Pool(List<int> pools, int slot) => pools.ElementAtOrDefault(slot - 1);
        List<AttribPoolTable> attribs = TableReaderV2.Parse<AttribPoolTable>();

        // ---- level / breakthrough / list / inspect (two weapons with different caps) ----
        List<EquipTable> attribWeapons = equipRows
            .Where(e => e.Site == 0 && e.WeaponSkillId > 0 && resonance.TryGetValue(e.Id, out var r) && Pool(r.AttribPoolId, 1) > 0 && MaxBt(e.Id) >= 2)
            .Take(2).ToList();
        EquipData wpA = Add(attribWeapons[0].Id), wpB = Add(attribWeapons[1].Id);
        Drain();
        int Limit(EquipData e, int bt) => breaks.Single(r => r.EquipId == e.TemplateId && r.Times == bt).LevelLimit;

        string listed = Run("list", "weapons");
        AssertEqual(true, listed.Contains($"#{wpA.Id} ", StringComparison.Ordinal) && listed.Contains($"#{wpB.Id} ", StringComparison.Ordinal), "list shows record ids");
        AssertEqual(true, Run("inspect", wpA.Id.ToString()).Contains($"Level 1/{Limit(wpA, 0)}", StringComparison.Ordinal), "inspect shows level and cap");
        Rejected("record id", "inspect unknown", "inspect", "99999");
        Rejected("list takes", "list bad filter", "list", "7");
        Drain();

        Run("level", wpA.Id.ToString(), "max");
        AssertEqual(Limit(wpA, 0), ReadPush("level push").EquipDataList.Single().Level, "level push carries cap level");
        Drain();
        AssertEqual(Limit(wpA, 0), Reloaded(wpA.Id).Level, "level survives reload");
        Run("level", wpB.Id.ToString(), "3");
        AssertEqual(3, ReadPush("level push B").EquipDataList.Single().Level, "second weapon level 3");
        Drain();
        AssertEqual(Limit(wpA, 0), wpA.Level, "other weapon untouched");
        Rejected("level must be", "level above cap", "level", wpB.Id.ToString(), (Limit(wpB, 0) + 1).ToString());
        Rejected("level must be", "level zero", "level", wpB.Id.ToString(), "0");
        Rejected("record id", "level unknown record", "level", "99999", "5");

        Run("breakthrough", wpA.Id.ToString(), "2");
        ReadPush("bt push");
        Drain();
        AssertEqual(2, Reloaded(wpA.Id).Breakthrough, "breakthrough survives reload");
        Run("level", wpA.Id.ToString(), "max");
        Drain();
        AssertEqual(Limit(wpA, 2), Reloaded(wpA.Id).Level, "level cap follows breakthrough");
        Run("breakthrough", wpA.Id.ToString(), "0");
        Drain();
        AssertEqual(Limit(wpA, 0), wpA.Level, "lowering breakthrough clamps level");
        Run("breakthrough", wpB.Id.ToString(), "max");
        Drain();
        AssertEqual(MaxBt((int)wpB.TemplateId), Reloaded(wpB.Id).Breakthrough, "breakthrough max");
        Rejected("breakthrough must be", "breakthrough above max", "breakthrough", wpB.Id.ToString(), (MaxBt((int)wpB.TemplateId) + 1).ToString());

        // 'all' / c<id> only touch worn equips and are atomic.
        AssertEqual(true, RunRaw("level", "all", "1").StartsWith("Rejected:", StringComparison.Ordinal), "all with nothing worn");

        // ---- resonance: weapon attribute ----
        int slotPool = Pool(resonance[(int)wpA.TemplateId].AttribPoolId, 1);
        List<int> inPool = attribs.Where(a => a.PoolId == slotPool).Select(a => a.Id).Distinct().ToList();
        int outOfPool = attribs.First(a => a.PoolId != slotPool).Id;
        Run("resonance", wpA.Id.ToString(), "1", "attrib", inPool[0].ToString());
        ResonanceInfo set = ReadPush("resonance push").EquipDataList.Single().ResonanceInfo.Single();
        Drain();
        AssertEqual((EquipResonanceType.Attrib, inPool[0]), (set.Type, set.TemplateId), "resonance pushed");
        Run("resonance", wpA.Id.ToString(), "1", "attrib", inPool[^1].ToString());
        ReadPush("resonance replace push");
        Drain();
        AssertEqual(inPool[^1], Reloaded(wpA.Id).ResonanceInfo.Single().TemplateId, "resonance replace survives reload");
        Rejected("not in this equip", "attrib outside pool", "resonance", wpA.Id.ToString(), "1", "attrib", outOfPool.ToString());
        Rejected("slot must be", "slot 4", "resonance", wpA.Id.ToString(), "4", "attrib", inPool[0].ToString());
        Rejected("needs a character", "weaponskill without character", "resonance", wpA.Id.ToString(), "1", "weaponskill", "101");
        Run("resonance", wpA.Id.ToString(), "1", "clear");
        ReadPush("resonance clear push");
        Drain();
        AssertEqual(0, Reloaded(wpA.Id).ResonanceInfo.Count, "clear survives reload");

        // ---- resonance: 6-star weapon skill ----
        EquipTable skillWeaponRow = equipRows.First(e => e.Site == 0 && e.WeaponSkillId > 0
            && resonance.TryGetValue(e.Id, out var r) && Pool(r.WeaponSkillPoolId, 1) > 0);
        int wsPool = Pool(resonance[skillWeaponRow.Id].WeaponSkillPoolId, 1);
        WeaponSkillPoolTable wsRow = TableReaderV2.Parse<WeaponSkillPoolTable>().First(r => r.PoolId == wsPool && r.SkillId.Count(s => s > 0) >= 2);
        int[] wsSkills = wsRow.SkillId.Where(s => s > 0).ToArray();
        Own((uint)wsRow.CharacterId, 80);
        EquipData skillWeapon = Add(skillWeaponRow.Id);
        Drain();
        Run("resonance", skillWeapon.Id.ToString(), "1", "weaponskill", wsSkills[0].ToString(), wsRow.CharacterId.ToString());
        ResonanceInfo ws = ReadPush("weaponskill push").EquipDataList.Single().ResonanceInfo.Single();
        Drain();
        AssertEqual((EquipResonanceType.WeaponSkill, wsSkills[0], wsRow.CharacterId), (ws.Type, ws.TemplateId, ws.CharacterId), "weapon skill resonance fields");
        Run("resonance", skillWeapon.Id.ToString(), "1", "weaponskill", wsSkills[1].ToString(), wsRow.CharacterId.ToString());
        ReadPush("weaponskill push 2");
        Drain();
        AssertEqual(wsSkills[1], Reloaded(skillWeapon.Id).ResonanceInfo.Single().TemplateId, "second weapon skill survives reload");
        int wrongSkill = TableReaderV2.Parse<WeaponSkillTable>().Select(s => s.Id).First(s => !wsRow.SkillId.Contains(s));
        Rejected("not in this weapon", "weapon skill outside pool", "resonance", skillWeapon.Id.ToString(), "1", "weaponskill", wrongSkill.ToString(), wsRow.CharacterId.ToString());
        Rejected("not owned", "weapon skill for unowned character", "resonance", skillWeapon.Id.ToString(), "1", "weaponskill", wsSkills[0].ToString(), "1011999");

        // ---- resonance: memory skill, bound character and the client cap ----
        List<CharacterSkillGroupTable> groups = TableReaderV2.Parse<CharacterSkillGroupTable>();
        List<CharacterSkillPoolTable> skillPools = TableReaderV2.Parse<CharacterSkillPoolTable>();
        List<CharacterSkillTable> charSkills = TableReaderV2.Parse<CharacterSkillTable>();
        EquipTable memoryRow = equipRows.First(e => e.Site > 0 && resonance.TryGetValue(e.Id, out var r) && Pool(r.CharacterSkillPoolId, 1) > 0 && Pool(r.AttribPoolId, 2) > 0);
        int memPool = Pool(resonance[memoryRow.Id].CharacterSkillPoolId, 1);
        // Two (character, pool skill) pairs whose resonance bonus lands on a base skill with a client effect cap.
        List<(CharacterSkillTable Char, int Pooled, int Base)> pairs = [];
        foreach (CharacterSkillTable cs in charSkills)
        {
            foreach (CharacterSkillPoolTable pooled in skillPools.Where(p => p.PoolId == memPool))
            {
                CharacterSkillGroupTable? g = cs.SkillGroupId.Select(id => groups.Find(x => x.Id == id)).FirstOrDefault(x => x?.SkillId.Contains(pooled.SkillId) == true);
                if (g is not null && Character.SkillEffectMaxLevel(pooled.SkillId) > 0 && Character.IsOwnableCharacter((uint)cs.CharacterId)
                    && !pairs.Any(p => p.Char.CharacterId == cs.CharacterId))
                    pairs.Add((cs, pooled.SkillId, pooled.SkillId));
            }
            if (pairs.Count == 2)
                break;
        }
        EquipData memory = Add(memoryRow.Id);
        foreach (var pair in pairs)
            Own((uint)pair.Char.CharacterId, 80);
        Drain();
        void SetSkill(int characterId, int skillId, int level) =>
            roster.Characters.Single(c => c.Id == characterId).SkillList.Single(s => s.Id == skillId).Level = level;
        var (charA, skillA, _) = pairs[0];
        var (charB, skillB, _) = pairs[1];
        // Boundary: base level == cap with the candidate applied is accepted, cap + 1 is rejected.
        int CapWith(EquipData candidate, int characterId, int skillId, List<ResonanceInfo> resonances, WeaponOverrunData overrun) =>
            Character.EffectiveSkillCap(
                [.. roster.Equips.Where(e => e.Id != candidate.Id), new EquipData { Id = candidate.Id, TemplateId = candidate.TemplateId, CharacterId = characterId, ResonanceInfo = resonances, WeaponOverrunData = overrun }],
                roster.Characters.Single(c => c.Id == characterId), skillId);
        int capA = CapWith(memory, charA.CharacterId, skillA, [new ResonanceInfo { Slot = 1, Type = EquipResonanceType.CharacterSkill, CharacterId = charA.CharacterId, TemplateId = skillA }], memory.WeaponOverrunData);
        SetSkill(charA.CharacterId, skillA, capA + 1);
        Rejected("allows at most", "skill resonance past the cap", "resonance", memory.Id.ToString(), "1", "skill", skillA.ToString(), charA.CharacterId.ToString());
        SetSkill(charA.CharacterId, skillA, capA);
        Run("resonance", memory.Id.ToString(), "1", "skill", skillA.ToString(), charA.CharacterId.ToString());
        ResonanceInfo skillResonance = ReadPush("memory skill push").EquipDataList.Single().ResonanceInfo.Single();
        Drain();
        AssertEqual((EquipResonanceType.CharacterSkill, skillA, charA.CharacterId), (skillResonance.Type, skillResonance.TemplateId, skillResonance.CharacterId), "memory skill resonance fields");
        AssertEqual(skillA, Reloaded(memory.Id).ResonanceInfo.Single().TemplateId, "memory skill survives reload");
        SetSkill(charB.CharacterId, skillB, 1);
        Run("resonance", memory.Id.ToString(), "1", "skill", skillB.ToString(), charB.CharacterId.ToString());
        ReadPush("memory skill push B");
        Drain();
        AssertEqual(charB.CharacterId, Reloaded(memory.Id).ResonanceInfo.Single().CharacterId, "second skill binds to the other character");
        Rejected("not in this memory", "skill of a different character", "resonance", memory.Id.ToString(), "1", "skill", skillA.ToString(), charB.CharacterId.ToString());
        Rejected("needs a character", "memory skill without character", "resonance", memory.Id.ToString(), "1", "skill", skillA.ToString());
        Rejected("slot must be", "memory slot 3", "resonance", memory.Id.ToString(), "3", "attrib", "1");
        Rejected("weapons only", "weapon skill on a memory", "resonance", memory.Id.ToString(), "1", "weaponskill", "101", charA.CharacterId.ToString());
        // memory attribute in slot 2 (worn by the bound character)
        int memAttribPool = Pool(resonance[memoryRow.Id].AttribPoolId, 2);
        int memAttrib = attribs.First(a => a.PoolId == memAttribPool).Id;
        Run("resonance", memory.Id.ToString(), "2", "attrib", memAttrib.ToString(), charB.CharacterId.ToString());
        ReadPush("memory attrib push");
        Drain();
        AssertEqual(2, Reloaded(memory.Id).ResonanceInfo.Count, "memory keeps both slots");

        // ---- worn equip: 'all' / c<id> ----
        wpA.CharacterId = charA.CharacterId;
        wpB.CharacterId = charA.CharacterId;
        Run("level", $"c{charA.CharacterId}", "1");
        List<uint> pushedIds = ReadPush("level c push").EquipDataList.Select(e => e.Id).Order().ToList();
        AssertEqual(string.Join(",", roster.Equips.Where(e => e.CharacterId == charA.CharacterId).Select(e => e.Id).Order()), string.Join(",", pushedIds), "c<id> touches exactly the worn equips (incl. the default weapon)");
        AssertEqual(true, pushedIds.Contains(wpA.Id) && pushedIds.Contains(wpB.Id), "c<id> covers both test weapons");
        Drain();
        AssertEqual(1, Reloaded(wpA.Id).Level, "worn weapon A level 1");
        Rejected("level must be", "all is atomic", "level", "all", "999");
        Rejected("not owned", "c<id> unowned", "level", "c1011999", "1");

        // ---- overrun: generic and character-specific levels ----
        List<WeaponOverrunTable> overruns = TableReaderV2.Parse<WeaponOverrunTable>();
        WeaponOverrunTable specificRow = overruns.First(r => r.CharacterId > 0 && r.UpSkillGroupId > 0);
        int specificMax = overruns.Where(r => r.WeaponId == specificRow.WeaponId && (r.CharacterId == 0 || r.CharacterId == specificRow.CharacterId)).Max(r => r.Level);
        WeaponOverrunTable genericRow = overruns.First(r => r.CharacterId == 0 && r.WeaponId != specificRow.WeaponId
            && !overruns.Any(o => o.WeaponId == r.WeaponId && o.CharacterId > 0) && Character.ResolveEquipTemplate((uint)r.WeaponId) is not null);
        Own((uint)specificRow.CharacterId, 80);
        EquipData generic = Add(genericRow.WeaponId), specific = Add(specificRow.WeaponId);
        Drain();
        Run("overrun", generic.Id.ToString(), "max");
        AssertEqual(1, ReadPush("overrun push").EquipDataList.Single().WeaponOverrunData.Level, "generic weapon overrun max is 1");
        Drain();
        AssertEqual(1, Reloaded(generic.Id).WeaponOverrunData.Level, "overrun survives reload");
        Rejected("overrun level must be", "generic weapon above max", "overrun", generic.Id.ToString(), "2");
        Rejected("overrun level must be", "specific weapon unworn above generic levels", "overrun", specific.Id.ToString(), "2");
        Rejected("not a weapon", "overrun on a memory", "overrun", memory.Id.ToString(), "1");
        Run("overrun", generic.Id.ToString(), "0");
        ReadPush("overrun clear push");
        Drain();
        AssertEqual(0, generic.WeaponOverrunData.Level, "overrun cleared");

        specific.CharacterId = specificRow.CharacterId;
        Run("overrun", specific.Id.ToString(), "2");
        ReadPush("specific overrun push");
        Drain();
        AssertEqual(2, Reloaded(specific.Id).WeaponOverrunData.Level, "worn specific weapon reaches level 2");
        int groupSkill = groups.Single(g => g.Id == specificRow.UpSkillGroupId).SkillId.First(s => s > 0);
        CharacterData specificChar = roster.Characters.Single(c => c.Id == specificRow.CharacterId);
        WeaponOverrunData target = new() { Level = specificRow.Level };
        int capOverrun = CapWith(specific, specificRow.CharacterId, groupSkill, specific.ResonanceInfo, target);
        specificChar.SkillList.Single(s => s.Id == groupSkill).Level = capOverrun + 1;
        Rejected("allows at most", "overrun past the client skill cap", "overrun", specific.Id.ToString(), specificRow.Level.ToString());
        specificChar.SkillList.Single(s => s.Id == groupSkill).Level = capOverrun;
        Run("overrun", specific.Id.ToString(), specificRow.Level.ToString());
        ReadPush("specific overrun at cap");
        Drain();
        AssertEqual(specificRow.Level, Reloaded(specific.Id).WeaponOverrunData.Level, "overrun at the exact cap is accepted");
        AssertEqual(true, specificMax >= specificRow.Level, "table max covers the tested level");

        // ---- overrun suit ----
        int suitId = equipRows.First(e => e.Quality == 6 && e.SuitId > 0).SuitId;
        Rejected("raise the overrun level", "suit with level 0", "overrun", generic.Id.ToString(), "suit", suitId.ToString());
        Run("overrun", specific.Id.ToString(), "suit", suitId.ToString());
        WeaponOverrunData suited = ReadPush("suit push").EquipDataList.Single().WeaponOverrunData;
        Drain();
        AssertEqual((true, suitId), (suited.ActiveSuits.Contains(suitId), suited.ChoseSuit), "suit activated and chosen");
        AssertEqual(suitId, Reloaded(specific.Id).WeaponOverrunData.ChoseSuit, "suit survives reload");
        Rejected("not a 6-star", "unknown suit", "overrun", specific.Id.ToString(), "suit", "1");
    }
}
