using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.guildwar;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static int GuildWarSetting(string key) => (int)RequiredMethod(
        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildWarModule"), "Setting",
        BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)]).Invoke(null, [key])!;

    private static (int Season, int RoundId, long Start, long End) GuildWarCalendar(DateTimeOffset now) =>
        ((int Season, int RoundId, long Start, long End))RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildWarModule"), "Calendar",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(DateTimeOffset)]).Invoke(null, [now])!;

    private static bool GuildWarNodeActive(GuildWarRoundState round, GuildWarNodeState node) => (bool)RequiredMethod(
        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildWarModule"), "IsDynamicsNodeActive",
        BindingFlags.Static | BindingFlags.NonPublic, [typeof(GuildWarRoundState), typeof(GuildWarNodeState)]).Invoke(null, [round, node])!;

    private static long GuildWarDailyPeriod(DateTimeOffset now) => (long)RequiredMethod(
        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildModule"), "DailyPeriod",
        BindingFlags.Static | BindingFlags.NonPublic, [typeof(DateTimeOffset)]).Invoke(null, [now])!;

    private static DateTimeOffset GuildWarGameDate(DateTimeOffset now) => (DateTimeOffset)RequiredMethod(
        RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildModule"), "GameDate",
        BindingFlags.Static | BindingFlags.NonPublic, [typeof(DateTimeOffset)]).Invoke(null, [now])!;

    private static List<AscNet.Table.V2.share.reward.RewardGoodsTable> GuildWarRewardGoods(int rewardId) =>
        (List<AscNet.Table.V2.share.reward.RewardGoodsTable>)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"), "GetRewardGoods",
            BindingFlags.Static | BindingFlags.Public, [typeof(int)]).Invoke(null, [rewardId])!;

    private static void ValidateGuildWarCompatibility()
    {
        using GuildTestScope scope = new();
        Type warModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildWarModule");
        FieldInfo clock = warModule.GetField("Clock", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(warModule.FullName, "Clock");
        Func<DateTimeOffset> oldClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        var calendar = GuildWarCalendar(oldClock());
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(calendar.Start + 3600);
        clock.SetValue(null, (Func<DateTimeOffset>)(() => now));
        try
        {
            GuildWarVerifyEntry(scope, calendar.Start, ref now);
            LoopbackSessionHarness leader = scope.CreatePlayer();
            LoopbackSessionHarness member = scope.CreatePlayer();
            LoopbackSessionHarness outsider = scope.CreatePlayer();
            GuildWarAssertUnaffiliated(outsider);
            Guild guild = scope.SeedGuild(leader, member);
            guild.CreatedAt = calendar.Start - 86400;
            guild.Normalize();
            foreach (GuildMemberState membership in guild.Members.Values) membership.JoinedAt = guild.CreatedAt;
            guild.SaveChecked();
            uint guildId = guild.Id;
            long leaderUid = leader.Session.player.PlayerData.Id;
            int energy = GuildWarSetting("ActivityPointItemId");
            int difficulty = TableReaderV2.Parse<GuildWarDifficultyTable>().Where(row => row.PreId.GetValueOrDefault() == 0).Min(row => row.Id);
            GuildWarCall(leader, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
            GuildWarCall(member, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = difficulty }, false);
            GuildWarCall(leader, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = int.MaxValue }, false);
            GuildWarCall(leader, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = difficulty });
            GuildWarRoundState round = Guild.FindById(guildId)!.War.Rounds.Single();
            var rules = TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(row => row.Id);
            GuildAssert(round.Nodes.Select(node => node.NodeId).Order().SequenceEqual(
                rules.Values.Where(row => row.DifficultyId == difficulty).Select(row => row.Id).Order()),
                "Selected difficulty must instantiate exactly its authoritative map, including replacement and relic nodes");
            GuildWarCall(leader, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = difficulty }, false);
            int home = round.Nodes.Single(node => rules[node.NodeId].Type == 1).Uid;
            int first = rules[home].LinkIds.First(id => id > 0);
            GuildWarSetBalance(leader, energy, GuildWarSetting("MaxEnergy"));
            long ap = GuildWarBalance(leader, energy);
            var move = new Dictionary<string, object> { ["CurNodeId"] = home, ["NextNodeId"] = first };
            GuildWarCall(leader, "GuildWarCanMoveRequest", move);
            GuildAssert(GuildWarPlayer(leader).GuildState.War.Rounds.Single().CurNodeId == home
                && GuildWarBalance(leader, energy) == ap, "CanMove is a preview: no movement or AP debit");
            GuildWarCall(leader, "GuildWarMoveRequest", new Dictionary<string, object>
                { ["CurNodeId"] = first, ["NextNodeId"] = home }, false);
            GuildWarCall(leader, "GuildWarMoveRequest", move);
            GuildAssert(GuildWarPlayer(leader).GuildState.War.Rounds.Single().CurNodeId == first
                && GuildWarBalance(leader, energy) == ap - GuildWarSetting("MoveCostEnergy"),
                "Movement must persist location and debit exactly the authored one-edge AP cost");
            GuildWarCall(leader, "GuildWarMoveRequest", move, false);
            GuildWarCall(member, "GuildWarEditLineRequest", new Dictionary<string, object> { ["AttackPlan"] = new[] { first } }, false);
            GuildWarCall(leader, "GuildWarEditLineRequest", new Dictionary<string, object> { ["AttackPlan"] = new[] { first, first } }, false);
            GuildWarCall(leader, "GuildWarEditLineRequest", new Dictionary<string, object> { ["AttackPlan"] = new[] { home, first } });
            GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().AttackPlan.SequenceEqual(new[] { home, first }),
                "Officer route edits must persist exactly the requested valid ordered nodes");
            guild = Guild.FindById(guildId)!;
            guild.Members[member.Session.player.PlayerData.Id].JoinedAt = now.ToUnixTimeSeconds();
            guild.SaveChecked();
            GuildAssert(GuildWarCall(member, "GuildWarCanMoveRequest", move, false).Value<int>("Code") == 20164010,
                "A member joining after round start cannot participate in that round");
            guild = Guild.FindById(guildId)!;
            guild.Members[member.Session.player.PlayerData.Id].JoinedAt = calendar.Start - 1;
            guild.SaveChecked();

            int peerCharacter = GuildWarVerifySupport(leader, member, outsider, ref now);
            GuildWarVerifyCombat(leader, member, guildId, peerCharacter, ref now);
            GuildWarVerifyDynamics(leader, member, guildId, ref now);
            GuildWarVerifyRollover(scope, leader, member, guildId, calendar.Start, ref now);
            GuildAssert(Guild.FindById(guildId)!.MemberIds.Order().SequenceEqual(new[] { leaderUid, member.Session.player.PlayerData.Id }.Order()),
                "War rounds, results, and season rollover must preserve real membership");
        }
        finally
        {
            clock.SetValue(null, oldClock);
        }
    }

    private static void GuildWarVerifyEntry(GuildTestScope scope, long seasonStart, ref DateTimeOffset now)
    {
        DateTimeOffset originalNow = now;
        var empty = new Dictionary<string, object>();
        var difficultyRows = TableReaderV2.Parse<GuildWarDifficultyTable>().ToDictionary(row => row.Id);
        GuildAssert(difficultyRows.ContainsKey(1), "Client first-round auto-selection must reference an authored difficulty");
        int alternate = difficultyRows.Values.First(row => row.Id != 1 && row.PreId.GetValueOrDefault() == 0).Id;
        var rules = TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(row => row.Id);
        int energy = GuildWarSetting("ActivityPointItemId");
        MethodInfo login = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildWarModule"),
            "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session)]);
        LoopbackSessionHarness actor = scope.CreatePlayer();
        Guild guild = scope.SeedGuild(actor);
        long uid = actor.Session.player.PlayerData.Id;
        guild.CreatedAt = seasonStart;
        guild.Members[uid].JoinedAt = seasonStart;
        guild.SaveChecked();
        uint guildId = guild.Id;

        void AssertEntry(int expected, bool expectNotification = true)
        {
            int packetId = Interlocked.Increment(ref guildPacketId);
            AscNet.GameServer.PacketFactory.GetRequestPacketHandler("GuildWarGetActivityDataRequest")!(actor.Session, new()
            {
                Id = packetId, Name = "GuildWarGetActivityDataRequest", Content = MessagePackSerialize(empty.GetType(), empty)
            });
            NotifyGuildWarActivityData? notified = null;
            JObject? response = null;
            for (int count = 0; count < 256 && response is null; count++)
            {
                var packet = actor.ReadPacket("guild war banner refresh");
                if (packet.Type == AscNet.GameServer.Packet.ContentType.Push)
                {
                    var push = MessagePack.MessagePackSerializer.Deserialize<AscNet.GameServer.Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyGuildWarActivityData))
                    {
                        GuildAssert(notified is null, "Banner refresh must not duplicate its personal state notification");
                        notified = MessagePack.MessagePackSerializer.Deserialize<NotifyGuildWarActivityData>(push.Content);
                    }
                    continue;
                }
                GuildAssert(packet.Type == AscNet.GameServer.Packet.ContentType.Response, "Banner refresh packet type");
                var reply = MessagePack.MessagePackSerializer.Deserialize<AscNet.GameServer.Packet.Response>(packet.Content);
                GuildAssert(reply.Id == packetId && reply.Name == "GuildWarGetActivityDataResponse", "Banner refresh response correlation");
                response = JObject.Parse(MessagePack.MessagePackSerializer.ConvertToJson(reply.Content));
            }
            if (response is null) throw new InvalidDataException("Banner refresh did not emit its correlated response");
            GuildAssert(response.Value<int>("Code") == 0 && (notified is not null) == expectNotification,
                "New/repaired personal state must arrive before the activity response; read-only refresh must not resend it");
            var data = notified ?? (NotifyGuildWarActivityData)login.Invoke(null, [actor.Session])!;
            int roundId = response["ActivityData"]!.Value<int>("CurRoundId");
            JToken current = response["ActivityData"]!["RoundData"]!.Single(row => row.Value<int>("RoundId") == roundId);
            var personal = data.MyRoundData.Single(row => row.RoundId == roundId && row.GuildId == guildId);
            GuildAssert(current.Value<int>("DifficultyId") == expected && current.Value<int>("SkipRound") == 0
                && personal.DifficultyId == expected && personal.SkipRound == 0
                && personal.CurNodeId == rules.Values.Single(row => row.DifficultyId == expected && row.Type == 1).Id,
                "Active banner refresh must expose matching guild/personal difficulty and authored home, avoiding the legacy selection route");
            GuildAssert(GuildWarPlayer(actor).GuildState.War.Rounds.Single(row => row.RoundId == roundId).DifficultyId == expected,
                "Banner eligibility must survive persisted player reload");
        }

        try
        {
            now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 3600);
            AssertEntry(1);
            AssertEntry(1, false);
            guild = Guild.FindById(guildId)!;
            GuildWarRoundState current = guild.War.Rounds.Single();
            guild.War.Rounds = [new() { RoundId = current.RoundId, StartedAt = current.StartedAt, EndsAt = current.EndsAt }];
            guild.SaveChecked();
            Player player = GuildWarPlayer(actor);
            player.GuildState.War.Rounds = [new() { GuildId = guildId, RoundId = current.RoundId }];
            player.SaveChecked();
            actor.Session.player = player;
            AssertEntry(1);
            AssertEntry(1, false);

            guild = Guild.FindById(guildId)!;
            current = guild.War.Rounds.Single();
            guild.War.NextDifficultyId = alternate;
            guild.War.Rounds = [new() { RoundId = current.RoundId, StartedAt = current.StartedAt, EndsAt = current.EndsAt }];
            guild.SaveChecked();
            player = GuildWarPlayer(actor);
            player.GuildState.War.Rounds.Clear();
            player.SaveChecked();
            actor.Session.player = player;
            AssertEntry(alternate);
            guild = Guild.FindById(guildId)!;
            current = guild.War.Rounds.Single();
            var damaged = current.Nodes.First(node => rules[node.NodeId].Type != 1 && node.CurHp > 1);
            damaged.CurHp--;
            long hp = damaged.CurHp;
            guild.War.NextDifficultyId = 1;
            guild.SaveChecked();
            GuildWarCall(actor, "GuildWarGetActivityDataRequest", empty);
            GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DifficultyId == alternate
                && Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == damaged.Uid).CurHp == hp,
                "Refreshing a selected map must preserve its difficulty and existing node damage");

            var roundIds = TableReaderV2.Parse<GuildWarRoundTable>().Where(row => row.ActivityId ==
                TableReaderV2.Parse<GuildWarActivityTable>().Where(row => row.TimeId > 0).MaxBy(row => row.Id)!.Id)
                .OrderBy(row => row.Id).Select(row => row.Id).ToArray();
            GuildWarRoundState Prior(int index, int difficulty, bool cleared) => new()
            {
                RoundId = roundIds[index], DifficultyId = difficulty, StartedAt = seasonStart + index * 7 * 86400,
                EndsAt = seasonStart + (index + 1) * 7 * 86400, Settled = true,
                Nodes = rules.Values.Where(row => row.DifficultyId == difficulty).Select(row => new GuildWarNodeState
                {
                    Uid = row.Id, NodeId = row.Id, HpMax = row.HpMax, CurHp = row.HpMax,
                    IsDead = cleared && row.Type is 6 or 7 or 9 or 14 or 19 ? 1 : 0
                }).ToList()
            };
            now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 14 * 86400 + 3600);
            foreach (bool newestCleared in new[] { false, true })
            {
                guild = Guild.FindById(guildId)!;
                guild.War.NextDifficultyId = 0;
                guild.War.Rounds = [Prior(0, alternate, true), Prior(1, 1, newestCleared)];
                guild.SaveChecked();
                player = GuildWarPlayer(actor);
                player.GuildState.War.Rounds.Clear();
                player.SaveChecked();
                actor.Session.player = player;
                AssertEntry(newestCleared ? 1 : alternate);
            }

            now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 3600);
            guild = Guild.FindById(guildId)!;
            guild.CreatedAt = seasonStart + 1;
            guild.War.Rounds.Clear();
            guild.War.NextDifficultyId = alternate;
            guild.SaveChecked();
            player = GuildWarPlayer(actor);
            player.GuildState.War.Rounds.Clear();
            player.GuildState.War.EnergyDay = 0;
            player.SaveChecked();
            actor.Session.player = player;
            GuildWarSetBalance(actor, energy, 0);
            JObject skipped = GuildWarCall(actor, "GuildWarGetActivityDataRequest", empty);
            GuildAssert(skipped["ActivityData"]!["RoundData"]!.Single().Value<int>("SkipRound") == 1
                && Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Count == 0 && GuildWarBalance(actor, energy) == 0,
                "Guild created after the exact round start must report SkipRound without initializing gameplay or granting energy");
            GuildWarCall(actor, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = 1 });
            GuildAssert(GuildWarCall(actor, "GuildWarMoveRequest", new Dictionary<string, object>
                { ["CurNodeId"] = 0, ["NextNodeId"] = rules.Values.First(row => row.DifficultyId == 1).Id }, false)
                .Value<int>("Code") == 20164010, "Skipped guild gameplay must fail participation eligibility before map validation");
            GuildWarCall(actor, "GuildWarEditLineRequest", new Dictionary<string, object> { ["AttackPlan"] = Array.Empty<int>() }, false);
            guild = Guild.FindById(guildId)!;
            GuildAssert(guild.MemberIds.SequenceEqual(new[] { uid }) && guild.War.Rounds.Single().Nodes.Count == 0
                && GuildWarBalance(actor, energy) == 0 && GuildWarPlayer(actor).GuildState.War.EnergyDay == 0,
                "Skipped selection and rejected gameplay cannot seed a map, change valid membership, or accrue energy");
            now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 7 * 86400);
            AssertEntry(1);
            GuildAssert(GuildWarBalance(actor, energy) == GuildWarSetting("AddEnergy"),
                "The same guild enters its next eligible round and receives the authored first energy grant");
        }
        finally
        {
            now = originalNow;
        }
    }

    private static void GuildWarVerifyCombat(LoopbackSessionHarness actor, LoopbackSessionHarness peer,
        uint guildId, int peerCharacter, ref DateTimeOffset now)
    {
        var nodeRules = TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(row => row.Id);
        var stages = TableReaderV2.Parse<GuildWarStageTable>().ToDictionary(row => row.Id);
        int energy = GuildWarSetting("ActivityPointItemId");
        long uid = actor.Session.player.PlayerData.Id;
        int character = checked((int)actor.Session.character.Characters.First().Id);
        var targets = Guild.FindById(guildId)!.War.Rounds.Single().Nodes
            .Where(node => nodeRules[node.NodeId].GuildWarStageId > 0 && nodeRules[node.NodeId].RootId.GetValueOrDefault() == 0)
            .GroupBy(node => nodeRules[node.NodeId].Type).Select(group => group.First()).ToList();
        GuildAssert(targets.Select(node => nodeRules[node.NodeId].Type).Order().SequenceEqual(new[] { 2, 5, 16, 19 }),
            "Every authored fightable node feature must enter the behavioral matrix");
        bool useSupport = true;
        foreach (GuildWarNodeState target in targets)
        {
            GuildWarNodeTable rule = nodeRules[target.NodeId];
            int stageId = stages[rule.GuildWarStageId!.Value].StageId;
            Player player = GuildWarPlayer(actor);
            player.GuildState.War.Rounds.Single().CurNodeId = target.NodeId;
            player.SaveChecked();
            actor.Session.player = player;
            GuildWarSetBalance(actor, energy, GuildWarSetting("MaxEnergy"));
            GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
            {
                ["TeamInfo"] = GuildWarTeam(uid, character, useSupport ? peer.Session.player.PlayerData.Id : 0, useSupport ? peerCharacter : 0)
            });
            PreFightRequest preRequest = new()
            {
                PreFightData = new()
                {
                    StageId = checked((uint)stageId), GuildWarUid = target.Uid, ChallengeCount = 1,
                    CaptainPos = 1, FirstFightPos = 1,
                    CardIds = [checked((uint)character), useSupport ? checked((uint)peerCharacter) : 0, 0],
                    RobotIds = [0, 0, 0]
                }
            };
            GuildWarCall(actor, "GuildWarSweepRequest", new GuildWarSweepRequest { Uid = target.Uid, StageId = stageId, SweepType = 1 }, false);
            long ap = GuildWarBalance(actor, energy);
            long hp = Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == target.Uid).CurHp;
            long point = GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point;
            JObject started = GuildWarCall(actor, "PreFightRequest", preRequest);
            long fightId = started["FightData"]!.Value<long>("FightId");
            GuildAssert(started["FightData"]!.Value<bool>("Online") == false,
                "Guild expedition combat is local node combat, never fabricated multiplayer matching");
            GuildAssert(GuildWarCall(actor, "PreFightRequest", preRequest)["FightData"]!.Value<long>("FightId") == fightId,
                "Retrying the same unsettled deployment must reuse its authorized fight");
            GuildAssert(GuildWarBalance(actor, energy) == ap, "Pre-fight must not spend confirmation AP");
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId }, false);
            FightSettleRequest settle = CreateMissingStageSettleRequest(checked((uint)stageId), fightId, uid);
            settle.Result.FightId++;
            GuildWarCall(actor, "FightSettleRequest", settle, false);
            settle.Result.FightId = fightId;
            settle.Result.IsWin = false;
            GuildWarCall(actor, "FightSettleRequest", settle);
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId }, false);
            GuildAssert(GuildWarBalance(actor, energy) == ap && GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point == point,
                "A lost candidate cannot spend AP or grant score");
            started = GuildWarCall(actor, "PreFightRequest", preRequest);
            fightId = started["FightData"]!.Value<long>("FightId");
            settle = CreateMissingStageSettleRequest(checked((uint)stageId), fightId, uid);
            settle.Result.LeftTime = int.MaxValue;
            GuildWarCall(actor, "FightSettleRequest", settle, false);
            settle.Result.LeftTime = 0;
            JObject settled = GuildWarCall(actor, "FightSettleRequest", settle);
            JObject result = (JObject)settled["Settle"]!["GuildWarFightResult"]!;
            long damage = result.Value<long>("Damage");
            long gained = result.Value<long>("Point");
            GuildAssert(damage > 0 && damage <= rule.MaxSubHp && gained > 0 && gained <= rule.MaxPoint,
                "Candidate damage and score must respect authored node limits for type " + rule.Type);
            GuildAssert(GuildWarBalance(actor, energy) == ap
                && Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == target.Uid).CurHp == hp
                && GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point == point,
                "Settling a win must persist only a candidate, not HP/AP/score");
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId + 1 }, false);
            GuildWarSetBalance(actor, energy, 0);
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId }, false);
            GuildAssert(!GuildWarPlayer(actor).GuildState.War.Rounds.Single().PendingFight!.Confirmed,
                "An unaffordable confirmation must preserve the uncommitted candidate");
            GuildWarSetBalance(actor, energy, ap);
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId });
            GuildAssert(GuildWarBalance(actor, energy) == ap - rule.FightCostEnergy
                && Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == target.Uid).CurHp == Math.Max(0, hp - damage)
                && GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point == point + gained,
                "Confirmation must apply authored AP, candidate damage and score exactly once");
            GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId });
            GuildAssert(GuildWarBalance(actor, energy) == ap - rule.FightCostEnergy
                && GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point == point + gained,
                "A new-packet duplicate confirmation must not charge or score again");
            if (useSupport)
            {
                GuildAssert(GuildWarPlayer(peer).GuildState.War.Rounds.Single().SupportSupply == GuildWarSetting("AssistCountSupply"),
                    "Confirmed peer support earns one authored supply grant");
                GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
                    { ["TeamInfo"] = GuildWarTeam(uid, character, peer.Session.player.PlayerData.Id, peerCharacter) }, false);
                long cooldown = GuildWarSetting("UseAssistCharacterCd");
                now = now.AddSeconds(cooldown - 1);
                GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
                    { ["TeamInfo"] = GuildWarTeam(uid, character, peer.Session.player.PlayerData.Id, peerCharacter) }, false);
                now = now.AddSeconds(1);
                GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
                    { ["TeamInfo"] = GuildWarTeam(uid, character, peer.Session.player.PlayerData.Id, peerCharacter) });
                useSupport = false;
            }
            var difficulty = TableReaderV2.Parse<GuildWarDifficultyTable>().Single(row => row.Id == rule.DifficultyId);
            double hpFactor = difficulty.SweepHpFactor;
            double pointFactor = difficulty.SweepPointFactor;
            GuildWarCall(actor, "GuildWarSweepRequest", new GuildWarSweepRequest { Uid = target.Uid, StageId = stageId, SweepType = 2 }, false);
            GuildWarCall(actor, "GuildWarSweepRequest", new GuildWarSweepRequest { Uid = target.Uid, StageId = stageId, SweepType = 1 });
            GuildAssert(GuildWarBalance(actor, energy) == ap - rule.FightCostEnergy - rule.SweepCostEnergy
                && GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point == point + gained + (long)Math.Floor(gained * pointFactor)
                && Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == target.Uid).CurHp
                    == Math.Max(0, hp - damage - (long)Math.Floor(damage * hpFactor)),
                "Sweep must derive HP/score from saved records and authored ordinary factors, including the current Boss7 node");
        }
    }
    private static JObject GuildWarFinishEncounter(LoopbackSessionHarness actor, uint guildId, int targetUid, int stageId, int location)
    {
        Player player = GuildWarPlayer(actor);
        GuildWarPlayerRound personal = player.GuildState.War.Rounds.Single(row => row.RoundId == Guild.FindById(guildId)!.War.RoundId);
        personal.CurNodeId = location;
        player.SaveChecked();
        actor.Session.player = player;
        int character = checked((int)actor.Session.character.Characters.First().Id);
        GuildWarSetBalance(actor, GuildWarSetting("ActivityPointItemId"), GuildWarSetting("MaxEnergy"));
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
            { ["TeamInfo"] = GuildWarTeam(player.PlayerData.Id, character) });
        JObject start = GuildWarCall(actor, "PreFightRequest", new PreFightRequest
        {
            PreFightData = new()
            {
                StageId = checked((uint)stageId), GuildWarUid = targetUid, ChallengeCount = 1,
                CaptainPos = 1, FirstFightPos = 1, CardIds = [checked((uint)character), 0, 0], RobotIds = [0, 0, 0]
            }
        });
        JObject settled = GuildWarCall(actor, "FightSettleRequest", CreateMissingStageSettleRequest(
            checked((uint)stageId), start["FightData"]!.Value<long>("FightId"), player.PlayerData.Id));
        GuildWarCall(actor, "GuildWarConfirmFightResultRequest", new GuildWarConfirmFightResultRequest { StageId = stageId });
        return (JObject)settled["Settle"]!["GuildWarFightResult"]!;
    }

    private static void GuildWarVerifyDynamics(LoopbackSessionHarness actor, LoopbackSessionHarness peer,
        uint guildId, ref DateTimeOffset now)
    {
        var nodes = TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(row => row.Id);
        var stages = TableReaderV2.Parse<GuildWarStageTable>().ToDictionary(row => row.Id);
        int energy = GuildWarSetting("ActivityPointItemId");
        Guild guild = Guild.FindById(guildId)!;
        GuildWarRoundState round = guild.War.Rounds.Single();
        int stationNode = round.Nodes.First(node => nodes[node.NodeId].Type == 2 && nodes[node.NodeId].RootId.GetValueOrDefault() == 0).Uid;
        int ownCharacter = checked((int)actor.Session.character.Characters.First().Id);
        int peerCharacter = checked((int)peer.Session.character.Characters.First().Id);
        GuildWarCall(peer, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = peerCharacter }, false);
        GuildWarCall(actor, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = int.MaxValue }, false);
        GuildWarCall(actor, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = ownCharacter });
        GuildWarCall(actor, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = ownCharacter }, false);
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
            { ["TeamInfo"] = GuildWarTeam(actor.Session.player.PlayerData.Id, ownCharacter) }, false);
        int stationStage = stages[nodes[stationNode].GuildWarStageId!.Value].StageId;
        JObject stationFight = GuildWarFinishEncounter(peer, guildId, stationNode, stationStage, stationNode);
        int stationBuff = nodes[stationNode].DeployBuff[nodes[stationNode].DeployCharacterNum.IndexOf(1)];
        GuildAssert(stationFight.Value<long>("Damage") == Math.Min(nodes[stationNode].MaxSubHp!.Value,
            nodes[stationNode].BaseSubHp!.Value + stationBuff), "A real peer's station must add the authored one-station damage buff");
        GuildWarCall(peer, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = peerCharacter });
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().Stations.Count(station => station.NodeId == stationNode) == 2,
            "Two members' stations must coexist without overwriting each other");
        GuildWarCall(actor, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = 0 });
        GuildWarCall(peer, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = 0 });
        GuildWarCall(actor, "XGuildWarBeStationedRequest", new XGuildWarBeStationedRequest { NodeUid = stationNode, CharacterId = 0 }, false);
        int home = round.Nodes.Single(node => nodes[node.NodeId].Type == 1).Uid;
        GuildWarCall(actor, "XGuildWarSelectDefenseNodeRequest", new XGuildWarSelectDefenseNodeRequest { NodeId = home }, false);
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DefenseDic.Count == 0,
            "Current authority has no defense node; a home node must not become a fake defense target");

        foreach (GuildWarMonsterState monster in round.Monsters)
        {
            var elite = TableReaderV2.Parse<GuildWarEliteMonsterTable>().Single(row => row.Id == monster.MonsterId);
            var patrol = TableReaderV2.Parse<GuildWarMonsterPatrolTable>().Single(row => row.Id == elite.PatrolId);
            long hp = Guild.FindById(guildId)!.War.Rounds.Single().Monsters.Single(row => row.Uid == monster.Uid).CurHp;
            JObject result = GuildWarFinishEncounter(actor, guildId, monster.Uid,
                stages[elite.GuildWarStageId].StageId, patrol.Routes[monster.CurNodeIdx]);
            GuildAssert(result.Value<int>("Type") == 2 && result.Value<int>("MonsterId") == elite.Id
                && Guild.FindById(guildId)!.War.Rounds.Single().Monsters.Single(row => row.Uid == monster.Uid).CurHp
                    == Math.Max(0, hp - result.Value<long>("Damage")), "Every authored map elite must settle against its real patrol identity and shared HP");
        }
        guild = Guild.FindById(guildId)!;
        GuildWarMonsterState defeated = guild.War.Rounds.Single().Monsters.First();
        defeated.CurHp = 1;
        guild.SaveChecked();
        var defeatedRule = TableReaderV2.Parse<GuildWarEliteMonsterTable>().Single(row => row.Id == defeated.MonsterId);
        var defeatedRoute = TableReaderV2.Parse<GuildWarMonsterPatrolTable>().Single(row => row.Id == defeatedRule.PatrolId);
        GuildWarFinishEncounter(actor, guildId, defeated.Uid, stages[defeatedRule.GuildWarStageId].StageId, defeatedRoute.Routes[defeated.CurNodeIdx]);
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.Monsters.Single(row => row.Uid == defeated.Uid).CurHp == 0
            && round.DynamicsActions.Any(action => action.ActionType == 1 && action.MonsterUid == defeated.Uid),
            "A confirmed lethal elite result must persist death and its own death action");
        guild = Guild.FindById(guildId)!;
        round = guild.War.Rounds.Single();
        GuildWarMonsterState attacking = round.Monsters.First(row => row.CurHp > 0);
        var attackingRule = TableReaderV2.Parse<GuildWarEliteMonsterTable>().Single(row => row.Id == attacking.MonsterId);
        var attackingRoute = TableReaderV2.Parse<GuildWarMonsterPatrolTable>().Single(row => row.Id == attackingRule.PatrolId);
        attacking.CurNodeIdx = attackingRoute.Routes.Count - 2;
        attacking.LastMoveDayNo = GuildWarDailyPeriod(now) - 1;
        round.DynamicsAdvancedAt = now.ToUnixTimeSeconds() - 1;
        long homeHp = round.Nodes.Single(node => node.Uid == home).CurHp;
        long homeMax = round.Nodes.Single(node => node.Uid == home).HpMax;
        guild.SaveChecked();
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.Monsters.Single(row => row.Uid == attacking.Uid).CurHp == 0
            && round.Nodes.Single(node => node.Uid == home).CurHp == homeHp - Math.Min(homeHp, homeMax * attackingRule.DamagePercent / 100)
            && round.DynamicsActions.Any(action => action.ActionType == 3 && action.MonsterUid == attacking.Uid)
            && round.DynamicsActions.Any(action => action.ActionType == 4 && action.MonsterUid == attacking.Uid),
            "A daily patrol endpoint must damage the real home by the authored percentage, then retire the elite");

        var reinforcementRule = TableReaderV2.Parse<GuildWarReinforcementsTable>()
            .Where(row => row.DifficultyId == round.DifficultyId).OrderBy(row => row.Id).First();
        guild = Guild.FindById(guildId)!;
        guild.War.Rounds.Single().Nodes.Single(node => node.NodeId == reinforcementRule.BornNodeId).CurHp = 1;
        guild.SaveChecked();
        GuildWarFinishEncounter(actor, guildId, reinforcementRule.BornNodeId,
            stages[nodes[reinforcementRule.BornNodeId].GuildWarStageId!.Value].StageId, reinforcementRule.BornNodeId);
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildWarReinforcementState reinforcement = round.Reinforcements.Single();
        GuildAssert(reinforcement.ReinforcementId == reinforcementRule.Id && reinforcement.CurHp == reinforcementRule.HpMax
            && round.DragonRageCfgId != 0, "Clearing the authored blocker must spawn its first reinforcement and open dragon rage");
        var request = new GuildWarSupportReinforcementRequest { ReinforcementUid = reinforcement.Uid };
        GuildWarSetBalance(actor, energy, GuildWarSetting("MaxEnergy"));
        GuildWarSetBalance(peer, energy, GuildWarSetting("MaxEnergy"));
        long before = GuildWarBalance(actor, energy);
        GuildWarCall(actor, "GuildWarSupportReinforcementRequest", request);
        GuildWarCall(actor, "GuildWarSupportReinforcementRequest", request, false);
        GuildWarCall(peer, "GuildWarSupportReinforcementRequest", request);
        long bonus = (long)reinforcementRule.SupportCost * GuildWarSetting("ReinforcementSupportHp");
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().Reinforcements.Single().CurHp == reinforcementRule.HpMax + 2 * bonus
            && GuildWarBalance(actor, energy) == before - reinforcementRule.SupportCost,
            "Independent member support spends one AP cost each and adds authored reinforcement HP");
        GuildWarCall(actor, "GuildWarCancelSupportReinforcementRequest", request);
        GuildWarCall(actor, "GuildWarCancelSupportReinforcementRequest", request, false);
        GuildAssert(GuildWarBalance(actor, energy) == before
            && Guild.FindById(guildId)!.War.Rounds.Single().Reinforcements.Single().CurHp == reinforcementRule.HpMax + bonus,
            "Cancellation refunds only the cancelling member's contribution");
        now = DateTimeOffset.FromUnixTimeSeconds(reinforcement.NextMoveTime - 1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(!Guild.FindById(guildId)!.War.Rounds.Single().Reinforcements.Single().Attacked,
            "Reinforcement cannot attack before its authored tick");
        now = now.AddSeconds(1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.Reinforcements.Single(row => row.Uid == reinforcement.Uid).Attacked
            && round.DynamicsActions.Any(action => action.ActionType == 16 && action.ReinforcementUid == reinforcement.Uid && action.Damage > 0),
            "The ready reinforcement must move and damage a real node at its scheduled tick");
        GuildWarCall(peer, "GuildWarCancelSupportReinforcementRequest", request, false);
        int actionCount = round.DynamicsActions.Count;
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DynamicsActions.Count == actionCount,
            "Same-clock activity refresh must not replay reinforcement actions");

        guild = Guild.FindById(guildId)!;
        round = guild.War.Rounds.Single();
        var rage = TableReaderV2.Parse<GuildWarDragonRageTable>().Single(row => row.Id == round.DragonRageCfgId);
        round.DragonRage = rage.UpLimit - rage.PassAdd;
        GuildWarNodeState chargeNode = round.Nodes.First(node => nodes[node.NodeId].Type == 2
            && nodes[node.NodeId].RootId.GetValueOrDefault() == 0 && node.Uid != stationNode && node.IsDead == 0);
        chargeNode.CurHp = 1;
        guild.SaveChecked();
        GuildWarFinishEncounter(actor, guildId, chargeNode.Uid,
            stages[nodes[chargeNode.NodeId].GuildWarStageId!.Value].StageId, chargeNode.NodeId);
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.DragonRage == rage.UpLimit && round.FullDragonRageCfgId > 0
            && round.DynamicsActions.Any(action => action.ActionType == 18), "A threshold-crossing clear activates full rage exactly at its authored cap");
        guild = Guild.FindById(guildId)!;
        round = guild.War.Rounds.Single();
        GuildWarNodeState treatingBoss = round.Nodes.First(node => nodes[node.NodeId].TreatMstInterval > 0
            && GuildWarNodeActive(round, node));
        treatingBoss.NextBossTreatMstTime = now.ToUnixTimeSeconds() + 1;
        GuildWarMonsterState wounded = round.Monsters.First();
        wounded.CurHp = wounded.HpMax / 2;
        wounded.DeadTime = 0;
        long woundedHp = wounded.CurHp;
        int deadUid = round.Monsters.First(monster => monster.Uid != wounded.Uid && monster.CurHp == 0).Uid;
        guild.SaveChecked();
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().Monsters.Single(monster => monster.Uid == wounded.Uid).CurHp == woundedHp,
            "Boss treatment must not heal before its scheduled interval");
        now = now.AddSeconds(1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.Monsters.Single(monster => monster.Uid == wounded.Uid).CurHp
                == Math.Min(wounded.HpMax, woundedHp + wounded.HpMax * Convert.ToInt64(nodes[treatingBoss.NodeId].TreatMstPercent) / 100)
            && round.Monsters.Single(monster => monster.Uid == deadUid).CurHp == 0
            && round.DynamicsActions.Any(action => action.ActionType == 12),
            "The authored rage-boss treatment heals living elites by the configured percentage but cannot resurrect dead elites");
        GuildWarNodeState changed = round.Nodes.First(node => nodes[node.NodeId].RootId > 0
            && nodes[node.NodeId].Type != 20 && GuildWarNodeActive(round, node));
        long changedHp = changed.CurHp;
        JObject changedResult = GuildWarFinishEncounter(actor, guildId, changed.Uid,
            stages[nodes[changed.NodeId].GuildWarStageId!.Value].StageId, nodes[changed.NodeId].RootId!.Value);
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().Nodes.Single(node => node.Uid == changed.Uid).CurHp
            == Math.Max(0, changedHp - changedResult.Value<long>("Damage")), "Full-rage replacement nodes must use their own authored fight and durable HP");
        guild = Guild.FindById(guildId)!;
        round = guild.War.Rounds.Single();
        round.DragonRage = rage.IntervalReduce;
        round.LastDragonRageReduceTime = now.ToUnixTimeSeconds();
        guild.SaveChecked();
        now = now.AddMinutes(rage.ReduceIntervalMinute).AddSeconds(-1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DragonRage == rage.IntervalReduce, "Dragon rage must not decay before its interval");
        now = now.AddSeconds(1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        GuildAssert(round.DragonRage == 0 && round.FullDragonRageCfgId == 0
            && round.DynamicsActions.Any(action => action.ActionType == 19), "The exact decay boundary restores normal node variants");
        for (int cycle = 2; cycle <= GuildWarSetting("FullDragonRageGameThrough"); cycle++)
        {
            guild = Guild.FindById(guildId)!;
            round = guild.War.Rounds.Single();
            GuildWarNodeState boss = round.Nodes.Single(node => nodes[node.NodeId].Type == 19
                && GuildWarNodeActive(round, node));
            boss.CurHp = 1;
            guild.SaveChecked();
            GuildWarFinishEncounter(actor, guildId, boss.Uid, stages[nodes[boss.NodeId].GuildWarStageId!.Value].StageId, boss.NodeId);
            GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().GameThrough == cycle,
                "A confirmed Boss7 defeat must advance exactly one cycle and rebuild its authored map");
        }
        round = Guild.FindById(guildId)!.War.Rounds.Single();
        var cycleRule = TableReaderV2.Parse<GuildWarPlayThroughTable>().Single(row => row.Id == round.GameThroughCfgId);
        GuildAssert(round.Nodes.Where(node => GuildWarNodeActive(round, node) && nodes[node.NodeId].Type == 20)
            .Select(node => nodes[node.NodeId].RootId!.Value).Order().SequenceEqual(cycleRule.ChangeNodeIds.Order()),
            "Cycle-three relics must replace exactly the authored free-route nodes");
        int fullRage = round.DragonRage;
        now = now.AddMinutes(rage.ReduceIntervalMinute);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DragonRage == fullRage,
            "The authored permanent-full-rage cycle must not decay");
    }


    private static void GuildWarVerifyRollover(GuildTestScope scope, LoopbackSessionHarness actor,
        LoopbackSessionHarness peer, uint guildId, long seasonStart, ref DateTimeOffset now)
    {
        Guild guild = Guild.FindById(guildId)!;
        GuildWarRoundState first = guild.War.Rounds.Single();
        int season = guild.War.Season;
        long uid = actor.Session.player.PlayerData.Id;
        var nodes = TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(row => row.Id);
        int boss = first.Nodes.Single(node => nodes[node.NodeId].Type == 19
            && GuildWarNodeActive(first, node)).Uid;
        var reward = TableReaderV2.Parse<GuildWarBossRewardTable>().Where(row => row.Difficulty == first.DifficultyId)
            .OrderBy(row => row.LimitLevel).First();
        var claim = new Dictionary<string, object> { ["Id"] = reward.Id, ["NodeUid"] = boss };
        GuildWarCall(actor, "XGuildWarGetBossRewardRequest", new Dictionary<string, object>
            { ["Id"] = reward.Id, ["NodeUid"] = first.Nodes.Single(node => nodes[node.NodeId].Type == 1).Uid }, false);
        var unavailable = TableReaderV2.Parse<GuildWarBossRewardTable>()
            .First(row => row.Difficulty == first.DifficultyId && row.LimitLevel > first.BossDead);
        GuildWarCall(actor, "XGuildWarGetBossRewardRequest", new Dictionary<string, object>
            { ["Id"] = unavailable.Id, ["NodeUid"] = boss }, false);
        Dictionary<int, long> beforeGoods = Inventory.collection.Find(row => row.Uid == uid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
        JObject claimed = GuildWarCall(actor, "XGuildWarGetBossRewardRequest", claim);
        foreach (var expected in GuildWarRewardGoods(reward.RewardId).GroupBy(goods => goods.TemplateId))
            GuildAssert(((JArray)claimed["RewardGoodsList"]!).Where(goods => goods.Value<int>("TemplateId") == expected.Key)
                    .Sum(goods => goods.Value<long>("Count")) == expected.Sum(goods => (long)goods.Count),
                "Boss claim response must contain every authored reward amount, not an empty success");
        foreach (var goods in ((JArray)claimed["RewardGoodsList"]!).GroupBy(goods => goods.Value<int>("TemplateId")))
            GuildAssert(GuildWarBalance(actor, goods.Key) == beforeGoods.GetValueOrDefault(goods.Key) + goods.Sum(row => row.Value<long>("Count")),
                "Boss reward goods must be credited to the real persisted inventory");
        long[] balances = Inventory.collection.Find(row => row.Uid == uid).Single().Items.OrderBy(item => item.Id).Select(item => item.Count).ToArray();
        GuildWarCall(actor, "XGuildWarGetBossRewardRequest", claim, false);
        GuildAssert(Inventory.collection.Find(row => row.Uid == uid).Single().Items.OrderBy(item => item.Id).Select(item => item.Count).SequenceEqual(balances),
            "A duplicate boss reward cannot mutate any inventory balance");
        GuildWarCall(peer, "XGuildWarGetBossRewardRequest", claim);
        GuildAssert(GuildWarPlayer(actor).GuildState.War.Rounds.Single().BossRewardIds.SequenceEqual(new[] { reward.Id })
            && GuildWarPlayer(peer).GuildState.War.Rounds.Single().BossRewardIds.SequenceEqual(new[] { reward.Id }),
            "Boss reward receipts are per member, not a single guild-wide claimant");
        int acknowledgedAction = first.DynamicsActions.First().ActionId;
        GuildWarCall(actor, "GuildWarPopupActionRequest", new GuildWarPopupActionRequest { ActionPlayed = [acknowledgedAction, acknowledgedAction, -1] });
        GuildAssert(GuildWarPlayer(actor).GuildState.War.PlayedActionIds.SequenceEqual(new[] { acknowledgedAction }),
            "Action acknowledgement must durably deduplicate only positive real action IDs");
        var taskIds = TableReaderV2.Parse<GuildWarTaskTable>()
            .Where(row => row.RoundId == first.RoundId && row.DifficultyId == first.DifficultyId).Select(row => row.TaskId).ToHashSet();
        var conditions = TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>().ToDictionary(row => row.Id);
        var fightTasks = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>()
            .Where(row => taskIds.Contains(row.Id) && conditions[row.Condition].Type == 81000).OrderBy(row => row.Result).ToList();
        int fights = GuildWarPlayer(actor).GuildState.War.Rounds.Single().FightCount;
        var earnedTask = fightTasks.First(row => (row.Result ?? 1) <= fights);
        var lockedTask = fightTasks.Last(row => (row.Result ?? 1) > fights);
        GuildWarCall(actor, "FinishTaskRequest", new Dictionary<string, object> { ["TaskId"] = lockedTask.Id }, false);
        beforeGoods = Inventory.collection.Find(row => row.Uid == uid).Single().Items.ToDictionary(item => item.Id, item => item.Count);
        JObject taskClaim = GuildWarCall(actor, "FinishTaskRequest", new Dictionary<string, object> { ["TaskId"] = earnedTask.Id });
        foreach (var expected in GuildWarRewardGoods(earnedTask.RewardId!.Value).GroupBy(goods => goods.TemplateId))
            GuildAssert(((JArray)taskClaim["RewardGoodsList"]!).Where(goods => goods.Value<int>("TemplateId") == expected.Key)
                    .Sum(goods => goods.Value<long>("Count")) == expected.Sum(goods => (long)goods.Count),
                "Task claim response must include its authored goods rather than an empty success");
        foreach (var goods in ((JArray)taskClaim["RewardGoodsList"]!).GroupBy(goods => goods.Value<int>("TemplateId")))
            GuildAssert(GuildWarBalance(actor, goods.Key) == beforeGoods.GetValueOrDefault(goods.Key) + goods.Sum(row => row.Value<long>("Count")),
                "An earned war task must pay its actual configured reward into persisted inventory");
        balances = Inventory.collection.Find(row => row.Uid == uid).Single().Items.OrderBy(item => item.Id).Select(item => item.Count).ToArray();
        GuildWarCall(actor, "FinishTaskRequest", new Dictionary<string, object> { ["TaskId"] = earnedTask.Id }, false);
        GuildAssert(GuildWarPlayer(actor).GuildState.War.TaskReceipts.Count(receipt => receipt.Season == season && receipt.TaskId == earnedTask.Id) == 1
            && Inventory.collection.Find(row => row.Uid == uid).Single().Items.OrderBy(item => item.Id).Select(item => item.Count).SequenceEqual(balances),
            "War task retries must preserve exactly one seasonal receipt and no duplicate grant");
        for (int rankType = 1; rankType <= 8; rankType++)
        {
            int target = rankType switch
            {
                1 => 0,
                2 or 6 => first.RoundId,
                4 => first.Monsters.First().Uid,
                8 => first.Reinforcements.First().Uid,
                _ => boss
            };
            JObject response = GuildWarCall(actor, "GuildWarOpenRankRequest",
                new Dictionary<string, object> { ["RankType"] = rankType, ["Uid"] = target });
            JArray ranks = (JArray)response["RankList"]!;
            GuildAssert(ranks.All(row => rankType == 1
                ? Guild.FindById(checked((uint)row.Value<long>("Uid"))) is { Active: true }
                : guild.MemberIds.Contains(row.Value<long>("Uid"))),
                "Rank view " + rankType + " must contain only persisted local guilds or actual guild members");
            if (rankType == 2)
                GuildAssert(ranks.Count == 2 && ranks[0].Value<long>("Uid") == uid
                    && ranks[0].Value<long>("Point") == GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point,
                    "Member ranking must order and project real accumulated battle points");
            if (rankType is 6 or 7)
                GuildAssert(ranks.Count == 0, "No authored hidden/defense participation means no fabricated ranking rows");
        }
        GuildWarCall(actor, "GuildWarOpenRankRequest", new Dictionary<string, object> { ["RankType"] = 0, ["Uid"] = 0 }, false);
        GuildWarCall(actor, "GuildWarOpenRankRequest", new Dictionary<string, object> { ["RankType"] = 3, ["Uid"] = int.MaxValue }, false);

        int nextDifficulty = TableReaderV2.Parse<GuildWarDifficultyTable>()
            .First(row => row.Id != first.DifficultyId && row.PreId.GetValueOrDefault() == 0).Id;
        GuildWarCall(actor, "GuildWarSelectDifficultyRequest", new Dictionary<string, object> { ["DifficultyId"] = nextDifficulty });
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Single().DifficultyId == first.DifficultyId,
            "Changing next difficulty cannot rewrite an in-progress map");
        long points = GuildWarPlayer(actor).GuildState.War.Rounds.Single().Point;
        int rewardCurrency = GuildWarSetting("RewardItemId");
        long currency = GuildWarBalance(actor, rewardCurrency);
        now = DateTimeOffset.FromUnixTimeSeconds(first.EndsAt - 1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(!Guild.FindById(guildId)!.War.Rounds.Single().Settled, "A round remains live one second before its boundary");
        now = now.AddSeconds(1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        guild = Guild.FindById(guildId)!;
        GuildAssert(guild.War.Rounds.Single(row => row.RoundId == first.RoundId).Settled
            && guild.War.Rounds.Single(row => row.RoundId == guild.War.RoundId).DifficultyId == nextDifficulty,
            "The exact round boundary archives the old map and applies the officer's next difficulty");
        GuildWarSettlement summary = GuildWarPlayer(actor).GuildState.War.Settlements.Single(row => row.Season == season && row.RoundId == first.RoundId);
        GuildAssert(summary.PlayerPoints == points && summary.PlayerActivation > 0 && summary.IsPass == 1
            && summary.CurMember.Order().SequenceEqual(guild.MemberIds.Select(id => checked((uint)id)).Order()),
            "Round settlement freezes actual contributor points, clear state, and participating members");
        GuildAssert(GuildWarBalance(actor, rewardCurrency) == currency,
            "Current EN round templates author no automatic pass reward; rollover must not invent currency");
        LoopbackSessionHarness reloaded = scope.OpenPlayer(uid);
        GuildWarCall(reloaded, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarPlayer(reloaded).GuildState.War.Settlements.Count(row => row.Season == season && row.RoundId == first.RoundId) == 1,
            "A second socket and repeated refresh must resume the same single durable round settlement");
        JObject popup = GuildWarCall(reloaded, "GuildWarPopupRequest", new Dictionary<string, object>());
        GuildAssert(((JArray)popup["PopupRecord"]!["SettleDatas"]!).Single(row => row.Value<int>("RoundId") == first.RoundId)
            .Value<long>("PlayerPoints") == points, "Settlement popup must project archived personal points after relog");
        GuildWarRoundState second = guild.War.Rounds.Single(row => row.RoundId == guild.War.RoundId);
        GuildAssert(second.Nodes.Select(node => node.NodeId).Order().SequenceEqual(
            nodes.Values.Where(row => row.DifficultyId == nextDifficulty).Select(row => row.Id).Order()),
            "A second difficulty must independently derive its own map instead of reusing the first map payload");
        now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 14 * 86400);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(Guild.FindById(guildId)!.War.Rounds.Count == 3, "Third-round boundary must preserve two archived rounds and open the third");
        now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 21 * 86400);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        guild = Guild.FindById(guildId)!;
        GuildAssert(guild.War.RoundId == 0 && guild.War.SeasonSettled && guild.War.Rounds.All(row => row.Settled)
            && guild.War.FinalRankPercent is >= 1 and <= 100, "The fourth week is results/rest with a real local season rank");
        GuildWarCall(actor, "GuildWarMoveRequest", new Dictionary<string, object> { ["CurNodeId"] = 0, ["NextNodeId"] = boss }, false);
        int settlements = GuildWarPlayer(actor).GuildState.War.Settlements.Count;
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarPlayer(actor).GuildState.War.Settlements.Count == settlements
            && GuildWarBalance(actor, rewardCurrency) == currency, "Repeated season settlement cannot create receipts or rewards");
        now = DateTimeOffset.FromUnixTimeSeconds(seasonStart + 28 * 86400);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildWarCall(peer, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        guild = Guild.FindById(guildId)!;
        GuildWarParticipation next = GuildWarPlayer(actor).GuildState.War;
        GuildAssert(guild.War.Season == season + 1 && guild.War.Rounds.Count == 1 && guild.War.RoundId != 0
            && next.Season == season + 1 && next.Rounds.Single().Point == 0 && next.Rounds.Single().BossRewardIds.Count == 0
            && next.SupportCharacterId == 0 && next.Rounds.Single().PendingFight is null,
            "New-season rollover must reset participation, reward eligibility, support, and pending combat without changing membership");
        GuildAssert(next.Settlements.Count == settlements, "New season must retain durable historical settlement receipts");
        GuildAssert(next.PlayedActionIds.SequenceEqual(new[] { acknowledgedAction })
            && next.TaskReceipts.Count(receipt => receipt.Season == season && receipt.TaskId == earnedTask.Id) == 1,
            "Season reset must retain action acknowledgements and historical task grant receipts");
        int apItem = GuildWarSetting("ActivityPointItemId");
        DateTimeOffset local = GuildWarGameDate(now);
        DateTimeOffset refresh = new(local.Date.AddHours(20), local.Offset);
        if (refresh <= now) refresh = refresh.AddDays(1);
        GuildWarSetBalance(actor, apItem, 0);
        now = refresh.AddSeconds(-1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarBalance(actor, apItem) == 0, "Daily AP must not replenish before the configured 20:00 boundary");
        now = refresh;
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarBalance(actor, apItem) == GuildWarSetting("AddEnergy"), "At 20:00 the new season grants exactly the authored daily AP");
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarBalance(actor, apItem) == GuildWarSetting("AddEnergy"), "Repeated daily refresh must not duplicate AP");
        GuildWarSetBalance(actor, apItem, GuildWarSetting("MaxEnergy") - 1);
        now = refresh.AddDays(1);
        GuildWarCall(actor, "GuildWarGetActivityDataRequest", new Dictionary<string, object>());
        GuildAssert(GuildWarBalance(actor, apItem) == GuildWarSetting("MaxEnergy"), "Daily AP must clamp at the authored maximum");
    }

    private static int GuildWarVerifySupport(LoopbackSessionHarness actor, LoopbackSessionHarness peer,
        LoopbackSessionHarness outsider, ref DateTimeOffset now)
    {
        long uid = actor.Session.player.PlayerData.Id;
        long peerUid = peer.Session.player.PlayerData.Id;
        int ownId = checked((int)actor.Session.character.Characters.First().Id);
        var peerRow = TableReaderV2.Parse<AscNet.Table.V2.share.character.CharacterTable>()
            .First(row => row.Id != ownId && row.DefaultNpcFashtionId > 0);
        peer.Session.character.Characters.Add(CreateLoginAccountCompatibilityCharacter(
            checked((uint)peerRow.Id), checked((uint)peerRow.DefaultNpcFashtionId)));
        peer.Session.character.SaveChecked();
        GuildWarCall(peer, "GuildWarSupportCharacterRequest", new GuildWarSupportCharacterRequest { CharacterId = int.MaxValue }, false);
        GuildWarCall(peer, "GuildWarSupportCharacterRequest", new GuildWarSupportCharacterRequest { CharacterId = peerRow.Id });
        GuildWarCall(peer, "GuildWarSupportCharacterRequest", new GuildWarSupportCharacterRequest { CharacterId = peerRow.Id }, false);
        GuildAssert(GuildWarPlayer(peer).GuildState.War.SupportTimes.Count == 1, "Duplicate publication must not restart support accrual");
        Guild supportGuild = Guild.FindByMember(peerUid)!;
        supportGuild.Members[peerUid].Rank = 5;
        supportGuild.SaveChecked();
        GuildWarAssertEmptySupport(peer);
        GuildWarCall(peer, "GuildWarSupportCharacterRequest", new GuildWarSupportCharacterRequest { CharacterId = peerRow.Id }, false);
        GuildWarCall(peer, "GuildWarEndSupportRequest", new GuildWarEndSupportRequest { CharacterId = peerRow.Id }, false);
        GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest(), false);
        supportGuild = Guild.FindByMember(peerUid)!;
        supportGuild.Members[peerUid].Rank = 4;
        supportGuild.SaveChecked();
        JObject detail = (JObject)GuildWarCall(peer, "GuildWarOpenSupportPanelRequest", new GuildWarOpenSupportPanelRequest())["SupportDetail"]!;
        GuildAssert(detail.Value<int>("CharacterId") == peerRow.Id
            && ((JArray)detail["MyAssistRecords"]!).Single().Value<long>("AssistTime") == now.ToUnixTimeSeconds()
            && ((JArray)detail["ToAssistRecords"]!).Single().Value<int>("RoundId") == GuildWarPlayer(peer).GuildState.War.Rounds.Single().RoundId,
            "Full-member support query must return the real published character, interval, and current round");
        JObject listed = GuildWarCall(actor, "GuildWarAssistCharacterListRequest", new GuildWarAssistCharacterListRequest());
        JArray assistants = (JArray)listed["CharacterList"]!;
        GuildAssert(assistants.Count == 1 && assistants[0].Value<long>("PlayerId") == peerUid
            && assistants[0]["FightNpcData"]!["Character"]!.Value<int>("Id") == peerRow.Id,
            "Support list must project the real published guild peer and owned character, not self or outsiders");
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
        {
            ["TeamInfo"] = GuildWarTeam(uid, ownId, outsider.Session.player.PlayerData.Id, peerRow.Id)
        }, false);
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
        {
            ["TeamInfo"] = GuildWarTeam(uid, ownId, peerUid, ownId)
        }, false);
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
        {
            ["TeamInfo"] = GuildWarTeam(uid, ownId, peerUid, peerRow.Id)
        });
        GuildWarTeamInfo stored = GuildWarPlayer(actor).GuildState.War.Rounds.Last().TeamInfo!;
        GuildAssert(stored.CharacterInfos.Single(slot => slot.Pos == 2).PlayerId == peerUid,
            "Accepted support team must survive a Mongo reload");

        int currency = GuildWarSetting("RewardItemId");
        long before = GuildWarBalance(peer, currency);
        now = now.AddSeconds(599);
        GuildAssert(GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest()).Value<int>("TotalSupply") == 0,
            "Support time reward must not round 599 seconds up");
        now = now.AddSeconds(1);
        int quantum = GuildWarSetting("AssistTimeSupply");
        GuildAssert(GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest()).Value<int>("TotalSupply") == quantum,
            "An early claim must preserve the remainder through the 600-second boundary");
        GuildAssert(GuildWarBalance(peer, currency) == before + quantum, "Support claim must grant the authored currency amount");
        GuildAssert(GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest()).Value<int>("TotalSupply") == 0
            && GuildWarBalance(peer, currency) == before + quantum, "Same-clock support claim must not pay twice");
        GuildWarCall(peer, "GuildWarEndSupportRequest", new GuildWarEndSupportRequest { CharacterId = ownId }, false);
        GuildWarCall(peer, "GuildWarEndSupportRequest", new GuildWarEndSupportRequest { CharacterId = peerRow.Id });
        GuildAssert(GuildWarPlayer(peer).GuildState.War.SupportTimes.Single().EndTime == now.ToUnixTimeSeconds(),
            "Cancelling support must durably close its earning interval");
        GuildWarCall(peer, "GuildWarEndSupportRequest", new GuildWarEndSupportRequest { CharacterId = peerRow.Id }, false);
        GuildAssert(((JArray)GuildWarCall(actor, "GuildWarAssistCharacterListRequest",
            new GuildWarAssistCharacterListRequest())["CharacterList"]!).Count == 0, "Cancelled support must disappear for another member");
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
        {
            ["TeamInfo"] = GuildWarTeam(uid, ownId, peerUid, peerRow.Id)
        }, false);
        GuildWarCall(peer, "GuildWarSupportCharacterRequest", new GuildWarSupportCharacterRequest { CharacterId = peerRow.Id });
        GuildWarCall(actor, "GuildWarSetTeamRequest", new Dictionary<string, object>
        {
            ["TeamInfo"] = GuildWarTeam(uid, ownId, peerUid, peerRow.Id)
        });
        foreach (string name in new[] { "GuildWarSetHideAreaTeamRequest", "GuildWarResetHideAreaTeamRequest", "GuildWarUploadHideNodePointRequest" })
        {
            JObject response = GuildWarCall(actor, name, new Dictionary<string, object>(), false);
            GuildAssert(response.Value<int>("Code") == 20164051, "Current authored map has no hidden-area stages; " + name + " must not fabricate one");
        }
        Player capped = GuildWarPlayer(peer);
        int limit = GuildWarSetting("AssistTimeSupplyLimit");
        capped.GuildState.War.Rounds.Single().ReceivedTimeSupply = limit - quantum;
        capped.GuildState.War.SupportTimes = [new GuildWarAssistTime { AssistTime = now.ToUnixTimeSeconds() - 1200 }];
        capped.GuildState.War.SupportLastRecvTime = now.ToUnixTimeSeconds() - 1200;
        capped.SaveChecked();
        peer.Session.player = capped;
        GuildAssert(GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest()).Value<int>("TotalSupply") == quantum
            && GuildWarPlayer(peer).GuildState.War.Rounds.Single().ReceivedTimeSupply == limit,
            "Support rewards must clamp an eligible two-quantum claim to the remaining authored round cap");
        GuildAssert(GuildWarCall(peer, "GuildWarReceivedSupportRequest", new GuildWarReceivedSupportRequest()).Value<int>("TotalSupply") == 0,
            "The support-time cap cannot be bypassed by another packet");
        return peerRow.Id;
    }

    private static JObject GuildWarCall(LoopbackSessionHarness actor, string name, object request, bool success = true)
    {
        JObject response = GuildRpc(actor, name, request);
        GuildAssert(response["Code"]?.Type == JTokenType.Integer, name + " must return an integer Code");
        GuildAssert((response.Value<int>("Code") == 0) == success, name + " unexpected result: " + response);
        return response;
    }

    private static Dictionary<string, object> GuildWarTeam(long uid, int characterId, long assistantUid = 0, int assistantId = 0) => new()
    {
        ["CaptainPos"] = 1,
        ["FirstFightPos"] = 1,
        ["CharacterInfos"] = new[]
        {
            new Dictionary<string, object> { ["Id"] = characterId, ["PlayerId"] = uid, ["RobotId"] = 0, ["Pos"] = 1 },
            new Dictionary<string, object> { ["Id"] = assistantId, ["PlayerId"] = assistantUid, ["RobotId"] = 0, ["Pos"] = 2 },
            new Dictionary<string, object> { ["Id"] = 0, ["PlayerId"] = 0L, ["RobotId"] = 0, ["Pos"] = 3 }
        }
    };

    private static Player GuildWarPlayer(LoopbackSessionHarness actor) =>
        Player.collection.Find(row => row.PlayerData.Id == actor.Session.player.PlayerData.Id).Single();

    private static long GuildWarBalance(LoopbackSessionHarness actor, int itemId) =>
        Inventory.collection.Find(row => row.Uid == actor.Session.player.PlayerData.Id).Single()
            .Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;

    private static void GuildWarSetBalance(LoopbackSessionHarness actor, int itemId, long count)
    {
        Inventory inventory = Inventory.collection.Find(row => row.Uid == actor.Session.player.PlayerData.Id).Single();
        Item? item = inventory.Items.SingleOrDefault(row => row.Id == itemId);
        if (item is null) inventory.Items.Add(new Item { Id = itemId, Count = count });
        else item.Count = count;
        inventory.SaveChecked();
        actor.Session.inventory = inventory;
    }

    private static void GuildWarAssertUnaffiliated(LoopbackSessionHarness actor)
    {
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"), "SendEmptyStartupPush",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session), typeof(string)])
            .Invoke(null, [actor.Session, nameof(NotifyGuildWarActivityData)]);
        AscNet.GameServer.Packet packet = actor.ReadPacket("guildless login GuildWar notification");
        GuildAssert(packet.Type == AscNet.GameServer.Packet.ContentType.Push, "GuildWar startup must emit a push");
        var push = MessagePack.MessagePackSerializer.Deserialize<AscNet.GameServer.Packet.Push>(packet.Content);
        GuildAssert(push.Name == nameof(NotifyGuildWarActivityData), "GuildWar startup notification name");
        JObject wire = JObject.Parse(MessagePack.MessagePackSerializer.ConvertToJson(push.Content));
        GuildAssert(wire["ActivityData"] is JObject activity && activity["ActionList"] is JArray,
            "Guildless startup must retain non-nil ActivityData and ActionList consumed by the client before its automatic support query");
        GuildWarAssertEmptySupport(actor);
        Player stale = GuildWarPlayer(actor);
        uint missingGuildId = int.MaxValue;
        while (Guild.collection.Find(row => row.Id == missingGuildId).Any()) missingGuildId--;
        stale.GuildState.War = new GuildWarParticipation
        {
            GuildId = missingGuildId, Season = -1, SupportCharacterId = checked((int)actor.Session.character.Characters.First().Id),
            SupportLastRecvTime = 100,
            SupportTimes = [new GuildWarAssistTime { AssistTime = 50 }],
            SupportLogs = [new GuildWarAssistLog { UserId = actor.Session.player.PlayerData.Id, UserTime = 75, Supply = 13 }],
            Rounds = [new GuildWarPlayerRound { GuildId = missingGuildId, RoundId = 1, SupportSupply = 13, ReceivedAssistSupply = 7, ReceivedTimeSupply = 11 }]
        };
        stale.SaveChecked();
        actor.Session.player = GuildWarPlayer(actor);
        GuildWarAssertEmptySupport(actor);
        GuildWarAssertEmptySupport(actor);
        foreach (string request in new[]
        {
            "GuildWarEditLineRequest", "GuildWarSelectDifficultyRequest", "GuildWarConfirmFightResultRequest",
            "GuildWarSweepRequest", "GuildWarMoveRequest", "GuildWarSupportCharacterRequest",
            "GuildWarEndSupportRequest", "GuildWarSetTeamRequest", "GuildWarCanMoveRequest",
            "GuildWarReceivedSupportRequest", "GuildWarSetHideAreaTeamRequest", "GuildWarResetHideAreaTeamRequest",
            "GuildWarUploadHideNodePointRequest", "XGuildWarGetBossRewardRequest",
            "GuildWarSupportReinforcementRequest", "GuildWarCancelSupportReinforcementRequest",
            "XGuildWarSelectDefenseNodeRequest", "XGuildWarBeStationedRequest"
        })
        {
            GuildWarCall(actor, request, new Dictionary<string, object>(), false);
            GuildAssert(Guild.FindByMember(actor.Session.player.PlayerData.Id) is null,
                request + " must not manufacture membership");
        }
    }

    private static void GuildWarAssertEmptySupport(LoopbackSessionHarness actor)
    {
        long uid = actor.Session.player.PlayerData.Id;
        byte[] player = GuildWarPlayer(actor).ToBson();
        byte[] sessionPlayer = actor.Session.player.ToBson();
        byte[] inventory = Inventory.collection.Find(row => row.Uid == uid).Single().ToBson();
        byte[] sessionInventory = actor.Session.inventory.ToBson();
        byte[]? guild = Guild.FindByMember(uid)?.ToBson();
        JObject response = GuildWarCall(actor, "GuildWarOpenSupportPanelRequest", new GuildWarOpenSupportPanelRequest());
        GuildAssert(response["SupportDetail"] is JObject detail
            && detail["CharacterId"]?.Type == JTokenType.Integer && detail.Value<int>("CharacterId") == 0
            && detail["SupportSupply"]?.Type == JTokenType.Integer && detail.Value<int>("SupportSupply") == 0
            && detail["LastRecvTime"]?.Type == JTokenType.Integer && detail.Value<long>("LastRecvTime") == 0
            && new[] { "ToAssistRecords", "GetAssistRecords", "MyAssistRecords", "MyLogs" }
                .All(field => detail[field] is JArray { Count: 0 }),
            "Startup support query must succeed with empty detail, never disclose stale support history");
        GuildAssert(player.SequenceEqual(GuildWarPlayer(actor).ToBson())
            && sessionPlayer.SequenceEqual(actor.Session.player.ToBson())
            && inventory.SequenceEqual(Inventory.collection.Find(row => row.Uid == uid).Single().ToBson())
            && sessionInventory.SequenceEqual(actor.Session.inventory.ToBson()),
            "Guildless and tourist support queries must not mutate persisted or session player/economy state");
        Guild? after = Guild.FindByMember(uid);
        GuildAssert(guild is null ? after is null : after is not null && guild.SequenceEqual(after.ToBson()),
            "Support query must neither manufacture membership nor mutate an existing tourist guild");
    }
}
