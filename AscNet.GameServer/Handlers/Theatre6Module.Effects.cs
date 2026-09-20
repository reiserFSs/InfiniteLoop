using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre6;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Theatre6 stage-buff lifecycle and player-state effects.
///
/// The shipped client never interprets authored stage-buff rows: it renders the live instance map the
/// server sends and native combat consumes the magic ids the server projects. The retail resolver is
/// not present in any shipped asset, so this file implements the authored trigger/effect/duration
/// families as an explicitly documented AscNet policy. Every authored family performs a real state
/// transition or projects a native magic id; nothing is accepted and dropped.
/// </summary>
internal static partial class Theatre6Module
{
    private const int BuffTriggerChoice = 1;
    private const int BuffTriggerGoods = 2;
    private const int BuffTriggerStateChange = 3;
    private const int BuffTriggerImmediate = 7;
    private const int BuffTriggerRelic = 10;
    private const int BuffTriggerMission = 11;

    private const int BuffEffectSan = 2;
    private const int BuffEffectGrantBuffs = 4;
    private const int BuffEffectStartSkill = 5;
    private const int BuffEffectHealth = 6;
    private const int BuffEffectAttr = 7;
    private const int BuffEffectMagic = 8;
    private const int BuffEffectGoodsModifier = 9;
    private const int BuffEffectMessyCode = 11;
    private const int BuffEffectSkillUp = 12;

    private const int BuffDeathDestroy = 1;
    private const int BuffDeathToDestroyed = 2;

    /// <summary>Duration kinds that count down at an explicit boundary (client "…Remaining" labels).</summary>
    private const int DurationPermanent = 1;
    private const int DurationBattles = 2;
    private const int DurationChoices = 3;
    private const int DurationRooms = 4;
    private const int DurationSingleUse = 5;
    private const int DurationFloors = 6;
    private const int DurationGeneric = 7;
    private const int DurationRun = 8;
    private const int DurationSkillUp = 9;

    private const int SanRowNormal = 1;
    private const int SanRowBelow = 2;
    private const int SanRowAbove = 3;
    private const int SanRowDeath = 4;

    /// <summary>
    /// Native affix family ids used by stage buff effect 5. The same ids appear as Theatre6KeyWord and
    /// Theatre6BuildTag entries (authoritative, locale-independent), so a family matches a skill through
    /// either catalog: e.g. Ignite is keyword/build tag 12/99.
    /// </summary>
    private static readonly Dictionary<int, int[]> StartSkillFamilies = new()
    {
        [1] = new[] { 19, 20 },  // Rage / Berserk
        [2] = new[] { 12, 99 },  // Ignite
        [3] = new[] { 17, 18 },  // <Block> / Block
        [4] = new[] { 15, 16 },  // <CRIT> / CRIT
        [5] = new[] { 11 },      // Stun
        [6] = new[] { 28, 29 }   // Dawnlight / Dawnbreak
    };

    // Nested effect application (buff 3500 grants 3600..3603) is bounded by the authored EventMaxDepth;
    // a nested sanity/state change re-enters the SAN resolver at most once more than the authored depth.
    [ThreadStatic] private static bool sanResolveActive;
    [ThreadStatic] private static bool sanResolvePending;

    #region Live buff lifecycle

    internal static Theatre6LiveBuffData ToWireLiveBuff(Theatre6LiveBuffState buff) => new()
    {
        Uid = buff.Uid,
        BuffId = buff.BuffId,
        RemainCount = buff.RemainCount,
        TriggerCount = buff.TriggerCount,
        TaskFreeRefreshCount = buff.TaskFreeRefreshCount,
        AddMagic = buff.AddMagic
    };

    /// <summary>Live star-up buffs that still have chances; the client opens their upgrade popups on push.</summary>
    internal static List<Theatre6LiveBuffData> PendingSkillUpBuffs(Theatre6RunState run) =>
        run.Buffs.Values
            .Where(buff => EcoTables.Buff(buff.BuffId).BuffEffectType == BuffEffectSkillUp && buff.TriggerCount < SkillUpChances(buff.BuffId))
            .OrderBy(buff => buff.Uid)
            .Select(ToWireLiveBuff)
            .ToList();

