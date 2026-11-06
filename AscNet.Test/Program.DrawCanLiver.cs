using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.draw;
using MessagePack;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // Lua XUiDrawPanelCanLiverJourneyReward:CheckToShow only treats nil as "no activity"; 0 is truthy and
    // indexes a missing DrawCanLiverActivity row. Every id the server can send must be a table row.
    private static void ValidateDrawCanLiverActivityId()
    {
        List<DrawCanLiverActivityTable> rows = TableReaderV2.Parse<DrawCanLiverActivityTable>().ToList();
        HashSet<int> ids = rows.Select(row => row.Id).ToHashSet();
        MethodInfo builder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DrawModule"),
            "BuildNotifyDrawCanLiverData",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DateTimeOffset)]);
        NotifyDrawCanLiverData Build(long unix) => (NotifyDrawCanLiverData)builder.Invoke(null,
            [CreateDrawCompatibilityPlayer(49_901), DateTimeOffset.FromUnixTimeSeconds(unix)])!;

        // Probe every schedule boundary +-1 plus far past/future, plus the real clock.
        List<long> probes = ActivityScheduleService.All
            .SelectMany(entry => new[] { entry.StartTime - 1, entry.StartTime, entry.EndTime - 1, entry.EndTime })
            .Where(time => time >= 0)
            .Concat([0L, 1L, 32503680000L, DateTimeOffset.UtcNow.ToUnixTimeSeconds()])
            .Distinct().ToList();
        foreach (long time in probes)
        {
            int? id = Build(time).DrawCanLiverData.ActivityId;
            if (id is { } sent && !ids.Contains(sent))
                throw new InvalidDataException($"DrawCanLiver sent ActivityId {sent} at {time} that is not a DrawCanLiverActivity row.");
            if (id == 0)
                throw new InvalidDataException($"DrawCanLiver sent truthy-in-Lua ActivityId 0 at {time}; must be nil.");
        }

        // Past the last authored window nothing is open: nil on the wire, not 0.
        long afterAll = ActivityScheduleService.All.Where(entry => rows.Any(row => row.TimeId == entry.Id)).Max(entry => entry.EndTime);
        NotifyDrawCanLiverData closed = Build(afterAll);
        AssertEqual<int?>(null, closed.DrawCanLiverData.ActivityId, "closed DrawCanLiver ActivityId is nil");
        string wire = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(closed));
        AssertEqual(true, wire.Contains("\"ActivityId\":null") || wire.Contains("\"ActivityId\": null"), "closed DrawCanLiver wire ActivityId is msgpack nil");

        foreach (DrawCanLiverActivityTable row in rows)
        {
            // Rows without a schedule entry (retired 4.4 window) are never open, so never sent.
            if (ActivityScheduleService.All.Any(entry => entry.Id == row.TimeId))
                AssertEqual<int?>(row.Id, Build(ActivityScheduleService.All.Single(entry => entry.Id == row.TimeId).StartTime).DrawCanLiverData.ActivityId, $"DrawCanLiver {row.Id} open at its window start");
            AssertEqual(row.Schedules.Count, row.RewardIds.Count, $"DrawCanLiver {row.Id} schedules match reward ids");
            foreach (int rewardId in row.RewardIds)
                AssertEqual(true, AscNet.GameServer.Handlers.RewardHandler.GetRewardGoods(rewardId).Count > 0, $"DrawCanLiver {row.Id} reward {rewardId} has goods");
        }
    }
}
