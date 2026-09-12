using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.dlcworld;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;
using AscNet.Table.V2.share.reward;
using MessagePack;
using System.Globalization;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static bool OwnsDlcWorld(int worldId) => worldId == 200;

    // XAFKCharBase adds the authored normal attack itself. Only equipped item skills
    // belong here; these are mode IDs, not ordinary CharacterData/Robot/NpcGroupList.
    internal static Theatre5AutoChessNpcData BuildPlayerActor(Player player, Theatre5AdventureData adventure, int fashionId)
    {
        var character = Rows<Theatre5CharacterTable>().Single(row => row.Id == adventure.CharacterId);
        Require(character.FashionIds.Contains(fashionId), 20280004);
        return new()
        {
            TemplateId = character.TemplateId, Name = player.PlayerData.Name,
            HeadFrameId = checked((int)player.PlayerData.CurrHeadFrameId),
            AutoChessData = new()
            {
                CharacterId = adventure.CharacterId, CharacterLevel = adventure.CharacterLv, FashionId = fashionId,
                Skills = adventure.BagData.SkillDict.OrderBy(pair => pair.Key).Select(pair => pair.Value.ItemId).ToList(),
                RuneEvolves = adventure.BagData.RuneDict.OrderBy(pair => pair.Key).Select(pair => new Theatre5RuneEvolve
                { RuneId = pair.Value.ItemId, IsStrengthen = pair.Value.IsStrengthen }).ToList(),
                Relics = adventure.RelicOrders.Select(id => adventure.BagData.RelicDict[id].ItemId).ToList(),
                WeaponIds = Rows<Theatre5CharacterFashionTable>().Single(row => row.Id == fashionId).DlcWeaponId.Where(id => id > 0).ToArray()
            }
        };
    }

    internal static void HandleDlcEnter(Session session, Packet.Request packet) =>
        Handle<DlcSingleEnterFightRequest, DlcSingleEnterFightResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Require(OwnsDlcWorld(request.WorldId), 20280013);
            var adventure = Adventure(m);
            int mode = m.Data.PvpType;
            long runId = mode == 1 ? m.State.PvpRunId : m.State.PveRunId;
            Theatre5Attempt? prior = mode == 1 ? m.State.PvpAttempt : m.State.PveAttempt;
            bool sameRound = prior != null && prior.Epoch == m.State.Epoch && prior.RunId == runId
                && prior.Mode == mode && prior.RoundNum == adventure.RoundNum;
            if (sameRound && !prior!.Settled)
            {
                Require(adventure.Status == 6 && (request.LevelId == null || request.LevelId == 0
                    || request.LevelId == (m.Data.PveAdventureData?.PveChapterData?.CurPveChapterLevel?.Level)), 20280006);
                response.WorldData = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(prior.EntryResponse).WorldData;
                m.Push(new NotifyTheatre5Effect { EffectQueue = Clone(prior.Effects) });
                return;
            }
            bool retry = sameRound && (prior!.CheckFailed || prior.Interrupted);
            Require(adventure.Health > 0 && (retry || adventure.Status == (mode == 1 ? 5 : 4)), 20280006);
            if (!retry)
            {
                Require(adventure.BagData.TempItemDict.Count == 0 && adventure.SkillChoiceData == null, 20280006);
                Require(mode != 2 || m.Data.PveAdventureData!.ItemBoxSelectData.Count == 0, 20280006);
                Require(adventure.RandomRelics.Count == 0, 20283006);
                Require(!adventure.IsCanFreeUnlockGrid, 20283013);
                Require(adventure.ChooseMissions.Count == 0 && adventure.Missioning?.MissionState != 2, 20280006);
                Require(!CanLevelUp(m), 20283009);
            }

            Theatre5WorldData world;
            List<Theatre5Effect> effects;
            int chapterId = m.Data.PveAdventureData?.PveChapterData?.ChapterId ?? 0;
            if (retry)
            {
                world = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(prior!.EntryResponse).WorldData!;
                effects = Clone(prior.Effects);
                m.Push(new NotifyTheatre5Effect { EffectQueue = Clone(effects) });
            }
            else
            {
                TriggerEffects(m, "BeforeBattle");
                UpdateMissionProgress(m, "BeforeBattle");
                effects = Clone(adventure.EffectQueue);
                var config = Rows<DlcWorldTable>().Single(row => row.WorldId == request.WorldId);
                Require(config.WorldType == 5 && config.RebootId == 1, 20280013);
                int fashion = mode == 1 ? m.Data.Characters[adventure.CharacterId].FashionId
                    : m.Data.PveCharacters[adventure.CharacterId].FashionId;
                var self = BuildPlayerActor(session.player, adventure, fashion);
                Theatre5AutoChessNpcData enemy;
                int levelId;
                if (mode == 1)
                {
                    Require(request.LevelId == null || request.LevelId == 0, 20280013);
                    Require(m.State.PvpOpponent != null, 20281012);
                    enemy = Clone(m.State.PvpOpponent);
                    // Local stage rotation uses only stages in the authored PvP level group.
                    var stages = Rows<Theatre5PveFightTable>().Where(row => row.GroupId == ConfigInt("PvpLevelGroup"))
                        .Select(row => row.Stage).Distinct().Order().ToArray();
                    Require(stages.Length > 0, 20280013);
                    levelId = stages[(int)((runId + adventure.RoundNum) % stages.Length)];
                }
                else
                {
                    var chapter = m.Data.PveAdventureData?.PveChapterData;
                    Require(chapter?.CurPveChapterLevel != null, 20282019);
                    Require(request.LevelId == chapter.CurPveChapterLevel.Level, 20282020);
                    var chapterConfig = Rows<Theatre5PveChapterTable>().Single(row => row.Id == chapter.ChapterId);
                    var level = Rows<Theatre5PveChapterLevelTable>().Single(row => row.GroupId == chapterConfig.LevelGroup
                        && row.Level == chapter.CurPveChapterLevel.Level);
                    var fights = Rows<Theatre5PveFightTable>().Where(row => row.GroupId == level.FightGroup).OrderBy(row => row.Id).ToArray();
                    Require(fights.Length > 0, 20282044);
                    // A run/round owns its selected authored encounter. Retry never rerolls it.
                    var fight = fights[(int)((runId + adventure.RoundNum) % fights.Length)];
                    Require(fight.EnemyType == 1, 20282044);
                    var monster = Rows<Theatre5PveMonsterTable>().Single(row => row.MonsterGroup == fight.EnemyId
                        && row.MonsterLevel == level.MonsterLevel);
                    var character = Rows<Theatre5CharacterTable>().Single(row => row.Id == monster.MonsterNpcId);
                    int enemyFashion = character.FashionIds.First(id => id > 0);
                    int nerfLevel = ConfigInt("PveHealth") - adventure.Health - chapter.ContinueWin;
                    enemy = new()
                    {
                        TemplateId = character.TemplateId, Name = fight.EnemyName,
                        AutoChessData = new()
                        {
                            CharacterId = character.Id, CharacterLevel = 0, FashionId = enemyFashion,
                            RuneEvolves = monster.EquipId.Where(id => id > 0).Select(id => new Theatre5RuneEvolve { RuneId = id }).ToList(),
                            // The client reapplies this map on positive chapter LevelId;
                            // freezing it also preserves the same buffs on retry LevelId0.
                            MagicIds = nerfLevel > 0
                                ? level.MonsterNerfBuff.Where(id => id > 0).Distinct().ToDictionary(id => id, _ => nerfLevel)
                                : [],
                            WeaponIds = Rows<Theatre5CharacterFashionTable>().Single(row => row.Id == enemyFashion).DlcWeaponId.Where(id => id > 0).ToArray()
                        }
                    };
                    levelId = fight.Stage;
                }
                Require(levelId is >= 1071 and <= 1076, 20280013);
                world = new()
                {
                    WorldId = config.WorldId, WorldType = config.WorldType, RebootId = config.RebootId,
                    LevelId = levelId, ServerControllerSeed = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                    Players = [new() { Id = checked((int)session.player.PlayerData.Id), Name = session.player.PlayerData.Name }],
                    AutoChessGameplayData = new() { RoundNum = adventure.RoundNum, SelfData = self, EnemyData = enemy }
                };
            }
            long attemptId = m.State.NextAttemptId = checked(m.State.NextAttemptId + 1);
            world.RoomId = $"theatre5:{session.player.PlayerData.Id}:{m.State.Epoch}:{runId}:{attemptId}";
            response.WorldData = world;
            var attempt = new Theatre5Attempt
            {
                Epoch = m.State.Epoch, RunId = runId, AttemptId = attemptId, Mode = mode,
                WorldId = world.WorldId, LevelId = world.LevelId, ChapterId = mode == 2 ? chapterId : 0,
                RoundNum = adventure.RoundNum, RoomId = world.RoomId, Seed = world.ServerControllerSeed,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                OpponentPlayerId = mode == 1 ? m.State.PvpOpponentPlayerId : 0,
                OpponentRobotId = mode == 1 ? m.State.PvpOpponentRobotId : 0,
                OpponentSnapshot = MessagePackSerializer.Serialize(world.AutoChessGameplayData!.EnemyData),
                EntryResponse = MessagePackSerializer.Serialize(response), Effects = effects
            };
            if (mode == 1) m.State.PvpAttempt = attempt; else m.State.PveAttempt = attempt;
            adventure.Status = 6;
        });

    internal static void HandleDlcSettle(Session session, Packet.Request packet) =>
        Handle<DlcSingleFightSettleRequest, DlcSingleFightSettleResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            var report = request.DlcReportWorldResult?.DlcFightSettleData;
            Require(report?.WorldData != null && OwnsDlcWorld(report.WorldData.WorldId), 20280013);
            int owner = checked((int)session.player.PlayerData.Id);
            Require(report.WorldData.Players is { Count: 1 } && report.WorldData.Players[0] is { } worldPlayer && worldPlayer.Id == owner
                && report.PlayerData is { Count: 1 } && report.PlayerData.TryGetValue(owner, out var player)
                && player != null && player.PlayerId == owner, 20280017);
            Require(report.SettleState is >= 0 and <= 3, 20280017);
            int mode = m.Data.PvpType;
            long runId = mode == 1 ? m.State.PvpRunId : m.State.PveRunId;
            var attempt = mode == 1 ? m.State.PvpAttempt : m.State.PveAttempt;
            bool minimal = IsMinimalBattleReport(report);
            string key = SemanticRequestKey(nameof(DlcSingleFightSettleRequest), request);
            if (!minimal && attempt?.Settled == true && attempt.RunId == runId && attempt.Epoch == m.State.Epoch
                && attempt.SettleRequestKey == key && attempt.SettleResponse != null)
            {
                response.DlcFightSettleData = MessagePackSerializer.Deserialize<DlcSingleFightSettleResponse>(attempt.SettleResponse).DlcFightSettleData;
                return;
            }
            var adventure = Adventure(m);
            if (minimal)
            {
                Require(!report.IsPlayerWin && report.SettleState is 0 or 2 or 3, 20280017);
                Require(adventure.Health > 0, 20280008);
                if (report.SettleState != 2)
                    Require(attempt != null && attempt.Epoch == m.State.Epoch && attempt.RunId == runId
                        && attempt.RoundNum == adventure.RoundNum && (!attempt.Settled || attempt.CheckFailed)
                        && (adventure.Status == 6 || attempt.CheckFailed), 20280008);
            }
            else
            {
                Require(attempt != null && !attempt.Settled && attempt.Epoch == m.State.Epoch && attempt.RunId == runId
                    && attempt.Mode == mode && attempt.RoundNum == adventure.RoundNum && adventure.Status == 6, 20280008);
                // Identity mismatches are stale/foreign reports, not attempts at this battle.
                Require(report.WorldData.RoomId == attempt.RoomId && report.WorldData.WorldId == attempt.WorldId
                    && report.WorldData.LevelId == attempt.LevelId && report.WorldData.ServerControllerSeed == attempt.Seed,
                    20280017);
            }

            var accepted = Clone(report);
            var result = new Theatre5AutoChessGameplayResult
            {
                RoundNum = adventure.RoundNum, Health = adventure.Health, CheckFailTimes = adventure.CheckFailTimes,
                CommonFightCnt = Clone(m.Data.CommonFightCnt), IsCanFreeUnlockGrid = adventure.IsCanFreeUnlockGrid,
                PveChapterData = mode == 2 ? Clone(m.Data.PveAdventureData?.PveChapterData) : null
            };
            if (mode == 1) FillPvpResult(m, result);
            // Rebuild the durable expectation once per settlement: the same instance is
            // echoed back and used to verify the incoming report.
            Theatre5WorldData? expectedNative = attempt != null && attempt.RunId == runId && attempt.Epoch == m.State.Epoch
                && attempt.RoundNum == adventure.RoundNum ? ReadNativeWorld(attempt) : null;
            if (expectedNative != null) accepted.WorldData = expectedNative;
            string? nativeFailure = null;
            // Non-minimal settlements required the matching attempt above, so the rebuilt
            // expectation is always available here.
            bool failed = !minimal && !ValidateNativeReport(report, attempt!, expectedNative!, out nativeFailure);
            if (failed)
            {
                session.log.Warn($"Theatre5 battle check rejected: reason={nativeFailure}; mode={mode} runId={runId} roundNum={adventure.RoundNum} attemptId={attempt!.AttemptId} priorCheckFailTimes={adventure.CheckFailTimes} settleState={report.SettleState} isPlayerWin={report.IsPlayerWin} elapsedMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - attempt!.StartedAt}.");
                result.CheckFailTimes = adventure.CheckFailTimes = checked(adventure.CheckFailTimes + 1);
                accepted.IsPlayerWin = false;
                attempt!.CheckFailed = true;
                // A verification failure is not a completed round: status7 would let
                // relog enter the next shop and collect the same round's income again.
                adventure.Status = 6;
                if (adventure.CheckFailTimes >= ConfigInt("BattleCheckFailTimesLimit"))
                {
                    accepted.SettleState = 2;
                    ApplyBattleOutcome(m, accepted, result);
                }
            }
            else
            {
                if (!minimal && report.SettleState == 0)
                    RecordBattleEvidence(m, report);
                ApplyBattleOutcome(m, accepted, result);
            }
            response.DlcFightSettleData = new()
            {
                ResultData = accepted, XAutoChessGameplayResult = result,
                RewardGoodsList = m.Grants.SelectMany(grant => grant.Goods).Select(good =>
                {
                    var row = new RewardGoodsTable { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, Params = good.Params };
                    var type = RewardHandler.GetRewardType(row);
                    Require(type.HasValue, 1);
                    return new RewardGoods { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type.Value };
                }).ToList()
            };
            if (attempt != null && attempt.RunId == runId && attempt.Epoch == m.State.Epoch)
            {
                attempt.Settled = true;
                attempt.Interrupted = report.SettleState == 3 && !failed;
                attempt.SettleRequestKey = key;
                attempt.SettleResponse = MessagePackSerializer.Serialize(response);
            }
        });

    private static void ApplyBattleOutcome(Mutation m, Theatre5DlcFightResultData report, Theatre5AutoChessGameplayResult result)
    {
        // Both a verified native result and validated state0 give-up resolve a round.
        if (report.SettleState == 0)
        {
            var adventure = Adventure(m);
            var bag = adventure.BagData;
            bag.RoundNumWithoutGridUnlock = checked(bag.RoundNumWithoutGridUnlock + 1);
            // EN AdventureDataBase:895 grants one free grid after the last round's
            // victory. The client trusts this flag; never offer an impossible unlock.
            adventure.IsCanFreeUnlockGrid = report.IsPlayerWin && bag.RuneGridsNum < ConfigInt("RuneGridMaxNum");
        }
        if (m.Data.PvpType == 1) ApplyPvpBattleResult(m, report, result);
        else ApplyPveBattleResult(m, report, result);
    }

    private static bool IsMinimalBattleReport(Theatre5DlcFightResultData report) =>
        report.WorldData?.AutoChessGameplayData == null && report.WorldData?.LevelId == 0
        && string.IsNullOrEmpty(report.WorldData?.RoomId) && string.IsNullOrEmpty(report.RoomId)
        && string.IsNullOrEmpty(report.FightUid) && report.AutoChessCheckData == null
        && report.NpcSettleInfos is { Count: 0 } && report.StartFightTime == 0 && report.SettleTime == 0
        && report.FinishTime == 0 && report.ChapterId == 0 && report.RoomData is { Length: 0 };

    // Native authority: the shipped 4.7 client's XDlcNpcAttribType (decoded from
    // resources/.../assets/temp/lua/matrix.ab) has 148 entries, ids 0..147, and the
    // shipped Share/dlcworld/DlcWorldAttrib binary header declares exactly those 148
    // columns plus Id. _CalNpcAttribsAfterEnterFightRequest writes all of them, so the
    // projection must seed the same key set: seeding the older 0..104 subset let
    // the exact-count actor map equality reject every genuine native report.
    // The extra 43 columns are zero in all 146 shipped rows, so seeding them 0 is exact;
    // BattleAttributeColumns only needs getters for columns the repo table imports.
    private const int NativeAttributeTypeCount = 148;

    // Only imported nonzero columns need getters; BinaryTable defaults omitted numerics to zero.
    private static readonly (string Name, int Id)[] BattleAttributeColumns =
    [
        ("Life", 0), ("Attack", 1), ("Defense", 3), ("CritP", 5), ("CritDmgRateP", 7),
        ("CharacterValue", 40), ("ExSkillPoint", 41), ("IdleSpinningSpeed", 42), ("RunSpinningSpeed", 43),
        ("DodgeEnergy", 44), ("JumpEnergy", 45), ("DodgeEnergyRegen", 46), ("JumpEnergyRegen", 47),
        ("CustomEnergyGroup1", 48), ("CustomEnergyGroup2", 49), ("CustomEnergyGroup3", 50), ("CustomEnergyGroup4", 51),
        ("Speed", 52), ("RunSpeedCOE", 55), ("JumpSpeedCOE", 56), ("IdleJumpSpeedCOE", 57),
        ("WalkJumpSpeedCOE", 58), ("SprintJumpSpeedCOE", 59), ("RunStartJumpSpeedCOE", 60),
        ("SprintStartJumpSpeedCOE", 61), ("RotationSpeed", 62), ("WalkSpeedCOE", 64), ("SprintSpeedCOE", 66),
        ("CauseDamageThreatCoe", 96), ("RebootValue", 97)
    ];

    // The shipped 4.7 client stores every projected value into the native
    // Dictionary<int,int> through the xLua int caster, which is Lua 5.3's
    // lua_tointegerx(L, idx, NULL) with the managed int32 taking the low bits: only a
    // double with an exact integer representation converts, while a fractional, NaN,
    // infinite or out-of-int64 value becomes 0. The former CLR cast truncated instead,
    // so a fractional source such as IdleSpinningSpeed 10499/10000 = 1.0499 was frozen
    // as 1 while the client held 0 (SelfData.Attribs[42] expected=1 actual=0) and every
    // genuine report was rejected.
    private static int NativeAttributeInt(double value) =>
        double.IsInteger(value) && value >= long.MinValue && value < -(double)long.MinValue
            ? unchecked((int)(long)value) : 0;

    // Durable native expectations are derived from the frozen authorization: the entry
    // response world the client actually received plus the effects that produced it,
    // replayed through the shipped tables. Storing the projection instead lost a
    // fractional source such as IdleSpinningSpeed 10499/10000 behind the converted int,
    // so repairs must come from these frozen inputs, never from guessed integers.
    private static Theatre5WorldData ReadNativeWorld(Theatre5Attempt attempt) =>
        ProjectNativeWorld(MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(attempt.EntryResponse).WorldData!, attempt.Effects);

    private static Theatre5WorldData ProjectNativeWorld(Theatre5WorldData world, List<Theatre5Effect> effects)
    {
        var native = Clone(world);
        // Lua copies the two actors, but does not copy gameplay RoundNum into the native class.
        native.AutoChessGameplayData!.RoundNum = 0;
        ProjectNativeActor(native.AutoChessGameplayData.SelfData!, effects);
        ProjectNativeActor(native.AutoChessGameplayData.EnemyData!, []);
        return native;
    }

    private static void ProjectNativeActor(Theatre5AutoChessNpcData actor, List<Theatre5Effect> effects)
    {
        var data = actor.AutoChessData!;
        var character = Rows<Theatre5CharacterTable>().Single(row => row.Id == data.CharacterId);
        var attributes = Enumerable.Range(0, NativeAttributeTypeCount).ToDictionary(id => id, _ => 0d);
        AddRow(Rows<DlcWorldAttribTable>().Single(row => row.Id == character.AttrId), 1);
        foreach (var rune in data.RuneEvolves)
        {
            var config = Rows<Theatre5ItemRuneTable>().Single(row => row.Id == rune.RuneId);
            int attrId = Convert.ToInt32(rune.IsStrengthen && config.EvolveAttrId > 0 ? config.EvolveAttrId : config.RuneAttrId, CultureInfo.InvariantCulture);
            if (attrId == 0) continue;
            var row = Rows<Theatre5ItemRuneAttrTable>().Single(value => value.Id == attrId);
            for (int index = 0; index < row.AttrTypes.Count; index++)
            {
                if (row.AttrValues[index] <= 0) continue;
                int id = BattleAttributeColumns.Single(column => column.Name == row.AttrTypes[index]).Id;
                attributes[id] += row.AttrValues[index];
            }
        }
        if (data.CharacterLevel > 0)
            AddRow(Rows<DlcWorldAttribTable>().Single(row => row.Id == character.PromotedAttrId), data.CharacterLevel - 1);
        var baseAttributes = new Dictionary<int, double>(attributes);
        foreach (var effect in effects)
        {
            if (effect.Type == 1 && effect.AddBuffResult != null)
                foreach (int buff in effect.AddBuffResult.Buffs) data.MagicIds[buff] = 1;
            if (effect.Type == 9 && effect.AddAttrResult is { } attr)
                attributes[attr.AttrType] = Math.Floor(attributes[attr.AttrType] + attr.FixVal
                    + baseAttributes[attr.AttrType] * attr.RateVal / 10000d + (double)attr.SpecificVal * attr.SpecificRateVal / 10000d);
        }
        data.Attribs = attributes.ToDictionary(pair => pair.Key, pair => NativeAttributeInt(pair.Value));
        void AddRow(DlcWorldAttribTable row, int count)
        {
            foreach (var column in BattleAttributeColumns)
            {
                double value = Convert.ToDouble(typeof(DlcWorldAttribTable).GetProperty(column.Name)!.GetValue(row), CultureInfo.InvariantCulture);
                // Reader.ReadFloat divides the extracted raw signed word by10000.
                if (column.Id is 42 or 43 || column.Id is >= 52 and <= 66) value /= 10000d;
                attributes[column.Id] += column.Id is >= 52 and <= 66 ? Math.Floor(value * count * 1000) : value * count;
            }
        }
    }

    // Same predicates as the native-authority review, but a rejection records the
    // operands of the first failing predicate so a genuine client report can be
    // diagnosed without reconstructing the incoming world from the echoed response.
    private static bool ValidateNativeReport(Theatre5DlcFightResultData report, Theatre5Attempt attempt, Theatre5WorldData expected, out string? reason)
    {
        reason = null;
        var world = report.WorldData!;
        if (world.WorldType != expected.WorldType)
            return FailNativeReport(out reason, $"WorldType expected={expected.WorldType} actual={world.WorldType}");
        if (world.RebootId != expected.RebootId)
            return FailNativeReport(out reason, $"RebootId expected={expected.RebootId} actual={world.RebootId}");
        if (world.Online) return FailNativeReport(out reason, "Online expected=false actual=true");
        if (world.IsLocalDebug) return FailNativeReport(out reason, "IsLocalDebug expected=false actual=true");
        if (world.IsTeaching != expected.IsTeaching)
            return FailNativeReport(out reason, $"IsTeaching expected={expected.IsTeaching} actual={world.IsTeaching}");
        if (world.IsSingleOnline != expected.IsSingleOnline)
            return FailNativeReport(out reason, $"IsSingleOnline expected={expected.IsSingleOnline} actual={world.IsSingleOnline}");
        if (world.MissionId != expected.MissionId)
            return FailNativeReport(out reason, $"MissionId expected={expected.MissionId} actual={world.MissionId}");
        if (world.AutoChessGameplayData is not { } gameplay)
            return FailNativeReport(out reason, "AutoChessGameplayData expected native gameplay data actual=<null>");
        if (gameplay.RoundNum != expected.AutoChessGameplayData!.RoundNum)
            return FailNativeReport(out reason, $"AutoChessGameplayData.RoundNum expected={expected.AutoChessGameplayData!.RoundNum} actual={gameplay.RoundNum}");
        if (ActorMismatch("SelfData", gameplay.SelfData, expected.AutoChessGameplayData!.SelfData) is { } self)
            return FailNativeReport(out reason, self);
        if (ActorMismatch("EnemyData", gameplay.EnemyData, expected.AutoChessGameplayData!.EnemyData) is { } enemy)
            return FailNativeReport(out reason, enemy);
        // The native SINGLE producer leaves these shared multiplayer fields unset.
        // FightUid is not supplied by XWorldData and must not be invented from RoomId.
        if (!string.IsNullOrEmpty(report.FightUid)) return FailNativeReport(out reason, $"FightUid expected empty actual={FieldString(report.FightUid)}");
        if (!string.IsNullOrEmpty(report.RoomId)) return FailNativeReport(out reason, $"RoomId expected empty actual={FieldString(report.RoomId)}");
        if (report.StartFightTime != 0) return FailNativeReport(out reason, $"StartFightTime expected=0 actual={report.StartFightTime}");
        if (report.SettleTime != 0) return FailNativeReport(out reason, $"SettleTime expected=0 actual={report.SettleTime}");
        if (report.ChapterId != 0) return FailNativeReport(out reason, $"ChapterId expected=0 actual={report.ChapterId}");
        if (report.RoomData is { Length: > 0 }) return FailNativeReport(out reason, $"RoomData expected empty actual length={report.RoomData.Length}");
        // Native full interruption reports can precede the five-second battle intro.
        // They do not award a combat outcome and need no fabricated combat record.
        if (report.SettleState is 1 or 2 or 3)
            return !report.IsPlayerWin || FailNativeReport(out reason, $"SettleState={report.SettleState} must not report IsPlayerWin=true");
        if (report.FinishTime < 5) return FailNativeReport(out reason, $"FinishTime expected>=5 actual={report.FinishTime}");
        if (report.AutoChessCheckData is not { } check) return FailNativeReport(out reason, "AutoChessCheckData expected native combat record actual=<null>");
        if (RecordMismatch("MyData", check.MyData) is { } myData) return FailNativeReport(out reason, myData);
        if (RecordMismatch("EnemyData", check.EnemyData) is { } enemyData) return FailNativeReport(out reason, enemyData);
        if (check.ConditionParam == null) return FailNativeReport(out reason, "ConditionParam expected non-null actual=<null>");
        // XFightResult.Settle and OnSingleSettle receive the same float Fight.Time:
        // FinishTime=trunc(T), ActBattleTime=trunc(float32(float32(T-5)*1000)).
        long minTime = (long)((float)(report.FinishTime - 5) * 1000f);
        long maxTime = (long)((float)((double)report.FinishTime + 1 - 5) * 1000f);
        if (check.ActBattleTime < minTime || check.ActBattleTime > maxTime)
            return FailNativeReport(out reason, $"ActBattleTime expected={minTime}..{maxTime} actual={check.ActBattleTime} FinishTime={report.FinishTime}");
        // Native UI exposes1x/2x; UseDoubleSpeed is sticky once2x was used.
        // The authorized server entry predates client loading, so elapsed wall time
        // is a conservative upper bound, including one integer-second quantization.
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - attempt.StartedAt;
        long finishLimit = elapsed * (check.UseDoubleSpeed ? 2 : 1) / 1000 + 1;
        if (elapsed < 0 || report.FinishTime > finishLimit)
            return FailNativeReport(out reason, $"FinishTime expected<={finishLimit} actual={report.FinishTime} elapsedMs={elapsed} useDoubleSpeed={check.UseDoubleSpeed}");
        // This checks source-format invariants, not a fabricated native combat simulation.
        // The five *Limit=1 operands have no recovered composition and are NOT
        // interpreted as literal damage/time caps or an invented expected-combat equation.
        if (report.NpcSettleInfos is not { } settleInfos) return FailNativeReport(out reason, "NpcSettleInfos expected non-null actual=<null>");
        foreach (var pair in settleInfos)
            if (pair.Value is null || pair.Value.LeftHp < 0)
                return FailNativeReport(out reason, $"NpcSettleInfos[{pair.Key}].LeftHp expected>=0 actual={(pair.Value is null ? "<null>" : pair.Value.LeftHp)}");
        return true;
    }

    private static bool FailNativeReport(out string? reason, string message)
    {
        reason = message;
        return false;
    }

    private static string? ActorMismatch(string side, Theatre5AutoChessNpcData? actual, Theatre5AutoChessNpcData? expected)
    {
        if (actual?.AutoChessData is not { } a || expected?.AutoChessData is not { } e)
            return $"{side}.AutoChessData missing (actual={(actual?.AutoChessData is null ? "<null>" : "present")} expected={(expected?.AutoChessData is null ? "<null>" : "present")})";
        if (actual.TemplateId != expected.TemplateId) return $"{side}.TemplateId expected={expected.TemplateId} actual={actual.TemplateId}";
        if (actual.Name != expected.Name) return $"{side}.Name mismatch expected={FieldString(expected.Name)} actual={FieldString(actual.Name)}";
        if (actual.HeadFrameId != expected.HeadFrameId) return $"{side}.HeadFrameId expected={expected.HeadFrameId} actual={actual.HeadFrameId}";
        if (a.CharacterId != e.CharacterId) return $"{side}.CharacterId expected={e.CharacterId} actual={a.CharacterId}";
        if (a.CharacterLevel != e.CharacterLevel) return $"{side}.CharacterLevel expected={e.CharacterLevel} actual={a.CharacterLevel}";
        if (a.FashionId != e.FashionId) return $"{side}.FashionId expected={e.FashionId} actual={a.FashionId}";
        if (SequenceMismatch("Skills", a.Skills, e.Skills) is { } skills) return $"{side}.{skills}";
        if (SequenceMismatch("WeaponIds", a.WeaponIds, e.WeaponIds) is { } weapons) return $"{side}.{weapons}";
        if (SequenceMismatch("Relics", a.Relics, e.Relics) is { } relics) return $"{side}.{relics}";
        if (a.RuneEvolves is null || a.RuneEvolves.Count != e.RuneEvolves.Count)
            return $"{side}.RuneEvolves count expected={e.RuneEvolves.Count} actual={(a.RuneEvolves is null ? "<null>" : a.RuneEvolves.Count.ToString())}";
        for (int index = 0; index < a.RuneEvolves.Count; index++)
        {
            var first = a.RuneEvolves[index];
            var second = e.RuneEvolves[index];
            if (first is null || first.RuneId != second.RuneId || first.IsStrengthen != second.IsStrengthen)
                return $"{side}.RuneEvolves[{index}] expected=(RuneId={second.RuneId},IsStrengthen={second.IsStrengthen}) actual={(first is null ? "<null>" : $"(RuneId={first.RuneId},IsStrengthen={first.IsStrengthen})")}";
        }
        if (MapMismatch("Attribs", a.Attribs, e.Attribs) is { } attribs) return $"{side}.{attribs}";
        if (MapMismatch("MagicIds", a.MagicIds, e.MagicIds) is { } magic) return $"{side}.{magic}";
        return null;
    }

    private static string? MapMismatch(string label, Dictionary<int, int>? actual, Dictionary<int, int> expected)
    {
        if (actual is null) return $"{label} expected count={expected.Count} actual=<null>";
        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out int value)) return $"{label}[{pair.Key}] missing (expected={pair.Value})";
            if (value != pair.Value) return $"{label}[{pair.Key}] expected={pair.Value} actual={value}";
        }
        if (actual.Count == expected.Count) return null;
        foreach (var pair in actual)
            if (!expected.ContainsKey(pair.Key)) return $"{label}[{pair.Key}] unexpected (actual={pair.Value})";
        return null;
    }

    // Lists and untrusted strings are reported as bounded operands: lengths plus the
    // first differing index, or an escaped/truncated value, never a raw payload dump.
    private static string? SequenceMismatch(string label, IReadOnlyList<int>? actual, IReadOnlyList<int> expected)
    {
        if (actual is null) return $"{label} expected count={expected.Count} actual=<null>";
        if (actual.Count != expected.Count) return $"{label} count expected={expected.Count} actual={actual.Count}";
        for (int index = 0; index < actual.Count; index++)
            if (actual[index] != expected[index]) return $"{label}[{index}] expected={expected[index]} actual={actual[index]}";
        return null;
    }

    private static string FieldString(string? value) => value is null
        ? "<null>"
        : $"len={value.Length} value={System.Text.Json.JsonSerializer.Serialize(value.Length > 32 ? value[..32] : value)}";

    private static string? RecordMismatch(string side, Theatre5AutoChessNpcRecordData? record)
    {
        if (record?.RecordData is not { } data) return $"{side}.RecordData expected native record actual=<null>";
        if (record.TotalDamage < 0) return $"{side}.TotalDamage expected>=0 actual={record.TotalDamage}";
        if (record.TotalCure < 0) return $"{side}.TotalCure expected>=0 actual={record.TotalCure}";
        if (record.TotalProtector < 0) return $"{side}.TotalProtector expected>=0 actual={record.TotalProtector}";
        if (record.GemRecord is null) return $"{side}.GemRecord expected non-null actual=<null>";
        foreach (var pair in record.GemRecord)
            if (pair.Value < 0 || !Rows<Theatre5ItemRuneTable>().Any(row => row.Id == pair.Key))
                return $"{side}.GemRecord[{pair.Key}] expected>=0 and a known rune actual={pair.Value}";
        if (record.RateRecord is null) return $"{side}.RateRecord expected non-null actual=<null>";
        foreach (int id in record.RateRecord.Keys)
            if (id is < 0 or > 104) return $"{side}.RateRecord key expected 0..104 actual={id}";
        if (ConsistentRecordsMismatch("SkillDamageRecord", record.SkillDamageRecord, data.DamageRecord, record.TotalDamage) is { } damage)
            return $"{side}.{damage}";
        if (ConsistentRecordsMismatch("SkillCureRecord", record.SkillCureRecord, data.CureRecord, record.TotalCure) is { } cure)
            return $"{side}.{cure}";
        if (ConsistentRecordsMismatch("ProtectorRecord", record.ProtectorRecord, data.ProtectorRecord, record.TotalProtector) is { } protector)
            return $"{side}.{protector}";
        return null;
    }

    private static string? ConsistentRecordsMismatch(string label, Dictionary<int, int>? flat, Dictionary<int, Dictionary<int, int>>? nested, int total)
    {
        if (flat is null || nested is null || nested.Values.Any(value => value is null))
            return $"{label} expected flat and nested records actual flat={(flat is null ? "<null>" : "present")} nested={(nested is null ? "<null>" : "present")}";
        // Native AddSkill{Damage,Cure,Protector} records every category in Total,
        // while ConvertClientDataToServer exposes category0 as the flat map.
        if (nested.TryGetValue(0, out var zero))
        {
            if (MapMismatch("category0", flat, zero) is { } category) return $"{label}.{category}";
        }
        else if (flat.Count != 0) return $"{label}[0] expected empty flat map actual count={flat.Count}";
        long sum = nested.Values.Sum(values => values.Values.Sum(value => (long)value));
        return sum == total ? null : $"{label} total expected={total} actualSum={sum}";
    }

    private static void RecordBattleEvidence(Mutation m, Theatre5DlcFightResultData report)
    {
        var check = report.AutoChessCheckData!;
        foreach (var pair in check.MyData!.GemRecord) UpdateMissionProgress(m, "BattleRune", pair.Value, pair.Key);
        foreach (var pair in check.MyData.RateRecord)
            UpdateMissionProgress(m, "BattleAttribute", pair.Value, pair.Key);
        UpdateMissionProgress(m, "BattleDamage", check.MyData!.TotalDamage);
        if (report.IsPlayerWin) UpdateMissionProgress(m, "BattleWinTime", (int)Math.Min(check.ActBattleTime, int.MaxValue));
        UpdateMissionProgress(m, "BattleHealShield", check.MyData.TotalCure);
        UpdateMissionProgress(m, "BattleHealShield", check.MyData.TotalProtector);
    }
}
