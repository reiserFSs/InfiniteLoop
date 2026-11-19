using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.partner;

namespace AscNet.GameServer.Commands
{
    [CommandName("cub")]
    internal sealed class PartnerCommand : Command
    {
        public PartnerCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help =>
            "Command to modify partners (cubs). Forms: /cub list [templates] | /cub add <templateId|all> | " +
            "/cub level <recordId> <level> [overclock] | /cub quality <recordId> <quality> | " +
            "/cub skill <recordId> <skillId> <level> | /cub max <recordId|all>. " +
            "recordId is the number shown by /cub list.";

        [Argument(0, @"^(list|add|level|quality|skill|max)$", "The operation selected (list, add, level, quality, skill, max)", ArgumentFlags.IgnoreCase)]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^([0-9]+|all|templates)$", "Partner record id, template id, 'all' (add/max) or 'templates' (list)", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Target { get; set; } = string.Empty;

        [Argument(2, @"^[0-9]+$", "Level, quality or skill id", ArgumentFlags.Optional)]
        string First { get; set; } = string.Empty;

        [Argument(3, @"^[0-9]+$", "Skill level or overclock count", ArgumentFlags.Optional)]
        string Second { get; set; } = string.Empty;

        const string Usage = "Usage: /cub list [templates] | /cub add <templateId|all> | /cub level <recordId> <level> [overclock] | " +
            "/cub quality <recordId> <quality> | /cub skill <recordId> <skillId> <level> | /cub max <recordId|all>";

        static CommandMessageCallbackException Fail(string message) => new(message);

        public override void Execute()
        {
            session.character.Partners ??= [];
            Target = Target.ToLowerInvariant();
            switch (Op.ToLowerInvariant())
            {
                case "list":
                    if (First != "" || Second != "" || Target is not ("" or "templates"))
                        throw Fail(Usage);
                    throw Fail(Target == "templates" ? ListTemplates() : ListOwned());
                case "add":
                    if (First != "" || Second != "" || Target == "" || Target == "templates")
                        throw Fail(Usage);
                    Add();
                    return;
                case "level":
                    Require(First != "" && Target is not ("" or "all" or "templates"));
                    Apply(partner => SetLevel(partner, Num(First), Second == "" ? null : Num(Second)));
                    return;
                case "quality":
                    Require(First != "" && Second == "" && Target is not ("" or "all" or "templates"));
                    Apply(partner => SetQuality(partner, Num(First)));
                    return;
                case "skill":
                    Require(First != "" && Second != "" && Target is not ("" or "all" or "templates"));
                    Apply(partner => SetSkill(partner, Num(First), Num(Second)));
                    return;
                default: // max
                    Require(First == "" && Second == "" && Target is not ("" or "templates"));
                    Apply(MaxOut, Target == "all");
                    return;
            }
        }

        static void Require(bool ok)
        {
            if (!ok)
                throw Fail(Usage);
        }

        static int Num(string value) =>
            int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                ? n : throw Fail("Number out of range: " + value);

        // ---- listing ----

        static string Name(int templateId) =>
            TableReaderV2.Parse<PartnerTable>().Find(row => row.Id == templateId)?.Name ?? "?";

        string ListOwned()
        {
            if (session.character.Partners!.Count == 0)
                return "No partners owned. Use /cub list templates, then /cub add <templateId>.";
            return "Partners (record: template name, level/limit, overclock, quality, stars, skills main|passives): " +
                string.Join("; ", session.character.Partners.OrderBy(p => p.Id).Select(p =>
                {
                    int limit = BreakThrough(p.TemplateId, p.BreakThrough)?.LevelLimit ?? 0;
                    string main = p.SkillList.Where(s => s.Type == 1).Select(s => s.Level.ToString()).FirstOrDefault() ?? "-";
                    string passives = string.Join(",", p.SkillList.Where(s => s.Type == 2).Select(s => s.Level));
                    return $"{p.Id}: {p.TemplateId} {Name(p.TemplateId)}, Lv{p.Level}/{limit}, OC{p.BreakThrough}, Q{p.Quality}, S{p.StarSchedule}, {main}|{passives}";
                }));
        }

