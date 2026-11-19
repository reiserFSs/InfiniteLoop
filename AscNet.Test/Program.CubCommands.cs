using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.partner;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // --cub-command-only: /cub list|add|level|quality|skill|max against the Partner* tables.
    private static void ValidateCubCommands()
    {
        const long playerId = 99_660;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Partners = [];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "cub-commands");

        string Run(params string[] args)
        {
            try { new PartnerCommand(harness.Session, args).Execute(); }
            catch (CommandMessageCallbackException ex) { return ex.Message; }
            throw new InvalidDataException($"/cub {string.Join(' ', args)}: expected command feedback.");
        }
        void Drain()
        {
            Thread.Sleep(150); // archive pushes trail the partner push through the async writer
            while (harness.TryReadAvailablePacket("cub drain", out _)) { }
        }
        NotifyPartnerDataList ReadPartnerPush(string name) =>
            ReadPushPayload<NotifyPartnerDataList>(harness, nameof(NotifyPartnerDataList), name, maxPacketsToRead: 4);
        Character Reload() => BsonSerializer.Deserialize<Character>(harness.Session.character.ToBson());
        PartnerData Stored(int recordId) => roster.Partners!.Single(p => p.Id == recordId);
        PartnerData Reloaded(int recordId) => Reload().Partners!.Single(p => p.Id == recordId);
        void Rejected(string expected, string name, params string[] args)
        {
            string before = roster.ToBsonDocument().ToJson();
            string message = Run(args);
            AssertEqual(true, message.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{name}: '{message}' mentions '{expected}'");
            AssertEqual(before, roster.ToBsonDocument().ToJson(), $"{name} leaves state untouched");
            AssertEqual(false, harness.TryReadAvailablePacket($"{name} no push", out _), $"{name} pushes nothing");
        }

        HashSet<int> skillTemplates = TableReaderV2.Parse<PartnerSkillTable>().Select(row => row.PartnerId).ToHashSet();
        List<int> templates = TableReaderV2.Parse<PartnerTable>().Select(row => row.Id).Where(skillTemplates.Contains).Order().ToList();
        int templateA = templates[0], templateB = templates[1];
        Drain();

        // ---- add ----
        AssertEqual(true, Run("list").Contains("No partners", StringComparison.Ordinal), "empty list");
        AssertEqual(true, Run("list", "templates").Contains(templateA.ToString(), StringComparison.Ordinal), "templates list");
        Drain();
        Rejected("Unknown partner template", "add unknown template", "add", "7");
        Rejected("Usage", "add without target", "add");
        Rejected("Usage", "add with extra", "add", templateA.ToString(), "1");
        Run("add", templateA.ToString());
        NotifyPartnerDataList addPush = ReadPartnerPush("add push");
        AssertEqual(true, addPush.OperateTypes.SequenceEqual([1]) && addPush.PartnerDataList.Single().TemplateId == templateA, "add push is an add of the template");
        Drain();
        Run("add", templateB.ToString());
        NotifyPartnerDataList addPushB = ReadPartnerPush("add push B");
        AssertEqual(2, addPushB.PartnerDataList.Single().Id, "second record id");
        Drain();
        AssertEqual(templateB, Reloaded(2).TemplateId, "add survives reload");
        string listed = Run("list");
        AssertEqual(true, listed.Contains($"1: {templateA}", StringComparison.Ordinal) && listed.Contains($"2: {templateB}", StringComparison.Ordinal), "list shows owned partners");
        int ownedBefore = roster.Partners!.Count;
        Run("add", "all");
        NotifyPartnerDataList allPush = ReadPartnerPush("add all push");
        AssertEqual(templates.Count - 2, allPush.PartnerDataList.Count, "add all skips owned templates");
        AssertEqual(templates.Count, roster.Partners!.Count, "one partner per template after add all");
        AssertEqual(true, allPush.OperateTypes.All(op => op == 1), "add all push operate types");
        Drain();
        Rejected("already own", "add all again", "add", "all");
        Run("add", templateA.ToString());
        Drain();
        AssertEqual(2, roster.Partners.Count(p => p.TemplateId == templateA), "single add may duplicate a template");
        PartnerData a = Stored(1), b = Stored(2);

        // ---- level ----
        PartnerBreakThroughTable bt0 = TableReaderV2.Parse<PartnerBreakThroughTable>().Single(r => r.PartnerId == templateA && r.BreakTimes == 0);
        PartnerBreakThroughTable bt1 = TableReaderV2.Parse<PartnerBreakThroughTable>().Single(r => r.PartnerId == templateA && r.BreakTimes == 1);
        Run("level", "1", "5");
        NotifyPartnerDataList levelPush = ReadPartnerPush("level push");
        AssertEqual(true, levelPush.OperateTypes.SequenceEqual([2]) && levelPush.PartnerDataList.Single().Level == 5, "level push is an update");
        Drain();
        AssertEqual(5, Reloaded(1).Level, "level 5 survives reload");
        Run("level", "1", bt1.LevelLimit.ToString(), "1");
        Drain();
        AssertEqual(true, a.Level == bt1.LevelLimit && a.BreakThrough == 1 && a.Exp == 0, "level at overclock 1");
        AssertEqual(bt1.LevelLimit, Reloaded(1).Level, "overclock level survives reload");
        Rejected("Level must be", "level zero", "level", "1", "0");
        Rejected("Level must be", "level above limit", "level", "1", (bt1.LevelLimit + 1).ToString());
        Rejected("Overclock 9", "unknown overclock", "level", "1", "1", "9");
        Rejected("not found", "unknown record", "level", "9999", "5");
        Rejected("Usage", "level missing value", "level", "1");
        Rejected("Usage", "level all", "level", "all", "5");
        Run("level", "1", "1", "0");
        Drain();
        AssertEqual(true, a.Level == 1 && a.BreakThrough == 0, "overclock reset by explicit 0");
        _ = bt0;

        // ---- quality ----
        List<PartnerQualityTable> qualities = TableReaderV2.Parse<PartnerQualityTable>().Where(r => r.PartnerId == templateB).OrderBy(r => r.Quality).ToList();
        PartnerQualityTable q1 = qualities[1], q2 = qualities[2];
        foreach (PartnerSkillData passive in b.SkillList.Where(s => s.Type == 2))
            passive.IsWear = true;
        Run("quality", "2", q1.Quality.ToString());
        NotifyPartnerDataList qualityPush = ReadPartnerPush("quality push");
        Drain();
        AssertEqual(q1.Quality, qualityPush.PartnerDataList.Single().Quality, "quality push value");
        AssertEqual(qualities[0].StarCostChipCount.Last(v => v > 0), b.StarSchedule, "stars start at the previous quality's full count");
        AssertEqual(q1.SkillColumnCount, b.SkillList.Count(s => s.Type == 2 && s.IsWear), "worn passives trimmed to the quality's skill columns");
        AssertEqual(q1.Quality, Reloaded(2).Quality, "quality survives reload");
        Run("quality", "2", q2.Quality.ToString());
        Drain();
        AssertEqual(q1.StarCostChipCount.Last(v => v > 0), b.StarSchedule, "second quality stars");
        Run("quality", "2", qualities[0].Quality.ToString());
        Drain();
        AssertEqual(0, b.StarSchedule, "lowest quality has no stars");
        Rejected("Quality must be", "unknown quality", "quality", "2", "99");
        Rejected("Quality must be", "quality below the table", "quality", "2", "0");
        Rejected("Usage", "quality with extra", "quality", "2", "3", "1");

        // ---- skill ----
        PartnerSkillData main = a.SkillList.Single(s => s.Type == 1);
        PartnerSkillData passiveSkill = a.SkillList.First(s => s.Type == 2);
        int mainMax = TableReaderV2.Parse<PartnerSkillEffectTable>().Where(r => r.SkillId == main.Id).Max(r => r.Level);
        int passiveMax = TableReaderV2.Parse<PartnerSkillEffectTable>().Where(r => r.SkillId == passiveSkill.Id).Max(r => r.Level);
        Run("skill", "1", main.Id.ToString(), "3");
        NotifyPartnerDataList skillPush = ReadPartnerPush("skill push");
        Drain();
        AssertEqual(3, skillPush.PartnerDataList.Single().SkillList.Single(s => s.Type == 1).Level, "skill push value");
        Run("skill", "1", passiveSkill.Id.ToString(), passiveMax.ToString());
        Drain();
        AssertEqual(passiveMax, Reloaded(1).SkillList.First(s => s.Type == 2).Level, "passive max survives reload");
        AssertEqual(3, Reloaded(1).SkillList.Single(s => s.Type == 1).Level, "main level survives reload");
        Rejected("Skill level must be", "skill zero", "skill", "1", main.Id.ToString(), "0");
        Rejected("Skill level must be", "skill above table max", "skill", "1", passiveSkill.Id.ToString(), (passiveMax + 1).ToString());
        Rejected("no skill", "foreign skill", "skill", "1", "1", "1");
        Rejected("Usage", "skill missing level", "skill", "1", main.Id.ToString());
        _ = mainMax;

        // ---- max ----
        Run("max", "1");
        NotifyPartnerDataList maxPush = ReadPartnerPush("max push");
        Drain();
        PartnerQualityTable topQuality = TableReaderV2.Parse<PartnerQualityTable>().Where(r => r.PartnerId == templateA).OrderBy(r => r.Quality).Last();
        PartnerBreakThroughTable topBreak = TableReaderV2.Parse<PartnerBreakThroughTable>().Where(r => r.PartnerId == templateA).OrderBy(r => r.BreakTimes).Last();
        void AssertMaxed(PartnerData p, int template, string label)
        {
            PartnerQualityTable tq = TableReaderV2.Parse<PartnerQualityTable>().Where(r => r.PartnerId == template).OrderBy(r => r.Quality).Last();
            PartnerBreakThroughTable tb = TableReaderV2.Parse<PartnerBreakThroughTable>().Where(r => r.PartnerId == template).OrderBy(r => r.BreakTimes).Last();
            int fullStars = TableReaderV2.Parse<PartnerQualityTable>().Where(r => r.PartnerId == template).OrderBy(r => r.Quality)
                .SelectMany(r => r.StarCostChipCount).Last(v => v > 0);
            AssertEqual(true, p.Quality == tq.Quality && p.StarSchedule == fullStars
                && p.BreakThrough == tb.BreakTimes && p.Level == tb.LevelLimit && p.Exp == 0, $"{label} quality/stars/overclock/level");
            AssertEqual(true, p.SkillList.All(s => s.Level == TableReaderV2.Parse<PartnerSkillEffectTable>().Where(r => r.SkillId == s.Id).Max(r => r.Level)), $"{label} skills at table max");
        }
        AssertMaxed(Reloaded(1), templateA, "max single");
        AssertEqual(true, maxPush.PartnerDataList.Single().Quality == topQuality.Quality && maxPush.PartnerDataList.Single().Level == topBreak.LevelLimit, "max push value");
        AssertEqual(false, Reloaded(2).Level == topBreak.LevelLimit && Reloaded(2).Quality == topQuality.Quality, "max single leaves other partners");
        Run("max", "all");
        NotifyPartnerDataList maxAllPush = ReadPartnerPush("max all push");
        Drain();
        AssertEqual(roster.Partners.Count, maxAllPush.PartnerDataList.Count, "max all pushes every partner");
        foreach (PartnerData p in Reload().Partners!)
            AssertMaxed(p, p.TemplateId, $"max all record {p.Id}");
        Rejected("not found", "max unknown record", "max", "9999");
        Rejected("Usage", "max templates", "max", "templates");
        _ = ownedBefore;
    }
}
