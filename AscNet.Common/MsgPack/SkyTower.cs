using MessagePack;

namespace AscNet.Common.MsgPack;

// Dreamhouse Tycoon (client XSkyTower). Field names come from XSkyTowerControlNetwork / XSkyTowerModel /
// XSkyTowerAgency; the client has no protocol class definitions, so every field a consumer reads is listed.

/// <summary>Every SkyTower response starts with the error Code the client branches on.</summary>
public interface ISkyTowerResponse { int Code { get; set; } }

[MessagePackObject(true)]
public sealed class SkyTowerProductAmountDto
{
    public int ProductType { get; set; }
    public int ProductId { get; set; }
    public long Count { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerWorkPosInfoDto
{
    public int WorkType { get; set; }
    public int ProductType { get; set; }
    public int ProductId { get; set; }
    public int CharacterId { get; set; }
    public int State { get; set; }
    public long WorkedSeconds { get; set; }
    public long ProduceRemainder { get; set; }
    public List<SkyTowerProductAmountDto> ReservedInputs { get; set; } = [];
    public long ReservedExtraProduceRatio { get; set; }
    public bool HasStartedRound { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerFloorGroupStorageDto
{
    /// <summary>Key = ProductType * 100 + ProductId.</summary>
    public Dictionary<int, SkyTowerProductAmountDto> StorageInfos { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class SkyTowerCharacterInfoDto
{
    public int CharacterLv { get; set; }
    /// <summary>SkillId -> condition ratio in ten-thousandths; only skills with a matching target are listed.</summary>
    public Dictionary<int, int> ActiveSkillRatios { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class SkyTowerPerformInfoDto
{
    public int PerformId { get; set; }
    public int State { get; set; }
    public int Type { get; set; }
    public Dictionary<int, int> PerformStoryInfo { get; set; } = new();
    public long UpdateTime { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerWorkPosLocationDto
{
    public int FloorId { get; set; }
    public int WorkPosIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerDataDto
{
    public int ActivityId { get; set; }
    public int SkyTowerLv { get; set; }
    public long GoldItemCount { get; set; }
    public long CharacterLvUpItemCount { get; set; }
    public long SpeedUpItemCount { get; set; }
    public int SpeedUpDailyUsedCount { get; set; }
    public int SpeedUpGrantedDays { get; set; }
    public long TotalGoldProduced { get; set; }
    public long TotalTransCharacterLvUpItem { get; set; }
    public long LastSettleTime { get; set; }
    public Dictionary<int, SkyTowerWorkPosInfoDto> WorkPosInfos { get; set; } = new();
    public Dictionary<int, SkyTowerFloorGroupStorageDto> Storages { get; set; } = new();
    public Dictionary<int, SkyTowerCharacterInfoDto> CharacterInfos { get; set; } = new();
    public Dictionary<int, SkyTowerPerformInfoDto> PerformInfos { get; set; } = new();
    public SkyTowerWorkPosLocationDto? LastUnlockedWorkPosLocation { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerDataUpdateDto
{
    public Dictionary<int, SkyTowerWorkPosInfoDto> ChangedWorkPosInfos { get; set; } = new();
    public Dictionary<int, SkyTowerFloorGroupStorageDto> ChangedStorages { get; set; } = new();
    public long SpeedUpItemCount { get; set; }
    public long GoldItemCount { get; set; }
    public long CharacterLvUpItemCount { get; set; }
    public long TotalGoldProduced { get; set; }
    public long TotalTransCharacterLvUpItem { get; set; }
}

[MessagePackObject(true)]
public sealed class SkyTowerLevelUpResultDto
{
    public int SkyTowerLv { get; set; }
    public long GoldItemCount { get; set; }
    public SkyTowerDataUpdateDto DataUpdate { get; set; } = new();
    public List<int> NewWorkPosIds { get; set; } = [];
    public List<int> NewCharacterIds { get; set; } = [];
    public List<int> NewPerformIds { get; set; } = [];
}

// ---- requests / responses ----

[MessagePackObject(true)] public sealed class SkyTowerActivityDataRequest { }
[MessagePackObject(true)]
public sealed class SkyTowerActivityDataResponse : ISkyTowerResponse
{
    public int Code { get; set; }
    public SkyTowerDataDto? ActivityData { get; set; }
    public long OfflineBill { get; set; }
    public long OfflineTime { get; set; }
    public long OfflineCharacterLvUpItemCount { get; set; }
}

[MessagePackObject(true)] public sealed class SkyTowerSettleToNowRequest { }
[MessagePackObject(true)] public sealed class SkyTowerSettleToNowResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerDataUpdateDto? DataUpdate { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerExitRequest { }
[MessagePackObject(true)] public sealed class SkyTowerExitResponse : ISkyTowerResponse { public int Code { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerPerformDataRequest { }
[MessagePackObject(true)]
public sealed class SkyTowerPerformDataResponse : ISkyTowerResponse
{
    public int Code { get; set; }
    public List<SkyTowerPerformInfoDto> PerformInfos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class SkyTowerSavePerformStoryRequest
{
    public int PerformId { get; set; }
    /// <summary>TalkId -> chosen answer index (1-based); the client sends nil for an empty table.</summary>
    public Dictionary<int, int>? PerformStoryInfo { get; set; }
}
[MessagePackObject(true)] public sealed class SkyTowerSavePerformStoryResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerPerformInfoDto? PerformInfo { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerFinishPerformRequest { public int PerformId { get; set; } }
[MessagePackObject(true)]
public sealed class SkyTowerFinishPerformResponse : ISkyTowerResponse
{
    public int Code { get; set; }
    public SkyTowerPerformInfoDto? PerformInfo { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)] public sealed class SkyTowerFloorUpLevelRequest { public int SkyTowerLv { get; set; } }
[MessagePackObject(true)] public sealed class SkyTowerFloorUpLevelResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerLevelUpResultDto? Result { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerCharacterUpLevelRequest { public int CharacterId { get; set; } public int CharacterLv { get; set; } }
[MessagePackObject(true)]
public sealed class SkyTowerCharacterUpLevelResponse : ISkyTowerResponse
{
    public int Code { get; set; }
    public int CharacterId { get; set; }
    public int CharacterLv { get; set; }
    public SkyTowerDataUpdateDto? DataUpdate { get; set; }
}

[MessagePackObject(true)] public sealed class SkyTowerWorkPosAssignRequest { public int CharacterId { get; set; } public int WorkPosId { get; set; } }
[MessagePackObject(true)] public sealed class SkyTowerWorkPosAssignResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerDataUpdateDto? DataUpdate { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerWorkPosSwapRequest { public int FromWorkPosId { get; set; } public int ToWorkPosId { get; set; } }
[MessagePackObject(true)] public sealed class SkyTowerWorkPosSwapResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerDataUpdateDto? DataUpdate { get; set; } }

[MessagePackObject(true)]
public sealed class SkyTowerWorkPosSetProductRequest
{
    public int WorkPosId { get; set; }
    public int WorkType { get; set; }
    public int ProductType { get; set; }
    public int ProductId { get; set; }
}
[MessagePackObject(true)] public sealed class SkyTowerWorkPosSetProductResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerDataUpdateDto? DataUpdate { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerWorkPosStopRequest { public int WorkPosId { get; set; } }
[MessagePackObject(true)] public sealed class SkyTowerWorkPosStopResponse : ISkyTowerResponse { public int Code { get; set; } public SkyTowerDataUpdateDto? DataUpdate { get; set; } }

[MessagePackObject(true)] public sealed class SkyTowerUseSpeedUpItemRequest { public int WorkPosId { get; set; } public int Count { get; set; } }
[MessagePackObject(true)]
public sealed class SkyTowerUseSpeedUpItemResponse : ISkyTowerResponse
{
    public int Code { get; set; }
    public int SpeedUpDailyUsedCount { get; set; }
    public SkyTowerDataUpdateDto? DataUpdate { get; set; }
    public long GoldCount { get; set; }
    public int ProductType { get; set; }
    public int ProductId { get; set; }
    public long ProduceCount { get; set; }
}

// ---- pushes ----

[MessagePackObject(true)] public sealed class NotifySkyTowerData { public SkyTowerDataDto ActivityData { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifySkyTowerCharacter { public Dictionary<int, SkyTowerCharacterInfoDto> ChangedCharacterInfos { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifySkyTowerPerform { public List<SkyTowerPerformInfoDto> ChangedPerformInfos { get; set; } = []; }
[MessagePackObject(true)]
public sealed class NotifySkyTowerLoginData
{
    public int ActivityId { get; set; }
    public int Level { get; set; }
    public long GoldItemCount { get; set; }
    public Dictionary<int, SkyTowerPerformInfoDto> PerformInfos { get; set; } = new();
}
[MessagePackObject(true)] public sealed class NotifySkyTowerSpeedUpDailyReset { public int SpeedUpDailyUsedCount { get; set; } public long SpeedUpItemCount { get; set; } }
