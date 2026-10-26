using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.boardeffect;
using AscNet.Table.V2.share.miniactivity.stagememory;
using AscNet.Table.V2.share.newbirthdayplot;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateFavorMemoryCompatibility()
    {
        const long playerId = 99_491;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "favor-memory");
        int id = 99_491_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }
        void Reload() { player = BsonSerializer.Deserialize<Player>(player.ToBson()); harness.Session.player = player; }

        // Birthday single story: config, birthday-set gate, persistence, idempotence, two distinct stories.
        SingleStoryTable[] stories = TableReaderV2.Parse<SingleStoryTable>().Take(2).ToArray();
        AssertEqual(20154003, Call<ViewBirthdaySingleStoryResponse>("ViewBirthdaySingleStory", new ViewBirthdaySingleStoryRequest { StoryId = int.MaxValue }).Code, "unknown story");
        AssertEqual(20154002, Call<ViewBirthdaySingleStoryResponse>("ViewBirthdaySingleStory", new ViewBirthdaySingleStoryRequest { StoryId = stories[0].Id }).Code, "no birthday set");
        AssertEqual(0, player.BirthdaySingleStoryIds.Count, "rejected views persist nothing");
        player.PlayerData.Birthday = new Birthday { Mon = 3, Day = 9 };
        foreach (SingleStoryTable story in new[] { stories[0], stories[0], stories[1] })
            AssertEqual(0, Call<ViewBirthdaySingleStoryResponse>("ViewBirthdaySingleStory", new ViewBirthdaySingleStoryRequest { StoryId = story.Id }).Code, "story viewed");
        Reload();
        AssertEqual(true, FavorMemoryModule.BirthdayStoryIds(player).SequenceEqual(stories.Select(s => s.Id)), "viewed stories survive reload exactly once");

        // Board effect: cooldown from table, persisted trigger time.
        BoardEffectActivityTable board = TableReaderV2.Parse<BoardEffectActivityTable>().OrderByDescending(r => r.Id).First();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BoardEffectTriggerResponse first = Call<BoardEffectTriggerResponse>("BoardEffectTrigger", new { });
        AssertEqual(0, first.Code, "first trigger");
        AssertEqual(true, first.LastTriggerTime >= before, "trigger time from clock");
        AssertEqual(20234003, Call<BoardEffectTriggerResponse>("BoardEffectTrigger", new { }).Code, "trigger in cooldown");
        Reload();
        AssertEqual(first.LastTriggerTime, player.BoardEffectLastTriggerTime, "trigger time survives reload");
        player.BoardEffectLastTriggerTime = before - board.TriggerCd - 1;
        BoardEffectTriggerResponse second = Call<BoardEffectTriggerResponse>("BoardEffectTrigger", new { });
        AssertEqual(0, second.Code, "trigger after cooldown");
        AssertEqual(true, second.LastTriggerTime >= before, "second trigger time advanced");

        // Stage memory: highest-Id open activity; milestone rewards from RewardIds, once each.
        StageMemoryActivityTable activity = TableReaderV2.Parse<StageMemoryActivityTable>().OrderByDescending(r => r.Id).First();
        AssertEqual(20238005, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 0 }).Code, "index 0 invalid");
        AssertEqual(20238005, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = activity.RewardIds.Count + 1 }).Code, "index past end invalid");
        AssertEqual(20238007, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 1 }).Code, "no stage cleared");
        for (int i = 0; i < 2; i++)
            harness.Session.stage.AddStage(new StageDatum { StageId = activity.StageIds[i], Passed = true });
        StageMemoryGetRewardResponse r1 = Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 1 });
        StageMemoryGetRewardResponse r2 = Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 2 });
        AssertEqual(0, r1.Code, "index 1 granted");
        AssertEqual(0, r2.Code, "index 2 granted");
        foreach ((StageMemoryGetRewardResponse res, int rewardId) in new[] { (r1, activity.RewardIds[0]), (r2, activity.RewardIds[1]) })
            AssertEqual(true, RewardHandler.GetRewardGoods(rewardId).Select(g => (g.TemplateId, g.Count)).OrderBy(x => x)
                .SequenceEqual(res.RewardList.Select(g => (g.TemplateId, g.Count)).OrderBy(x => x)), $"reward {rewardId} from table");
        AssertEqual(20238006, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 1 }).Code, "claim once");
        AssertEqual(20238007, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 3 }).Code, "third needs more clears");
        Reload();
        AssertEqual(20238006, Call<StageMemoryGetRewardResponse>("StageMemoryGetReward", new StageMemoryGetRewardRequest { Index = 2 }).Code, "claim persists after reload");

        // Login pushes carry the persisted state the client reads.
        FavorMemoryModule.SendLoginPushes(harness.Session);
        NotifyBoardEffectData boardPush = ReadPushPayload<NotifyBoardEffectData>(harness, nameof(NotifyBoardEffectData), "board push", 16);
        AssertEqual(board.Id, boardPush.BoardEffectData.ActivityId, "board activity id");
        AssertEqual(player.BoardEffectLastTriggerTime, boardPush.BoardEffectData.LastTriggerTime, "board last trigger");
        NotifyStageMemoryActivity memoryPush = ReadPushPayload<NotifyStageMemoryActivity>(harness, nameof(NotifyStageMemoryActivity), "stage memory push");
        AssertEqual(activity.Id, memoryPush.ActivityId, "stage memory activity id");
        AssertEqual(true, memoryPush.GotRewardIndexSet.OrderBy(x => x).SequenceEqual([1, 2]), "claimed indices pushed");
    }
}
