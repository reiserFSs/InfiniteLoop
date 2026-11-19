using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.teamrecommend;
using AscNet.Table.V2.share.trust;

namespace AscNet.GameServer.Commands
{
    [CommandName("character")]
    internal class CharacterCommand : Command
    {
        public CharacterCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        const string Usage = "Usage: /character add <id|all> | /character max <id|all> [gear] | " +
            "/character set <id> level|grade|quality|star|awaken|trust <value> | /character set <id> skill <skillId> <level>";

        public override string Help =>
            "Command to modify characters. Forms: /character add <id|all> | /character max <id|all> [gear] | " +
            "/character set <id> level|grade|quality|star|awaken|trust <value> | /character set <id> skill <skillId> <level>. " +
            "max raises level, quality, grade, trust, liberation, awakening and every skill to the table maximum " +
            "(skills never above the client battle cap); 'gear' also wears the recommended weapon and memories. " +
            "All values are validated against the game tables.";

        [Argument(0, @"^(add|max|set)$", "The operation selected (add, max, set)", ArgumentFlags.IgnoreCase)]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^[0-9]+$|^all$", "The target character, value is character id or 'all'", ArgumentFlags.IgnoreCase)]
        string Target { get; set; } = string.Empty;

        [Argument(2, @"^(gear|level|grade|quality|star|awaken|trust|skill)$", "max: 'gear'; set: level, grade, quality, star, awaken, trust or skill", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Field { get; set; } = string.Empty;

        [Argument(3, @"^[0-9]+$", "set: the new value, or the skill id for 'skill'", ArgumentFlags.Optional)]
        string First { get; set; } = string.Empty;

        [Argument(4, @"^[0-9]+$", "set skill: the new base level", ArgumentFlags.Optional)]
        string Second { get; set; } = string.Empty;

        static CommandMessageCallbackException Fail(string message) => new(message);

        public override void Execute()
        {
            Target = Target.ToLowerInvariant();
            switch (Op.ToLowerInvariant())
            {
                case "add":
                    if (Field != "" || First != "" || Second != "")
                        throw Fail(Usage);
                    Add();
                    break;
                case "max":
                    if (!Field.Equals("gear", StringComparison.OrdinalIgnoreCase) && Field != "" || First != "" || Second != "")
                        throw Fail(Usage);
                    Max(Field != "");
                    break;
                case "set":
                    Set();
                    break;
                default:
                    throw new InvalidOperationException("Invalid operation!");
            }
        }

        void Add()
        {
            int id = Miscs.ParseIntOr(Target);
            RewardApplicationResult result;
            if (Target == "all")
            {
                HashSet<uint> ownedCharacterIds = session.character.Characters
                    .Select(character => character.Id)
                    .ToHashSet();
                IEnumerable<Reward> rewards = TableReaderV2.Parse<CharacterTable>()
                    .Where(character => Character.IsOwnableCharacter((uint)character.Id)
                        && !ownedCharacterIds.Contains((uint)character.Id))
                    .Select(character => new Reward { Id = character.Id, Type = RewardType.Character });

                result = RewardHandler.ApplyRewards(rewards, session);
            }
            else
            {
                result = RewardHandler.ApplyRewards([ new Reward() { Id = id, Type = RewardType.Character } ], session);
            }
            session.inventory.SaveChecked();
            session.character.SaveChecked();
            if (result.DormFurnitureChanged || result.GatherRewardIds.Count > 0 || result.HeadPortraitData.Heads.Count > 0)
                session.player.SaveChecked();
            result.SendPushes(session);
        }

        // ---- table limits (authoritative: Resources/table/share) ----

        static CharacterTable Template(CharacterData character) =>
            TableReaderV2.Parse<CharacterTable>().Find(row => row.Id == character.Id)
                ?? throw Fail($"Character {character.Id} is not in the character table.");

        static int MaxLevel(CharacterData character) =>
            Character.characterLevelUpTemplates
                .Where(row => row.Type == Template(character).LevelUpTemplateId)
                .Select(row => row.Level)
                .DefaultIfEmpty()
                .Max();

        static List<int> Grades(CharacterData character) =>
            TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == character.Id)
                .Select(row => row.Grade).Order().ToList();

        static List<int> Qualities(CharacterData character) =>
            TableReaderV2.Parse<CharacterQualityTable>().Where(row => row.CharacterId == character.Id)
                .Select(row => row.Quality).Order().ToList();

        // CharacterActivateStarRequest: Star counts up to StarUseCount.Count of the (Type, Quality) fragment row.
        static int MaxStar(CharacterData character, int quality) =>
            TableReaderV2.Parse<CharacterQualityFragmentTable>()
                .Find(row => row.Type == Template(character).Type && row.Quality == quality)?.StarUseCount.Count ?? 0;

        static List<int> TrustLevels(CharacterData character) =>
            TableReaderV2.Parse<CharacterTrustExpTable>().Where(row => row.CharacterId == character.Id)
                .Select(row => row.TrustLv).Order().ToList();

        IReadOnlyDictionary<int, int> SkillPlus(CharacterData character) =>
            FightModule.BuildCharacterSkillPlus(session, character);

        int SkillCap(CharacterData character, int skillId) =>
            Character.EffectiveSkillCap(session.character.Equips, character, skillId, SkillPlus(character));

        IEnumerable<EnhanceSkillGroupTable> EnhanceGroups(CharacterData character)
        {
            EnhanceSkillTable? row = TableReaderV2.Parse<EnhanceSkillTable>().Find(candidate => candidate.CharacterId == character.Id);
            return row is null ? [] : row.SkillGroupId.Where(id => id > 0).Distinct()
                .Select(id => TableReaderV2.Parse<EnhanceSkillGroupTable>().Find(group => group.Id == id))
                .OfType<EnhanceSkillGroupTable>().Where(group => group.SkillId.Any(skill => skill > 0));
        }

        // ---- set ----

        void Set()
        {
            if (Target == "all" || Field == "" || Field.Equals("gear", StringComparison.OrdinalIgnoreCase) || First == "")
                throw Fail(Usage);
            string field = Field.ToLowerInvariant();
            if ((field == "skill") != (Second != ""))
                throw Fail(Usage);
            CharacterData character = session.character.Characters.Find(row => row.Id == (uint)Miscs.ParseIntOr(Target))
                ?? throw Fail($"Character {Target} is not owned. Use /character add {Target} first.");
            int value = Miscs.ParseIntOr(First, -1);
            if (value < 0)
                throw Fail($"Invalid value {First}.");

            // Awaken's Enhance skill list, skills and the gate for new groups are validated in SetAwaken.
            switch (field)
            {
                case "level":
                    int maxLevel = MaxLevel(character);
                    if (value < 1 || value > maxLevel)
                        throw Fail($"Level must be 1-{maxLevel}.");
                    character.Level = value;
                    character.Exp = 0;
                    break;
                case "grade":
                    List<int> grades = Grades(character);
                    if (!grades.Contains(value))
                        throw Fail($"Grade must be one of {string.Join(",", grades)}.");
                    character.Grade = value;
                    break;
                case "quality":
                    List<int> qualities = Qualities(character);
                    if (!qualities.Contains(value))
                        throw Fail($"Quality must be one of {string.Join(",", qualities)}.");
                    character.Quality = value;
                    character.Star = 0;
                    session.character.UnlockQualityGatedSkills(character, session.player.GatherRewards);
                    break;
                case "star":
                    int maxStar = MaxStar(character, character.Quality);
                    if (value > maxStar)
                        throw Fail($"Star must be 0-{maxStar} at quality {character.Quality}.");
                    character.Star = value;
                    break;
                case "trust":
                    List<int> trust = TrustLevels(character);
                    if (!trust.Contains(value))
                        throw Fail($"Trust level must be {trust.Min()}-{trust.Max()}.");
                    character.TrustLv = value;
                    character.TrustExp = 0;
                    break;
                case "awaken":
                    SetAwaken(character, value);
                    break;
                case "skill":
                    SetSkill(character, value, Miscs.ParseIntOr(Second, -1));
                    break;
                default:
                    throw Fail(Usage);
            }

            SaveAndPush([character], [], [], false);
        }

        void SetSkill(CharacterData character, int skillId, int level)
        {
            CharacterSkill skill = character.SkillList.Find(row => row.Id == (uint)skillId)
                ?? throw Fail($"Character {character.Id} has no unlocked skill {skillId}. Skills: " +
                    string.Join(",", character.SkillList.Select(row => row.Id)));
            int cap = SkillCap(character, skillId);
            if (level < 1 || level > cap)
                throw Fail($"Skill {skillId} level must be 1-{Math.Max(cap, 0)} (battle cap {Character.SkillEffectMaxLevel(skillId)} " +
                    $"minus resonance, overrun and Border Pact levels, upgrade terminal {Character.SkillUpgradeTerminalLevel(skillId)}).");
            skill.Level = level;
        }

        // Enhance (awakening) groups already unlocked, plus groups whose unlock condition is met.
        void SetAwaken(CharacterData character, int? requested)
        {
            List<(EnhanceSkillGroupTable Group, int SkillId)> targets = [];
            foreach (EnhanceSkillGroupTable group in EnhanceGroups(character))
            {
                CharacterSkill? current = character.EnhanceSkillList.Find(row => group.SkillId.Contains((int)row.Id));
                int skillId = current is null ? group.SkillId.First(id => id > 0) : (int)current.Id;
                List<EnhanceSkillUpgradeTable> rows = Character.OrderedEnhanceSkillUpgrades(skillId);
                if (current is null && (rows.Count == 0 || !Character.MeetsCharacterSkillCondition(character,
                        rows[0].ConditionId, session.player.GatherRewards, session.player.PlayerData.Level)))
                    continue;
                targets.Add((group, skillId));
            }
            int limit = targets.Min(target => Character.EnhanceSkillMaxLevel(target.SkillId));
            int level = requested ?? limit;
            if (level < 1 || level > limit)
                throw Fail($"Awaken level must be 1-{limit}.");
            foreach ((EnhanceSkillGroupTable group, int skillId) in targets)
            {
                CharacterSkill? current = character.EnhanceSkillList.Find(row => group.SkillId.Contains((int)row.Id));
                if (current is null)
                    character.EnhanceSkillList.Add(new CharacterSkill { Id = (uint)skillId, Level = level });
                else
                    current.Level = level;
            }
        }

        // ---- max ----

        void Max(bool gear)
        {
            List<CharacterData> targets = Target == "all"
                ? session.character.Characters.ToList()
                : [session.character.Characters.Find(row => row.Id == (uint)Miscs.ParseIntOr(Target))
                    ?? throw Fail($"Character {Target} is not owned. Use /character add {Target} first.")];
            List<EquipData> changedEquips = [];
            List<int> newGatherIds = [];
            List<string> notes = [];
            foreach (CharacterData character in targets)
            {
                MaxProgress(character, newGatherIds);
                if (gear)
                    notes.Add(WearRecommendedGear(character, changedEquips));
                MaxSkills(character);
            }
            SaveAndPush(targets, changedEquips, newGatherIds, newGatherIds.Count > 0);
            if (gear)
                throw Fail(string.Join(" ", notes));
        }

        void MaxProgress(CharacterData character, List<int> newGatherIds)
        {
            character.Level = MaxLevel(character);
            character.Exp = 0;
            character.Quality = Qualities(character).Max();
            character.Star = MaxStar(character, character.Quality);
            character.Grade = Grades(character).Max();
            List<int> trust = TrustLevels(character);
            character.TrustLv = trust.Max();
            character.TrustExp = 0;

            // Liberation is the claimed Exhibition milestones (conditions read GatherRewards, not LiberateLv);
            // the milestone reward goods are not granted.
            foreach (ExhibitionRewardTable reward in TableReaderV2.Parse<ExhibitionRewardTable>()
                         .Where(row => row.CharacterId == character.Id))
            {
                if (session.player.AddGatherReward(reward.Id))
                    newGatherIds.Add(reward.Id);
                character.LiberateLv = Math.Max(character.LiberateLv, reward.LevelId);
            }
            Character.EnsureLiberateBallMagic(character);

            session.character.UnlockQualityGatedSkills(character, session.player.GatherRewards);
            UnlockEligibleSkillGroups(character);
            if (EnhanceGroups(character).Any())
            {
                try { SetAwaken(character, null); }
                catch (CommandMessageCallbackException) { /* no group unlocked/unlockable: nothing to raise */ }
            }
        }

        // Liberation-gated groups (e.g. Ultima) are never auto-added at login; max performs the unlock request.
        void UnlockEligibleSkillGroups(CharacterData character)
        {
            CharacterSkillTable? skills = TableReaderV2.Parse<CharacterSkillTable>().Find(row => row.CharacterId == character.Id);
            foreach (int groupId in skills?.SkillGroupId.Where(id => id > 0).Distinct() ?? [])
            {
                IReadOnlyList<uint> groupSkills = Character.ResolveCharacterSkillIdsForGroupId(groupId);
                if (groupSkills.Count == 0 || character.SkillList.Any(skill => groupSkills.Contains(skill.Id)))
                    continue;
                CharacterSkillUpgradeTable? initial = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                    .Find(row => row.SkillId == (int)groupSkills[0] && row.Level == 0);
                if (initial is null || Character.MeetsCharacterSkillCondition(character, initial.ConditionId,
                        session.player.GatherRewards, session.player.PlayerData.Level))
                    character.SkillList.Add(new CharacterSkill { Id = groupSkills[0], Level = 1 });
            }
        }

        void MaxSkills(CharacterData character)
        {
            foreach (CharacterSkill skill in character.SkillList)
                skill.Level = Math.Max(1, SkillCap(character, (int)skill.Id));
        }

        // TeamRecommendBaseCharacter: the highest-investment authored loadout for the character.
        string WearRecommendedGear(CharacterData character, List<EquipData> changed)
        {
            CharacterTable template = Template(character);
            TeamRecommendBaseCharacterTable? recommended = TableReaderV2.Parse<TeamRecommendBaseCharacterTable>()
                .Where(row => row.CharacterId == character.Id)
                .OrderByDescending(row => row.CharacterQualityStar).ThenByDescending(row => row.Id).FirstOrDefault();
            if (recommended is null)
                return $"{character.Id}: no recommended gear in TeamRecommendBaseCharacter.";

            List<string> parts = [];
            List<(int TemplateId, int Site)> loadout = [(recommended.WeaponId, 0)];
            for (int index = 0; index < recommended.EquipIds.Count; index++)
                loadout.Add((recommended.EquipIds[index], index + 1));
            foreach ((int templateId, int site) in loadout.Where(entry => entry.TemplateId > 0))
            {
                EquipTable? equip = Character.ResolveEquipTemplate((uint)templateId);
                if (equip is null || equip.Site != site
                    || (site == 0 && equip.Type != template.EquipType)
                    || (equip.CharacterId != 0 && equip.CharacterId != (int)character.Id))
                {
                    parts.Add($"{templateId} skipped (not wearable at site {site})");
                    continue;
                }

                EquipData? target = session.character.Equips.Find(row => row.TemplateId == (uint)templateId && row.CharacterId == (int)character.Id)
                    ?? session.character.Equips.Find(row => row.TemplateId == (uint)templateId && row.CharacterId == 0);
                string source = target is null ? "granted" : "owned";
                target ??= session.character.AddEquip((uint)templateId);
                if (target is null)
                {
                    parts.Add($"{templateId} skipped (cannot be granted)");
                    continue;
                }

                foreach (EquipData previous in session.character.Equips.Where(row => row.Id != target.Id
                             && row.CharacterId == (int)character.Id
                             && TableReaderV2.Parse<EquipTable>().Find(table => table.Id == row.TemplateId)?.Site == site))
                {
                    previous.CharacterId = 0;
                    changed.Add(previous);
                }
                target.CharacterId = (int)character.Id;
                target.IsRecycle = false;
                changed.Add(target);
                parts.Add($"{templateId} {source}");
            }
            return $"{character.Id} gear (TeamRecommendBaseCharacter {recommended.Id}): {string.Join(", ", parts)}; " +
                "levels and resonances are not changed (use /equip).";
        }

        // ---- persistence and pushes ----

        void SaveAndPush(List<CharacterData> characters, List<EquipData> equips, List<int> gatherIds, bool playerChanged)
        {
            session.character.SaveChecked();
            if (playerChanged)
                session.player.SaveChecked();
            foreach (int id in gatherIds)
                session.SendPush(new NotifyGatherReward { Id = id });
            if (equips.Count > 0)
                session.SendPush(new NotifyEquipDataList { EquipDataList = equips.DistinctBy(equip => equip.Id).ToList() });
            session.SendPush(new NotifyCharacterDataList { CharacterDataList = characters });
        }
    }
}
