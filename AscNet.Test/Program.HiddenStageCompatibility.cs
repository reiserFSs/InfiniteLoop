using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using MessagePack;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateHiddenStageCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);

        const int stageSixId = 13_010_116;
        const int hiddenStageId = 13_010_120;
        const long playerId = 88_120;
        StageTable[] stageRows = TableReaderV2.Parse<StageTable>().ToArray();
        StageTable stageSix = stageRows.Single(row => row.StageId == stageSixId);
        int predecessorStageId = stageSix.PreStageId.First(stageId => stageId > 0);
        int objectiveEventId = stageSix.PreEventId.First(eventId => eventId > 0);
        int normalReplayEventId = Convert.ToInt32(stageSix.NormalEventIdOnPassed);
        (int TargetStageId, int ProducerStageId, int EventId, int[] TargetPreStageIds) foreign = stageRows
            .Where(target => target.StageId != hiddenStageId)
            .SelectMany(target => target.UnlockEventId.Where(eventId => eventId > 0)
                .Select(eventId => (target, EventId: eventId)))
            .SelectMany(candidate => stageRows
                .Where(producer => producer.StageId != stageSixId
                    && producer.StageId != candidate.target.StageId
                    && producer.PreEventId.Contains(candidate.EventId))
                .Select(producer => (
                    TargetStageId: candidate.target.StageId,
                    ProducerStageId: producer.StageId,
                    EventId: candidate.EventId,
                    TargetPreStageIds: candidate.target.PreStageId.Where(stageId => stageId > 0).ToArray())))
            .First(candidate => candidate.EventId != objectiveEventId && candidate.TargetPreStageIds.Length > 0);

        MethodInfo buildNotifyLogin = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNotifyLogin",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session)]);

        LoopbackSessionHarness Harness(long uid, string name)
        {
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness harness = new(
                character,
                CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []),
                name);
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            harness.Session.stage.AddStage(new StageDatum { StageId = predecessorStageId, Passed = true });
            return harness;
        }

        JObject Pre(LoopbackSessionHarness harness, int packetId, string name)
        {
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, packetId, new PreFightRequest
            {
                PreFightData = new()
                {
                    ChallengeCount = 1,
                    StageId = stageSixId,
                    CardIds = [1_021_001],
                    RobotIds = [],
                    FirstFightPos = 1,
                    CaptainPos = 1,
                    IsHasAssist = false
                }
            });
            JObject response = ReadResponseMapPayload(harness, packetId, nameof(PreFightResponse), name);
            AssertEqual(0L, RequiredValue<long>(response, "Code", JTokenType.Integer, name), $"{name} PreFightResponse Code");
            AssertEqual((long)stageSixId, RequiredValue<long>(RequiredObject(response, "FightData", name), "StageId", JTokenType.Integer, name), $"{name} FightData.StageId");
            return response;
        }

        (List<(string Name, JObject Payload)> Pushes, JObject Response) Settle(
            LoopbackSessionHarness harness,
            long fightId,
            int packetId,
            IReadOnlyList<object> events,
            bool isWin,
            bool isForceExit,
            long uid,
            string name)
        {
            FightSettleRequest request = CreateMissingStageSettleRequest((uint)stageSixId, fightId, uid);
            request.Result.IsWin = isWin;
            request.Result.IsForceExit = isForceExit;
            request.Result.EventSet = events.ToArray();
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, packetId, request);

            List<(string Name, JObject Payload)> pushes = [];
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket($"{name} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
                    continue;
                }

                AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(packetId, response.Id, $"{name} response packet id");
                AssertEqual(nameof(FightSettleResponse), response.Name, $"{name} response packet name");
                return (pushes, JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content)));
            }

            throw new InvalidDataException($"{name}: missing FightSettleResponse.");
        }

        using (LoopbackSessionHarness harness = Harness(playerId, "hidden-stage-clear"))
        {
            JObject firstPreFight = Pre(harness, 88_201, "hidden stage first attempt");
            long firstFightId = RequiredValue<long>(RequiredObject(firstPreFight, "FightData", "hidden stage first attempt"), "FightId", JTokenType.Integer, "hidden stage first attempt");
            var first = Settle(harness, firstFightId, 88_202, Array.Empty<object>(), true, false, playerId, "hidden stage first clear");
            AssertEqual(0L, RequiredValue<long>(first.Response, "Code", JTokenType.Integer, "hidden stage first clear"), "hidden stage first clear response Code");
            AssertEqual(false, first.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "objective-absent clear remains locked");
            AssertEqual(true, harness.Session.stage.Stages.TryGetValue(stageSixId, out StageDatum? firstStage) && firstStage.Passed, "first clear persists stage 6 Passed");

            NotifyLogin firstLogin = buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("first hidden-stage login returned no NotifyLogin.");
            NotifyLogin firstLoginWire = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(firstLogin));
            AssertIntegerList([], ReadIntegerList(firstLoginWire.FubenData.UnlockHideStages, "first hidden-stage UnlockHideStages").ToArray(), "objective-absent login remains locked");

            Dictionary<int, long> beforeControlReplay = harness.Session.inventory.Items
                .ToDictionary(item => item.Id, item => item.Count);
            JObject replayPreFight = Pre(harness, 88_203, "hidden stage replay");
            long replayFightId = RequiredValue<long>(RequiredObject(replayPreFight, "FightData", "hidden stage replay"), "FightId", JTokenType.Integer, "hidden stage replay");
            var control = Settle(harness, replayFightId, 88_204, Array.Empty<object>(), true, false, playerId, "hidden stage ordinary replay");
            AssertEqual(false, control.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "ordinary replay remains locked");
            Dictionary<int, long> afterControlReplay = harness.Session.inventory.Items
                .ToDictionary(item => item.Id, item => item.Count);

            JObject objectivePreFight = Pre(harness, 88_205, "hidden stage objective replay");
            long objectiveFightId = RequiredValue<long>(RequiredObject(objectivePreFight, "FightData", "hidden stage objective replay"), "FightId", JTokenType.Integer, "hidden stage objective replay");
            var accepted = Settle(harness, objectiveFightId, 88_206, new object[] { objectiveEventId }, true, false, playerId, "hidden stage accepted objective");
            JObject acceptedSettle = RequiredObject(accepted.Response, "Settle", "hidden stage accepted objective");
            AssertEqual(0L, RequiredValue<long>(accepted.Response, "Code", JTokenType.Integer, "hidden stage accepted objective"), "accepted objective response Code");
            AssertEqual((long)stageSixId, RequiredValue<long>(acceptedSettle, "StageId", JTokenType.Integer, "hidden stage accepted objective"), "accepted objective response StageId");
            AssertEqual(true, RequiredValue<bool>(acceptedSettle, "IsWin", JTokenType.Boolean, "hidden stage accepted objective"), "accepted objective response IsWin");

            int stagePushIndex = accepted.Pushes.FindIndex(push => push.Name == nameof(NotifyStageData));
            int unlockPushIndex = accepted.Pushes.FindIndex(push => push.Name == "NotifyUnlockHideStage");
            AssertEqual(true, stagePushIndex >= 0 && stagePushIndex < unlockPushIndex, "NotifyStageData precedes hidden unlock push");
            (string Name, JObject Payload) stagePush = accepted.Pushes.Single(push => push.Name == nameof(NotifyStageData));
            JObject pushedStage = ((JArray)RequiredToken(stagePush.Payload, "StageList", JTokenType.Array, "accepted objective NotifyStageData"))
                .OfType<JObject>()
                .Single(row => RequiredValue<long>(row, "StageId", JTokenType.Integer, "accepted objective NotifyStageData") == stageSixId);
            AssertEqual(true, RequiredValue<bool>(pushedStage, "Passed", JTokenType.Boolean, "accepted objective NotifyStageData"), "accepted objective stage is passed");
            (string Name, JObject Payload) unlockPush = accepted.Pushes.Single(push => push.Name == "NotifyUnlockHideStage");
            AssertEqual(true, unlockPush.Payload.Properties().Select(property => property.Name).SequenceEqual(["UnlockHideStage"]), "hidden unlock push exact fields");
            AssertEqual((long)hiddenStageId, RequiredValue<long>(unlockPush.Payload, "UnlockHideStage", JTokenType.Integer, "accepted objective NotifyUnlockHideStage"), "hidden unlock push stage id");
            Dictionary<int, long> afterObjectiveReplay = harness.Session.inventory.Items
                .ToDictionary(item => item.Id, item => item.Count);
            foreach (int itemId in beforeControlReplay.Keys.Concat(afterControlReplay.Keys).Concat(afterObjectiveReplay.Keys).Distinct())
            {
                AssertEqual(
                    afterControlReplay.GetValueOrDefault(itemId) - beforeControlReplay.GetValueOrDefault(itemId),
                    afterObjectiveReplay.GetValueOrDefault(itemId) - afterControlReplay.GetValueOrDefault(itemId),
                    $"hidden objective preserves the ordinary reward delta for item {itemId}");
            }
            AssertEqual(false, harness.Session.stage.Stages.TryGetValue(hiddenStageId, out StageDatum? hiddenAfterClear) && hiddenAfterClear.Passed, "hidden stage is not falsely passed");

            JArray firstNormalEvents = (JArray)RequiredToken(RequiredObject(firstPreFight, "FightData", "first attempt"), "NormalEventIds", JTokenType.Array, "first attempt");
            JArray replayNormalEvents = (JArray)RequiredToken(RequiredObject(replayPreFight, "FightData", "replay attempt"), "NormalEventIds", JTokenType.Array, "replay attempt");
            AssertEqual(false, firstNormalEvents.Values<long>().Contains(normalReplayEventId), "first attempt omits passed-stage normal event");
            AssertEqual(true, replayNormalEvents.Values<long>().Contains(normalReplayEventId), "replay includes NormalEventIdOnPassed");


            JObject repeatedPreFight = Pre(harness, 88_207, "hidden stage repeated objective");
            long repeatedFightId = RequiredValue<long>(RequiredObject(repeatedPreFight, "FightData", "hidden stage repeated objective"), "FightId", JTokenType.Integer, "hidden stage repeated objective");
            var repeated = Settle(harness, repeatedFightId, 88_208, new object[] { objectiveEventId }, true, false, playerId, "hidden stage repeated objective");
            AssertEqual(false, repeated.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "repeated objective does not duplicate unlock push");

        }

        using (LoopbackSessionHarness failedHarness = Harness(playerId + 1, "hidden-stage-failed"))
        {
            JObject preFight = Pre(failedHarness, 88_211, "failed hidden stage");
            long fightId = RequiredValue<long>(RequiredObject(preFight, "FightData", "failed hidden stage"), "FightId", JTokenType.Integer, "failed hidden stage");
            var failed = Settle(failedHarness, fightId, 88_212, new object[] { objectiveEventId }, false, true, playerId + 1, "failed/retreated hidden stage");
            AssertEqual(0L, RequiredValue<long>(failed.Response, "Code", JTokenType.Integer, "failed/retreated hidden stage"), "failed/retreated response Code");
            AssertEqual(false, RequiredValue<bool>(RequiredObject(failed.Response, "Settle", "failed/retreated hidden stage"), "IsWin", JTokenType.Boolean, "failed/retreated hidden stage"), "failed/retreated response IsWin");
            AssertEqual(false, failed.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "failed/retreated report cannot unlock");
            AssertEqual(false, failedHarness.Session.stage.Stages.TryGetValue(hiddenStageId, out StageDatum? failedHidden) && failedHidden.Passed, "failed/retreated report does not persist hidden stage");
        }

        using (LoopbackSessionHarness mismatchHarness = Harness(playerId + 2, "hidden-stage-mismatch"))
        {
            JObject preFight = Pre(mismatchHarness, 88_221, "mismatched hidden stage");
            long fightId = RequiredValue<long>(RequiredObject(preFight, "FightData", "mismatched hidden stage"), "FightId", JTokenType.Integer, "mismatched hidden stage");
            var mismatch = Settle(mismatchHarness, fightId + 1, 88_222, new object[] { objectiveEventId }, true, false, playerId + 2, "mismatched hidden stage");
            AssertEqual(false, RequiredValue<long>(mismatch.Response, "Code", JTokenType.Integer, "mismatched hidden stage") == 0, "mismatched fight report is rejected");
            AssertEqual(false, mismatch.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "mismatched fight report cannot unlock");
            AssertEqual(false, mismatchHarness.Session.stage.Stages.TryGetValue(hiddenStageId, out StageDatum? mismatchHidden) && mismatchHidden.Passed, "mismatched report does not persist hidden stage");
        }

        using (LoopbackSessionHarness foreignHarness = Harness(playerId + 3, "hidden-stage-foreign-event"))
        {
            foreach (int prerequisite in foreign.TargetPreStageIds)
                foreignHarness.Session.stage.AddStage(new StageDatum { StageId = prerequisite, Passed = true });
            JObject preFight = Pre(foreignHarness, 88_231, $"foreign event from stage {foreign.ProducerStageId}");
            long fightId = RequiredValue<long>(RequiredObject(preFight, "FightData", "foreign event hidden stage"), "FightId", JTokenType.Integer, "foreign event hidden stage");
            var foreignResult = Settle(foreignHarness, fightId, 88_232, new object[] { foreign.EventId }, true, false, playerId + 3, "foreign event hidden stage");
            AssertEqual(0L, RequiredValue<long>(foreignResult.Response, "Code", JTokenType.Integer, "foreign event hidden stage"), "valid foreign-event source report response Code");
            AssertEqual(false, foreignResult.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), $"foreign event from producer {foreign.ProducerStageId} cannot unlock target {foreign.TargetStageId}");
            AssertEqual(false, foreignHarness.Session.stage.Stages.TryGetValue(foreign.TargetStageId, out StageDatum? foreignHidden) && foreignHidden.Passed, "foreign event does not persist target hidden stage");
            NotifyLogin foreignLogin = buildNotifyLogin.Invoke(null, [foreignHarness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("foreign event login returned no NotifyLogin.");
            NotifyLogin foreignWire = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(foreignLogin));
            AssertIntegerList([], ReadIntegerList(foreignWire.FubenData.UnlockHideStages, "foreign event UnlockHideStages").ToArray(), "foreign event login stays locked");
        }

        using (LoopbackSessionHarness malformedHarness = Harness(playerId + 4, "hidden-stage-malformed-event"))
        {
            JObject preFight = Pre(malformedHarness, 88_241, "malformed hidden stage");
            long fightId = RequiredValue<long>(RequiredObject(preFight, "FightData", "malformed hidden stage"), "FightId", JTokenType.Integer, "malformed hidden stage");
            var malformed = Settle(malformedHarness, fightId, 88_242, new object[] { objectiveEventId.ToString() }, true, false, playerId + 4, "malformed hidden stage");
            AssertEqual(false, malformed.Pushes.Any(push => push.Name == "NotifyUnlockHideStage"), "string objective event cannot unlock hidden stage");
        }
        using (LoopbackSessionHarness saveFailureHarness = Harness(playerId + 5, "hidden-stage-save-failure"))
        {
            JObject preFight = Pre(saveFailureHarness, 88_251, "hidden stage save failure");
            long retainedFightId = RequiredValue<long>(RequiredObject(preFight, "FightData", "hidden stage save failure"), "FightId", JTokenType.Integer, "hidden stage save failure");
            FightSettleRequest failedRequest = CreateMissingStageSettleRequest((uint)stageSixId, retainedFightId, playerId + 5);
            failedRequest.Result.EventSet = new object[] { objectiveEventId };
            stageSaves.ThrowOnReplaceOne = true;
            try { InvokeRegisteredRequestHandler(nameof(FightSettleRequest), saveFailureHarness.Session, 88_252, failedRequest); throw new InvalidDataException("injected Stage.SaveChecked failure was not raised."); }
            catch (InvalidDataException exception) when (exception.InnerException is MongoDB.Driver.MongoException) { }
            stageSaves.ThrowOnReplaceOne = false;

            AssertEqual(false, saveFailureHarness.TryReadAvailablePacket("hidden stage failed save packet", out _), "failed Stage.SaveChecked emits no response or success push");
            AssertEqual(false, saveFailureHarness.Session.stage.Stages.TryGetValue(stageSixId, out StageDatum? failedSaveSource) && failedSaveSource.Passed, "failed Stage.SaveChecked restores source stage datum");
            NotifyLogin failedSaveLogin = buildNotifyLogin.Invoke(null, [saveFailureHarness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("failed Stage.SaveChecked returned no NotifyLogin.");
            NotifyLogin failedSaveWire = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(failedSaveLogin));
            AssertIntegerList([], ReadIntegerList(failedSaveWire.FubenData.UnlockHideStages, "failed Stage.SaveChecked UnlockHideStages").ToArray(), "failed Stage.SaveChecked login stays locked");
            var retry = Settle(saveFailureHarness, retainedFightId, 88_253, new object[] { objectiveEventId }, true, false, playerId + 5, "hidden stage save retry");
            AssertEqual(0L, RequiredValue<long>(retry.Response, "Code", JTokenType.Integer, "hidden stage save retry"), "hidden stage save retry response Code");
            AssertEqual(1, retry.Pushes.Count(push => push.Name == "NotifyUnlockHideStage"), "hidden stage save retry emits one unlock push");
            byte[] retryBson = stageSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("hidden stage save retry did not produce durable Stage BSON.");
            AscNet.Common.Database.Stage retryStage = BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(retryBson);
            using LoopbackSessionHarness retryLoginHarness = Harness(playerId + 15, "hidden-stage-save-retry-login");
            retryLoginHarness.Session.stage = retryStage;
            NotifyLogin retryLogin = buildNotifyLogin.Invoke(null, [retryLoginHarness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("hidden stage save retry returned no NotifyLogin.");
            NotifyLogin retryWire = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(retryLogin));
            AssertIntegerList([hiddenStageId], ReadIntegerList(retryWire.FubenData.UnlockHideStages, "hidden stage save retry UnlockHideStages").ToArray(), "hidden stage save retry BSON relog unlocks target");
            AssertEqual(false, retryWire.FubenData.StageData.TryGetValue(hiddenStageId, out StageDatum? retryHidden) && retryHidden.Passed, "hidden stage save retry BSON relog does not mark target passed");
        }
    }
}