    private static int SkillUpChances(int buffId)
    {
        List<int> parameters = EcoTables.Buff(buffId).BuffEffectParams;
        return parameters.Count > 0 ? Math.Max(1, parameters[0]) : 1;
    }

    /// <summary>
    /// Grants a stage buff as a live instance. Multiple instances are only created for the authored
    /// star-up buff (each relic grants its own chance); every other buff refreshes its single instance,
    /// matching the authored CanStack/MaxNum defaults of 0.
    /// </summary>
    internal static Theatre6LiveBuffState AddBuff(Mutation m, Theatre6RunState run, int buffId, int depth = 0)
    {
        Theatre6StageBuffTable row = EcoTables.Buff(buffId);
        bool isSkillUp = row.BuffEffectType == BuffEffectSkillUp;
        Theatre6LiveBuffState? existing = isSkillUp ? null : run.Buffs.Values.FirstOrDefault(buff => buff.BuffId == buffId);
        if (existing is not null)
        {
            existing.RemainCount = Math.Max(existing.RemainCount, InitialRemainCount(row));
            m.Push(new NotifyTheatre6BuffUpdate { BuffDatas = new List<Theatre6LiveBuffData> { ToWireLiveBuff(existing) } });
            return existing;
        }

        Theatre6LiveBuffState buff = new()
        {
            Uid = run.NextBuffUid++,
            BuffId = buffId,
            RemainCount = InitialRemainCount(row),
            TriggerCount = 0,
            AddMagic = row.BuffEffectType == BuffEffectMagic && row.BuffEffectParams is { Count: > 0 } magic ? magic[0] : 0,
            FloorScoped = row.DurationType == DurationFloors
        };
        run.Buffs[buff.Uid] = buff;
        if (buff.FloorScoped && !run.FloorBuffUid.Contains(buff.Uid))
            run.FloorBuffUid.Add(buff.Uid);
        if (row.IsNotShow is not > 0)
            m.Push(new NotifyTheatre6AddBuff { BuffData = ToWireLiveBuff(buff) });

        if (row.BuffTriggerType == BuffTriggerImmediate && !isSkillUp && depth <= Math.Max(1, EconCfg("EventMaxDepth")))
        {
            // Bookkeeping first so attribute buffs aggregate this application when they recalculate.
            buff.TriggerCount = 1;
            ApplyBuffEffect(m, run, buff, row, units: 1, depth: depth);
        }

        if (row.DurationType == DurationSingleUse)
            RemoveBuff(m, run, buff, row.DeathType);
        else if (isSkillUp)
            m.Push(new NotifyTheatre6SkillUpEffect { BuffDatas = new List<Theatre6LiveBuffData> { ToWireLiveBuff(buff) } });

        return buff;
    }

    private static int InitialRemainCount(Theatre6StageBuffTable row) => row.DurationType switch
    {
        DurationPermanent or DurationSingleUse or DurationRun or DurationSkillUp => 0,
        _ => row.DurationValues is int value and > 0 ? value : 1
    };

    private static void RemoveBuff(Mutation m, Theatre6RunState run, Theatre6LiveBuffState buff, int deathType)
    {
        if (!run.Buffs.Remove(buff.Uid))
            return;
        run.FloorBuffUid.Remove(buff.Uid);
        if (deathType == BuffDeathToDestroyed)
            run.DestroyedBuffs[buff.Uid] = buff;
        m.Push(new NotifyTheatre6DelBuff { BuffUid = buff.Uid, DeathType = deathType });
        Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
        if (row.BuffEffectType == BuffEffectMessyCode)
            RebuildMessyCodes(m, run, buff.Uid, added: false);
        if (row.BuffEffectType == BuffEffectAttr)
        {
            // Removing an accumulated attribute buff changes attributes and therefore the score.
            RecalculateAttrs(m, run);
            RecalculateScore(m, run);
        }
    }

    #endregion

    #region Trigger and duration resolution

