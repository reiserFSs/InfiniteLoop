using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.theatre4.theatre4map;
using MessagePack;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Reflection;
using GateConditionTable = AscNet.Table.V2.share.condition.ConditionTable;

namespace AscNet.Test;

internal partial class Program
{
    // EN XConditionManager.lua:2337-2489 and XTheatre4Agency.lua:743-903 are the
    // authority for these leaves. Authored rows use IsConditionMet through the
    // production condition map; only synthetic boundary rows use CompileGateLeaf.
    private static void ValidateTheatre4ProgressionAuditCompatibility()
    {
        ValidateTheatre4ProgressionGateAudit();
        ValidateTheatre4ProgressionGridChains();
        ValidateTheatre4ProgressionFateChain();
        ValidateTheatre4ProgressionRecovery();
        ValidateTheatre4ProgressionTechEffects();
    }

    private static void ValidateTheatre4ProgressionGateAudit()
    {
        // 17401 is the authored talent gate 1020771 [16, 1]. The EN checker walks
        // every selected slot, not the account talent atlas.
        using (Theatre4Case talent = new("progress-gate-talent"))
        {
            talent.StartRun(1);
            GateConditionTable talentGate = Theatre4ProgressGateRow(1020771);
            AssertEqual(false, Theatre4ProgressEvaluateGate(talent, talentGate),
                "17401 rejects an unselected talent");
            Theatre4ColorTalentData color = talent.Adventure.Colors.First(row => row.Color == 1);
            color.Slots.Add(new Theatre4ColorTalentSlotData
            {
                SlotId = 99_001,
                Talents = [new Theatre4TalentData { TalentId = talentGate.Params[0] }]
            });
            talent.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(talent, talentGate),
                "17401 accepts an active selected talent");
        }

