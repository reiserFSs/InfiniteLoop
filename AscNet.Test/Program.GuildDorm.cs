using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer;
using MessagePack;
using Newtonsoft.Json.Linq;
using MongoDB.Driver;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateGuildDormCompatibility()
        {
            using GuildTestScope scope = new();
            GuildAssert(GuildDormRoomService.Port == 0, "Dorm verification must own its listener");
            GuildDormRoomService.Start();
            try
            {
                var leader = scope.CreatePlayer();
                var member = scope.CreatePlayer();
                var otherChannel = scope.CreatePlayer();
                var outsider = scope.CreatePlayer();
                Guild guild = scope.SeedGuild(leader, member, otherChannel);
                long leaderUid = leader.Session.player.PlayerData.Id;
                long memberUid = member.Session.player.PlayerData.Id;
                long outsiderUid = outsider.Session.player.PlayerData.Id;
                int roomId = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormRoomTable>().First().Id;
                GuildAssert(GuildRpc(outsider, "GuildDormPreEnterRequest", DormArgs(("RoomId", roomId), ("ChannelId", 1))).Value<int>("Code") != 0,
                    "Dorm nonmember cannot allocate a token");
                scope.SeedGuild(outsider);
                HashSet<int> dormRoles = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormRoleTable>().Select(row => row.Id).ToHashSet();
                var alternate = TableReaderV2.Parse<AscNet.Table.V2.share.character.CharacterTable>()
                    .First(row => dormRoles.Contains(row.Id) && !leader.Session.character.Characters.Any(character => character.Id == row.Id)
                        && Character.IsOwnableCharacter(checked((uint)row.Id)));
                leader.Session.character.Characters.Add(CreateLoginAccountCompatibilityCharacter(checked((uint)alternate.Id), checked((uint)alternate.DefaultNpcFashtionId)));
                Character.collection.ReplaceOne(row => row.Uid == leaderUid, leader.Session.character);
                foreach (var player in new[] { leader, member, otherChannel, outsider })
                    RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildDormModule").GetMethod("PrepareLogin")!
                        .Invoke(null, new object[] { player.Session });
                GuildAssert(GuildRpc(leader, "GuildDormPreEnterRequest", DormArgs(("RoomId", int.MaxValue), ("ChannelId", 1))).Value<int>("Code") != 0,
                    "Dorm unknown room rejected");

                JObject PreEnter(LoopbackSessionHarness player, int channel = 1)
                {
                    JObject response = GuildRpc(player, "GuildDormPreEnterRequest", DormArgs(("RoomId", roomId), ("ChannelId", channel)));
                    GuildAssert(response.Value<int>("Code") == 0, "Dorm main-server handoff succeeds");
                    JObject connect = (JObject)response["ConnectData"]!;
                    GuildAssert(connect.Value<int>("TcpPort") == GuildDormRoomService.Port
                        && !string.IsNullOrEmpty(connect.Value<string>("Token"))
                        && !string.IsNullOrEmpty(connect.Value<string>("ChatChannelId")), "Dorm real listener handoff");
                    return connect;
                }

                JObject Enter(GuildDormTcpClient client, JObject connect, long uid, bool valid, bool fragment = false)
                {
                    JObject response = client.Call("GuildDormEnterRoomRequest",
                        DormArgs(("PlayerId", checked((int)uid)), ("Token", connect.Value<string>("Token")!)), fragment);
                    GuildAssert((response.Value<int>("Code") == 0) == valid, "Dorm token authentication result");
                    if (valid)
                    {
                        GuildAssert(response.Value<int>("Port") == 0 && response.Value<uint>("Conv") == 0,
                            "Dorm TCP-only does not advertise fake KCP");
                        GuildAssert(response["RoomData"] is JObject, "Dorm successful entry supplies room snapshot");
                    }
                    else client.AssertClosed();
                    return response;
                }

                JObject admission = PreEnter(leader);
                List<GuildDormTcpClient> pending = [];
                try
                {
                    var admissionClock = System.Diagnostics.Stopwatch.StartNew();
                    for (int index = 0; index < 64; index++)
                        pending.Add(new GuildDormTcpClient(admission));
                    GuildAssert(admissionClock.Elapsed < TimeSpan.FromSeconds(5), "Dorm admission fixture fills before handshake timeout");
                    using (var excess = new GuildDormTcpClient(admission))
                        excess.AssertClosed(TimeSpan.FromSeconds(1));
                    // Finish the held handshakes over the actual stream, observing closure.
                    foreach (var held in pending) held.RejectInvalidLength();
                }
                finally
                {
                    foreach (var held in pending) held.Dispose();
                }
                using (var recovered = new GuildDormTcpClient(admission))
                {
                    Enter(recovered, admission, leaderUid, true);
                    GuildAssert(recovered.Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0,
                        "Dorm admission capacity recovers after pending-handshake cleanup");
                    recovered.RejectInvalidLength((1 << 22) + 1);
                }

                JObject expired = PreEnter(leader);
                // Exercise the real monotonic 60-second token lifetime, not a fabricated token store.
                Thread.Sleep(TimeSpan.FromSeconds(61));
                using (var client = new GuildDormTcpClient(expired)) Enter(client, expired, leaderUid, false);
                JObject wrongPlayer = PreEnter(leader);
                using (var client = new GuildDormTcpClient(wrongPlayer)) Enter(client, wrongPlayer, outsiderUid, false);
                using (var consumed = new GuildDormTcpClient(wrongPlayer)) Enter(consumed, wrongPlayer, leaderUid, false);

                JObject leaderConnect = PreEnter(leader);
                foreach (int invalidLength in new[] { 0, -1, 16 * 1024 + 1 })
                    using (var malformed = new GuildDormTcpClient(leaderConnect)) malformed.RejectInvalidLength(invalidLength);
                using var leaderRoom = new GuildDormTcpClient(leaderConnect);
                JObject initial = Enter(leaderRoom, leaderConnect, leaderUid, true, fragment: true);
                GuildAssert(DormObjects(DormObject(initial["RoomData"], "RoomData")["PlayerDatas"])
                    .Select(player => player.Value<long>("PlayerId")).SequenceEqual(new[] { leaderUid }), "Dorm initial presence only contains actual occupant");
                using (var replay = new GuildDormTcpClient(leaderConnect)) Enter(replay, leaderConnect, leaderUid, false);
                JObject memberConnect = PreEnter(member);
                using var memberRoom = new GuildDormTcpClient(memberConnect);
                JObject joined = Enter(memberRoom, memberConnect, memberUid, true);
                GuildAssert(DormObjects(DormObject(joined["RoomData"], "RoomData")["PlayerDatas"]).Select(player => player.Value<long>("PlayerId"))
                    .Order().SequenceEqual(new[] { leaderUid, memberUid }.Order()), "Dorm peer join snapshot");
                leaderRoom.Push("NotifyGuildDormSyncEntities", data => DormHasPlayer(data, memberUid));

                JObject channelConnect = PreEnter(otherChannel, 2);
                using var isolatedChannel = new GuildDormTcpClient(channelConnect);
                JObject channelRoom = Enter(isolatedChannel, channelConnect, otherChannel.Session.player.PlayerData.Id, true);
                GuildAssert(DormObject(channelRoom["RoomData"], "RoomData").Value<int>("ChannelId") == 2
                    && DormObjects(DormObject(channelRoom["RoomData"], "RoomData")["PlayerDatas"]).Count() == 1, "Dorm channel presence isolation");
                JObject outsiderConnect = PreEnter(outsider);
                using var isolatedGuild = new GuildDormTcpClient(outsiderConnect);
                JObject outsiderRoom = Enter(isolatedGuild, outsiderConnect, outsiderUid, true);
                GuildAssert(DormObjects(DormObject(outsiderRoom["RoomData"], "RoomData")["PlayerDatas"])
                    .All(player => player.Value<long>("PlayerId") == outsiderUid), "Dorm guild presence isolation");

                foreach (float x in new[] { 12.25f, -3.5f })
                {
                    leaderRoom.Send("GuildDormSyncPlayerStateMessage", DormArgs(("Position",
                        DormArgs(("X", x), ("Z", 7.5f), ("Y", 0.25f), ("Angle", 90f))), ("State", 2)));
                    JObject sync = memberRoom.Push("NotifyGuildDormSyncEntities", data => DormHasPlayer(data, leaderUid)
                        && DormObjects(data["PlayerDatas"]).Any(player => player.Value<long>("PlayerId") == leaderUid
                            && DormObject(player["Position"], "Position").Value<float>("X") == x));
                    GuildAssert(DormObject(DormObjects(sync["PlayerDatas"]).Single(player => player.Value<long>("PlayerId") == leaderUid)["Position"], "Position").Value<float>("Z") == 7.5f, "Dorm transform broadcast preserves accepted coordinates");
                }
                isolatedChannel.AssertNoPush("NotifyGuildDormSyncEntities", data => DormHasPlayer(data, leaderUid));
                isolatedGuild.AssertNoPush("NotifyGuildDormSyncEntities", data => DormHasPlayer(data, leaderUid));
                isolatedGuild.Exit();
                isolatedChannel.Send("GuildDormSyncPlayerStateMessage", DormArgs(("Position",
                    DormArgs(("X", float.NaN), ("Z", 0f), ("Y", 0f), ("Angle", 0f))), ("State", 2)));
                isolatedChannel.Push("NotifyGuildDormMessageFailed", data => data.Value<int>("Code") != 0);
                GuildAssert(isolatedChannel.Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0,
                    "Dorm invalid transform rejected without poisoning connection");
                isolatedChannel.Exit();
                leaderRoom.Send("GuildDormPlayActionMessage", DormArgs(("ActionId", -1)));
                memberRoom.Push("NotifyGuildDormPlayAction", data => data.Value<long>("PlayerId") == leaderUid && data.Value<int>("ActionId") == -1);
                int actionId = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormPlayActionTable>().First().Id;
                leaderRoom.Send("GuildDormPlayActionMessage", DormArgs(("ActionId", actionId)));
                memberRoom.Push("NotifyGuildDormPlayAction", data => data.Value<long>("PlayerId") == leaderUid && data.Value<int>("ActionId") == actionId);
                leaderRoom.Send("GuildDormPlayActionMessage", DormArgs(("ActionId", -1)));
                memberRoom.Push("NotifyGuildDormPlayAction", data => data.Value<long>("PlayerId") == leaderUid && data.Value<int>("ActionId") == -1);
                leaderRoom.AssertCoalescedHeartbeats();
                GuildAssert(leaderRoom.Call("GuildDormKcpConfirmRequest", DormArgs()).Value<int>("Code") != 0,
                    "Dorm TCP cannot claim successful KCP confirmation");
                GuildAssert(leaderRoom.Call("GuildDormNpcInteractRequest", DormArgs(("NpcId", int.MaxValue))).Value<int>("Code") != 0,
                    "Dorm absent dynamic NPC cannot be invented");
                GuildAssert(leaderRoom.Call("GuildDormChangeCharacterRequest", DormArgs(("CharacterId", int.MaxValue))).Value<int>("Code") != 0,
                    "Dorm rejects unowned character");
                int ownedCharacter = alternate.Id;
                GuildAssert(leaderRoom.Call("GuildDormChangeCharacterRequest", DormArgs(("CharacterId", ownedCharacter))).Value<int>("Code") == 0,
                    "Dorm owned character selection");
                memberRoom.Push("NotifyGuildDormSyncEntities", data => DormObjects(data["PlayerDatas"])
                    .Any(player => player.Value<long>("PlayerId") == leaderUid && player.Value<int?>("CharacterId") == ownedCharacter));
                GuildAssert(Player.collection.Find(row => row.PlayerData.Id == leaderUid).Single().GuildState.Dorm.CurrentCharacterId == ownedCharacter,
                    "Dorm selected avatar survives database reload");

                ValidateGuildDormInteractions(leader, member, leaderRoom, memberRoom, initial, roomId, guild);

                memberRoom.Dispose();
                leaderRoom.Push("NotifyGuildDormPlayerExit", data => data.Value<long>("PlayerId") == memberUid);

                JObject slowConnect = PreEnter(otherChannel);
                using (var slowReader = new GuildDormTcpClient(slowConnect, receiveBufferSize: 1024))
                {
                    long slowUid = otherChannel.Session.player.PlayerData.Id;
                    Enter(slowReader, slowConnect, slowUid, true);
                    // Do not read this peer again: its advertised receive window must fill.
                    var flood = System.Diagnostics.Stopwatch.StartNew();
                    for (int batch = 0; batch < 4096 && flood.Elapsed < TimeSpan.FromSeconds(15)
                        && !leaderRoom.HasPush("NotifyGuildDormPlayerExit", data => data.Value<long>("PlayerId") == slowUid); batch++)
                    {
                        for (int offset = 0; offset < 16; offset++)
                            leaderRoom.Send("GuildDormSyncPlayerStateMessage", DormArgs(("Position",
                                DormArgs(("X", (float)(batch * 16 + offset)), ("Z", 0f), ("Y", 0f), ("Angle", 0f))), ("State", 2)));
                        GuildAssert(leaderRoom.Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0,
                            "Dorm slow reader cannot block healthy room responses");
                        if (batch % 16 == 0)
                        {
                            JObject mainResponse = Task.Run(() => GuildRpc(leader, "GuildDormRoomChannelDataRequest", DormArgs(("RoomId", roomId))))
                                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                            GuildAssert(mainResponse.Value<int>("Code") == 0, "Dorm slow reader cannot block main-server guild requests");
                        }
                    }
                    GuildAssert(leaderRoom.HasPush("NotifyGuildDormPlayerExit", data => data.Value<long>("PlayerId") == slowUid),
                        "Dorm bounds slow-reader output and evicts it before ordinary idle expiry");
                    leaderRoom.Push("NotifyGuildDormPlayerExit", data => data.Value<long>("PlayerId") == slowUid);
                }
                JObject reconnect = PreEnter(member);
                using var reconnected = new GuildDormTcpClient(reconnect);
                JObject resumed = Enter(reconnected, reconnect, memberUid, true);
                GuildAssert(DormObjects(DormObject(resumed["RoomData"], "RoomData")["PlayerDatas"]).Count(player => player.Value<long>("PlayerId") == memberUid) == 1,
                    "Dorm reconnect has no duplicate ghost presence");
                GuildAssert(reconnected.Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0, "Dorm reconnect heartbeat");
                JObject revoked = PreEnter(member);
                GuildAssert(GuildRpc(leader, "GuildKickMemberRequest", DormArgs(("OtherId", memberUid))).Value<int>("Code") == 0, "Dorm membership removal succeeds");
                reconnected.AssertClosed();
                leaderRoom.Push("NotifyGuildDormPlayerExit", data => data.Value<long>("PlayerId") == memberUid);
                using (var stale = new GuildDormTcpClient(revoked)) Enter(stale, revoked, memberUid, false);
                GuildAssert(GuildRpc(member, "GuildDormPreEnterRequest", DormArgs(("RoomId", roomId), ("ChannelId", 1))).Value<int>("Code") != 0,
                    "Dorm expelled player cannot reenter");

                var randomBox = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormFurnitureRandomBoxTable>().First();
                var placement = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormDefaultFurnitureTable>()
                    .First(row => row.FurnitureId == randomBox.Id);
                var theme = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormRoomThemeTable>().Single(row => row.Id == placement.ThemeId);
                GuildAssert(GuildRpc(leader, "GuildDormSetRoomThemeRequest", DormArgs(("RoomId", roomId), ("ThemeId", theme.Id))).Value<int>("Code") != 0,
                    "Dorm paid theme requires owned entitlement");
                // Fixture entitlement is scoped to this newly created guild, never a captured guild document.
                Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.AddToSet(row => row.DormThemes, theme.Id));
                JObject themeResponse = GuildRpc(leader, "GuildDormSetRoomThemeRequest", DormArgs(("RoomId", roomId), ("ThemeId", theme.Id)));
                GuildAssert(themeResponse.Value<int>("Code") == 0 && themeResponse.Value<long>("NextSetRoomThemeTime") > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "Dorm owned theme sets durable cooldown");
                leaderRoom.Push("NotifyGuildDormThemeChanged", data => data.Value<int>("ThemeId") == theme.Id);
                GuildAssert(GuildRpc(leader, "GuildDormSetRoomThemeRequest", DormArgs(("RoomId", roomId), ("ThemeId", theme.Id))).Value<int>("Code") != 0,
                    "Dorm theme change cooldown enforced");
                leaderRoom.Exit();
                JObject themeConnect = PreEnter(leader);
                using var themedRoom = new GuildDormTcpClient(themeConnect);
                JObject themedSnapshot = Enter(themedRoom, themeConnect, leaderUid, true);
                GuildAssert(DormObject(themedSnapshot["RoomData"], "RoomData").Value<int>("ThemeId") == theme.Id, "Dorm reconnect reads persisted theme");
                ValidateGuildDormOneTimeRewards(leader, themedSnapshot, randomBox.Id, randomBox.RandomTimes);
                themedRoom.Exit();
                JObject finalConnect = PreEnter(leader);
                using var finalRoom = new GuildDormTcpClient(finalConnect);
                Enter(finalRoom, finalConnect, leaderUid, true);
                GuildAssert(GuildRpc(leader, "GuildDormGetDailyInteractRewardRequest", DormArgs()).Value<int>("Code") != 0,
                    "Dorm daily reward cap survives room disconnect and reconnect");
                finalRoom.Exit();
            }
            finally
            {
                GuildDormRoomService.Stop();
            }
        }

        private static void ValidateGuildDormInteractions(LoopbackSessionHarness leader, LoopbackSessionHarness member,
            GuildDormTcpClient leaderRoom, GuildDormTcpClient memberRoom, JObject initial, int roomId, Guild guild)
        {
            long leaderUid = leader.Session.player.PlayerData.Id;
            var config = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormConfigTable>().Single();
            var furnitureRows = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormFurnitureTable>();
            HashSet<int> present = DormObjects(DormObject(initial["RoomData"], "RoomData")["FurnitureDatas"]).Select(row => row.Value<int>("Id")).ToHashSet();
            int furnitureId = furnitureRows.First(row => present.Contains(row.Id) && row.IsGetReward == 1).Id;
            GuildAssert(GuildRpc(leader, "GuildDormGetDailyInteractRewardRequest", DormArgs()).Value<int>("Code") != 0,
                "Dorm daily reward requires an earned interaction");
            GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", int.MaxValue))).Value<int>("Code") != 0,
                "Dorm unknown furniture rejected");
            GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", furnitureId))).Value<int>("Code") == 0,
                "Dorm starts real furniture interaction");
            memberRoom.Push("NotifyGuildDormSyncFurniture", data => DormObjects(data["FurnitureDatas"])
                .Any(row => row.Value<int>("Id") == furnitureId && row.Value<long>("PlayerId") == leaderUid));
            GuildAssert(memberRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", furnitureId))).Value<int>("Code") != 0,
                "Dorm furniture occupancy cannot be stolen");
            GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", -1))).Value<int>("Code") == 0,
                "Dorm ends furniture interaction");
            GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", furnitureId))).Value<int>("Code") != 0,
                "Dorm interaction cooldown prevents immediate restart");
            memberRoom.Push("NotifyGuildDormSyncFurniture", data => DormObjects(data["FurnitureDatas"])
                .Any(row => row.Value<int>("Id") == furnitureId && row.Value<long>("PlayerId") == 0));

            int cap = Player.collection.Find(row => row.PlayerData.Id == leaderUid).Single().GuildState.Dorm.DailyInteractRewardTotalTimes;
            GuildAssert(cap >= config.DailyInteractRewardTimesMin && cap <= config.DailyInteractRewardTimesMax, "Dorm daily quota derives from config");
            for (int index = 0; index < cap; index++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(config.InteractIntervalTime + 1));
                GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", furnitureId))).Value<int>("Code") == 0,
                    "Dorm repeat eligible interaction");
                Dictionary<int, long> before = Inventory.collection.Find(row => row.Uid == leaderUid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
                JObject reward = GuildRpc(leader, "GuildDormGetDailyInteractRewardRequest", DormArgs());
                GuildAssert(reward.Value<int>("Code") == 0, "Dorm earned daily claim succeeds");
                DormAssertRewardDelta(leaderUid, before, reward, config.DailyInteractRewardId);
                GuildAssert(GuildRpc(leader, "GuildDormGetDailyInteractRewardRequest", DormArgs()).Value<int>("Code") != 0,
                    "Dorm daily claim cannot replay without another interaction");
                GuildAssert(leaderRoom.Call("GuildDormFurnitureInteractRequest", DormArgs(("FurnitureId", -1))).Value<int>("Code") == 0,
                    "Dorm interaction releases after claim");
                GuildAssert(memberRoom.Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0, "Dorm peer stays connected during rewards");
            }
            GuildAssert(Player.collection.Find(row => row.PlayerData.Id == leaderUid).Single().GuildState.Dorm.DailyInteractRewardCurTimes == cap,
                "Dorm reward receipts survive database reload");
            Dictionary<int, long> capped = Inventory.collection.Find(row => row.Uid == leaderUid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
            GuildAssert(GuildRpc(leader, "GuildDormGetDailyInteractRewardRequest", DormArgs()).Value<int>("Code") != 0, "Dorm daily cap enforced");
            GuildAssert(Inventory.collection.Find(row => row.Uid == leaderUid).Single().Items.All(item => capped.GetValueOrDefault(item.Id) == item.Count),
                "Dorm rejected duplicate cannot grant inventory");

            int bgmId = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormBgmTable>().First(row => !(row.NeedBuy > 0)).Id;
            GuildAssert(GuildRpc(member, "GuildDormSetRoomBgmIdsRequest", DormArgs(("RoomId", roomId), ("BgmIds", new[] { bgmId }))).Value<int>("Code") != 0,
                "Dorm ordinary member cannot change shared music");
            GuildAssert(GuildRpc(leader, "GuildDormSetRoomBgmIdsRequest", DormArgs(("RoomId", roomId), ("BgmIds", new[] { bgmId }))).Value<int>("Code") == 0,
                "Dorm leader changes music");
            memberRoom.Push("NotifyGuildDormBgmChanged", data => data["BgmIds"]!.Values<int>().SequenceEqual(new[] { bgmId }));
            GuildAssert(Guild.collection.Find(row => row.Id == guild.Id).Single().DormRooms[roomId].BgmIds.SequenceEqual(new[] { bgmId }),
                "Dorm music persists after reload");
            int currentTheme = DormObject(initial["RoomData"], "RoomData").Value<int>("ThemeId");
            GuildAssert(GuildRpc(member, "GuildDormSetRoomThemeRequest", DormArgs(("RoomId", roomId), ("ThemeId", currentTheme))).Value<int>("Code") != 0,
                "Dorm ordinary member cannot change theme");
            GuildAssert(GuildRpc(leader, "GuildDormSetRoomThemeRequest", DormArgs(("RoomId", roomId), ("ThemeId", int.MaxValue))).Value<int>("Code") != 0,
                "Dorm unknown theme cannot mutate room");
        }

        private static void ValidateGuildDormOneTimeRewards(LoopbackSessionHarness player, JObject snapshot, int boxId, int boxLimit)
        {
            long uid = player.Session.player.PlayerData.Id;
            HashSet<int> present = DormObjects(DormObject(snapshot["RoomData"], "RoomData")["FurnitureDatas"]).Select(row => row.Value<int>("Id")).ToHashSet();
            var interaction = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormFurnitureInteractionTable>()
                .First(row => present.Contains(row.Id) && row.NeedMark != 0 && row.ReplyRewardIds.Any(id => id > 0));
            int reply = interaction.ReplyRewardIds.FindIndex(id => id > 0) + 1;
            GuildAssert(GuildRpc(player, "GuildDormRecordInteractRequest", DormArgs(("FurnitureId", interaction.Id))).Value<int>("Code") == 0,
                "Dorm records authored interaction");
            GuildAssert(GuildRpc(player, "GuildDormRecordInteractRequest", DormArgs(("FurnitureId", interaction.Id))).Value<int>("Code") != 0,
                "Dorm duplicate interaction record rejected");
            GuildAssert(Player.collection.Find(row => row.PlayerData.Id == uid).Single().GuildState.Dorm.InteractedFurnitureIds.Contains(interaction.Id),
                "Dorm interaction record survives persistence");
            GuildAssert(GuildRpc(player, "GuildDormGetOneTimeInteractRewardRequest", DormArgs(("FurnitureId", interaction.Id), ("ReplyIndex", 0))).Value<int>("Code") != 0,
                "Dorm invalid reply cannot claim");
            Dictionary<int, long> before = Inventory.collection.Find(row => row.Uid == uid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
            JObject reward = GuildRpc(player, "GuildDormGetOneTimeInteractRewardRequest", DormArgs(("FurnitureId", interaction.Id), ("ReplyIndex", reply)));
            GuildAssert(reward.Value<int>("Code") == 0, "Dorm authored one-time reply grants reward");
            DormAssertRewardDelta(uid, before, reward, interaction.ReplyRewardIds[reply - 1]);
            before = Inventory.collection.Find(row => row.Uid == uid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
            GuildAssert(GuildRpc(player, "GuildDormGetOneTimeInteractRewardRequest", DormArgs(("FurnitureId", interaction.Id), ("ReplyIndex", reply))).Value<int>("Code") != 0,
                "Dorm one-time reply cannot replay");
            GuildAssert(Inventory.collection.Find(row => row.Uid == uid).Single().Items.All(item => before.GetValueOrDefault(item.Id) == item.Count),
                "Dorm replay preserves inventory");
            GuildAssert(Player.collection.Find(row => row.PlayerData.Id == uid).Single().GuildState.Dorm.OneTimeInteractReplyIds.Contains((long)interaction.Id * 100 + reply),
                "Dorm one-time receipt persists");

            HashSet<int> choices = TableReaderV2.Parse<AscNet.Table.V2.share.guilddorm.GuildDormFurnitureRandomBoxItemTable>()
                .Where(row => row.FurnitureId == boxId).Select(row => row.Id).ToHashSet();
            for (int index = 1; index <= boxLimit; index++)
            {
                JObject result = GuildRpc(player, "GuildDormCallRandomBoxRequest", DormArgs(("FurnitureId", boxId)));
                GuildAssert(result.Value<int>("Code") == 0 && result["RandomBox"]!.Value<int>("RandomTimes") == index
                    && choices.Contains(result["RandomBox"]!.Value<int>("RandomItemId")), "Dorm random box uses authored pool and increments durable count");
            }
            GuildAssert(GuildRpc(player, "GuildDormCallRandomBoxRequest", DormArgs(("FurnitureId", boxId))).Value<int>("Code") != 0,
                "Dorm random-box authored quota enforced");
            GuildAssert(Player.collection.Find(row => row.PlayerData.Id == uid).Single().GuildState.Dorm.RandomBoxes.Single(box => box.FurnitureId == boxId).RandomTimes == boxLimit,
                "Dorm random-box count survives reload without an extra roll");
        }

        private static void DormAssertRewardDelta(long uid, Dictionary<int, long> before, JObject response, int rewardId)
        {
            JObject[] rewards = DormObjects(response["RewardGoodsList"]).ToArray();
            GuildAssert(rewards.Length > 0, "Dorm earned claim must expose actual grants");
            var expected = ResolveRewardGoods(rewardId, TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>(), "Dorm authored reward");
            GuildAssert(rewards.Select(reward => (reward.Value<int>("TemplateId"), reward.Value<long>("Count"))).Order()
                .SequenceEqual(expected.Select(reward => (reward.TemplateId, (long)reward.Count)).Order()), "Dorm grants match authoritative reward table");
            var after = Inventory.collection.Find(row => row.Uid == uid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
            foreach (var group in rewards.GroupBy(reward => reward.Value<int>("TemplateId")))
                GuildAssert(after.GetValueOrDefault(group.Key) - before.GetValueOrDefault(group.Key) == group.Sum(reward => reward.Value<long>("Count")),
                    "Dorm reward response matches persisted inventory delta");
        }

        private static bool DormHasPlayer(JObject data, long uid) =>
            data["PlayerDatas"] is JArray players && DormObjects(players).Any(player => player.Value<long>("PlayerId") == uid);

        private static JObject DormObject(JToken? value, string field) =>
            value as JObject ?? throw new InvalidDataException($"Dorm required {field} payload is missing or is not an object.");

        private static IEnumerable<JObject> DormObjects(JToken? value)
        {
            if (value is not JArray array)
                throw new InvalidDataException($"Dorm required object array '{value?.Path ?? "<missing>"}' is missing or is not an array.");
            foreach (JToken? element in array)
                yield return DormObject(element, element?.Path ?? "array element");
        }

        private static Dictionary<string, object> DormArgs(params (string Name, object Value)[] fields) =>
            fields.ToDictionary(field => field.Name, field => field.Value);

        // Unlike the main-session fixture, this client connects to the real room listener.
        private sealed class GuildDormTcpClient : IDisposable
        {
            private static readonly MessagePackSerializerOptions PlainOptions =
                MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);
            private readonly TcpClient socket = new() { NoDelay = true };
            private readonly List<(string Name, JObject Body)> pushes = [];
            private int requestId;

            public GuildDormTcpClient(JObject connect, int? receiveBufferSize = null)
            {
                if (receiveBufferSize.HasValue) socket.ReceiveBufferSize = receiveBufferSize.Value;
                socket.Connect(connect.Value<string>("IpAddress")!, connect.Value<int>("TcpPort"));
            }

            public void AssertCoalescedHeartbeats()
            {
                int first = ++requestId;
                int second = ++requestId;
                byte[] frames = RequestFrame("GuildDormHeartbeatRequest", first, DormArgs())
                    .Concat(RequestFrame("GuildDormHeartbeatRequest", second, DormArgs())).ToArray();
                socket.GetStream().Write(frames);
                foreach (int expected in new[] { first, second })
                {
                    Packet packet = Read();
                    while (packet.Type == Packet.ContentType.Push) { Remember(packet); packet = Read(); }
                    GuildAssert(packet.Type == Packet.ContentType.Response, "Dorm coalesced request response envelope");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    GuildAssert(response.Id == expected && response.Name == "GuildDormHeartbeatResponse"
                        && JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content)).Value<int>("Code") == 0,
                        "Dorm parses two coalesced frames in order");
                }
            }

            public void Exit()
            {
                socket.GetStream().Write(RequestFrame("GuildDormExitRoomRequest", ++requestId, DormArgs()));
                AssertClosed();
            }

            public void RejectInvalidLength(int length = 0)
            {
                byte[] header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, length);
                socket.GetStream().Write(header);
                AssertClosed();
            }

            public JObject Call(string name, object body, bool fragment = false)
            {
                int id = ++requestId;
                byte[] frame = RequestFrame(name, id, body);
                if (fragment)
                {
                    // Split both the little-endian length prefix and plaintext body across writes.
                    foreach (byte value in frame)
                        socket.GetStream().WriteByte(value);
                }
                else
                    socket.GetStream().Write(frame);
                while (true)
                {
                    Packet packet = Read();
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Remember(packet);
                        continue;
                    }
                    GuildAssert(packet.Type == Packet.ContentType.Response, $"Dorm {name}: response envelope");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    GuildAssert(response.Id == id && response.Name == name.Replace("Request", "Response"), $"Dorm {name}: response correlation");
                    return JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                }
            }

            public void Send(string name, object body)
            {
                socket.GetStream().Write(Frame(new Packet
                {
                    No = 0,
                    Type = Packet.ContentType.Push,
                    Content = MessagePackSerializer.Serialize(new Packet.Push
                    {
                        Name = name,
                        Content = MessagePackSerializer.Serialize(body, PlainOptions)
                    }, PlainOptions)
                }));
            }

            public bool HasPush(string name, Func<JObject, bool> predicate) =>
                pushes.Any(value => value.Name == name && predicate(value.Body));

            public JObject Push(string name, Func<JObject, bool> predicate)
            {
                while (true)
                {
                    int index = pushes.FindIndex(value => value.Name == name && predicate(value.Body));
                    if (index >= 0)
                    {
                        JObject result = pushes[index].Body;
                        pushes.RemoveAt(index);
                        return result;
                    }
                    Packet packet = Read();
                    GuildAssert(packet.Type == Packet.ContentType.Push, $"Dorm waiting for {name}: push envelope");
                    Remember(packet);
                }
            }

            public void AssertNoPush(string name, Func<JObject, bool> predicate)
            {
                // A response on this same stream is the barrier after earlier broadcasts.
                GuildAssert(Call("GuildDormHeartbeatRequest", DormArgs()).Value<int>("Code") == 0, "Dorm isolation heartbeat");
                GuildAssert(!pushes.Any(value => value.Name == name && predicate(value.Body)), $"Dorm isolated from {name}");
            }

            public void AssertClosed(TimeSpan? deadline = null)
            {
                using CancellationTokenSource timeout = new(deadline ?? TimeSpan.FromSeconds(5));
                while (true)
                {
                    Packet? packet = ReadAsync(timeout.Token).GetAwaiter().GetResult();
                    if (packet == null) return;
                    GuildAssert(packet.Type == Packet.ContentType.Push, "Dorm closed connection cannot process another request");
                    Remember(packet);
                }
            }

            private Packet Read()
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                return ReadAsync(timeout.Token).GetAwaiter().GetResult()
                    ?? throw new InvalidDataException("Dorm connection closed before expected packet.");
            }

            private static byte[] RequestFrame(string name, int id, object body) => Frame(new Packet
            {
                No = 0,
                Type = Packet.ContentType.Request,
                Content = MessagePackSerializer.Serialize(new Packet.Request
                {
                    Id = id,
                    Name = name,
                    Content = MessagePackSerializer.Serialize(body, PlainOptions)
                }, PlainOptions)
            });

            // Native Dorm uses ordinary MessagePack, not the main-session Haru/LZ4 codec.
            private static byte[] Frame(Packet packet)
            {
                byte[] body = MessagePackSerializer.Serialize(packet, PlainOptions);
                byte[] frame = new byte[sizeof(int) + body.Length];
                BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
                body.CopyTo(frame, sizeof(int));
                return frame;
            }

            private async Task<Packet?> ReadAsync(CancellationToken cancellationToken)
            {
                NetworkStream stream = socket.GetStream();
                byte[] header = new byte[sizeof(int)];
                if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0) return null;
                await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                GuildAssert(length > 0 && length <= 1 << 22, "Dorm response has a bounded little-endian body length");
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken);
                // Compression.None rejects a top-level LZ4 extension rather than accepting it.
                return MessagePackSerializer.Deserialize<Packet>(body, PlainOptions);
            }

            private void Remember(Packet packet)
            {
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
            }

            public void Dispose() => socket.Dispose();
        }
    }
}
