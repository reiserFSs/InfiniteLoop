using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.guild;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static int guildPacketId = 700_000;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LoopbackSessionHarness, GuildTestScope> guildTestScopes = new();

    private static void GuildAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static JObject GuildRpc(LoopbackSessionHarness harness, string requestName, object request, int? requestId = null)
    {
        int packetId = requestId ?? Interlocked.Increment(ref guildPacketId);
        RequestPacketHandlerDelegate handler = PacketFactory.GetRequestPacketHandler(requestName)
            ?? throw new InvalidDataException($"PacketFactory did not register {requestName}.");
        Packet.Request packetRequest = new()
        {
            Id = packetId,
            Name = requestName,
            Content = MessagePackSerialize(request.GetType(), request)
        };
        try
        {
            handler.Invoke(harness.Session, packetRequest);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"{requestName}: registered handler invocation failed.", exception);
        }
        if (requestName == nameof(GuildCreateRequest) && guildTestScopes.TryGetValue(harness, out var scope) && scope is not null)
        {
            Guild? created = Guild.FindByMember(harness.Session.player.PlayerData.Id);
            if (created is not null) scope.TrackGuild(created.Id);
        }
        for (int i = 0; i < 256; i++)
        {
            Packet packet = harness.ReadPacket(requestName);
            if (packet.Type == Packet.ContentType.Push) continue;
            GuildAssert(packet.Type == Packet.ContentType.Response, requestName + " emitted unexpected packet type");
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            GuildAssert(response.Id == packetId, requestName + " response correlation mismatch");
            GuildAssert(response.Name == requestName[..^"Request".Length] + "Response", requestName + " response name mismatch");
            return JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
        }
        throw new InvalidDataException(requestName + " did not emit its correlated response");
    }

    private sealed class GuildTestScope : IDisposable
    {
        private readonly List<long> players = [];
        private readonly List<LoopbackSessionHarness> harnesses = [];
        private readonly HashSet<uint> guilds = [];

        public GuildTestScope()
        {
            if (AscNet.Common.Common.config.Database.Name != "ascnet_guild_verification"
                || AscNet.Common.Common.db.DatabaseNamespace.DatabaseName != "ascnet_guild_verification")
                throw new InvalidOperationException("Guild full verification requires the isolated ascnet_guild_verification database.");
            PacketFactory.LoadPacketHandlers();
        }

        public LoopbackSessionHarness CreatePlayer()
        {
            long uid = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
            while (Player.collection.Find(row => row.PlayerData.Id == uid).Any())
                uid = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 100;
            player.MissionProgress = new();
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            Inventory inventory = CreateDrawCompatibilityInventory(uid,
                new[] { 1, 2, 3, 4, 5, 6, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }
                    .Concat(TableReaderV2.Parse<GuildCreateTable>().SelectMany(row => row.ItemId)).Distinct()
                    .Select(id => new Item { Id = id, Count = 1_000_000 }));
            Player.collection.InsertOne(player);
            players.Add(uid);
            Character.collection.InsertOne(character);
            Inventory.collection.InsertOne(inventory);
            Stage stage = new() { Uid = uid, Stages = new() };
            foreach (var condition in TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().Where(row => row.Id == 70124))
                stage.Stages[condition.Params[0]] = new StageDatum { StageId = condition.Params[0], Passed = true };
            Stage.collection.InsertOne(stage);
            return OpenPlayer(uid);
        }

        public LoopbackSessionHarness OpenPlayer(long uid)
        {
            GuildAssert(players.Contains(uid), "Cannot open a player not owned by this Guild verification scope");
            var harness = new LoopbackSessionHarness(
                Character.collection.Find(row => row.Uid == uid).Single(),
                Player.collection.Find(row => row.PlayerData.Id == uid).Single(),
                Inventory.collection.Find(row => row.Uid == uid).Single(),
                "guild-verification-" + Guid.NewGuid().ToString("N"), startClientLoop: false);
            harness.Session.stage = Stage.collection.Find(row => row.Uid == uid).Single();
            GuildAssert(Server.Instance.Sessions.TryAdd(harness.Session.id, harness.Session), "Could not register Guild verification session");
            (typeof(Session).GetField("GuildIdentityReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(Session).FullName, "GuildIdentityReady"))
                .SetValue(harness.Session, true);
            harnesses.Add(harness);
            guildTestScopes.Add(harness, this);
            return harness;
        }
        public Guild SeedGuild(params LoopbackSessionHarness[] members)
        {
            GuildAssert(members.Length > 0, "Guild fixture requires a founder");
            var level = TableReaderV2.Parse<GuildLevelTable>().OrderBy(row => row.Level).First();
            var portrait = TableReaderV2.Parse<GuildHeadPortraitTable>().First(row => !(row.ConditionId > 0) && !(row.Cost > 0));
            Guild guild = Guild.ReserveCreation(new Guild
            {
                Name = "G" + Guid.NewGuid().ToString("N")[..7],
                LeaderId = members[0].Session.player.PlayerData.Id,
                Level = level.Level,
                IconId = portrait.Id,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MaxMembers = level.Capacity,
                MaxTourists = level.PositionNum[2],
                Option = 2,
                MinLevel = 1
            });
            guild.Active = true;
            guild.CreationReserved = false;
            guild.MemberIds = members.Select(h => h.Session.player.PlayerData.Id).ToList();
            guild.Normalize();
            Guild.collection.ReplaceOne(row => row.Id == guild.Id, guild);
            TrackGuild(guild.Id);
            return guild;
        }
        public void TrackGuild(uint id)
        {
            lock (guilds) guilds.Add(id);
        }

        public void Dispose()
        {
            foreach (var harness in harnesses)
            {
                Server.Instance.Sessions.TryRemove(harness.Session.id, out _);
                guildTestScopes.Remove(harness);
                harness.Dispose();
            }
            var ownedGuilds = Guild.collection.Find(Builders<Guild>.Filter.In(row => row.LeaderId, players)).ToList();
            guilds.UnionWith(ownedGuilds.Select(row => row.Id));
            Guild.collection.DeleteMany(Builders<Guild>.Filter.In(row => row.Id, guilds));
            Player.collection.DeleteMany(Builders<Player>.Filter.In(row => row.PlayerData.Id, players));
            Inventory.collection.DeleteMany(Builders<Inventory>.Filter.In(row => row.Uid, players));
            Character.collection.DeleteMany(Builders<Character>.Filter.In(row => row.Uid, players));
            Stage.collection.DeleteMany(Builders<Stage>.Filter.In(row => row.Uid, players));
            AscNet.Common.Common.db.GetCollection<BsonDocument>("guild_counters").UpdateMany(
                Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression("^creation:")),
                Builders<BsonDocument>.Update.PullAll("founder_ids", players));
        }
    }
}
