using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.Table.V2.share.fuben.stronghold;
using AscNet.GameServer.Handlers;
using AscNet.Common.Util;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // Unhandled request: the client Call must get "<Name>Response" with the same id and a non-zero Code (CodeText 20000001).
    private static void ValidateUnhandledRequestAnswered()
    {
        const string name = "FeedbackFixesUnknownRequest";
        using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(88_201), sessionId: "feedback-unhandled");
        h.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(name, 77, new Dictionary<string, int> { ["X"] = 1 }));
        Packet packet = h.ReadPacket("unhandled request answer");
        AssertEqual(Packet.ContentType.Response, packet.Type, "unhandled request answered with a response");
        Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
        AssertEqual("FeedbackFixesUnknownResponse", response.Name, "unhandled response name");
        AssertEqual(77, response.Id, "unhandled response correlates the request id");
        AssertEqual(20000001, MessagePackSerializer.Deserialize<Dictionary<string, int>>(response.Content)["Code"], "unhandled response Code");

        // Heartbeat is registered and must still be answered normally right after.
        PacketFactory.LoadPacketHandlers();
        h.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame("HeartbeatRequest", 78, new HeartbeatRequest()));
        AssertHeartbeatResponse(h, 78, "heartbeat after unhandled request");
    }

    // An old save carrying a renamed/removed field (at any depth) must still deserialize.
    private static void ValidateOldSaveExtraElements()
    {
        const long uid = 88_202;
        _ = AscNet.Common.Common.config;
        // Add an unknown element to every class-mapped sub-document (dictionary/array documents keep their real keys).
        static BsonValue Pollute(BsonValue value, Type type)
        {
            if (value is BsonArray array && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return new BsonArray(array.Select(item => Pollute(item, type.GetGenericArguments()[0])));
            if (value is BsonDocument doc && type.Namespace?.StartsWith("AscNet", StringComparison.Ordinal) == true && !type.IsEnum)
            {
                foreach (BsonMemberMap member in BsonClassMap.LookupClassMap(type).AllMemberMaps)
                    if (doc.Contains(member.ElementName))
                        doc[member.ElementName] = Pollute(doc[member.ElementName], member.MemberType);
                doc["removed_in_new_version"] = 1;
            }
            return value;
        }
        T Load<T>(T source) => BsonSerializer.Deserialize<T>((BsonDocument)Pollute(source.ToBsonDocument(), typeof(T)));

        Player player = CreateDrawCompatibilityPlayer(uid);
        AssertEqual(uid, Load(player).PlayerData.Id, "old Player save with extra elements loads");
        _ = Load(new MissionProgressState());
        _ = Load(CreateDrawCompatibilityCharacter(uid));
        _ = Load(CreateDrawCompatibilityInventory(uid, []));
        _ = Load(CreateLoginAccountCompatibilityStage(uid));
    }

    // StrongholdRune has no level/chapter/group gate: the client's "this period" list is the whole table.
    private static void ValidateStrongholdRuneList()
    {
        List<int> expected = TableReaderV2.Parse<StrongholdRuneTable>().Select(row => row.Id).OrderBy(id => id).ToList();
        AssertEqual(true, expected.Count > 1, "authoritative rune table is not empty");
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StrongholdModule");
        var build = RequiredMethod(module, "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]);

        Player fresh = CreateDrawCompatibilityPlayer(88_203);
        fresh.Stronghold.ActivityId = 1;
        fresh.Stronghold.LevelId = 1;
        Player stale = CreateDrawCompatibilityPlayer(88_204);
        stale.Stronghold.ActivityId = 2;
        stale.Stronghold.LevelId = 2;

        foreach ((Player p, string label) in new[] { (fresh, "level 1 fresh"), (stale, "level 2") })
        {
            NotifyStrongholdLoginData login = (NotifyStrongholdLoginData)build.Invoke(null, [p])!;
            AssertEqual(true, login.RuneList.SequenceEqual(expected), $"Stronghold login RuneList is the table list ({label})");
        }
    }
}
