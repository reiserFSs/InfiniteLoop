using AscNet.Common.Database;
using MessagePack;

namespace AscNet.Common.MsgPack;

// PvE run state that is both persisted (Player.Theatre6.Theatre6RunState) and sent on the wire.
// Class names equal the client push names, so Mutation.Push sends them under the right packet name.

[MessagePackObject(true)]
public sealed class Theatre6RoomDataDb
{
    public int RoomIdx { get; set; }
    public int RoomType { get; set; }
    public int ChooseRoomStatus { get; set; }
    public int ChooseGroupId { get; set; }
    public int CurChoosePoolIdx { get; set; }
    public int CurChooseId { get; set; }
    public List<Theatre6RewardData> LeftRewards { get; set; } = new();
    public List<Theatre6RewardData> RightRewards { get; set; } = new();
    public int FightId { get; set; }
    public int FightSeed { get; set; }
    public int SelectedMonsterId { get; set; }
    public List<Theatre6RewardData> FightRewards { get; set; } = new();
    public int ShopId { get; set; }
    public List<Theatre6ShopGoodData> ShopGoods { get; set; } = new();
    public int ShopFreshCount { get; set; }
    public int BuySanTimes { get; set; }
    public int LastFightId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6RewardData
{
    public int RewardType { get; set; }
    public int TemplateId { get; set; }
    public int Amount { get; set; }
    public int AmountChange { get; set; }
    public int SkillId { get; set; }
    public int AttrPack { get; set; }
    public List<Theatre6BuffState> BuffList { get; set; } = new();
    public int FightId { get; set; }
    public int MonsterId { get; set; }
    public List<Theatre6RewardData> FightRewards { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre6TaskGoodsData
{
    public int GoodsId { get; set; }
    public int Amount { get; set; }
    public int NeedNum { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6TaskData
{
    public int TaskId { get; set; }
    public int SlotIndex { get; set; }
    public int TaskState { get; set; }
    public int GoodsState { get; set; }
    public int ConditionState { get; set; }
    public List<Theatre6TaskGoodsData> GoodsSlots { get; set; } = new();
    public int Schedule { get; set; }
    public List<Theatre6RewardData> RewardGoods { get; set; } = new();
    public int FailAddNum { get; set; }
    public int Progress { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6TaskSlotData
{
    public int Index { get; set; }
    public int TaskId { get; set; }
    public int RefreshCount { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6GoodsData
{
    public int GoodsId { get; set; }
    public int Amount { get; set; }
}

// ------------------------------------------------------------------ pushes

[MessagePackObject(true)]
public sealed class NotifyTheatre6NewRoomData
{
    public Theatre6RoomDataDb? RoomDataDb { get; set; }
    public Dictionary<int, Theatre6TaskData>? StageTasks { get; set; }
    public List<Theatre6TaskSlotData>? TaskSlotData { get; set; }
    public int TaskGroupId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6NewFloorData
{
    public Theatre6ModeDataDb? ModeDataDb { get; set; }
}

// ------------------------------------------------------------------ requests / responses

[MessagePackObject(true)]
public sealed class Theatre6PlayModeStartFightRequest
{
    public int CharacterId { get; set; }
    public int FashionId { get; set; }
    public int InitBuffId { get; set; }
    public int DifficultyId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PlayModeStartFightResponse
{
    public Theatre6ModeDataDb? PlayModeDataDb { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6EnterStoryLineRequest
{
    public int StoryLineId { get; set; }
    public int ReplayStageId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6EnterStoryLineResponse
{
    public Theatre6ModeDataDb? StoryModeDataDb { get; set; }
    public Theatre6StoryLineData? StoryLineData { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6RefreshTaskRequest
{
    public int Index { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6RefreshTaskResponse
{
    public Theatre6TaskData? NewTask { get; set; }
    public int RefreshCount { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ConfirmTaskRequest
{
    public List<int> TaskIds { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre6ConfirmTaskResponse
{
    public int NextRoomStatus { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6RecvTaskRoomRewardRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6RecvTaskRoomRewardResponse
{
    public int NextRoomStatus { get; set; }
    public List<Theatre6TaskSlotData> TaskSlotData { get; set; } = new();
    public Dictionary<int, Theatre6TaskData> NewStageTasks { get; set; } = new();
    public int TaskGroupId { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ChooseEventRequest
{
    public int SelectType { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ChooseEventResponse
{
    public int NextChooseId { get; set; }
    public Dictionary<int, Theatre6TaskData> StageTasks { get; set; } = new();
    public List<Theatre6RewardData> LeftRewards { get; set; } = new();
    public List<Theatre6RewardData> RightRewards { get; set; } = new();
    public int NextRoomStatus { get; set; }
    public bool IsEnd { get; set; }
    public List<Theatre6RewardData> RewardGoodsList { get; set; } = new();
    public List<Theatre6LiveBuffData> SkillUpBuffDatas { get; set; } = new();
    public List<Theatre6LiveBuffData> SanBuffDatas { get; set; } = new();
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6FightRoomSlideRequest
{
    public int SelectType { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6FightRoomSlideResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6EndAvgRoomRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6EndAvgRoomResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ExFloorConfirmRequest
{
    public bool IsEnter { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ExFloorConfirmResponse
{
    public Theatre6ModeDataDb? ModeDataDb { get; set; }
    public Theatre6SettleData? SettleData { get; set; }
    public Theatre6StoryModeSaveDb? StoryModeSaveDb { get; set; }
    public int Code { get; set; }
}
