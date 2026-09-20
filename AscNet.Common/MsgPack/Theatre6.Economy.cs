using MessagePack;

namespace AscNet.Common.MsgPack;

/// <summary>
/// Live Theatre6 stage buff instance. It is deliberately separate from the archive-saved
/// <see cref="Theatre6BuffData"/> (BuffId/TriggerCount/AddMagic) because a live instance carries an
/// instance UID, remaining duration counters and per-instance trigger bookkeeping.
/// </summary>
[MessagePackObject(true)]
public sealed class Theatre6LiveBuffData
{
    public int Uid { get; set; }
    public int BuffId { get; set; }
    public int RemainCount { get; set; }
    public int TriggerCount { get; set; }
    public int TaskFreeRefreshCount { get; set; }
    public int AddMagic { get; set; }
}

/// <summary>
/// One ordered skill delta. Client application order is Replace -> Remove -> Add; the overflow
/// queue changes are applied afterwards. The misspelled key <c>RemovesSkills</c> is the actual
/// consumer key and must never be "fixed".
/// </summary>
[MessagePackObject(true)]
public sealed class Theatre6SkillUpdate
{
    public List<Theatre6SkillData>? ReplaceSkills { get; set; }
    public List<Theatre6SkillData>? RemovesSkills { get; set; }
    public Theatre6SkillData? AddSkill { get; set; }
    public int FullEnQueueSkill { get; set; }
    public List<int>? DequeueSkills { get; set; }
}

/// <summary>In-run shop good. Position is 1-based; Type 1 = skill, 2 = relic (attr pack).</summary>
[MessagePackObject(true)]
public sealed class Theatre6ShopGoodData
{
    public int Position { get; set; }
    public int GoodId { get; set; }
    public bool IsSell { get; set; }
    public bool IsLock { get; set; }
    public int Type { get; set; }
}

/// <summary>Buff ambience entry: the client plays the highest-priority CueId of the Bgms map.</summary>
[MessagePackObject(true)]
public sealed class Theatre6BgmData
{
    public int CueId { get; set; }
    public int Priority { get; set; }
}

#region In-run shop requests

[MessagePackObject(true)] public sealed class Theatre6ShopFreshRequest { public int ShopFreshCount { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6ShopFreshResponse
{
    public List<Theatre6ShopGoodData>? ShopGoods { get; set; }
    public int ShopFreshCount { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)] public sealed class Theatre6EndShopRequest { }

[MessagePackObject(true)] public sealed class Theatre6EndShopResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6ShopGoodLockRequest
{
    public int Pos { get; set; }
    public bool IsLock { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ShopGoodLockResponse
{
    public bool IsLock { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)] public sealed class Theatre6ShopGoodBuyRequest { public int Pos { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6ShopGoodBuyResponse
{
    public Theatre6ShopGoodData? SellShopGood { get; set; }
    public List<Theatre6SkillUpdate>? SkillUpdates { get; set; }
    public int AttrPackId { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)] public sealed class Theatre6ShopGoodSellRequest { public int SkillId { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6ShopGoodSellResponse
{
    public List<Theatre6SkillUpdate>? SkillUpdates { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)] public sealed class Theatre6ShopBuySanRequest { public int BuySanTimes { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6ShopBuySanResponse
{
    public int BuySanTimes { get; set; }
    public int Code { get; set; }
}

#endregion

#region Skill requests

[MessagePackObject(true)]
public sealed class Theatre6SkillMoveOrSwapRequest
{
    public int SrcSkillId { get; set; }
    public int DstSlotType { get; set; }
    public int DstPosition { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6SkillMoveOrSwapResponse
{
    public List<Theatre6SkillUpdate>? SkillUpdates { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)] public sealed class Theatre6SkillOverQueueSellRequest { }

[MessagePackObject(true)] public sealed class Theatre6SkillOverQueueSellResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public sealed class Theatre6BuffLevelUpSkillRequest
{
    /// <summary>Live buff instance UID (never the configuration BuffId).</summary>
    public int BuffId { get; set; }

    /// <summary>Target skill id, or 0 to cancel the remaining upgrade chances.</summary>
    public int SkillId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6BuffLevelUpSkillResponse
{
    public List<Theatre6SkillUpdate>? SkillUpdates { get; set; }
    public int Code { get; set; }
}

#endregion

#region Economy pushes

[MessagePackObject(true)] public sealed class NotifyTheatre6AddBuff { public Theatre6LiveBuffData BuffData { get; set; } = new(); }

[MessagePackObject(true)] public sealed class NotifyTheatre6BuffUpdate { public List<Theatre6LiveBuffData> BuffDatas { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class NotifyTheatre6DelBuff
{
    public int BuffUid { get; set; }
    public int DeathType { get; set; }
}

[MessagePackObject(true)] public sealed class NotifyTheatre6AddBgm { public Dictionary<int, Theatre6BgmData> Bgms { get; set; } = new(); }

[MessagePackObject(true)] public sealed class NotifyTheatre6DelBgm { public Dictionary<int, Theatre6BgmData> Bgms { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class NotifyTheatre6AddMessyCode
{
    public Dictionary<int, int> MessyCodes { get; set; } = new();
    public int CurCodeId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6DelMessyCode
{
    public Dictionary<int, int> MessyCodes { get; set; } = new();
    public int CurCodeId { get; set; }
}

[MessagePackObject(true)] public sealed class NotifyTheatre6AddSkill { public List<Theatre6SkillUpdate> SkillUpdates { get; set; } = new(); }

[MessagePackObject(true)] public sealed class NotifyTheatre6SkillUpEffect { public List<Theatre6LiveBuffData> BuffDatas { get; set; } = new(); }

[MessagePackObject(true)] public sealed class NotifyTheatre6AttrChange { public List<Theatre6AttrData> AttrList { get; set; } = new(); }

[MessagePackObject(true)] public sealed class NotifyTheatre6AttrPackChange { public Theatre6AttrPackData AttrPack { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class NotifyTheatre6SanChange
{
    public int San { get; set; }
    public int MaxSan { get; set; }
    public int SanChange { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6HealthChange
{
    public int Health { get; set; }
    public int HealthChange { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6GoldChange
{
    public int GoldAmount { get; set; }
    public int GoldChange { get; set; }
}

[MessagePackObject(true)] public sealed class NotifyTheatre6GoodsChange { public List<Theatre6GoodsData> GoodsList { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class Theatre6TotalScoreNotify
{
    public int TotalScoreOld { get; set; }
    public int TotalScoreNew { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6TalentLevel
{
    public int Level { get; set; }
    public int Exp { get; set; }
}

#endregion
