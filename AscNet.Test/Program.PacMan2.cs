using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.miniactivity.pacman2;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    public static void ValidatePacMan2Compatibility()
    {
        foreach (string name in new[] { "PacMan2StageStartRequest", "PacMan2SettleRequest" })
            AssertEqual("AscNet.GameServer.Handlers.PacMan2Module", GetRegisteredRequestHandlerMethod(name).DeclaringType?.FullName,
                $"{name} registered handler");
        // EN CodeText ids, asserted as literals so a remap is caught.
        AssertEqual("20251001,20251002,20251003,20251004,20251005,20251006,20251007,20251008",
            string.Join(",", new[] { PacMan2Module.ActivityNotOpen, PacMan2Module.StageNotOpen, PacMan2Module.StageConfigNotFound,
                PacMan2Module.PreStageNotComplete, PacMan2Module.StageGroupError, PacMan2Module.SettleStageIdError,
                PacMan2Module.SettleScoreError, PacMan2Module.SettleTypeError }), "PacMan2 CodeText ids");

        PacMan2ActivityTable[] activities = TableReaderV2.Parse<PacMan2ActivityTable>().ToArray();
        Dictionary<int, PacMan2StageTable> stages = TableReaderV2.Parse<PacMan2StageTable>().ToDictionary(row => row.Id);
        PacMan2ActivityTable current = activities.OrderByDescending(row => row.Id).First();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (int timeId in stages.Values.Select(row => row.TimeId).Append(current.TimeId).Where(id => id > 0).Distinct())
            AssertEqual(true, ActivityScheduleService.IsOpen(timeId, now), $"PacMan2 TimeId {timeId} open by AscNet calendar policy");
        AssertEqual(current.Id, PacMan2Module.ActiveActivity(now)?.Id, "newest open PacMan2 activity is selected");

        // Stage chain of the current activity's group: first, second, third and a stage whose PreStageId is the second.
        PacMan2StageTable[] chain = stages.Values.Where(row => row.GroupId == current.GroupId).OrderBy(row => row.GroupOrder).ToArray();
        PacMan2StageTable s1 = chain[0], s2 = chain[1], s3 = chain[2];
        PacMan2StageTable foreign = stages.Values.First(row => row.GroupId != current.GroupId && row.PreStageId == 0);
        AssertEqual(0, s1.PreStageId, "first stage has no prerequisite");
        AssertEqual(s1.Id, s2.PreStageId, "second stage requires first");
        AssertEqual(s2.Id, s3.PreStageId, "third stage requires second");

        const long playerId = 99_249;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventorySaves);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "pacman2");
        long Count(int id) => harness.Session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        int packetId = 900;
        T Call<T>(string name, object request)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push) continue;
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                AssertEqual(name.Replace("Request", "Response"), wire.Name, $"{name} response name");
                return MessagePackSerializer.Deserialize<T>(wire.Content);
            }
        }
        int Start(int stageId) => Call<PacMan2StageStartResponse>("PacMan2StageStartRequest", new PacMan2StageStartRequest { StageId = stageId }).Code;
        PacMan2SettleResponse Settle(PacMan2StageTable stage, int score, int type = 1, Action<PacMan2SettleRequest>? edit = null)
        {
            PacMan2SettleRequest request = new()
            {
                StageId = stage.Id, Score = score, SettleType = type, OperateType = 1, Time = 0, Orbs = stage.RedOrb, Kills = 0,
                Hp = stage.InitialHp,
                Shoe = [new PacMan2ShoeData { Id = 6, Times = 0 }]
            };
            edit?.Invoke(request);
            return Call<PacMan2SettleResponse>("PacMan2SettleRequest", request);
        }
        Dictionary<int, long> Expected(params int[] rewardIds) => rewardIds
            .SelectMany(id => RewardHandler.GetRewardGoods(id)).GroupBy(row => row.TemplateId)
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)row.Count));
        void AssertGain(Dictionary<int, long> before, Dictionary<int, long> gain, string name)
        {
            foreach ((int templateId, long count) in gain)
                AssertEqual(before.GetValueOrDefault(templateId) + count, Count(templateId), $"{name} item {templateId}");
        }
        Dictionary<int, long> Snapshot(params int[] ids) => ids.Distinct().ToDictionary(id => id, Count);

        // Login push: activity id and (empty) records.
        NotifyPacMan2Activity login = PacMan2Module.BuildLoginData(harness.Session.player, now);
        AssertEqual(current.Id, login.ActivityId, "login push activity id");
        AssertEqual(0, login.StageRecords.Count, "fresh player has no stage records");

        // Start boundaries.
        AssertEqual(PacMan2Module.StageConfigNotFound, Start(-5), "unknown stage rejected");
        AssertEqual(PacMan2Module.StageGroupError, Start(foreign.Id), "stage of another group rejected");
        AssertEqual(PacMan2Module.PreStageNotComplete, Start(s2.Id), "locked stage rejected");
        AssertEqual(0, playerSaves.ReplaceOneCalls, "rejected starts persist nothing");
        AssertEqual(PacMan2Module.SettleStageIdError, Settle(s1, s1.Star[0]).Code, "settle without a started run rejected");

        // Settle boundaries on a started run; none of them consumes the run or grants anything.
        AssertEqual(0, Start(s1.Id), "first stage starts");
        AssertEqual(s1.Id, harness.Session.player.PacMan2!.PlayingStageId, "started run persisted on player");
        long saves = playerSaves.ReplaceOneCalls;
        Dictionary<int, long> bagBefore = Snapshot(Expected(s1.KeyReward, s1.StarReward[0], s1.StarReward[1], s1.StarReward[2]).Keys.ToArray());
        AssertEqual(PacMan2Module.SettleStageIdError, Settle(s2, 1).Code, "settle for a different stage rejected");
        AssertEqual(PacMan2Module.SettleTypeError, Settle(s1, s1.Star[0], 7).Code, "unknown settle type rejected");
        AssertEqual(PacMan2Module.SettleScoreError, Settle(s1, -1).Code, "negative score rejected");
        AssertEqual(PacMan2Module.SettleScoreError, Settle(s1, s1.Star[0], 1, r => r.Time = 99_999).Code, "play time beyond server elapsed rejected");
        AssertEqual(PacMan2Module.SettleScoreError, Settle(s1, s1.Star[0], 1, r => r.Hp = s1.InitialHp + 1).Code, "hp above authored initial rejected");
        AssertEqual(PacMan2Module.SettleScoreError, Settle(s1, s1.Star[0], 1, r => r.Hp = 0).Code, "cleared run with zero hp rejected");
        AssertEqual(saves, playerSaves.ReplaceOneCalls, "rejected settles persist nothing");
        AssertEqual(s1.Id, harness.Session.player.PacMan2!.PlayingStageId, "rejected settles keep the run");
        AssertGain(bagBefore, new Dictionary<int, long>(), "rejected settles grant nothing");
        AssertEqual(0, harness.Session.player.PacMan2!.Records.Count, "rejected settles record no score");

        // Below the first star: cleared but not passed, no rewards, score still recorded.
        PacMan2SettleResponse low = Settle(s1, s1.Star[0] - 1);
        AssertEqual(0, low.Code, "sub-star clear accepted");
        AssertEqual(s1.Star[0] - 1, low.StageData!.HighestScore, "sub-star score recorded");
        AssertEqual(0, low.RewardGoodsList!.Count, "sub-star clear rewards nothing");
        AssertEqual(PacMan2Module.PreStageNotComplete, Start(s2.Id), "sub-star clear does not unlock the next stage");
        AssertEqual(PacMan2Module.SettleStageIdError, Settle(s1, s1.Star[0]).Code, "settle consumes the run");

        // First star: key reward + first star reward, once.
        AssertEqual(0, Start(s1.Id), "restart first stage");
        PacMan2SettleResponse one = Settle(s1, s1.Star[0]);
        AssertEqual(0, one.Code, "first-star clear accepted");
        AssertEqual(s1.Star[0], one.StageData!.HighestScore, "first-star score");
        AssertGain(bagBefore, Expected(s1.KeyReward, s1.StarReward[0]), "first star key+star1 reward");
        AssertEqual(Expected(s1.KeyReward, s1.StarReward[0]).Values.Sum(), one.RewardGoodsList!.Sum(row => (long)row.Count), "response lists granted goods");
        Dictionary<int, long> afterOne = Snapshot(bagBefore.Keys.ToArray());

        // Better score: only the new stars pay (distinct input -> distinct reward set); lower score changes nothing.
        AssertEqual(0, Start(s1.Id), "replay first stage");
        PacMan2SettleResponse three = Settle(s1, s1.Star[2] + 5);
        AssertEqual(s1.Star[2] + 5, three.StageData!.HighestScore, "best score raised");
        AssertGain(afterOne, Expected(s1.StarReward[1], s1.StarReward[2]), "stars 2 and 3 reward");
        Dictionary<int, long> afterThree = Snapshot(bagBefore.Keys.ToArray());
        AssertEqual(0, Start(s1.Id), "replay again");
        PacMan2SettleResponse worse = Settle(s1, s1.Star[0]);
        AssertEqual(s1.Star[2] + 5, worse.StageData!.HighestScore, "lower score keeps the best");
        AssertEqual(0, worse.RewardGoodsList!.Count, "repeat clear grants nothing");
        AssertGain(afterThree, new Dictionary<int, long>(), "repeat clear grants nothing to the bag");

        // Progression: stage 2 unlocked and pays its own rewards; an abort never records or pays.
        AssertEqual(0, Start(s2.Id), "second stage unlocked");
        Dictionary<int, long> beforeTwo = Snapshot(Expected(s2.KeyReward, s2.StarReward[0], s2.StarReward[1], s2.StarReward[2]).Keys.ToArray());
        AssertEqual(0, Settle(s2, s2.Star[1] + 1, PacMan2Module.SettleAbort).Code, "abort accepted");
        AssertEqual(false, harness.Session.player.PacMan2!.Records.Any(row => row.StageId == s2.Id), "abort records no score");
        AssertGain(beforeTwo, new Dictionary<int, long>(), "abort grants nothing");
        AssertEqual(PacMan2Module.PreStageNotComplete, Start(s3.Id), "abort does not unlock the next stage");
        AssertEqual(0, Start(s2.Id), "start second stage again");
        PacMan2SettleResponse two = Settle(s2, s2.Star[1]);
        AssertEqual(s2.Star[1], two.StageData!.HighestScore, "second stage score");
        AssertGain(beforeTwo, Expected(s2.KeyReward, s2.StarReward[0], s2.StarReward[1]), "second stage key+star1+star2 reward");
        AssertEqual(0, Start(s3.Id), "third stage unlocked by the second");

        // Persistence: BSON round trip and a login rebuilt from it.
        PacMan2State reloaded = BsonSerializer.Deserialize<PacMan2State>(harness.Session.player.PacMan2!.ToBson());
        Player fresh = CreateDrawCompatibilityPlayer(playerId + 1);
        fresh.PacMan2 = reloaded;
        NotifyPacMan2Activity relogin = PacMan2Module.BuildLoginData(fresh, now);
        AssertEqual($"{s1.Id}:{s1.Star[2] + 5},{s2.Id}:{s2.Star[1]}",
            string.Join(",", relogin.StageRecords.Select(row => $"{row.StageId}:{row.HighestScore}")), "reloaded login records");
        AssertEqual(s3.Id, reloaded.PlayingStageId, "in-flight run survives reload");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0 && inventorySaves.ReplaceOneCalls > 0, "player and rewards persisted");
    }
}
