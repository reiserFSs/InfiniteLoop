using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.equip.equipguide;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateEquipGuideGoalCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        const long uid = 99_781;
        List<EquipTargetTable> targets = TableReaderV2.Parse<EquipTargetTable>();
        var recommendations = TableReaderV2.Parse<EquipRecommendTable>().ToDictionary(row => row.Id);
        List<EquipTable> equipment = TableReaderV2.Parse<EquipTable>()
            .Where(Character.IsOwnableEquipTemplate).ToList();
        EquipTargetTable specific = targets.Single(row => row.Id == 103);
        EquipTargetTable other = targets.OrderBy(row => row.Id).First(row => row.CharacterId != specific.CharacterId
            && recommendations[row.EquipRecommendId].Number.Sum() == 6);
        Player player = CreateDrawCompatibilityPlayer(uid);
        Character character = CreateDrawCompatibilityCharacter(uid);
        character.Characters = [new CharacterData { Id = (uint)specific.CharacterId }, new CharacterData { Id = (uint)other.CharacterId }];
        character.Equips = [];
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(uid, []), "equip-guide-goal");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        MethodInfo dispatch = typeof(Session).GetMethod("InvokeRequestHandler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Session).FullName, "InvokeRequestHandler");
        int packetId = 47_900;

        CheckTask(7844, 0, "No guide selected");
        CheckTask(50080, 0, "No guide completed");
        CheckTask(8017, 0, "Specific guide not completed");
        Select(other.Id);
        CheckTask(7844, 1, "Selecting a guide records historical selection");
        Reload();
        Select(0);
        CheckTask(7844, 1, "BSON reload and cancel retain historical selection");
        Select(other.Id);
        Equip(other);
        CheckTask(50080, 1, "Qualifying current guide satisfies general task before finish");
        CheckTask(8017, 0, "Other character never satisfies specific guide task");

        foreach (EquipData equip in harness.Session.character.Equips)
        {
            equip.Level = 1;
            equip.Breakthrough = 0;
        }
        CheckTask(50080, 0, "Underleveled current guide is not complete");
        Reject(other.CharacterId, 20021103, "Underleveled equipment");
        Equip(other);
        Reject(specific.CharacterId, 20021098, "Request character differs from active target");
        EquipData memory = harness.Session.character.Equips.Last();
        harness.Session.character.Equips.Remove(memory);
        Reject(other.CharacterId, 20021102, "Missing required suit piece");
        harness.Session.character.Equips.Add(memory);

        string beforeFailure = Convert.ToHexString(harness.Session.player.ToBson());
        players.ThrowOnReplaceOne = true;
        EquipGuideTargetFinishResponse failed;
        try
        {
            failed = Finish(other.CharacterId);
        }
        finally
        {
            players.ThrowOnReplaceOne = false;
        }
        AssertEqual(2, failed.Code, "Guide finish reports persistence failure");
        AssertEqual(beforeFailure, Convert.ToHexString(harness.Session.player.ToBson()), "Failed guide save rolls back player state");
        AssertEqual(0, failed.EquipGuideData.FinishedTargets.Count, "Failed guide save reports no finished target");

        Complete(other, [other.Id]);
        CheckTask(8017, 0, "Finishing another target does not complete specific guide task");
        Reload();
        Reject(other.CharacterId, 20021096, "Finish retry after BSON reload has no active target");
        CheckFinished(harness.Session.player.EquipGuideData, [other.Id], "Reloaded completion");
        Select(other.Id);
        Complete(other, [other.Id]);
        Select(specific.Id);
        Equip(specific);
        CheckTask(8017, 0, "Qualifying specific target must still be finished");
        Complete(specific, [other.Id, specific.Id]);
        CheckTask(8017, 1, "Finishing target 103 completes specific guide task");
        Reload();
        CheckTask(50080, 1, "Finished targets retain general progress after reload");
        CheckTask(8017, 1, "Specific completion survives reload");
        Reject(specific.CharacterId, 20021096, "Specific completion retry remains unique");
        Select(specific.Id);
        Complete(specific, [other.Id, specific.Id]);

        void Equip(EquipTargetTable target)
        {
            EquipRecommendTable recommendation = recommendations[target.EquipRecommendId];
            List<EquipTable> templates = [equipment.Single(row => row.Id == recommendation.EquipRecomend)];
            int site = 1;
            for (int suit = 0; suit < recommendation.SuitId.Count; suit++)
                for (int count = 0; count < recommendation.Number[suit]; count++, site++)
                    templates.Add(equipment.OrderBy(row => row.Id).First(row => row.Site == site && row.SuitId == recommendation.SuitId[suit]));
            AssertEqual(7, templates.Count, "Authoritative recommendation supplies weapon and six memories");
            harness.Session.character.Equips = templates.Select((row, index) => new EquipData
            {
                Id = (uint)(index + 1), TemplateId = (uint)row.Id, CharacterId = target.CharacterId,
                Level = 45, Breakthrough = 4
            }).ToList();
        }

        void Select(int targetId)
        {
            EquipGuideSetTargetResponse response = Dispatch<EquipGuideSetTargetRequest, EquipGuideSetTargetResponse>(
                new() { TargetId = targetId, PutOnPosList = targetId == 0 ? [] : [0, 1, 2, 3, 4, 5, 6] });
            AssertEqual(0, response.Code, "Guide selection succeeds");
            AssertEqual(targetId, response.EquipGuideData.TargetId, "Guide selection returns active target");
        }

        EquipGuideTargetFinishResponse Finish(int characterId) =>
            Dispatch<EquipGuideTargetFinishRequest, EquipGuideTargetFinishResponse>(new() { CharacterId = characterId });

        void Reject(int characterId, int code, string context)
        {
            string beforePlayer = Convert.ToHexString(harness.Session.player.ToBson());
            string beforeCharacter = Convert.ToHexString(harness.Session.character.ToBson());
            int saves = players.ReplaceOneCalls;
            AssertEqual(code, Finish(characterId).Code, context);
            AssertEqual(beforePlayer, Convert.ToHexString(harness.Session.player.ToBson()), $"{context} preserves player");
            AssertEqual(beforeCharacter, Convert.ToHexString(harness.Session.character.ToBson()), $"{context} preserves equipment");
            AssertEqual(saves, players.ReplaceOneCalls, $"{context} does not save player");
        }

        void Complete(EquipTargetTable target, int[] finished)
        {
            EquipGuideTargetFinishResponse response = Finish(target.CharacterId);
            AssertEqual(0, response.Code, "Qualifying guide finish succeeds");
            CheckFinished(response.EquipGuideData, finished, "Finish response");
            CheckFinished(harness.Session.player.EquipGuideData, finished, "Finish session state");
            Player persisted = BsonSerializer.Deserialize<Player>((players.LastReplacement
                ?? throw new InvalidDataException("Guide completion did not persist Player.")).ToBson());
            CheckFinished(persisted.EquipGuideData, finished, "Persisted finish state");
            CheckTask(50080, 1, "Finished guide satisfies general guide task");
        }

        void CheckFinished(EquipGuideData guide, int[] finished, string context)
        {
            AssertEqual(0, guide.TargetId, $"{context} clears active target");
            AssertEqual(0, guide.CharacterId, $"{context} clears active character");
            AssertEmptyList(guide.PutOnPosList, $"{context} clears positions");
            AssertIntegerList(finished.Select(value => (long)value).ToArray(),
                guide.FinishedTargets.Select(value => (long)value).ToArray(), $"{context} retains unique completions");
        }

        void Reload() => harness.Session.player = BsonSerializer.Deserialize<Player>((players.LastReplacement
            ?? throw new InvalidDataException("Guide request did not persist Player.")).ToBson());

        void CheckTask(int taskId, int expected, string context) => AssertEqual(expected,
            RequiredStoryLoginTask(BuildTaskData(harness.Session), taskId).Schedule.Single().Value, context);

        TResponse Dispatch<TRequest, TResponse>(TRequest payload)
        {
            int id = packetId++;
            dispatch.Invoke(harness.Session, [GetRegisteredRequestHandler(typeof(TRequest).Name), new Packet.Request
            {
                Id = id, Name = typeof(TRequest).Name, Content = MessagePackSerializer.Serialize(payload)
            }]);
            while (true)
            {
                Packet packet = harness.ReadPacket("Guide goal response or push");
                if (packet.Type != Packet.ContentType.Response)
                {
                    AssertEqual(Packet.ContentType.Push, packet.Type, "Guide goal notification type");
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, "Guide goal response request id");
                AssertEqual(typeof(TResponse).Name, response.Name, "Guide goal response name");
                while (harness.TryReadAvailablePacket("Guide goal trailing push", out Packet? trailing))
                    AssertEqual(Packet.ContentType.Push, trailing!.Type, "Guide goal trailing notification type");
                return MessagePackSerializer.Deserialize<TResponse>(response.Content);
            }
        }
    }
}
