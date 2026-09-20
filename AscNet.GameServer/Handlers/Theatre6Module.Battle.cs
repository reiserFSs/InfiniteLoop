using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

// Theatre6 native DLC boundary. The shipped EN client (matrix/xmodule/xtheatre6/subagency/
// XTheatre6BattleAgency.lua, byte-identical to the copy inside the installed matrix bundle) is the
// producer and consumer of every field below:
//   * XTheatre6NpcData <- _GetXWorldGameplayData copies exactly TemplateId, Name, HeadFrameId,
//     CharacterId, CharacterLevel, FashionId, PvpEnvMagicId, Attribs, GameplayAttribs, Skills,
//     MagicIds, WeaponIds, Relics, MagicIdsWithoutLevel, PvpBuffActionRecord, field by field.
//   * XTheatre6GameplayData <- _GetXWorldData copies both actors; RoundNum and RoundResults are
//     assigned for PvP only, RoundResults from the client's own cached round history.
//   * XWorldData <- _GetXWorldData copies WorldId, LevelId, WorldType, PlayerSeeds and Players{Id,Name}.
//     It never copies RoomId, ServerControllerSeed, MissionId, Online, IsTeaching or IsSingleOnline,
//     so those stay at their native defaults and are deliberately not validated here.
// Combat stays native: the server freezes the world it authorised, re-projects the same actors from
// that frozen entry and accepts a client outcome only when the world identity and both actor
// projections are exactly the ones it issued.
internal static partial class Theatre6Module
{
    // XEnumConst.Theatre6 (xmodule/XEnumConst.lua:3668-3830).
    private const int AttrTypeDlc = 1;
    private const int AttrTypeGameplay = 2;
    private const int SkillSlotTypeBag = 4;
    private const int DlcWorldTypeTheatre6 = 7;
    private const int StageBuffEffectCombatMagic = 8;

    // XCode.Theatre6 entries used by this boundary (EN bytes/share/text/CodeText.json).
    internal const int FightWorldError = 20423053;      // World ID not found
    internal const int FightModeError = 20423013;       // Current mode data not found
    internal const int FightRoomError = 20423014;       // Current room data not found
    internal const int FightNotFound = 20423035;        // Battle not found
    internal const int FightDataError = 20423041;       // Data not found
    internal const int FightConfigError = 20423039;     // Battle configuration error
    internal const int FightArchiveError = 20423044;    // Archive ID error

