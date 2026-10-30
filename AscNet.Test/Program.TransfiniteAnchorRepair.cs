using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Common.MsgPack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // Runs paid before the anchor-skipped stages counted missed their base score; the next login grants it once.
    private static void ValidateTransfiniteAnchorRepair()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        var stages = TableReaderV2.Parse<TransfiniteStageTable>().ToDictionary(row => row.StageId);
        var groups = TableReaderV2.Parse<TransfiniteStageGroupTable>().ToDictionary(row => row.StageGroupId);
        var activeRows = TransfiniteModule.GetAnchoredActivities();
        var (activity, schedule, _) = activeRows.Last();
        long uid = 49_100;

        // Builds a logged-in-before legacy state: the circle's group, the outgoing progress the anchor came from, and a paid terminal claim.
        LoopbackSessionHarness Build(int outgoing, bool viaReceipt, bool paidMarker, bool reset, long modified, string name, out TransfiniteState state, out long now, out int groupId)
        {
            now = 0; groupId = 0; state = null!;
            for (int offset = 1; offset < 20; offset++)
            {
                long at = schedule.StartTime + offset * activity.CycleSeconds + 1;
                Player probe = CreateDrawCompatibilityPlayer(uid);
                probe.PlayerData.Level = 90;
                TransfiniteModule.PrepareLogin(probe, at);
                if (probe.Transfinite is { } s && groups[s.StageGroupId].StageId.Count == 15) { state = s; now = at; groupId = s.StageGroupId; break; }
            }
            AssertEqual(true, state is not null, "found a 15-stage circle");
            state.BattleInfo = null;
            state.RotateSettleInfo = null; state.LastRotateReceipt = null;
            if (viaReceipt) state.LastRotateReceipt = new() { RotationId = state.CircleId - 1, MaxStageProgressIndex = outgoing };
            else state.RotateSettleInfo = new() { RotationId = state.CircleId - 1, RegionId = state.RegionId, MaxStageProgressIndex = outgoing };
            state.LastModifyTime = modified;
            if (paidMarker) state.TerminalPaidGroupId = groupId;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 90;
            player.Transfinite = state;
            Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = 105, Count = 200 }]);
            inventory.AppliedRewardClaims.Add($"transfinite-terminal:{state.ActivityId}:{state.CircleId}:{groupId}");
            if (reset) inventory.AppliedRewardClaims.Add($"transfinite-reset:{state.ActivityId}:{state.CircleId}:{groupId}:3");
            return new(CreateDrawCompatibilityCharacter(uid++), player, inventory, name);
        }
        int Count(LoopbackSessionHarness h) => (int)h.Session.inventory.Items.Single(i => i.Id == 105).Count;

        // outgoing 15 -> anchor 8, outgoing 8 -> anchor 5 (TransfiniteStartStageProgress).
        foreach ((int outgoing, int anchor, bool receipt) in new[] { (15, 8, true), (8, 5, false) })
        {
            using LoopbackSessionHarness h = Build(outgoing, receipt, false, false, 1793000000, $"transfinite-anchor-repair-{anchor}", out var state, out long now, out int groupId);
            int skipped = groups[groupId].StageId.Take(anchor - 1).Sum(id => stages[id].Score ?? 0);
            TransfiniteModule.PrepareLogin(h.Session, now);
            AssertEqual(200 + skipped, Count(h), $"anchor {anchor}: login grants exactly the skipped base score");
            TransfiniteModule.PrepareLogin(h.Session, now);
            AssertEqual(200 + skipped, Count(h), $"anchor {anchor}: second login grants nothing");

            // BSON reload keeps the claim, so a fresh session grants nothing either.
            Player reloadedPlayer = BsonSerializer.Deserialize<Player>(h.Session.player.ToBson());
            Inventory reloadedInventory = BsonSerializer.Deserialize<Inventory>(h.Session.inventory.ToBson());
            using LoopbackSessionHarness reloaded = new(CreateDrawCompatibilityCharacter(uid), reloadedPlayer, reloadedInventory, $"transfinite-anchor-repair-reload-{anchor}");
            TransfiniteModule.PrepareLogin(reloaded.Session, now);
            AssertEqual(200 + skipped, Count(reloaded), $"anchor {anchor}: reloaded login grants nothing");
        }

        foreach ((string name, int outgoing, bool marker, bool reset, long modified) in new[]
            { ("non-anchored run", 0, false, false, 1793000000L), ("post-fix paid run", 15, true, false, 1793000000L), ("reset before the paid run", 15, false, true, 1793000000L), ("run started after the cutoff", 15, false, false, 1793200000L), ("never-modified state", 15, false, false, 0L) })
        {
            using LoopbackSessionHarness h = Build(outgoing, true, marker, reset, modified, $"transfinite-anchor-repair-none-{name.Replace(' ', '-')}", out _, out long now, out _);
            TransfiniteModule.PrepareLogin(h.Session, now);
            AssertEqual(200, Count(h), $"{name}: nothing to repair");
        }
        Console.WriteLine("Transfinite anchor repair passed.");
    }
}
