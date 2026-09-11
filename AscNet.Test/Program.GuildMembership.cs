using System.Globalization;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guild;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateGuildMembershipCompatibility()
    {
        using GuildTestScope scope = new();
        var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(row => row.Key, row => row.Value);
        var create = TableReaderV2.Parse<GuildCreateTable>().First();
        var level = TableReaderV2.Parse<GuildLevelTable>().OrderBy(row => row.Level).First();
        var portrait = TableReaderV2.Parse<GuildHeadPortraitTable>().First(row => !(row.ConditionId > 0) && !(row.Cost > 0));
        var costs = create.ItemId.Select((id, index) => (Id: id, Count: (long)create.ItemNum[index]))
            .GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildModule");
        MethodInfo prepare = RequiredMethod(module, "PrepareLogin", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Session)]);
        MethodInfo login = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Session)]);
        FieldInfo identityReady = typeof(Session).GetField("GuildIdentityReady", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(Session).FullName, "GuildIdentityReady");
        long Uid(LoopbackSessionHarness player) => player.Session.player.PlayerData.Id;
        Guild Own(LoopbackSessionHarness player) => Guild.FindByMember(Uid(player)) ?? throw new InvalidDataException("Expected persisted membership.");
        JObject Rpc(LoopbackSessionHarness player, string name, object? request = null) => GuildRpc(player, name + "Request",
            request is null ? new Dictionary<string, object>() : request.GetType().GetProperties().ToDictionary(property => property.Name, property => property.GetValue(request)!));
        Guild Fresh(Guild guild) => Guild.FindById(guild.Id) ?? throw new InvalidDataException("Expected persisted guild.");
        void Ok(JObject response, string scenario) => GuildAssert(response.Value<int?>("Code") == 0, scenario + ": " + response);
        void Denied(JObject response, string scenario) => GuildAssert(response.Value<int?>("Code") is int code && code != 0, scenario + ": " + response);
        JObject Apply(LoopbackSessionHarness player, Guild guild) => Rpc(player, "GuildApply", new { GuildId = guild.Id });
        JObject Ack(LoopbackSessionHarness actor, LoopbackSessionHarness target, bool agree = true) => Rpc(actor, "GuildAckApply", new { PlayId = Uid(target), IsAgree = agree });
        JObject Rank(LoopbackSessionHarness actor, LoopbackSessionHarness target, int rank) => Rpc(actor, "GuildChangeRank", new { PlayerId = Uid(target), NewRank = rank });
        JObject Create(LoopbackSessionHarness player, string name) => Rpc(player, "GuildCreate", new { GuildName = name, GuildDeclaration = "Membership verification", IconId = portrait.Id });
        string Name() => "G" + Guid.NewGuid().ToString("N")[..12];
        Dictionary<int, long> Balances(LoopbackSessionHarness player)
        {
            long uid = Uid(player);
            return Inventory.collection.Find(row => row.Uid == uid).Single().Items
                .GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        }
        void SameBalances(Dictionary<int, long> before, LoopbackSessionHarness player, string scenario)
        {
            var after = Balances(player);
            GuildAssert(before.Keys.Union(after.Keys).All(id => before.GetValueOrDefault(id) == after.GetValueOrDefault(id)), scenario);
        }
        void Reload(LoopbackSessionHarness player)
        {
            long uid = Uid(player);
            player.Session.player = Player.collection.Find(row => row.PlayerData.Id == uid).Single();
            player.Session.inventory = Inventory.collection.Find(row => row.Uid == uid).Single();
            player.Session.character = Character.collection.Find(row => row.Uid == uid).Single();
        }
        NotifyGuildData Identity(LoopbackSessionHarness player) => (NotifyGuildData)login.Invoke(null, [player.Session])!;
        void SilentBoundary(LoopbackSessionHarness player, string scenario)
        {
            int boundaryId = Interlocked.Increment(ref guildPacketId);
            InvokeRegisteredRequestHandler("GuildFindRequest", player.Session, boundaryId,
                new Dictionary<string, object> { ["GuildId"] = 0 });
            Packet boundary = player.ReadPacket(scenario);
            // Read exactly the next packet: unexpected pushes are failures, never skipped.
            var response = ReadResponsePayload<AscNet.GameServer.Handlers.GuildFindResponse>(boundary, "GuildFindResponse");
            GuildAssert(MessagePack.MessagePackSerializer.Deserialize<Packet.Response>(boundary.Content).Id == boundaryId && response.Code == 0, scenario);
        }
        void Recover(LoopbackSessionHarness player)
        {
            // The writer is asynchronous: a nonblocking read cannot drain already-enqueued pushes.
            // GuildFind(0) only sends its response, bounding earlier cross-player traffic in FIFO order.
            Ok(Rpc(player, "GuildFind", new { GuildId = 0 }), "pre-recovery stream boundary");
            Reload(player);
            // Match AccountModule's login preparation phase, not recovery on an already-ready socket.
            identityReady.SetValue(player.Session, false);
            prepare.Invoke(null, [player.Session]);
            SilentBoundary(player, "pre-identity login preparation must remain silent");
            player.Session.SendPush(Identity(player));
            identityReady.SetValue(player.Session, true);
            NotifyGuildData delivered = ReadPushPayload<NotifyGuildData>(player, "NotifyGuildData", "prepared login guild identity");
            Guild? persisted = Guild.FindByMember(Uid(player));
            int rank = persisted is null ? 9 : persisted.LeaderId == Uid(player) ? 1 : persisted.Members[Uid(player)].Rank;
            GuildAssert(delivered.GuildId == (persisted?.Id ?? 0) && delivered.GuildName == (persisted?.Name ?? "")
                && delivered.GuildLevel == (persisted?.Level ?? 0) && delivered.GuildRankLevel == rank,
                "login publishes authoritative persisted membership after silent preparation");
            SilentBoundary(player, "login preparation must not queue duplicate identity or reward pushes");
        }
        void Option(LoopbackSessionHarness leader, int option, int minLevel = 1) => Ok(Rpc(leader, "GuildChangeApplyOption", new { Option = option, MinLevel = minLevel }), "set admission policy");
        JObject[] Race(Func<JObject> first, Func<JObject> second)
        {
            using Barrier start = new(2);
            Task<JObject> Run(Func<JObject> action) => Task.Run(() => { start.SignalAndWait(); return action(); });
            var a = Run(first);
            var b = Run(second);
            Task.WaitAll(a, b);
            return [a.Result, b.Result];
        }

        var founder = scope.CreatePlayer();
        var applicant = scope.CreatePlayer();
        var outsider = scope.CreatePlayer();
        Recover(founder);
        var empty = Identity(founder);
        GuildAssert(empty.GuildId == 0 && empty.GuildName == "" && empty.GuildLevel == 0 && empty.IconId == 0 && empty.GuildRankLevel == 9,
            "fresh player has exact no-guild identity, not an automatic AscNet membership");
        Denied(Rpc(founder, "GuildListDetail", new { GuildId = 0 }), "no own detail without membership");
        Denied(Rpc(founder, "GuildQuit"), "no-guild quit");
        Denied(Rpc(founder, "GuildListApply"), "no-guild applications");
        var unpaid = Balances(founder);
        Denied(Create(founder, ""), "empty creation name");
        SameBalances(unpaid, founder, "invalid create cannot consume currency");
        string name = Name();
        Ok(Create(founder, name), "create real guild");
        Guild guild = Own(founder);
        GuildAssert(guild.Name == name && guild.LeaderId == Uid(founder) && guild.MemberIds.SequenceEqual([Uid(founder)]), "creation persists only requested founder");
        var poor = scope.CreatePlayer();
        var requiredCost = costs.First(row => row.Value > 0);
        poor.Session.inventory.Items.Single(row => row.Id == requiredCost.Key).Count = requiredCost.Value - 1;
        long poorUid = Uid(poor);
        Inventory.collection.ReplaceOne(row => row.Uid == poorUid, poor.Session.inventory);
        var poorBalance = Balances(poor);
        Denied(Create(poor, Name()), "one short of configured creation currency");
        GuildAssert(Guild.FindByMember(Uid(poor)) is null, "unfunded create cannot activate membership");
        SameBalances(poorBalance, poor, "insufficient creation currency cannot partially debit other costs");
        var paid = Balances(founder);
        foreach (var cost in costs)
            GuildAssert(unpaid.GetValueOrDefault(cost.Key) - paid.GetValueOrDefault(cost.Key) == cost.Value, "creation debits configured cost once");
        _ = Create(founder, name);
        SameBalances(paid, founder, "duplicate create does not charge twice");
        Denied(Create(outsider, name), "duplicate guild name");
        GuildAssert(Guild.FindByMember(Uid(outsider)) is null, "duplicate-name failure cannot assign outsider");
        Recover(founder);
        GuildAssert(Identity(founder).GuildId == guild.Id && Identity(founder).GuildRankLevel == 1, "founder identity survives reload");

        Option(founder, 2);
        var pending = Apply(applicant, guild);
        Ok(pending, "application");
        GuildAssert(pending.Value<bool?>("IsPass") == false && Guild.FindByMember(Uid(applicant)) is null, "application is not admission");
        _ = Apply(applicant, guild);
        GuildAssert(Fresh(guild).Applications.Count(row => row.PlayerId == Uid(applicant)) == 1, "duplicate application has one persisted row");
        JObject applications = Rpc(founder, "GuildListApply");
        Ok(applications, "officer application list");
        GuildAssert(applications["Data"] is JArray rows && rows.Any(row => row.Value<long?>("PlayerId") == Uid(applicant)), "officer sees actual applicant");
        Denied(Ack(outsider, applicant), "outsider cannot approve another guild applicant");
        GuildAssert(Guild.FindByMember(Uid(applicant)) is null, "unauthorized approval changes no membership");
        Ok(Ack(founder, applicant), "approve applicant");
        GuildAssert(Own(applicant).Id == guild.Id && !Fresh(guild).Applications.Any(row => row.PlayerId == Uid(applicant)), "admission atomically consumes application");
        Recover(applicant);
        GuildAssert(Identity(applicant).GuildRankLevel == 4 && Identity(outsider).GuildId == 0, "member recovery cannot leak identity cross-player");
        var refused = scope.CreatePlayer();
        Ok(Apply(refused, guild), "refusal candidate applies");
        Denied(Ack(applicant, refused), "ordinary member cannot approve");
        Ok(Ack(founder, refused, false), "officer refuses application");
        GuildAssert(Guild.FindByMember(Uid(refused)) is null && !Fresh(guild).Applications.Any(row => row.PlayerId == Uid(refused)), "refusal removes pending only");
        Ok(Apply(refused, guild), "expired candidate applies");
        long timeout = long.Parse(config["GuildApplyTimeoutSec"], CultureInfo.InvariantCulture);
        Guild.collection.UpdateOne(row => row.Id == guild.Id, Builders<Guild>.Update.Set(row => row.Applications,
            new List<GuildApplication> { new() { PlayerId = Uid(refused), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timeout - 1 } }));
        Denied(Ack(founder, refused), "expired application cannot be admitted");
        GuildAssert(Guild.FindByMember(Uid(refused)) is null, "expiry preserves no membership");

        Option(founder, 3);
        Denied(Apply(outsider, guild), "closed guild rejects direct entry");
        Option(founder, 1, 100);
        outsider.Session.player.PlayerData.Level = 99;
        outsider.Session.player.SaveChecked();
        Denied(Apply(outsider, guild), "one level below admission minimum");
        outsider.Session.player.PlayerData.Level = 100;
        outsider.Session.player.SaveChecked();
        var joined = Apply(outsider, guild);
        Ok(joined, "minimum-level boundary direct entry");
        GuildAssert(joined.Value<bool?>("IsPass") == true && Own(outsider).Id == guild.Id, "direct entry persists real membership");

        Denied(Rank(applicant, outsider, 2), "member cannot promote officer");
        Denied(Rank(founder, founder, 2), "leader cannot rank-change self");
        Denied(Rank(founder, outsider, 9), "invalid destination rank");
        Ok(Rank(founder, applicant, 2), "promote co-leader");
        Denied(Rank(applicant, founder, 4), "co-leader cannot demote leader");
        var recruit = scope.CreatePlayer();
        Denied(Rpc(outsider, "GuildRecruit", new { PlayId = Uid(recruit) }), "member cannot recruit");
        Ok(Rpc(applicant, "GuildRecruit", new { PlayId = Uid(recruit) }), "co-leader recruits real player");
        JObject invitations = Rpc(recruit, "GuildListRecruit");
        Ok(invitations, "invitation inbox");
        GuildAssert(invitations["Data"] is JArray invites && invites.Any(row => row.Value<uint?>("GuildId") == guild.Id), "inbox identifies actual inviting guild");
        Ok(Rpc(recruit, "GuildAckRecruit", new { GuildId = guild.Id, IsAgree = false }), "decline invitation");
        GuildAssert(Guild.FindByMember(Uid(recruit)) is null, "declining does not join");
        Denied(Rpc(recruit, "GuildAckRecruit", new { GuildId = guild.Id, IsAgree = true }), "consumed invitation cannot admit");
        Ok(Rpc(applicant, "GuildRecruit", new { PlayId = Uid(recruit) }), "invite after decline");
        Ok(Rpc(recruit, "GuildAckRecruit", new { GuildId = guild.Id, IsAgree = true }), "accept invitation");
        GuildAssert(Own(recruit).Id == guild.Id, "invitation acceptance persists target membership");
        Denied(Rpc(applicant, "GuildRecruit", new { PlayId = Uid(recruit) }), "already joined target cannot be recruited");

        var visitor = scope.CreatePlayer();
        Ok(Rpc(visitor, "GuildTourist", new { GuildId = guild.Id }), "tourist entry");
        Recover(visitor);
        GuildAssert(Identity(visitor).GuildId == guild.Id && Identity(visitor).GuildRankLevel == 5, "tourist persisted identity");
        Denied(Rpc(visitor, "GuildListApply"), "tourist cannot view applications");
        Denied(Rpc(visitor, "GuildGetContributeReward"), "tourist cannot claim member welfare");
        Ok(Rpc(visitor, "GuildQuitTourist"), "tourist exits");
        Recover(visitor);
        GuildAssert(Identity(visitor).GuildId == 0 && visitor.Session.player.GuildState.JoinCdEnd == 0, "tourist exit has no invented cooldown");
        Ok(Apply(visitor, guild), "former tourist can join immediately");
        Ok(Rpc(founder, "GuildKickMember", new { OtherId = Uid(visitor) }), "leader kicks lower rank");
        Recover(visitor);
        GuildAssert(Guild.FindByMember(Uid(visitor)) is null && visitor.Session.player.GuildState.JoinCdEnd == 0, "kick removes membership without invented cooldown");
        Denied(Rpc(applicant, "GuildKickMember", new { OtherId = Uid(founder) }), "co-leader cannot kick leader");
        Denied(Rpc(founder, "GuildQuit"), "leader cannot abandon remaining full members");
        Ok(Rank(founder, applicant, 1), "leadership transfer");
        Recover(founder);
        Recover(applicant);
        GuildAssert(Fresh(guild).LeaderId == Uid(applicant) && Identity(founder).GuildRankLevel == 4 && Identity(applicant).GuildRankLevel == 1,
            "transfer atomically changes leader and both ranks");
        long leaveBefore = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Ok(Rpc(founder, "GuildQuit"), "ordinary member quits");
        Recover(founder);
        long cooldown = long.Parse(config["GuildEnterCd"], CultureInfo.InvariantCulture) * 3600;
        long deadline = founder.Session.player.GuildState.JoinCdEnd;
        GuildAssert(deadline >= leaveBefore + cooldown && deadline <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cooldown, "quit persists configured eight-hour cooldown");
        GuildAssert(Rpc(founder, "GuildListRecommend", new { PageNo = 1 }).Value<long?>("JoinCdEnd") == deadline, "recommendation returns persisted cooldown");
        Denied(Apply(founder, guild), "quit cooldown blocks join");
        Ok(Rpc(applicant, "GuildRecruit", new { PlayId = Uid(founder) }), "invite eligible former member during join cooldown");
        Denied(Rpc(founder, "GuildAckRecruit", new { GuildId = guild.Id, IsAgree = true }), "quit cooldown blocks invitation acceptance");
        GuildAssert(Guild.FindByMember(Uid(founder)) is null, "invitation cannot bypass join cooldown");
        var leaveBalance = Balances(founder);
        Denied(Create(founder, Name()), "quit cooldown blocks paid create");
        SameBalances(leaveBalance, founder, "cooldown create does not charge");
        founder.Session.player.GuildState.JoinCdEnd = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        founder.Session.player.SaveChecked();
        Ok(Apply(founder, guild), "expired cooldown permits join");

        // Capacity uses actual persisted fixture players, never made-up member IDs.
        var fullPlayers = Enumerable.Range(0, level.Capacity).Select(_ => scope.CreatePlayer()).ToArray();
        Guild full = scope.SeedGuild(fullPlayers);
        Option(fullPlayers[0], 1);
        var overflow = scope.CreatePlayer();
        Denied(Apply(overflow, full), "full member capacity rejects next player");
        GuildAssert(Fresh(full).MemberIds.Count == level.Capacity && Guild.FindByMember(Uid(overflow)) is null, "capacity rejection preserves both sides");
        Ok(Rpc(fullPlayers[^1], "GuildQuit"), "vacate final member slot");
        Ok(Apply(overflow, full), "last free member slot accepts entry");
        var touristPlayers = Enumerable.Range(0, level.PositionNum[2]).Select(_ => scope.CreatePlayer()).ToArray();
        foreach (var tourist in touristPlayers)
            Ok(Rpc(tourist, "GuildTourist", new { GuildId = full.Id }), "tourist capacity independent of full members");
        var touristOverflow = scope.CreatePlayer();
        Denied(Rpc(touristOverflow, "GuildTourist", new { GuildId = full.Id }), "tourist capacity rejects next player");
        GuildAssert(Guild.FindByMember(Uid(touristOverflow)) is null, "tourist capacity failure does not assign membership");
        int coLeaderSlots = level.PositionNum[0];
        foreach (var target in fullPlayers.Skip(1).Take(coLeaderSlots))
            Ok(Rank(fullPlayers[0], target, 2), "configured co-leader slot");
        Denied(Rank(fullPlayers[0], overflow, 2), "co-leader position capacity");
        Recover(overflow);
        GuildAssert(Identity(overflow).GuildRankLevel == 4, "position rejection preserves member rank");

        var kickLeader = scope.CreatePlayer();
        int kickLimit = int.Parse(config["GuildKickCountDailyMax"], CultureInfo.InvariantCulture);
        var kickTargets = Enumerable.Range(0, kickLimit + 1).Select(_ => scope.CreatePlayer()).ToArray();
        Guild kickGuild = scope.SeedGuild(new[] { kickLeader }.Concat(kickTargets).ToArray());
        foreach (var target in kickTargets.Take(kickLimit))
            Ok(Rpc(kickLeader, "GuildKickMember", new { OtherId = Uid(target) }), "daily kick quota available");
        Denied(Rpc(kickLeader, "GuildKickMember", new { OtherId = Uid(kickTargets[^1]) }), "daily kick quota exhausted");
        GuildAssert(Own(kickTargets[^1]).Id == kickGuild.Id, "daily kick limit preserves target membership");

        var solo = scope.CreatePlayer();
        Guild soloGuild = scope.SeedGuild(solo);
        var soloTourist = scope.CreatePlayer();
        Ok(Rpc(soloTourist, "GuildTourist", new { GuildId = soloGuild.Id }), "tourist before disband");
        Ok(Rpc(solo, "GuildQuit"), "sole full-member leader disbands through quit");
        Recover(solo);
        Recover(soloTourist);
        GuildAssert(Guild.FindByMember(Uid(solo)) is null && Guild.FindByMember(Uid(soloTourist)) is null && Guild.FindById(soloGuild.Id)?.Active != true,
            "disband clears full member and tourist authority");
        GuildAssert(solo.Session.player.GuildState.JoinCdEnd > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && soloTourist.Session.player.GuildState.JoinCdEnd == 0,
            "disband cooldown belongs to leaving leader, not tourist");

        var concurrent = scope.CreatePlayer();
        var concurrentSocket = scope.OpenPlayer(Uid(concurrent));
        var beforeRace = Balances(concurrent);
        string raceName = Name();
        JObject[] duplicate = Race(() => Create(concurrent, raceName), () => Create(concurrentSocket, raceName));
        GuildAssert(duplicate.Any(row => row.Value<int?>("Code") == 0), "concurrent duplicate create has a successful caller");
        long concurrentUid = Uid(concurrent);
        GuildAssert(Guild.collection.CountDocuments(row => row.Active && row.MemberIds.Contains(concurrentUid)) == 1, "concurrent create has one authoritative membership");
        var afterRace = Balances(concurrent);
        foreach (var cost in costs)
            GuildAssert(beforeRace.GetValueOrDefault(cost.Key) - afterRace.GetValueOrDefault(cost.Key) == cost.Value, "concurrent duplicate create charges once");
        Guild interrupted = Own(concurrent);
        Guild.collection.UpdateOne(row => row.Id == interrupted.Id, Builders<Guild>.Update.Set(row => row.Active, false).Set(row => row.CreationReserved, true));
        Recover(concurrent);
        GuildAssert(Own(concurrent).Id == interrupted.Id, "paid inactive creation resumes on login");
        SameBalances(afterRace, concurrent, "paid creation recovery cannot charge again");
        Recover(concurrent);
        SameBalances(afterRace, concurrent, "repeated recovery cannot replay costs");

        var otherLeader = scope.CreatePlayer();
        Guild other = scope.SeedGuild(otherLeader);
        Option(otherLeader, 1);
        Option(applicant, 1);
        var competing = scope.CreatePlayer();
        var competingSocket = scope.OpenPlayer(Uid(competing));
        var competingBalance = Balances(competing);
        JObject[] competition = Race(() => Apply(competing, guild), () => Apply(competingSocket, other));
        GuildAssert(competition.Count(row => row.Value<int?>("Code") == 0 && row.Value<bool?>("IsPass") == true) == 1, "competing guild admissions have exactly one winner");
        long competingUid = Uid(competing);
        GuildAssert(Guild.collection.CountDocuments(row => row.Active && row.MemberIds.Contains(competingUid)) == 1, "competing admissions preserve unique membership");
        SameBalances(competingBalance, competing, "competing free admissions do not charge create costs");
        var createJoin = scope.CreatePlayer();
        var createJoinSocket = scope.OpenPlayer(Uid(createJoin));
        var beforeCompetingCost = Balances(createJoin);
        _ = Race(() => Create(createJoin, Name()), () => Apply(createJoinSocket, other));
        Guild winner = Own(createJoin);
        long createJoinUid = Uid(createJoin);
        GuildAssert(Guild.collection.CountDocuments(row => row.Active && row.MemberIds.Contains(createJoinUid)) == 1, "competing paid create/free join has one winner");
        var afterCompetingCost = Balances(createJoin);
        foreach (var cost in costs)
            GuildAssert(beforeCompetingCost.GetValueOrDefault(cost.Key) - afterCompetingCost.GetValueOrDefault(cost.Key) == (winner.Id == other.Id ? 0 : cost.Value),
                "only winning paid creation debits its configured costs");

        int wishItem = TableReaderV2.Parse<AscNet.Table.V2.share.trust.CharacterTrustItemTable>().First(row => row.FavorCharacterId.Count > 0).Id;
        foreach (bool donorFirst in new[] { true, false })
        {
            var pair = new[] { scope.CreatePlayer(), scope.CreatePlayer() }.OrderBy(Uid).ToArray();
            var donor = donorFirst ? pair[0] : pair[1];
            var recipient = donorFirst ? pair[1] : pair[0];
            Guild recoveringGuild = scope.SeedGuild(pair);
            donor.Session.inventory.Items.RemoveAll(row => row.Id == wishItem);
            donor.Session.inventory.Items.Add(new Item { Id = wishItem, Count = 5 });
            long donorUid = Uid(donor);
            Inventory.collection.ReplaceOne(row => row.Uid == donorUid, donor.Session.inventory);
            Ok(Rpc(recipient, "GuildReleaseWish", new { ItemId = wishItem }), "publish real wish for cross-player recovery");
            var wish = Fresh(recoveringGuild).Wishes.Single(row => row.PlayerId == Uid(recipient));
            var donorBefore = Balances(donor);
            var recipientBefore = Balances(recipient);
            int donorCountBefore = Player.collection.Find(row => row.PlayerData.Id == donorUid).Single().GuildState.WishContributeCount;
            int requestId = Interlocked.Increment(ref guildPacketId);
            var request = new Dictionary<string, object> { ["PlayerId"] = Uid(recipient), ["Seq"] = wish.Seq, ["ItemId"] = wishItem };
            var inventoryId = pair[1].Session.inventory.Id;
            GuildPendingOperation frozen;
            // A stale online document identity passes participant existence checks but its real
            // SaveChecked replace must fail. This interrupts after the first participant commits,
            // without deleting data, mocking Mongo, or hand-authoring a pending outcome.
            pair[1].Session.inventory.Id = MongoDB.Bson.ObjectId.GenerateNewId();
            try
            {
                Denied(GuildRpc(donor, "GuildWishContributeRequest", request, requestId), "second participant persistence failure is not success");
                frozen = Fresh(recoveringGuild).PendingOperation
                    ?? throw new InvalidDataException("Cross-player failure must retain its actual frozen operation.");
                GuildAssert(frozen.Players.Select(row => row.Uid).Order().SequenceEqual(pair.Select(Uid)), "pending operation contains both actual players");
                var committed = Balances(pair[0]);
                var before = donorFirst ? donorBefore : recipientBefore;
                GuildAssert(committed.GetValueOrDefault(wishItem) - before.GetValueOrDefault(wishItem) == (donorFirst ? -1 : 1),
                    donorFirst ? "failure occurs after durable donor cost" : "failure occurs after durable recipient reward");
                GuildAssert(Fresh(recoveringGuild).Wishes.Single(row => row.Seq == wish.Seq).GotCount == 0,
                    "guild outcome stays unfinalized while a participant remains pending");
            }
            finally
            {
                pair[1].Session.inventory.Id = inventoryId;
            }
            Reload(donor);
            Reload(recipient);
            Recover(recipient); // Any participant login must resume the original actor's operation.
            Guild resolved = Fresh(recoveringGuild);
            GuildAssert(resolved.PendingOperation is null, "participant login finalizes cross-player pending operation");
            var resolvedWish = resolved.Wishes.Single(row => row.Seq == wish.Seq);
            GuildAssert(resolvedWish.GotCount == 1 && resolvedWish.Donors.SequenceEqual([Uid(donor)]), "recovery finalizes one donation");
            GuildAssert(Balances(donor).GetValueOrDefault(wishItem) == donorBefore.GetValueOrDefault(wishItem) - 1
                && Balances(recipient).GetValueOrDefault(wishItem) == recipientBefore.GetValueOrDefault(wishItem) + 1,
                "recovery settles both sides once regardless of cost/reward commit order");
            GuildAssert(Balances(donor).GetValueOrDefault(39) == donorBefore.GetValueOrDefault(39)
                + int.Parse(config["GuildWishContributeAddCoin"], CultureInfo.InvariantCulture),
                "recovery grants configured donor currency exactly once");
            GuildAssert(Player.collection.Find(row => row.PlayerData.Id == donorUid).Single().GuildState.WishContributeCount == donorCountBefore + 1,
                "recovered donation consumes one daily count");
            foreach (var participant in pair)
            {
                long participantUid = Uid(participant);
                GuildAssert(Player.collection.Find(row => row.PlayerData.Id == participantUid).Single().GuildState.AppliedOperationIds.Contains(frozen.Id),
                    "both participants durably record completed operation");
            }
            var finalDonor = Balances(donor);
            var finalRecipient = Balances(recipient);
            JObject replay = GuildRpc(donor, "GuildWishContributeRequest", request, requestId);
            Ok(replay, "matching failed request retries frozen success");
            GuildAssert(JToken.DeepEquals(replay, JObject.Parse(MessagePack.MessagePackSerializer.ConvertToJson(frozen.ResponseBody))),
                "retry returns original frozen response rather than rerunning donation");
            Recover(donor);
            Recover(recipient);
            SameBalances(finalDonor, donor, "retry/repeated login cannot duplicate donor cost or reward");
            SameBalances(finalRecipient, recipient, "retry/repeated login cannot duplicate recipient reward");
            GuildAssert(Fresh(recoveringGuild).Wishes.Single(row => row.Seq == wish.Seq).GotCount == 1, "retry cannot increment donation progress again");
        }

        var repairLeader = scope.CreatePlayer();
        var repairMember = scope.CreatePlayer();
        var deletedMember = scope.CreatePlayer();
        Guild damaged = scope.SeedGuild(repairLeader, repairMember, deletedMember);
        long deletedUid = Uid(deletedMember);
        Server.Instance.Sessions.TryRemove(deletedMember.Session.id, out _);
        Player.collection.DeleteOne(row => row.PlayerData.Id == deletedUid);
        GuildAssert(!Player.collection.Find(row => row.PlayerData.Id == deletedUid).Any(), "dangling membership fixture has no player document");
        Recover(repairLeader);
        Recover(repairMember);
        GuildAssert(Identity(repairLeader).GuildId == damaged.Id && Identity(repairMember).GuildId == damaged.Id,
            "dangling peer does not prevent real member login");
        Ok(Rpc(repairMember, "GuildReleaseWish", new { ItemId = wishItem }), "real member can mutate economy despite dangling peer");
        GuildAssert(Fresh(damaged).Wishes.Any(row => row.PlayerId == Uid(repairMember) && row.ItemId == wishItem),
            "valid member mutation persists despite missing peer player");
        GuildAssert(!Player.collection.Find(row => row.PlayerData.Id == deletedUid).Any(),
            "login and real member mutation never fabricate a missing peer player");
        Ok(Rpc(repairMember, "GuildListDetail", new { GuildId = damaged.Id }), "dangling peer does not strand member detail");
        Ok(Rpc(repairMember, "GuildQuit"), "dangling peer does not strand legitimate member quit");
        GuildAssert(Guild.FindByMember(Uid(repairMember)) is null && Fresh(damaged).MemberIds.Contains(deletedUid),
            "valid quit removes only the real quitter, leaving stale membership for explicit repair");
        Ok(Rpc(repairLeader, "GuildKickMember", new { OtherId = deletedUid }), "administrator repairs dangling member");
        GuildAssert(!Fresh(damaged).MemberIds.Contains(deletedUid) && Own(repairLeader).Id == damaged.Id
            && !Player.collection.Find(row => row.PlayerData.Id == deletedUid).Any(),
            "repair removes stale membership without deleting real members or recreating missing player");

        var cleanupLeader = scope.CreatePlayer();
        var cleanupVictim = scope.CreatePlayer();
        Guild cleanupGuild = scope.SeedGuild(cleanupLeader, cleanupVictim);
        Option(cleanupLeader, 2);
        identityReady.SetValue(cleanupVictim.Session, true);
        Ok(Rpc(cleanupVictim, "GuildFind", new { GuildId = 0 }), "pre-kick victim stream boundary");
        IMongoCollection<Guild> realGuildCollection = Guild.collection;
        IMongoCollection<Guild> forwardingCollection = DispatchProxy.Create<IMongoCollection<Guild>, GuildCleanupFailureCollection>();
        var cleanupFault = (GuildCleanupFailureCollection)(object)forwardingCollection;
        cleanupFault.Inner = realGuildCollection;
        cleanupFault.TargetUid = Uid(cleanupVictim);
        FieldInfo guildCollectionField = typeof(Guild).GetField("collection", BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingFieldException(typeof(Guild).FullName, "collection");
        MethodInfo setCollection = typeof(MongoCollectionOverride).GetMethod("SetStaticReadonlyField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MongoCollectionOverride).FullName, "SetStaticReadonlyField");
        setCollection.Invoke(null, [guildCollectionField, forwardingCollection]);
        try
        {
            Denied(Rpc(cleanupLeader, "GuildKickMember", new { OtherId = Uid(cleanupVictim) }),
                "post-commit cleanup acknowledgement failure is reported");
            Guild interruptedCleanup = Fresh(cleanupGuild);
            GuildAssert(!interruptedCleanup.MemberIds.Contains(Uid(cleanupVictim)) && interruptedCleanup.PendingOperation is null,
                "kick outcome was finalized before cleanup acknowledgement failed");
            GuildAssert(cleanupFault.Triggered && interruptedCleanup.PendingMembershipChanges.Contains(Uid(cleanupVictim)),
                "failed real cleanup acknowledgement retains durable membership notification marker");
            NotifyGuildData beforeAcknowledgement = ReadPushPayload<NotifyGuildData>(
                cleanupVictim, "NotifyGuildData", "identity published before failed acknowledgement");
            GuildAssert(beforeAcknowledgement.GuildId == 0 && beforeAcknowledgement.GuildRankLevel == 9,
                "cleanup publishes no-guild identity before attempting acknowledgement");
            SilentBoundary(cleanupVictim, "failed acknowledgement must not publish unrelated pushes");
        }
        finally
        {
            setCollection.Invoke(null, [guildCollectionField, realGuildCollection]);
        }
        int cleanupRequestId = Interlocked.Increment(ref guildPacketId);
        InvokeRegisteredRequestHandler("GuildQuitRequest", cleanupVictim.Session, cleanupRequestId, new Dictionary<string, object>());
        NotifyGuildData cleared = ReadPushPayload<NotifyGuildData>(
            cleanupVictim, "NotifyGuildData", "recovered online membership identity");
        GuildAssert(cleared.GuildId == 0 && cleared.GuildName == "" && cleared.GuildRankLevel == 9,
            "victim's next authenticated request publishes no-guild identity before its response");
        var cleanupResponse = ReadResponsePayload<AscNet.GameServer.Handlers.GuildQuitResponse>(
            cleanupVictim.ReadPacket("post-recovery no-membership quit response"), "GuildQuitResponse");
        GuildAssert(cleanupResponse.Code != 0, "recovered removed member cannot quit a guild it no longer belongs to");
        SilentBoundary(cleanupVictim, "cleanup recovery must not replay ordinary rewards");
        GuildAssert(!Fresh(cleanupGuild).PendingMembershipChanges.Contains(Uid(cleanupVictim)),
            "successful identity publication acknowledges marker");

        // Historical false identity was wire-only. A legitimately persisted guild named AscNet is never a migration target.
        Guild? named = Guild.collection.Find(row => row.Name == "AscNet").FirstOrDefault();
        if (named is null)
        {
            var legitimateLeader = scope.CreatePlayer();
            named = scope.SeedGuild(legitimateLeader);
            Guild.collection.UpdateOne(row => row.Id == named.Id, Builders<Guild>.Update.Set(row => row.Name, "AscNet"));
            named = Fresh(named);
        }
        uint preservedId = named.Id;
        long[] preservedMembers = named.MemberIds.ToArray();
        var cleanLogin = scope.CreatePlayer();
        Recover(cleanLogin);
        GuildAssert(Identity(cleanLogin).GuildId == 0, "fresh login never enrolls in a legitimate AscNet guild");
        Guild? preserved = Guild.FindById(preservedId);
        GuildAssert(preserved is not null && preserved.Name == "AscNet" && preserved.MemberIds.SequenceEqual(preservedMembers) && preserved.Active == named.Active,
            "login preserves legitimate AscNet document without name-based deletion");
        Console.WriteLine("Guild membership compatibility passed.");
    }

    private class GuildCleanupFailureCollection : DispatchProxy
    {
        public IMongoCollection<Guild> Inner { get; set; } = null!;
        public long TargetUid { get; set; }
        public bool Triggered { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (!Triggered && targetMethod.Name == nameof(IMongoCollection<Guild>.UpdateOne)
                && args?.OfType<UpdateDefinition<Guild>>().SingleOrDefault() is { } update)
            {
                var registry = MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry;
                var rendered = update.Render(registry.GetSerializer<Guild>(), registry);
                if (rendered is MongoDB.Bson.BsonDocument document
                    && document.TryGetValue("$pull", out var pull)
                    && pull.AsBsonDocument.TryGetValue("pending_membership_changes", out var uid)
                    && uid.ToInt64() == TargetUid)
                {
                    Triggered = true;
                    throw new IOException("Injected guild cleanup acknowledgement write failure.");
                }
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
