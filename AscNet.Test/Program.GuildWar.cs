using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGuildWarPopupActionCompatibility()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerCollection,
            out _,
            out _);
        const long playerId = 88_050;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.GuildWar.PlayedActionIds = [1];
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            CreateDrawCompatibilityInventory(playerId, []),
            "guild-war-popup-action-compat-test");

        RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler("GuildWarPopupActionRequest");

        // ---- named-key MessagePack contract ----
        GuildWarPopupActionRequest wire = MessagePackSerializer.Deserialize<GuildWarPopupActionRequest>(
            MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [1, 3, 5] }));
        AssertIntegerList([1, 3, 5], wire.ActionPlayed.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest ActionPlayed MessagePack round-trip");
        AssertEqual(true,
            MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [7] }))
                .Contains("ActionPlayed", StringComparison.Ordinal),
            "GuildWarPopupActionRequest named MessagePack key ActionPlayed");

        // ---- first distinct list ----
        const int firstPacketId = 14_001;
        handler.Invoke(harness.Session, new Packet.Request
        {
            Id = firstPacketId,
            Name = "GuildWarPopupActionRequest",
            Content = MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [1, 3, 5] })
        });
        GuildWarPopupActionResponse firstResponse = ReadResponsePayload<GuildWarPopupActionResponse>(
            harness, firstPacketId, nameof(GuildWarPopupActionResponse), "GuildWarPopupActionRequest first response");
        AssertEqual(0, firstResponse.Code, "GuildWarPopupActionResponse first Code");
        AssertIntegerList([1, 3, 5], player.GuildState.War.PlayedActionIds.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest first merged ids");
        Player persistedFirst = playerCollection.LastReplacement
            ?? throw new InvalidDataException("GuildWarPopupActionRequest first did not persist player.");
        AssertIntegerList([1, 3, 5], persistedFirst.GuildState.War.PlayedActionIds.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest first persisted ids");

        // ---- second distinct list with overlap, duplicates, and negatives ----
        const int secondPacketId = 14_002;
        handler.Invoke(harness.Session, new Packet.Request
        {
            Id = secondPacketId,
            Name = "GuildWarPopupActionRequest",
            Content = MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [5, 3, 7, 2, 4, 3, -1, 0] })
        });
        GuildWarPopupActionResponse secondResponse = ReadResponsePayload<GuildWarPopupActionResponse>(
            harness, secondPacketId, nameof(GuildWarPopupActionResponse), "GuildWarPopupActionRequest second response");
        AssertEqual(0, secondResponse.Code, "GuildWarPopupActionResponse second Code");
        // existing [1,3,5] preserved; only new positive ids 7,2,4 appended in first-seen order; negatives ignored
        AssertIntegerList([1, 3, 5, 7, 2, 4], player.GuildState.War.PlayedActionIds.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest merged ordered ids");

        // ---- repeat preserves the durable receipt set ----
        const int repeatPacketId = 14_003;
        handler.Invoke(harness.Session, new Packet.Request
        {
            Id = repeatPacketId,
            Name = "GuildWarPopupActionRequest",
            Content = MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [2, 4, 1] })
        });
        GuildWarPopupActionResponse repeatResponse = ReadResponsePayload<GuildWarPopupActionResponse>(
            harness, repeatPacketId, nameof(GuildWarPopupActionResponse), "GuildWarPopupActionRequest repeat response");
        AssertEqual(0, repeatResponse.Code, "GuildWarPopupActionResponse repeat Code");
        AssertIntegerList([1, 3, 5, 7, 2, 4], player.GuildState.War.PlayedActionIds.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest repeat unchanged ids");

        // ---- relog BSON round-trip ----
        Player reloaded = BsonSerializer.Deserialize<Player>((playerCollection.LastReplacement
            ?? throw new InvalidDataException("GuildWarPopupActionRequest expected persisted player.")).ToBson());
        AssertIntegerList([1, 3, 5, 7, 2, 4], reloaded.GuildState.War.PlayedActionIds.Select(id => (long)id).ToArray(),
            "GuildWarPopupActionRequest relog BSON ids");

        // ---- persistence failure rolls back in-memory state and returns an error response ----
        Player failedPlayer = CreateDrawCompatibilityPlayer(playerId + 1);
        using MongoCollectionOverride failedMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> failedSaves, out _, out _);
        using LoopbackSessionHarness failedHarness = new(
            CreateDrawCompatibilityCharacter(playerId + 1),
            failedPlayer,
            CreateDrawCompatibilityInventory(playerId + 1, []),
            "guild-war-popup-action-save-failure");
        failedSaves.ThrowOnReplaceOne = true;
        const int failedPacketId = 14_004;
        handler.Invoke(failedHarness.Session, new Packet.Request
        {
            Id = failedPacketId,
            Name = "GuildWarPopupActionRequest",
            Content = MessagePackSerializer.Serialize(new GuildWarPopupActionRequest { ActionPlayed = [9, 11] })
        });
        GuildWarPopupActionResponse failedResponse = ReadResponsePayload<GuildWarPopupActionResponse>(
            failedHarness, failedPacketId, nameof(GuildWarPopupActionResponse), "GuildWarPopupActionRequest failure response");
        AssertEqual(false, failedResponse.Code == 0, "GuildWarPopupActionResponse failure Code is non-zero");
        AssertEqual(0, failedPlayer.GuildState.War.PlayedActionIds.Count,
            "GuildWarPopupActionRequest failure rolls back in-memory ids");
    }
}
