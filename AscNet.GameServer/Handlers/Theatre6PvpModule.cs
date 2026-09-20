using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

// Mutation is nested in the Theatre6Module partial class; this module is a separate class, so the
// staged request state has to be aliased rather than imported.
using Mutation = AscNet.GameServer.Handlers.Theatre6Module.Mutation;

namespace AscNet.GameServer.Handlers;

// Phantom Clash (Theatre6 PvP). Wire contracts live in AscNet.Common.MsgPack/Theatre6.Pvp.cs; the
// native entry/settlement for world 202 is in Theatre6Module.Battle.cs, which calls
// EnterNativeBattle/SettleNativeBattle here. Every request name and every consumed key comes from the
// shipped client (matrix/xmodule/xtheatre6/controlpartial/XTheatre6ControlPvpNetwork.lua and
// submodel/XTheatre6SubPvpModel.lua, both byte-identical inside the installed matrix bundle).
//
// Compositions the shipped client cannot supply are built here from authored Theatre6Pvp* operands and
// marked LOCAL_POLICY at each site:
//   * the expected-score term of the rating update (KWin/KLose/BaseWinScore/AllWinScore/DefKWin/
//     DefKLose are authored columns; the Elo expectation itself is server-only);
//   * the opponent-pool composition across Theatre6PvpRankFight bands, including the binding of that
//     table's MinScore to the ladder score (see BuildMatches: the table has no client consumer, so no
//     client source fixes its domain, and the binding is policy, not a recovered retail mapping);
//   * a promotion battle is free of stamina cost, and a loss never deducts while the promotion gate
//     is open - the client's own authored tips say the score is locked (ScoreDetailDesc5/6).
internal static class Theatre6PvpModule
{
    // XCode.Theatre6 (EN bytes/share/text/CodeText.json).
    private const int NotOpen = 20427001, AlreadyInBattle = 20427003, DataError = 20427004, RankNotFound = 20427005,
        RefreshCd = 20427007, RefreshLimit = 20427008, PromotionInProgress = 20427009, ActionPointLow = 20427010,
        DefenseCount = 20427011, RepeatLimit = 20427012, FileNotFound = 20427013, NotInBattle = 20427014,
        FightRoundError = 20427015, EnemyNotFound = 20427016, FileInvalid = 20427017, BattleFinished = 20427018,
        EnemyInvalid = 20427019, AttackCount = 20427021, DefenseMax = 20427022, ModeLocked = 20427023,
        BuffUnsupported = 20427026, BuffMustSelect = 20427027, BuffNotInGroup = 20427028, BuffInvalid = 20427029,
        BuffConfigMissing = 20427031, BuffGroupConfigMissing = 20427032;

    // XEnumConst.Theatre6.Pvp.PlayerState / BattlePhase.
    private const int PlayerStateInit = 0;
    private const int PlayerStateInAdvanceBattle = 1;
    private const int BattlePhaseNormal = 0, BattlePhaseAdvanceUnlocked = 1, BattlePhaseAdvanceLocked = 2;
    private const int DlcWorldTypeTheatre6 = 7;

    private const int LeaderboardPageSize = 100;
    private const int MatchOfferCount = 3;
    private const int Basis = 10000;
    private const int MaxHumanCandidates = 64;

    private static void Require([DoesNotReturnIf(false)] bool valid, int code) => Theatre6Module.Require(valid, code);

    private static int Cfg(string key)
    {
        Theatre6PvpConfigTable? row = TableReaderV2.Parse<Theatre6PvpConfigTable>().FirstOrDefault(entry => entry.Key == key);
        if (row is null || row.Values < 0) throw new InvalidDataException($"Theatre6PvpConfig has no value for '{key}'");
        return row.Values;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static List<Theatre6PvpRankTable> Ranks() => TableReaderV2.Parse<Theatre6PvpRankTable>();

    private static Theatre6PvpRankTable? Rank(int rankId) => Ranks().FirstOrDefault(row => row.Id == rankId);

    private static Theatre6PvpRankTable? NextRank(int rankId) => Ranks().FirstOrDefault(row => row.Id == rankId + 1);

    // A rank's authored MinScore is the floor of its score band, and the lowest authored band legitimately
    // authors none (rank 1 has an empty MinScore in the shipped table). An unauthored floor is therefore
    // zero rather than an error: the value is only ever used to clamp a score that cannot fall below the
    // band it belongs to, so treating it as an unhandled exception turned a fresh player's first Phantom
    // Clash request into a dropped connection.
    private static int RankFloor(Theatre6PvpRankTable rank) => rank.MinScore ?? 0;

    internal static bool ReconcileActionPoint(Theatre6PvpState s, long now)
    {
        int cap = Cfg("ActionPointMaxLimit"), interval = Cfg("ActionPointRecoverInterval");
        if (s.ActionPoint >= cap || s.ActionPointTime <= 0 || now < s.ActionPointTime + interval) return false;
        long ticks = (now - s.ActionPointTime) / interval;
        int gain = (int)Math.Min(ticks, cap - s.ActionPoint);
        if (gain <= 0) return false;
        s.ActionPoint += gain;
        s.ActionPointTime += gain * interval;
        return true;
    }

    // The season's current authority, staged into the request's own state. A cached authorisation from
    // an earlier login is not authoritative: a season that opens (or a base/unlock condition that
    // completes) while this session is connected must be enterable without a relog, so the authority is
    // re-derived here and written into the staged state before the season is reconciled.
    private static void EnsureSeason(Mutation m)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Require(Theatre6Module.IsAvailable(m.Player, now, out int seasonId, out int timeId), NotOpen);
        Require(TableReaderV2.Parse<Theatre6PvpActivityTable>().Any(activity => activity.Id == seasonId), NotOpen);
        Require(Theatre6Module.HasCondition(m.Player, Cfg("UnlockPvpModeConditionId")), ModeLocked);
        Theatre6PvpState s = m.State.Pvp;
        s.AuthorizedSeasonId = seasonId;
        if (!s.AuthorizedTimeIds.Contains(timeId)) s.AuthorizedTimeIds = new List<int> { timeId };
        int code = ReconcileSeason(s, Now());
        Require(code == 0, code);
    }

    private static int ReconcileSeason(Theatre6PvpState s, long now)
    {
        if (s.InitializedSeasonId == s.AuthorizedSeasonId) return Rank(s.RankId) is null ? RankNotFound : 0;
        Theatre6PvpActivityTable? activity = TableReaderV2.Parse<Theatre6PvpActivityTable>().FirstOrDefault(row => row.Id == s.AuthorizedSeasonId);
        if (activity is null) return NotOpen;
        Theatre6PvpRankTable? rank = Ranks().FirstOrDefault(row => activity.InitPoint >= (row.MinScore ?? 0) && activity.InitPoint <= row.MaxScore)
            ?? Ranks().OrderBy(row => row.Id).FirstOrDefault();
        if (rank is null) return RankNotFound;
        s.InitializeSeason(activity.Id, rank.Id, activity.InitPoint, Cfg("ActionPointInit"), now);
        s.CurrentRankMinScore = RankFloor(rank);
        return 0;
    }

    // The challenge state the client derives in XTheatre6Control:IsPVPChallengeState: the rank's own
    // score ceiling is reached, a next rank exists, and that rank is enterable right now. An authored
    // TimeId of 0 means permanently enterable.
    private static bool IsChallengeState(Theatre6PvpState s, DateTimeOffset now)
    {
        Theatre6PvpRankTable? rank = Rank(s.RankId);
        if (rank is null || s.Score < rank.MaxScore) return false;
        Theatre6PvpRankTable? next = NextRank(rank.Id);
        if (next is null) return false;
        return next.TimeId is not int timeId || timeId <= 0 || ActivityScheduleService.IsOpen(timeId, now);
    }

    private static int BattlePhaseAfter(Theatre6PvpState s, DateTimeOffset now)
    {
        Theatre6PvpRankTable? rank = Rank(s.RankId);
        if (rank is null || s.Score < rank.MaxScore) return BattlePhaseNormal;
        return IsChallengeState(s, now) ? BattlePhaseAdvanceUnlocked : BattlePhaseAdvanceLocked;
    }

    #region Handlers