    /// <summary>
    /// Applies every live buff whose authored trigger family matches. Parameter conventions (frozen for
    /// Run and Combat callers):
    /// trigger 1 choice — <paramref name="parameter"/> = SelectType (1 left / 2 right);
    /// trigger 2 goods — <paramref name="parameter"/> = goods id, <paramref name="amount"/> = gained amount;
    /// trigger 3 state — <paramref name="parameter"/> &gt; 0 = goods id gained, 0 = sanity decreased with
    /// <paramref name="amount"/> = signed sanity delta;
    /// trigger 10 relic / 11 mission — <paramref name="amount"/> = acquisitions this event.
    /// </summary>
    internal static void TriggerEffects(Mutation m, Theatre6RunState run, int triggerType, int amount = 1, int parameter = 0, int depth = 0)
    {
        if (amount <= 0)
            return;
        int maxDepth = Math.Max(1, EconCfg("EventMaxDepth"));
        if (depth > maxDepth)
            return;
        int maxApplications = Math.Max(1, EconCfg("BuffMaxTriggerCount"));
        int cumulativeGoods = parameter > 0 && run.Goods.TryGetValue(parameter, out Theatre6GoodsData? goods)
            ? goods.Amount
            : 0;
        int applications = 0;
        foreach (Theatre6LiveBuffState buff in run.Buffs.Values.ToList())
        {
            Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
            if (row.BuffTriggerType != triggerType)
                continue;
            if (row.Limit > 0 && buff.TriggerCount >= row.Limit)
                continue;
            int units = TriggerUnits(row, triggerType, amount, parameter, cumulativeGoods, buff.TriggerCount);
            if (units <= 0)
                continue;
            if (row.Limit > 0)
                units = Math.Min(units, row.Limit - buff.TriggerCount);
            if (applications + units > maxApplications)
                units = maxApplications - applications;
            if (units <= 0)
                break;

            buff.TriggerCount += units;
            ApplyBuffEffect(m, run, buff, row, units, depth + 1);
            applications += units;
            m.Push(new NotifyTheatre6BuffUpdate { BuffDatas = new List<Theatre6LiveBuffData> { ToWireLiveBuff(buff) } });
            if (row.DurationType == DurationSingleUse)
                RemoveBuff(m, run, buff, row.DeathType);
            if (applications >= maxApplications)
                break;
        }
    }

