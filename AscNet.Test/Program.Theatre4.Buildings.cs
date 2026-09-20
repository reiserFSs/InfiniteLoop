using System.Reflection;

using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.theatre4.theatre4map;
using MessagePack;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private const int T4GridHurdle = 3;
    // Awakening Tundra building proof is deliberately request/state based. The building and effect
    // rows below are selected from the installed tables; no retail packet payload is embedded.
    private static void ValidateTheatre4BuildingAuditCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        _ = GetRegisteredRequestHandler(nameof(Theatre4UseSkillEffectRequest));

        ValidateTheatre4BuildingAuditConstruction();
        ValidateTheatre4BuildingAuditAutoExplore();
        ValidateTheatre4BuildingAuditSupplyAndResidence();
        ValidateTheatre4BuildingAuditCombination();
        ValidateTheatre4BuildingAuditTower();
        ValidateTheatre4BuildingAuditDepotRangeEffects();
        ValidateTheatre4BuildingAuditModifications();
        ValidateTheatre4BuildingAuditCostEffects();
        ValidateTheatre4BuildingAuditSweepEffects();
        ValidateTheatre4BuildingAuditShopAndTreasureThief();
    }

    private static void ValidateTheatre4BuildingAuditConstruction()
    {
        Theatre4BuildingTable[] buildings = TableReaderV2.Parse<Theatre4BuildingTable>()
            .Where(row => row.Type is >= 1 and <= 5).OrderBy(row => row.Type).ToArray();
        AssertEqual(5, buildings.Length, "All five authored building types are available");

        foreach (Theatre4BuildingTable building in buildings)
        {
            using Theatre4Case test = new($"building-type-{building.Type}");
            test.StartRun(1);
            Theatre4EffectTable skill = Theatre4BuildingAuditBuildEffect(building);
            Theatre4SeedEffect(test, skill.Id);
            Theatre4GridData target = Theatre4BuildingAuditEmpty(test, test.Adventure.Chapters[0].MapId);
            int cost = Math.Max(0, skill.SkillCostCount ?? 0);
            int bpBefore = cost + 10;
            test.SetBuildPoint(bpBefore);

            int mapId = test.Adventure.Chapters[0].MapId;
            int buildTransportId = test.NextPacketId;
            JObject firstResponse = test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = skill.Id,
                // EN XTheatre4MapSubControl:GetMapBuildParams returns [MapId, PosY, PosX].
                Params = [mapId, target.PosY, target.PosX]
            });

            Theatre4GridData live = test.Grid(mapId, target.PosX, target.PosY);
            AssertEqual(T4GridBuilding, live.Type, $"Building type {building.Type} installs on an empty tile");
            AssertEqual(building.Id, live.Building!.BuildingId,
                $"Building type {building.Type} persists the authored building id");
            AssertEqual(building.Type, live.Building.BuildingType,
                $"Building type {building.Type} persists the authored building type");
            AssertEqual(bpBefore - cost, test.Asset(T4AssetBuildPoint, 0),
                $"Building type {building.Type} charges its authored construction cost");

            // Settled receipts are transport-scoped; replay the same packet before relog.
            byte[] settledState = test.State.ToBson();
            int settledBuildPoint = test.Asset(T4AssetBuildPoint, 0);
            JObject replayResponse = test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = skill.Id,
                Params = [mapId, target.PosY, target.PosX]
            }, transportId: buildTransportId);
            AssertEqual(true, JToken.DeepEquals(firstResponse, replayResponse),
                $"Building type {building.Type} exact retry replays the settled response");
            AssertEqual(true, settledState.SequenceEqual(test.State.ToBson()),
                $"Building type {building.Type} exact retry preserves settled state");
            AssertEqual(settledBuildPoint, test.Asset(T4AssetBuildPoint, 0),
                $"Building type {building.Type} exact retry does not debit again");

            test.Relog($"building type {building.Type}");
            Theatre4GridData relogged = test.Grid(mapId, target.PosX, target.PosY);
            AssertEqual(building.Id, relogged.Building!.BuildingId,
                $"Building type {building.Type} survives relog");

            List<int> occupied = [relogged.GridId];
            if (building.MaxCountInChapter is int max and > 0)
            {
                for (int built = 1; built < max; built++)
                {
                    Theatre4GridData next = Theatre4BuildingAuditEmpty(test, mapId, occupied);
                    test.SetBuildPoint(cost + 10);
                    test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
                    {
                        EffectId = skill.Id,
                        Params = [mapId, next.PosY, next.PosX]
                    });
                    occupied.Add(next.GridId);
                }

                Theatre4GridData overCap = Theatre4BuildingAuditEmpty(test, mapId, occupied);
                test.SetBuildPoint(cost + 10);
                int beforeCapBuildPoint = test.Asset(T4AssetBuildPoint, 0);
                byte[] beforeCapState = test.State.ToBson();
                test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
                {
                    EffectId = skill.Id,
                    Params = [mapId, overCap.PosY, overCap.PosX]
                }, success: false);
                AssertEqual(beforeCapBuildPoint, test.Asset(T4AssetBuildPoint, 0),
                    $"Building type {building.Type} cap rejection does not debit");
                AssertEqual(true, beforeCapState.SequenceEqual(test.State.ToBson()),
                    $"Building type {building.Type} cap rejection preserves state");
            }

            Theatre4BuildingTable alternateBuilding = buildings.First(candidate => candidate.Id != building.Id);
            Theatre4EffectTable alternateSkill = Theatre4BuildingAuditBuildEffect(alternateBuilding);
            Theatre4SeedEffect(test, alternateSkill.Id);
            int alternateCost = Math.Max(0, alternateSkill.SkillCostCount ?? 0);
            test.SetBuildPoint(alternateCost + 10);
            test.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = alternateSkill.Id,
                Params = [mapId, relogged.PosY, relogged.PosX]
            }, $"Building type {building.Type} rejects a distinct build on an occupied tile");
        }

        using Theatre4Case invalid = new("building-invalid-atomic");
        invalid.StartRun(1);
        Theatre4BuildingTable station = Theatre4BuildingAuditBuilding(1);
        Theatre4EffectTable stationSkill = Theatre4BuildingAuditBuildEffect(station);
        Theatre4SeedEffect(invalid, stationSkill.Id);
        int invalidMap = invalid.Adventure.Chapters[0].MapId;
        Theatre4GridData valid = Theatre4BuildingAuditEmpty(invalid, invalidMap);
        invalid.SetBuildPoint(Math.Max(1, stationSkill.SkillCostCount ?? 0) + 5);
        invalid.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = stationSkill.Id, Params = [invalidMap, valid.PosY, valid.PosX]
        });
        invalid.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = stationSkill.Id, Params = [invalidMap]
        }, "Malformed construction params are atomic");
        invalid.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = stationSkill.Id, Params = [invalidMap, valid.PosY, valid.PosX]
        }, "Duplicate construction is atomic");
        Theatre4GridData nonEmpty = invalid.Adventure.Chapters[0].Grids
            .First(grid => grid.Type is T4GridMonster or T4GridBoss or T4GridShop or T4GridBox);
        invalid.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = stationSkill.Id, Params = [invalidMap, nonEmpty.PosY, nonEmpty.PosX]
        }, "Non-empty construction is atomic");
    }

    private static void ValidateTheatre4BuildingAuditAutoExplore()
    {
        Theatre4BuildingTable station = Theatre4BuildingAuditBuilding(1);
        Theatre4EffectTable build = Theatre4BuildingAuditBuildEffect(station);
        Theatre4EffectTable manual = Theatre4BuildingAuditEffectByType(103);
        Theatre4EffectTable automatic = Theatre4BuildingAuditEffectByType(104);

        using (Theatre4Case created = new("building-manual-open"))
        {
            created.StartRun(1);
            Theatre4SeedEffect(created, build.Id);
            Theatre4SeedEffect(created, manual.Id);
            int mapId = created.Adventure.Chapters[0].MapId;
            Theatre4GridData anchor = Theatre4BuildingAuditEmpty(created, mapId);
            int apBefore = created.Adventure.Ap;
            int exploredBefore = created.Adventure.ExploreCount;
            int bpBefore = Math.Max(1, build.SkillCostCount ?? 0) + 20;
            created.SetBuildPoint(bpBefore);
            created.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id, Params = [mapId, anchor.PosY, anchor.PosX]
            });
            Theatre4GridData live = created.Grid(mapId, anchor.PosX, anchor.PosY);
            AssertEqual(T4GridBuilding, live.Type, "Type103 is attached to a real station construction");
            AssertEqual(apBefore, created.Adventure.Ap,
                "Type103 construction auto-exploration does not spend action points");
            AssertEqual(true, created.Adventure.ExploreCount > exploredBefore,
                "Type103 construction performs an automatic exploration transition");
            AssertEqual(true, Theatre4BuildingAuditChangePushContainsBuilding(created, station.Id),
                "Construction publishes the building through the real change-grid push");
            created.Relog("manual station open");
            AssertEqual(station.Id, created.Grid(mapId, anchor.PosX, anchor.PosY).Building!.BuildingId,
                "Manual-open station remains durable after relog");
        }

        using Theatre4Case daily = new("building-automatic-open");
        daily.StartRun(1);
        Theatre4SeedEffect(daily, build.Id);
        Theatre4SeedEffect(daily, automatic.Id);
        int dailyMap = daily.Adventure.Chapters[0].MapId;
        Theatre4GridData dailyAnchor = Theatre4BuildingAuditEmpty(daily, dailyMap);
        daily.SetBuildPoint(Math.Max(1, build.SkillCostCount ?? 0) + 20);
        daily.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [dailyMap, dailyAnchor.PosY, dailyAnchor.PosX]
        });
        int exploredBeforeDay = daily.Adventure.ExploreCount;
        daily.Adventure.Ap = 0;
        daily.SaveFixture();
        daily.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(true, daily.Adventure.ExploreCount > exploredBeforeDay,
            "Type104 opens an unknown tile during automatic daily settlement");
        AssertEqual(daily.Adventure.MaxAp + daily.Adventure.ExtraMaxAp, daily.Adventure.Ap,
            "Type104 automatic exploration does not charge the daily action-point refill");
    }

    private static void ValidateTheatre4BuildingAuditSupplyAndResidence()
    {
        Theatre4BuildingTable depot = Theatre4BuildingAuditBuilding(3);
        Theatre4EffectTable depotSkill = Theatre4BuildingAuditBuildEffect(depot);
        using Theatre4Case test = new("building-supply-range");
        test.StartRun(1);
        Theatre4SeedEffect(test, depotSkill.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        (Theatre4GridData anchor, List<Theatre4GridData> range, Theatre4GridData outside) =
            Theatre4BuildingAuditRangeFixture(test, mapId, 1,
                static (anchorX, anchorY, targetX, targetY) =>
                    Math.Max(Math.Abs(anchorX - targetX), Math.Abs(anchorY - targetY)) == 1);
        Theatre4BuildingAuditPrepareEmpty(anchor, T4StateProcessed);
        test.SaveFixture();
        int buildCost = Math.Max(0, depotSkill.SkillCostCount ?? 0);
        test.SetBuildPoint(buildCost + 20);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = depotSkill.Id, Params = [mapId, anchor.PosY, anchor.PosX]
        });

        anchor = test.Grid(mapId, anchor.PosX, anchor.PosY);
        Theatre4GridData source = Theatre4BuildingAuditResourceSource(test.Chapter(mapId), anchor.GridId);
        int sourceColor = source.Color;
        int sourceResource = source.ColorResource;
        Theatre4GridData inside = test.Grid(mapId, range[0].PosX, range[0].PosY);
        outside = test.Grid(mapId, outside.PosX, outside.PosY);
        Theatre4BuildingAuditPrepareExploreTile(inside, sourceColor, sourceResource);
        Theatre4BuildingAuditPrepareExploreTile(outside, sourceColor, sourceResource);
        test.SetBuildPoint(100);
        test.Adventure.Ap = 10;
        int colorBefore = test.Adventure.Colors.Single(color => color.Color == sourceColor).Resource;
        inside = test.Grid(mapId, inside.PosX, inside.PosY);
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = mapId, PosX = inside.PosX, PosY = inside.PosY
        });
        int inDelta = test.Adventure.Colors.Single(color => color.Color == sourceColor).Resource - colorBefore;
        AssertEqual(sourceResource + depot.Params[0], inDelta,
            "Supply depot adds its authored resource only to an explored in-range tile");

        colorBefore = test.Adventure.Colors.Single(color => color.Color == sourceColor).Resource;
        outside = test.Grid(mapId, outside.PosX, outside.PosY);
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = mapId, PosX = outside.PosX, PosY = outside.PosY
        });
        int outDelta = test.Adventure.Colors.Single(color => color.Color == sourceColor).Resource - colorBefore;
        AssertEqual(sourceResource, outDelta,
            "Supply depot does not add its authored resource outside its cube range");

        Theatre4BuildingTable residence = Theatre4BuildingAuditBuilding(4);
        Theatre4EffectTable residenceSkill = Theatre4BuildingAuditBuildEffect(residence);
        using Theatre4Case area = new("building-residence-area");
        area.StartRun(1);
        Theatre4SeedEffect(area, residenceSkill.Id);
        int firstMap = area.Adventure.Chapters[0].MapId;
        Theatre4GridData firstResidence = Theatre4BuildingAuditEmpty(area, firstMap);
        area.SetBuildPoint(Math.Max(0, residenceSkill.SkillCostCount ?? 0) + 20);
        area.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = residenceSkill.Id, Params = [firstMap, firstResidence.PosY, firstResidence.PosX]
        });
        Theatre4ChapterData second = Theatre4InvokeInitializeMap(area, 11002);
        area.Adventure.Ap = 0;
        area.SaveFixture();
        area.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(area.Adventure.MaxAp + area.Adventure.ExtraMaxAp, area.Adventure.Ap,
            "Residence bonus does not leak from a prior map into the current area");

        Theatre4GridData secondResidence = Theatre4BuildingAuditEmpty(area, second.MapId);
        area.SetBuildPoint(Math.Max(0, residenceSkill.SkillCostCount ?? 0) + 20);
        area.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = residenceSkill.Id, Params = [second.MapId, secondResidence.PosY, secondResidence.PosX]
        });
        area.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = residenceSkill.Id,
            Params = [second.MapId, secondResidence.PosY, secondResidence.PosX]
        }, "Residence chapter cap rejects a duplicate in the current area");
        area.Adventure.Ap = 0;
        area.SaveFixture();
        area.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(area.Adventure.MaxAp + area.Adventure.ExtraMaxAp + residence.Params[0], area.Adventure.Ap,
            "Residence adds its authored action points only in the current area");
    }

    private static void ValidateTheatre4BuildingAuditCombination()
    {
        Theatre4BuildingTable combination = Theatre4BuildingAuditBuilding(5);
        Theatre4EffectTable skill = Theatre4BuildingAuditBuildEffect(combination);
        using Theatre4Case test = new("building-combination");
        test.StartRun(1);
        Theatre4SeedEffect(test, skill.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData anchor = Theatre4BuildingAuditEmpty(test, mapId);
        List<Theatre4GridData> cross = test.Chapter(mapId).Grids
            .Where(grid => grid.GridId != anchor.GridId && grid.PosX != grid.PosY
                && Theatre4BuildingAuditCross(anchor, grid, 1)).Take(2).ToList();
        foreach (Theatre4GridData grid in cross)
            Theatre4BuildingAuditPrepareExploreTile(grid,
                Theatre4BuildingAuditResourceSource(test.Chapter(mapId), anchor.GridId).Color, 0);
        int apBefore = test.Adventure.Ap;
        int bpBefore = Math.Max(0, skill.SkillCostCount ?? 0) + 20;
        test.SetBuildPoint(bpBefore);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = skill.Id, Params = [mapId, anchor.PosY, anchor.PosX]
        });
        Theatre4GridData live = test.Grid(mapId, anchor.PosX, anchor.PosY);
        AssertEqual(combination.Id, live.Building!.BuildingId,
            "Temporary base construction stores the authored combination building");
        AssertEqual(apBefore, test.Adventure.Ap,
            "Temporary base construction's cross preview is automatic and free of action points");
        AssertEqual(bpBefore - Math.Max(0, skill.SkillCostCount ?? 0), test.Asset(T4AssetBuildPoint, 0),
            "Temporary base construction charges its authored cost once");
        AssertEqual(true, cross.Count == 0 || cross.All(grid =>
            test.Grid(mapId, grid.PosX, grid.PosY).State == T4StateDiscover),
            "Temporary base reveals its authored cross range without exploring it");
    }

    private static void ValidateTheatre4BuildingAuditTower()
    {
        Theatre4BuildingTable tower = Theatre4BuildingAuditBuilding(2);
        Theatre4EffectTable towerSkill = Theatre4BuildingAuditBuildEffect(tower);
        Theatre4EffectTable allTargets = Theatre4BuildingAuditEffectByType(106);
        Theatre4EffectTable bossEffect = Theatre4BuildingAuditEffectByType(107);
        Theatre4EffectTable selfDestruct = Theatre4BuildingAuditEffectByType(105);
        Theatre4EffectTable towerSpecialty = Theatre4BuildingAuditEffectByType(17);
        Theatre4EffectTable precision = Theatre4BuildingAuditEffectByType(109);
        Theatre4EffectTable pacify = Theatre4BuildingAuditEffectByType(34);

        using Theatre4Case test = new("building-tower-clear");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[]
        {
            towerSkill, allTargets, bossEffect, selfDestruct, towerSpecialty, precision, pacify
        }) Theatre4SeedEffect(test, effect.Id);

        int mapId = test.Adventure.Chapters[0].MapId;
        (Theatre4GridData anchor, List<Theatre4GridData> ordinary, Theatre4GridData boss) =
            Theatre4BuildingAuditTowerFixture(test, mapId);
        // ContentId is the frozen authored fight identity; do not reconstruct it from
        // ContentGroup/FightGroupId because a group can have multiple authored aliases.
        int[] expectedOrdinaryFightIds = ordinary
            .Select(grid => Theatre4BuildingAuditFightRow(grid).Id).ToArray();
        int expectedBossFightId = Theatre4BuildingAuditFightRow(boss).Id;
        int cost = Math.Max(0, towerSkill.SkillCostCount ?? 0);
        int bpBefore = cost + 100;
        test.SetBuildPoint(bpBefore);
        int sweepsBefore = test.Adventure.EffectSweepTimes;
        int finishBefore = test.Adventure.FinishFightIds.Count;
        int bossHpBefore = boss.Fight!.HpPercent;
        AssertEqual(10000, bossHpBefore, "Arrow tower starts a healthy boss at native full HP");
        HashSet<int> preExistingRewardTransactions = test.Adventure.Transactions
            .Where(transaction => transaction.Type == 4)
            .Select(transaction => transaction.Id)
            .ToHashSet();
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = towerSkill.Id, Params = [mapId, anchor.PosY, anchor.PosX]
        });

        AssertEqual(T4GridBuilding, test.Grid(mapId, anchor.PosX, anchor.PosY).Type,
            "Arrow tower construction persists its building tile");
        AssertEqual(true, ordinary.All(grid => test.Grid(mapId, grid.PosX, grid.PosY).State == T4StateProcessed),
            "Type106 clears every eligible ordinary tower target");
        AssertEqual(sweepsBefore, test.Adventure.EffectSweepTimes,
            "Arrow tower clears do not increment paid sweep counters");
        AssertEqual(true, test.Adventure.FinishFightIds.Count > finishBefore,
            "Arrow tower clears record fight progression");
        int[] finishedFightIds = test.Adventure.FinishFightIds.Skip(finishBefore).ToArray();
        AssertEqual(true, expectedOrdinaryFightIds.All(finishedFightIds.Contains),
            "Arrow tower progression uses the frozen authored fight ids");

        int expectedHp = Math.Max(1, (int)((long)bossHpBefore
            * (10000 - Math.Clamp(bossEffect.Params[0], 1, 9999)) / 10000));
        AssertEqual(expectedHp, test.Grid(mapId, boss.PosX, boss.PosY).Fight!.HpPercent,
            "Type107 weakens the boss in native basis-point HP");
        AssertEqual(false, test.Adventure.FinishFightIds.Contains(expectedBossFightId),
            "Type107 leaves the boss unprocessed for a later retry");
        AssertEqual(ordinary.Count * towerSpecialty.Params[1],
            test.Adventure.CustomEffects.Single(effect => effect.EffectId == towerSpecialty.Id).MarkupRate,
            "Type17 responds only to actual tower clears");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == pacify.Id).MarkupRate,
            "Type34 does not treat a tower clear as a paid sweep");
        AssertEqual(true, test.Adventure.CustomEffects.All(effect => effect.EffectId != selfDestruct.Id),
            "Type105 is consumed after the owning tower actually acts");
        AssertEqual(bpBefore - cost + selfDestruct.Params[0] + ordinary.Count * precision.Params[2],
            test.Asset(T4AssetBuildPoint, 0),
            "Tower self-destruction and precision use the authored per-clear build-point operands");

        List<Theatre4TransactionData> rewards = test.Adventure.Transactions
            .Where(transaction => transaction.Type == 4
                && !preExistingRewardTransactions.Contains(transaction.Id))
            .ToList();
        AssertEqual(true, rewards.Count > 0, "Tower clear creates an authored fight reward offer");
        HashSet<int> towerFightIds = expectedOrdinaryFightIds.ToHashSet();
        Dictionary<int, Theatre4RewardTable> rewardRows = TableReaderV2.Parse<Theatre4RewardTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, AscNet.Table.V2.share.condition.ConditionTable> conditionRows =
            TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .ToDictionary(row => row.Id);
        AssertEqual(true, rewards.All(reward => towerFightIds.Contains(reward.ConfigId)),
            "Tower reward offers come only from newly cleared tower fights");
        foreach (Theatre4TransactionData reward in rewards)
        {
            Theatre4FightTable fight = TableReaderV2.Parse<Theatre4FightTable>()
                .Single(row => row.Id == reward.ConfigId);
            if (fight.RewardDropId is not > 0) continue;
            Theatre4RewardDropTable drop = TableReaderV2.Parse<Theatre4RewardDropTable>()
                .Single(row => row.Id == fight.RewardDropId.Value);
            AssertEqual(true, reward.Rewards.Count > 0 && reward.Rewards.All(asset =>
                rewardRows.TryGetValue(asset.RewardId, out Theatre4RewardTable? authored)
                && drop.GroupIds.Contains(authored.GroupId)
                && (authored.Condition is not > 0
                    || Theatre4ProgressEvaluateGate(test, conditionRows[authored.Condition.Value]))
                // Asset.Id is Theatre4Reward.ElementId; RewardId is the reward-row identity.
                && asset.Type == authored.ElementType
                && asset.Id == (authored.ElementId ?? 0)
                && asset.Num == authored.ElementCount),
                "Tower reward options carry their authored row provenance and asset fields");
        }
        using Theatre4Case noAction = new("building-tower-no-action");
        noAction.StartRun(1);
        Theatre4SeedEffect(noAction, towerSkill.Id);
        Theatre4SeedEffect(noAction, selfDestruct.Id);
        int noActionMap = noAction.Adventure.Chapters[0].MapId;
        Theatre4GridData noActionAnchor = Theatre4BuildingAuditEmpty(noAction, noActionMap);
        foreach (Theatre4GridData neighbour in noAction.Chapter(noActionMap).Grids.Where(grid =>
            grid.GridId != noActionAnchor.GridId
            && Theatre4BuildingAuditCross(noActionAnchor, grid, 1)
            && grid.Type is not (T4GridStart or T4GridBoss)))
            Theatre4BuildingAuditPrepareEmpty(neighbour, T4StateProcessed);
        noAction.SaveFixture();
        int noActionBp = Math.Max(0, towerSkill.SkillCostCount ?? 0) + 10;
        noAction.SetBuildPoint(noActionBp);
        noAction.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = towerSkill.Id, Params = [noActionMap, noActionAnchor.PosY, noActionAnchor.PosX]
        });
        AssertEqual(true, noAction.Adventure.CustomEffects.Any(effect => effect.EffectId == selfDestruct.Id),
            "Type105 remains equipped when its tower has no eligible target");
        int noActionAfterBuild = noAction.Asset(T4AssetBuildPoint, 0);
        var distantFixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
        Theatre4GridData distant = Theatre4AuditInstallGridFight(noAction, distantFixture, boss: false);
        noAction.RecruitAndSetTeam();
        noAction.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = noActionMap, PosX = distant.PosX, PosY = distant.PosY
        });
        noAction.ResolveFrozenFight();
        AssertEqual(true, noAction.Adventure.CustomEffects.Any(effect => effect.EffectId == selfDestruct.Id),
            "A distant normal fight does not consume Type105");
        AssertEqual(noActionAfterBuild, noAction.Asset(T4AssetBuildPoint, 0),
            "A distant normal fight does not receive the tower refund");
    }

    private static void ValidateTheatre4BuildingAuditDepotRangeEffects()
    {
        Theatre4BuildingTable depot = Theatre4BuildingAuditBuilding(3);
        Theatre4BuildingTable tower = Theatre4BuildingAuditBuilding(2);
        Theatre4EffectTable depotSkill = Theatre4BuildingAuditBuildEffect(depot);
        Theatre4EffectTable towerSkill = Theatre4BuildingAuditBuildEffect(tower);
        Theatre4EffectTable saveMoney = Theatre4BuildingAuditEffectByType(110);
        Theatre4EffectTable colorLevel = Theatre4BuildingAuditEffectByType(108);

        using Theatre4Case test = new("building-depot-effects");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { depotSkill, towerSkill, saveMoney, colorLevel })
            Theatre4SeedEffect(test, effect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData monsterSource = test.Chapter(mapId).Grids
            .Where(grid => grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentId > 0)
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Single tower fixture has no authored monster.");
        Theatre4FightData monsterFight = monsterSource.Fight
            ?? throw new InvalidDataException("Single tower fixture source has no fight state.");
        (int Group, int Id, Theatre4FightData Fight) combatTemplate =
            (monsterSource.ContentGroup, monsterSource.ContentId, monsterFight);

        (Theatre4GridData depotAnchor, List<Theatre4GridData> inside, Theatre4GridData outside) =
            Theatre4BuildingAuditRangeFixture(test, mapId, 4,
                static (anchorX, anchorY, targetX, targetY) =>
                    Math.Max(Math.Abs(anchorX - targetX), Math.Abs(anchorY - targetY)) == 1);
        Theatre4BuildingAuditPrepareEmpty(depotAnchor, T4StateProcessed);
        test.SaveFixture();
        test.SetBuildPoint(100);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = depotSkill.Id, Params = [mapId, depotAnchor.PosY, depotAnchor.PosX]
        });

        depotAnchor = test.Grid(mapId, depotAnchor.PosX, depotAnchor.PosY);
        Theatre4GridData source = Theatre4BuildingAuditResourceSource(test.Chapter(mapId),
            depotAnchor.GridId);
        int sourceColor = source.Color;
        inside = inside.Select(grid => test.Grid(mapId, grid.PosX, grid.PosY)).ToList();
        outside = test.Grid(mapId, outside.PosX, outside.PosY);
        foreach (Theatre4GridData grid in inside.Append(outside))
            Theatre4BuildingAuditPrepareExploreTile(grid, sourceColor, 0);
        test.Adventure.Ap = 20;
        test.SaveFixture();

        Theatre4RewardDropTable saveDrop = TableReaderV2.Parse<Theatre4RewardDropTable>()
            .Single(row => row.Id == saveMoney.Params[1]);
        Dictionary<int, Theatre4RewardTable> rewardRows =
            TableReaderV2.Parse<Theatre4RewardTable>().ToDictionary(row => row.Id);
        Dictionary<int, AscNet.Table.V2.share.condition.ConditionTable> conditionRows =
            TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .ToDictionary(row => row.Id);
        Theatre4EffectData saveInstance = test.Adventure.CustomEffects.Single(effect => effect.EffectId == saveMoney.Id);
        int transactionsBeforeOutside = test.Adventure.Transactions.Count;
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = mapId, PosX = outside.PosX, PosY = outside.PosY
        });
        saveInstance = test.Adventure.CustomEffects.Single(effect => effect.EffectId == saveMoney.Id);
        AssertEqual(0, saveInstance.Count, "Type110 ignores exploration outside a Supply Depot");
        AssertEqual(transactionsBeforeOutside, test.Adventure.Transactions.Count,
            "Type110 creates no outside-range pending reward transaction");
        AssertEqual(false, test.Pushes.Any(push => push.Name == nameof(NotifyTheatre4Reward)),
            "Type110 creates no outside-range direct reward");

        Theatre4AdventureData beforeThreshold = Theatre4ProgressCloneAdventure(test.Adventure);
        foreach (Theatre4GridData grid in inside.Take(saveMoney.Params[0]))
        {
            Theatre4GridData live = test.Grid(mapId, grid.PosX, grid.PosY);
            test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
            {
                MapId = mapId, PosX = live.PosX, PosY = live.PosY
            });
        }
        saveInstance = test.Adventure.CustomEffects.Single(effect => effect.EffectId == saveMoney.Id);
        AssertEqual(saveMoney.Params[0], saveInstance.Count,
            "Type110 counts only actual in-range exploration transitions");
        NotifyTheatre4Reward thresholdReward = test.Pushes
            .Where(push => push.Name == nameof(NotifyTheatre4Reward))
            .Select(push => MessagePackSerializer.Deserialize<NotifyTheatre4Reward>(push.Content))
            .SingleOrDefault()
            ?? throw new InvalidDataException("Type110 threshold omitted its authored reward push.");
        Theatre4RewardTable[] authoredRewards = thresholdReward.Rewards
            .Select(asset => rewardRows.TryGetValue(asset.RewardId, out Theatre4RewardTable? authored)
                ? authored
                : throw new InvalidDataException(
                    $"Type110 threshold emitted unknown reward row {asset.RewardId}."))
            .ToArray();
        AssertEqual(true, authoredRewards.Length > 0 && authoredRewards.All(authored =>
            saveDrop.GroupIds.Contains(authored.GroupId)
            && (authored.Condition is not > 0
                || conditionRows.TryGetValue(authored.Condition.Value, out var condition)
                    && Theatre4ProgressEvaluateGate(test, condition))),
            "Type110 threshold emits only eligible rows from its authored reward drop");
        AssertEqual(authoredRewards.Length, authoredRewards.Select(authored => authored.GroupId).Distinct().Count(),
            "Type110 threshold emits one direct reward row per drop group");
        AssertEqual(beforeThreshold.Transactions.Count, test.Adventure.Transactions.Count,
            "Type110's direct mode reward creates no pending choice transaction");
        Theatre4ProgressAssertModeRewards(test, beforeThreshold,
            authoredRewards.Select(authored => authored.Id), "Type110 authored threshold reward");
        Theatre4AdventureData afterThreshold = Theatre4ProgressCloneAdventure(test.Adventure);
        Theatre4GridData extra = test.Grid(mapId, inside[saveMoney.Params[0]].PosX,
            inside[saveMoney.Params[0]].PosY);
        test.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = mapId, PosX = extra.PosX, PosY = extra.PosY
        });
        AssertEqual(0, test.Pushes.Count(push => push.Name == nameof(NotifyTheatre4Reward)),
            "Type110 does not publish a second reward after its one-shot threshold");
        foreach (IGrouping<(int Type, int Id), Theatre4RewardTable> group in authoredRewards.GroupBy(
            authored => (authored.ElementType, authored.ElementId ?? 0)))
            AssertEqual(Theatre4ProgressModeUnits(afterThreshold, group.Key.Type, group.Key.Id),
                Theatre4ProgressModeUnits(test.Adventure, group.Key.Type, group.Key.Id),
                $"Type110 does not grant mode asset {group.Key.Type}/{group.Key.Id} twice");

        // One actual tower clear inside and one outside the depot distinguish Type108's range gate.
        (Theatre4GridData inTower, Theatre4GridData inTarget, Theatre4FightTable inFight) =
            Theatre4BuildingAuditInstallSingleTowerTarget(test, mapId, depotAnchor,
                combatTemplate, inRange: true);

        test.SetBuildPoint(100);
        Theatre4AdventureData beforeColorDrop = Theatre4ProgressCloneAdventure(test.Adventure);
        int fightRewardsBefore = beforeColorDrop.Transactions.Count(transaction => transaction.Type == 4);
        int choiceTransactionsBefore = beforeColorDrop.Transactions.Count(transaction => transaction.Type is 1 or 2 or 3);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = towerSkill.Id, Params = [mapId, inTower.PosY, inTower.PosX]
        });
        AssertEqual(fightRewardsBefore + 1,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Type108 preserves the target's normal fight reward offer");
        AssertEqual(true, test.Adventure.Transactions.Any(transaction =>
            transaction.Type == 4 && transaction.ConfigId == inFight.Id),
            "Type108 clear leaves the authored fight reward transaction active");
        AssertEqual(choiceTransactionsBefore,
            test.Adventure.Transactions.Count(transaction => transaction.Type is 1 or 2 or 3),
            "Type108's direct mode reward creates no pending choice transaction");
        Theatre4RewardDropTable colorDrop = TableReaderV2.Parse<Theatre4RewardDropTable>()
            .Single(row => row.Id == colorLevel.Params[0]);
        NotifyTheatre4Reward colorReward = test.Pushes
            .Where(push => push.Name == nameof(NotifyTheatre4Reward))
            .Select(push => MessagePackSerializer.Deserialize<NotifyTheatre4Reward>(push.Content))
            .SingleOrDefault()
            ?? throw new InvalidDataException("Type108 in-range clear omitted its authored reward push.");
        Theatre4RewardTable[] colorRewards = colorReward.Rewards
            .Select(asset => rewardRows.TryGetValue(asset.RewardId, out Theatre4RewardTable? authored)
                ? authored
                : throw new InvalidDataException(
                    $"Type108 in-range clear emitted unknown reward row {asset.RewardId}."))
            .ToArray();
        AssertEqual(true, colorRewards.Length > 0 && colorRewards.All(authored =>
            colorDrop.GroupIds.Contains(authored.GroupId)
            && (authored.Condition is not > 0
                || conditionRows.TryGetValue(authored.Condition.Value, out var condition)
                    && Theatre4ProgressEvaluateGate(test, condition))),
            "Type108 in-range clear emits only eligible rows from its authored reward drop");
        AssertEqual(colorRewards.Length, colorRewards.Select(authored => authored.GroupId).Distinct().Count(),
            "Type108 in-range clear emits one direct reward row per drop group");
        Theatre4ProgressAssertModeRewards(test, beforeColorDrop,
            colorRewards.Select(authored => authored.Id), "Type108 authored in-range reward");
        Theatre4AdventureData afterColorDrop = Theatre4ProgressCloneAdventure(test.Adventure);
        AssertEqual(T4StateProcessed, test.Grid(mapId, inTarget.PosX, inTarget.PosY).State,
            "Type108 fixture clears the in-range tower target");
        AssertEqual(true, inFight.Id > 0, "In-range tower target remains bound to an authored fight row");

        (Theatre4GridData outTower, Theatre4GridData outTarget, _) =
            Theatre4BuildingAuditInstallSingleTowerTarget(test, mapId, depotAnchor,
                combatTemplate, inRange: false, excluded: [inTower.GridId, inTarget.GridId]);

        test.SetBuildPoint(100);
        int fightRewardsAfterInRange = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = towerSkill.Id, Params = [mapId, outTower.PosY, outTower.PosX]
        });
        AssertEqual(fightRewardsAfterInRange,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Type108 out-of-range clear does not add a duplicate normal fight reward offer");
        AssertEqual(0, test.Pushes.Count(push => push.Name == nameof(NotifyTheatre4Reward)),
            "Type108 creates no direct drop for an actual clear outside every depot range");
        foreach (IGrouping<(int Type, int Id), Theatre4RewardTable> group in colorRewards.GroupBy(
            authored => (authored.ElementType, authored.ElementId ?? 0)))
            AssertEqual(Theatre4ProgressModeUnits(afterColorDrop, group.Key.Type, group.Key.Id),
                Theatre4ProgressModeUnits(test.Adventure, group.Key.Type, group.Key.Id),
                $"Type108 does not grant mode asset {group.Key.Type}/{group.Key.Id} outside depot range");
        AssertEqual(T4StateProcessed, test.Grid(mapId, outTarget.PosX, outTarget.PosY).State,
            "Out-of-range tower target still clears normally without Type108");
    }

    private static void ValidateTheatre4BuildingAuditModifications()
    {
        Theatre4BuildingAuditModificationRefund();
        Theatre4BuildingAuditObstacleMining();
        Theatre4BuildingAuditFreeLoading();
        Theatre4BuildingAuditNextBuildRefund();
    }

    private static void Theatre4BuildingAuditModificationRefund()
    {
        Theatre4BuildingTable station = Theatre4BuildingAuditBuilding(1);
        Theatre4EffectTable build = Theatre4BuildingAuditBuildEffect(station);
        Theatre4EffectTable refund = Theatre4BuildingAuditEffectByType(112);
        Theatre4EffectTable modify = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 115 && row.SkillCostType == T4AssetBuildPoint);
        using Theatre4Case test = new("building-modification-refund");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { build, refund, modify }) Theatre4SeedEffect(test, effect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        (Theatre4GridData anchor, List<Theatre4GridData> range, Theatre4GridData outside) =
            Theatre4BuildingAuditRangeFixture(test, mapId, 1,
                static (anchorX, anchorY, targetX, targetY) =>
                {
                    int x = Math.Abs(anchorX - targetX), y = Math.Abs(anchorY - targetY);
                    return x == 0 && y is 1 or 2 || y == 0 && x is 1 or 2 || x == 1 && y == 1;
                });
        Theatre4BuildingAuditPrepareEmpty(anchor, T4StateProcessed);
        test.SaveFixture();
        test.SetBuildPoint(100);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [mapId, anchor.PosY, anchor.PosX]
        });

        anchor = test.Grid(mapId, anchor.PosX, anchor.PosY);
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .Single(row => row.Id == mapId);
        Theatre4GridData source = test.Chapter(mapId).Grids
            .Where(grid => grid.GridId != anchor.GridId && grid.Color is >= 1 and <= 3
                && grid.Color != modify.Params[0] && grid.ColorResource > 0
                && Theatre4BuildingAuditPositionUsable(test.Chapter(mapId), map,
                    grid.PosX, grid.PosY))
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Modification refund fixture has no coloured authored tile.");
        int sourceColor = source.Color;
        Theatre4GridData inside = test.Grid(mapId, range[0].PosX, range[0].PosY);
        outside = test.Grid(mapId, outside.PosX, outside.PosY);
        Theatre4BuildingAuditPrepareExploreTile(inside, sourceColor, 0);
        Theatre4BuildingAuditPrepareExploreTile(outside, sourceColor, 0);
        test.SetBuildPoint(100);
        test.SaveFixture();
        int cost = Math.Max(0, modify.SkillCostCount ?? 0);
        int before = test.Asset(T4AssetBuildPoint, 0);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = modify.Id, Params = [mapId, inside.PosY, inside.PosX]
        });
        AssertEqual(before - cost + refund.Params[2], test.Asset(T4AssetBuildPoint, 0),
            "Type112 refunds build energy only for an in-range modification");

        before = test.Asset(T4AssetBuildPoint, 0);
        outside = test.Grid(mapId, outside.PosX, outside.PosY);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = modify.Id, Params = [mapId, outside.PosY, outside.PosX]
        });
        AssertEqual(before - cost, test.Asset(T4AssetBuildPoint, 0),
            "Type112 gives no build-energy refund outside a building range");
    }

    private static void Theatre4BuildingAuditObstacleMining()
    {
        Theatre4EffectTable remove = Theatre4BuildingAuditEffectByType(117);
        Theatre4EffectTable mining = Theatre4BuildingAuditEffectByType(118);
        Theatre4EffectTable extra = Theatre4BuildingAuditEffectByType(119);
        using Theatre4Case test = new("building-obstacle-mining");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { remove, mining, extra }) Theatre4SeedEffect(test, effect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        (Theatre4GridData anchor, List<Theatre4GridData> range, _) =
            Theatre4BuildingAuditRangeFixture(test, mapId, 1,
                static (anchorX, anchorY, targetX, targetY) =>
                    (anchorX == targetX && Math.Abs(anchorY - targetY) <= 1)
                    || (anchorY == targetY && Math.Abs(anchorX - targetX) <= 1));
        Theatre4GridData neighbour = range[0];
        Theatre4BuildingAuditPrepareObstacle(anchor);
        Theatre4BuildingAuditPrepareObstacle(neighbour);
        test.SetBuildPoint(100);
        test.SetGold(0);
        test.SaveFixture();
        int bpBefore = test.Asset(T4AssetBuildPoint, 0);
        int goldBefore = test.Asset(T4AssetGold, 0);
        int cost = Math.Max(0, remove.SkillCostCount ?? 0);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = remove.Id, Params = [mapId, anchor.PosY, anchor.PosX]
        });
        int removedCount = 1 + extra.Params[0];
        AssertEqual(T4GridEmpty, test.Grid(mapId, anchor.PosX, anchor.PosY).Type,
            "Obstacle removal clears the selected authored hurdle");
        AssertEqual(T4GridEmpty, test.Grid(mapId, neighbour.PosX, neighbour.PosY).Type,
            "Type119 removes its authored extra cross obstacle");
        AssertEqual(goldBefore + removedCount * mining.Params[2], test.Asset(T4AssetGold, 0),
            "Type118 pays the authored gold per actually removed obstacle");
        AssertEqual(bpBefore - cost, test.Asset(T4AssetBuildPoint, 0),
            "Obstacle removal charges its authored cost once");
    }

    private static void Theatre4BuildingAuditFreeLoading()
    {
        Theatre4EffectTable modify = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 115 && row.SkillCostType == T4AssetBuildPoint);
        Theatre4EffectTable free = Theatre4BuildingAuditEffectByType(125);
        using Theatre4Case test = new("building-free-loading");
        test.StartRun(1);
        Theatre4SeedEffect(test, modify.Id);
        Theatre4SeedEffect(test, free.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData target = Theatre4BuildingAuditPrepareModificationTile(test, mapId,
            modify.Params[0]);
        int oldColor = target.Color;
        int newColor = modify.Params[0];
        if (newColor == oldColor) throw new InvalidDataException("Free-loading fixture needs a colour change.");
        int tileResource = target.ColorResource;
        int beforeOld = test.Adventure.Colors.Single(color => color.Color == oldColor).Resource;
        int beforeNew = test.Adventure.Colors.Single(color => color.Color == newColor).Resource;
        int ratio = free.Params[0];
        test.SetBuildPoint(Math.Max(0, modify.SkillCostCount ?? 0) + 10);
        test.SaveFixture();
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = modify.Id, Params = [mapId, target.PosY, target.PosX]
        });
        int expected = (int)((long)tileResource * ratio / 10000);
        AssertEqual(beforeNew + expected,
            test.Adventure.Colors.Single(color => color.Color == newColor).Resource,
            "Type125 credits the affected tile resource multiplied by its authored basis-point operand");
        AssertEqual(beforeOld,
            test.Adventure.Colors.Single(color => color.Color == oldColor).Resource,
            "Type125 does not credit an unrelated colour bucket");
    }

    private static void Theatre4BuildingAuditNextBuildRefund()
    {
        Theatre4BuildingTable station = Theatre4BuildingAuditBuilding(1);
        Theatre4EffectTable build = Theatre4BuildingAuditBuildEffect(station);
        Theatre4EffectTable modify = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 115 && row.SkillCostType == T4AssetBuildPoint);
        Theatre4EffectTable collab = Theatre4BuildingAuditEffectByType(122);
        using Theatre4Case test = new("building-next-refund");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { build, modify, collab }) Theatre4SeedEffect(test, effect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData modified = Theatre4BuildingAuditPrepareModificationTile(test, mapId,
            modify.Params[0]);
        test.SetBuildPoint(100);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = modify.Id, Params = [mapId, modified.PosY, modified.PosX]
        });
        AssertEqual(1, test.Adventure.CustomEffects.Single(effect => effect.EffectId == collab.Id).Count,
            "Type122 arms a refund after a real modification");
        test.Adventure.Ap = 0;
        test.SaveFixture();
        test.Call(nameof(Theatre4DailySettleRequest));
        test.Relog("next-build refund armed");
        AssertEqual(1, test.Adventure.CustomEffects.Single(effect => effect.EffectId == collab.Id).Count,
            "Type122 remains armed across daily settlement and relog");

        Theatre4GridData first = Theatre4BuildingAuditEmpty(test, mapId, [modified.GridId]);
        int buildCost = Math.Max(0, build.SkillCostCount ?? 0);
        test.SetBuildPoint(buildCost + 10);
        int before = test.Asset(T4AssetBuildPoint, 0);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [mapId, first.PosY, first.PosX]
        });
        AssertEqual(before - buildCost + collab.Params[2], test.Asset(T4AssetBuildPoint, 0),
            "Type122 refunds the next construction with its authored operand");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == collab.Id).Count,
            "Type122 consumes its armed refund exactly once");

        Theatre4GridData second = Theatre4BuildingAuditEmpty(test, mapId, [modified.GridId, first.GridId]);
        test.SetBuildPoint(buildCost + 10);
        before = test.Asset(T4AssetBuildPoint, 0);
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [mapId, second.PosY, second.PosX]
        });
        AssertEqual(before - buildCost, test.Asset(T4AssetBuildPoint, 0),
            "Type122 does not refund a later construction after consumption");
    }

    private static void ValidateTheatre4BuildingAuditCostEffects()
    {
        Theatre4BuildingTable station = Theatre4BuildingAuditBuilding(1);
        Theatre4EffectTable build = Theatre4BuildingAuditBuildEffect(station);
        Theatre4EffectTable modify = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 115 && row.SkillCostType == T4AssetBuildPoint);
        Theatre4EffectTable energyConversion = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 211 && row.Params.Count >= 3 && row.Params[0] == T4AssetGold);
        using (Theatre4Case test = new("building-energy-conversion"))
        {
            test.StartRun(1);
            foreach (Theatre4EffectTable effect in new[] { build, modify, energyConversion })
                Theatre4SeedEffect(test, effect.Id);
            int mapId = test.Adventure.Chapters[0].MapId;
            Theatre4GridData first = Theatre4BuildingAuditEmpty(test, mapId);
            int buildCost = Math.Max(0, build.SkillCostCount ?? 0);
            test.SetBuildPoint(buildCost + 20);
            test.SetGold(0);
            int gold = test.Asset(T4AssetGold, 0);
            test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id, Params = [mapId, first.PosY, first.PosX]
            });
            AssertEqual(gold + buildCost * energyConversion.Params[2], test.Asset(T4AssetGold, 0),
                "Type211 pays gold for the first authored build-point cost");

            Theatre4GridData target = Theatre4BuildingAuditPrepareModificationTile(test, mapId,
                modify.Params[0]);
            int modifyCost = Math.Max(0, modify.SkillCostCount ?? 0);
            test.SetBuildPoint(modifyCost + 20);
            gold = test.Asset(T4AssetGold, 0);
            test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = modify.Id, Params = [mapId, target.PosY, target.PosX]
            });
            AssertEqual(gold + modifyCost * energyConversion.Params[2], test.Asset(T4AssetGold, 0),
                "Type211 pays gold for a distinct authored modification cost");

            gold = test.Asset(T4AssetGold, 0);
            test.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id, Params = [mapId, first.PosY, first.PosX]
            }, "Rejected construction does not trigger Type211");
            AssertEqual(gold, test.Asset(T4AssetGold, 0), "Rejected construction gives no Type211 credit");

            foreach (int _ in Enumerable.Range(0, buildCost)) Theatre4BuildingAuditSeedEffectCopy(test, 
                TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == 102
                    && row.Params.Count >= 2 && row.Params[0] == build.Id));
            Theatre4GridData free = Theatre4BuildingAuditEmpty(test, mapId, [first.GridId, target.GridId]);
            test.SetBuildPoint(0);
            gold = test.Asset(T4AssetGold, 0);
            test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id, Params = [mapId, free.PosY, free.PosX]
            });
            AssertEqual(gold, test.Asset(T4AssetGold, 0),
                "A free construction emits no Type211 credit because no build points were debited");
            test.RepeatRejected(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id, Params = [mapId, free.PosY, free.PosX]
            }, "Duplicate free construction");
            AssertEqual(gold, test.Asset(T4AssetGold, 0), "Duplicate construction emits no Type211 credit");
        }

        Theatre4EffectTable diversity = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 212 && row.Params.Count >= 3 && row.Params[0] == T4AssetGold);
        using Theatre4Case diversityCase = new("building-diversity");
        diversityCase.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { build, modify, diversity })
            Theatre4SeedEffect(diversityCase, effect.Id);
        int diversityMap = diversityCase.Adventure.Chapters[0].MapId;
        Theatre4GridData firstBuild = Theatre4BuildingAuditEmpty(diversityCase, diversityMap);
        diversityCase.SetBuildPoint(Math.Max(0, build.SkillCostCount ?? 0) + 20);
        diversityCase.SetGold(0);
        int diversityGold = diversityCase.Asset(T4AssetGold, 0);
        diversityCase.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [diversityMap, firstBuild.PosY, firstBuild.PosX]
        });
        AssertEqual(diversityGold + diversity.Params[2], diversityCase.Asset(T4AssetGold, 0),
            "Type212 pays once for a valid construction");
        Theatre4GridData diversityMod = Theatre4BuildingAuditPrepareModificationTile(diversityCase,
            diversityMap, modify.Params[0]);
        diversityCase.SetBuildPoint(Math.Max(0, modify.SkillCostCount ?? 0) + 20);
        diversityGold = diversityCase.Asset(T4AssetGold, 0);
        diversityCase.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = modify.Id, Params = [diversityMap, diversityMod.PosY, diversityMod.PosX]
        });
        AssertEqual(diversityGold + diversity.Params[2], diversityCase.Asset(T4AssetGold, 0),
            "Type212 pays once for a valid modification");
        Theatre4EffectTable reduction = TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == 102
            && row.Params.Count >= 2 && row.Params[0] == build.Id);
        foreach (int _ in Enumerable.Range(0, Math.Max(0, build.SkillCostCount ?? 0)))
            Theatre4BuildingAuditSeedEffectCopy(diversityCase, reduction);
        Theatre4GridData freeBuild = Theatre4BuildingAuditEmpty(diversityCase, diversityMap,
            [firstBuild.GridId, diversityMod.GridId]);
        diversityCase.SetBuildPoint(0);
        diversityGold = diversityCase.Asset(T4AssetGold, 0);
        diversityCase.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [diversityMap, freeBuild.PosY, freeBuild.PosX]
        });
        AssertEqual(diversityGold + diversity.Params[2], diversityCase.Asset(T4AssetGold, 0),
            "Type212 pays for a valid free construction even without a build-point debit");
        diversityCase.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
        {
            EffectId = build.Id, Params = [diversityMap, freeBuild.PosY, freeBuild.PosX]
        }, "Type212 rejects duplicate construction without another payout");
    }

    private static void ValidateTheatre4BuildingAuditSweepEffects()
    {
        Theatre4EffectTable yellow = Theatre4BuildingAuditEffectByType(205);
        Theatre4EffectTable red = Theatre4BuildingAuditEffectByType(422);
        Theatre4EffectTable towerOnly = Theatre4BuildingAuditEffectByType(17);
        Theatre4EffectTable sweep = Theatre4BuildingAuditEffectByType(34);
        using Theatre4Case test = new("building-sweep-proxies");
        test.StartRun(1);
        foreach (Theatre4EffectTable effect in new[] { yellow, red, towerOnly, sweep })
            Theatre4SeedEffect(test, effect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        var fixture = Theatre4AuditFindFightFixture(reward: true, restartable: true);
        Theatre4GridData yellowGrid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        Theatre4GridData redGrid = Theatre4AuditInstallGridFight(test, fixture, boss: false,
            skipGridId: yellowGrid.GridId);
        Theatre4GridData normalGrid = Theatre4AuditInstallGridFight(test, fixture, boss: false,
            skipGridId: redGrid.GridId);
        test.Adventure.Prosperity = int.MaxValue;
        test.SetGold(int.MaxValue);
        test.Adventure.Colors.Single(color => color.Color == 1).PointCanCost = int.MaxValue;
        test.SaveFixture();
        int markup = 0;
        test.Call(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
        {
            MapId = mapId, PosX = yellowGrid.PosX, PosY = yellowGrid.PosY, SweepType = 2
        });
        markup += sweep.Params[1];
        AssertEqual(1, test.Adventure.EffectSweepTimes,
            "Yellow user-proxy sweep increments its authored sweep counter");
        AssertEqual(markup, test.Adventure.CustomEffects.Single(effect => effect.EffectId == sweep.Id).MarkupRate,
            "Type34 responds to a yellow user-proxy sweep");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == towerOnly.Id).MarkupRate,
            "Type17 ignores a paid user-proxy sweep");

        test.Call(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
        {
            MapId = mapId, PosX = redGrid.PosX, PosY = redGrid.PosY, SweepType = 1
        });
        markup += sweep.Params[1];
        AssertEqual(2, test.Adventure.EffectSweepTimes,
            "Red user-proxy sweep increments its authored sweep counter");
        AssertEqual(markup, test.Adventure.CustomEffects.Single(effect => effect.EffectId == sweep.Id).MarkupRate,
            "Type34 responds to both paid user-proxy sweep types");

        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = mapId, PosX = normalGrid.PosX, PosY = normalGrid.PosY
        });
        test.ResolveFrozenFight();
        AssertEqual(markup, test.Adventure.CustomEffects.Single(effect => effect.EffectId == sweep.Id).MarkupRate,
            "Type34 ignores a normal native fight");
        AssertEqual(0, test.Adventure.CustomEffects.Single(effect => effect.EffectId == towerOnly.Id).MarkupRate,
            "Type17 remains tower-only after normal combat");
    }
    private static void ValidateTheatre4BuildingAuditShopAndTreasureThief()
    {
        Theatre4EffectTable[] shopEffects = TableReaderV2.Parse<Theatre4EffectTable>()
            .Where(row => row.Type == 201 && row.Params.Count > 0 && row.Params[0] > 0)
            .ToArray();
        AssertEqual(true, shopEffects.Length >= 2
            && shopEffects.Select(row => row.Params[0]).Distinct().Count() >= 2,
            "Both authored Type201 shop groups are available");
        foreach (Theatre4EffectTable shopEffect in shopEffects)
        {
            using Theatre4Case shop = new($"building-authored-shop-{shopEffect.Id}");
            shop.StartRun(1);
            Theatre4SeedEffect(shop, shopEffect.Id);
            int mapId = shop.Adventure.Chapters[0].MapId;
            Theatre4GridData target = Theatre4BuildingAuditEmpty(shop, mapId);
            int shopGroupId = shopEffect.Params[0];
            Theatre4ShopGroupTable[] groups = TableReaderV2.Parse<Theatre4ShopGroupTable>()
                .Where(row => row.ShopGroupId == shopGroupId).ToArray();
            if (groups.Length == 0)
                throw new InvalidDataException($"Type201 group {shopGroupId} is not authored.");
            int cost = Math.Max(0, shopEffect.SkillCostCount ?? 0);
            AssertEqual(T4AssetGold, shopEffect.SkillCostType ?? 0,
                $"Type201 {shopEffect.Id} uses its authored gold cost");
            int goldBefore = cost + 100;
            shop.SetGold(goldBefore);
            shop.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = shopEffect.Id, Params = [mapId, target.PosY, target.PosX]
            });
            Theatre4GridData live = shop.Grid(mapId, target.PosX, target.PosY);
            Theatre4ShopTable shopConfig = TableReaderV2.Parse<Theatre4ShopTable>()
                .Single(row => row.Id == live.ContentId);
            HashSet<int> authoredGoodsGroups = shopConfig.GoodsGroupId.Where(id => id > 0).ToHashSet();
            HashSet<int> authoredGoods = TableReaderV2.Parse<Theatre4ShopGoodsTable>()
                .Where(row => authoredGoodsGroups.Contains(row.GroupId)).Select(row => row.Id).ToHashSet();
            AssertEqual(T4GridShop, live.Type, $"Type201 {shopEffect.Id} creates a real shop tile");
            AssertEqual(shopGroupId, live.ContentGroup,
                $"Type201 {shopEffect.Id} persists its authored shop group");
            AssertEqual(true, groups.Any(candidate =>
                    candidate.ShopGroupId == live.ContentGroup && candidate.ShopId == live.ContentId),
                $"Type201 {shopEffect.Id} resolves its weighted group row to an authored shop");
            List<(int GoodsId, int Stock)> shelf = live.Shop?.Goods
                .Select(goods => (goods.GoodsId, goods.Stock)).ToList()
                ?? throw new InvalidDataException($"Type201 {shopEffect.Id} created no shop shelf.");
            AssertEqual(true, shelf.Count > 0
                && shelf.All(goods => authoredGoods.Contains(goods.GoodsId) && goods.Stock > 0),
                $"Type201 {shopEffect.Id} materialises nonempty goods from the ShopTable groups");
            AssertEqual(goldBefore - cost, shop.Asset(T4AssetGold, 0),
                $"Type201 {shopEffect.Id} charges its authored cost once");
            AssertEqual(true, shop.Adventure.CreatedShopGroupIds.Contains(live.ContentGroup),
                $"Type201 {shopEffect.Id} records its authored shop group in run state");
            shop.Relog($"authored shop {shopEffect.Id}");
            Theatre4GridData relogged = shop.Grid(mapId, target.PosX, target.PosY);
            AssertEqual(true, relogged.Shop is { Goods.Count: > 0 }
                && relogged.Shop.Goods.Select(goods => (goods.GoodsId, goods.Stock)).SequenceEqual(shelf),
                $"Type201 {shopEffect.Id} persists goods ids and stock through relog");
            shop.Reject(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest
            {
                EffectId = shopEffect.Id, Params = [mapId, target.PosY, target.PosX]
            }, $"Type201 {shopEffect.Id} rejects duplicate shop placement");
        }

        Theatre4EffectTable thiefEffect = Theatre4BuildingAuditEffectByType(215);
        using Theatre4Case thief = new("building-treasure-thief");
        thief.StartRun(1);
        Theatre4GridData unused = Theatre4BuildingAuditEmpty(thief, thief.Adventure.Chapters[^1].MapId);
        unused.State = T4StateUnknown;
        Theatre4SeedEffect(thief, thiefEffect.Id);
        Theatre4BuildingAuditRebuildEffects(thief);
        int thiefMap = thief.Adventure.Chapters[^1].MapId;
        Theatre4GridData spawned = thief.Chapter(thiefMap).Grids.FirstOrDefault(grid =>
            grid.Type == T4GridMonster && grid.State == T4StateUnknown && grid.Fight is not null
            && thiefEffect.Params.Contains(grid.ContentGroup) && grid.ContentId > 0)
            ?? throw new InvalidDataException("Type215 did not spawn an authored treasure encounter.");
        Theatre4FightTable spawnedFight = Theatre4BuildingAuditFightRow(spawned);
        AssertEqual(4, spawnedFight.FightType,
            "Type215 resolves a legal authored Type4 fight instead of a generic fallback");
        AssertEqual(spawnedFight.Id, spawned.ContentId,
            "Type215 stores the resolved fight id in the spawned grid ContentId");
        thief.Adventure.Ap = Math.Max(10, thief.Adventure.Ap);
        thief.SaveFixture();
        thief.ExploreUntilDiscover(thiefMap, spawned);
        thief.Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest
        {
            MapId = thiefMap, PosX = spawned.PosX, PosY = spawned.PosY
        });
        AssertEqual(T4StateExplored, thief.Grid(thiefMap, spawned.PosX, spawned.PosY).State,
            "Spawned Type215 encounter can be legally explored");
        thief.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = thiefMap, PosX = spawned.PosX, PosY = spawned.PosY
        });
        AssertEqual(spawned.ContentId, thief.State.ActiveEncounter!.FightId,
            "Type215 encounter can be legally located by its spawned grid");
        thief.RecruitAndSetTeam();
        JObject prefight = thief.AuthorizeFrozenFight();
        JObject fightData = prefight["FightData"] as JObject
            ?? throw new InvalidDataException("Type215 legal PreFight omitted FightData.");
        AssertEqual(checked((uint)thief.State.ActiveEncounter!.FightUuid), fightData.Value<uint>("FightId"),
            "Type215 legal PreFight returns the frozen transport fight identity");
    }

    private static void Theatre4BuildingAuditRebuildEffects(Theatre4Case test)
    {
        object mutation = Theatre4NewMutation(test);
        Type mutationType = mutation.GetType();
        Type module = mutationType.DeclaringType
            ?? throw new InvalidDataException("Theatre4 mutation has no module owner.");
        RequiredMethod(module, "RebuildEffects",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [mutationType])
            .Invoke(null, [mutation]);
        test.AdoptMutationState(mutation);
    }

    private static Theatre4BuildingTable Theatre4BuildingAuditBuilding(int type) =>
        TableReaderV2.Parse<Theatre4BuildingTable>().Single(row => row.Type == type);

    private static Theatre4EffectTable Theatre4BuildingAuditBuildEffect(Theatre4BuildingTable building) =>
        TableReaderV2.Parse<Theatre4EffectTable>().Single(row => row.Type == 101
            && row.Params.Count > 0 && row.Params[0] == building.Id);

    private static Theatre4EffectTable Theatre4BuildingAuditEffectByType(int type) =>
        TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == type);


    private static void Theatre4BuildingAuditSeedEffectCopy(Theatre4Case test, Theatre4EffectTable row)
    {
        Theatre4AdventureData adventure = test.Adventure;
        adventure.CustomEffects.Add(new Theatre4EffectData
        {
            Id = adventure.IdSequence + 1, EffectId = row.Id, Count = 1
        });
        adventure.IdSequence += 1;
        test.SaveFixture();
    }

    private static Theatre4GridData Theatre4BuildingAuditEmpty(Theatre4Case test, int mapId,
        IEnumerable<int>? excluded = null)
    {
        HashSet<int> skip = excluded?.ToHashSet() ?? [];
        Theatre4ChapterData chapter = test.Chapter(mapId);
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == mapId)
            ?? throw new InvalidDataException($"Theatre4 map {mapId} is not authored.");
        Theatre4GridData grid = chapter.Grids
            .Where(candidate => !skip.Contains(candidate.GridId)
                && candidate.PosX != candidate.PosY
                && Theatre4BuildingAuditPositionUsable(chapter, map, candidate.PosX, candidate.PosY))
            .OrderByDescending(candidate => candidate.PosX + candidate.PosY)
            .ThenByDescending(candidate => candidate.PosX)
            .ThenByDescending(candidate => candidate.PosY)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no asymmetric construction tile.");
        Theatre4BuildingAuditPrepareEmpty(grid, T4StateProcessed);
        test.SaveFixture();
        return grid;
    }

    private static (Theatre4GridData Anchor, List<Theatre4GridData> Inside, Theatre4GridData Outside)
        Theatre4BuildingAuditRangeFixture(Theatre4Case test, int mapId, int count,
            Func<int, int, int, int, bool> inRange)
    {
        if (count <= 0) throw new InvalidDataException("Theatre4 range fixture needs a positive target count.");
        Theatre4ChapterData chapter = test.Chapter(mapId);
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == mapId)
            ?? throw new InvalidDataException($"Theatre4 map {mapId} is not authored.");
        List<(int X, int Y)> positions = [];
        for (int y = 0; y < map.SizeY; y++)
            for (int x = 0; x < map.SizeX; x++)
                if (x != y && Theatre4BuildingAuditPositionUsable(chapter, map, x, y))
                    positions.Add((x, y));

        foreach ((int anchorX, int anchorY) in positions)
        {
            List<(int X, int Y)> insidePositions = positions
                .Where(pos => (pos.X != anchorX || pos.Y != anchorY)
                    && inRange(anchorX, anchorY, pos.X, pos.Y))
                .Take(count).ToList();
            if (insidePositions.Count < count) continue;
            List<(int X, int Y)> outsidePositions = positions
                .Where(pos => (pos.X != anchorX || pos.Y != anchorY)
                    && !inRange(anchorX, anchorY, pos.X, pos.Y))
                .Take(1).ToList();
            if (outsidePositions.Count == 0) continue;

            Theatre4GridData anchor = Theatre4BuildingAuditAuthoredGrid(chapter, map,
                anchorX, anchorY);
            List<Theatre4GridData> inside = insidePositions
                .Select(pos => Theatre4BuildingAuditAuthoredGrid(chapter, map, pos.X, pos.Y))
                .ToList();
            Theatre4GridData outside = Theatre4BuildingAuditAuthoredGrid(chapter, map,
                outsidePositions[0].X, outsidePositions[0].Y);
            return (anchor, inside, outside);
        }
        throw new InvalidDataException(
            $"Theatre4 map {mapId} has no authored in-bounds non-hidden range fixture.");
    }

    private static bool Theatre4BuildingAuditPositionUsable(Theatre4ChapterData chapter,
        Theatre4MapTable map, int x, int y)
    {
        if (x < 0 || y < 0 || x >= map.SizeX || y >= map.SizeY
            || T4EffectAuditAuthoredHiddenCell(map, x, y))
            return false;
        Theatre4GridData? existing = chapter.Grids.FirstOrDefault(grid =>
            grid.PosX == x && grid.PosY == y);
        return existing is null
            || existing.Type is not (T4GridNothing or T4GridStart or T4GridBoss
                or T4GridBlank or T4GridBuilding);
    }

    private static Theatre4GridData Theatre4BuildingAuditAuthoredGrid(
        Theatre4ChapterData chapter, Theatre4MapTable map, int x, int y)
    {
        if (!Theatre4BuildingAuditPositionUsable(chapter, map, x, y))
            throw new InvalidDataException(
                $"Theatre4 fixture coordinate {chapter.MapId}/{x}/{y} is not an authored open tile.");
        return T4EffectAuditAuthoredGrid(chapter, map, x, y);
    }

    private static void Theatre4BuildingAuditPrepareEmpty(Theatre4GridData grid, int state)
    {
        grid.Type = T4GridEmpty;
        grid.State = state;
        grid.ContentGroup = 0;
        grid.ContentId = 0;
        grid.Fight = null;
        grid.Shop = null;
        grid.Event = null;
        grid.Building = null;
    }

    private static void Theatre4BuildingAuditPrepareExploreTile(Theatre4GridData grid, int color,
        int resource)
    {
        Theatre4BuildingAuditPrepareEmpty(grid, T4StateDiscover);
        grid.Color = color;
        grid.ColorResource = resource;
    }

    private static void Theatre4BuildingAuditPrepareObstacle(Theatre4GridData grid)
    {
        Theatre4BuildingAuditPrepareEmpty(grid, T4StateExplored);
        grid.Type = T4GridHurdle;
    }

    private static Theatre4GridData Theatre4BuildingAuditResourceSource(Theatre4ChapterData chapter,
        int excluded)
    {
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == chapter.MapId)
            ?? throw new InvalidDataException($"Theatre4 map {chapter.MapId} is not authored.");
        return chapter.Grids
            .Where(grid => grid.GridId != excluded && grid.Color is >= 1 and <= 3
                && grid.ColorResource > 0
                && Theatre4BuildingAuditPositionUsable(chapter, map, grid.PosX, grid.PosY))
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no authored coloured-resource tile.");
    }

    private static Theatre4GridData Theatre4BuildingAuditPrepareModificationTile(Theatre4Case test,
        int mapId, int newColor)
    {
        Theatre4ChapterData chapter = test.Chapter(mapId);
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == mapId)
            ?? throw new InvalidDataException($"Theatre4 map {mapId} is not authored.");
        Theatre4GridData source = chapter.Grids
            .Where(grid => grid.Color is >= 1 and <= 3 && grid.Color != newColor
                && grid.ColorResource > 0
                && Theatre4BuildingAuditPositionUsable(chapter, map, grid.PosX, grid.PosY))
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Modification fixture has no coloured authored tile.");
        int color = source.Color;
        int resource = source.ColorResource;
        Theatre4BuildingAuditPrepareExploreTile(source, color, resource);
        test.SaveFixture();
        return source;
    }


    private static bool Theatre4BuildingAuditCross(Theatre4GridData anchor,
        Theatre4GridData target, int radius) =>
        anchor.GridId != target.GridId &&
        ((anchor.PosX == target.PosX && Math.Abs(anchor.PosY - target.PosY) <= radius)
            || (anchor.PosY == target.PosY && Math.Abs(anchor.PosX - target.PosX) <= radius));

    private static bool Theatre4BuildingAuditDiamond(Theatre4GridData anchor,
        Theatre4GridData target)
    {
        int x = Math.Abs(anchor.PosX - target.PosX), y = Math.Abs(anchor.PosY - target.PosY);
        return anchor.GridId != target.GridId &&
            (x == 0 && y is 1 or 2 || y == 0 && x is 1 or 2 || x == 1 && y == 1);
    }

    private static bool Theatre4BuildingAuditCube(Theatre4GridData anchor,
        Theatre4GridData target) => anchor.GridId != target.GridId
        && Math.Max(Math.Abs(anchor.PosX - target.PosX), Math.Abs(anchor.PosY - target.PosY)) == 1;

    private static IEnumerable<JObject> Theatre4BuildingAuditPushJson(Theatre4Case test, string name) =>
        test.Pushes.Where(push => push.Name == name)
            .Select(push => JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content)));

    private static bool Theatre4BuildingAuditChangePushContainsBuilding(Theatre4Case test, int buildingId) =>
        Theatre4BuildingAuditPushJson(test, nameof(NotifyTheatre4ChangeGrids)).Any(body =>
            body["Grids"] is JArray grids && grids.OfType<JObject>().Any(grid =>
                grid["Building"] is JObject building && building.Value<int?>("BuildingId") == buildingId));

    private static Theatre4FightTable Theatre4BuildingAuditFightRow(Theatre4GridData grid) =>
        TableReaderV2.Parse<Theatre4FightTable>().FirstOrDefault(row => row.Id == grid.ContentId)
        ?? throw new InvalidDataException($"Grid {grid.GridId} has no authored fight row {grid.ContentId}.");

    private static void Theatre4BuildingAuditInstallCombatGrid(Theatre4GridData grid,
        int contentGroup, int contentId, Theatre4FightData fight, bool boss)
    {
        grid.Type = boss ? T4GridBoss : T4GridMonster;
        grid.State = T4StateDiscover;
        grid.ContentGroup = contentGroup;
        grid.ContentId = contentId;
        grid.Fight = new Theatre4FightData
        {
            FightGroupId = fight.FightGroupId,
            StageId = fight.StageId,
            HpPercent = 10000,
            PunishCountdown = fight.PunishCountdown,
            FightEvents = fight.FightEvents.ToList(),
            Rewards = []
        };
        grid.Shop = null;
        grid.Event = null;
        grid.Building = null;
    }

    private static (Theatre4GridData Anchor, List<Theatre4GridData> Ordinary, Theatre4GridData Boss)
        Theatre4BuildingAuditTowerFixture(Theatre4Case test, int mapId)
    {
        Theatre4ChapterData chapter = test.Chapter(mapId);
        Theatre4GridData source = chapter.Grids
            .Where(grid => grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentId > 0)
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Tower fixture has no authored ordinary fight.");
        Theatre4FightData ordinaryFight = source.Fight
            ?? throw new InvalidDataException("Tower fixture source has no fight state.");
        int ordinaryGroup = source.ContentGroup;
        int ordinaryId = source.ContentId;
        Theatre4GridData bossSource = chapter.Grids
            .Where(grid => grid.Type == T4GridBoss && grid.Fight is not null && grid.ContentId > 0)
            .OrderBy(grid => grid.PosY).ThenBy(grid => grid.PosX)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Tower fixture has no authored boss fight.");
        Theatre4FightData bossFight = bossSource.Fight
            ?? throw new InvalidDataException("Tower fixture boss source has no fight state.");
        int bossGroup = bossSource.ContentGroup;
        int bossId = bossSource.ContentId;
        (Theatre4GridData anchor, List<Theatre4GridData> neighbours, _) =
            Theatre4BuildingAuditRangeFixture(test, mapId, 3,
                static (anchorX, anchorY, targetX, targetY) =>
                    (anchorX == targetX && Math.Abs(anchorY - targetY) <= 1)
                    || (anchorY == targetY && Math.Abs(anchorX - targetX) <= 1));
        Theatre4BuildingAuditPrepareEmpty(anchor, T4StateProcessed);
        Theatre4BuildingAuditInstallCombatGrid(neighbours[0], ordinaryGroup, ordinaryId, ordinaryFight,
            boss: false);
        Theatre4BuildingAuditInstallCombatGrid(neighbours[1], ordinaryGroup, ordinaryId, ordinaryFight,
            boss: false);
        Theatre4BuildingAuditInstallCombatGrid(neighbours[2], bossGroup, bossId, bossFight, boss: true);
        test.SaveFixture();
        return (anchor, [neighbours[0], neighbours[1]], neighbours[2]);
    }

    private static (Theatre4GridData Tower, Theatre4GridData Target, Theatre4FightTable Fight)
        Theatre4BuildingAuditInstallSingleTowerTarget(Theatre4Case test, int mapId,
        Theatre4GridData depot, (int Group, int Id, Theatre4FightData Fight) combatTemplate,
        bool inRange, IEnumerable<int>? excluded = null)
    {
        HashSet<int> skip = excluded?.ToHashSet() ?? [];
        Theatre4ChapterData chapter = test.Chapter(mapId);
        Theatre4MapTable map = TableReaderV2.Parse<Theatre4MapTable>()
            .SingleOrDefault(row => row.Id == mapId)
            ?? throw new InvalidDataException($"Theatre4 map {mapId} is not authored.");
        Theatre4GridData liveDepot = test.Grid(mapId, depot.PosX, depot.PosY);

        List<(int X, int Y)> positions = [];
        for (int y = 0; y < map.SizeY; y++)
            for (int x = 0; x < map.SizeX; x++)
                if (x != y && Theatre4BuildingAuditPositionUsable(chapter, map, x, y))
                    positions.Add((x, y));
        foreach ((int targetX, int targetY) in positions)
        {
            int targetId = 10_000 + targetX * 100 + targetY;
            if (targetId == liveDepot.GridId || skip.Contains(targetId)
                || (inRange != (Math.Max(Math.Abs(liveDepot.PosX - targetX),
                    Math.Abs(liveDepot.PosY - targetY)) == 1)))
                continue;
            List<(int X, int Y)> towerPositions = positions
                .Where(pos =>
                {
                    int id = 10_000 + pos.X * 100 + pos.Y;
                    return id != targetId && id != liveDepot.GridId && !skip.Contains(id)
                        && Theatre4BuildingAuditCross(
                            new Theatre4GridData { GridId = targetId, PosX = targetX, PosY = targetY },
                            new Theatre4GridData { GridId = id, PosX = pos.X, PosY = pos.Y }, 1);
                })
                .Take(1).ToList();
            if (towerPositions.Count == 0) continue;
            (int towerX, int towerY) = towerPositions[0];

            Theatre4GridData target = Theatre4BuildingAuditAuthoredGrid(chapter, map, targetX, targetY);
            Theatre4GridData tower = Theatre4BuildingAuditAuthoredGrid(chapter, map, towerX, towerY);
            foreach (Theatre4GridData neighbour in chapter.Grids.Where(grid =>
                grid.GridId != tower.GridId && Theatre4BuildingAuditCross(tower, grid, 1)
                && grid.GridId != liveDepot.GridId
                && Theatre4BuildingAuditPositionUsable(chapter, map, grid.PosX, grid.PosY)).ToList())
                Theatre4BuildingAuditPrepareEmpty(neighbour, T4StateProcessed);
            Theatre4BuildingAuditPrepareEmpty(tower, T4StateProcessed);
            Theatre4BuildingAuditInstallCombatGrid(target, combatTemplate.Group, combatTemplate.Id,
                combatTemplate.Fight, boss: false);
            test.SaveFixture();
            return (tower, target, Theatre4BuildingAuditFightRow(target));
        }
        throw new InvalidDataException("Single tower fixture has no requested depot-range target.");
    }
}