    [RequestPacketHandler("Theatre6GetPvpPreviewInfoRequest")]
    public static void Preview(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6GetPvpPreviewInfoRequest, Theatre6GetPvpPreviewInfoResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            ReconcileActionPoint(s, Now());
            s.RankRecords[s.AuthorizedSeasonId] = new Theatre6RankRecordState { RankId = s.RankId, Score = s.Score };
            m.Push(ApPush(s));
            response.PvpRankRecords = RankRecords(s);
            response.ActionPoint = s.ActionPoint;
            response.LastActionPointRecoverTime = s.ActionPointTime;
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpStartRequest")]
    public static void Start(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpStartRequest, Theatre6PvpStartResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            ReconcileActionPoint(s, Now());
            if (s.Matches.Count == 0) Require(BuildMatches(m, s, DateTimeOffset.UtcNow), EnemyNotFound);
            response.ActivityData = Activity(s);
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpUpdateDefenseRequest")]
    public static void Defense(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpUpdateDefenseRequest, Theatre6PvpUpdateDefenseResponse>(session, packet, (m, request, _) =>
        {
            Require(request is not null, DataError);
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            List<Theatre6FileSlot?> sent = request!.Slots ?? new List<Theatre6FileSlot?>();
            Require(sent.All(slot => slot is not null), FileInvalid);
            List<Theatre6FileSlot> slots = sent.Select(slot => slot!).ToList();
            // The defence UI refuses to confirm below the authored maximum
            // (XUiTheatre6PVPAttackDefend:OnBtnConfirmClick, DefendSlotLimitTip).
            int limit = Cfg("MaxSlotDefenseLineupLimit");
            Require(slots.Count == limit, DefenseCount);
            Require(slots.Select(slot => (slot.CharacterId, slot.SlotId)).Distinct().Count() <= limit, DefenseMax);
            Require(RepeatCount(slots) <= Cfg("LineupSlotRepeatLimit"), RepeatLimit);
            Require(BuffCode(s, request.BuffId ?? 0, attack: false) == 0, BuffInvalid);
            s.DefenseFiles = ResolveFiles(m.Player, slots);
            s.DefenseBuffId = request.BuffId ?? 0;
            s.DefenseUpdateTime = Now();
            m.Push(new NotifyTheatre6DefenseUpdate
            {
                DefenseBuffId = s.DefenseBuffId,
                Lineups = s.DefenseFiles.Select(Theatre6Module.ToWire).ToList()
            });
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpRefreshMatchRequest")]
    public static void Refresh(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpRefreshMatchRequest, Theatre6PvpRefreshMatchResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            Require(s.Battle is not { Finished: false }, AlreadyInBattle);
            // A pending promotion must be fought, not rerolled: the client hides the refresh control
            // while it is in the challenge state.
            Require(!IsChallengeState(s, DateTimeOffset.UtcNow), PromotionInProgress);
            long stamp = Now();
            Require(stamp - s.LastRefreshTime >= Cfg("RefreshMatchCd"), RefreshCd);
            if (stamp - s.RefreshPeriodStart >= Cfg("RefreshMatchPeriodSeconds"))
            {
                s.RefreshPeriodStart = stamp;
                s.RefreshPeriodCount = 0;
            }
            Require(s.RefreshPeriodCount < Cfg("RefreshMatchMaxCountPerPeriod"), RefreshLimit);
            Require(BuildMatches(m, s, DateTimeOffset.UtcNow), EnemyNotFound);
            s.RefreshPeriodCount++;
            m.Push(new NotifyMatchPlayersUpdate { Enemies = Enemies(s) });
            response.MatchResult = new Theatre6MatchResult { Enemies = Enemies(s) };
            response.LastRefreshMatchTime = s.LastRefreshTime;
            response.RefreshRemainSeconds = Cfg("RefreshMatchCd");
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpGetActionPointRequest")]
    public static void GetAp(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpGetActionPointRequest, Theatre6PvpGetActionPointResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            ReconcileActionPoint(s, Now());
            response.ActionPoint = s.ActionPoint;
            response.LastActionPointRecoverTime = s.ActionPointTime;
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpStartFightRequest")]
    public static void StartFight(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpStartFightRequest, Theatre6PvpStartFightResponse>(session, packet, (m, request, response) =>
        {
            Require(request is not null, DataError);
            Theatre6PvpState s = m.State.Pvp;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            EnsureSeason(m);
            ReconcileActionPoint(s, Now());
            Require(s.Battle is not { Finished: false }, AlreadyInBattle);

            Theatre6MatchState? match = s.Matches.FirstOrDefault(entry => entry.Uid == request!.EnemyId);
            Require(match?.Enemy is not null, EnemyInvalid);
            Theatre6EnemySnapshotState enemy = match!.Enemy;

            List<Theatre6FileSlot> slots = (request!.MyFileSlots ?? new List<Theatre6FileSlot>()).Where(slot => slot is not null).ToList();
            // The attack UI refuses to start below three filled positions
            // (XUiTheatre6PVPAttackDefend:OnBtnFightClick, AttackSlotLimitTip).
            Require(slots.Count == MatchOfferCount, AttackCount);
            Require(slots.Select(slot => (slot.CharacterId, slot.SlotId)).Distinct().Count() <= Cfg("SlotAttackLineupLimit"), AttackCount);
            Require(RepeatCount(slots) <= Cfg("LineupSlotRepeatLimit"), RepeatLimit);
            List<Theatre6FileState> files = ResolveFiles(m.Player, slots);

            int buffId = request.BuffId ?? 0;
            Require(BuffCode(s, buffId, attack: true) == 0, BuffInvalid);

            // LOCAL_POLICY: a promotion battle is free; every other attack costs the authored stamina
            // price. The client performs the same distinction (it hides the energy panel and skips the
            // stamina check while it is in the challenge state).
            bool challenge = IsChallengeState(s, now);
            int phase = challenge ? PlayerStateInAdvanceBattle : PlayerStateInit;
            if (!challenge)
            {
                int cost = Cfg("ActionPointPerCost");
                Require(s.ActionPoint >= cost, ActionPointLow);
                if (s.ActionPoint >= Cfg("ActionPointMaxLimit")) s.ActionPointTime = Now();
                s.ActionPoint -= cost;
            }

            s.Battle = new Theatre6BattleState
            {
                BattleId = s.NextBattleId++,
                EnemyUid = match.Uid,
                EnemyRobotId = enemy.RobotId,
                Enemy = enemy,
                MyFiles = files,
                CurrentRound = 1,
                RoundResults = new List<bool>(),
                Finished = false,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpireAt = Now() + Cfg("FightExpireDuration"),
                WorldId = Cfg("DlcFightWorldId"),
                LevelId = 0,
                Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                BuffId = buffId,
                Phase = phase
            };
            s.PlayerState = phase;
            m.Push(ApPush(s));
            response.BattleState = BattleState(s);
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpRestartFightRequest")]
    public static void Restart(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpRestartFightRequest, Theatre6PvpRestartFightResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            Theatre6BattleState? battle = s.Battle;
            Require(battle is not null, NotInBattle);
            if (battle!.Finished)
            {
                // The battle is over: the client takes FightResult and opens the rank settlement, the
                // same branch it uses for a give-up.
                response.FightResult = FightResult(s, battle);
                return;
            }
            // Expiry ends the battle before the season gate, so a battle whose season closed is still
            // resolvable rather than blocking Phantom Clash until the next season reset.
            if (Now() > battle.ExpireAt)
            {
                FinishBattle(m, s, battle, interrupted: true);
                response.FightResult = FightResult(s, battle);
                return;
            }
            EnsureSeason(m);
            // An abandoned battle cannot be resumed forever; passing the restart limit ends it as a loss
            // for the rounds not played, so the client always leaves with a settlement.
            if (battle.RestartCount >= Cfg("RestartFightTimesLimit"))
            {
                FinishBattle(m, s, battle, interrupted: true);
                response.FightResult = FightResult(s, battle);
                return;
            }
            battle.RestartCount++;
            response.BattleState = BattleState(s);
        }, (response, code) => response.Code = code, afterCommit: HandOffPendingDefense);

    [RequestPacketHandler("Theatre6PvpQueryRankRequest")]
    public static void QueryRank(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpQueryRankRequest, Theatre6PvpQueryRankResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            int seasonId = s.AuthorizedSeasonId;
            FilterDefinition<Player> participants = Builders<Player>.Filter.And(
                Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.AuthorizedSeasonId, seasonId),
                Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.InitializedSeasonId, seasonId));
            long totalCount = Player.collection.CountDocuments(participants);
            long playerId = m.Player.PlayerData.Id;
            bool isParticipant = Player.collection.CountDocuments(Builders<Player>.Filter.And(
                participants, Builders<Player>.Filter.Eq(player => player.PlayerData.Id, playerId))) > 0;
            long betterCount = isParticipant
                ? Player.collection.CountDocuments(Builders<Player>.Filter.And(
                    participants,
                    Builders<Player>.Filter.Or(
                        Builders<Player>.Filter.Gt(player => player.Theatre6.Pvp.Score, s.Score),
                        Builders<Player>.Filter.And(
                            Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.Score, s.Score),
                            Builders<Player>.Filter.Lt(player => player.PlayerData.Id, playerId)))))
                : 0;
            response.RankPlayerInfos = Player.collection.Find(participants)
                .SortByDescending(player => player.Theatre6.Pvp.Score)
                .ThenBy(player => player.PlayerData.Id)
                .Limit(LeaderboardPageSize)
                .ToList()
                .Select(player => new Theatre6RankPlayer
                {
                    Id = player.PlayerData.Id,
                    Name = player.PlayerData.Name,
                    HeadPortraitId = player.PlayerData.CurrHeadPortraitId,
                    HeadFrameId = player.PlayerData.CurrHeadFrameId,
                    Score = player.Theatre6.Pvp.Score
                }).ToList();
            response.TotalCount = ToProtocolCount(totalCount);
            response.SelfRank = isParticipant ? ToProtocolCount(betterCount + 1) : -1;
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpGetBattleRecordsRequest")]
    public static void Records(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpGetBattleRecordsRequest, Theatre6PvpGetBattleRecordsResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            response.BattleRecords = s.BattleRecords.Take(Cfg("MaxBattleRecordCount")).Select(Record).ToList();
        }, (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6PvpGiveUpFightRequest")]
    public static void GiveUp(Session session, Packet.Request packet) =>
        Theatre6Module.Handle<Theatre6PvpGiveUpFightRequest, Theatre6PvpGiveUpFightResponse>(session, packet, (m, _, response) =>
        {
            Theatre6PvpState s = m.State.Pvp;
            EnsureSeason(m);
            Theatre6BattleState? battle = s.Battle;
            Require(battle is not null, NotInBattle);
            Require(!battle!.Finished, BattleFinished);
            // The client interrupts the native fight and expects FightResult immediately; the rounds
            // already reported stay as they were, so the settlement shows what actually ran.
            FinishBattle(m, s, battle, interrupted: true);
            response.FightResult = FightResult(s, battle);
        }, (response, code) => response.Code = code, afterCommit: HandOffPendingDefense);

    private static int ToProtocolCount(long count) =>
        count >= int.MaxValue ? int.MaxValue : checked((int)Math.Max(0, count));

    #endregion

    #region Native battle (world 202)

    internal static Theatre5WorldData EnterNativeBattle(Mutation m, DlcSingleEnterFightRequest request)
    {
        EnsureSeason(m);
        Theatre6PvpState s = m.State.Pvp;
        Require(request.WorldId == Cfg("DlcFightWorldId"), NotOpen);
        Theatre6BattleState? battle = s.Battle;
        Require(battle is not null && !battle.Finished, NotInBattle);
        Require(battle!.ExpireAt >= Now(), BattleFinished);
        Require(battle.CurrentRound is >= 1 and <= MatchOfferCount, FightRoundError);
        Require(battle.MyFiles.Count >= battle.CurrentRound, FileInvalid);
        Theatre6EnemySnapshotState enemy = battle.Enemy;
        Require(enemy is not null, EnemyInvalid);

        if (battle.EntryResponse is { Length: > 0 })
        {
            // The round is still live: the client must receive the identical fight it was authorised
            // for, otherwise a reconnect would reroll the encounter.
            Theatre5WorldData frozen = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(battle.EntryResponse).WorldData!;
            Require(frozen.WorldId == request.WorldId
                && Theatre6Module.ResolveNativeLevelId(frozen.WorldId, frozen.LevelId)
                    == Theatre6Module.ResolveNativeLevelId(request.WorldId, request.LevelId ?? 0), DataError);
            return frozen;
        }

        Theatre5Theatre6NpcData self = Theatre6Module.BuildArchiveActor(
            battle.MyFiles[battle.CurrentRound - 1], m.Player.PlayerData.Name, m.Player.PlayerData.CurrHeadFrameId);
        Theatre5Theatre6NpcData foe = BuildEnemyActor(enemy, battle.CurrentRound);
        // PvpEnvMagicId is the authored environment magic; the native side executes it and the server
        // never simulates its damage.
        self.PvpEnvMagicId = EnvironmentMagic(battle.BuffId);
        foe.PvpEnvMagicId = EnvironmentMagic(enemy.DefenseBuffId);
        CarryBuffAction(battle.SelfBuffActionRecord, self.PvpBuffActionRecord);
        CarryBuffAction(battle.EnemyBuffActionRecord, foe.PvpBuffActionRecord);

        // As in PvE, PlayerSeeds is the only per-authorisation discriminator the shipped producer copies
        // into the native world and back; deriving it per round keeps every authorised round distinct
        // even when the same opponent lineups face each other again.
        int ownerId = checked((int)m.Player.PlayerData.Id);
        var world = new Theatre5WorldData
        {
            PlayerSeeds = new Dictionary<int, int> { [ownerId] = unchecked(battle.Seed ^ (battle.CurrentRound * (int)0x9E3779B1)) },
            WorldId = request.WorldId,
            // Initial entry uses zero; later rounds can reuse the concrete native result level.
            // Preserve the request while native resolves zero through World.DefaultLevel.
            LevelId = request.LevelId ?? 0,
            WorldType = DlcWorldTypeTheatre6,
            Players = [new Theatre5WorldPlayerData { Id = checked((int)m.Player.PlayerData.Id), Name = m.Player.PlayerData.Name }],
            Theatre6GameplayData = new Theatre5Theatre6GameplayData
            {
                SelfData = self,
                EnemyData = foe,
                RoundNum = battle.CurrentRound,
                RoundResults = battle.RoundResults.ToList()
            }
        };
        battle.AttemptId++;
        battle.WorldId = world.WorldId;
        battle.LevelId = world.LevelId;
        battle.EntryResponse = MessagePackSerializer.Serialize(new DlcSingleEnterFightResponse { WorldData = world });
        // A new round's authorisation replaces the previous round's settlement receipt; the retransmit
        // guard below only ever matches the report of the round that is currently live.
        battle.SettleRequestKey = "";
        battle.SettleResponse = [];
        return world;
    }

    // A robot opponent has no stored archives: the client rebuilds them itself from
    // Theatre6PvpRobot.UseMonsterIds + Theatre6Monster (XTheatre6Control:BuiltRobotSaveFiles), so the
    // round's opponent actor is the authored monster for that lineup position.
    private static Theatre5Theatre6NpcData BuildEnemyActor(Theatre6EnemySnapshotState enemy, int round)
    {
        if (enemy.IsHuman)
        {
            Require(round <= enemy.Files.Count, EnemyInvalid);
            return Theatre6Module.BuildArchiveActor(enemy.Files[round - 1], enemy.Name, enemy.HeadFrameId);
        }
        Theatre6PvpRobotTable? robot = TableReaderV2.Parse<Theatre6PvpRobotTable>().FirstOrDefault(row => row.Id == enemy.RobotId);
        Require(robot is not null && round <= robot!.UseMonsterIds.Count, EnemyInvalid);
        Theatre6MonsterTable? monster = TableReaderV2.Parse<Theatre6MonsterTable>()
            .FirstOrDefault(row => row.Id == robot!.UseMonsterIds[round - 1]);
        Require(monster is not null, EnemyInvalid);
        return Theatre6Module.BuildMonsterActor(monster!);
    }

    private static int EnvironmentMagic(int buffId)
    {
        if (buffId <= 0) return 0;
        Theatre6PvpBuffTable? buff = TableReaderV2.Parse<Theatre6PvpBuffTable>().FirstOrDefault(row => row.Id == buffId);
        return buff?.MagicId ?? 0;
    }

    // Buff_1025815/1025818/1025819/1025820 accumulate this map inside the native fight through
    // SetTheatre6BuffActionValue and the reported actor carries it back, so the next round must be
    // seeded with what the previous round ended with.
    private static void CarryBuffAction(Dictionary<int, int> source, Dictionary<int, int> target)
    {
        foreach ((int magic, int value) in source)
            if (value != 0) target[magic] = value;
    }

    internal static Theatre5DlcFightSettleData SettleNativeBattle(Mutation m, string key, Theatre5DlcFightResultData report)
    {
        Theatre6PvpState s = m.State.Pvp;
        Theatre6BattleState? battle = s.Battle;
        Require(battle is not null, NotInBattle);
        if (battle!.SettleRequestKey == key && battle.SettleResponse is { Length: > 0 })
        {
            // Identical retransmission replays the frozen answer before any terminal guard: the client
            // can retry a round whose response was lost even after the round advanced or the battle
            // finished, and it must never be awarded twice. The receipt is cleared when the next
            // round's entry is authorised, so it can only ever match the round it belongs to.
            return MessagePackSerializer.Deserialize<DlcSingleFightSettleResponse>(battle.SettleResponse).DlcFightSettleData!;
        }
        // Only a battle that is still running may transition. This guard must precede the expiry branch:
        // otherwise a second report against an already finished battle would expire it again and award a
        // record, a statistic, a mission counter and a fresh defence outcome for the same battle.
        Require(!battle.Finished, BattleFinished);
        // The authored FightExpireDuration lapses the authorisation: a report arriving after it must not
        // settle a fresh round or promote a rank. The battle ends as an interruption, which is what the
        // client's own give-up path produces, and the report's outcome is discarded. This runs before the
        // season gate on purpose, so a battle whose season closed is still resolvable instead of blocking
        // Phantom Clash until the next season reset.
        if (Now() > battle.ExpireAt)
        {
            FinishBattle(m, s, battle, interrupted: true);
            var expired = new Theatre5DlcFightSettleData { ResultData = report, Theatre6PvpFightResult = FightResult(s, battle) };
            // Freeze the answer exactly like a normal settlement, so an exact retry replays it instead of
            // running the expiry transition again.
            battle.SettleRequestKey = key;
            battle.SettleResponse = MessagePackSerializer.Serialize(new DlcSingleFightSettleResponse { DlcFightSettleData = expired });
            return expired;
        }
        EnsureSeason(m);
        Require(battle.EntryResponse is { Length: > 0 }, FightRoundError);
        Require(report.WorldData is not null, DataError);
        Require(report.WorldData.WorldId == battle.WorldId
            && report.WorldData.LevelId == Theatre6Module.ResolveNativeLevelId(battle.WorldId, battle.LevelId), DataError);

        Theatre5WorldData expected = MessagePackSerializer.Deserialize<DlcSingleEnterFightResponse>(battle.EntryResponse).WorldData!;
        if (!Theatre6Module.ValidateNativeReport(report, expected, battle.CurrentRound, battle.StartedAt, out string? failure))
        {
            m.Session.log.Warn($"Theatre6 PvP round rejected: reason={failure}; battleId={battle.BattleId} round={battle.CurrentRound} enemyUid={battle.EnemyUid} robotId={battle.EnemyRobotId}.");
            Require(false, FightRoundError);
        }

        Theatre5Theatre6NpcData reportedSelf = report.WorldData.Theatre6GameplayData!.SelfData!;
        Theatre5Theatre6NpcData reportedEnemy = report.WorldData.Theatre6GameplayData!.EnemyData!;
        battle.SelfBuffActionRecord = new Dictionary<int, int>(reportedSelf.PvpBuffActionRecord);
        battle.EnemyBuffActionRecord = new Dictionary<int, int>(reportedEnemy.PvpBuffActionRecord);

        bool roundWin = report.SettleState == 0 && report.IsPlayerWin;
        battle.RoundResults.Add(roundWin);
        battle.LastSettleTime = Now();
        var settle = new Theatre5DlcFightSettleData { ResultData = report };
        // XTheatre6BattleAgency:_IsPvpUnfinished. Two opening losses end the match; two opening wins
        // still require the third round, so this is deliberately not a symmetric best-of-three.
        bool finished = (battle.RoundResults.Count == 2 && !battle.RoundResults[0] && !battle.RoundResults[1])
            || battle.RoundResults.Count >= MatchOfferCount;
        if (finished) FinishBattle(m, s, battle, interrupted: false);
        else
        {
            battle.CurrentRound = battle.RoundResults.Count + 1;
            // The resolved round's authorisation is spent: the next DLC enter must build the next
            // round's frozen world instead of replaying the one that just ended.
            battle.EntryResponse = [];
        }
        settle.Theatre6PvpFightResult = FightResult(s, battle);
        battle.SettleRequestKey = key;
        battle.SettleResponse = MessagePackSerializer.Serialize(new DlcSingleFightSettleResponse { DlcFightSettleData = settle });
        return settle;
    }

    #endregion

    #region Outcome

    // Resolves one Phantom Clash battle: rating, promotion, rank rewards, records and statistics for
    // the attacker, plus the defender's own score and record. Everything below mutates only the staged
    // state and one guarded, field-targeted write to the defender's document.
    private static void FinishBattle(Mutation m, Theatre6PvpState s, Theatre6BattleState battle, bool interrupted)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Theatre6PvpRankTable rank = Rank(s.RankId)!;
        Theatre6EnemySnapshotState enemy = battle.Enemy;
        int oldScore = s.Score;
        int oldRank = s.RankId;
        int roundCount = battle.RoundResults.Count;
        int wins = battle.RoundResults.Count(result => result);
        // An abandoned or expired battle is a loss whatever the completed rounds were: the client's
        // give-up path always settles through the fail branch.
        bool finalWin = !interrupted && roundCount > 0 && wins * 2 > roundCount;
        bool allWin = roundCount == MatchOfferCount && wins == MatchOfferCount;
        bool advanceBattle = battle.Phase == PlayerStateInAdvanceBattle;
        bool promoted = advanceBattle && finalWin;
        var detail = new Theatre6ScoreDetail();
        int newScore = oldScore;
        if (promoted)
        {
            Theatre6PvpRankTable next = NextRank(rank.Id)!;
            s.RankId = next.Id;
            s.CurrentRankMinScore = RankFloor(next);
            newScore = s.CurrentRankMinScore;
            s.Score = newScore;
        }
        else if (!advanceBattle && !IsChallengeStateLocked(s, now))
        {
            double expected = ExpectedScore(s.Score, enemy.Score);
            detail.BaseWinScore = finalWin ? rank.BaseWinScore : 0;
            detail.EloScore = finalWin
                ? (int)Math.Round(rank.KWin * (1 - expected), MidpointRounding.AwayFromZero)
                : -(int)Math.Round(rank.KLose * expected, MidpointRounding.AwayFromZero);
            detail.AllWinScore = allWin ? rank.AllWinScore : 0;
            newScore = Math.Clamp(oldScore + detail.BaseWinScore + detail.EloScore + detail.AllWinScore,
                s.CurrentRankMinScore, Math.Max(s.CurrentRankMinScore, rank.MaxScore));
            s.Score = newScore;
        }
        int phaseAfter = BattlePhaseAfter(s, now);

        // Rank rewards are earned by reaching a rank and are granted exactly once per season: the
        // durable RewardedRanks set and the grant commit inside the same staged mutation, so a crash
        // or a retransmission can never grant the same rank twice.
        List<int> granted = new();
        if (!s.RewardedRanks.Contains(s.RankId))
        {
            s.RewardedRanks.Add(s.RankId);
            // Theatre6PvpRank.RewardIds is a single authored column, so a rank carries at most one
            // reward id, and the lowest ranks author none.
            if (Rank(s.RankId) is { RewardIds: int rewardId } && rewardId > 0)
            {
                m.Grant(rewardId);
                granted.Add(rewardId);
            }
        }
        s.PendingRankRewardIds = granted;

        int rankKey = oldRank;
        Increment(advanceBattle ? s.Stats.Advance : s.Stats.Normal, rankKey);
        if (finalWin) Increment(advanceBattle ? s.Stats.AdvanceWins : s.Stats.NormalWins, rankKey);

        int scoreChange = newScore - oldScore;
        var record = new Theatre6BattleRecordState
        {
            BattleId = battle.BattleId,
            BattleTime = Now(),
            IsWin = finalWin,
            IsAllWin = allWin,
            ScoreChange = scoreChange,
            RobotId = enemy.RobotId,
            IsAttacker = true,
            PlayerId = enemy.PlayerId,
            EnemyName = enemy.Name,
            EnemyHeadPortraitId = enemy.HeadPortraitId,
            EnemyHeadFrameId = enemy.HeadFrameId,
            EnemyRankId = enemy.RankId,
            EnemyScore = enemy.Score,
            DefenseBuffId = enemy.DefenseBuffId,
            MyRankId = oldRank,
            Status = interrupted ? 1 : 0
        };
        s.BattleRecords.Insert(0, record);
        s.BattleRecords = s.BattleRecords.Take(Cfg("MaxBattleRecordCount")).ToList();
        // Authored Phantom Clash missions count completed non-promotion matches only
        // (parameter 2 = normal battle); promotion challenges never count.
        if (!advanceBattle) Theatre6Module.RecordMetaProgress(m, "PvpBattle", 1, parameter: 2);
        // The battle is filed under the season it was authorised in, not under whatever authorisation
        // this request derived: expiry and give-up can run after the calendar closed, when the current
        // authorisation is already zero, and attributing the record or the defence outcome to season 0
        // would scatter history and lose the outcome's own scope.
        int battleSeasonId = s.InitializedSeasonId != 0 ? s.InitializedSeasonId : s.AuthorizedSeasonId;
        bool newHistory = !s.RankRecords.TryGetValue(battleSeasonId, out Theatre6RankRecordState? previous)
            || s.Score > previous.Score || s.RankId != previous.RankId;
        s.RankRecords[battleSeasonId] = new Theatre6RankRecordState { RankId = s.RankId, Score = s.Score };
        battle.Finished = true;
        battle.Phase = phaseAfter;
        s.PlayerState = s.Score >= rank.MaxScore && s.RankId == oldRank ? PlayerStateInAdvanceBattle : PlayerStateInit;
        battle.Result = new Theatre6FightResultState
        {
            RoundResults = battle.RoundResults.ToList(),
            OldScore = oldScore,
            NewScore = newScore,
            RankId = s.RankId,
            Phase = phaseAfter,
            IsFinalWin = finalWin,
            IsAdvanceBattleWin = promoted,
            BaseWinScore = detail.BaseWinScore,
            EloScore = detail.EloScore,
            AllWinScore = detail.AllWinScore,
            IsNewHistory = newHistory
        };

        m.Push(new NotifyTheatre6PvpScoreUpdate { RankId = s.RankId, Score = s.Score });
        m.Push(new NotifyTheatre6PvpBattleStatsUpdate
        {
            BattleStats = new Theatre6BattleStats
            {
                NormalBattleCounts = s.Stats.Normal.ToDictionary(),
                NormalBattleWinCounts = s.Stats.NormalWins.ToDictionary(),
                AdvanceBattleCounts = s.Stats.Advance.ToDictionary(),
                AdvanceBattleWinCounts = s.Stats.AdvanceWins.ToDictionary()
            }
        });
        m.Push(new NotifyTheatre6BattleRecordsUpdate { BattleRecords = [Record(record)] });

        // The defence outcome for a human opponent is frozen here, in the same staged mutation that
        // records the attacker's own result: it must commit together with that result, and it carries
        // everything the defender's session needs so the hand-off never has to read the attacker again.
        if (enemy.IsHuman && enemy.PlayerId > 0 && enemy.PlayerId != m.Player.PlayerData.Id)
            s.PendingDefenseOutcomes.Add(new Theatre6DefenseOutcome
            {
                SeasonId = battleSeasonId,
                AttackerId = m.Player.PlayerData.Id,
                BattleId = battle.BattleId,
                DefenderId = enemy.PlayerId,
                DefenderRankId = enemy.RankId,
                DefenderScore = enemy.Score,
                AttackerPreScore = oldScore,
                AttackerWin = finalWin,
                AttackerAllWin = allWin,
                DefenseBuffId = s.DefenseBuffId,
                AttackerName = m.Player.PlayerData.Name,
                AttackerHeadPortraitId = m.Player.PlayerData.CurrHeadPortraitId,
                AttackerHeadFrameId = m.Player.PlayerData.CurrHeadFrameId,
                AttackerRankId = oldRank,
                CreatedAt = Now()
            });
    }

    // LOCAL_POLICY: the authored tips say a locked score neither gains nor loses
    // (Theatre6PvpScoreDetailDesc5/6), which the client selects from BattlePhase 2.
    private static bool IsChallengeStateLocked(Theatre6PvpState s, DateTimeOffset now)
    {
        Theatre6PvpRankTable? rank = Rank(s.RankId);
        return rank is not null && s.Score >= rank.MaxScore && !IsChallengeState(s, now);
    }

    // LOCAL_POLICY: the expected-score composition of the Elo term is server-only. The K operands, the
    // flat win score and the all-win bonus are the authored Theatre6PvpRank columns.
    private static double ExpectedScore(int mine, int opponent) =>
        1d / (1d + Math.Pow(10d, (opponent - mine) / 400d));

    private static void Increment(Dictionary<int, int> map, int key) =>
        map[key] = map.TryGetValue(key, out int value) ? value + 1 : 1;

    // Cross-player defence result: an origin outbox plus a recipient watermark, no second queue.
    //  * the outcome is enqueued inside the attacker's committed mutation, so a failed attacker save
    //    can never leave the defender mutated for an outcome that never happened;
    //  * identity is (SeasonId, AttackerId, BattleId) - BattleId alone is attacker-local and collides
    //    across attackers - and `NextBattleId` allocates it monotonically, which is what makes the
    //    recipient's watermark sound;
    //  * the recipient discovers undelivered outcomes by querying the committed origin outboxes, so a
    //    defender converges without the attacker ever returning, and nothing is lost while an entry
    //    sits unsent;
    //  * both directions scan ascending BattleId and stop at the first entry they cannot deliver, so
    //    the watermark can never advance past a gap;
    //  * a season boundary discards the remainder instead of applying an old-season delta to the new
    //    season's score.
    internal static void HandOffPendingDefense(Session attacker)
    {
        Player origin = attacker.player;
        List<Theatre6DefenseOutcome> outbox = origin.Theatre6.Pvp.PendingDefenseOutcomes;
        if (outbox.Count == 0) return;
        bool changed = false;
        foreach (Theatre6DefenseOutcome outcome in outbox.OrderBy(item => item.BattleId).ToList())
        {
            if (!DeliverOutcome(attacker, origin, outcome, message => attacker.log.Warn(message)))
            {
                // Stop here: entries behind an undeliverable one must not overtake it.
                break;
            }
            outbox.Remove(outcome);
            changed = true;
        }
        if (changed) PersistOrigin(attacker);
    }

    // Recipient-side recovery, called on login and before writes (Main wires it like
    // GuildModule.RecoverParticipant). The defender pulls from the committed origin outboxes. Each
    // delivery is persisted and rolled back on its own inside DeliverOutcome, so this only has to
    // discover work and retire what was delivered.
    internal static void RecoverPendingDefense(Session session)
    {
        Player defender = session.player;
        if (defender.Theatre6.PendingMutation is not null) return;
        long defenderId = defender.PlayerData.Id;
        List<Player> origins;
        try
        {
            origins = Player.collection.Find(Builders<Player>.Filter.ElemMatch(
                player => player.Theatre6.Pvp.PendingDefenseOutcomes, item => item.DefenderId == defenderId)).ToList();
        }
        catch (Exception exception)
        {
            // Recovery is a background participant step: a database hiccup must never fail the login or
            // the request that triggered it. The watermark makes the next attempt safe.
            session.log.Warn($"Theatre6 PvP defence recovery could not read origin outboxes, retrying later: defender={defenderId} reason={exception.Message}");
            return;
        }
        if (origins.Count == 0) return;

        List<(Player Origin, Theatre6DefenseOutcome Outcome)> delivered = new();
        foreach (Player origin in origins.OrderBy(player => player.PlayerData.Id))
        {
            foreach (Theatre6DefenseOutcome outcome in origin.Theatre6.Pvp.PendingDefenseOutcomes
                .Where(item => item.DefenderId == defenderId).OrderBy(item => item.BattleId).ToList())
            {
                if (!DeliverOutcome(session, origin, outcome, message => session.log.Warn(message))) break;
                delivered.Add((origin, outcome));
            }
        }
        // Only after the recipient's side is durable are the origin entries retired. Each pull is
        // identity-guarded, so a concurrent retry cannot remove a different outcome, and the live
        // session of an online origin is kept in step so its next save cannot resurrect the entry.
        foreach ((Player origin, Theatre6DefenseOutcome outcome) in delivered) RetireOutcome(origin.PlayerData.Id, outcome, session);
    }

    // Applies one outcome to the defender and reports whether it may be retired from the origin.
    // Returns false only when the entry must be retried later; already-applied and out-of-season
    // entries return true so they are retired rather than lingering forever.
    private static bool DeliverOutcome(Session actor, Player origin, Theatre6DefenseOutcome outcome, Action<string> warn)
    {
        if (outcome.DefenderId <= 0 || outcome.DefenderId == origin.PlayerData.Id) return true;
        lock (Session.GetPlayerOperationLock(outcome.DefenderId))
        {
            // The acting session owns its player object: during login recovery the session is not yet
            // discoverable through SessionFromUID, and resolving an offline copy here would be erased
            // by the live player's next save.
            Player? defender = outcome.DefenderId == actor.player.PlayerData.Id
                ? actor.player
                : Server.Instance.SessionFromUID(outcome.DefenderId)?.player ?? Player.TryFromPlayerId(outcome.DefenderId);
            if (defender is null)
            {
                warn($"Theatre6 PvP defence deferred: defender {outcome.DefenderId} has no document (season={outcome.SeasonId} attacker={outcome.AttackerId} battle={outcome.BattleId}).");
                return false;
            }
            // A frozen pending mutation owns this document until it resolves; retry later instead of
            // writing over it.
            if (defender.Theatre6.PendingMutation is not null) return false;
            // The season the defender is in is derived from live authority. When the mode is not
            // available at all the current season is ZERO, never the cached authorisation: a connection
            // that outlived the season still carries the old id, and falling back to it would let a late
            // outcome score after the period expired. A mismatched season is recorded and watermarked
            // with a zero delta instead, so history and the acknowledgement survive.
            int currentSeason = Theatre6Module.IsAvailable(defender, DateTimeOffset.UtcNow, out int seasonId, out _)
                ? seasonId
                : 0;

            Theatre6PvpState snapshot = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Theatre6PvpState>(defender.Theatre6.Pvp.ToBson());
            if (!ApplyDefenseOutcome(defender.Theatre6.Pvp, outcome, currentSeason, warn)) return false;
            try
            {
                defender.SaveChecked();
            }
            catch (Exception exception)
            {
                defender.Theatre6.Pvp = snapshot;
                warn($"Theatre6 PvP defence delivery failed, retrying later: defender={outcome.DefenderId} season={outcome.SeasonId} attacker={outcome.AttackerId} battle={outcome.BattleId} reason={exception.Message}");
                return false;
            }
            return true;
        }
    }

    // Applies the authored defence delta once. False means "retry later" (rank row unavailable), not
    // "failed": an already-applied outcome is not a retry.
    private static bool ApplyDefenseOutcome(Theatre6PvpState pvp, Theatre6DefenseOutcome outcome, int currentSeason, Action<string> warn)
    {
        Theatre6DefenseWatermark? watermark = pvp.AppliedDefenseWatermarks.FirstOrDefault(
            item => item.SeasonId == outcome.SeasonId && item.AttackerId == outcome.AttackerId);
        if (watermark is not null && outcome.BattleId <= watermark.HighestAppliedBattleId) return true;
        if (Rank(outcome.DefenderRankId) is not { } rank)
        {
            warn($"Theatre6 PvP defence deferred: rank {outcome.DefenderRankId} missing (season={outcome.SeasonId} attacker={outcome.AttackerId} battle={outcome.BattleId}).");
            return false;
        }
        double expected = ExpectedScore(outcome.DefenderScore, outcome.AttackerPreScore);
        // LOCAL_POLICY for an outcome that outlived its season: the period is closed, so it may never
        // move the new season's score, but it is still acknowledged - recorded and watermarked, with a
        // zero delta - rather than dropped, because the defender did defend and the origin must be able
        // to retire the entry after a durable acknowledgement.
        int delta = outcome.SeasonId != currentSeason
            ? 0
            : outcome.AttackerWin
                ? -(int)Math.Round(rank.DefKLose * expected, MidpointRounding.AwayFromZero)
                : (int)Math.Round(rank.DefKWin * (1 - expected), MidpointRounding.AwayFromZero);
        // The delta is applied to the defender's CURRENT score so their own progress since the battle
        // is never rolled back, and it is clamped against their CURRENT rank band for the same reason.
        // The expectation above still uses the frozen defender score, which is the value in force when
        // the battle was authorised.
        int previous = pvp.Score;
        int score = previous + delta;
        if (Rank(pvp.RankId) is { } current)
        {
            int floor = current.MinScore ?? 0;
            score = Math.Clamp(score, floor, Math.Max(floor, current.MaxScore));
        }
        pvp.Score = score;
        // The record carries the change that was actually applied, after any clamp.
        delta = score - previous;
        pvp.BattleRecords.Insert(0, new Theatre6BattleRecordState
        {
            BattleId = outcome.BattleId,
            // The battle happened when it happened: the record keeps the frozen attacker-side time, not
            // the moment this delivery was drained.
            BattleTime = outcome.CreatedAt,
            IsWin = !outcome.AttackerWin,
            IsAllWin = outcome.AttackerWin && outcome.AttackerAllWin,
            ScoreChange = delta,
            IsAttacker = false,
            PlayerId = outcome.AttackerId,
            EnemyName = outcome.AttackerName,
            EnemyHeadPortraitId = outcome.AttackerHeadPortraitId,
            EnemyHeadFrameId = outcome.AttackerHeadFrameId,
            EnemyRankId = outcome.AttackerRankId,
            EnemyScore = outcome.AttackerPreScore,
            DefenseBuffId = outcome.DefenseBuffId,
            MyRankId = outcome.DefenderRankId,
            Status = 0
        });
        pvp.BattleRecords = pvp.BattleRecords.Take(Cfg("MaxBattleRecordCount")).ToList();
        if (watermark is null)
            pvp.AppliedDefenseWatermarks.Add(new Theatre6DefenseWatermark
            {
                SeasonId = outcome.SeasonId,
                AttackerId = outcome.AttackerId,
                HighestAppliedBattleId = outcome.BattleId
            });
        else
            watermark.HighestAppliedBattleId = outcome.BattleId;
        return true;
    }

    private static void PersistOrigin(Session origin)
    {
        try
        {
            origin.player.SaveChecked();
        }
        catch (Exception exception)
        {
            // The recipient already applied its side and the watermark makes the delivery idempotent,
            // so a failed retirement only means the entry is offered again and skipped.
            origin.log.Warn($"Theatre6 PvP defence outbox persist failed, will retry: attacker={origin.player.PlayerData.Id} reason={exception.Message}");
        }
    }

    // Retires one delivered outcome from an origin: the durable document first, then the live session
    // of an online origin, so its next save cannot restore the entry.
    private static void RetireOutcome(long originId, Theatre6DefenseOutcome outcome, Session actor)
    {
        // A frozen pending mutation replays the origin's own Theatre6 state, which would restore the
        // entry we retired here; leave it queued (the recipient watermark already prevents any second
        // credit) and retire it once that mutation resolves.
        Player? origin = null;
        try
        {
            origin = Server.Instance.SessionFromUID(originId)?.player ?? Player.TryFromPlayerId(originId);
        }
        catch (Exception exception)
        {
            actor.log.Warn($"Theatre6 PvP defence outbox retirement skipped this pass: origin {originId} could not be read ({exception.Message}).");
            return;
        }
        if (origin?.Theatre6.PendingMutation is not null)
        {
            actor.log.Warn($"Theatre6 PvP defence outbox retirement deferred: origin {originId} has a frozen pending mutation (season={outcome.SeasonId} battle={outcome.BattleId}).");
            return;
        }
        FilterDefinition<Player> filter = Builders<Player>.Filter.And(
            Builders<Player>.Filter.Eq(player => player.PlayerData.Id, originId),
            Builders<Player>.Filter.ElemMatch(player => player.Theatre6.Pvp.PendingDefenseOutcomes,
                item => item.SeasonId == outcome.SeasonId && item.AttackerId == outcome.AttackerId && item.BattleId == outcome.BattleId));
        UpdateDefinition<Player> update = Builders<Player>.Update.PullFilter(
            player => player.Theatre6.Pvp.PendingDefenseOutcomes,
            item => item.SeasonId == outcome.SeasonId && item.AttackerId == outcome.AttackerId && item.BattleId == outcome.BattleId);
        try
        {
            Player.collection.UpdateOne(filter, update);
        }
        catch (Exception exception)
        {
            actor.log.Warn($"Theatre6 PvP defence outbox retirement failed in the database, will retry: attacker={originId} season={outcome.SeasonId} battle={outcome.BattleId} reason={exception.Message}");
            return;
        }
        Session? online = Server.Instance.SessionFromUID(originId);
        if (online is null || ReferenceEquals(online, actor)) return;
        lock (Session.GetPlayerOperationLock(originId))
        {
            online.player.Theatre6.Pvp.PendingDefenseOutcomes.RemoveAll(item =>
                item.SeasonId == outcome.SeasonId && item.AttackerId == outcome.AttackerId && item.BattleId == outcome.BattleId);
        }
    }

    // Also serves the non-final rounds: the client reads Theatre6PvpFightResult.RoundResults on every
    // round to seed the next native entry (XTheatre6BattleAgency:_IsPvpUnfinished and
    // XTheatre6SubPvpModel:UpdateBattleResultData), so a round that has not finished yet still returns
    // one with the rounds so far and no rank movement.
    private static Theatre6PvpFightResult FightResult(Theatre6PvpState s, Theatre6BattleState battle)
    {
        Theatre6FightResultState? result = battle.Result;
        return new Theatre6PvpFightResult
        {
            RoundResults = result?.RoundResults ?? battle.RoundResults.ToList(),
            IsFinalWin = result?.IsFinalWin ?? false,
            RankId = result?.RankId ?? s.RankId,
            OldScore = result?.OldScore ?? s.Score,
            NewScore = result?.NewScore ?? s.Score,
            IsNewHistory = result?.IsNewHistory ?? false,
            BattlePhase = result?.Phase ?? battle.Phase,
            IsAdvanceBattleWin = result?.IsAdvanceBattleWin ?? false,
            ScoreDetail = new Theatre6ScoreDetail
            {
                BaseWinScore = result?.BaseWinScore ?? 0,
                EloScore = result?.EloScore ?? 0,
                AllWinScore = result?.AllWinScore ?? 0
            },
            RewardedRanks = s.RewardedRanks.ToList(),
            RankRewardGoods = s.PendingRankRewardIds.SelectMany(RewardHandler.GetRewardGoods).Select(good => new RewardGoods
            {
                Id = good.Id,
                TemplateId = good.TemplateId,
                Count = good.Count,
                RewardType = (int)(RewardHandler.GetRewardType(good) ?? 0)
            }).ToList()
        };
    }

    #endregion

    #region Matchmaking

    // LOCAL_POLICY: the authored operands decide the offer composition - Theatre6PvpRank.SearchUp/
    // SearchDown x Theatre6PvpConfig.MatchExpandMaxCount bound the score window, Theatre6PvpRankFight
    // supplies a tier pool, RobotProp is the robot-vs-saved-defence weight, PlayerDown/PlayerUp the
    // below-vs-above weight, PoolCount how many authored robots the tier offers. The client only ever
    // receives the frozen result.
    //
    // The scored operand is Theatre6PvpState.Score, the persisted Phantom Clash ladder score the client
    // shows in the rank bar, and the window/catalogue selection below is grounded in retail traffic: at
    // the authored initial score the offered opponents were catalogue robots whose authored scores
    // clustered inside the authored search window around the player's own score, which also fixes
    // Theatre6PvpRobot.Score to that same ladder domain.
    //
    // What is NOT recoverable is the domain of Theatre6PvpRankFight.MinScore. The table has no client
    // consumer, no extracted or installed source binds its thresholds to the ladder, and its authored
    // values (30000/60000/80000/100000) sit far above the authored initial score and above the MaxScore
    // of every rank but the last, so they could equally describe a different quantity such as saved-build
    // score or combat power. Reading them as ladder thresholds that unlock a tier pool is therefore an
    // AscNet policy, not a retail mapping, and it is applied only as an override: below every authored
    // threshold the pool stays the catalogue filtered by the window above, which is the behaviour the
    // traffic actually shows. No pool or robot id is ever hardcoded; every id in a match comes from these
    // tables.
    private static bool BuildMatches(Mutation m, Theatre6PvpState s, DateTimeOffset now)
    {
        Theatre6PvpRankTable? rank = Rank(s.RankId);
        if (rank is null) return false;
        // The tier table is applied as an override on the ladder score (see the LOCAL_POLICY note above:
        // its MinScore domain is unrecoverable, so this reading is policy). It never matches a fresh
        // season's starting score, since the lowest authored threshold is far above the authored
        // InitPoint, so below every threshold the pool is the authored robot catalogue inside the rank's
        // score window - the behaviour the retail traffic shows.
        int expansions = Math.Max(1, Cfg("MatchExpandMaxCount"));
        long lower = Math.Max(0, (long)s.Score - (long)(rank.SearchDown * expansions));
        long upper = Math.Min(int.MaxValue, (long)s.Score + (long)(rank.SearchUp * expansions));
        Theatre6PvpRankFightTable? band = TableReaderV2.Parse<Theatre6PvpRankFightTable>()
            .Where(row => row.MinScore is int threshold && s.Score >= threshold && row.RobotIds.Any(id => id > 0))
            .OrderBy(row => row.MinScore ?? 0).LastOrDefault();
        List<int> robots = band is not null
            ? band.RobotIds.Where(id => id > 0).Distinct().ToList()
            : TableReaderV2.Parse<Theatre6PvpRobotTable>()
                .Where(row => row.Score >= lower && row.Score <= upper)
                .OrderBy(row => row.Id).Select(row => row.Id).Distinct().ToList();
        List<Player> candidates = FindDefenders(m, s, rank);
        if (robots.Count == 0 && candidates.Count == 0) return false;

        int robotProp = rank.RobotProp ?? 0;
        // The robot side of an offer needs a robot pool, and the human side needs its authored below/above
        // weights. A matched tier carries both; when no tier matched there is no authored weight to use,
        // and the default row authors none either, so the choice stays unweighted instead of pinning one.
        int belowWeight = band?.PlayerDown ?? 0;
        int aboveWeight = band?.PlayerUp ?? 0;
        int pool = robots.Count == 0 ? 0 : band is null ? robots.Count : Math.Max(1, Math.Min(band.PoolCount, robots.Count));
        int offset = robots.Count == 0 ? 0 : RandomNumberGenerator.GetInt32(0, robots.Count);
        List<Theatre6MatchState> matches = new();
        for (int index = 0; index < MatchOfferCount; index++)
        {
            // With no robot pool every offer must be a saved defence, and BuildMatches has already failed
            // when there is neither a pool nor a candidate, so the tiers below are always real.
            Player? defender = robots.Count == 0 || (candidates.Count > 0 && RandomNumberGenerator.GetInt32(0, Basis) >= robotProp)
                ? PickDefender(candidates, belowWeight, aboveWeight, s.Score)
                : null;
            if (defender is null && robots.Count == 0) return false;
            Theatre6EnemySnapshotState enemy = defender is not null
                ? HumanSnapshot(defender)
                : Snapshot(TableReaderV2.Parse<Theatre6PvpRobotTable>().First(robot => robot.Id == robots[(offset + index % pool) % robots.Count]));
            matches.Add(new Theatre6MatchState { Uid = s.NextMatchUid++, Enemy = enemy });
        }
        s.Matches = matches;
        s.LastRefreshTime = Now();
        if (s.RefreshPeriodStart == 0) s.RefreshPeriodStart = s.LastRefreshTime;
        return true;
    }

    // A saved defence is only offered when it is complete: the defence UI refuses to confirm below
    // MaxSlotDefenseLineupLimit filled positions, so an incomplete defence never faces an attacker.
    private static List<Player> FindDefenders(Mutation m, Theatre6PvpState s, Theatre6PvpRankTable rank)
    {
        int limit = Cfg("MaxSlotDefenseLineupLimit");
        long lower = Math.Max(0, (long)s.Score - (long)(rank.SearchDown * Math.Max(1, Cfg("MatchExpandMaxCount"))));
        long upper = Math.Min(int.MaxValue, (long)s.Score + (long)(rank.SearchUp * Math.Max(1, Cfg("MatchExpandMaxCount"))));
        FilterDefinition<Player> filter = Builders<Player>.Filter.And(
            Builders<Player>.Filter.Ne(player => player.PlayerData.Id, m.Player.PlayerData.Id),
            Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.AuthorizedSeasonId, s.AuthorizedSeasonId),
            Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.InitializedSeasonId, s.AuthorizedSeasonId),
            Builders<Player>.Filter.Gte(player => player.Theatre6.Pvp.Score, (int)lower),
            Builders<Player>.Filter.Lte(player => player.Theatre6.Pvp.Score, (int)upper),
            Builders<Player>.Filter.Size(player => player.Theatre6.Pvp.DefenseFiles, limit));
        return Player.collection.Find(filter).Limit(MaxHumanCandidates).ToList();
    }

    // The authored below/above weights decide which side of the player's score a defender comes from.
    // They are passed in rather than read off a tier row, because a score below every authored threshold
    // has no tier at all; an unauthored weight leaves the choice unweighted rather than pinned.
    private static Player? PickDefender(List<Player> candidates, int belowWeight, int aboveWeight, int myScore)
    {
        List<Player> below = candidates.Where(player => player.Theatre6.Pvp.Score <= myScore).ToList();
        List<Player> above = candidates.Where(player => player.Theatre6.Pvp.Score > myScore).ToList();
        bool preferBelow = belowWeight + aboveWeight <= 0
            || RandomNumberGenerator.GetInt32(0, belowWeight + aboveWeight) < belowWeight;
        List<Player> pool = preferBelow ? below : above;
        if (pool.Count == 0) pool = preferBelow ? above : below;
        return pool.Count == 0 ? null : pool[RandomNumberGenerator.GetInt32(0, pool.Count)];
    }

    private static Theatre6EnemySnapshotState HumanSnapshot(Player defender)
    {
        Theatre6PvpState pvp = defender.Theatre6.Pvp;
        return new Theatre6EnemySnapshotState
        {
            PlayerId = defender.PlayerData.Id,
            IsHuman = true,
            RankId = pvp.RankId,
            Score = pvp.Score,
            DefenseBuffId = pvp.DefenseBuffId,
            Name = defender.PlayerData.Name,
            HeadPortraitId = defender.PlayerData.CurrHeadPortraitId,
            HeadFrameId = defender.PlayerData.CurrHeadFrameId,
            MistNum = Rank(pvp.RankId)?.MistNum ?? 0,
            Files = pvp.DefenseFiles.Select(Theatre6Module.Clone).ToList()
        };
    }

    private static Theatre6EnemySnapshotState Snapshot(Theatre6PvpRobotTable robot) => new()
    {
        PlayerId = 0,
        IsHuman = false,
        RobotId = robot.Id,
        RankId = Ranks().FirstOrDefault(row => robot.Score >= (row.MinScore ?? 0) && robot.Score <= row.MaxScore)?.Id ?? 0,
        Score = robot.Score,
        DefenseBuffId = robot.BuffId ?? 0,
        Name = robot.Name,
        HeadPortraitId = robot.HeadIcon,
        HeadFrameId = 0,
        MistNum = 0,
        Files = new List<Theatre6FileState>()
    };

    #endregion

    #region Projection

    private static List<Theatre6MatchEnemy> Enemies(Theatre6PvpState s) =>
        s.Matches.Where(match => match.Enemy is not null)
            .Select(match => new Theatre6MatchEnemy { Uid = match.Uid, BattleData = BattleDb(match.Enemy), MistNum = match.Enemy.MistNum })
            .ToList();

    private static Theatre6PlayerBattleDb BattleDb(Theatre6EnemySnapshotState enemy) => new()
    {
        PlayerId = enemy.PlayerId,
        UpdateTime = 0,
        Name = enemy.Name,
        HeadPortraitId = enemy.HeadPortraitId,
        HeadFrameId = enemy.HeadFrameId,
        RankId = enemy.RankId,
        Score = enemy.Score,
        // A robot carries RobotId and no files: the client rebuilds them from the authored monster rows.
        SaveFiles = enemy.IsHuman ? enemy.Files.Select(Theatre6Module.ToWire).ToList() : new List<Theatre6FileData>(),
        RobotId = enemy.RobotId,
        DefenseBuffId = enemy.DefenseBuffId
    };

    private static Theatre6TinyBattleState? BattleState(Theatre6PvpState s) => s.Battle is { } battle
        ? new Theatre6TinyBattleState
        {
            EnemyId = battle.EnemyUid,
            EnemyData = BattleDb(battle.Enemy),
            MyLineups = battle.MyFiles.Select(Theatre6Module.ToWire).ToList(),
            RoundResults = battle.RoundResults.ToList(),
            CurrentRound = battle.CurrentRound,
            IsFinished = battle.Finished
        }
        : null;

    private static Theatre6PvpActivityData Activity(Theatre6PvpState s) => new()
    {
        ActivityId = s.AuthorizedSeasonId,
        RankId = s.RankId,
        Score = s.Score,
        PlayerState = s.PlayerState,
        TinyBattleState = BattleState(s),
        Enemies = Enemies(s),
        Lineups = s.DefenseFiles.Select(Theatre6Module.ToWire).ToList(),
        RewardedRanks = s.RewardedRanks.ToList(),
        LastRefreshMatchTime = s.LastRefreshTime,
        RefreshRemainSeconds = RemainingRefreshSeconds(s),
        AttackBuffId = s.Battle is { Finished: false } ? s.Battle.BuffId : 0,
        DefenseBuffId = s.DefenseBuffId,
        PvpRankRecords = RankRecords(s),
        BattleStats = Stats(s),
        ActionPoint = s.ActionPoint,
        LastActionPointRecoverTime = s.ActionPointTime
    };

    private static int RemainingRefreshSeconds(Theatre6PvpState s) =>
        Math.Max(0, Cfg("RefreshMatchCd") - (int)Math.Min(int.MaxValue, Math.Max(0, Now() - s.LastRefreshTime)));

    private static Dictionary<int, Theatre6RankRecord> RankRecords(Theatre6PvpState s) =>
        s.RankRecords.ToDictionary(entry => entry.Key, entry => new Theatre6RankRecord { RankId = entry.Value.RankId, Score = entry.Value.Score });

    private static NotifyTheatre6PvpGetActionPoint ApPush(Theatre6PvpState s) =>
        new() { ActionPoint = s.ActionPoint, LastActionPointRecoverTime = s.ActionPointTime };

    private static Theatre6BattleStats Stats(Theatre6PvpState s) => new()
    {
        NormalBattleCounts = s.Stats.Normal.ToDictionary(),
        NormalBattleWinCounts = s.Stats.NormalWins.ToDictionary(),
        AdvanceBattleCounts = s.Stats.Advance.ToDictionary(),
        AdvanceBattleWinCounts = s.Stats.AdvanceWins.ToDictionary()
    };

    private static Theatre6BattleRecord Record(Theatre6BattleRecordState record) => new()
    {
        BattleId = record.BattleId,
        BattleTime = record.BattleTime,
        IsAttacker = record.IsAttacker,
        IsWin = record.IsWin,
        IsAllWin = record.IsAllWin,
        ScoreChange = record.ScoreChange,
        EnemyInfo = new Theatre6PlayerBattleDb
        {
            PlayerId = record.PlayerId,
            Name = record.EnemyName,
            HeadPortraitId = record.EnemyHeadPortraitId,
            HeadFrameId = record.EnemyHeadFrameId,
            RankId = record.EnemyRankId,
            Score = record.EnemyScore,
            DefenseBuffId = record.DefenseBuffId,
            RobotId = record.RobotId
        },
        MyRankId = record.MyRankId,
        RecordStatus = record.Status
    };

    private static int RepeatCount(IEnumerable<Theatre6FileSlot> slots) =>
        slots.GroupBy(slot => (slot.CharacterId, slot.SlotId)).Max(group => group.Count());

    private static List<Theatre6FileState> ResolveFiles(Player player, IEnumerable<Theatre6FileSlot> slots)
    {
        List<Theatre6FileState> files = new();
        foreach (Theatre6FileSlot slot in slots)
        {
            Theatre6FileState? file = player.Theatre6.Files.FirstOrDefault(
                candidate => candidate.CharacterId == slot.CharacterId && candidate.SlotId == slot.SlotId);
            Require(file is not null, FileNotFound);
            files.Add(Theatre6Module.Clone(file!));
        }
        return files;
    }

    private static int BuffCode(Theatre6PvpState s, int id, bool attack)
    {
        Theatre6PvpRankTable? rank = Rank(s.RankId);
        if (rank is null) return RankNotFound;
        int groupId = rank.PvpBuffGroupId ?? 0;
        if (groupId <= 0) return id == 0 ? 0 : BuffUnsupported;
        Theatre6PvpBuffGroupTable? group = TableReaderV2.Parse<Theatre6PvpBuffGroupTable>().FirstOrDefault(row => row.Id == groupId);
        if (group is null) return BuffGroupConfigMissing;
        if (id == 0) return BuffMustSelect;
        List<int> allowed = attack ? group.AttBuffs : group.DefBuffs;
        if (!allowed.Contains(id)) return BuffNotInGroup;
        return TableReaderV2.Parse<Theatre6PvpBuffTable>().Any(row => row.Id == id) ? 0 : BuffConfigMissing;
    }

    #endregion
}
