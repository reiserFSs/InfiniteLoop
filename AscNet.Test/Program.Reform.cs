using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.reform;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test;

internal static partial class Program
{
    [MessagePackObject(true)] public sealed class ReformSettleBody { public bool IsWin { get; set; } public ReformFightResult? ReformFightResult { get; set; } }
    [MessagePackObject(true)] public sealed class ReformSettleProbe { public int Code { get; set; } public ReformSettleBody? Settle { get; set; } }

    public static void ValidateReformCompatibility()
    {
        AssertEqual("AscNet.GameServer.Handlers.ReformModule", GetRegisteredRequestHandlerMethod("FubenReformEnterRequest").DeclaringType?.FullName,
            "FubenReformEnterRequest registered handler");
        // EN CodeText ids, asserted as literals so a remap is caught.
        AssertEqual("20123001,20123002,20123003,20123004,20123008,20123046,20123048,20123052,20123053,20123054,20123055,20123056,20123057,20123058,"
            + "20123059,20123060,20123061,20123062,20123064,20123065,20123066,20123067,20123068,20123069,20123070,20123071,20123072,20123073,"
            + "20123074,20123075,20123076,20123077,20123078,20123079",
            string.Join(",", new[]
            {
                ReformModule.NotInTime, ReformModule.StageNotActive, ReformModule.StageConfigMissing, ReformModule.StageNotInTime,
                ReformModule.DifficultyNotActive, ReformModule.GetCharacterError, ReformModule.TeamCountError, ReformModule.DataNotFound,
                ReformModule.PreFightDataNotFound, ReformModule.MassIdError, ReformModule.MassCfgNotFound, ReformModule.MassSideError,
                ReformModule.MassRepeat, ReformModule.SlotOverMax, ReformModule.MassNotEnough, ReformModule.MassCalcError,
                ReformModule.MinQualityNotEnough, ReformModule.MassTargetError, ReformModule.AffixCfgNotFound,
                ReformModule.EnemyTargetCfgNotFound, ReformModule.EnemySpawnCfgError, ReformModule.DifficultyBuffCfgNotFound,
                ReformModule.DifficultyCfgCountNotMatch, ReformModule.CharaGroupCfgNotFound, ReformModule.EnemyGroupCfgNotFound,
                ReformModule.AffixGroupCfgNotFound, ReformModule.CharacterOutOfRange, ReformModule.EnemyOutOfRange,
                ReformModule.AffixOutOfRange, ReformModule.FixedEnemyMissing, ReformModule.RobotNotInRange, ReformModule.CharacterRepeat,
                ReformModule.TeamSelectNull, ReformModule.TeamOutOfRange
            }), "Reform CodeText ids");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReformCfgTable cfg = TableReaderV2.Parse<ReformCfgTable>().OrderByDescending(row => row.Id).First(row => row.OpenTimeId > 0);
        ReformChapterTable[] chapters = TableReaderV2.Parse<ReformChapterTable>().OrderBy(row => row.Order).ToArray();
        Dictionary<int, ReformStageTable> stages = TableReaderV2.Parse<ReformStageTable>().ToDictionary(row => row.Id);
        foreach (int timeId in chapters.Select(row => row.OpenTime).Concat(chapters.SelectMany(row => row.ChapterStageId).Select(id => stages[id].OpenTimeId))
                     .Append(cfg.OpenTimeId).Where(id => id > 0).Distinct())
            AssertEqual(true, ActivityScheduleService.IsOpen(timeId, now), $"Reform TimeId {timeId} open by AscNet calendar policy");
        AssertEqual(cfg.Id, ReformModule.ActiveActivity(now)?.Id, "newest open Reform activity is selected");
        AssertEqual(2, cfg.GameType, "the open Reform activity is the balance mode");

        Dictionary<int, ReformBalanceMassTable> masses = TableReaderV2.Parse<ReformBalanceMassTable>().ToDictionary(row => row.Id);
        Dictionary<int, ReformBalanceCharacterTable> characterRows = TableReaderV2.Parse<ReformBalanceCharacterTable>().ToDictionary(row => row.CharacterId);
        Dictionary<int, ReformBalanceAffixTable> affixes = TableReaderV2.Parse<ReformBalanceAffixTable>().ToDictionary(row => row.Id);
        Dictionary<int, ReformBalanceTargetGroupTable> targetGroups = TableReaderV2.Parse<ReformBalanceTargetGroupTable>().ToDictionary(row => row.Id);
        Dictionary<int, ReformBalanceAffixGroupTable> affixGroups = TableReaderV2.Parse<ReformBalanceAffixGroupTable>().ToDictionary(row => row.Id);
        Dictionary<int, ReformUseCharaGroupTable> charaGroups = TableReaderV2.Parse<ReformUseCharaGroupTable>().ToDictionary(row => row.Id);
        Dictionary<int, ReformEnemyTargetTable> enemies = TableReaderV2.Parse<ReformEnemyTargetTable>().ToDictionary(row => row.Id);
        HashSet<int> robotIds = TableReaderV2.Parse<RobotTable>().Select(row => row.Id).ToHashSet();
        foreach (ReformBalanceCharacterTable row in characterRows.Values)
            AssertEqual(true, robotIds.Contains(row.RobotId), $"balance robot {row.RobotId} exists in the Robot table");

        ReformBalanceMassTable MassOf(int type, int target, int difficulty) =>
            masses.Values.Single(row => row.WeightType == type && row.TargetId == target && row.Difficulty == difficulty);
        int Resolve(ReformBalanceMassTable row, HashSet<int> passed)
        {
            int hit = -1;
            for (int i = 0; i < row.UnlockStageIds.Count && i < row.MassValues.Count; i++)
                if (passed.Contains(row.UnlockStageIds[i])) hit = i;
            return hit >= 0 ? row.MassValues[hit] : row.DefaultMass;
        }

        // Stage 1 (chapter 1, normal) and the chapter-2 hard stage with its fixed monster.
        ReformStageTable s1 = stages[chapters[0].ChapterStageId[0]];
        ReformChapterTable chapter2 = chapters[1];
        ReformStageTable s2 = stages[chapter2.ChapterStageId[0]];
        ReformStageTable hard = stages[chapter2.ChapterStageId[1]];
        AssertEqual(1, s1.Difficulty, "first chapter stage is normal");
        AssertEqual(2, hard.Difficulty, "second chapter stage is hard");
        AssertEqual(chapters[0].Id, chapter2.Order, "chapter 2 follows chapter 1 by Order");

        List<int> FixedIds(ReformStageTable stage) => stage.FixedEnemyIds is > 0 ? new List<int> { stage.FixedEnemyIds.Value } : new List<int>();
        List<ReformBalanceMassTable> CharacterMasses(ReformStageTable stage) => charaGroups[stage.UseCharaGroup].CharacterIds
            .Select(id => MassOf(1, id, stage.Difficulty)).ToList();
        List<ReformBalanceMassTable> MonsterMasses(ReformStageTable stage) => targetGroups[stage.EnemyGroupId].TargetIds
            .Concat(FixedIds(stage))
            .Select(id => MassOf(2, id, stage.Difficulty)).ToList();
        // Brute-force a valid selection (fixed monster included) whose totals satisfy the predicate.
        (List<int> Wish, List<int> Cost, int WishTotal, int CostTotal) Find(ReformStageTable stage, HashSet<int> passed, Func<int, int, bool> predicate,
            Func<List<ReformBalanceMassTable>, bool>? wishFilter = null)
        {
            List<ReformBalanceMassTable> chars = CharacterMasses(stage), monsters = MonsterMasses(stage);
            ReformBalanceMassTable? fixedMass = stage.FixedEnemyIds is > 0 ? MassOf(2, stage.FixedEnemyIds!.Value, stage.Difficulty) : null;
            List<ReformBalanceMassTable> enemyAffixMasses = affixGroups[stage.AffixGroupId].AffixIds.Where(id => affixes[id].AffixType == 2)
                .Select(id => MassOf(4, id, stage.Difficulty)).ToList();
            int monsterMin = fixedMass is null ? 2 : 1, costRoom = stage.CostWeightSlotNum - (fixedMass is null ? 0 : 1);
            foreach (List<ReformBalanceMassTable> wish in Subsets(chars, 1, Math.Min(stage.WishWeightSlotNum, 3)))
                foreach (List<ReformBalanceMassTable> cost in Subsets(monsters.Where(row => row != fixedMass).ToList(), monsterMin, costRoom))
                    foreach (List<ReformBalanceMassTable> extra in Subsets(enemyAffixMasses, 0, costRoom - cost.Count))
                    {
                        List<ReformBalanceMassTable> fullCost = [.. (fixedMass is null ? [] : new List<ReformBalanceMassTable> { fixedMass }), .. cost, .. extra];
                        int w = wish.Sum(row => Resolve(row, passed)), c = fullCost.Sum(row => Resolve(row, passed));
                        if (w >= stage.MinQuality && predicate(w, c) && (wishFilter?.Invoke(wish) ?? true))
                            return (wish.Select(row => row.Id).ToList(), fullCost.Select(row => row.Id).ToList(), w, c);
                    }
            throw new InvalidOperationException($"no Reform selection for stage {stage.Id}");
        }
        static IEnumerable<List<T>> Subsets<T>(List<T> items, int min, int max)
        {
            for (int mask = min == 0 ? 0 : 1; mask < 1 << items.Count; mask++)
            {
                List<T> subset = Enumerable.Range(0, items.Count).Where(i => (mask >> i & 1) == 1).Select(i => items[i]).ToList();
                if (subset.Count >= min && subset.Count <= max) yield return subset;
            }
        }
        const double normalRate = 0.2;
        bool Perfect(int w, int c) => w == c;
        bool Unbalanced(int w, int c) => (double)Math.Abs(w - c) / w > normalRate;
        bool NormalBalance(int w, int c) => w != c && !Unbalanced(w, c);
        int Tier(ReformStageTable stage, int cost) => Math.Clamp(stage.DifficultyMassThresholds.FindLastIndex(t => cost >= t), 0, 2);
        int TargetOf(int massId) => masses[massId].TargetId;

        const long playerId = 99_250;
        Character character = CreateDrawCompatibilityCharacter(playerId);
        // Own every stage character except the second bond character, who is deployed as a balance robot.
        int bondRobotCharacter = 1_231_002;
        int[] ownedCharacters = charaGroups[s1.UseCharaGroup].CharacterIds.Where(id => id != bondRobotCharacter).ToArray();
        character.Characters.AddRange(ownedCharacters.Select(id => new CharacterData { Id = (uint)id, Level = 80, Ability = 6_500 }));
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _, out _);
        using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "reform");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        int packetId = 700;
        List<Packet.Push> pushes = [];
        T Call<T>(string name, object? request)
        {
            int id = ++packetId;
            pushes.Clear();
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push) { pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content)); continue; }
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                AssertEqual(name.Replace("Request", "Response"), wire.Name, $"{name} response name");
                return MessagePackSerializer.Deserialize<T>(wire.Content);
            }
        }
        Fight? fightBefore = null;
        PreFightResponse PreFight(int stageId, List<int>? wish, List<int>? cost, uint[]? cards, int[]? robots, int challengeCount = 1)
        {
            PreFightRequest request = new()
            {
                PreFightData = new()
                {
                    StageId = (uint)stageId, ChallengeCount = challengeCount, CaptainPos = 1, FirstFightPos = 1,
                    CardIds = cards?.ToList() ?? [], RobotIds = robots?.ToList() ?? [0, 0, 0],
                    ReformBalanceData = wish is null && cost is null ? null : new ReformBalanceData { WishMassIds = wish, CostMassIds = cost }
                }
            };
            fightBefore = harness.Session.fight;
            return Call<PreFightResponse>(nameof(PreFightRequest), request);
        }
        // Lineup for a selection: own characters as cards, the rest as the balance robots.
        (uint[] Cards, int[] Robots) Lineup(List<int> wish, params int[] robotFor)
        {
            List<int> chars = wish.Where(id => masses[id].WeightType == 1).Select(TargetOf).ToList();
            uint[] cards = chars.Where(id => !robotFor.Contains(id) && ownedCharacters.Contains(id)).Select(id => (uint)id).ToArray();
            int[] robots = robotFor.Select(id => characterRows[id].RobotId).ToArray();
            return (cards.Concat(new uint[3]).Take(3).ToArray(), robots.Concat(new int[3]).Take(3).ToArray());
        }
        PreFightResponse Enter(int stageId, List<int> wish, List<int> cost, params int[] robotFor)
        {
            (uint[] cards, int[] robots) = Lineup(wish, robotFor);
            return PreFight(stageId, wish, cost, cards, robots);
        }
        // A selection that needs robots for every character the player does not own.
        int[] Unowned(List<int> wish) => wish.Where(id => masses[id].WeightType == 1).Select(TargetOf).Where(id => !ownedCharacters.Contains(id)).ToArray();
        PreFightResponse Valid(int stageId, (List<int> Wish, List<int> Cost, int WishTotal, int CostTotal) pick) => Enter(stageId, pick.Wish, pick.Cost, Unowned(pick.Wish));
        FightSettleRequest SettleRequest(int stageId, long fightId, bool win = true)
        {
            FightSettleRequest request = CreateMissingStageSettleRequest((uint)stageId, fightId, playerId);
            request.Result.IsWin = win;
            return request;
        }
        long saves() => playerSaves.ReplaceOneCalls;
        ReformFubenDb EnterDb() => Call<FubenReformEnterResponse>("FubenReformEnterRequest", null).ReformFubenDb!;

        // Enter + login push: activity, every chapter stage, DifficultyDbs[CurDiffIndex + 1] present for SetData.
        FubenReformEnterResponse entered = Call<FubenReformEnterResponse>("FubenReformEnterRequest", null);
        AssertEqual(0, entered.Code, "FubenReformEnterRequest code");
        AssertEqual(cfg.Id, entered.ReformFubenDb!.ActivityId, "enter activity id");
        AssertEqual(chapters.SelectMany(row => row.ChapterStageId).Count(), entered.ReformFubenDb.StageDbs.Count, "enter lists every chapter stage");
        AssertEqual(true, entered.ReformFubenDb.StageDbs.All(row => !row.Pass && row.DifficultyDbs.Count == 1 && row.EnvId is null), "fresh stage dbs");
        AssertEqual(cfg.Id, ReformModule.BuildLoginData(harness.Session.player, now)!.ReformFubenDb.ActivityId, "login push activity id");
        AssertEqual(0, saves(), "enter persists nothing");

        // Stage availability.
        PreFightResponse bad = PreFight(s2.Id, [1], [2], [1], [0, 0, 0]);
        AssertEqual(ReformModule.StageNotActive, bad.Code, "chapter 2 locked until chapter 1 is passed");
        AssertEqual(ReformModule.StageNotActive, PreFight(hard.Id, [1], [2], [1], [0, 0, 0]).Code, "chapter 2 hard locked behind chapter 1");

        // Selection boundaries on stage 1; none starts a fight or persists.
        var pick = Find(s1, [], Unbalanced, chars => chars.Any(row => ownedCharacters.Contains(row.TargetId)));
        AssertEqual(null, harness.Session.fight, "no fight before a valid pre-fight");
        List<int> w = pick.Wish, c = pick.Cost;
        (uint[] okCards, int[] okRobots) = Lineup(w, Unowned(w));
        void Reject(int code, string name, PreFightResponse response)
        {
            AssertEqual(code, response.Code, name);
            AssertEqual(true, ReferenceEquals(fightBefore, harness.Session.fight), $"{name} starts no fight");
        }
        Reject(ReformModule.DataNotFound, "missing ReformBalanceData", PreFight(s1.Id, null, null, okCards, okRobots));
        Reject(ReformModule.SlotOverMax, "wish side over slot capacity", PreFight(s1.Id, CharacterMasses(s1).Take(s1.WishWeightSlotNum + 1).Select(row => row.Id).ToList(), c, okCards, okRobots));
        Reject(ReformModule.MassRepeat, "duplicate weight", PreFight(s1.Id, [w[0], w[0]], c, okCards, okRobots));
        Reject(ReformModule.MassCfgNotFound, "unknown weight id", PreFight(s1.Id, [-1], c, okCards, okRobots));
        Reject(ReformModule.MassSideError, "monster weight on the wish side", PreFight(s1.Id, [c[0]], c.Skip(1).ToList(), okCards, okRobots));
        ReformStageTable foreignStage = stages[chapters[2].ChapterStageId[0]];
        int foreignMonster = MonsterMasses(foreignStage).First(row => !targetGroups[s1.EnemyGroupId].TargetIds.Contains(row.TargetId)).Id;
        Reject(ReformModule.EnemyOutOfRange, "monster outside the stage group", PreFight(s1.Id, w, [foreignMonster, c[0]], okCards, okRobots));
        Reject(ReformModule.MassNotEnough, "fewer monsters than the minimum", PreFight(s1.Id, w, [c[0]], okCards, okRobots));
        Reject(ReformModule.MassNotEnough, "no character weight", PreFight(s1.Id, [], c, okCards, okRobots));
        int lowChar = CharacterMasses(s1).OrderBy(row => row.DefaultMass).First().Id;
        AssertEqual(true, masses[lowChar].DefaultMass < s1.MinQuality, "a single weakest character is below the stage minimum");
        Reject(ReformModule.MinQualityNotEnough, "wish mass below the stage minimum", PreFight(s1.Id, [lowChar], c, okCards, okRobots));
        int hardCharMass = CharacterMasses(hard).First().Id;
        Reject(ReformModule.MassIdError, "weight of the other difficulty", PreFight(s1.Id, [hardCharMass], c, okCards, okRobots));
        Reject(ReformModule.PreFightDataNotFound, "challenge count above one", PreFight(s1.Id, w, c, okCards, okRobots, challengeCount: 3));
        // Lineup must come from the selected characters, with whitelisted robots and no own+robot twins.
        int unselectedChar = ownedCharacters.First(id => !w.Select(TargetOf).Contains(id));
        Reject(ReformModule.TeamOutOfRange, "card outside the selected weights", PreFight(s1.Id, w, c, [(uint)unselectedChar, 0, 0], [0, 0, 0]));
        Reject(ReformModule.RobotNotInRange, "robot outside the balance whitelist", PreFight(s1.Id, w, c, [0, 0, 0], [robotIds.First(id => characterRows.Values.All(row => row.RobotId != id)), 0, 0]));
        Reject(ReformModule.TeamSelectNull, "empty lineup", PreFight(s1.Id, w, c, [0, 0, 0], [0, 0, 0]));
        int ownedSelected = w.Select(TargetOf).First(ownedCharacters.Contains);
        Reject(ReformModule.CharacterRepeat, "character deployed as both card and robot",
            PreFight(s1.Id, w, c, [(uint)ownedSelected, 0, 0], [0, characterRows[ownedSelected].RobotId, 0]));
        AssertEqual(0, saves(), "rejected pre-fights persist nothing");

        // Accepted pre-fight, unbalanced input: events and waves derive from the selection.
        PreFightResponse unbalanced = Valid(s1.Id, pick);
        AssertEqual(0, unbalanced.Code, "valid pre-fight code");
        AssertEqual(true, harness.Session.fight is not null, "valid pre-fight starts a fight");
        List<int> events = unbalanced.FightData.EventIds.Select(e => (int)e).ToList();
        AssertEqual(true, events.Contains(s1.UnbalancedFightEventId), "unbalanced selection carries the unbalanced event");
        AssertEqual(false, events.Contains(s1.PerfectBalanceFightEventId), "unbalanced selection has no perfect event");
        AssertEqual(true, events.Contains(s1.DifficultyBuffIds[Tier(s1, pick.CostTotal)]), "difficulty buff of the cost tier");
        List<ReformEnemyTargetTable> chosenEnemies = pick.Cost.Select(id => enemies[TargetOf(id)]).ToList();
        // The stage's behavior tree (Behavior 30162552..62) reads StageParams ReformBalanceEnemyWave1..3 ([NpcId, Level, Hp] x up to 8) and
        // never NpcGroupList: an absent/empty list makes it skip every wave and clear instantly (the reported "no enemies, auto-clear").
        Dictionary<int, AscNet.Table.V2.share.fight.npc.NpcTable> npcRows = TableReaderV2.Parse<AscNet.Table.V2.share.fight.npc.NpcTable>().ToDictionary(row => row.Id);
        void AssertWaves(PreFightResponse response, IEnumerable<ReformEnemyTargetTable> chosen, string label)
        {
            // The loader preloads npc models from NpcGroupList (the level spawns from StageParams): every spawned NpcId must be in it.
            HashSet<int> preload = ((System.Collections.IEnumerable)response.FightData.NpcGroupList!).Cast<object>()
                .SelectMany(group => ((System.Collections.IEnumerable)((System.Collections.IDictionary)group)["NpcList"]!).Cast<object>())
                .Select(npc => Convert.ToInt32(((System.Collections.IDictionary)npc)["NpcId"])).ToHashSet();
            AssertEqual(true, chosen.All(row => preload.Contains(row.NpcId)), $"{label}: every spawned NpcId is in the NpcGroupList preload set");
            var stageParams = ((Dictionary<object, object>)response.FightData.StageParams!).ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value);
            AssertEqual(3, stageParams.Count, $"{label}: three wave keys");
            int total = 0;
            for (int wave = 1; wave <= 3; wave++)
            {
                List<int> list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<int>>(stageParams[$"ReformBalanceEnemyWave{wave}"])!;
                List<int> expected = chosen.Where(row => row.SpawnWave == wave)
                    .SelectMany(row => Enumerable.Repeat(new[] { row.NpcId, row.Level, row.Hp }, row.SpawnCount)).SelectMany(slot => slot).ToList();
                AssertEqual(string.Join(",", expected), string.Join(",", list), $"{label}: wave {wave} [NpcId, Level, Hp] triples");
                AssertEqual(true, list.Count % 3 == 0 && list.Count <= 24, $"{label}: wave {wave} fits the level's 8 slots");
                for (int slot = 0; slot < list.Count; slot += 3)
                    AssertEqual(true, npcRows.ContainsKey(list[slot]) && list[slot + 1] > 0 && list[slot + 2] > 0, $"{label}: wave {wave} slot {slot / 3} npc {list[slot]} exists, level and hp positive");
                total += list.Count / 3;
            }
            AssertEqual(true, total > 0, $"{label}: at least one enemy spawns");
            AssertEqual(chosen.Sum(row => row.SpawnCount), total, $"{label}: enemy count = sum of SpawnCount");
        }
        AssertWaves(unbalanced, chosenEnemies, "normal stage selection A");
        AssertEqual(unbalanced.FightData.RoleData[0].NpcData.Count, Lineup(pick.Wish, Unowned(pick.Wish)).Cards.Count(id => id > 0) + Unowned(pick.Wish).Length, "deployed team");

        // Distinct inputs: a perfect balance carries the perfect event instead (normal stage), a normal balance neither.
        var perfect = Find(s1, [], Perfect);
        PreFightResponse perfectResponse = Valid(s1.Id, perfect);
        AssertEqual(0, perfectResponse.Code, "perfect selection accepted");
        AssertWaves(perfectResponse, perfect.Cost.Where(id => masses[id].WeightType == 2).Select(id => enemies[TargetOf(id)]), "normal stage selection B");
        List<int> perfectEvents = perfectResponse.FightData.EventIds.Select(e => (int)e).ToList();
        AssertEqual(true, perfectEvents.Contains(s1.PerfectBalanceFightEventId), "perfect normal-stage selection carries the perfect event");
        AssertEqual(false, perfectEvents.Contains(s1.UnbalancedFightEventId), "perfect selection has no unbalanced event");
        var normal = Find(s1, [], NormalBalance);
        List<int> normalEvents = Valid(s1.Id, normal).FightData.EventIds.Select(e => (int)e).ToList();
        AssertEqual(false, normalEvents.Contains(s1.UnbalancedFightEventId) || normalEvents.Contains(s1.PerfectBalanceFightEventId), "normal balance carries neither event");

        // Affix weights: group affix accepted and its fight event added; bond-unlocked affix needs the bond's characters.
        ReformBalanceAffixGroupTable group1 = affixGroups[s1.AffixGroupId];
        ReformBalanceAffixTable myAffix = group1.AffixIds.Select(id => affixes[id]).First(row => row.AffixType == 1);
        ReformBalanceAffixTable enemyAffix = group1.AffixIds.Select(id => affixes[id]).First(row => row.AffixType == 2);
        var withAffix = Find(s1, [], (wt, ct) => true, chars => chars.Count < s1.WishWeightSlotNum);
        List<int> wishAffix = [.. withAffix.Wish, MassOf(3, myAffix.Id, 1).Id];
        List<int> costAffix = withAffix.Cost.Count < s1.CostWeightSlotNum ? [.. withAffix.Cost, MassOf(4, enemyAffix.Id, 1).Id] : withAffix.Cost;
        PreFightResponse affixResponse = Enter(s1.Id, wishAffix, costAffix, Unowned(wishAffix));
        AssertEqual(0, affixResponse.Code, "affix selection accepted");
        List<int> affixEvents = affixResponse.FightData.EventIds.Select(e => (int)e).ToList();
        AssertEqual(true, affixEvents.Contains(myAffix.FightEventId), "own affix event added");
        if (costAffix != withAffix.Cost)
            AssertEqual(true, affixEvents.Contains(enemyAffix.FightEventId), "enemy affix event added");
        ReformBalanceAffixTable foreignAffix = affixes.Values.First(row => row.AffixType == 1 && !group1.AffixIds.Contains(row.Id)
            && masses.Values.Any(m => m.WeightType == 3 && m.TargetId == row.Id && m.Difficulty == 1)
            && TableReaderV2.Parse<ReformBalanceBondTable>().All(bond => bond.UnlockedAffixIds != row.Id));
        Reject(ReformModule.AffixOutOfRange, "affix outside the stage group", Enter(s1.Id, [.. withAffix.Wish, MassOf(3, foreignAffix.Id, 1).Id], withAffix.Cost, Unowned(withAffix.Wish)));
        ReformBalanceBondTable bond = TableReaderV2.Parse<ReformBalanceBondTable>().First(row => s1.BalanceBondIds.Contains(row.Id) && row.RequiredAffixIds.Count == 0
            && row.RequiredCharacterIds.Count == 2 && row.TriggerMode == 1);
        List<int> bondChars = bond.RequiredCharacterIds.Select(id => MassOf(1, id, 1).Id).ToList();
        List<int> bondWish = [.. bondChars, MassOf(3, bond.UnlockedAffixIds, 1).Id];
        AssertEqual(0, Enter(s1.Id, bondWish, c, Unowned(bondWish)).Code, "bond-unlocked affix accepted once the bond's characters are selected");
        Reject(ReformModule.AffixOutOfRange, "bond affix without the bond's characters",
            Enter(s1.Id, [bondChars[0], bondWish[2]], c, Unowned([bondChars[0]])));

        // Settle: loss changes nothing; win persists pass + best wish mass and returns the client's ReformFightResult.
        PreFightResponse fight1 = Valid(s1.Id, perfect);
        long before = saves();
        ReformSettleProbe lost = Call<ReformSettleProbe>(nameof(FightSettleRequest), SettleRequest(s1.Id, fight1.FightData.FightId, win: false));
        AssertEqual(0, lost.Code, "lost balance fight settles");
        AssertEqual(false, lost.Settle!.IsWin, "lost balance fight is not a win");
        AssertEqual(null, lost.Settle.ReformFightResult, "lost balance fight has no ReformFightResult");
        AssertEqual(before, saves(), "lost balance fight persists no Reform state");
        AssertEqual(null, harness.Session.fight, "settle clears the fight");
        PreFightResponse fight2 = Valid(s1.Id, perfect);
        ReformSettleProbe won = Call<ReformSettleProbe>(nameof(FightSettleRequest), SettleRequest(s1.Id, fight2.FightData.FightId));
        AssertEqual(0, won.Code, "won balance fight settles");
        AssertEqual(perfect.WishTotal, won.Settle!.ReformFightResult!.FinalWishWeight, "FinalWishWeight = selected wish mass (normal stage: no bonus)");
        AssertEqual(null, won.Settle.ReformFightResult.BonusWishWeight, "normal stage perfect balance has no bonus mass");
        AssertEqual(true, harness.Session.player.Reform!.Stages.Single(row => row.StageId == s1.Id).Passed, "stage passed in state");
        AssertEqual(true, saves() > before, "win persists the player");
        AssertEqual(true, pushes.Any(push => push.Name == nameof(NotifyTask)), "win pushes the Reform task state");
        harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        AssertEqual(perfect.WishTotal, harness.Session.player.Reform!.Stages.Single(row => row.StageId == s1.Id).MaxWishQuality, "best wish mass survives BSON reload");
        ReformStageDb db1 = EnterDb().StageDbs.Single(row => row.Id == s1.Id);
        AssertEqual(true, db1.Pass, "enter reports the pass");
        AssertEqual(perfect.WishTotal, db1.BalanceMaxWishQuality, "enter reports the best wish mass");
        // A later weaker clear keeps the best.
        // A later higher clear raises the best, a weaker one keeps it.
        var higher = Find(s1, [], (wt, ct) => wt > perfect.WishTotal);
        PreFightResponse fight3 = Valid(s1.Id, higher);
        AssertEqual(0, Call<ReformSettleProbe>(nameof(FightSettleRequest), SettleRequest(s1.Id, fight3.FightData.FightId)).Code, "higher win settles");
        AssertEqual(higher.WishTotal, harness.Session.player.Reform!.Stages.Single(row => row.StageId == s1.Id).MaxWishQuality, "higher win raises the best wish mass");
        PreFightResponse fight4 = Valid(s1.Id, perfect);
        AssertEqual(0, Call<ReformSettleProbe>(nameof(FightSettleRequest), SettleRequest(s1.Id, fight4.FightData.FightId)).Code, "weaker win settles");
        AssertEqual(higher.WishTotal, harness.Session.player.Reform!.Stages.Single(row => row.StageId == s1.Id).MaxWishQuality, "weaker win keeps the best wish mass");
        AssertEqual(true, Call<FightSettleResponse>(nameof(FightSettleRequest), SettleRequest(s1.Id, 4242)).Code != 0,
            "settle without an accepted pre-fight is not accepted");

        // Chapter 2 is open now; its hard stage still needs every normal stage.
        PreFightResponse s2Fight = Valid(s2.Id, Find(s2, [s1.Id], (wt, ct) => true));
        AssertEqual(0, s2Fight.Code, "chapter 2 opens after chapter 1");
        Reject(ReformModule.DifficultyNotActive, "hard stage locked until every normal stage is passed", PreFight(hard.Id, [1], [2], [1], [0, 0, 0]));

        // Task progress: stage-clear task achieved, claimable once.
        TaskTable clearTask = TableReaderV2.Parse<TaskTable>().First(task => task.Id == 140435);
        ConditionTable clearCondition = TableReaderV2.Parse<ConditionTable>().First(row => row.Id == clearTask.Condition);
        AssertEqual(s1.Id, clearCondition.Params[0], "task 140435 targets the first chapter stage");
        LoginTask Row(int id) => TaskModule.BuildTaskData(harness.Session).Single(row => row.Id == id);
        AssertEqual(3, Row(140435).State, "stage-clear task achieved after the win");
        AssertEqual(1, Row(140436).State, "second stage-clear task still active");
        FinishTaskResponse claim = Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140435 });
        AssertEqual(0, claim.Code, "Reform task claim succeeds");
        AssertEqual(true, claim.RewardGoodsList.Count > 0, "Reform task claim grants its reward");
        AssertEqual(true, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140435 }).Code != 0, "Reform task rejects a duplicate claim");
        AssertEqual(true, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140436 }).Code != 0, "Reform task rejects an unearned claim");
        AssertEqual(4, Row(140435).State, "claimed task state");

        // Hard stage after every normal stage: a perfect balance converts into extra wish mass.
        HashSet<int> passedAll = chapters.Select(row => row.ChapterStageId[0]).ToHashSet();
        ReformState state = harness.Session.player.Reform!;
        foreach (int id in passedAll.Where(id => state.Stages.All(row => row.StageId != id)))
            state.Stages.Add(new ReformStageRecord { StageId = id, Passed = true, MaxWishQuality = stages[id].MinQuality });
        foreach (ReformStageRecord row in state.Stages) row.Passed = true;
        harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        Reject(ReformModule.FixedEnemyMissing, "hard stage without its fixed monster",
            Enter(hard.Id, Find(hard, passedAll, (wt, ct) => true).Wish, MonsterMasses(hard).Where(row => row.TargetId != hard.FixedEnemyIds).Take(2).Select(row => row.Id).ToList(),
                Unowned(Find(hard, passedAll, (wt, ct) => true).Wish)));
        var hardPerfect = Find(hard, passedAll, Perfect);
        PreFightResponse hardFight = Valid(hard.Id, hardPerfect);
        AssertEqual(0, hardFight.Code, "hard perfect selection accepted");
        AssertWaves(hardFight, hardPerfect.Cost.Where(id => masses[id].WeightType == 2).Select(id => enemies[TargetOf(id)]), "hard stage selection");
        AssertEqual(false, hardFight.FightData.EventIds.Any(e => (int)e == hard.PerfectBalanceFightEventId), "hard perfect balance pays mass, not the fight event");
        ReformSettleProbe hardWin = Call<ReformSettleProbe>(nameof(FightSettleRequest), SettleRequest(hard.Id, hardFight.FightData.FightId));
        int bonus = (int)Math.Floor(hardPerfect.WishTotal * 1.1 - hardPerfect.WishTotal);
        AssertEqual(bonus, hardWin.Settle!.ReformFightResult!.BonusWishWeight, "hard perfect balance bonus = floor(10% of wish mass)");
        AssertEqual(hardPerfect.WishTotal + bonus, hardWin.Settle.ReformFightResult.FinalWishWeight, "hard FinalWishWeight includes the bonus");
        AssertEqual(hardPerfect.WishTotal + bonus, harness.Session.player.Reform!.Stages.Single(row => row.StageId == hard.Id).MaxWishQuality, "hard best wish mass includes the bonus");
        // Hard accumulation task: progress equals the summed best of the hard stages.
        TaskTable hardTask = TableReaderV2.Parse<TaskTable>().First(task => task.Id == 140441);
        AssertEqual(Math.Min(hardTask.Result ?? 1, hardPerfect.WishTotal + bonus), Row(140441).Schedule.Single().Value, "hard wish-mass task progress");
        AssertEqual(hardTask.Result <= hardPerfect.WishTotal + bonus ? 3 : 1, Row(140441).State, "hard wish-mass task state follows the accumulated mass");
        // Laia's mass grows once her stage is passed (UnlockStageIds), changing the total of the same selection.
        ReformBalanceMassTable laia = masses.Values.First(row => row.UnlockStageIds.Count > 0 && row.WeightType == 1);
        AssertEqual(true, Resolve(laia, []) != Resolve(laia, [laia.UnlockStageIds[0]]), "growth mass differs once its unlock stage is passed");
    }
}