    private static readonly Lazy<Dictionary<int, Theatre6MonsterTable>> BattleMonsters = new(() => TableReaderV2.Parse<Theatre6MonsterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrTable>> BattleAttrs = new(() => TableReaderV2.Parse<Theatre6AttrTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrPackTable>> BattleAttrPacks = new(() => TableReaderV2.Parse<Theatre6AttrPackTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6CharacterTable>> BattleCharacters = new(() => TableReaderV2.Parse<Theatre6CharacterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6CharacterFashionTable>> BattleFashions = new(() => TableReaderV2.Parse<Theatre6CharacterFashionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<HashSet<int>> PveWorldIds = new(() => TableReaderV2.Parse<Theatre6ActivityTable>().Select(row => row.WorldId).Where(id => id > 0).ToHashSet());
    private static readonly Lazy<HashSet<int>> PvpWorldIds = new(() => TableReaderV2.Parse<Theatre6PvpConfigTable>().Where(row => row.Key == "DlcFightWorldId").Select(row => row.Values).Where(id => id > 0).ToHashSet());
    private static readonly Lazy<Dictionary<int, Theatre6StageBuffTable>> BattleStageBuffs = new(() => TableReaderV2.Parse<Theatre6StageBuffTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, WorldTable>> BattleWorlds = new(() => TableReaderV2.Parse<WorldTable>().ToDictionary(row => row.Id));

    // Zero authorises the native world's configured default; concrete levels retain exact identity.
    internal static int ResolveNativeLevelId(int worldId, int levelId) =>
        levelId == 0 ? Row(BattleWorlds.Value, worldId).DefaultLevel : levelId;

    // World ownership is table-derived on both sides: world 200 belongs to Theatre5, world 201
    // (Theatre6Activity.WorldId) and world 202 (Theatre6PvpConfig.DlcFightWorldId) belong here.
    internal static bool OwnsDlcWorld(int worldId) =>
        worldId > 0 && (PveWorldIds.Value.Contains(worldId) || PvpWorldIds.Value.Contains(worldId));

    // Ownership probe for the settle path. A partial view keeps the dispatcher from materialising the
    // whole battle report a second time just to read one nested world id: map-mode MessagePack skips
    // the members this probe does not declare.
    internal static bool OwnsDlcSettleWorld(Packet.Request packet) =>
        OwnsDlcWorld(packet.Deserialize<DlcSettleOwnershipProbe>()?.DlcReportWorldResult?.DlcFightSettleData?.WorldData?.WorldId ?? 0);

    internal static void EnterDlcFight(Session session, Packet.Request packet) =>
        EnterDlcFight(session, packet, packet.Deserialize<DlcSingleEnterFightRequest>());

    internal static void SettleDlcFight(Session session, Packet.Request packet) =>
        SettleDlcFight(session, packet, packet.Deserialize<DlcSingleFightSettleRequest>());

    internal static void EnterDlcFight(Session session, Packet.Request packet, DlcSingleEnterFightRequest request) =>
        Handle<DlcSingleEnterFightRequest, DlcSingleEnterFightResponse>(session, packet, (m, _, response) =>
        {
            EnsureAvailable(session);
            Require(request is not null && OwnsDlcWorld(request.WorldId), FightWorldError);
            response.WorldData = PveWorldIds.Value.Contains(request!.WorldId)
                ? EnterPveFight(m, request)
                : Theatre6PvpModule.EnterNativeBattle(m, request);
        }, (response, code) => response.Code = code);

    internal static void SettleDlcFight(Session session, Packet.Request packet, DlcSingleFightSettleRequest request) =>
        Handle<DlcSingleFightSettleRequest, DlcSingleFightSettleResponse>(session, packet, (m, _, response) =>
        {
            EnsureAvailable(session);
            Theatre5DlcFightResultData? report = request?.DlcReportWorldResult?.DlcFightSettleData;
            Require(report is not null && OwnsDlcWorld(report.WorldData?.WorldId ?? 0), FightWorldError);
            string key = ReportKey(packet);
            response.DlcFightSettleData = PveWorldIds.Value.Contains(report!.WorldData!.WorldId)
                ? SettlePveFight(m, key, report)
                : Theatre6PvpModule.SettleNativeBattle(m, key, report);
        }, (response, code) => response.Code = code, afterCommit: Theatre6PvpModule.HandOffPendingDefense);

    // Identity of one settlement body. A retransmission (socket retry, reconnect) re-sends the same
    // bytes; a legitimate second action always differs, because the frozen world it reports differs.
    private static string ReportKey(Packet.Request packet) => Convert.ToHexString(SHA256.HashData(packet.Content));

    #region PvE

    private static Theatre5WorldData EnterPveFight(Mutation m, DlcSingleEnterFightRequest request)
    {
        Theatre6RunState run = CurrentRun(m);
        Require(!run.Settled, FightModeError);
        // The fight identity is frozen on the run's current room by the progression owner: FightId,
        // SelectedMonsterId, FightSeed and the reward preview, re-rolled per binding and cleared at
        // settlement. The attempt is resolved from that frozen room, so the client can never name its
        // own encounter.
        Theatre6RoomDataDb room = run.CurrentRoomDataDb!;
        Require(room is not null, FightRoomError);
        // What authorises an attempt is the frozen fight the progression owner wrote on the current
        // room, not the room type: a monster room, a boss room and an event-spawned fight inside a
        // choose room all carry it, and the progression owner re-rolls FightSeed per binding, so a
        // room without a live frozen fight (including one already settled) is rejected here.
        Require(room.FightId > 0 && room.SelectedMonsterId > 0 && room.FightSeed > 0 && room.FightRewards.Count > 0, FightDataError);

        Theatre6NativeAttemptState? prior = run.NativeAttempt;
        // The attempt is identified by the whole frozen room identity, not by the fight pair alone:
        // the same authored fight/monster can be offered by two rooms in a run, and NativeAttempt is
        // not cleared when the room advances, so a narrower key would reject the second encounter.
        bool sameFight = prior is not null && prior.Epoch == m.State.Epoch && prior.RunId == run.RunId
            && prior.RoomIdx == room.RoomIdx && prior.FightId == room.FightId
            && prior.MonsterId == room.SelectedMonsterId && prior.FightSeed == room.FightSeed;
        Require(!sameFight || !prior!.Settled, FightNotFound);
        if (sameFight)
        {
            // Re-entry must return the identical frozen world - same actors, identity and level - so
            // the native fight resumes the authorised encounter instead of rerolling it.
            Require(request.WorldId == prior!.WorldId
                && ResolveNativeLevelId(request.WorldId, request.LevelId ?? 0) == ResolveNativeLevelId(prior.WorldId, prior.LevelId), FightWorldError);
            return MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(prior.EntryResponse).WorldData!;
        }

        Theatre6MonsterTable monster = Row(BattleMonsters.Value, room.SelectedMonsterId);
        // PlayerSeeds is the one per-attempt discriminator the shipped producer copies verbatim into the
        // native world, so a fresh seed here is what binds the report to THIS authorisation: neither
        // RoomId nor ServerControllerSeed makes that trip, and without it two identical encounters
        // (same archive, same opponent, same level) would be interchangeable.
        int ownerId = checked((int)m.Player.PlayerData.Id);
        int attemptSeed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var world = new Theatre5WorldData
        {
            PlayerSeeds = new Dictionary<int, int> { [ownerId] = attemptSeed },
            WorldId = request.WorldId,
            // Preserve the entry sentinel: native resolves zero to World.DefaultLevel and reports
            // that concrete level at settlement.
            LevelId = request.LevelId ?? 0,
            WorldType = DlcWorldTypeTheatre6,
            Players = [new Theatre5WorldPlayerData { Id = checked((int)m.Player.PlayerData.Id), Name = m.Player.PlayerData.Name }],
            Theatre6GameplayData = new Theatre5Theatre6GameplayData
            {
                SelfData = BuildPlayerActor(m.Player, run),
                EnemyData = BuildMonsterActor(monster)
            }
        };

        run.NativeAttempt = new Theatre6NativeAttemptState
        {
            Epoch = m.State.Epoch,
            RunId = run.RunId,
            AttemptId = ++m.State.NextAttemptId,
            WorldId = world.WorldId,
            LevelId = world.LevelId,
            Seed = attemptSeed,
            RoomIdx = room.RoomIdx,
            FightId = room.FightId,
            MonsterId = room.SelectedMonsterId,
            FightSeed = room.FightSeed,
            DifficultyType = run.DifficultyId,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EntryResponse = MessagePackSerializer.Serialize(new DlcSingleEnterFightResponse { WorldData = world })
        };
        return world;
    }

    private static Theatre5DlcFightSettleData SettlePveFight(Mutation m, string key, Theatre5DlcFightResultData report)
    {
        Theatre6RunState run = CurrentRun(m, allowSettled: true);
        Theatre6NativeAttemptState attempt = run.NativeAttempt!;
        Require(attempt is not null, FightNotFound);
        if (attempt.Settled && attempt.Epoch == m.State.Epoch && attempt.RunId == run.RunId
            && attempt.SettleRequestKey == key && attempt.SettleResponse is { Length: > 0 })
        {
            // A retransmitted settlement replays the frozen answer; it must not award the fight, its
            // rewards or its record a second time.
            return MessagePackSerializer.Deserialize<DlcSingleFightSettleResponse>(attempt.SettleResponse).DlcFightSettleData!;
        }

        Require(!run.Settled, FightModeError);
        Require(!attempt.Settled && attempt.Epoch == m.State.Epoch && attempt.RunId == run.RunId, FightNotFound);
        // The authorisation belongs to the binding that was live when the entry was issued: an easy/hard
        // slide re-rolls FightSeed and changes the selected monster, and a settled room has its fight
        // fields cleared. A report for a binding the room no longer carries must not be applied to
        // whatever the room holds now.
        Theatre6RoomDataDb room = run.CurrentRoomDataDb!;
        Require(room is not null && room.FightId == attempt.FightId && room.SelectedMonsterId == attempt.MonsterId
            && room.FightSeed == attempt.FightSeed, FightDataError);
        Require(report.WorldData is not null, FightDataError);
        Require(report.WorldData.WorldId == attempt.WorldId
            && report.WorldData.LevelId == ResolveNativeLevelId(attempt.WorldId, attempt.LevelId), FightWorldError);
        Theatre5WorldData expected = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(attempt.EntryResponse).WorldData!;
        if (!ValidateNativeReport(report, expected, roundNum: 0, attempt.StartedAt, out string? failure))
        {
            // A rejected report is a foreign or tampered world, not a lost fight: the run keeps its
            // pending fight so a genuine client can still settle it.
            m.Session.log.Warn($"Theatre6 PvE settlement rejected: reason={failure}; runId={run.RunId} attemptId={attempt.AttemptId} fightId={attempt.FightId} monsterId={attempt.MonsterId} worldId={attempt.WorldId}.");
            Require(false, FightDataError);
        }

        List<Theatre6RewardData> rewards = Theatre6Module.OnFightSettled(m, run, report.IsPlayerWin);
        var settle = new Theatre5DlcFightSettleData
        {
            ResultData = report,
            Theatre6FightResult = new Theatre6FightResult { RewardGoodsList = rewards }
        };
        attempt.Settled = true;
        attempt.Interrupted = report.SettleState is 1 or 2 or 3;
        attempt.SettleRequestKey = key;
        attempt.SettleResponse = MessagePackSerializer.Serialize(new DlcSingleFightSettleResponse { DlcFightSettleData = settle });
        return settle;
    }

    #endregion

    #region Actor projection

    // Attribute keys are authored strings naming the native dictionaries one to one: AttrKey
    // "Life"/"Attack" fill Attribs (XEnumConst.Theatre6.NpcAttrib Life=0, Attack=1) and
    // "Stamina"/"WrestlePoint"/"OverClock" fill GameplayAttribs (0/1/2). AttrType 3 is the run's
    // Sanity Cap, which never reaches the native actor.
    private sealed class ActorAttributes
    {
        public Dictionary<int, int> Attribs { get; } = new();
        public Dictionary<int, int> GameplayAttribs { get; } = new();
    }

    private static void AddAttr(ActorAttributes target, string key, int value)
    {
        if (key == "Life") target.Attribs[0] = target.Attribs.GetValueOrDefault(0) + value;
        else if (key == "Attack") target.Attribs[1] = target.Attribs.GetValueOrDefault(1) + value;
        else if (key == "Stamina") target.GameplayAttribs[0] = target.GameplayAttribs.GetValueOrDefault(0) + value;
        else if (key == "WrestlePoint") target.GameplayAttribs[1] = target.GameplayAttribs.GetValueOrDefault(1) + value;
        else if (key == "OverClock") target.GameplayAttribs[2] = target.GameplayAttribs.GetValueOrDefault(2) + value;
    }

    private static void ApplyAttr(ActorAttributes target, int attrId, int value)
    {
        if (!BattleAttrs.Value.TryGetValue(attrId, out Theatre6AttrTable? row)) return;
        if (row.AttrType is not (AttrTypeDlc or AttrTypeGameplay)) return;
        AddAttr(target, row.AttrKey, value);
    }

    // Relic attribute contributions are folded here because the native actor only receives finished
    // dictionaries: the client model keeps Attrs and AttrPacks apart and scores them apart, so
    // nothing on the client applies AttrPack.AttrTypes/AttrNums.
    private static void ApplyAttrPacks(ActorAttributes target, IReadOnlyList<int> packIds, IReadOnlyList<int> packNums)
    {
        int count = Math.Min(packIds.Count, packNums.Count);
        for (int index = 0; index < count; index++)
        {
            if (packNums[index] == 0 || !BattleAttrPacks.Value.TryGetValue(packIds[index], out Theatre6AttrPackTable? row)) continue;
            int pairs = Math.Min(row.AttrTypes.Count, row.AttrNums.Count);
            for (int pair = 0; pair < pairs; pair++)
                ApplyAttr(target, row.AttrTypes[pair], row.AttrNums[pair] * packNums[index]);
        }
    }

    private static ActorAttributes RunAttributes(Theatre6FileState file)
    {
        ActorAttributes attrs = new();
        foreach (Theatre6AttrState attr in file.Attrs)
            ApplyAttr(attrs, attr.AttrId, attr.Value);
        ApplyAttrPackList(attrs, file.AttrPacks);
        return attrs;
    }

    private static void ApplyAttrPackList(ActorAttributes target, IEnumerable<Theatre6AttrPackState> packs)
    {
        foreach (Theatre6AttrPackState pack in packs)
        {
            if (pack.Num == 0 || !BattleAttrPacks.Value.TryGetValue(pack.PackId, out Theatre6AttrPackTable? row)) continue;
            int pairs = Math.Min(row.AttrTypes.Count, row.AttrNums.Count);
            for (int index = 0; index < pairs; index++)
                ApplyAttr(target, row.AttrTypes[index], row.AttrNums[index] * pack.Num);
        }
    }

    // Relic identities travel as Relics (one entry per owned copy, the same shape the Godfall
    // producer uses) and their authored passive magics as MagicIds, so a relic's BuffIds magic is
    // executed by the native side instead of being a server-side no-op.
    private static List<int> RelicList(IEnumerable<Theatre6AttrPackState> packs) =>
        packs.Where(pack => pack.Num > 0 && BattleAttrPacks.Value.ContainsKey(pack.PackId))
            .SelectMany(pack => Enumerable.Repeat(pack.PackId, Math.Max(1, pack.Num)))
            .ToList();

    private static Dictionary<int, int> RelicMagics(IEnumerable<Theatre6AttrPackState> packs)
    {
        Dictionary<int, int> magics = new();
        foreach (Theatre6AttrPackState pack in packs)
        {
            if (pack.Num <= 0 || !BattleAttrPacks.Value.TryGetValue(pack.PackId, out Theatre6AttrPackTable? row)) continue;
            if (row.BuffIds is int magic && magic > 0) magics.TryAdd(magic, 1);
        }
        return magics;
    }

    // Live stage buffs whose authored effect is BuffEffectType 8 ("combat magic") carry their magic
    // id in the live instance's AddMagic (Theatre6Module.Effects.cs). Projecting it is what makes
    // an acquired combat magic execute natively instead of being simulated or ignored server-side.
    private static void ApplyLiveBuffMagics(Dictionary<int, int> magics, IEnumerable<Theatre6LiveBuffState> buffs)
    {
        foreach (Theatre6LiveBuffState buff in buffs)
        {
            if (buff.AddMagic <= 0) continue;
            if (!BattleStageBuffs.Value.TryGetValue(buff.BuffId, out Theatre6StageBuffTable? row)) continue;
            if (row.BuffEffectType != StageBuffEffectCombatMagic) continue;
            magics.TryAdd(buff.AddMagic, 1);
        }
    }

    private static int[] FashionWeapons(int fashionId) =>
        BattleFashions.Value.TryGetValue(fashionId, out Theatre6CharacterFashionTable? fashion)
            ? fashion.DlcWeaponIds.Where(id => id > 0).ToArray()
            : [];

    private static List<int> EquippedSkills(Theatre6FileState file) =>
        file.Skills.Where(skill => skill.SlotType != SkillSlotTypeBag && skill.SkillId > 0)
            .OrderBy(skill => skill.SlotType).ThenBy(skill => skill.Position)
            .Select(skill => skill.SkillId).ToList();

    internal static Theatre5Theatre6NpcData BuildPlayerActor(Player player, Theatre6RunState run)
    {
        Require(BattleCharacters.Value.ContainsKey(run.File.CharacterId) && BattleFashions.Value.ContainsKey(run.File.FashionId), FightArchiveError);
        Theatre6CharacterTable character = Row(BattleCharacters.Value, run.File.CharacterId);
        ActorAttributes attrs = RunAttributes(run.File);
        Dictionary<int, int> magics = RelicMagics(run.File.AttrPacks);
        ApplyLiveBuffMagics(magics, run.Buffs.Values);
        return new Theatre5Theatre6NpcData
        {
            TemplateId = character.TemplateId,
            Name = player.PlayerData.Name,
            HeadFrameId = unchecked((int)player.PlayerData.CurrHeadFrameId),
            CharacterId = run.File.CharacterId,
            CharacterLevel = 0,
            FashionId = run.File.FashionId,
            Attribs = attrs.Attribs,
            GameplayAttribs = attrs.GameplayAttribs,
            Skills = EquippedSkills(run.File),
            MagicIds = magics,
            WeaponIds = FashionWeapons(run.File.FashionId),
            Relics = RelicList(run.File.AttrPacks)
        };
    }

    internal static Theatre5Theatre6NpcData BuildMonsterActor(Theatre6MonsterTable monster)
    {
        Theatre6CharacterTable character = Row(BattleCharacters.Value, monster.CharacterId);
        ActorAttributes attrs = new();
        for (int index = 0; index < monster.AttrTypes.Count; index++)
            ApplyAttr(attrs, index + 1, monster.AttrTypes[index]);
        ApplyAttrPacks(attrs, monster.AttrPacks, monster.AttrPackNums);
        List<Theatre6AttrPackState> packs = PackStates(monster.AttrPacks, monster.AttrPackNums);
        return new Theatre5Theatre6NpcData
        {
            TemplateId = character.TemplateId,
            Name = character.Name,
            HeadFrameId = 0,
            CharacterId = monster.CharacterId,
            CharacterLevel = 0,
            FashionId = character.FashionIds,
            Attribs = attrs.Attribs,
            GameplayAttribs = attrs.GameplayAttribs,
            Skills = monster.SkillIds.Where(id => id > 0).ToList(),
            MagicIds = RelicMagics(packs),
            WeaponIds = FashionWeapons(character.FashionIds),
            Relics = RelicList(packs)
        };
    }

    // A Phantom Clash participant fights with its stored archive; the archive contents come from the
    // server's own file state, never from the request.
    internal static Theatre5Theatre6NpcData BuildArchiveActor(Theatre6FileState file, string name, long headFrameId)
    {
        Require(BattleCharacters.Value.ContainsKey(file.CharacterId) && BattleFashions.Value.ContainsKey(file.FashionId), FightArchiveError);
        Theatre6CharacterTable character = Row(BattleCharacters.Value, file.CharacterId);
        ActorAttributes attrs = RunAttributes(file);
        return new Theatre5Theatre6NpcData
        {
            TemplateId = character.TemplateId,
            Name = name,
            HeadFrameId = unchecked((int)headFrameId),
            CharacterId = file.CharacterId,
            CharacterLevel = 0,
            FashionId = file.FashionId,
            Attribs = attrs.Attribs,
            GameplayAttribs = attrs.GameplayAttribs,
            Skills = EquippedSkills(file),
            MagicIds = RelicMagics(file.AttrPacks),
            WeaponIds = FashionWeapons(file.FashionId),
            Relics = RelicList(file.AttrPacks)
        };
    }

    internal static List<Theatre6AttrPackState> PackStates(IReadOnlyList<int> ids, IReadOnlyList<int> nums)
    {
        List<Theatre6AttrPackState> packs = new();
        int count = Math.Min(ids.Count, nums.Count);
        for (int index = 0; index < count; index++)
            packs.Add(new Theatre6AttrPackState { PackId = ids[index], Num = nums[index] });
        return packs;
    }

    private static T Row<T>(Dictionary<int, T> table, int id)
    {
        Require(table.TryGetValue(id, out T? row), FightConfigError);
        return row!;
    }

    #endregion

    #region Report validation

    // World/level identity is checked at settlement above, including native default-level resolution.
    // Copied fields are exact except the native unset array representation of empty WeaponIds.
    // Native-mutated PvpBuffActionRecord (Buff_1025815/1025818/1025820 write it through
    // SetTheatre6BuffActionValue) must retain seeded keys and non-negative counts.
    internal static bool ValidateNativeReport(Theatre5DlcFightResultData report, Theatre5WorldData expected, int roundNum, long attemptStartedAtMs, out string? reason)
    {
        reason = null;
        if (report.WorldData is not { } world) return Fail(out reason, "WorldData expected the authorised world actual=<null>");
        if (world.WorldType != expected.WorldType) return Fail(out reason, $"WorldType expected={expected.WorldType} actual={world.WorldType}");
        if (world.Players is not { Count: 1 } || world.Players[0] is not { } reporter) return Fail(out reason, "Players expected exactly the reporting player");
        if (reporter.Id != expected.Players[0].Id) return Fail(out reason, $"Players[0].Id expected={expected.Players[0].Id} actual={reporter.Id}");
        if (reporter.Name != expected.Players[0].Name) return Fail(out reason, $"Players[0].Name mismatch expectedLen={expected.Players[0].Name.Length} actualLen={reporter.Name?.Length ?? -1}");
        if (MapMismatch("PlayerSeeds", world.PlayerSeeds, expected.PlayerSeeds, out string? seeds)) return Fail(out reason, seeds);
        if (report.PlayerData is not { Count: 1 } || !report.PlayerData.TryGetValue(expected.Players[0].Id, out Theatre5DlcFightResultPlayerData? player) || player is null)
            return Fail(out reason, "PlayerData expected exactly the reporting player");
        if (report.SettleState is < 0 or > 3) return Fail(out reason, $"SettleState out of range actual={report.SettleState}");
        if (world.Theatre6GameplayData is not { } gameplay) return Fail(out reason, "Theatre6GameplayData expected native gameplay data actual=<null>");
        if (gameplay.RoundNum != roundNum) return Fail(out reason, $"RoundNum expected={roundNum} actual={gameplay.RoundNum}");
        if (ActorMismatch("SelfData", gameplay.SelfData, expected.Theatre6GameplayData!.SelfData, out string? self)) return Fail(out reason, self);
        if (ActorMismatch("EnemyData", gameplay.EnemyData, expected.Theatre6GameplayData!.EnemyData, out string? enemy)) return Fail(out reason, enemy);

        // An interrupted report (client-side give-up or reconnect loss) carries no combat outcome and
        // nothing below is required for it. Native full interruptions can also precede the intro.
        if (report.SettleState is 1 or 2 or 3)
            return !report.IsPlayerWin || Fail(out reason, $"SettleState={report.SettleState} must not report IsPlayerWin=true");
        if (report.FinishTime < 0) return Fail(out reason, $"FinishTime expected>=0 actual={report.FinishTime}");
        // FinishTime is native simulation seconds, trunc_int32(XFight.Time) accumulated as delta*TimeScale,
        // so it may outrun wall clock. The report carries no speed history and no source-backed Theatre6
        // maximum exists, so no cross-clock upper bound is validated - only the native time's sign.
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < attemptStartedAtMs)
            return Fail(out reason, $"attempt StartedAt expected<=now actual={attemptStartedAtMs}");
        if (report.Theatre6CheckData is not { } check) return Fail(out reason, "Theatre6CheckData expected native combat record actual=<null>");
        if (RecordMismatch("MyData", check.MyData, out string? myData)) return Fail(out reason, myData);
        if (RecordMismatch("EnemyData", check.EnemyData, out string? enemyData)) return Fail(out reason, enemyData);
        if (report.NpcSettleInfos is not { } settleInfos) return Fail(out reason, "NpcSettleInfos expected a map actual=<null>");
        foreach ((int key, Theatre5DlcNpcSettleInfo? info) in settleInfos)
            if (info is null || info.LeftHp < 0) return Fail(out reason, $"NpcSettleInfos[{key}].LeftHp expected>=0");
        return true;
    }

    // Two error polarities, deliberately named apart: Fail reports "this report is invalid" to
    // ValidateNativeReport, Mismatch reports "these two values differ" to the comparison helpers
    // below. Mixing them makes every nested comparison silently succeed.
    private static bool Fail(out string? reason, string message)
    {
        reason = message;
        return false;
    }

    private static bool Mismatch([NotNullWhen(true)] out string? reason, string message)
    {
        reason = message;
        return true;
    }

    private static bool ActorMismatch(string side, Theatre5Theatre6NpcData? actual, Theatre5Theatre6NpcData? expected, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (actual is null || expected is null) return Mismatch(out reason, $"{side} expected native actor actual={(actual is null ? "<null>" : "present")}");
        if (actual.TemplateId != expected.TemplateId) return Mismatch(out reason, $"{side}.TemplateId expected={expected.TemplateId} actual={actual.TemplateId}");
        if (actual.Name != expected.Name) return Mismatch(out reason, $"{side}.Name mismatch expectedLen={expected.Name.Length} actualLen={actual.Name.Length}");
        if (actual.HeadFrameId != expected.HeadFrameId) return Mismatch(out reason, $"{side}.HeadFrameId expected={expected.HeadFrameId} actual={actual.HeadFrameId}");
        if (actual.CharacterId != expected.CharacterId) return Mismatch(out reason, $"{side}.CharacterId expected={expected.CharacterId} actual={actual.CharacterId}");
        if (actual.CharacterLevel != expected.CharacterLevel) return Mismatch(out reason, $"{side}.CharacterLevel expected={expected.CharacterLevel} actual={actual.CharacterLevel}");
        if (actual.FashionId != expected.FashionId) return Mismatch(out reason, $"{side}.FashionId expected={expected.FashionId} actual={actual.FashionId}");
        if (actual.PvpEnvMagicId != expected.PvpEnvMagicId) return Mismatch(out reason, $"{side}.PvpEnvMagicId expected={expected.PvpEnvMagicId} actual={actual.PvpEnvMagicId}");
        if (MapMismatch("Attribs", actual.Attribs, expected.Attribs, out string? attribs)) return Mismatch(out reason, $"{side}.{attribs}");
        if (MapMismatch("GameplayAttribs", actual.GameplayAttribs, expected.GameplayAttribs, out string? gameplay)) return Mismatch(out reason, $"{side}.{gameplay}");
        if (MapMismatch("MagicIds", actual.MagicIds, expected.MagicIds, out string? magic)) return Mismatch(out reason, $"{side}.{magic}");
        if (SequenceMismatch("Skills", actual.Skills, expected.Skills, out string? skills)) return Mismatch(out reason, $"{side}.{skills}");
        // _GetXWorldGameplayData assigns this native int[] only when nonempty; unlike the
        // constructor-initialized lists/maps populated through Add, an empty array stays null.
        if ((actual.WeaponIds is not null || expected.WeaponIds.Length != 0) &&
            SequenceMismatch("WeaponIds", actual.WeaponIds, expected.WeaponIds, out string? weapons)) return Mismatch(out reason, $"{side}.{weapons}");
        if (SequenceMismatch("Relics", actual.Relics, expected.Relics, out string? relics)) return Mismatch(out reason, $"{side}.{relics}");
        if (SequenceMismatch("MagicIdsWithoutLevel", actual.MagicIdsWithoutLevel, expected.MagicIdsWithoutLevel, out string? noLevel)) return Mismatch(out reason, $"{side}.{noLevel}");
        if (actual.PvpBuffActionRecord is not { } actionRecord) return Mismatch(out reason, $"{side}.PvpBuffActionRecord expected a map actual=<null>");
        foreach ((int id, int seeded) in expected.PvpBuffActionRecord)
            if (!actionRecord.ContainsKey(id)) return Mismatch(out reason, $"{side}.PvpBuffActionRecord[{id}] missing (seeded={seeded})");
        foreach ((int id, int count) in actionRecord)
            if (count < 0) return Mismatch(out reason, $"{side}.PvpBuffActionRecord[{id}] expected>=0 actual={count}");
        return false;
    }

    private static bool MapMismatch(string label, Dictionary<int, int>? actual, Dictionary<int, int> expected, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (actual is null) return Mismatch(out reason, $"{label} expected count={expected.Count} actual=<null>");
        foreach ((int key, int value) in expected)
        {
            if (!actual.TryGetValue(key, out int found)) return Mismatch(out reason, $"{label}[{key}] missing (expected={value})");
            if (found != value) return Mismatch(out reason, $"{label}[{key}] expected={value} actual={found}");
        }
        if (actual.Count == expected.Count) return false;
        foreach ((int key, int value) in actual)
            if (!expected.ContainsKey(key)) return Mismatch(out reason, $"{label}[{key}] unexpected (actual={value})");
        return false;
    }

    private static bool SequenceMismatch(string label, IReadOnlyList<int>? actual, IReadOnlyList<int> expected, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (actual is null) return Mismatch(out reason, $"{label} expected count={expected.Count} actual=<null>");
        if (actual.Count != expected.Count) return Mismatch(out reason, $"{label} count expected={expected.Count} actual={actual.Count}");
        for (int index = 0; index < actual.Count; index++)
            if (actual[index] != expected[index]) return Mismatch(out reason, $"{label}[{index}] expected={expected[index]} actual={actual[index]}");
        return false;
    }

    // The native recorder only ever adds non-negative entries; this checks source-format invariants,
    // not an invented combat equation the server has no authority to execute.
    private static bool RecordMismatch(string side, Theatre6CheckNpcData? record, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (record is null) return Mismatch(out reason, $"{side} expected native combat record actual=<null>");
        if (record.TotalDamage < 0) return Mismatch(out reason, $"{side}.TotalDamage expected>=0 actual={record.TotalDamage}");
        if (record.TotalEnergyCast < 0) return Mismatch(out reason, $"{side}.TotalEnergyCast expected>=0 actual={record.TotalEnergyCast}");
        if (BucketMismatch($"{side}.DamageRecord", record.DamageRecord, out string? damage)) return Mismatch(out reason, damage);
        if (BucketMismatch($"{side}.EnergyCastRecord", record.EnergyCastRecord, out string? energy)) return Mismatch(out reason, energy);
        if (record.SkillCountRecord is not { } skillCounts) return Mismatch(out reason, $"{side}.SkillCountRecord expected a map actual=<null>");
        foreach ((int skillId, int count) in skillCounts)
            if (count < 0) return Mismatch(out reason, $"{side}.SkillCountRecord[{skillId}] expected>=0 actual={count}");
        return false;
    }

    private static bool BucketMismatch(string label, Dictionary<int, Dictionary<int, int>>? buckets, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (buckets is null) return Mismatch(out reason, $"{label} expected categories actual=<null>");
        foreach ((int category, Dictionary<int, int>? bucket) in buckets)
        {
            if (category is not (0 or 1)) return Mismatch(out reason, $"{label}[{category}] expected category 0 or 1");
            if (bucket is null) return Mismatch(out reason, $"{label}[{category}] expected bucket actual=<null>");
            foreach ((int id, int value) in bucket)
                if (value < 0) return Mismatch(out reason, $"{label}[{category}][{id}] expected>=0 actual={value}");
        }
        return false;
    }

    #endregion
}