    private static int TriggerUnits(Theatre6StageBuffTable row, int triggerType, int amount, int parameter, int cumulative, int applied)
    {
        List<int> trigger = row.BuffTriggerParams;
        switch (triggerType)
        {
            case BuffTriggerChoice:
                int direction = trigger.Count > 0 ? trigger[0] : 0;
                if (direction is 1 or 2 && direction != parameter)
                    return 0;
                int required = trigger.Count > 1 && trigger[1] > 0 ? trigger[1] : 1;
                return amount / required;
            case BuffTriggerGoods:
                int scopedGoods = trigger.Count > 0 ? trigger[0] : 0;
                if (scopedGoods != 0 && scopedGoods != parameter)
                    return 0;
                int perGoods = trigger.Count > 1 && trigger[1] > 0 ? trigger[1] : 1;
                return ThresholdUnits(perGoods, amount, cumulative, applied);
            case BuffTriggerStateChange:
                int kind = trigger.Count > 0 ? trigger[0] : 0;
                // kind 2 = a material was obtained (trigger[1] = goods id, trigger[2] = threshold),
                // kind 4 = sanity decreased; the authored "additional loss" applies once per decrease.
                if (kind == 2)
                {
                    int goodsId = trigger.Count > 1 ? trigger[1] : 0;
                    if (goodsId != 0 && goodsId != parameter)
                        return 0;
                    int per = trigger.Count > 2 && trigger[2] > 0 ? trigger[2] : 1;
                    return ThresholdUnits(per, amount, cumulative, applied);
                }

                return kind == 4 && parameter == 0 ? 1 : 0;
            case BuffTriggerRelic:
            case BuffTriggerMission:
                return amount;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Threshold triggers ("for every N materials obtained") carry the remainder between events: the
    /// authored per-threshold counts against the run's cumulative material total minus this instance's
    /// applications so far, capped by the units gained in the current event.
    /// </summary>
    // ponytail: cumulative-goods carry assumes threshold buffs are acquired before materials accumulate
    // (authored trigger-2 buffs 2/5/8 are run-start/floor-pool grants and permanent); a buff granted
    // after prior gains fires min(amount, cumulative/per) applications on its first event. Persist a
    // per-buff acquisition baseline if a pool/relic/SAN route ever grants a threshold buff mid-run.
    private static int ThresholdUnits(int per, int amount, int cumulative, int applied)
    {
        if (per <= 1)
            return amount;
        int reached = cumulative / per;
        return Math.Max(0, Math.Min(amount, reached - applied));
    }

    /// <summary>
    /// Advances duration counters at authored boundaries: "fight" (battles), "choice" (choices),
    /// "floor" (floors) and "room" (generic counters). Expired instances are destroyed or moved to the
    /// destroyed map exactly as their authored DeathType requires.
    /// </summary>
    internal static void TickEffects(Mutation m, Theatre6RunState run, string boundary)
    {
        int duration = boundary switch
        {
            "fight" => DurationBattles,
            "choice" => DurationChoices,
            "floor" => DurationFloors,
            "room" => DurationGeneric,
            _ => 0
        };
        List<Theatre6LiveBuffData> updates = new();
        foreach (Theatre6LiveBuffState buff in run.Buffs.Values.ToList())
        {
            Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
            if (row.DurationType != duration)
                continue;
            if (row.DurationType == DurationFloors)
            {
                // Floor-scoped buffs are always cleared when their floor ends, regardless of counter.
                RemoveBuff(m, run, buff, row.DeathType);
                continue;
            }

            buff.RemainCount--;
            if (buff.RemainCount <= 0)
            {
                RemoveBuff(m, run, buff, row.DeathType);
                continue;
            }

            updates.Add(ToWireLiveBuff(buff));
        }

        if (updates.Count > 0)
            m.Push(new NotifyTheatre6BuffUpdate { BuffDatas = updates });
    }

    #endregion

    #region Effect application

    private static void ApplyBuffEffect(Mutation m, Theatre6RunState run, Theatre6LiveBuffState buff, Theatre6StageBuffTable row, int units, int depth)
    {
        int maxDepth = Math.Max(1, EconCfg("EventMaxDepth"));
        if (depth > maxDepth)
            return;
        List<int> parameters = row.BuffEffectParams;
        switch (row.BuffEffectType)
        {
            case BuffEffectSan:
                AddSan(m, run, ScalarEffectValue(parameters) * units, depth + 1);
                break;
            case BuffEffectHealth:
                AddHealth(m, run, ScalarEffectValue(parameters) * units);
                break;
            case BuffEffectAttr:
                // Attribute pairs are applied as TriggerCount-scaled bonuses in RecalculateAttrs; the
                // score derives from attribute values, so both must be republished here — a choice-room
                // attribute gain (e.g. Rending Breath) otherwise leaves the client's ScoreTotal stale.
                RecalculateAttrs(m, run);
                RecalculateScore(m, run);
                break;
            case BuffEffectGrantBuffs:
                foreach (int nested in parameters)
                {
                    if (nested > 0)
                        AddBuff(m, run, nested, depth + 1);
                }

                break;
            case BuffEffectStartSkill:
                GrantStartSkill(m, run, parameters.Count > 0 ? parameters[0] : 0, units);
                break;
            case BuffEffectMagic:
                // Native projection: the magic id now lives on the live instance (AddMagic) and is
                // projected into the native actor by Combat. Applying damage here would double-apply it.
                break;
            case BuffEffectGoodsModifier:
                // Applied on material gain in AddGoods (per-mille over the matching goods id).
                break;
            case BuffEffectMessyCode:
                RebuildMessyCodes(m, run, buff.Uid, added: true, effectId: parameters.Count > 0 ? parameters[0] : 0);
                break;
            case BuffEffectSkillUp:
                // Chance bookkeeping only; the upgrade itself is the client's BuffLevelUpSkillRequest.
                break;
            default:
                throw new InvalidDataException($"Unsupported Theatre6 stage buff effect type {row.BuffEffectType} on buff {row.Id}.");
        }
    }

    /// <summary>Effect 2 uses [mode, value]; a single authored operand is the value itself.</summary>
    private static int ScalarEffectValue(List<int> parameters) => parameters.Count switch
    {
        0 => 0,
        1 => parameters[0],
        _ => parameters[1]
    };

    /// <summary>
    /// Effect 5: grant the character's signature skill for the authored affix family. The family is
    /// resolved from the authoritative catalogs that share the native family numbering: Theatre6KeyWord
    /// ids and Theatre6BuildTag ids (Ignite 12/99, Rage/Berserk 19/20, Dawnlight 28/29, Block 17/18,
    /// CRIT 15/16, Stun 11). Candidates are the character's own level-1 grantable skills (the same
    /// eligibility rule the offer pools use: same character, not IsOutPool). A family with no authored
    /// candidate is a data error and is reported, never substituted with an unrelated skill.
    /// </summary>
    private static void GrantStartSkill(Mutation m, Theatre6RunState run, int family, int count)
    {
        int[] identifiers = StartSkillFamilies.GetValueOrDefault(family, Array.Empty<int>());
        Require(identifiers.Length > 0, ErrorNotOpen);
        List<Theatre6SkillTable> matched = EcoTables.SkillCatalog.Value
            .Where(skill => skill.Character == run.File.CharacterId
                && skill.Level == 1
                && skill.IsOutPool is not 1
                && (skill.KeyWordIds.Any(identifiers.Contains) || skill.BuildTags.Any(identifiers.Contains)))
            .OrderBy(skill => skill.Id)
            .ToList();
        Require(matched.Count > 0, ErrorNotOpen);
        for (int index = 0; index < Math.Max(1, count); index++)
            GrantSkill(m, run, matched[index % matched.Count].Id, pushUpdate: true);
    }

    #endregion

    #region Sanity, health, BGM and messy codes

    internal static int AddSan(Mutation m, Theatre6RunState run, int amount, int depth = 0)
    {
        if (amount == 0)
            return 0;
        int before = run.CurSan;
        int clamped = Math.Clamp(before + amount, 0, Math.Max(1, run.MaxSan));
        if (clamped == before)
            return 0;
        run.CurSan = clamped;
        run.MinSan = Math.Min(run.MinSan, clamped);
        m.Push(new NotifyTheatre6SanChange { San = clamped, MaxSan = run.MaxSan, SanChange = clamped - before });
        if (amount < 0)
            TriggerEffects(m, run, BuffTriggerStateChange, amount: -amount, parameter: 0, depth: depth);
        ApplySanState(m, run, depth + 1);
        return clamped - before;
    }

    internal static int AddHealth(Mutation m, Theatre6RunState run, int amount)
    {
        if (amount == 0)
            return 0;
        int before = run.CurHealth;
        int clamped = Math.Clamp(before + amount, 0, Math.Max(1, run.MaxHealth));
        if (clamped == before)
            return 0;
        run.CurHealth = clamped;
        m.Push(new NotifyTheatre6HealthChange { Health = clamped, HealthChange = clamped - before });
        return clamped - before;
    }

    /// <summary>
    /// Aligns the live buff set, the buff ambience map and the messy-code map with the authored sanity
    /// row for the current sanity value. Row buffs are added/removed as a set so repeated calls are
    /// idempotent, and the whole messy map is republished because the client replaces it wholesale.
    /// </summary>
    internal static void ApplySanState(Mutation m, Theatre6RunState run, int depth = 0)
    {
        if (depth > 4 || EcoTables.Stages.Value.Count == 0)
            return;
        if (sanResolveActive)
        {
            sanResolvePending = true;
            return;
        }

        sanResolveActive = true;
        try
        {
            for (int pass = 0; pass < 4; pass++)
            {
                sanResolvePending = false;
                ApplySanRow(m, run);
                if (!sanResolvePending)
                    break;
            }
        }
        finally
        {
            sanResolveActive = false;
        }
    }

    private static void ApplySanRow(Mutation m, Theatre6RunState run)
    {
        int groupId = EcoTables.Stage(run.StageId).SanGroupId;
        List<Theatre6StageSanTable> rows = TableReaderV2.Parse<Theatre6StageSanTable>()
            .Where(row => row.SanGroupId == groupId).OrderBy(row => row.Id).ToList();
        if (rows.Count == 0)
            return;
        Theatre6StageSanTable? target = run.CurSan <= 0
            ? rows.FirstOrDefault(row => row.SanType == SanRowDeath)
            : rows.FirstOrDefault(row => row.SanType is SanRowBelow or SanRowAbove
                && row.MinSan is int min && row.MaxSan is int max && run.CurSan >= min && run.CurSan <= max)
              ?? rows.FirstOrDefault(row => row.SanType == SanRowNormal);
        if (target is null)
            return;

        Theatre6StageSanTable? applied = rows.FirstOrDefault(row =>
            row.BuffIds is { Count: > 0 } buffs && buffs.All(buffId => buffId <= 0 || run.Buffs.Values.Any(buff => buff.BuffId == buffId)));
        if (applied?.Id == target.Id)
            return;

        if (applied is not null)
        {
            foreach (int buffId in applied.BuffIds)
            {
                if (buffId <= 0 || target.BuffIds.Contains(buffId))
                    continue;
                Theatre6LiveBuffState? live = run.Buffs.Values.FirstOrDefault(buff => buff.BuffId == buffId);
                if (live is not null)
                    RemoveBuff(m, run, live, EcoTables.Buff(buffId).DeathType);
            }
        }

        foreach (int buffId in target.BuffIds)
        {
            if (buffId <= 0 || run.Buffs.Values.Any(buff => buff.BuffId == buffId))
                continue;
            AddBuff(m, run, buffId);
        }

        UpdateSanBgm(m, run, applied?.SanType ?? 0, target.SanType, target.CueId);
    }

    /// <summary>
    /// Sanity ambience: one Bgms entry per authored sanity tier, keyed by SanType and prioritised by it,
    /// so the client plays the cue of the most severe active tier (death above all others).
    /// </summary>
    private static void UpdateSanBgm(Mutation m, Theatre6RunState run, int previousType, int currentType, int cueId)
    {
        if (previousType == currentType)
        {
            if (cueId > 0 && (!run.Bgms.TryGetValue(currentType, out Theatre6BgmState? existing) || existing.CueId != cueId))
            {
                Theatre6BgmData updated = new() { CueId = cueId, Priority = currentType };
                run.Bgms[currentType] = new Theatre6BgmState { CueId = cueId, Priority = currentType };
                m.Push(new NotifyTheatre6AddBgm { Bgms = new Dictionary<int, Theatre6BgmData> { [currentType] = updated } });
            }

            return;
        }

        if (previousType > 0 && run.Bgms.Remove(previousType))
            m.Push(new NotifyTheatre6DelBgm { Bgms = new Dictionary<int, Theatre6BgmData> { [previousType] = new() } });
        if (cueId <= 0)
            return;
        run.Bgms[currentType] = new Theatre6BgmState { CueId = cueId, Priority = currentType };
        m.Push(new NotifyTheatre6AddBgm { Bgms = new Dictionary<int, Theatre6BgmData> { [currentType] = new Theatre6BgmData { CueId = cueId, Priority = currentType } } });
    }

    /// <summary>
    /// Republishes the messy-code map (live buff UID to StageEffect id) after an add or a removal. The
    /// client replaces the complete map on both pushes, so the full map always travels.
    /// </summary>
    private static void RebuildMessyCodes(Mutation m, Theatre6RunState run, int buffUid, bool added, int effectId = 0)
    {
        Dictionary<int, int> map = new();
        foreach (Theatre6LiveBuffState buff in run.Buffs.Values)
        {
            if (EcoTables.Buff(buff.BuffId).BuffEffectType != BuffEffectMessyCode)
                continue;
            List<int> parameters = EcoTables.Buff(buff.BuffId).BuffEffectParams;
            if (parameters.Count > 0 && parameters[0] > 0)
                map[buff.Uid] = parameters[0];
        }

        int curCodeId = added && map.TryGetValue(buffUid, out int addedCode)
            ? addedCode
            : effectId > 0 ? effectId : run.CurCodeId;
        bool changed = run.MessyCodes.Count != map.Count
            || map.Any(entry => !run.MessyCodes.TryGetValue(entry.Key, out int known) || known != entry.Value);
        run.MessyCodes = map;
        run.CurCodeId = curCodeId;
        if (!changed && !added)
            return;
        if (added)
            m.Push(new NotifyTheatre6AddMessyCode { MessyCodes = new Dictionary<int, int>(map), CurCodeId = curCodeId });
        else
            m.Push(new NotifyTheatre6DelMessyCode { MessyCodes = new Dictionary<int, int>(map), CurCodeId = curCodeId });
    }

    #endregion

    #region Derived attributes and score

    /// <summary>
    /// Rebuilds the file attribute list from its real sources: authored character base attributes,
    /// permanent talent levels, acquired relics and accumulated attribute buffs. Attribute 6 (Sanity
    /// Cap) also drives MaxSan. Changed entries are published through NotifyTheatre6AttrChange.
    /// </summary>
    internal static void RecalculateAttrs(Mutation m, Theatre6RunState run, bool pushChanges = true)
    {
        Dictionary<int, int> attrs = new();
        List<Theatre6AttrTable> attrRows = TableReaderV2.Parse<Theatre6AttrTable>().OrderBy(row => row.Id).ToList();
        foreach (Theatre6AttrTable row in attrRows)
            attrs[row.Id] = 0;

        Theatre6CharacterTable character = EcoTables.Character(run.File.CharacterId);
        List<int> baseValues = character.AttrValue;
        for (int index = 0; index < baseValues.Count; index++)
            attrs[index + 1] = attrs.GetValueOrDefault(index + 1) + baseValues[index];

        int talentLevel = m.State.PlaySave.TalentLevel;
        foreach (Theatre6TalentTable talent in EcoTables.Talents.Value)
        {
            if (talent.Level > talentLevel || talent.AttrTypes <= 0)
                continue;
            attrs[talent.AttrTypes] = attrs.GetValueOrDefault(talent.AttrTypes) + talent.AttrNums;
        }

        foreach (Theatre6AttrPackState pack in run.File.AttrPacks)
        {
            Theatre6AttrPackTable row = EcoTables.AttrPack(pack.PackId);
            List<int> types = row.AttrTypes;
            List<int> values = row.AttrNums;
            for (int index = 0; index < types.Count && index < values.Count; index++)
            {
                if (types[index] > 0)
                    attrs[types[index]] = attrs.GetValueOrDefault(types[index]) + values[index] * pack.Num;
            }
        }

        foreach (Theatre6LiveBuffState buff in run.Buffs.Values)
        {
            Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
            if (row.BuffEffectType != BuffEffectAttr)
                continue;
            List<int> parameters = row.BuffEffectParams;
            int units = buff.TriggerCount;
            for (int index = 0; index + 1 < parameters.Count; index += 2)
            {
                if (parameters[index] > 0)
                    attrs[parameters[index]] = attrs.GetValueOrDefault(parameters[index]) + parameters[index + 1] * units;
            }
        }

        if (attrs.ContainsKey(6))
        {
            int baseSan = EcoTables.Stage(run.StageId).BaseSan;
            run.MaxSan = Math.Max(1, baseSan + attrs[6]);
            run.CurSan = Math.Min(run.CurSan, run.MaxSan);
        }

        List<Theatre6AttrData> changed = new();
        List<Theatre6AttrState> rebuilt = new();
        foreach ((int attrId, int value) in attrs.OrderBy(entry => entry.Key))
        {
            Theatre6AttrState? existing = run.File.Attrs.FirstOrDefault(attr => attr.AttrId == attrId);
            if (existing is null)
            {
                rebuilt.Add(new Theatre6AttrState { AttrId = attrId, Value = value });
                changed.Add(new Theatre6AttrData { AttrId = attrId, Value = value });
                continue;
            }

            if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(new Theatre6AttrData { AttrId = attrId, Value = value });
            }

            rebuilt.Add(existing);
        }

        run.File.Attrs = rebuilt;
        if (pushChanges && changed.Count > 0)
            m.Push(new NotifyTheatre6AttrChange { AttrList = changed });
    }

    /// <summary>
    /// Fighting score published to the client. Mirrors the documented server settlement arithmetic:
    /// equipped (non-bag) skills, remaining base active skills for empty active slots, relic save scores
    /// and attribute save scores.
    /// </summary>
    internal static void RecalculateScore(Mutation m, Theatre6RunState run, bool pushChange = true)
    {
        long score = 0;
        foreach (Theatre6SkillState skill in run.File.Skills)
        {
            Theatre6SkillTable row = EcoTables.Skill(skill.SkillId);
            if (skill.SlotType != SlotTypeBag)
                score += row.SaveScore;
        }

        Theatre6CharacterTable character = EcoTables.Character(run.File.CharacterId);
        List<int> baseSkills = character.BaseSkill;
        for (int position = 1; position <= baseSkills.Count; position++)
        {
            if (FindSkillAt(run, SlotTypeActive, position) is not null || baseSkills[position - 1] <= 0)
                continue;
            Theatre6SkillTable row = EcoTables.Skill(baseSkills[position - 1]);
            score += row.SaveScore;
        }

        foreach (Theatre6AttrPackState pack in run.File.AttrPacks)
            score += (long)EcoTables.AttrPack(pack.PackId).SaveScore.GetValueOrDefault() * pack.Num;
        foreach (Theatre6AttrState attr in run.File.Attrs)
            score += (long)EcoTables.Attr(attr.AttrId).SaveScore.GetValueOrDefault() * attr.Value;

        int total = (int)Math.Clamp(score, 0, int.MaxValue);
        if (run.ScoreTotal == total)
            return;
        int previous = run.ScoreTotal;
        run.ScoreTotal = total;
        if (pushChange)
            m.Push(new Theatre6TotalScoreNotify { TotalScoreOld = previous, TotalScoreNew = total });
    }

    #endregion

    #region Star-up upgrades

    /// <summary>
    /// Authored star-up step for a live skill-up buff instance (Theatre6BuffLevelUpSkillRequest).
    /// SkillId 0 is the client's explicit "cancel the remaining chances" and consumes the instance.
    /// </summary>
    internal static Theatre6SkillUpdate UpgradeSkillByBuff(Mutation m, Theatre6RunState run, Theatre6LiveBuffState buff, int skillId)
    {
        Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
        Require(row.BuffEffectType == BuffEffectSkillUp, ErrorBuffUpgradeLimit);
        if (skillId <= 0)
        {
            RemoveBuff(m, run, buff, row.DeathType);
            return new Theatre6SkillUpdate();
        }

        Theatre6SkillState? target = FindSkill(run, skillId);
        Require(target is not null, ErrorSkillNotExist);
        List<int> parameters = row.BuffEffectParams;
        int step = parameters.Count > 1 && parameters[1] > 0 ? parameters[1] : 1;
        int levelLimit = parameters.Count > 2 ? parameters[2] : 0;
        int qualityLimit = parameters.Count > 3 ? parameters[3] : 0;
        Theatre6SkillUpdate update = new();
        bool upgraded = false;
        for (int index = 0; index < step; index++)
        {
            Theatre6SkillTable current = EcoTables.Skill(target!.SkillId);
            Require(levelLimit <= 0 || current.Level < levelLimit, ErrorBuffUpgradeLimit);
            Require(qualityLimit <= 0 || current.Quality <= qualityLimit, ErrorBuffUpgradeLimit);
            int next = NextLevelSkillId(target.SkillId);
            Require(next > 0, ErrorSkillMax);
            target.SkillId = next;
            update.ReplaceSkills ??= new List<Theatre6SkillData>();
            update.ReplaceSkills.Add(CloneSkill(target));
            upgraded = true;
        }

        if (upgraded)
        {
            RecalculateAttrs(m, run, pushChanges: false);
            RecalculateScore(m, run);
        }

        buff.TriggerCount++;
        if (buff.TriggerCount >= SkillUpChances(buff.BuffId))
            RemoveBuff(m, run, buff, row.DeathType);
        else
            m.Push(new NotifyTheatre6BuffUpdate { BuffDatas = new List<Theatre6LiveBuffData> { ToWireLiveBuff(buff) } });
        return update;
    }

    #endregion
}