        static string ListTemplates() =>
            "Partner templates: " + string.Join("; ", TableReaderV2.Parse<PartnerTable>()
                .Where(row => TableReaderV2.Parse<PartnerSkillTable>().Any(skill => skill.PartnerId == row.Id))
                .Select(row => $"{row.Id} {row.Name}"));

        // ---- add ----

        void Add()
        {
            HashSet<int> addable = TableReaderV2.Parse<PartnerTable>().Select(row => row.Id)
                .Intersect(TableReaderV2.Parse<PartnerSkillTable>().Select(row => row.PartnerId))
                .ToHashSet();
            List<int> templates;
            if (Target == "all")
            {
                HashSet<int> owned = session.character.Partners!.Select(p => p.TemplateId).ToHashSet();
                templates = addable.Where(id => !owned.Contains(id)).Order().ToList();
                if (templates.Count == 0)
                    throw Fail("You already own every partner template.");
            }
            else
            {
                int id = Num(Target);
                if (!addable.Contains(id))
                    throw Fail($"Unknown partner template {id}. Use /cub list templates.");
                templates = [id];
            }

            List<PartnerData> added = [];
            foreach (int templateId in templates)
            {
                PartnerData partner = PartnerModule.CreatePartner(PartnerModule.AllocatePartnerId(session.character), templateId);
                session.character.Partners!.Add(partner);
                added.Add(partner);
            }
            session.character.SaveChecked();
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = added,
                OperateTypes = Enumerable.Repeat(1, added.Count).ToList()
            });
            PartnerModule.SyncArchive(session);
            throw Fail($"Added {added.Count} partner(s): {string.Join(", ", added.Select(p => $"{p.Id}={Name(p.TemplateId)}"))}.");
        }

        // ---- table lookups ----

        static PartnerBreakThroughTable? BreakThrough(int templateId, int breakTimes) =>
            TableReaderV2.Parse<PartnerBreakThroughTable>().Find(row => row.PartnerId == templateId && row.BreakTimes == breakTimes);

        static List<PartnerQualityTable> Qualities(int templateId) =>
            TableReaderV2.Parse<PartnerQualityTable>().Where(row => row.PartnerId == templateId).OrderBy(row => row.Quality).ToList();

        static int SkillMax(int skillId) =>
            TableReaderV2.Parse<PartnerSkillEffectTable>().Where(row => row.SkillId == skillId).Select(row => row.Level).DefaultIfEmpty(0).Max();

        // Cumulative chip count that fills the quality (the top quality has no star costs: it keeps the previous count).
        static int FullStars(List<PartnerQualityTable> rows, int quality) =>
            rows.Where(r => r.Quality <= quality).Select(r => r.StarCostChipCount.LastOrDefault(v => v > 0)).LastOrDefault(v => v > 0);

        // ---- mutations (each returns the feedback text) ----

        void Apply(Func<PartnerData, string> mutate, bool all = false)
        {
            List<PartnerData> targets;
            if (all)
            {
                targets = session.character.Partners!.ToList();
                if (targets.Count == 0)
                    throw Fail("No partners owned.");
            }
            else
            {
                int recordId = Num(Target);
                targets = session.character.Partners!.Where(p => p.Id == recordId).ToList();
                if (targets.Count == 0)
                    throw Fail($"Partner record {recordId} not found. Use /cub list.");
            }

            // Only a malformed partner table can fail midway; nothing is saved or pushed in that case.
            List<string> messages = [];
            foreach (PartnerData partner in targets)
                messages.Add(mutate(partner));
            session.character.SaveChecked();
            session.SendPush(new NotifyPartnerDataList
            {
                PartnerDataList = targets,
                OperateTypes = Enumerable.Repeat(2, targets.Count).ToList()
            });
            PartnerModule.SyncArchive(session);
            throw Fail(all ? $"Maxed {targets.Count} partner(s)." : messages[0]);
        }

        static string SetLevel(PartnerData partner, int level, int? breakTimes)
        {
            int bt = breakTimes ?? partner.BreakThrough;
            PartnerBreakThroughTable row = BreakThrough(partner.TemplateId, bt)
                ?? throw Fail($"Overclock {bt} does not exist for partner {partner.TemplateId} ({string.Join(", ", TableReaderV2.Parse<PartnerBreakThroughTable>().Where(r => r.PartnerId == partner.TemplateId).Select(r => r.BreakTimes))}).");
            if (level < 1 || level > row.LevelLimit || !PartnerModule.GetLevelTemplate(row.LevelUpTemplateId).Any(l => l.Level == level))
                throw Fail($"Level must be 1-{row.LevelLimit} at overclock {bt}.");
            partner.BreakThrough = bt;
            partner.Level = level;
            partner.Exp = 0;
            return $"Partner {partner.Id}: level {level}/{row.LevelLimit}, overclock {bt}.";
        }

        static string SetQuality(PartnerData partner, int quality)
        {
            List<PartnerQualityTable> rows = Qualities(partner.TemplateId);
            PartnerQualityTable row = rows.Find(r => r.Quality == quality)
                ?? throw Fail($"Quality must be one of {string.Join(", ", rows.Select(r => r.Quality))} for partner {partner.TemplateId}.");
            ApplyQuality(partner, rows, row, atFullStars: false);
            return $"Partner {partner.Id}: quality {quality}, stars {partner.StarSchedule}.";
        }

        // Evolution keeps the cumulative chip count (PartnerEvolutionRequest), so a quality starts at the previous quality's full count.
        static void ApplyQuality(PartnerData partner, List<PartnerQualityTable> rows, PartnerQualityTable row, bool atFullStars)
        {
            partner.Quality = row.Quality;
            partner.StarSchedule = FullStars(rows, atFullStars ? row.Quality : row.Quality - 1);
            // Passive wear slots are limited by the quality's SkillColumnCount (PartnerSkillWearRequest).
            int excess = partner.SkillList.Count(s => s.Type == 2 && s.IsWear) - row.SkillColumnCount;
            foreach (PartnerSkillData skill in Enumerable.Reverse(partner.SkillList).Where(s => s.Type == 2 && s.IsWear).Take(Math.Max(0, excess)))
                skill.IsWear = false;
        }

        static string SetSkill(PartnerData partner, int skillId, int level)
        {
            PartnerSkillData? skill = partner.SkillList.Find(s => s.Id == skillId);
            if (skill is null)
            {
                // Main skills change id with the carrier element; accept any sibling of the partner's current main skill.
                PartnerSkillData? main = partner.SkillList.Find(s => s.Type == 1);
                if (main is not null && TableReaderV2.Parse<PartnerMainSkillGroupTable>().Any(group =>
                        partner.UnlockSkillGroup.Contains(group.Id) && group.SkillId.Contains(skillId) && group.SkillId.Contains(main.Id)))
                    skill = main;
            }
            if (skill is null)
                throw Fail($"Partner {partner.Id} has no skill {skillId} (has {string.Join(", ", partner.SkillList.Select(s => s.Id))}).");
            int max = SkillMax(skill.Id);
            if (max == 0 || level < 1 || level > max)
                throw Fail($"Skill level must be 1-{max}.");
            skill.Level = level;
            return $"Partner {partner.Id}: skill {skill.Id} level {level}/{max}.";
        }

        static string MaxOut(PartnerData partner)
        {
            List<PartnerQualityTable> rows = Qualities(partner.TemplateId);
            PartnerBreakThroughTable? top = TableReaderV2.Parse<PartnerBreakThroughTable>()
                .Where(r => r.PartnerId == partner.TemplateId).OrderBy(r => r.BreakTimes).LastOrDefault();
            if (rows.Count == 0 || top is null || partner.SkillList.Any(s => SkillMax(s.Id) == 0))
                throw Fail($"Partner {partner.Id} ({partner.TemplateId}) has incomplete partner tables.");
            ApplyQuality(partner, rows, rows[^1], atFullStars: true);
            partner.BreakThrough = top.BreakTimes;
            partner.Level = top.LevelLimit;
            partner.Exp = 0;
            foreach (PartnerSkillData skill in partner.SkillList)
                skill.Level = SkillMax(skill.Id);
            return $"Partner {partner.Id} maxed.";
        }
    }
}
