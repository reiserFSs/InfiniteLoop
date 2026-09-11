using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.client.config;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGuildIdentityCompatibility()
    {
        using GuildTestScope scope = new();
        var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(row => row.Key, row => row.Value);
        int Limit(string key) => int.Parse(config[key], System.Globalization.CultureInfo.InvariantCulture);
        string Scalars(int count) => string.Concat(Enumerable.Repeat("\U00020000", count));
        var leader = scope.CreatePlayer();
        var officer = scope.CreatePlayer();
        var member = scope.CreatePlayer();
        var outsider = scope.CreatePlayer();
        long leaderId = leader.Session.player.PlayerData.Id;
        long officerId = officer.Session.player.PlayerData.Id;
        long memberId = member.Session.player.PlayerData.Id;
        Guild guild = scope.SeedGuild(leader, officer, member);
        guild.Members[officerId].Rank = 2;
        Guild.collection.ReplaceOne(row => row.Id == guild.Id, guild);
        Guild Reload() => Guild.collection.Find(row => row.Id == guild.Id).Single();
        void Save(Guild value) => Guild.collection.ReplaceOne(row => row.Id == guild.Id, value);
        long Balance(long uid, int item) => Inventory.collection.Find(row => row.Uid == uid).Single().Items.FirstOrDefault(row => row.Id == item)?.Count ?? 0;
        void Fund(LoopbackSessionHarness player, int item, int count)
        {
            player.Session.inventory.Items.RemoveAll(row => row.Id == item);
            player.Session.inventory.Items.Add(new Item { Id = item, Count = count });
            player.Session.inventory.Save();
        }
        JObject Rpc(LoopbackSessionHarness actor, string name, object request, int code = 0, int? packetId = null)
        {
            JObject response = GuildRpc(actor, name, request, packetId);
            GuildAssert(response.Value<int>("Code") == code, name + " expected " + code + ", got " + response);
            return response;
        }
        JObject Detail(LoopbackSessionHarness actor) => Rpc(actor, "GuildListDetailRequest", new Dictionary<string, object> { ["GuildId"] = 0 });

        Fund(leader, 53, 2);
        string originalName = guild.Name;
        foreach (var invalid in new[] { ("", 20063239), (" ", 20063239), (Scalars(Limit("GuildNameMaxLen") + 1), 20063240), ("<name>", 20063241), ("bad\nname", 20063241) })
        {
            Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = invalid.Item1 }, invalid.Item2);
            GuildAssert(Reload().Name == originalName && Balance(leaderId, 53) == 2, "Rejected rename changed name/card balance");
        }
        Rpc(officer, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = "Officer" }, 20063246);
        Rpc(member, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = "Member" }, 20063246);
        Rpc(outsider, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = "Outside" }, 20063238);
        string paidName = Scalars(Limit("GuildNameMaxLen"));
        int paidPacket = Interlocked.Increment(ref guildPacketId);
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = paidName }, packetId: paidPacket);
        GuildAssert(Reload().Name == paidName && Balance(leaderId, 53) == 1, "Scalar-limit paid rename did not persist/debit exactly one name card");
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = paidName }, packetId: paidPacket);
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = paidName }, 20063242);
        GuildAssert(Balance(leaderId, 53) == 1, "Rename replay/equal-name retry charged twice");
        guild = Reload();
        guild.FreeChangeGuildNameCount = 1;
        Save(guild);
        string freeName = Scalars(Limit("GuildNameMinLen"));
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = freeName });
        GuildAssert(Reload().FreeChangeGuildNameCount == 0 && Balance(leaderId, 53) == 1 && Reload().Name == freeName, "Free rename did not precede paid card consumption");
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = freeName }, 20063242);
        var duplicateGuild = scope.SeedGuild(outsider);
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = duplicateGuild.Name }, 20063244);
        Fund(leader, 53, 0);
        Rpc(leader, "GuildChangeNameRequest", new GuildChangeNameRequest { Name = "Unfunded" }, 20063243);
        GuildAssert(Reload().Name == freeName && Reload().FreeChangeGuildNameCount == 0 && Balance(leaderId, 53) == 0, "Rejected rename modified durable identity");
        var reopenedLeader = scope.OpenPlayer(leaderId);
        GuildAssert(Detail(reopenedLeader).Value<string>("GuildName") == freeName, "Renamed guild lost identity across player reload");

        int noticeLimit = TableReaderV2.Parse<ClientConfigTable>().Single(row => row.Key == "GuildInterComMaxLen").Value;
        foreach (var field in new[] { ("GuildChangeDeclarationRequest", "Delaration", Limit("GuildDeclarationMaxLen")), ("GuildChangeNoticeRequest", "Notice", noticeLimit) })
        {
            string text = Scalars(field.Item3);
            Rpc(officer, field.Item1, new Dictionary<string, object> { [field.Item2] = text });
            Rpc(member, field.Item1, new Dictionary<string, object> { [field.Item2] = "denied" }, 20063056);
            Rpc(officer, field.Item1, new Dictionary<string, object> { [field.Item2] = text + "x" }, 20063052);
            Rpc(leader, field.Item1, new Dictionary<string, object> { [field.Item2] = "<bad>" }, 20063053);
            GuildAssert((field.Item2 == "Notice" ? Reload().Notice : Reload().Declaration) == text, "Rejected text edit overwrote accepted scalar-limit value");
        }
        foreach (int option in new[] { 1, 2, 3 })
        {
            int level = option == 1 ? Limit("PlayerMinLevel") : Limit("PlayerMaxLevel");
            Rpc(officer, "GuildChangeApplyOptionRequest", new GuildChangeApplyOptionRequest { Option = option, MinLevel = level });
            GuildAssert(Reload().Option == option && Reload().MinLevel == level, "Officer application settings did not persist");
        }
        foreach (var invalid in new[] { (0, Limit("PlayerMinLevel")), (4, Limit("PlayerMinLevel")), (2, Limit("PlayerMinLevel") - 1), (2, Limit("PlayerMaxLevel") + 1) })
            Rpc(leader, "GuildChangeApplyOptionRequest", new GuildChangeApplyOptionRequest { Option = invalid.Item1, MinLevel = invalid.Item2 }, 20063217);
        Rpc(member, "GuildChangeApplyOptionRequest", new GuildChangeApplyOptionRequest { Option = 1, MinLevel = 1 }, 20063220);
        GuildAssert(Reload().Option == 3 && Reload().MinLevel == Limit("PlayerMaxLevel"), "Rejected settings request mutated persisted settings");
        guild = Reload();
        guild.Members[memberId].Rank = 3;
        Save(guild);
        Rpc(member, "GuildChangeNoticeRequest", new GuildChangeNoticeRequest { Notice = "elder" }, 20063056);
        Rpc(member, "GuildChangeApplyOptionRequest", new GuildChangeApplyOptionRequest { Option = 1, MinLevel = 1 }, 20063220);
        guild = Reload();
        guild.Members[memberId].Rank = 4;
        Save(guild);

        var scripts = Enumerable.Range(0, Limit("GuildScriptCount")).Select(_ => Scalars(Limit("GuildScriptLength"))).ToArray();
        JObject scriptResponse = Rpc(member, "GuildChangeScriptRequest", new GuildChangeScriptRequest { Scripts = scripts.ToList() });
        GuildAssert(scriptResponse["Scripts"]!.Values<string>().SequenceEqual(scripts), "Script response differs from accepted personal scripts");
        Rpc(member, "GuildChangeScriptRequest", new GuildChangeScriptRequest { Scripts = scripts.Append("extra").ToList() }, 20063129);
        Rpc(member, "GuildChangeScriptRequest", new GuildChangeScriptRequest { Scripts = [Scalars(Limit("GuildScriptLength") + 1)] }, 20063128);
        Rpc(member, "GuildChangeScriptRequest", new GuildChangeScriptRequest { Scripts = ["<bad>"] }, 20063127);
        GuildAssert(Player.collection.Find(row => row.PlayerData.Id == memberId).Single().GuildState.Scripts.SequenceEqual(scripts)
            && Player.collection.Find(row => row.PlayerData.Id == officerId).Single().GuildState.Scripts.Count == 0, "Rejected scripts changed personal state or leaked to another member");

        var positions = TableReaderV2.Parse<GuildPositionTable>().OrderBy(row => row.Id).ToArray();
        var choices = TableReaderV2.Parse<GuildCustomNameTable>().Where(row => row.Enable == 1).ToArray();
        var rankNames = positions.Select(row => new { Id = row.Id, Name = row.Name }).ToArray();
        var alternative = choices.First(row => row.RankLevel > 1 && row.Name != positions.Single(position => position.Id == row.RankLevel).Name);
        rankNames = rankNames.Select(row => row.Id == alternative.RankLevel ? new { row.Id, alternative.Name } : row).ToArray();
        string rankJson = JsonConvert.SerializeObject(rankNames);
        Rpc(officer, "GuildChangeRankNameRequest", new GuildChangeRankNameRequest { AllRankName = rankJson });
        Rpc(member, "GuildChangeRankNameRequest", new GuildChangeRankNameRequest { AllRankName = rankJson }, 20063063);
        Rpc(leader, "GuildChangeRankNameRequest", new GuildChangeRankNameRequest { AllRankName = "not-json" }, 20063060);
        Rpc(leader, "GuildChangeRankNameRequest", new GuildChangeRankNameRequest { AllRankName = JsonConvert.SerializeObject(rankNames.Skip(1)) }, 20063060);
        var leaderChoice = choices.First(row => row.RankLevel == 1 && row.Name != positions.Single(position => position.Id == 1).Name);
        Rpc(officer, "GuildChangeRankNameRequest", new GuildChangeRankNameRequest { AllRankName = JsonConvert.SerializeObject(rankNames.Select(row => row.Id == 1 ? new { row.Id, leaderChoice.Name } : row)) }, 20063063);
        GuildAssert(JToken.DeepEquals(JArray.Parse(Reload().RankNames), JArray.Parse(rankJson)), "Rejected rank-name request changed durable names");
        var freeIcon = TableReaderV2.Parse<GuildHeadPortraitTable>().First(row => !(row.ConditionId > 0) && !(row.Cost > 0) && row.Id != Reload().IconId);
        Rpc(officer, "GuildChangeIconRequest", new GuildChangeIconRequest { IconId = freeIcon.Id });
        Rpc(member, "GuildChangeIconRequest", new GuildChangeIconRequest { IconId = guild.IconId }, 20063048);
        Rpc(leader, "GuildChangeIconRequest", new GuildChangeIconRequest { IconId = int.MaxValue }, 20063049);
        GuildAssert(Reload().IconId == freeIcon.Id, "Icon permission/ownership rejection modified accepted icon");

        ValidateGuildIdentityImpeachment(scope, config);
        ValidateGuildIdentityMaintenance(scope);
        Console.WriteLine("Guild identity compatibility passed.");
    }

    private static void ValidateGuildIdentityImpeachment(GuildTestScope scope, Dictionary<string, string> config)
    {
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long Days(string name) => long.Parse(config[name]) * 86400L;
        var leader = scope.CreatePlayer();
        var a = scope.CreatePlayer();
        var b = scope.CreatePlayer();
        var lowerRank = scope.CreatePlayer();
        var tourist = scope.CreatePlayer();
        Guild guild = scope.SeedGuild(leader, a, b, lowerRank, tourist);
        long oldLeader = guild.LeaderId;
        long aId = a.Session.player.PlayerData.Id, bId = b.Session.player.PlayerData.Id;
        long lowerId = lowerRank.Session.player.PlayerData.Id, touristId = tourist.Session.player.PlayerData.Id;
        guild.Members[aId].Rank = guild.Members[bId].Rank = 2;
        guild.Members[lowerId].Rank = 3;
        guild.Members[touristId].Rank = 5;
        Guild.collection.ReplaceOne(row => row.Id == guild.Id, guild);
        Guild Reload() => Guild.collection.Find(row => row.Id == guild.Id).Single();
        void Save(Guild value) => Guild.collection.ReplaceOne(row => row.Id == guild.Id, value);
        object Empty() => new Dictionary<string, object>();
        JObject Petition(LoopbackSessionHarness actor, int code)
        {
            JObject response = GuildRpc(actor, "GuildImpeachRequest", Empty());
            GuildAssert(response.Value<int>("Code") == code, "Impeachment expected " + code + ": " + response);
            return response;
        }
        void Prepare() => GuildAssert(GuildRpc(a, "GuildMemberDetailRequest", new Dictionary<string, object> { ["GuildId"] = 0 }).Value<int>("Code") == 0, "Impeachment lifecycle member reload failed");
        int[] items = config["GuildImpeachCostItem"].Split('|').Select(int.Parse).ToArray();
        int[] costs = config["GuildImpeachCostCount"].Split('|').Select(int.Parse).ToArray();
        long[] Balances(long uid) => items.Select(item => Inventory.collection.Find(row => row.Uid == uid).Single().Items.Single(row => row.Id == item).Count).ToArray();
        var before = Balances(aId);
        Petition(leader, 20063120);
        Petition(a, 20063121);
        GuildAssert(Reload().ImpeachEndAt == 0 && Balances(aId).SequenceEqual(before), "Active-leader rejection charged or nominated");
        Server.Instance.Sessions.TryRemove(leader.Session.id, out _);
        Player.collection.UpdateOne(row => row.PlayerData.Id == oldLeader, Builders<Player>.Update.Set(row => row.PlayerData.LastLoginTime, Now() - Days("GuildImpeachDays") + 3600));
        Petition(a, 20063121);
        Player.collection.UpdateOne(row => row.PlayerData.Id == oldLeader, Builders<Player>.Update.Set(row => row.PlayerData.LastLoginTime, Now() - Days("GuildImpeachDays") - 60));
        Petition(a, 0);
        guild = Reload();
        GuildAssert(guild.ImpeachLeaderId == oldLeader && guild.ImpeachPetitioners.SequenceEqual(new[] { aId })
            && guild.ImpeachEndAt - guild.ImpeachStartedAt == Days("GuildImpeachDuration"), "Nomination target, petitioner, or authoritative duration was not persisted");
        GuildAssert(Balances(aId).SequenceEqual(before.Zip(costs, (balance, cost) => balance - cost)), "Nomination did not debit all authoritative costs exactly once");
        var paid = Balances(aId);
        Petition(a, 20063122);
        GuildAssert(Balances(aId).SequenceEqual(paid) && Reload().ImpeachPetitioners.Count == 1, "Duplicate petition charged or duplicated candidate");
        GuildAssert(GuildRpc(tourist, "GuildImpeachRequest", Empty()).Value<int>("Code") != 0 && Reload().ImpeachPetitioners.Count == 1, "Tourist became impeachment candidate");
        var bBalances = Balances(bId);
        b.Session.inventory.Items.Single(row => row.Id == items[^1]).Count = costs[^1] - 1;
        b.Session.inventory.Save();
        var insufficient = Balances(bId);
        GuildAssert(GuildRpc(b, "GuildImpeachRequest", Empty()).Value<int>("Code") != 0
            && Balances(bId).SequenceEqual(insufficient) && !Reload().ImpeachPetitioners.Contains(bId),
            "Unaffordable multi-item petition partially charged or nominated the caller");
        for (int index = 0; index < items.Length; index++)
            b.Session.inventory.Items.Single(row => row.Id == items[index]).Count = bBalances[index];
        b.Session.inventory.Save();
        Petition(b, 0);
        Petition(lowerRank, 0);
        guild = Reload();
        guild.ImpeachEndAt = Now() - 1;
        var dailyPeriod = RequiredMethod(
            typeof(Session).Assembly.GetType("AscNet.GameServer.Handlers.GuildModule", throwOnError: true)!,
            "DailyPeriod", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(DateTimeOffset)]);
        long contributionDay = (long)dailyPeriod.Invoke(null, [DateTimeOffset.UtcNow])!;
        guild.MemberContributionDays =
        [
            new() { PlayerId = aId, Day = contributionDay, Count = 10 },
            new() { PlayerId = bId, Day = contributionDay, Count = 10 },
            new() { PlayerId = lowerId, Day = contributionDay, Count = 10000 }
        ];
        guild.Members[aId].JoinedAt = guild.Members[bId].JoinedAt = Now() - 1000;
        Save(guild);
        Prepare();
        long winner = Math.Min(aId, bId);
        guild = Reload();
        GuildAssert(guild.LeaderId == winner && guild.Members[winner].Rank == 1 && guild.Members[oldLeader].Rank == 4
            && guild.ImpeachEndAt == 0 && guild.ImpeachPetitioners.Count == 0, "Election did not resolve rank/UID ties, demote old leader, and clear election atomically");
        Prepare();
        GuildAssert(Reload().LeaderId == winner && Balances(aId).SequenceEqual(paid), "Election replay changed leader or charged petitioner");

        // Persisted expired rounds exercise eligibility, contribution and join-time precedence without waiting days.
        foreach (int precedence in new[] { 0, 1, 2, 4, 5, 3 })
        {
            guild = Reload();
            guild.LeaderId = oldLeader;
            guild.Members[oldLeader].Rank = 1;
            guild.Members[aId].Rank = guild.Members[bId].Rank = 2;
            contributionDay = (long)dailyPeriod.Invoke(null, [DateTimeOffset.UtcNow])!;
            guild.Members[aId].WeekContribute = 10000;
            guild.Members[bId].WeekContribute = 0;
            guild.MemberContributionDays =
            [
                new() { PlayerId = aId, Day = contributionDay - 6, Count = 20 },
                new() { PlayerId = bId, Day = contributionDay, Count = precedence == 0 ? 21 : 20 },
                new() { PlayerId = aId, Day = contributionDay - 7, Count = 10000 },
                new() { PlayerId = aId, Day = contributionDay + 1, Count = 10000 }
            ];
            guild.Members[aId].JoinedAt = 100;
            guild.Members[bId].JoinedAt = precedence == 1 ? 99 : 100;
            guild.ImpeachLeaderId = oldLeader;
            guild.ImpeachStartedAt = Now() - Days("GuildImpeachDuration") - 100;
            guild.ImpeachEndAt = Now() - 1;
            guild.ImpeachPetitioners = precedence == 2 ? new List<long> { touristId } : new List<long> { aId, bId };
            if (precedence is 4 or 5)
            {
                guild.Members[lowerId].Rank = precedence == 4 ? 3 : 4;
                guild.ImpeachPetitioners = [lowerId];
            }
            Save(guild);
            if (precedence == 3)
                Player.collection.UpdateOne(row => row.PlayerData.Id == oldLeader, Builders<Player>.Update.Set(row => row.PlayerData.LastLoginTime, Now()));
            long start = Now();
            Prepare();
            guild = Reload();
            if (precedence < 2)
                GuildAssert(guild.LeaderId == bId, "Election ignored " + (precedence == 0 ? "contribution" : "earliest-join") + " precedence");
            else if (precedence == 2)
                GuildAssert(guild.LeaderId == oldLeader && guild.ImpeachEndAt >= start + Days("GuildImpeachExtend") && guild.ImpeachEndAt <= Now() + Days("GuildImpeachExtend"), "No-eligible-candidate election failed to extend configured duration");
            else if (precedence is 4 or 5)
                GuildAssert(guild.LeaderId == lowerId && guild.Members[lowerId].Rank == 1 && guild.Members[oldLeader].Rank == 4,
                    "Eligible elder/member petitioner could not become leader");
            else
                GuildAssert(guild.LeaderId == oldLeader && guild.ImpeachEndAt == 0 && guild.ImpeachPetitioners.Count == 0, "Returning leader did not cancel impeachment without leadership mutation");
        }
    }

    private static void ValidateGuildIdentityMaintenance(GuildTestScope scope)
    {
        var leader = scope.CreatePlayer();
        var officer = scope.CreatePlayer();
        var member = scope.CreatePlayer();
        Guild guild = scope.SeedGuild(leader, officer, member);
        guild.Members[officer.Session.player.PlayerData.Id].Rank = 2;
        guild.ContributeLeft = 12345;
        guild.EconomyDay = long.MinValue;
        guild.MaintainState = 1;
        guild.EmergenceTime = DateTimeOffset.UtcNow.AddYears(-1).ToUnixTimeSeconds();
        Guild.collection.ReplaceOne(row => row.Id == guild.Id, guild);
        foreach (var actor in new[] { leader, officer, member })
        {
            JObject response = GuildRpc(actor, "GuildPayMaintainRequest", new Dictionary<string, object>());
            GuildAssert(response.Value<int>("Code") == (actor == member ? 20063183 : 20063184), "Maintenance accepted payment or ignored administrator gate without authoritative dues");
        }
        var reopened = scope.OpenPlayer(leader.Session.player.PlayerData.Id);
        JObject detail = GuildRpc(reopened, "GuildListDetailRequest", new Dictionary<string, object> { ["GuildId"] = 0 });
        Guild persisted = Guild.collection.Find(row => row.Id == guild.Id).Single();
        GuildAssert(detail.Value<int>("Code") == 0 && detail.Value<int>("MaintainState") == 0 && detail.Value<long>("EmergenceTime") == 0,
            "Absent upkeep authority did not report normal/no debt after reload");
        GuildAssert(persisted.Active && persisted.Level == guild.Level && persisted.LeaderId == guild.LeaderId
            && persisted.MemberIds.SequenceEqual(guild.MemberIds) && persisted.ContributeLeft == guild.ContributeLeft,
            "Absent maintenance dues caused a debit, demotion, disband, or membership loss");
    }
}
