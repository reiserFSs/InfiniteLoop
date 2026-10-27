using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.stronghold;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    // Player report: winning a Norman Revival chapter's last group (one with FinishRelatedId) did not finish the
    // related sub-groups, although the client already charges their endurance (XStrongholdManager.GetGroupCostEndurance)
    // and XUiStrongholdFightSettleWin lists one reward line per finished group, fought group last.
    private static void ValidateStrongholdRelatedFightCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride stageMongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out _);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out var playerSaves, out _, out _);
        var groups = TableReaderV2.Parse<StrongholdGroupTable>().ToDictionary(row => row.Id);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        var prepare = RequiredMethod(module, "PrepareLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player)]);

        List<int> RelatedClosure(int groupId)
        {
            List<int> order = [];
            HashSet<int> seen = [groupId];
            void Visit(int id)
            {
                foreach (int related in groups[id].FinishRelatedId.Where(value => value > 0))
                {
                    if (!seen.Add(related)) continue;
                    Visit(related);
                    order.Add(related);
                }
            }
            Visit(groupId);
            return order;
        }

        long uid = 48_960;
        int tested = 0;
        Player probe = CreateDrawCompatibilityPlayer(uid);
        probe.PlayerData.Level = 80;
        prepare.Invoke(null, [probe]);
        int[] chapterLastGroups = probe.Stronghold.GroupStageDatas
            .Select(data => data.Id)
            .Where(id => groups[id].FinishRelatedId.Any(value => value > 0)
                && RelatedClosure(id).All(related => probe.Stronghold.GroupStageDatas.Any(data => data.Id == related)))
            .Take(2).ToArray();
        AssertEqual(2, chapterLastGroups.Length, "two authoritative chapter-last groups with allocated related groups");

        foreach (int lastGroupId in chapterLastGroups)
        {
            uid++;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 80;
            Character roster = CreateDrawCompatibilityCharacter(uid);
            roster.Characters = [new CharacterData { Id = 1_021_001 }];
            using LoopbackSessionHarness h = new(roster, player,
                CreateDrawCompatibilityInventory(uid, []), "stronghold-related-fight");
            h.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            prepare.Invoke(null, [player]);
            StrongholdState state = player.Stronghold;
            List<int> related = RelatedClosure(lastGroupId);
            foreach (int id in related.Append(lastGroupId))
                if (groups[id].PreId is int predecessor and > 0 && !related.Contains(predecessor) && predecessor != lastGroupId
                    && !state.FinishGroupIds.Contains(predecessor))
                    state.FinishGroupIds.Add(predecessor);
            int totalCost = related.Append(lastGroupId).Sum(id => groups[id].Endurance ?? 0);
            state.Endurance = totalCost;
            int packetId = 48_970;

            InvokeRegisteredRequestHandler("SetStrongholdFightTeamRequest", h.Session, packetId,
                new SetStrongholdFightTeamRequest
                {
                    Id = lastGroupId,
                    TeamInfos = [new StrongholdTeamInfo { Id = 1, CharacterInfos = [new StrongholdCharacterInfo { Id = 1_021_001, Pos = 1 }] }]
                });
            AssertEqual(0, ReadStrongholdResponse<SetStrongholdFightTeamResponse>(h, packetId++, out _).Code, "team set on chapter-last group");

            List<uint> stages = state.GroupStageDatas.Single(data => data.Id == lastGroupId).StageIds;
            FightSettleResponse settle = null!;
            List<string> pushes = [];
            foreach (uint stageId in stages)
            {
                InvokeRegisteredRequestHandler(nameof(PreFightRequest), h.Session, packetId,
                    new PreFightRequest { PreFightData = new() { StageId = stageId, CardIds = [1_021_001], CaptainPos = 1, FirstFightPos = 1 } });
                PreFightResponse preFight = ReadStrongholdResponse<PreFightResponse>(h, packetId++, out _);
                AssertEqual(0, preFight.Code, $"chapter-last group {lastGroupId} pre-fight {stageId}");
                InvokeRegisteredRequestHandler(nameof(FightSettleRequest), h.Session, packetId,
                    CreateMissingStageSettleRequest(stageId, preFight.FightData.FightId, uid));
                settle = ReadStrongholdResponse<FightSettleResponse>(h, packetId++, out pushes);
                AssertEqual(0, settle.Code, $"chapter-last group {lastGroupId} settle {stageId}");
            }

            StrongholdFightResult fightResult = MessagePack.MessagePackSerializer.Deserialize<StrongholdFightResult>(
                MessagePack.MessagePackSerializer.Serialize<object>(settle.Settle.StrongholdFightResult));
            List<int> lineIds = fightResult.GroupFightResultInfos.Select(line => line.GroupId).ToList();
            AssertEqual(string.Join(",", related.Append(lastGroupId)), string.Join(",", lineIds),
                "one result line per finished group, related groups first and the fought group last");
            AssertEqual(true, related.Append(lastGroupId).All(state.FinishGroupIds.Contains),
                "winning the chapter-last group finishes every related sub-group");
            AssertEqual(0, state.Endurance, "endurance charged once for the whole chapter");
            AssertEqual(related.Count + 1, pushes.Count(name => name == nameof(NotifyDeleteStrongholdGroupData)),
                "every finished group is removed from the client's active groups");
            foreach (int id in related)
            {
                AssertEqual(1, h.Session.inventory.AppliedRewardClaims.Count(key =>
                    key == $"stronghold:{uid}:{state.ActivityId}:{id}" || key.StartsWith($"stronghold:{uid}:{state.ActivityId}:{id}:", StringComparison.Ordinal)),
                    $"related group {id} reward granted once");
                AssertEqual(true, state.GroupStageDatas.Single(data => data.Id == id).StageIds
                    .All(stageId => h.Session.stage.Stages[stageId].Passed), $"related group {id} stages recorded as passed");
            }
            Player persisted = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(playerSaves.LastReplacement!.ToBson());
            AssertEqual(true, related.Append(lastGroupId).All(persisted.Stronghold.FinishGroupIds.Contains),
                "related completion survives reload");
            AssertEqual(0, persisted.Stronghold.PendingStageId, "no pending battle after chapter completion");
            tested++;
        }
        AssertEqual(2, tested, "two distinct chapter-last groups exercised");
        Console.WriteLine("Stronghold related fight compatibility: chapter-last wins finish related groups passed.");
    }
}
