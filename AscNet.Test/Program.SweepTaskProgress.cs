using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.repeatchallenge;
using AscNet.Table.V2.share.task;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateSweepTaskProgressCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out _, out _, out _);
            const long playerId = 88_064;
            RepeatChallengeActivityTable activity = TableReaderV2.Parse<RepeatChallengeActivityTable>().OrderBy(row => row.Id).First();
            int stageId = TableReaderV2.Parse<RepeatChallengeChapterTable>().Single(row => row.Id == activity.NormalChapter).StageId;
            StageTable stage = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == stageId);
            int cost = stage.RequireActionPoint!.Value;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.SimulatedBattlefield = new()
            {
                RepeatChallengeActivityId = activity.Id,
                RepeatChallengeCleared = true,
                RepeatChallengeLevel = TableReaderV2.Parse<RepeatChallengeLevelTable>().Count(),
                RepeatChallengeExp = TableReaderV2.Parse<RepeatChallengeLevelTable>().Sum(row => row.UpExp)
            };
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.ActionPoint, Count = cost * 3 }]);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "sweep-task-progress-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            CurrentConditionTable clears = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 15201 && row.Params.Count == 1);
            CurrentConditionTable serum = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 11202 && row.Params[1] == Inventory.ActionPoint);
            CurrentConditionTable coin = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 11202 && row.Params[1] == Inventory.Coin);
            int packetId = 88_640;
            foreach (int count in new[] { 2, 1, 1, 0 })
            {
                int previous = player.MissionProgress.ConditionCounters.GetValueOrDefault(clears.Id);
                bool succeeds = count > 0 && previous + count <= 3;
                InvokeRegisteredRequestHandler("SweepRequest", harness.Session, ++packetId, new SweepRequest { StageId = stageId, Count = count });
                SweepResponse response = (SweepResponse)ReadResponsePayload(harness, packetId, nameof(SweepResponse),
                    "sweep task progression", typeof(SweepResponse), maxPacketsToRead: 64);
                AssertEqual(succeeds ? 0 : 1, response.Code, "sweep affordability and positive count boundary");
                int total = previous + (succeeds ? count : 0);
                AssertEqual(total, player.MissionProgress.ConditionCounters.GetValueOrDefault(clears.Id), "sweep counts only committed clears");
                AssertEqual(total * cost, player.MissionProgress.ConditionCounters.GetValueOrDefault(serum.Id), "sweep records actual serum cost");
                AssertEqual(0, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "sweep does not count coin spending");
            }
        }
    }
}
