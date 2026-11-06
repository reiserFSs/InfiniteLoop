using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.draw;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Phylotree Nexus rerun (DrawTabs 8; DrawExtraTagGroup 1 normal / 2 Fate / 3 weapon / 4 CUB). The draws are the ones the 4.7
    /// retail capture served with GroupSubType 1-4 (1498, 2492, 379, 7065): offered under their group's own option, never on
    /// the group's normal tab, windowed by the missing-calendar policy; the Fate variant stays closed with every Fate group.
    /// </summary>
    public static void ValidatePhylotreeRerun49()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1794000000); // 2026-11-06, inside 4.9
        Dictionary<int, DrawExtraTagGroupTable> extra = TableReaderV2.Parse<DrawExtraTagGroupTable>().ToDictionary(row => row.Id);
        AssertEqual(8, extra[1].Tag, "Phylotree Nexus Rerun is on DrawTabs tab 8");
        AssertEqual(8, extra[2].Tag, "Fate Phylotree Nexus Rerun is on DrawTabs tab 8");
        AssertEqual((1, 7), (extra[3].Tag, extra[4].Tag), "Phylotree weapon/CUB options sit on the Weapon/CUB tabs");
        foreach (int timeId in new[] { extra[1].TimeId, extra[3].TimeId })
        {
            AssertEqual(true, ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry), $"DrawExtraTagGroup TimeId {timeId} has a window");
            AssertEqual(true, entry.IsOpen(now), $"DrawExtraTagGroup TimeId {timeId} is open");
            AssertEqual(true, entry.Source.StartsWith(MissingCalendarSource + "DrawExtraTagGroup.TimeId", StringComparison.Ordinal),
                $"DrawExtraTagGroup TimeId {timeId} carries the missing-calendar label ({entry.Source})");
        }

        // Derivation from client tables only: later DrawPreview draw per target is the rerun (AscNet missing-retail policy),
        // Liv: Limpidity's CUB is Levvi, and the Fate group is the Themed group's DrawGroupRelation destiny.
        List<DrawPreviewTable> previews = TableReaderV2.Parse<DrawPreviewTable>();
        foreach ((int target, int rerun) in new[] { (1031005, 1498), (1031005, 2492), (2606001, 379), (16340000, 7065) })
            AssertEqual(rerun, previews.Where(row => row.UpGoodsId.Contains(target) && row.Id >= (rerun == 2492 ? 2000 : 0)
                && (target != 1031005 || (row.Id < 2000) == (rerun < 2000))).Max(row => row.Id),
                $"rerun draw for target {target} is its latest DrawPreview draw");
        AssertEqual(1031005, TableReaderV2.Parse<AscNet.Table.V2.share.partner.PartnerTable>().Single(row => row.Id == 16340000).RecommendCharacterId,
            "Levvi is Liv: Limpidity's recommended CUB");
        AssertEqual(15, TableReaderV2.Parse<DrawGroupRelationTable>().Single(row => row.NormalGroupId == 11).DestinyGroupId,
            "Fate group of the Themed group");

        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        FieldInfo clock = manager.GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        Func<DateTimeOffset> prior = (Func<DateTimeOffset>)clock.GetValue(null)!;
        MethodInfo groupInfos = RequiredMethod(manager, "GetDrawGroupInfos", BindingFlags.Static | BindingFlags.Public, [typeof(Player)]);
        MethodInfo drawInfo = RequiredMethod(manager, "GetDrawInfoById", BindingFlags.Static | BindingFlags.Public, [typeof(int), typeof(Player)]);
        MethodInfo setUse = RequiredMethod(manager, "SetUseDrawId", BindingFlags.Static | BindingFlags.Public, [typeof(Player), typeof(int)]);
        MethodInfo roll = RequiredMethod(manager, "DrawDraw", BindingFlags.Static | BindingFlags.Public, [typeof(Player), typeof(int), typeof(int)]);
        MethodInfo rewardReady = manager.GetMethod("HasRewardConfiguration", BindingFlags.Public | BindingFlags.Static)!;
        Player player = CreateDrawCompatibilityPlayer(49_091);
        try
        {
            clock.SetValue(null, (Func<DateTimeOffset>)(() => now));
            List<DrawGroupInfo> groups = (List<DrawGroupInfo>)groupInfos.Invoke(null, [player])!;

            // (group, draw, target, DrawExtraTagGroup.Id): the offered rerun set.
            foreach ((int groupId, int drawId, int target, int subtype) in new[]
                { (11, 1498, 1031005, 1), (4, 379, 2606001, 3), (22, 7065, 16340000, 4) })
            {
                DrawGroupInfo group = groups.Single(row => row.Id == groupId);
                AssertEqual(true, group.OptionalDrawIdList.Contains(drawId), $"rerun draw {drawId} is offered in group {groupId}");
                AssertEqual(true, group.TagBlackListDrawIds.Contains(drawId), $"rerun draw {drawId} stays off group {groupId}'s normal tab");
                DrawInfo draw = (DrawInfo?)drawInfo.Invoke(null, [drawId, player]) ?? throw new InvalidDataException($"draw {drawId} is not served");
                AssertEqual((groupId, subtype, target), (draw.GroupId, draw.GroupSubType, draw.ResourceIds[1]), $"draw {drawId} identity");
                AssertEqual(true, (bool)rewardReady.Invoke(null, [drawId])!, $"rerun draw {drawId} has rewards");
                AssertEqual(1, ((List<RewardGoods>)roll.Invoke(null, [player, drawId, 0])!).Count, $"rerun draw {drawId} draws");
                setUse.Invoke(null, [player, drawId]);
                AssertEqual(drawId, player.DrawState.SelectedDrawByGroup[groupId].Slots.GetValueOrDefault(subtype),
                    $"draw {drawId} is selected in its own option slot {subtype}");
            }

            // The group's own banner and presentation stay with the 4.9 season.
            DrawGroupInfo leia = groups.Single(row => row.Id == 11);
            AssertEqual((2, 0L), (leia.Tag, leia.EndTime), "Leia group stays on the Current Season tab without a countdown");
            AssertEqual(false, leia.TagBlackListDrawIds.Contains(1526), "current Leia banner is on the normal tab");
            AssertEqual(1526, ((List<DrawGroupInfo>)groupInfos.Invoke(null, [CreateDrawCompatibilityPlayer(49_092)])!)
                .Single(row => row.Id == 11).UseDrawIdDict[0], "default Leia selection is the 4.9 banner");

            // Fate stays closed.
            AssertEqual(true, drawInfo.Invoke(null, [2492, player]) is null, "Fate Phylotree rerun draw 2492 is not served");
            AssertEqual(false, (bool)rewardReady.Invoke(null, [2492])!, "Fate Phylotree rerun draw 2492 has no rewards");
            AssertEqual(false, groups.Any(row => row.Id == 15), "Fate group 15 stays unadvertised");
        }
        finally
        {
            clock.SetValue(null, prior);
        }
    }
}
