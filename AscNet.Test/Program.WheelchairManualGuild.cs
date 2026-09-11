using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using ConditionTable = AscNet.Table.V2.share.condition.ConditionTable;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateWheelchairManualGuildCompatibility()
    {
        if (!AscNet.Common.Common.config.Database.Name.StartsWith("ascnet_manual_full", StringComparison.Ordinal)
            || !AscNet.Common.Common.db.DatabaseNamespace.DatabaseName.StartsWith("ascnet_manual_full", StringComparison.Ordinal))
            throw new InvalidOperationException("Guild compatibility requires the isolated ascnet_manual_full database.");

        PacketFactory.LoadPacketHandlers();
        var create = TableReaderV2.Parse<GuildCreateTable>().First();
        var level = TableReaderV2.Parse<GuildLevelTable>().OrderBy(row => row.Level).First();
        var portrait = TableReaderV2.Parse<GuildHeadPortraitTable>().First(row => !(row.ConditionId > 0) && !(row.Cost > 0));
        var task = TableReaderV2.Parse<CurrentTaskTable>().Single(row => row.Id == 8020);
        var costs = create.ItemId.Select((id, index) => (Id: id, Count: (long)create.ItemNum[index]))
            .GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        long firstUid = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        string guildName = "M" + Guid.NewGuid().ToString("N")[..7];
        List<long> ownedPlayers = [];
        List<LoopbackSessionHarness> harnesses = [];
        string unauthenticatedSessionId = "manual-guild-unauthenticated-" + Guid.NewGuid().ToString("N");
        bool unauthenticatedSessionRegistered = false;
        int packetId = 58_000;
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildModule");
        MethodInfo buildLogin = RequiredMethod(module, "BuildLoginData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Session)]);
        MethodInfo prepareLogin = RequiredMethod(module, "PrepareLogin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Session)]);

        LoopbackSessionHarness NewPlayer(bool funded = true, bool stagePassed = true, int playerLevel = 40)
        {
            long uid = firstUid + ownedPlayers.Count;
            if (Player.collection.Find(row => row.PlayerData.Id == uid).Any()
                || Inventory.collection.Find(row => row.Uid == uid).Any()
                || Character.collection.Find(row => row.Uid == uid).Any()
                || Stage.collection.Find(row => row.Uid == uid).Any()
                || Server.Instance.SessionFromUID(uid) is not null)
                throw new InvalidDataException("Manual guild fixture UID is already in use.");
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = playerLevel;
            player.MissionProgress = new();
            Character character = CreateDrawCompatibilityCharacter(uid);
            Inventory inventory = CreateDrawCompatibilityInventory(uid,
                costs.Select(cost => new Item { Id = cost.Key, Count = funded ? cost.Value * 3 : 0 }));
            Player.collection.InsertOne(player);
            ownedPlayers.Add(uid);
            Inventory.collection.InsertOne(inventory);
            Character.collection.InsertOne(character);
            LoopbackSessionHarness harness = new(character, player, inventory, $"manual-guild-{uid}");
            harnesses.Add(harness);
            harness.Session.stage = new Stage { Uid = uid, Stages = new() };
            if (stagePassed)
            {
                int stageId = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == 70124).Params[0];
                harness.Session.stage.Stages[stageId] = new StageDatum { StageId = stageId, Passed = true };
            }
            Stage.collection.InsertOne(harness.Session.stage);
            return harness;
        }
        T Request<T>(LoopbackSessionHarness harness, string name, object request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            return (T)ReadResponsePayload(harness, id, typeof(T).Name, name, typeof(T), maxPacketsToRead: 64);
        }
        GuildCreateResponse Create(LoopbackSessionHarness harness, string? name = null, int? icon = null) =>
            Request<GuildCreateResponse>(harness, nameof(GuildCreateRequest), new GuildCreateRequest
                { GuildName = name ?? guildName, GuildDeclaration = "Manual compatibility", IconId = icon ?? portrait.Id });
        GuildApplyResponse Apply(LoopbackSessionHarness harness, uint guildId) =>
            Request<GuildApplyResponse>(harness, nameof(GuildApplyRequest), new GuildApplyRequest { GuildId = checked((int)guildId) });
        GuildAckApplyResponse Approve(LoopbackSessionHarness officer, LoopbackSessionHarness applicant) =>
            Request<GuildAckApplyResponse>(officer, nameof(GuildAckApplyRequest), new GuildAckApplyRequest
                { PlayId = applicant.Session.player.PlayerData.Id, IsAgree = true });
        NotifyGuildData Login(LoopbackSessionHarness harness) => (NotifyGuildData)buildLogin.Invoke(null, [harness.Session])!;
        int State(LoopbackSessionHarness harness) => BuildTaskData(harness.Session).Single(row => row.Id == task.Id).State;
        void Reload(LoopbackSessionHarness harness)
        {
            long uid = harness.Session.player.PlayerData.Id;
            harness.Session.player = BsonSerializer.Deserialize<Player>(Player.collection.Find(row => row.PlayerData.Id == uid).Single().ToBson());
            harness.Session.inventory = BsonSerializer.Deserialize<Inventory>(Inventory.collection.Find(row => row.Uid == uid).Single().ToBson());
            harness.Session.character = BsonSerializer.Deserialize<Character>(Character.collection.Find(row => row.Uid == uid).Single().ToBson());
        }
        void Claim(LoopbackSessionHarness harness)
        {
            AssertEqual(3, State(harness), "Real guild membership achieves manual8020");
            var response = Request<FinishTaskResponse>(harness, nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id });
            AssertEqual(0, response.Code, "Guild manual reward claim succeeds naturally");
            var reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == task.RewardId);
            string Signature(IEnumerable<RewardGoods> goods) => string.Join(";", goods.OrderBy(row => row.TemplateId).Select(row => $"{row.TemplateId}:{row.Count}"));
            AssertEqual(Signature(TableReaderV2.Parse<RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id))
                .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count })),
                Signature(response.RewardGoodsList), "Guild manual reward matches authoritative task reward");
            Reload(harness);
            AssertEqual(4, State(harness), "Guild manual claim survives database and BSON reload");
            string before = harness.Session.inventory.ToJson();
            AssertEqual(true, Request<FinishTaskResponse>(harness, nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }).Code != 0,
                "Guild manual duplicate claim rejects");
            Reload(harness);
            AssertEqual(before, harness.Session.inventory.ToJson(), "Guild manual duplicate claim cannot grant rewards twice");
        }
        void RejectCreate(LoopbackSessionHarness harness, string reason, string? name = null, int? icon = null)
        {
            string before = harness.Session.inventory.ToJson();
            AssertEqual(true, Create(harness, name ?? ("M" + Guid.NewGuid().ToString("N")[..7]), icon).Code != 0, reason);
            Reload(harness);
            AssertEqual(before, harness.Session.inventory.ToJson(), reason + " preserves inventory");
            AssertEqual(0L, Guild.collection.CountDocuments(row => row.LeaderId == harness.Session.player.PlayerData.Id), reason + " creates no reservation");
            AssertEqual(false, State(harness) == 3, reason + " cannot complete task");
        }

        try
        {
            var founder = NewPlayer();
            var applicant = NewPlayer();
            var outsider = NewPlayer();
            AssertEqual(0u, Login(founder).GuildId, "Fresh player has no fabricated guild");
            prepareLogin.Invoke(null, [outsider.Session]);
            AssertNoAvailablePacket(outsider, "Fresh guild PrepareLogin stays silent");
            AssertEqual(false, State(outsider) == 3, "Login without membership cannot recover join history");
            AssertEqual(true, Request<FinishTaskResponse>(founder, nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }).Code != 0,
                "Fresh founder cannot claim join-guild task");
            AssertEqual(0, Create(founder).Code, "Valid founder creates a real guild");
            Guild guild = Guild.FindByMember(founder.Session.player.PlayerData.Id) ?? throw new InvalidDataException("Founder membership was not persisted.");
            AssertEqual(guildName, guild.Name, "Created guild preserves requested name");
            AssertEqual(level.Level, guild.Level, "Created guild uses initial level");
            AssertEqual(1, guild.MemberIds.Count, "Created guild has exactly its founder");
            Reload(founder);
            foreach (var cost in costs)
                AssertEqual(cost.Value * 2, founder.Session.inventory.Items.Where(item => item.Id == cost.Key).Sum(item => item.Count), "Creation debits each configured item cost");
            string paidInventory = founder.Session.inventory.ToJson();
            _ = Create(founder);
            Reload(founder);
            AssertEqual(paidInventory, founder.Session.inventory.ToJson(), "Duplicate create cannot debit again");
            AssertEqual(1L, Guild.collection.CountDocuments(row => row.LeaderId == founder.Session.player.PlayerData.Id), "Duplicate create cannot create another guild");
            AssertEqual(guild.Id, Login(founder).GuildId, "Founder login uses persisted guild ID");
            Claim(founder);

            AssertEqual(true, Apply(outsider, 99_999_999).Code != 0, "Unknown guild rejects application");
            var pending = Apply(applicant, guild.Id);
            AssertEqual(0, pending.Code, "Valid application is accepted");
            AssertEqual(false, pending.IsPass, "NeedApply guild leaves application pending");
            Reload(applicant);
            AssertEqual(0u, Login(applicant).GuildId, "Pending applicant is not a member");
            prepareLogin.Invoke(null, [applicant.Session]);
            AssertNoAvailablePacket(applicant, "Pending guild PrepareLogin stays silent");
            AssertEqual(false, State(applicant) == 3, "Pending application cannot complete join task");
            var unauthenticated = new LoopbackSessionHarness(
                CreateDrawCompatibilityCharacter(firstUid + 30_000), sessionId: unauthenticatedSessionId);
            harnesses.Add(unauthenticated);
            unauthenticated.Session.player = null!;
            unauthenticatedSessionRegistered = Server.Instance.Sessions.TryAdd(unauthenticatedSessionId, unauthenticated.Session);
            if (!unauthenticatedSessionRegistered)
                throw new InvalidDataException("Could not register the isolated unauthenticated guild test session.");
            var members = Request<GuildMemberDetailResponse>(founder, nameof(GuildMemberDetailRequest),
                new GuildMemberDetailRequest { GuildId = checked((int)guild.Id) });
            AssertEqual(0, members.Code, "Unauthenticated connection does not prevent listing guild members");
            AssertEqual(guild.Id, members.GuildId, "Member details identify the requested guild");
            AssertEqual(founder.Session.player.PlayerData.Id, (long)members.MembersData.Single().Id,
                "Member details identify the persisted founder despite an unauthenticated connection");
            var applications = Request<GuildListApplyResponse>(founder, nameof(GuildListApplyRequest), new GuildListApplyRequest());
            AssertEqual(0, applications.Code, "Founder can list applications");
            AssertEqual(true, JArray.FromObject(applications.Data).Descendants().OfType<JValue>()
                .Any(value => value.Type == JTokenType.Integer && value.Value<long>() == applicant.Session.player.PlayerData.Id), "Application list identifies pending player");
            AssertEqual(true, Approve(outsider, applicant).Code != 0, "Non-officer cannot approve application");
            AssertEqual(false, Guild.FindById(guild.Id)!.MemberIds.Contains(applicant.Session.player.PlayerData.Id), "Unauthorized approval cannot add member");
            AssertEqual(0, Approve(founder, applicant).Code, "Founder admits pending applicant");
            Reload(applicant);
            prepareLogin.Invoke(null, [applicant.Session]);
            AssertNoAvailablePacket(applicant, "Admission recovery PrepareLogin stays silent");
            AssertEqual(guild.Id, Login(applicant).GuildId, "Approved applicant login resolves real membership");
            AssertEqual(false, Guild.FindById(guild.Id)!.Applications.Any(row => row.PlayerId == applicant.Session.player.PlayerData.Id), "Admission consumes pending application");
            Claim(applicant);
            Reload(outsider);
            AssertEqual(0u, Login(outsider).GuildId, "Guild membership remains per user");
            AssertEqual(false, State(outsider) == 3, "Another user's admission cannot advance outsider task");

            RejectCreate(NewPlayer(funded: false), "Unfunded create rejects");
            RejectCreate(NewPlayer(stagePassed: false), "Creation stage gate rejects");
            RejectCreate(NewPlayer(playerLevel: 1), "Guild functional gate rejects");
            RejectCreate(NewPlayer(), "Empty guild name rejects", "");
            RejectCreate(NewPlayer(), "Unknown guild icon rejects", icon: int.MaxValue);
            RejectCreate(NewPlayer(), "Duplicate guild name rejects", guildName);

            var waiting = NewPlayer();
            AssertEqual(0, Apply(waiting, guild.Id).Code, "Capacity candidate can apply before guild fills");
            AssertEqual(true, Approve(applicant, waiting).Code != 0, "Ordinary guild member cannot act as an officer");
            AssertEqual(false, Guild.FindById(guild.Id)!.MemberIds.Contains(waiting.Session.player.PlayerData.Id), "Unauthorized member approval preserves pending status");
            var fullMembers = Guild.FindById(guild.Id)!.MemberIds.ToList();
            for (int index = fullMembers.Count; index < level.Capacity; index++)
                fullMembers.Add(firstUid + 10_000 + index);
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.MemberIds, fullMembers));
            AssertEqual(true, Approve(founder, waiting).Code != 0, "Full guild rejects admission");
            AssertEqual(false, Guild.FindById(guild.Id)!.MemberIds.Contains(waiting.Session.player.PlayerData.Id), "Capacity rejection leaves applicant outside guild");
            AssertEqual(true, Apply(outsider, guild.Id).Code != 0, "Full guild rejects new application");
            Reload(waiting);
            prepareLogin.Invoke(null, [waiting.Session]);
            AssertEqual(false, State(waiting) == 3, "Capacity rejection cannot complete manual task");

            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update
                .Set(row => row.MemberIds, new List<long> { founder.Session.player.PlayerData.Id, applicant.Session.player.PlayerData.Id })
                .Set(row => row.Option, 1));
            var directJoin = NewPlayer();
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Option, 3));
            AssertEqual(true, Apply(directJoin, guild.Id).Code != 0, "Forbidden admission rejects eligible player");
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Option, 1).Set(row => row.MinLevel, 41));
            AssertEqual(true, Apply(directJoin, guild.Id).Code != 0, "Guild minimum level rejects otherwise unlocked player");
            AssertEqual(0u, Login(directJoin).GuildId, "Admission policy rejections cannot create membership");
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.MinLevel, 1).Set(row => row.Option, 2));
            var guildConfig = TableReaderV2.Parse<AscNet.Table.V2.share.config.ConfigTable>().ToDictionary(row => row.Key, row => row.Value);
            int applicationLimit = int.Parse(guildConfig["GuildApplyGuildMaxCount"], System.Globalization.CultureInfo.InvariantCulture);
            long timeout = long.Parse(guildConfig["GuildApplyTimeoutSec"], System.Globalization.CultureInfo.InvariantCulture);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fullApplications = Enumerable.Range(0, applicationLimit).Select(index => new GuildApplication
                { PlayerId = firstUid + 20_000 + index, CreatedAt = now }).ToList();
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Applications, fullApplications));
            AssertEqual(true, Apply(directJoin, guild.Id).Code != 0, "Full application queue rejects another pending applicant");
            foreach (var application in fullApplications)
                application.CreatedAt = now - timeout - 1;
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Applications, fullApplications));
            var afterExpiry = Apply(directJoin, guild.Id);
            AssertEqual(0, afterExpiry.Code, "Expired applications release queue capacity");
            AssertEqual(false, afterExpiry.IsPass, "Queue expiry does not bypass officer approval");
            AssertEqual(1, Guild.FindById(guild.Id)!.Applications.Count, "Expired pending records are replaced by the new live application");
            AssertEqual(false, State(directJoin) == 3, "Application queue replacement cannot complete manual task");
            Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Option, 1));
            var admitted = Apply(directJoin, guild.Id);
            AssertEqual(0, admitted.Code, "Open guild admits a valid applicant directly");
            AssertEqual(true, admitted.IsPass, "Direct admission reports membership rather than pending status");
            Reload(directJoin);
            prepareLogin.Invoke(null, [directJoin.Session]);
            AssertEqual(guild.Id, Login(directJoin).GuildId, "Direct admission persists membership");
            Claim(directJoin);

            // Recover historical membership without pre-seeding the join condition counter.
            applicant.Session.player.MissionProgress = new();
            applicant.Session.player.GuildProgressRecordedId = 0;
            applicant.Session.player.SaveChecked();
            Reload(applicant);
            prepareLogin.Invoke(null, [applicant.Session]);
            AssertNoAvailablePacket(applicant, "Historical guild PrepareLogin stays silent");
            AssertEqual(3, State(applicant), "PrepareLogin recovers historical persisted membership");
            Reload(applicant);
            AssertEqual(3, State(applicant), "Recovered guild task progress persists");
            int recoveredCount = applicant.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition);
            prepareLogin.Invoke(null, [applicant.Session]);
            AssertNoAvailablePacket(applicant, "Repeated guild PrepareLogin stays silent");
            Reload(applicant);
            AssertEqual(recoveredCount, applicant.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition),
                "Repeated login cannot replay the recorded membership event");

            var interrupted = NewPlayer();
            AssertEqual(0, Create(interrupted, "M" + Guid.NewGuid().ToString("N")[..7]).Code, "Interrupted creation fixture pays real creation costs");
            Guild interruptedGuild = Guild.FindByMember(interrupted.Session.player.PlayerData.Id)!;
            Reload(interrupted);
            string interruptedInventory = interrupted.Session.inventory.ToJson();
            Guild.collection.UpdateOne(row => row.Id == interruptedGuild.Id, Builders<Guild>.Update
                .Set(row => row.Active, false).Set(row => row.CreationReserved, true));
            interrupted.Session.player.GuildProgressRecordedId = 0;
            interrupted.Session.player.MissionProgress = new();
            interrupted.Session.player.SaveChecked();
            Reload(interrupted);
            foreach (var changed in new[]
            {
                new GuildCreateRequest { GuildName = "M" + Guid.NewGuid().ToString("N")[..7], IconId = interruptedGuild.IconId, GuildDeclaration = interruptedGuild.Declaration },
                new GuildCreateRequest { GuildName = interruptedGuild.Name, IconId = interruptedGuild.IconId + 1, GuildDeclaration = interruptedGuild.Declaration },
                new GuildCreateRequest { GuildName = interruptedGuild.Name, IconId = interruptedGuild.IconId, GuildDeclaration = interruptedGuild.Declaration + " changed" }
            })
            {
                AssertEqual(20063009, Request<GuildCreateResponse>(interrupted, nameof(GuildCreateRequest), changed).Code,
                    "Paid pending creation rejects changed reservation metadata");
                Reload(interrupted);
                AssertEqual(interruptedInventory, interrupted.Session.inventory.ToJson(), "Changed reservation metadata cannot debit costs");
                Guild frozen = Guild.collection.Find(row => row.Id == interruptedGuild.Id).Single();
                AssertEqual(false, frozen.Active, "Changed reservation metadata cannot activate the pending guild");
                AssertEqual(interruptedGuild.Name, frozen.Name, "Pending reservation keeps its original name");
                AssertEqual(interruptedGuild.IconId, frozen.IconId, "Pending reservation keeps its original icon");
                AssertEqual(interruptedGuild.Declaration, frozen.Declaration, "Pending reservation keeps its original declaration");
            }
            prepareLogin.Invoke(null, [interrupted.Session]);
            AssertNoAvailablePacket(interrupted, "Paid inactive reservation PrepareLogin stays silent");
            Reload(interrupted);
            AssertEqual(interruptedInventory, interrupted.Session.inventory.ToJson(), "Recovery after paid creation cannot debit costs again");
            AssertEqual(interruptedGuild.Id, Login(interrupted).GuildId, "Login activates the existing paid reservation");
            AssertEqual(3, State(interrupted), "Recovered paid reservation completes the natural guild task");
            AssertEqual(1L, Guild.collection.CountDocuments(row => row.LeaderId == interrupted.Session.player.PlayerData.Id),
                "Interrupted creation recovery cannot allocate another guild");
            Guild bsonGuild = BsonSerializer.Deserialize<Guild>(Guild.FindById(guild.Id)!.ToBson());
            AssertEqual(guild.Id, bsonGuild.Id, "Guild ID survives BSON round trip");
            AssertEqual(true, bsonGuild.MemberIds.Contains(applicant.Session.player.PlayerData.Id), "Membership survives BSON round trip");

            var failedRecord = NewPlayer();
            _ = State(failedRecord); // Prepare reset state before injecting the recording save failure.
            MethodInfo record = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
                "RecordTableDrivenProgress", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(IEnumerable<(int ConditionType, int? Parameter, int Amount)>), typeof(bool), RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TheatreModule+Mutation")]);
            using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> saves, out _, out _))
            {
                for (int before = 0; before < 2; before++)
                {
                    saves.ThrowOnReplaceOne = true;
                    bool failed = false;
                    try
                    {
                        record.Invoke(null, [failedRecord.Session, new (int, int?, int)[] { (35002, null, 1) }, true, null]);
                    }
                    catch (TargetInvocationException)
                    {
                        failed = true;
                    }
                    finally
                    {
                        saves.ThrowOnReplaceOne = false;
                    }
                    AssertEqual(true, failed, "Guild task recording propagates failed player save");
                    AssertEqual(before, failedRecord.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition),
                        "Failed guild task recording restores the previous counter");
                    AssertEqual(before > 0, failedRecord.Session.player.MissionProgress.ConditionCounters.ContainsKey(task.Condition),
                        "Failed guild task recording preserves counter absence");
                    AssertNoAvailablePacket(failedRecord, "Failed guild task recording cannot notify uncommitted progress");
                    record.Invoke(null, [failedRecord.Session, new (int, int?, int)[] { (35002, null, 1) }, true, null]);
                    AssertEqual(before + 1, failedRecord.Session.player.MissionProgress.ConditionCounters[task.Condition],
                        "Successful retry records the membership increment exactly once");
                    NotifyTask notification = ReadPushPayload<NotifyTask>(failedRecord, nameof(NotifyTask), "Durable guild task retry notification");
                    AssertEqual(3, notification.Tasks.Tasks.Single(row => row.Id == task.Id).State,
                        "Normal guild task recording still notifies achieved manual task");
                }
            }
            Console.WriteLine("Wheelchair manual guild compatibility passed.");
        }
        finally
        {
            if (unauthenticatedSessionRegistered)
                Server.Instance.Sessions.TryRemove(unauthenticatedSessionId, out _);
            foreach (var harness in harnesses)
                harness.Dispose();
            Guild.collection.DeleteMany(Builders<Guild>.Filter.In(row => row.LeaderId, ownedPlayers));
            Player.collection.DeleteMany(Builders<Player>.Filter.In(row => row.PlayerData.Id, ownedPlayers));
            Inventory.collection.DeleteMany(Builders<Inventory>.Filter.In(row => row.Uid, ownedPlayers));
            Character.collection.DeleteMany(Builders<Character>.Filter.In(row => row.Uid, ownedPlayers));
            Stage.collection.DeleteMany(Builders<Stage>.Filter.In(row => row.Uid, ownedPlayers));
            AscNet.Common.Common.db.GetCollection<BsonDocument>("guild_counters").UpdateMany(
                Builders<BsonDocument>.Filter.Regex("_id", new BsonRegularExpression("^creation:")),
                Builders<BsonDocument>.Update.PullAll("founder_ids", ownedPlayers));
        }
    }
}