        // No authored rows currently consume 17405-17408. These are deliberately
        // synthetic evaluator boundaries, never resource fixtures or wire proofs.
        using (Theatre4Case leaves = new("progress-gate-leaves"))
        {
            leaves.StartRun(1);
            Theatre4ColorTalentData red = leaves.Adventure.Colors.Single(row => row.Color == 1);
            Theatre4ColorTalentData yellow = leaves.Adventure.Colors.Single(row => row.Color == 2);
            Theatre4ColorTalentData blue = leaves.Adventure.Colors.Single(row => row.Color == 3);

            red.Level = 3;
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17405, 1, 3)), "17405 accepts the exact color level");
            red.Level = 2;
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17405, 1, 3)), "17405 rejects a lower color level");

            leaves.SetGold(100);
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17406, 1, 100)), "17406 >= accepts the boundary gold");
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17406, 1, 101)), "17406 >= rejects below the boundary");
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17406, 2, 100)), "17406 <= accepts the boundary gold");
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17406, 2, 99)), "17406 <= rejects above the boundary");

            leaves.Adventure.Hp = 100;
            leaves.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17407, 1, 100)), "17407 >= accepts the boundary HP");
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17407, 1, 101)), "17407 >= rejects below the boundary");
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17407, 2, 100)), "17407 <= accepts the boundary HP");
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17407, 2, 99)), "17407 <= rejects above the boundary");

            red.Point = 10;
            yellow.Point = 10;
            blue.Point = 3;
            leaves.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17408, 1)),
                "17408 treats a tied largest color as largest");
            red.Point = 9;
            leaves.SaveFixture();
            AssertEqual(false, Theatre4ProgressEvaluateGate(leaves,
                Theatre4ProgressSyntheticGate(17408, 1)),
                "17408 rejects a color below another color");
        }

        // Authored difficulty request gate 1020677 = 17410 [1, 1]. The same
        // condition is proven through the real SelectDifficult request, not only
        // by invoking the evaluator.
        using (Theatre4Case difficulty = new("progress-gate-difficulty"))
        {
            GateConditionTable difficultyGate = Theatre4ProgressGateRow(1020677);
            Theatre4EndingTable successfulEnding = TableReaderV2.Parse<Theatre4EndingTable>()
                .FirstOrDefault(row => row.PassType != 1)
                ?? throw new InvalidDataException("Theatre4 has no authored successful ending row.");
            AssertEqual(false, Theatre4ProgressEvaluateGate(difficulty, difficultyGate),
                "17410 blocks an uncleared difficulty");
            difficulty.Data.Difficultys[1] = 1;
            difficulty.Data.Endings[successfulEnding.Id] = 1;
            difficulty.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(difficulty, difficultyGate),
                "17410 observes a difficulty clear paired with an authored successful ending");
            AssertEqual(true, Theatre4ProgressEvaluateGate(difficulty,
                Theatre4ProgressSyntheticGate(17410, 1, 1)),
                "17410 count-one accepts an authored successful ending completion");
            difficulty.Data.Difficultys[1] = 0;
            difficulty.Data.Endings.Remove(successfulEnding.Id);
            difficulty.SaveFixture();
            AssertEqual(false, Theatre4ProgressEvaluateGate(difficulty, difficultyGate),
                "17410 still blocks with no completed difficulty");
            AssertEqual(true, Theatre4ProgressEvaluateGate(difficulty,
                Theatre4ProgressSyntheticGate(17410, 1, 0)),
                "17410 zero-count accepts no completed difficulty");
            difficulty.Data.Difficultys[1] = 1;
            difficulty.Data.Endings[successfulEnding.Id] = 1;
            difficulty.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(difficulty,
                Theatre4ProgressSyntheticGate(17410, 1, 0)),
                "17410 zero-count accepts a completed difficulty");

            difficulty.Data.Difficultys[1] = 0;
            difficulty.Data.Endings.Remove(successfulEnding.Id);
            difficulty.SaveFixture();
            difficulty.Reject(nameof(Theatre4SelectDifficultRequest),
                new Theatre4SelectDifficultRequest { Difficult = 2 },
                "SelectDifficult before the authored prerequisite");
            difficulty.Data.Difficultys[1] = 1;
            difficulty.Data.Endings[successfulEnding.Id] = 1;
            difficulty.SaveFixture();
            difficulty.StartRun(2);
            AssertEqual(2, difficulty.Adventure.Difficulty,
                "SelectDifficult accepts after the authored difficulty clear");
        }

        // 1020790 = 17411 [11, 0] is the authored zero-threshold check.
        // EN XTheatre4Agency.lua:815-833 returns completeCount >= count for 17411;
        // unlike 17415's explicit count==0 absence check, reaching ending 11 still
        // satisfies this authored zero threshold. Keep a positive-count boundary too.
        // 1020770 = 17411 [4, 1] is consumed by the real tech unlock request.
        using (Theatre4Case ending = new("progress-gate-ending"))
        {
            GateConditionTable notEnding11 = Theatre4ProgressGateRow(1020790);
            GateConditionTable ending11Once = Theatre4ProgressSyntheticGate(17411, 11, 1);
            AssertEqual(true, Theatre4ProgressEvaluateGate(ending, notEnding11),
                "17411 zero-count accepts an unseen ending");
            AssertEqual(false, Theatre4ProgressEvaluateGate(ending, ending11Once),
                "17411 positive count rejects an unseen ending");
            ending.Data.Endings[11] = 1;
            ending.SaveFixture();
            ending.Relog("progress-gate-ending-success");
            AssertEqual(true, Theatre4ProgressEvaluateGate(ending, notEnding11),
                "17411 zero-count accepts a durably reached ending");
            AssertEqual(true, Theatre4ProgressEvaluateGate(ending, ending11Once),
                "17411 positive count accepts a durably reached ending");
            ending.Data.Endings[11] = 0;
            ending.SaveFixture();
            ending.StartRun(1);
            ending.Adventure.FinishEventIds[31130] = 1;
            ending.Data.GlobalFinishEventIds[31130] = 1;
            ending.SaveFixture();
            ending.Call(nameof(Theatre4SettleAdventureRequest));
            AssertEqual(true, Theatre4ProgressEvaluateGate(ending, notEnding11),
                "17411 zero-count remains true after a durably settled failure ending");
            AssertEqual(false, Theatre4ProgressEvaluateGate(ending, ending11Once),
                "17411 positive count excludes a durably settled failure ending");

            GateConditionTable windCatcher = Theatre4ProgressGateRow(1020770);
            AssertEqual(false, Theatre4ProgressEvaluateGate(ending, windCatcher),
                "17411 blocks the tech gate before its ending");
            ending.Data.Endings[4] = 1;
            ending.SaveFixture();
            AssertEqual(true, Theatre4ProgressEvaluateGate(ending, windCatcher),
                "17411 accepts the authored ending count");
            ending.EnsureTechCoin(50);
            ending.Data.Endings[4] = 0;
            ending.SaveFixture();
            ending.Reject(nameof(Theatre4TechUnlockRequest),
                new Theatre4TechUnlockRequest { TechId = 16 },
                "Tech unlock before the authored ending");
            ending.Data.Endings[4] = 1;
            ending.SaveFixture();
            ending.EnsureTechCoin(50);
            ending.Call(nameof(Theatre4TechUnlockRequest),
                new Theatre4TechUnlockRequest { TechId = 16 });
            AssertEqual(true, ending.Data.Techs.Contains(16),
                "Tech unlock accepts the authored 17411 gate");
            AssertEqual(0, ending.Balance(96200),
                "Tech unlock spends the authored tech coin cost");
        }

        // 17416 rows are authored guide gates with no exposed request consumer.
        // The condition params are concrete MapIds, while InitializeMap takes a map-group key.
        // Resolve each concrete id through its authored group row and invoke the same
        // InitializeMapFromRow path the route uses; do not guess that the two ids are equal.
        using (Theatre4Case grids = new("progress-gate-grids"))
        {
            grids.StartRun(1);
            GateConditionTable[] rows = new[] { 1020682, 1020685, 1020801, 1020803, 1020804 }
                .Select(Theatre4ProgressGateRow).ToArray();
            List<Theatre4MapGroupTable> mapGroups = TableReaderV2.Parse<Theatre4MapGroupTable>();

            foreach (int mapId in rows.Select(row => row.Params[0]).Distinct())
            {
                Theatre4MapGroupTable mapGroup = mapGroups
                    .Where(candidate => candidate.MapId == mapId)
                    .OrderBy(candidate => candidate.MapGroup == mapId ? 0 : 1)
                    .ThenBy(candidate => candidate.Id)
                    .FirstOrDefault()
                    ?? throw new InvalidDataException($"17416 target map {mapId} has no authored map-group row.");
                Theatre4ChapterData generated = grids.Adventure.Chapters.FirstOrDefault(chapter =>
                    chapter.MapId == mapId)
                    ?? Theatre4ProgressionInvokeInitializeMapFromRow(grids, mapGroup);
                AssertEqual(mapGroup.MapId, generated.MapId,
                    $"17416 map-group {mapGroup.MapGroup} resolves to its authored map id");
                AssertEqual(mapGroup.MapGroup, generated.MapGroup,
                    $"17416 map-group {mapGroup.MapGroup} remains attached to its generated chapter");
            }

            // GrowFloor intentionally omits valid rectangle cells. Add only missing, in-bounds
            // authored targets to that generated snapshot, keeping their real MapId and GridId.
            foreach (GateConditionTable row in rows)
            {
                Theatre4ChapterData chapter = grids.Adventure.Chapters.FirstOrDefault(candidate =>
                    candidate.MapId == row.Params[0])
                    ?? throw new InvalidDataException($"17416 target map {row.Params[0]} was not generated.");
                Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
                    .SingleOrDefault(candidate => candidate.Id == chapter.MapId)
                    ?? throw new InvalidDataException($"17416 generated map {chapter.MapId} has no authored map row.");
                Theatre4ProgressionEnsureGrid(chapter, map, row.Params[1], row.Params[2]);
            }
            grids.SaveFixture();
            grids.Relog("progress-gate-grids-authored");

            foreach (GateConditionTable row in rows)
            {
                Theatre4GridData grid = grids.Grid(row.Params[0], row.Params[1], row.Params[2]);
                AssertEqual(false, Theatre4ProgressEvaluateGate(grids, row),
                    $"17416 condition {row.Id} rejects an unprocessed authored grid");
                grid.State = T4StateProcessed;
                grids.SaveFixture();
                grids.Relog($"progress-gate-grid-{row.Id}");
                AssertEqual(true, Theatre4ProgressEvaluateGate(grids, row),
                    $"17416 condition {row.Id} accepts its processed authored grid");
            }
        }

        // 17434 rows are map-location leaves for the authored DLC maps. Their Params[0] is
        // the concrete MapId, while the matching map-group rows are conditional alternatives
        // under MapGroup 60001 (zero base weight plus an authored AddWeight gate). The fresh
        // run does not satisfy those gates, so InitializeMap's weighted group-key lookup cannot
        // legally materialize these maps; use the already-selected authored row instead.
        using (Theatre4Case maps = new("progress-gate-dlc-maps"))
        {
            maps.StartRun(1);
            List<Theatre4MapGroupTable> mapGroups = TableReaderV2.Parse<Theatre4MapGroupTable>();
            foreach (int conditionId in new[] { 863023, 863024, 863025 })
            {
                GateConditionTable row = Theatre4ProgressGateRow(conditionId);
                AssertEqual(false, Theatre4ProgressEvaluateGate(maps, row),
                    $"17434 condition {conditionId} rejects before entering its authored map");
                int mapId = row.Params.FirstOrDefault();
                Theatre4MapGroupTable mapGroup = mapGroups
                    .Where(candidate => candidate.MapId == mapId)
                    .OrderBy(candidate => candidate.MapGroup == mapId ? 0 : 1)
                    .ThenBy(candidate => candidate.Id)
                    .FirstOrDefault()
                    ?? throw new InvalidDataException($"17434 target map {mapId} has no authored map-group row.");
                AssertEqual(true, mapGroup.MapId == mapId && mapGroup.MapGroup > 0
                    && mapGroup.BaseWeight.GetValueOrDefault() == 0
                    && mapGroup.AddWeight.GetValueOrDefault() > 0
                    && mapGroup.AddWeightCondition is > 0,
                    $"17434 condition {conditionId} resolves its conditional authored DLC map row");
                Theatre4ChapterData chapter =
                    Theatre4ProgressionInvokeInitializeMapFromRow(maps, mapGroup);
                AssertEqual(mapId, chapter.MapId,
                    $"17434 condition {conditionId} uses its authored map id");
                AssertEqual(true, Theatre4ProgressEvaluateGate(maps, row),
                    $"17434 condition {conditionId} accepts its authored current map");
                maps.SaveFixture();
                maps.Relog($"progress-gate-dlc-map-{conditionId}");
                AssertEqual(true, Theatre4ProgressEvaluateGate(maps, row),
                    $"17434 condition {conditionId} accepts its authored current map after relog");
            }
        }
    }

    private static void ValidateTheatre4ProgressionGridChains()
    {
        // EN table rows 30427 -> 30428 -> 30429. Row 30428 is IsEnd=1 but also
        // has NextEvent=30429; the native popup remains open on that successor.
        using (Theatre4Case endFlag = new("progress-grid-is-end"))
        {
            endFlag.StartRun(1);
            int mapId = endFlag.Adventure.Chapters[0].MapId;
            Theatre4GridData tile = Theatre4EventTile(endFlag, mapId, 30427, T4StateExplored);
            endFlag.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = tile.PosX, PosY = tile.PosY, Option = 0
            });
            Theatre4GridData first = endFlag.Grid(mapId, tile.PosX, tile.PosY);
            AssertEqual(30428, first.Event?.EventId ?? 0,
                "30427 records its local row before advancing to 30428");
            AssertEqual(T4StateExplored, first.State,
                "30427 successor remains an explored event tile");
            AssertEqual(1, endFlag.Adventure.FinishEventIds.GetValueOrDefault(30427),
                "30427 local finish counter increments once");
            Theatre4ProgressAssertFinishPush(endFlag, 30427, 1,
                "30427 emits local and global finish records");

            endFlag.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = tile.PosX, PosY = tile.PosY, Option = 0
            });
            Theatre4GridData second = endFlag.Grid(mapId, tile.PosX, tile.PosY);
            AssertEqual(30429, second.Event?.EventId ?? 0,
                "30428 advances through IsEnd to its authored successor");
            AssertEqual(T4StateExplored, second.State,
                "30428 IsEnd does not prematurely process a tile with a successor");
            AssertEqual(1, endFlag.Adventure.FinishEventIds.GetValueOrDefault(30428),
                "30428 local finish counter increments once");
            Theatre4ProgressAssertFinishPush(endFlag, 30428, 1,
                "30428 emits local and global finish records");
        }
        using (Theatre4Case localRows = new("progress-grid-local-rows"))
        {
            localRows.StartRun(1);
            int mapId = localRows.Adventure.Chapters[0].MapId;
            Dictionary<int, Theatre4EventTable> authored = TableReaderV2.Parse<Theatre4EventTable>()
                .Where(row => new[] { 30182, 31004, 31005, 31046, 31041 }.Contains(row.Id))
                .ToDictionary(row => row.Id);
            foreach (int rowId in new[] { 30182, 31004, 31005, 31046, 31041 })
            {
                Theatre4EventTable row = authored[rowId];
                Theatre4GridData tile = Theatre4EventTile(localRows, mapId, row.Id, T4StateExplored);
                Theatre4EventOptionTable? option = row.OptionGroupId is > 0
                    ? TableReaderV2.Parse<Theatre4EventOptionTable>().Single(candidate =>
                        candidate.GroupId == row.OptionGroupId
                        && !(candidate.OptionCondition > 0)
                        && !(candidate.OptionShowCondition > 0)
                        && candidate.OptionType is 3 or 4 or 5)
                    : null;
                int requestOption = option?.Id ?? 0;
                localRows.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
                {
                    MapId = mapId, PosX = tile.PosX, PosY = tile.PosY, Option = requestOption
                });
                Theatre4GridData after = localRows.Grid(mapId, tile.PosX, tile.PosY);
                AssertEqual(1, localRows.Adventure.FinishEventIds.GetValueOrDefault(row.Id),
                    $"Authored local event row {row.Id} records exactly once");
                Theatre4ProgressAssertFinishPush(localRows, row.Id, 1,
                    $"Authored local event row {row.Id} finish push");
                int expectedNext = option?.NextEvent ?? row.NextEvent ?? 0;
                if (expectedNext > 0)
                    AssertEqual(expectedNext, after.Event?.EventId ?? 0,
                        $"Authored local event row {row.Id} preserves its successor");
                else
                    AssertEqual(T4StateProcessed, after.State,
                        $"Authored local event row {row.Id} processes at chain end");
                if (row.Id == 30182)
                    AssertEqual(true, localRows.Adventure.CustomEffects.Any(effect => effect.EffectId == 1401),
                        "30182 applies its intermediate authored effect");
                if (row.Id == 31005)
                    AssertEqual(true, localRows.Pushes.Any(push => push.Name == nameof(NotifyTheatre4Reward)),
                        "31005 publishes its authored reward");
            }
        }


        // Root 31130 -> 31131 -> 31135 exercises intermediate effect/reward rows,
        // per-row local+global counters, unavailable option rejection, and active-run
        // login recovery.
        using (Theatre4Case globalIntermediate = new("progress-grid-global-intermediate"))
        {
            globalIntermediate.StartRun(1);
            int intermediateMap = globalIntermediate.Adventure.Chapters[0].MapId;
            GateConditionTable not60121 = Theatre4ProgressGateRow(1020703);
            Theatre4GridData intermediateTile = Theatre4EventTile(globalIntermediate, intermediateMap,
                60010, T4StateExplored);
            globalIntermediate.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = intermediateMap, PosX = intermediateTile.PosX, PosY = intermediateTile.PosY, Option = 0
            });
            AssertEqual(60121,
                globalIntermediate.Grid(intermediateMap, intermediateTile.PosX, intermediateTile.PosY)
                    .Event?.EventId ?? 0,
                "60010 advances to the authored 60121 branch");
            AssertEqual(1, globalIntermediate.Data.GlobalFinishEventIds.GetValueOrDefault(60010),
                "60010 global completion is recorded before its successor");
            AssertEqual(true, Theatre4ProgressEvaluateGate(globalIntermediate, not60121),
                "17415 60121 zero-count remains true before the branch completes");
            Theatre4ProgressAssertFinishPush(globalIntermediate, 60010, 1,
                "60010 intermediate global finish push");

            globalIntermediate.Relog("progress-grid-global-intermediate");
            AssertEqual(1, globalIntermediate.Data.GlobalFinishEventIds.GetValueOrDefault(60010),
                "17415 intermediate global count survives a run relog");
            globalIntermediate.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = intermediateMap, PosX = intermediateTile.PosX, PosY = intermediateTile.PosY, Option = 0
            });
            AssertEqual(1, globalIntermediate.Data.GlobalFinishEventIds.GetValueOrDefault(60121),
                "60121 global completion is recorded at its authored terminal row");
            AssertEqual(false, Theatre4ProgressEvaluateGate(globalIntermediate, not60121),
                "17415 60121 zero-count rejects its completed branch");
            Theatre4ProgressAssertFinishPush(globalIntermediate, 60121, 1,
                "60121 terminal global finish push");
        }

        using (Theatre4Case chain = new("progress-grid-root-chain"))
        {
            chain.StartRun(1);
            int mapId = chain.Adventure.Chapters[0].MapId;
            GateConditionTable globalNever = Theatre4ProgressGateRow(1020649);
            GateConditionTable globalOnce = Theatre4ProgressGateRow(1020650);
            GateConditionTable globalTwice = Theatre4ProgressGateRow(1020691);
            AssertEqual(true, Theatre4ProgressEvaluateGate(chain, globalNever),
                "17415 zero-count accepts an uncompleted global root");

            Theatre4GridData root = Theatre4EventTile(chain, mapId, 31130, T4StateExplored);
            chain.Reject(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = root.PosX, PosY = root.PosY, Option = 3113001
            }, "Unavailable 31130 option");
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = root.PosX, PosY = root.PosY, Option = 3113002
            });
            Theatre4GridData rootAfter = chain.Grid(mapId, root.PosX, root.PosY);
            AssertEqual(31131, rootAfter.Event?.EventId ?? 0,
                "31130 advances to the authored intermediate row");
            AssertEqual(1, chain.Adventure.FinishEventIds.GetValueOrDefault(31130),
                "31130 is recorded locally before its successor");
            AssertEqual(1, chain.Data.GlobalFinishEventIds.GetValueOrDefault(31130),
                "31130 global counter increments before its successor");
            Theatre4ProgressAssertFinishPush(chain, 31130, 1,
                "31130 finish push carries local and global state");

            chain.Relog("progress-grid-after-root");
            Theatre4GridData reloggedRoot = chain.Grid(mapId, root.PosX, root.PosY);
            AssertEqual(31131, reloggedRoot.Event?.EventId ?? 0,
                "Active chain relog preserves the authored successor");
            AssertEqual(1, chain.Adventure.FinishEventIds.GetValueOrDefault(31130),
                "Active chain relog preserves local finish counters");
            AssertEqual(1, chain.Data.GlobalFinishEventIds.GetValueOrDefault(31130),
                "Active chain relog preserves the intermediate global count");
            Theatre4EventTable intermediateRow = TableReaderV2.Parse<Theatre4EventTable>()
                .Single(row => row.Id == 31131);
            Theatre4AdventureData beforeReward = Theatre4ProgressCloneAdventure(chain.Adventure);
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = root.PosX, PosY = root.PosY, Option = 0
            });
            Theatre4GridData intermediate = chain.Grid(mapId, root.PosX, root.PosY);
            AssertEqual(31135, intermediate.Event?.EventId ?? 0,
                "31131 advances to its authored terminal dialogue");
            AssertEqual(1, chain.Adventure.FinishEventIds.GetValueOrDefault(31131),
                "31131 intermediate row is recorded locally");
            JObject intermediateReward = Theatre4ProgressPushJson(chain, nameof(NotifyTheatre4Reward),
                "31131 intermediate reward push");
            AssertEqual(true, (intermediateReward["Rewards"] as JArray)?.Count > 0,
                "31131 reward push carries an authored reward");
            Theatre4ProgressAssertModeRewards(chain, beforeReward, intermediateRow.RewardId,
                "31131 authored intermediate reward");
            Theatre4ProgressAssertFinishPush(chain, 31131, 1,
                "31131 finish push carries local and global counters");

            byte[] intermediateState = MessagePackSerializer.Serialize(chain.Adventure);
            chain.Relog("progress-grid-before-terminal");
            AssertEqual(true, intermediateState.SequenceEqual(MessagePackSerializer.Serialize(chain.Adventure)),
                "31131 mode reward survives the active-chain relog without regranting");
            Theatre4GridData beforeTerminal = chain.Grid(mapId, root.PosX, root.PosY);
            AssertEqual(31135, beforeTerminal.Event?.EventId ?? 0,
                "Second active chain relog preserves the terminal successor");
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = root.PosX, PosY = root.PosY, Option = 0
            });
            Theatre4GridData terminal = chain.Grid(mapId, root.PosX, root.PosY);
            AssertEqual(T4StateProcessed, terminal.State,
                "31135 processes the grid at the true chain end");
            AssertEqual(true, terminal.Event is null,
                "31135 clears the event only at the true chain end");
            AssertEqual(1, chain.Adventure.FinishEventIds.GetValueOrDefault(31135),
                "31135 terminal row is recorded locally");
            AssertEqual(1, chain.Data.GlobalFinishEventIds.GetValueOrDefault(31135),
                "31135 global counter increments once at chain completion");
            AssertEqual(1, chain.Data.GlobalFinishEventIds.GetValueOrDefault(31130),
                "31130 global counter is not re-added at chain completion");
            AssertEqual(false, Theatre4ProgressEvaluateGate(chain, globalNever),
                "17415 zero-count rejects a completed global root");
            AssertEqual(true, Theatre4ProgressEvaluateGate(chain, globalOnce),
                "17415 one-count accepts one completed global root");
            AssertEqual(false, Theatre4ProgressEvaluateGate(chain, globalTwice),
                "17415 two-count rejects a single completed global root");
            Theatre4ProgressAssertFinishPush(chain, 31135, 1,
                "31135 finish push carries local and global completion counters");

            chain.Reject(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = root.PosX, PosY = root.PosY, Option = 0
            }, "Repeated 31135 terminal event");

            Theatre4GridData secondRoot = Theatre4EventTile(chain, mapId, 31130, T4StateExplored);
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = secondRoot.PosX, PosY = secondRoot.PosY, Option = 3113002
            });
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = secondRoot.PosX, PosY = secondRoot.PosY, Option = 0
            });
            chain.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = secondRoot.PosX, PosY = secondRoot.PosY, Option = 0
            });
            AssertEqual(2, chain.Data.GlobalFinishEventIds.GetValueOrDefault(31130),
                "A second completed 31130 chain increments the global root exactly once");
            AssertEqual(true, Theatre4ProgressEvaluateGate(chain, globalTwice),
                "17415 two-count accepts two completed global roots");
        }
    }

    private static void ValidateTheatre4ProgressionFateChain()
    {
        // Trigger an authored FateEvent through daily settlement, then follow the
        // authored Event/Option successor graph. Synthetic map-story rows are not
        // used as fate fixtures.
        using (Theatre4Case fate = new("progress-fate-chain"))
        {
            // Difficulty-1 FateEvent group 1 is authored behind 17411[11,1]:
            // the account must have reached "Collapse of the Sand City" before
            // the first timeline day can select one of its rows.
            fate.Data.Endings[11] = 1;
            fate.SaveFixture();
            fate.StartRun(1);
            Theatre4FateTable timeline = TableReaderV2.Parse<Theatre4FateTable>()
                .Single(row => row.Difficulty == 1);
            int firstTriggerDay = timeline.TriggerDay.First(day => day > 0);
            for (int day = 0; day < firstTriggerDay && fate.Adventure.Fate is null; day++)
                fate.Call(nameof(Theatre4DailySettleRequest));

            Theatre4FateEventData first = fate.Adventure.Fate?.FateEvents.Single()
                ?? throw new InvalidDataException(
                    $"Authored fate did not spawn on timeline trigger day {firstTriggerDay}.");
            Theatre4EventData firstEvent = first.Event
                ?? throw new InvalidDataException("Authored fate spawned without an event.");
            HashSet<int> authoredStarts = TableReaderV2.Parse<Theatre4FateEventTable>()
                .Where(row => timeline.EventGroup.Contains(row.GroupId))
                .Select(row => row.EventId).ToHashSet();
            AssertEqual(timeline.Id, fate.Adventure.Fate!.Id,
                "Daily settlement spawns the authored difficulty-1 fate timeline");
            AssertEqual(true, authoredStarts.Contains(firstEvent.EventId),
                "Daily settlement spawns an authored FateEvent start row");

            Theatre4EventTable[] events = TableReaderV2.Parse<Theatre4EventTable>().ToArray();
            Theatre4EventOptionTable[] options = TableReaderV2.Parse<Theatre4EventOptionTable>().ToArray();
            int uniqueId = first.UniqueId;
            bool exercisedUnavailableOption = false;
            bool sawReward = false;
            int acceptedRows = 0;
            int firstSuccessor = 0;

            for (int step = 0; step < 64 && fate.Adventure.Fate is not null; step++)
            {
                Theatre4FateEventData pending = fate.Adventure.Fate.FateEvents.Single();
                Theatre4EventData current = pending.Event
                    ?? throw new InvalidDataException("Authored fate lost its active event.");
                Theatre4EventTable row = events.Single(candidate => candidate.Id == current.EventId);
                Theatre4EventOptionTable? option = null;
                if (row.OptionGroupId is > 0)
                {
                    option = Theatre4ProgressionSelectFateOption(row, events, options);
                    if (!exercisedUnavailableOption)
                    {
                        fate.Reject(nameof(Theatre4DoFateEventRequest),
                            new Theatre4DoFateEventRequest
                            {
                                FateEventUniqueId = uniqueId, Option = int.MaxValue
                            }, $"Unavailable authored fate option {row.Id}");
                        exercisedUnavailableOption = true;
                    }
                }

                int expectedNext = option?.NextEvent ?? row.NextEvent ?? 0;
                if (expectedNext <= 0
                    && (option?.NextEventGroupId is > 0 || row.NextEventGroup is > 0))
                    throw new InvalidDataException($"Fate row {row.Id} has an unhandled successor group.");
                Theatre4AdventureData beforeEvent = Theatre4ProgressCloneAdventure(fate.Adventure);
                int beforeLocal = fate.Adventure.FinishEventIds.GetValueOrDefault(row.Id);
                int beforeGlobal = fate.Data.GlobalFinishEventIds.GetValueOrDefault(row.Id);
                fate.Call(nameof(Theatre4DoFateEventRequest), new Theatre4DoFateEventRequest
                {
                    FateEventUniqueId = uniqueId, Option = option?.Id ?? 0
                });
                acceptedRows++;
                AssertEqual(beforeLocal + 1, fate.Adventure.FinishEventIds.GetValueOrDefault(row.Id),
                    $"Fate row {row.Id} records its local completion exactly once");
                Theatre4ProgressAssertFinishPush(fate, row.Id, beforeGlobal + 1,
                    $"Fate row {row.Id} finish push");
                AssertEqual(beforeGlobal + 1, fate.Data.GlobalFinishEventIds.GetValueOrDefault(row.Id),
                    $"Fate row {row.Id} records global progress exactly once");
                if (row.RewardId.Any(rewardId => rewardId > 0))
                {
                    Theatre4ProgressAssertModeRewards(fate, beforeEvent, row.RewardId,
                        $"Fate row {row.Id} authored reward");
                    sawReward = true;
                }
                if (step == 0)
                {
                    firstSuccessor = expectedNext;
                    if (firstSuccessor > 0)
                    {
                        byte[] firstStepState = MessagePackSerializer.Serialize(fate.Adventure);
                        fate.Relog("progress-fate-intermediate");
                        AssertEqual(firstSuccessor,
                            fate.Adventure.Fate?.FateEvents.Single().Event?.EventId ?? 0,
                            "Authored fate successor survives active-run relog");
                        AssertEqual(beforeGlobal + 1,
                            fate.Data.GlobalFinishEventIds.GetValueOrDefault(row.Id),
                            "Authored fate global progress survives active-run relog");
                        AssertEqual(true, firstStepState.SequenceEqual(
                                MessagePackSerializer.Serialize(fate.Adventure)),
                            "Authored fate mode reward survives active-run relog without regranting");
                    }
                }
                if (expectedNext > 0)
                    AssertEqual(expectedNext, fate.Adventure.Fate?.FateEvents.Single().Event?.EventId ?? 0,
                        $"Fate row {row.Id} preserves its authored successor");
                else
                    AssertEqual(true, fate.Adventure.Fate is null,
                        $"Fate row {row.Id} removes the completed fate at chain end");
            }

            AssertEqual(true, acceptedRows > 1, "Authored fate proof traverses an intermediate chain row");
            AssertEqual(true, exercisedUnavailableOption,
                "Authored fate proof rejects an unavailable option before accepting one");
            AssertEqual(true, sawReward, "Authored fate proof observes a table reward row");
            AssertEqual(true, fate.Adventure.Fate is null,
                "Authored fate chain removes its exact completed fate item");
            byte[] terminalState = MessagePackSerializer.Serialize(fate.Adventure);
            fate.Relog("progress-fate-terminal");
            AssertEqual(true, terminalState.SequenceEqual(MessagePackSerializer.Serialize(fate.Adventure)),
                "Authored fate terminal relog does not regrant its mode rewards");
            fate.Reject(nameof(Theatre4DoFateEventRequest), new Theatre4DoFateEventRequest
            {
                FateEventUniqueId = uniqueId, Option = 0
            }, "Repeated authored terminal fate event");
        }
    }

    private static void ValidateTheatre4ProgressionRecovery()
    {
        // Legacy shop snapshots can occur in the active run, traceback snapshots,
        // and pending settle snapshot. Recovery only repairs the state marker; it
        // must not reroll an authored shelf or fire the Type217 entry reward again.
        using (Theatre4Case shops = new("progress-shop-recovery"))
        {
            shops.StartRun(1);
            Theatre4EffectTable entry = TableReaderV2.Parse<Theatre4EffectTable>().First(row =>
                row.Type == 217 && row.Params.Count >= 3 && row.Params[0] == T4AssetGold);
            Theatre4SeedEffect(shops, entry.Id);

            Theatre4ShopTable[] shopRows = TableReaderV2.Parse<Theatre4ShopTable>().ToArray();
            Theatre4ShopGoodsTable[] goodsRows = TableReaderV2.Parse<Theatre4ShopGoodsTable>().ToArray();
            Theatre4ShopGroupTable authoredGroup = TableReaderV2.Parse<Theatre4ShopGroupTable>().First(group =>
            {
                Theatre4ShopTable shop = shopRows.Single(row => row.Id == group.ShopId);
                return shop.GoodsGroupId.Any(goodsGroup => goodsRows.Any(goods =>
                    goods.GroupId == goodsGroup && goods.GoodsType == T4AssetItem
                    && goods.GoodsId is > 0 && goods.Id != goods.GoodsId
                    && !goods.ConditionId.Where(id => id > 0).Any()));
            });
            Theatre4ShopTable authoredShop = shopRows.Single(row => row.Id == authoredGroup.ShopId);
            Theatre4ShopGoodsTable[] authoredShelf = goodsRows
                .Where(goods => authoredShop.GoodsGroupId.Contains(goods.GroupId)
                    && goods.GoodsType == T4AssetItem && goods.GoodsId is > 0
                    && goods.Id != goods.GoodsId
                    && !goods.ConditionId.Where(id => id > 0).Any())
                .Take(6).ToArray();
            AssertEqual(true, authoredShelf.Length > 0,
                "Legacy shop recovery fixture has an unconditional authored shelf");

            Theatre4ChapterData chapter = shops.Adventure.Chapters[0];
            Theatre4GridData host = chapter.Grids
                .Where(grid => grid.State == T4StateUnknown
                    && grid.Type is not (T4GridBoss or T4GridStart or T4GridBuilding
                        or T4GridNothing or T4GridBlank))
                .OrderByDescending(grid => grid.PosX + grid.PosY).FirstOrDefault()
                ?? throw new InvalidDataException("Legacy shop recovery has no unknown traversable tile.");
            host.Type = T4GridShop;
            host.State = T4StateExplored;
            host.ContentGroup = authoredGroup.ShopGroupId;
            host.ContentId = authoredShop.Id;
            host.Fight = null;
            host.Event = null;
            host.Building = null;
            Theatre4ShopData expectedShop = new()
            {
                ShopId = authoredShop.Id,
                RefreshTimes = 0,
                FreeBuyTimes = 0,
                Discount = 0,
                Goods = authoredShelf.Select(goods => new Theatre4ShopGoodsData
                {
                    GoodsId = goods.Id,
                    Stock = 1,
                    IsFree = false
                }).ToList()
            };
            host.Shop = expectedShop;
            shops.Adventure.Gold = 1234;
            Theatre4AdventureData snapshot = Theatre4ProgressCloneAdventure(shops.Adventure);
            shops.State.TracebackSnapshots[0] = Theatre4ProgressCloneAdventure(snapshot);
            shops.State.PendingSettleAdventure = Theatre4ProgressCloneAdventure(snapshot);
            shops.SaveFixture();

            int gold = shops.Adventure.Gold;
            byte[] legacyAdventure = MessagePackSerializer.Serialize(shops.Adventure);
            shops.Players.ThrowOnReplaceOne = true;
            try
            {
                bool failed = false;
                try
                {
                    RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"),
                        "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        [typeof(Session)]).Invoke(null, [shops.Session]);
                }
                catch (TargetInvocationException)
                {
                    failed = true;
                }
                AssertEqual(true, failed, "Failed legacy shop recovery surfaces persistence failure");
            }
            finally
            {
                shops.Players.ThrowOnReplaceOne = false;
            }
            AssertEqual(true, legacyAdventure.SequenceEqual(MessagePackSerializer.Serialize(shops.Adventure)),
                "Failed legacy shop recovery leaves the active snapshot unchanged until relog");

            shops.Relog("progress-legacy-shop-snapshots");
            AssertEqual(gold, shops.Adventure.Gold,
                "Legacy shop recovery does not rerun a Type217 entry reward");
            Theatre4ProgressAssertShopSnapshot(shops.Adventure, chapter.MapId, host.PosX, host.PosY,
                expectedShop, "active legacy shop snapshot");
            Theatre4ProgressAssertShopSnapshot(shops.State.TracebackSnapshots[0], chapter.MapId,
                host.PosX, host.PosY, expectedShop, "traceback legacy shop snapshot");
            Theatre4ProgressAssertShopSnapshot(shops.State.PendingSettleAdventure!, chapter.MapId,
                host.PosX, host.PosY, expectedShop, "pending legacy shop snapshot");
        }

        // Normalize only authored failure-ending completion records. Legitimate
        // pass-type-2 wins and unknown ids stay durable, while a real failure
        // settlement remains available through the terminal presentation/relog push.
        using (Theatre4Case legacy = new("progress-ending-normalization"))
        {
            Theatre4EndingTable[] failureRows = TableReaderV2.Parse<Theatre4EndingTable>()
                .Where(row => row.PassType == 1).Take(3).ToArray();
            Theatre4EndingTable[] successRows = TableReaderV2.Parse<Theatre4EndingTable>()
                .Where(row => row.PassType == 2).Take(2).ToArray();
            AssertEqual(true, failureRows.Length > 0 && successRows.Length > 0,
                "Ending normalization has authored failure and success rows");
            foreach (Theatre4EndingTable row in failureRows) legacy.Data.Endings[row.Id] = 3;
            foreach (Theatre4EndingTable row in successRows) legacy.Data.Endings[row.Id] = 2;
            legacy.Data.Endings[int.MaxValue] = 4;
            Dictionary<int, long> items = legacy.ItemCounts();
            legacy.SaveFixture();
            legacy.Relog("progress-ending-normalization");
            foreach (Theatre4EndingTable row in failureRows)
                AssertEqual(false, legacy.Data.Endings.ContainsKey(row.Id),
                    $"Authored failure ending {row.Id} is removed on login");
            foreach (Theatre4EndingTable row in successRows)
                AssertEqual(2, legacy.Data.Endings.GetValueOrDefault(row.Id),
                    $"Legitimate success ending {row.Id} is retained on login");
            AssertEqual(4, legacy.Data.Endings.GetValueOrDefault(int.MaxValue),
                "Unknown ending history is not destructively normalized");
            AssertEqual(true, items.OrderBy(pair => pair.Key).SequenceEqual(
                legacy.ItemCounts().OrderBy(pair => pair.Key)),
                "Ending normalization never regrants account rewards");
        }

        using (Theatre4Case failure = new("progress-failure-terminal"))
        {
            failure.StartRun(1);
            failure.Adventure.FinishEventIds[31130] = 1;
            failure.Data.GlobalFinishEventIds[31130] = 1;
            failure.SaveFixture();
            failure.Call(nameof(Theatre4SettleAdventureRequest));
            NotifyTheatre4AdventureSettle first = failure.EndingPush("failure terminal");
            Theatre4EndingTable ending = TableReaderV2.Parse<Theatre4EndingTable>()
                .Single(row => row.Id == first.SettleData!.EndingId);
            AssertEqual(1, ending.PassType,
                "Failure settlement retains an authored failure ending presentation");
            AssertEqual(1, first.AdventureData!.FinishEventIds.GetValueOrDefault(31130),
                "Failure settlement retains the terminal event snapshot");
            AssertEqual(1, failure.Data.GlobalFinishEventIds.GetValueOrDefault(31130),
                "Failure settlement retains the terminal global snapshot");
            NotifyTheatre4AdventureSettle recovered = failure.RelogEnding("failure terminal recovery");
            AssertEqual(true, MessagePackSerializer.Serialize(first.SettleData).SequenceEqual(
                MessagePackSerializer.Serialize(recovered.SettleData)),
                "Failure relog replays the same settlement summary");
            AssertEqual(true, MessagePackSerializer.Serialize(first.AdventureData).SequenceEqual(
                MessagePackSerializer.Serialize(recovered.AdventureData)),
                "Failure relog replays the same terminal snapshot without recounting events");
        }
    }
    private static void ValidateTheatre4ProgressionTechEffects()
    {
        Theatre4TechTable prerequisite = TableReaderV2.Parse<Theatre4TechTable>()
            .Single(row => row.Id == 16);
        Theatre4TechTable rewindTech = TableReaderV2.Parse<Theatre4TechTable>()
            .Single(row => row.Id == 17);
        Theatre4EffectGroupTable rewindGroup = TableReaderV2.Parse<Theatre4EffectGroupTable>()
            .Single(row => row.Id == rewindTech.EffectGroupId);
        int[] authoredEffects = (rewindGroup.Effects ?? []).Where(effectId => effectId > 0).ToArray();
        AssertEqual(1, authoredEffects.Length,
            "Tech 17 uses exactly one authored effect-group member");
        Theatre4EffectTable rewindEffect = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Id == authoredEffects[0]);
        AssertEqual(423, rewindEffect.Type ?? 0,
            "Tech 17's authored effect is the time-rewind skill");

        using (Theatre4Case unowned = new("progress-tech-effect-unowned"))
        {
            unowned.StartRun(1);
            AssertEqual(0, unowned.Adventure.CustomEffects.Count(effect =>
                    authoredEffects.Contains(effect.EffectId)),
                "An unowned tech contributes no authored time-rewind effect to a new run");
            unowned.Reject(nameof(Theatre4UseSkillEffectRequest),
                new Theatre4UseSkillEffectRequest { EffectId = rewindEffect.Id },
                "Time-rewind skill is unavailable before the tech unlock");
        }

        using (Theatre4Case owned = new("progress-tech-effect-owned"))
        {
            owned.Data.Endings[4] = 1;
            owned.EnsureTechCoin(prerequisite.Cost + rewindTech.Cost);
            owned.SaveFixture();
            owned.Call(nameof(Theatre4TechUnlockRequest),
                new Theatre4TechUnlockRequest { TechId = prerequisite.Id });
            owned.Call(nameof(Theatre4TechUnlockRequest),
                new Theatre4TechUnlockRequest { TechId = rewindTech.Id });
            owned.Relog("progress-tech-effect-account");
            AssertEqual(true, owned.Data.Techs.Contains(rewindTech.Id),
                "Unlocked time-rewind tech survives account relog");

            owned.StartRun(1);
            AssertEqual(authoredEffects.Length,
                owned.Adventure.CustomEffects.Count(effect => authoredEffects.Contains(effect.EffectId)),
                "A new run materializes the authored tech effect-group multiplicity");
            int tracebackDays = owned.Adventure.Chapters[0].MaxTracebackDays;
            while (owned.Adventure.Days < tracebackDays)
                owned.Call(nameof(Theatre4DailySettleRequest));
            Theatre4AddAsset(owned, T4AssetTimeBack, 0, 1);
            int daysBefore = owned.Adventure.Days;
            owned.Call(nameof(Theatre4UseSkillEffectRequest),
                new Theatre4UseSkillEffectRequest { EffectId = rewindEffect.Id });
            AssertEqual(0, owned.Asset(T4AssetTimeBack, 0),
                "Authored time-rewind skill consumes one available rewind charge");
            AssertEqual(true, owned.Adventure.Days < daysBefore,
                "Unlocked authored time-rewind effect changes real handler behavior");
            owned.Relog("progress-tech-effect-active-run");
            AssertEqual(authoredEffects.Length,
                owned.Adventure.CustomEffects.Count(effect => authoredEffects.Contains(effect.EffectId)),
                "Materialized tech effect survives active-run relog without duplication");
        }
    }

    private static Theatre4EventOptionTable Theatre4ProgressionSelectFateOption(
        Theatre4EventTable row, IReadOnlyList<Theatre4EventTable> events,
        IReadOnlyList<Theatre4EventOptionTable> options)
    {
        return options
            .Where(candidate => candidate.GroupId == row.OptionGroupId
                && !(candidate.OptionCondition > 0)
                && !(candidate.OptionShowCondition > 0))
            .OrderByDescending(candidate => Theatre4ProgressionFatePathHasReward(
                candidate.NextEvent, events, options, []))
            .FirstOrDefault()
            ?? throw new InvalidDataException($"Fate row {row.Id} has no visible authored option.");
    }

    private static bool Theatre4ProgressionFatePathHasReward(int? next,
        IReadOnlyList<Theatre4EventTable> events,
        IReadOnlyList<Theatre4EventOptionTable> options, HashSet<int> seen)
    {
        if (next is not > 0 || !seen.Add(next.Value)) return false;
        Theatre4EventTable row = events.Single(candidate => candidate.Id == next.Value);
        if (row.RewardId.Any(rewardId => rewardId > 0)) return true;
        if (row.OptionGroupId is > 0)
            return options
                .Where(candidate => candidate.GroupId == row.OptionGroupId
                    && !(candidate.OptionCondition > 0)
                    && !(candidate.OptionShowCondition > 0))
                .Any(candidate => Theatre4ProgressionFatePathHasReward(
                    candidate.NextEvent, events, options, new HashSet<int>(seen)));
        return Theatre4ProgressionFatePathHasReward(
            row.NextEvent, events, options, seen);
    }


    private static GateConditionTable Theatre4ProgressGateRow(int id) =>
        TableReaderV2.Parse<GateConditionTable>().Single(row => row.Id == id);

    private static GateConditionTable Theatre4ProgressSyntheticGate(int type, params int[] parameters) =>
        new() { Type = type, Params = parameters.ToList() };

    private static bool Theatre4ProgressEvaluateGate(Theatre4Case test, GateConditionTable row)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module");
        if (row.Id == 0)
        {
            Delegate predicate = (Delegate)(RequiredMethod(module, "CompileGateLeaf",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(GateConditionTable)])
                .Invoke(null, [row]) ?? throw new InvalidDataException($"Gate {row.Type} did not compile."));
            return predicate.DynamicInvoke(test.Session.player, test.State) is true;
        }
        return (bool)(RequiredMethod(module, "IsConditionMet",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Session), typeof(int)]).Invoke(null, [test.Session, row.Id])
            ?? throw new InvalidDataException($"Gate {row.Id} returned no result."));
    }
    private static Theatre4ChapterData Theatre4ProgressionInvokeInitializeMapFromRow(
        Theatre4Case test, Theatre4MapGroupTable row)
    {
        object mutation = Theatre4NewMutation(test);
        Type mutationType = mutation.GetType();
        Type module = mutationType.DeclaringType!;
        object? chapter = RequiredMethod(module, "InitializeMapFromRow",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                [mutationType, typeof(Theatre4MapGroupTable)])
            .Invoke(null, [mutation, row]);
        test.AdoptMutationState(mutation);
        return (Theatre4ChapterData?)chapter
            ?? throw new InvalidDataException("InitializeMapFromRow returned no chapter.");
    }

    private static Theatre4GridData Theatre4ProgressionEnsureGrid(
        Theatre4ChapterData chapter, Theatre4MapTable map, int x, int y)
    {
        if (x < 0 || y < 0 || x >= map.SizeX || y >= map.SizeY)
            throw new InvalidDataException(
                $"17416 target {chapter.MapId}/{x}/{y} is outside authored bounds {map.SizeX}x{map.SizeY}.");
        Theatre4GridData? existing = chapter.Grids.FirstOrDefault(grid =>
            grid.PosX == x && grid.PosY == y);
        if (existing is not null)
        {
            if (existing.Type is T4GridNothing or T4GridStart)
                throw new InvalidDataException(
                    $"17416 target {chapter.MapId}/{x}/{y} is an ineligible generated grid.");
            return existing;
        }
        if (map.HiddenId.Any(id => id > 0))
            throw new InvalidDataException(
                $"17416 target {chapter.MapId}/{x}/{y} is behind authored hidden-map metadata.");
        int gridId = 10_000 + x * 100 + y;
        if (chapter.Grids.Any(grid => grid.GridId == gridId))
            throw new InvalidDataException($"17416 target {chapter.MapId}/{x}/{y} conflicts with GridId {gridId}.");
        Theatre4GridData template = chapter.Grids.FirstOrDefault(grid =>
            grid.Type is not (T4GridStart or T4GridBoss))
            ?? throw new InvalidDataException($"17416 map {chapter.MapId} has no generated grid template.");
        Theatre4GridData authored = new()
        {
            GridId = gridId,
            Color = template.Color,
            ColorResource = template.ColorResource,
            Type = T4GridEmpty,
            PosX = x,
            PosY = y,
            State = T4StateUnknown
        };
        chapter.Grids.Add(authored);
        return authored;
    }

    private static Theatre4AdventureData Theatre4ProgressCloneAdventure(Theatre4AdventureData source) =>
        MessagePackSerializer.Deserialize<Theatre4AdventureData>(MessagePackSerializer.Serialize(source));

    private static void Theatre4ProgressAssertModeRewards(Theatre4Case test,
        Theatre4AdventureData before, IEnumerable<int> rewardIds, string context)
    {
        Theatre4RewardTable[] expected = rewardIds.Where(id => id > 0)
            .Select(id => TableReaderV2.Parse<Theatre4RewardTable>().Single(row => row.Id == id))
            .ToArray();
        Theatre4AssetData[] actual = test.Pushes
            .Where(push => push.Name == nameof(NotifyTheatre4Reward))
            .SelectMany(push => MessagePackSerializer.Deserialize<NotifyTheatre4Reward>(push.Content).Rewards)
            .ToArray();
        AssertEqual(expected.Length, actual.Length, $"{context}: reward push count");
        for (int index = 0; index < expected.Length; index++)
        {
            Theatre4RewardTable authored = expected[index];
            Theatre4AssetData asset = actual[index];
            AssertEqual(authored.Id, asset.RewardId, $"{context}: reward row {index}");
            AssertEqual(authored.ElementType, asset.Type, $"{context}: reward type {authored.Id}");
            AssertEqual(authored.ElementId ?? 0, asset.Id, $"{context}: reward id {authored.Id}");
            AssertEqual(authored.ElementCount, asset.Num, $"{context}: reward count {authored.Id}");
        }

        foreach (IGrouping<(int Type, int Id), Theatre4RewardTable> group in expected.GroupBy(
            row => (row.ElementType, row.ElementId ?? 0)))
        {
            long amount = group.Sum(row => (long)row.ElementCount);
            long beforeUnits = Theatre4ProgressModeUnits(before, group.Key.Type, group.Key.Id);
            long afterUnits = Theatre4ProgressModeUnits(test.Adventure, group.Key.Type, group.Key.Id);
            AssertEqual(beforeUnits + amount, afterUnits,
                $"{context}: mode asset {group.Key.Type}/{group.Key.Id} is granted exactly once");

            if (group.Key.Type == 1)
            {
                int beforeOffers = before.Transactions.Count(transaction =>
                    transaction.Type == 2 && transaction.ConfigId == group.Key.Id);
                int afterOffers = test.Adventure.Transactions.Count(transaction =>
                    transaction.Type == 2 && transaction.ConfigId == group.Key.Id);
                AssertEqual(beforeOffers + checked((int)amount), afterOffers,
                    $"{context}: item-box reward opens its authored pending offer");
            }
            else if (group.Key.Type == 3)
            {
                int beforeOffers = before.Transactions.Count(transaction =>
                    transaction.Type == 1 && transaction.ConfigId == group.Key.Id);
                int afterOffers = test.Adventure.Transactions.Count(transaction =>
                    transaction.Type == 1 && transaction.ConfigId == group.Key.Id);
                AssertEqual(true, afterOffers >= beforeOffers,
                    $"{context}: recruit reward preserves pending recruit offers");
                if (beforeOffers == 0)
                    AssertEqual(true, afterOffers > 0,
                        $"{context}: recruit reward creates its pending recruit offer");
            }
        }
    }

    private static long Theatre4ProgressModeUnits(Theatre4AdventureData adventure, int type, int id) =>
        type switch
        {
            1 => adventure.ItemBoxs.LongCount(itemId => itemId == id),
            T4AssetItem => adventure.Items.Concat(adventure.Props)
                .LongCount(item => item.ItemId == id) + adventure.WaitItems.LongCount(itemId => itemId == id),
            3 => adventure.RecruitTickets.LongCount(ticketId => ticketId == id)
                + adventure.Transactions.LongCount(transaction =>
                    transaction.Type == 1 && transaction.ConfigId == id),
            T4AssetGold => adventure.Gold,
            5 => adventure.Hp,
            6 => adventure.Prosperity,
            7 => adventure.Colors.Single(color => color.Color == id).Level,
            8 => adventure.Colors.Single(color => color.Color == id).Resource,
            T4AssetColorPoint => adventure.Colors.Single(color => color.Color == id).Point,
            T4AssetBuildPoint => adventure.Bp,
            T4AssetActionPoint => adventure.Ap,
            T4AssetColorDailyResource => adventure.Colors.Single(color => color.Color == id).DailyResource,
            13 => adventure.ItemLimit,
            14 => adventure.SettleBpExp,
            15 => adventure.AwakeningPoint,
            16 => adventure.Colors.Single(color => color.Color == id).PointCanCost,
            T4AssetTimeBack => adventure.TracebackPoint,
            _ => throw new InvalidDataException($"Unsupported authored Theatre4 reward asset type {type}.")
        };

    private static JObject Theatre4ProgressPushJson(Theatre4Case test, string name, string context) =>
        JObject.Parse(MessagePackSerializer.ConvertToJson(test.Pushes.Single(push => push.Name == name).Content));

    private static void Theatre4ProgressAssertFinishPush(Theatre4Case test, int eventId, int? globalCount,
        string context)
    {
        Packet.Push push = test.Pushes.SingleOrDefault(candidate =>
            candidate.Name == "NotifyTheatre4FinishEventRecord")
            ?? throw new InvalidDataException($"{context}: missing NotifyTheatre4FinishEventRecord push");
        JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
        AssertEqual(true, Theatre4ProgressPushMapCount(body, "FinishEventIds", eventId) >= 1,
            $"{context}: local event {eventId} is pushed");
        if (globalCount is int expected)
            AssertEqual(expected, Theatre4ProgressPushMapCount(body, "GlobalFinishEventIds", eventId),
                $"{context}: global event {eventId} count is pushed");
    }

    private static int Theatre4ProgressPushMapCount(JObject body, string mapName, int id)
    {
        if (body[mapName] is not JObject map) return 0;
        return map[id.ToString(CultureInfo.InvariantCulture)]?.Value<int>() ?? 0;
    }

    private static void Theatre4ProgressAssertShopSnapshot(Theatre4AdventureData adventure, int mapId,
        int posX, int posY, Theatre4ShopData expectedShop, string context)
    {
        Theatre4GridData grid = adventure.Chapters.Single(chapter => chapter.MapId == mapId)
            .Grids.Single(candidate => candidate.PosX == posX && candidate.PosY == posY);
        AssertEqual(T4GridShop, grid.Type, $"{context}: normalized grid remains a shop");
        AssertEqual(T4StateProcessed, grid.State, $"{context}: processed shop reaches the native shop step");
        Theatre4ShopData actualShop = grid.Shop
            ?? throw new InvalidDataException($"{context}: normalized shop payload is missing");
        AssertEqual(grid.ContentId, actualShop.ShopId, $"{context}: shop content id is usable");

        Theatre4ShopTable authoredShop = TableReaderV2.Parse<Theatre4ShopTable>()
            .Single(row => row.Id == actualShop.ShopId);
        AssertEqual(true, TableReaderV2.Parse<Theatre4ShopGroupTable>().Any(row =>
            row.ShopGroupId == grid.ContentGroup && row.ShopId == actualShop.ShopId),
            $"{context}: shop group is authored");
        HashSet<int> authoredGroups = authoredShop.GoodsGroupId.Where(id => id > 0).ToHashSet();
        Theatre4ShopGoodsTable[] authoredGoods = TableReaderV2.Parse<Theatre4ShopGoodsTable>()
            .Where(row => authoredGroups.Contains(row.GroupId)
                && row.GoodsType == T4AssetItem && row.GoodsId is > 0
                && row.Id != row.GoodsId && !row.ConditionId.Where(id => id > 0).Any())
            .ToArray();
        AssertEqual(true, actualShop.Goods.Count > 0,
            $"{context}: normalized shop opens with a usable shelf");
        AssertEqual(true, actualShop.Goods.All(goods =>
            authoredGoods.Any(row => row.Id == goods.GoodsId)),
            $"{context}: normalized shelf contains only authored goods");
        AssertEqual(expectedShop.ShopId, actualShop.ShopId, $"{context}: shop id survives recovery");
        AssertEqual(expectedShop.RefreshTimes, actualShop.RefreshTimes,
            $"{context}: refresh count survives recovery");
        AssertEqual(expectedShop.FreeBuyTimes, actualShop.FreeBuyTimes,
            $"{context}: free-buy count survives recovery");
        AssertEqual(expectedShop.Discount, actualShop.Discount,
            $"{context}: discount survives recovery");
        AssertEqual(true, expectedShop.Goods.Select(goods => (goods.GoodsId, goods.Stock, goods.IsFree))
                .SequenceEqual(actualShop.Goods.Select(goods => (goods.GoodsId, goods.Stock, goods.IsFree))),
            $"{context}: authored shelf survives recovery without reroll");
    }
}

