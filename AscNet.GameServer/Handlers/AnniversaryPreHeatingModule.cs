using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.anniversarypreheating;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    // Wire contracts: XAnniversaryPreHeatingModel.SetServerData / XAnniversaryPreHeatingControl (4.9 client Lua).
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingGroupDataDb { public int GroupId { get; set; } public List<int> ReadContentIds { get; set; } = new(); public bool RewardReceived { get; set; } }
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingDataDb { public int ActivityId { get; set; } public List<AnniversaryPreHeatingGroupDataDb> AnniversaryPreHeatingGroupDatas { get; set; } = new(); }
    [MessagePackObject(true)] public sealed class NotifyAnniversaryPreHeatingData { public AnniversaryPreHeatingDataDb Data { get; set; } = new(); }
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingReadRequest { public int GroupId { get; set; } public int ContentId { get; set; } }
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingReadResponse { public int Code { get; set; } }
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingRewardRequest { public int GroupId { get; set; } }
    [MessagePackObject(true)] public sealed class AnniversaryPreHeatingRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); }

    /// <summary>
    /// 4.9 Time Trace (Anniversary pre-heating): read each content of a group, then claim the group reward once.
    /// Rules come from the AnniversaryPreHeating{Activity,Group,Content} tables; error codes are the installed
    /// CodeText 20436001-20436010 rows. The activity is closed (Docs/closed.md "Time Trace"): no schedule entry exists for
    /// its TimeIds, so every request answers NotOpen and the login push is skipped until the windows are restored.
    /// </summary>
    internal static class AnniversaryPreHeatingModule
    {
        private const int NotOpen = 20436001, GroupNotFound = 20436004, GroupNotInActivity = 20436005,
            GroupNotOpen = 20436006, ContentNotInGroup = 20436007, GroupNotComplete = 20436008, AlreadyReceived = 20436009;

        // Test seam (same pattern as Version47EventModule.Clock).
        internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        private static readonly Lazy<IReadOnlyList<AnniversaryPreHeatingActivityTable>> Activities = new(() =>
            TableReaderV2.Parse<AnniversaryPreHeatingActivityTable>());
        private static readonly Lazy<IReadOnlyList<AnniversaryPreHeatingGroupTable>> Groups = new(() =>
            TableReaderV2.Parse<AnniversaryPreHeatingGroupTable>());

        public static void SendLoginPush(Session session, DateTimeOffset now)
        {
            if (BuildNotify(session.player, now) is { } notify)
                session.SendPush(notify);
        }

        internal static NotifyAnniversaryPreHeatingData? BuildNotify(Player player, DateTimeOffset now)
        {
            if (ActiveActivity(now) is not { } activity)
                return null;
            AnniversaryPreHeatingState state = Reconcile(player, activity.Id);
            return new NotifyAnniversaryPreHeatingData
            {
                Data = new AnniversaryPreHeatingDataDb
                {
                    ActivityId = activity.Id,
                    AnniversaryPreHeatingGroupDatas = state.Groups.OrderBy(group => group.GroupId)
                        .Select(group => new AnniversaryPreHeatingGroupDataDb
                        {
                            GroupId = group.GroupId,
                            ReadContentIds = group.ReadContentIds.Order().ToList(),
                            RewardReceived = group.RewardReceived
                        }).ToList()
                }
            };
        }

        [RequestPacketHandler("AnniversaryPreHeatingReadRequest")]
        public static void Read(Session session, Packet.Request packet)
        {
            AnniversaryPreHeatingReadRequest request = packet.Deserialize<AnniversaryPreHeatingReadRequest>();
            session.SendResponse(new AnniversaryPreHeatingReadResponse
            {
                Code = MarkRead(session.player, request.GroupId, request.ContentId, Clock())
            }, packet.Id);
        }

        internal static int MarkRead(Player player, int groupId, int contentId, DateTimeOffset now)
        {
            int code = ValidateGroup(groupId, now, out AnniversaryPreHeatingActivityTable? activity, out AnniversaryPreHeatingGroupTable? group);
            if (code != 0)
                return code;
            if (!group!.ContentIds.Contains(contentId))
                return ContentNotInGroup;
            AnniversaryPreHeatingGroupState state = GroupState(Reconcile(player, activity!.Id), groupId);
            if (!state.ReadContentIds.Contains(contentId))
            {
                state.ReadContentIds.Add(contentId);
                player.Save();
            }
            return 0;
        }

        [RequestPacketHandler("AnniversaryPreHeatingRewardRequest")]
        public static void Reward(Session session, Packet.Request packet)
        {
            AnniversaryPreHeatingRewardRequest request = packet.Deserialize<AnniversaryPreHeatingRewardRequest>();
            AnniversaryPreHeatingRewardResponse response = ClaimReward(session, request.GroupId, Clock());
            session.SendResponse(response, packet.Id);
        }

        internal static AnniversaryPreHeatingRewardResponse ClaimReward(Session session, int groupId, DateTimeOffset now)
        {
            int code = ValidateGroup(groupId, now, out AnniversaryPreHeatingActivityTable? activity, out AnniversaryPreHeatingGroupTable? group);
            if (code != 0)
                return new() { Code = code };
            AnniversaryPreHeatingGroupState state = Reconcile(session.player, activity!.Id).Groups.FirstOrDefault(row => row.GroupId == groupId)
                ?? new AnniversaryPreHeatingGroupState { GroupId = groupId };
            if (state.RewardReceived)
                return new() { Code = AlreadyReceived };
            if (!group!.ContentIds.Where(id => id > 0).All(state.ReadContentIds.Contains))
                return new() { Code = GroupNotComplete };

            List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(group.RewardId);
            RewardApplicationResult result = new();
            if (goods.Count > 0)
                result = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"anniversary-preheating:{session.player.PlayerData.Id}:{activity.Id}:{groupId}", goods)], session);
            state.RewardReceived = true;
            session.player.Save();
            result.SendPushes(session);
            return new() { RewardGoodsList = result.RewardGoods };
        }

        private static int ValidateGroup(int groupId, DateTimeOffset now,
            out AnniversaryPreHeatingActivityTable? activity, out AnniversaryPreHeatingGroupTable? group)
        {
            group = null;
            activity = ActiveActivity(now);
            if (activity is null)
                return NotOpen;
            group = Groups.Value.FirstOrDefault(row => row.Id == groupId);
            if (group is null)
                return GroupNotFound;
            if (!activity.GroupIds.Contains(groupId))
                return GroupNotInActivity;
            // Client IsGroupInTime = CheckInTimeByTimeId(group.TimeId, false): an invalid or unscheduled id is closed.
            return group.TimeId > 0 && ActivityScheduleService.IsOpen(group.TimeId, now) ? 0 : GroupNotOpen;
        }

        // Client IsActivityInTime = CheckInTimeByTimeId(activity.TimeId, false).
        private static AnniversaryPreHeatingActivityTable? ActiveActivity(DateTimeOffset now) =>
            Activities.Value.Where(row => row.TimeId > 0 && ActivityScheduleService.IsOpen(row.TimeId, now))
                .OrderByDescending(row => row.Id).FirstOrDefault();

        private static AnniversaryPreHeatingState Reconcile(Player player, int activityId)
        {
            AnniversaryPreHeatingState state = player.AnniversaryPreHeating;
            if (state.ActivityId != activityId)
            {
                state.ActivityId = activityId;
                state.Groups = new List<AnniversaryPreHeatingGroupState>();
            }
            return state;
        }

        private static AnniversaryPreHeatingGroupState GroupState(AnniversaryPreHeatingState state, int groupId)
        {
            AnniversaryPreHeatingGroupState? group = state.Groups.FirstOrDefault(row => row.GroupId == groupId);
            if (group is null)
                state.Groups.Add(group = new AnniversaryPreHeatingGroupState { GroupId = groupId });
            return group;
        }
    }
}
