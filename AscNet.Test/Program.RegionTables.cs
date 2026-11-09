using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.bigworld.common.task;
using AscNet.Table.V2.share.mailcollectionbox;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // A TW client resolves ids against its own tables (Docs/development.md "Region tables"): a login must not send it a task or
    // collection-box id its Task.tab / MailCollectionBoxActivity.tab lack (each unknown id logs a stack trace x ~44 on the client,
    // which freezes it past the server's 10 s idle drop). EN must stay byte-for-byte what the shared tables say.
    private static void ValidateRegionTablesCompatibility()
    {
        AssertEqual("tw", ClientRegions.FromGatePath("/api/Login/Login-tw"), "TW gate path region");
        AssertEqual("cn", ClientRegions.FromGatePath("/api/Login/Login-cn"), "CN gate path region");
        AssertEqual("en", ClientRegions.FromGatePath("/api/Login/Login"), "plain gate path is EN");
        AssertEqual("en", ClientRegions.FromGatePath("/api/Login/Login-zz"), "unknown gate suffix is EN");
        AssertEqual("tw", ClientRegions.FromPackage("com.kurogame.punishing.grayraven.tw"), "TW package region");
        AssertEqual(true, ClientRegions.FromPackage("com.kurogame.punishing.grayraven.en") is null, "EN package has no region");

        HashSet<int> twTasks = TableReaderV2.Parse<TaskTable>("tw").Select(row => row.Id)
            .Concat(TableReaderV2.Parse<BigWorldTaskTable>("tw").Select(row => row.Id)).ToHashSet();
        HashSet<int> enTasks = TableReaderV2.Parse<TaskTable>().Select(row => row.Id)
            .Concat(TableReaderV2.Parse<BigWorldTaskTable>().Select(row => row.Id)).ToHashSet();
        HashSet<int> twActivities = TableReaderV2.Parse<MailCollectionBoxActivityTable>("tw").Select(row => row.Id).ToHashSet();
        AssertEqual(false, ReferenceEquals(TableReaderV2.Parse<TaskTable>("tw"), TableReaderV2.Parse<TaskTable>()), "TW reads its own Task overlay");
        AssertEqual(true, ReferenceEquals(TableReaderV2.Parse<TaskTable>("zz"), TableReaderV2.Parse<TaskTable>()), "a region without overlay reads the shared table");
        // The client log of the reported hang: these ids exist only in the EN-derived tables.
        foreach (int missing in new[] { 2120001, 3001699, 3001715, 3001728 })
        {
            AssertEqual(true, enTasks.Contains(missing), $"shared tables have task {missing}");
            AssertEqual(false, twTasks.Contains(missing), $"TW tables lack task {missing}");
        }
        AssertEqual(false, twActivities.Contains(50007), "TW tables lack collection-box activity 50007");

        MethodInfo doLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();

        (HashSet<int> Tasks, HashSet<int> Finished, List<int> Mail) Login(string? region, long uid)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.ClientRegion = region;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
                CreateDrawCompatibilityInventory(uid, []), $"region-tables-{region ?? "en"}", startClientLoop: false);
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            harness.Session.character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            doLogin.Invoke(null, [harness.Session]);
            HashSet<int> tasks = [], finished = [];
            List<int> mail = [];
            bool done = false;
            for (int index = 0; index < 400 && !done; index++)
            {
                Packet.Push push = ReadStartupPush(harness, $"region tables {region ?? "en"} login push {index}");
                switch (push.Name)
                {
                    case nameof(NotifyTaskData):
                        NotifyTaskData data = MessagePackSerializer.Deserialize<NotifyTaskData>(push.Content);
                        tasks.UnionWith(data.TaskData.Tasks.Select(task => (int)task.Id));
                        finished.UnionWith(data.TaskData.FinishedTasks);
                        break;
                    case nameof(NotifyTask):
                        tasks.UnionWith(MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks.Select(task => (int)task.Id));
                        break;
                    case nameof(NotifyLoginMailCollectionBoxData):
                        mail = MessagePackSerializer.Deserialize<NotifyLoginMailCollectionBoxData>(push.Content).OpenActivityIds;
                        break;
                    case "NotifyWheelchairManualActivityUpdate":
                        done = true;
                        break;
                }
            }
            AssertEqual(true, done, $"{region ?? "en"} login reached its last startup push");
            return (tasks, finished, mail);
        }

        var en = Login(null, 99_851);
        var tw = Login("tw", 99_852);
        // The login list omits client-default rows (Program.LoginStall.cs), so the EN-only ids from the TW report (e.g. 2120001, Active/0)
        // are no longer in it; they still reach a client through gameplay NotifyTask pushes, which FilterTasks covers below.
        AssertEqual(true, tw.Tasks.All(twTasks.Contains), "TW login sends only tasks in the TW tables");
        AssertEqual(true, tw.Tasks.SetEquals(en.Tasks.Where(twTasks.Contains)), "TW login drops exactly the ids TW lacks");
        AssertEqual(true, tw.Finished.All(twTasks.Contains), "TW login finished-task ids are in the TW tables");
        AssertEqual(true, en.Tasks.All(enTasks.Contains), "EN login ids stay inside the shared tables");

        AssertEqual(true, en.Mail.Contains(50007), "EN login opens collection-box activity 50007");
        AssertEqual(true, tw.Mail.Count > 0 && tw.Mail.All(twActivities.Contains), "TW login opens only TW collection-box activities");
        AssertEqual(false, tw.Mail.Contains(50007), "TW login omits EN-only collection-box activity 50007");
        AssertEqual(true, en.Mail.Any(id => !twActivities.Contains(id)), "EN login keeps shared-table activities TW lacks");

        // KR, JP and CN have the same class of mismatch (1,462 / 1,493 / 1,564 EN-only task ids, 38 / 62 / 62 EN-only activities) and their own overlay.
        HashSet<int> enActivities = TableReaderV2.Parse<MailCollectionBoxActivityTable>().Select(row => row.Id).ToHashSet();
        foreach (string region in new[] { "kr", "jp", "cn" })
        {
            HashSet<int> own = AscNet.GameServer.Game.ClientRegionTables.TaskIds(region) ?? throw new InvalidDataException($"{region} has no Task overlay");
            int enOnlyTask = enTasks.Where(id => !own.Contains(id)).Min();
            int sharedTask = enTasks.Where(own.Contains).Min();
            NotifyTask push = new() { Tasks = new() { Tasks = [new() { Id = (uint)enOnlyTask }, new() { Id = (uint)sharedTask }] } };
            AscNet.GameServer.Game.ClientRegionTables.FilterTasks(region, push);
            AssertEqual(true, push.Tasks.Tasks.Select(task => (int)task.Id).SequenceEqual([sharedTask]), $"{region} task push keeps only ids its tables have");
            HashSet<int> regionActivities = TableReaderV2.Parse<MailCollectionBoxActivityTable>(region).Select(row => row.Id).ToHashSet();
            AssertEqual(false, regionActivities.Contains(50007), $"{region} tables lack collection-box activity 50007");
            AssertEqual(true, regionActivities.Any(id => !enActivities.Contains(id)) && enActivities.Any(id => !regionActivities.Contains(id)),
                $"{region} collection-box activities differ from the shared set both ways");
        }

        // Favourite mail claims validate against the player's own region: a TW-only mail id (the long-standing TW gap) is claimable
        // by a TW player and unknown to an EN one.
        HashSet<int> enMails = TableReaderV2.Parse<MailCollectionBoxFavoriteMailTable>().Select(row => row.Id).ToHashSet();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<int> twOpen = TableReaderV2.Parse<MailCollectionBoxActivityTable>("tw")
            .Where(activity => CollectionMiscModule.IsFavoriteMailActivityOpen(activity, now)).SelectMany(activity => activity.FavoriteMailIds).ToHashSet();
        int twMail = TableReaderV2.Parse<MailCollectionBoxFavoriteMailTable>("tw")
            .Where(mail => !enMails.Contains(mail.Id) && twOpen.Contains(mail.Id) && !mail.RewardIds.Any(reward => reward > 0)).Min(mail => mail.Id);
        MailCollectionBoxReceiveFavoriteMailIdsResponse Claim(string? region, long uid)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.ClientRegion = region;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
                CreateDrawCompatibilityInventory(uid, []), $"region-claim-{region ?? "en"}", startClientLoop: false);
            InvokeRegisteredRequestHandler("MailCollectionBoxReceiveFavoriteMailIdsRequest", harness.Session, 99_853_001,
                new MailCollectionBoxReceiveFavoriteMailIdsRequest { FavoriteMailIds = [twMail] });
            var response = ReadResponsePayload<MailCollectionBoxReceiveFavoriteMailIdsResponse>(harness, 99_853_001,
                nameof(MailCollectionBoxReceiveFavoriteMailIdsResponse), "region favourite mail claim", maxPacketsToRead: 16);
            AssertEqual(response.Code == 0, player.ReceivedFavoriteMailIds.Contains(twMail), $"{region ?? "en"} claim persists exactly when accepted");
            return response;
        }
        MailCollectionBoxReceiveFavoriteMailIdsResponse twClaim = Claim("tw", 99_854);
        AssertEqual(0, twClaim.Code, "TW player claims a TW-only favourite mail");
        AssertEqual(true, twClaim.SuccessedIds.SequenceEqual([twMail]), "TW claim reports the TW mail id");
        AssertEqual(20027007, Claim(null, 99_855).Code, "EN player: the TW-only mail id is unknown");

        // The gate's region reaches the player: persisted once, kept when a later login has no gate request, EN never rewritten.
        Player gated = CreateDrawCompatibilityPlayer(99_856);
        gated.Token = Guid.NewGuid().ToString();
        AccountModule.ApplyClientRegion(gated);
        AssertEqual(true, gated.ClientRegion is null, "no gate request leaves a fresh player on the shared tables");
        ClientRegions.Remember(gated.Token, "tw");
        AccountModule.ApplyClientRegion(gated);
        AssertEqual("tw", gated.ClientRegion, "TW gate request sets the player's region");
        AssertEqual("tw", BsonSerializer.Deserialize<Player>(gated.ToBson()).ClientRegion, "region survives a BSON reload (reconnect after restart)");
        gated.Token = Guid.NewGuid().ToString();
        AccountModule.ApplyClientRegion(gated);
        AssertEqual("tw", gated.ClientRegion, "a login without a gate request keeps the persisted region");
        ClientRegions.Remember(gated.Token, "en");
        AccountModule.ApplyClientRegion(gated);
        AssertEqual(true, gated.ClientRegion is null, "plain gate request returns the player to the shared tables");
    }
}
