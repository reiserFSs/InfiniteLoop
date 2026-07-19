using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

#region Theatre6 PVP wire contracts
[MessagePackObject(true)] public sealed class Theatre6PvpUpdateDefenseResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6RankRecord { public int RankId { get; set; } public int Score { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PlayerBattleDb { public long PlayerId { get; set; } public int UpdateTime { get; set; } public string Name { get; set; } = ""; public long HeadPortraitId { get; set; } public long HeadFrameId { get; set; } public int RankId { get; set; } public int Score { get; set; } public List<Theatre6FileData> SaveFiles { get; set; } = new(); public int RobotId { get; set; } public int DefenseBuffId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6MatchEnemy { public int Uid { get; set; } public Theatre6PlayerBattleDb BattleData { get; set; } = new(); public int MistNum { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6MatchResult { public int Code { get; set; } public List<Theatre6MatchEnemy> Enemies { get; set; } = new(); }
[MessagePackObject(true)] public class Theatre6TinyBattleState { public int EnemyId { get; set; } public Theatre6PlayerBattleDb EnemyData { get; set; } = new(); public List<Theatre6FileData> MyLineups { get; set; } = new(); public List<bool> RoundResults { get; set; } = new(); public int CurrentRound { get; set; } public bool IsFinished { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6BattleStats { public Dictionary<int,int>? NormalBattleCounts { get; set; } public Dictionary<int,int>? NormalBattleWinCounts { get; set; } public Dictionary<int,int>? AdvanceBattleCounts { get; set; } public Dictionary<int,int>? AdvanceBattleWinCounts { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6BattleRecord { public int BattleId { get; set; } public long BattleTime { get; set; } public bool IsAttacker { get; set; } public bool IsWin { get; set; } public bool IsAllWin { get; set; } public int ScoreChange { get; set; } public Theatre6PlayerBattleDb EnemyInfo { get; set; } = new(); public int MyRankId { get; set; } public int RecordStatus { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6RankPlayer { public long Id { get; set; } public string Name { get; set; } = ""; public long HeadPortraitId { get; set; } public long HeadFrameId { get; set; } public int Score { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6ScoreDetail { public int BaseWinScore { get; set; } public int EloScore { get; set; } public int AllWinScore { get; set; } public int DefenseScore { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpFightResult { public List<bool> RoundResults { get; set; } = new(); public bool IsFinalWin { get; set; } public int RankId { get; set; } public int OldScore { get; set; } public int NewScore { get; set; } public bool IsNewHistory { get; set; } public int BattlePhase { get; set; } public bool IsAdvanceBattleWin { get; set; } public Theatre6ScoreDetail ScoreDetail { get; set; } = new(); public List<int> RewardedRanks { get; set; } = new(); public List<RewardGoods> RankRewardGoods { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6PvpActivityData { public int ActivityId { get; set; } public int RankId { get; set; } public int Score { get; set; } public int PlayerState { get; set; } public Theatre6TinyBattleState? TinyBattleState { get; set; } public List<Theatre6MatchEnemy> Enemies { get; set; } = new(); public List<Theatre6FileData> Lineups { get; set; } = new(); public List<int> RewardedRanks { get; set; } = new(); public long LastRefreshMatchTime { get; set; } public int RefreshRemainSeconds { get; set; } public int AttackBuffId { get; set; } public int DefenseBuffId { get; set; } public Dictionary<int,Theatre6RankRecord> PvpRankRecords { get; set; } = new(); public Theatre6BattleStats BattleStats { get; set; } = new(); public int ActionPoint { get; set; } public long LastActionPointRecoverTime { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6GetPvpPreviewInfoResponse { public int Code { get; set; } public Dictionary<int,Theatre6RankRecord> PvpRankRecords { get; set; } = new(); public int ActionPoint { get; set; } public long LastActionPointRecoverTime { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpStartResponse { public int Code { get; set; } public Theatre6PvpActivityData? ActivityData { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpUpdateDefenseRequest { public int? BuffId { get; set; } public List<Theatre6FileSlot?>? Slots { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpRefreshMatchResponse { public int Code { get; set; } public Theatre6MatchResult? MatchResult { get; set; } public long LastRefreshMatchTime { get; set; } public int RefreshRemainSeconds { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpGetActionPointResponse { public int Code { get; set; } public int ActionPoint { get; set; } public long LastActionPointRecoverTime { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpStartFightRequest { public int EnemyId { get; set; } public List<Theatre6FileSlot> MyFileSlots { get; set; } = new(); public int? BuffId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpStartFightResponse { public int Code { get; set; } public Theatre6TinyBattleState? BattleState { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpRestartFightResponse { public int Code { get; set; } public Theatre6TinyBattleState? BattleState { get; set; } public Theatre6PvpFightResult? FightResult { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6PvpQueryRankResponse { public int Code { get; set; } public List<Theatre6RankPlayer> RankPlayerInfos { get; set; } = new(); public int TotalCount { get; set; } public int SelfRank { get; set; } = -1; }
[MessagePackObject(true)] public sealed class Theatre6PvpGetBattleRecordsResponse { public int Code { get; set; } public List<Theatre6BattleRecord> BattleRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6PvpGiveUpFightResponse { public int Code { get; set; } public Theatre6PvpFightResult? FightResult { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTheatre6PvpGetActionPoint { public int ActionPoint { get; set; } public long LastActionPointRecoverTime { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTheatre6PvpTinyBattleState : Theatre6TinyBattleState { }
[MessagePackObject(true)] public sealed class NotifyTheatre6BattleRecordsUpdate { public List<Theatre6BattleRecord> BattleRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyTheatre6DefenseUpdate { public int DefenseBuffId { get; set; } public List<Theatre6FileData> Lineups { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyTheatre6PvpBattleStatsUpdate { public Theatre6BattleStats BattleStats { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyMatchPlayersUpdate { public int Code { get; set; } public List<Theatre6MatchEnemy> Enemies { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyTheatre6PvpScoreUpdate { public int RankId { get; set; } public int Score { get; set; } }
#endregion

internal static class Theatre6PvpModule
{
    private const int NotOpen=20427001, AlreadyInBattle=20427003, DataError=20427004, RankNotFound=20427005, RefreshCd=20427007, RefreshLimit=20427008, ActionPointLow=20427010, DefenseCount=20427011, RepeatLimit=20427012, FileNotFound=20427013, NotInBattle=20427014, FightRoundError=20427015, EnemyNotFound=20427016, FileInvalid=20427017, BattleFinished=20427018, AttackCount=20427021, DefenseMax=20427022, BuffMustSelect=20427027, BuffNotInGroup=20427028;
    private const int LeaderboardPageSize = 100;

    private static int Cfg(string key) => TableReaderV2.Parse<Theatre6PvpConfigTable>().Single(x=>x.Key==key).Values;
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static bool ReconcileActionPoint(Theatre6PvpState s,long now)
    {
        int cap=Cfg("ActionPointMaxLimit"), interval=Cfg("ActionPointRecoverInterval");
        if(s.ActionPoint>=cap || s.ActionPointTime<=0 || now<s.ActionPointTime+interval) return false;
        long ticks=(now-s.ActionPointTime)/interval; int gain=(int)Math.Min(ticks,cap-s.ActionPoint);
        if(gain<=0)return false; s.ActionPoint+=gain; s.ActionPointTime+=gain*interval; return true;
    }
    private static int Gate(Player player) => Gate(player, DateTimeOffset.UtcNow);
    internal static int Gate(Player player, DateTimeOffset now) =>
        Theatre6Module.IsAvailable(player, now, out int seasonId, out _)
        && player.Theatre6.Pvp.AuthorizedSeasonId == seasonId
        && TableReaderV2.Parse<Theatre6PvpActivityTable>().Any(activity => activity.Id == seasonId)
            ? 0
            : NotOpen;
    private static Theatre6PvpRankTable? Rank(Theatre6PvpState s)=>TableReaderV2.Parse<Theatre6PvpRankTable>().FirstOrDefault(x=>x.Id==s.RankId);
    private static Theatre6PvpRankTable? RankForScore(int score)
    {
        var ranks=TableReaderV2.Parse<Theatre6PvpRankTable>();
        for(int i=0;i<ranks.Count;i++)
            if((i==0||score>=ranks[i].MinScore)&&(i==ranks.Count-1||score<=ranks[i].MaxScore))
                return ranks[i];
        return null;
    }
    private static int ReconcileSeason(Session session, Theatre6PvpState s, long now)
    {
        if(s.InitializedSeasonId==s.AuthorizedSeasonId)return Rank(s)==null?RankNotFound:0;
        var activity=TableReaderV2.Parse<Theatre6PvpActivityTable>().FirstOrDefault(x=>x.Id==s.AuthorizedSeasonId);
        if(activity==null)return NotOpen;
        var rank=RankForScore(activity.InitPoint);
        if(rank==null)return RankNotFound;
        s.InitializeSeason(activity.Id,rank.Id,activity.InitPoint,Cfg("ActionPointInit"),now);
        return 0;
    }
    private static Theatre6PvpState Clone(Theatre6PvpState value) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Theatre6PvpState>(value.ToBson());
    private static void Commit(Session session, Theatre6PvpState original, Theatre6PvpState staged)
    {
        session.player.Theatre6.Pvp = staged;
        try { session.player.Save(); }
        catch { session.player.Theatre6.Pvp = original; throw; }
    }

    [RequestPacketHandler("Theatre6GetPvpPreviewInfoRequest")]
    public static void Preview(Session session,Packet.Request packet){var original=session.player.Theatre6.Pvp;var s=Clone(original);long now=Now();int code=Gate(session.player);if(code==0)code=ReconcileSeason(session,s,now);if(code!=0){session.SendResponse(new Theatre6GetPvpPreviewInfoResponse{Code=code},packet.Id);return;}ReconcileActionPoint(s,now);s.RankRecords[s.AuthorizedSeasonId]=new(){RankId=s.RankId,Score=s.Score};Commit(session,original,s);session.SendPush(ApPush(s));session.SendResponse(new Theatre6GetPvpPreviewInfoResponse{PvpRankRecords=Ranks(s),ActionPoint=s.ActionPoint,LastActionPointRecoverTime=s.ActionPointTime},packet.Id);}

    [RequestPacketHandler("Theatre6PvpStartRequest")]
    public static void Start(Session session,Packet.Request packet){var original=session.player.Theatre6.Pvp;var s=Clone(original);long now=Now();int c=Gate(session.player);if(c==0)c=ReconcileSeason(session,s,now);if(c!=0){session.SendResponse(new Theatre6PvpStartResponse{Code=c},packet.Id);return;}ReconcileActionPoint(s,now);if(s.Matches.Count==0&&!BuildMatches(session.player,s,now)){session.SendResponse(new Theatre6PvpStartResponse{Code=DataError},packet.Id);return;}Commit(session,original,s);session.SendResponse(new Theatre6PvpStartResponse{ActivityData=Activity(s)},packet.Id);}

    [RequestPacketHandler("Theatre6PvpUpdateDefenseRequest")]
    public static void Defense(Session session, Packet.Request packet)
    {
        Theatre6PvpUpdateDefenseRequest? request = packet.Deserialize<Theatre6PvpUpdateDefenseRequest>();
        if (request is null)
        {
            session.SendResponse(new Theatre6PvpUpdateDefenseResponse { Code = DataError }, packet.Id);
            return;
        }

        Theatre6PvpState original = session.player.Theatre6.Pvp;
        Theatre6PvpState state = Clone(original);
        long now = Now();
        int code = Gate(session.player);
        if (code == 0)
            code = ReconcileSeason(session, state, now);
        if (code == 0 && state.Battle is { Finished: false })
            code = AlreadyInBattle;

        List<Theatre6FileSlot?> slots = request.Slots ?? [];
        if (code == 0 && slots.Count != 3)
            code = DefenseCount;
        if (code == 0 && slots.Any(slot => slot is null))
            code = FileInvalid;
        if (code == 0
            && slots.Select(slot => (slot!.CharacterId, slot.SlotId)).Distinct().Count()
                > Cfg("MaxSlotDefenseLineupLimit"))
            code = DefenseMax;
        if (code == 0
            && slots.GroupBy(slot => (slot!.CharacterId, slot.SlotId))
                .Any(group => group.Count() > Cfg("LineupSlotRepeatLimit")))
            code = RepeatLimit;

        List<Theatre6FileState>? files = code == 0 ? ResolveFiles(session.player, slots) : null;
        if (code == 0 && files is null)
            code = FileNotFound;
        if (code == 0)
            code = BuffCode(state, request.BuffId ?? 0, false);
        if (code != 0)
        {
            session.SendResponse(new Theatre6PvpUpdateDefenseResponse { Code = code }, packet.Id);
            return;
        }

        state.DefenseFiles = files!;
        state.DefenseBuffId = request.BuffId ?? 0;
        state.DefenseUpdateTime = now;
        Commit(session, original, state);
        session.SendPush(new NotifyTheatre6DefenseUpdate
        {
            DefenseBuffId = state.DefenseBuffId,
            Lineups = state.DefenseFiles.Select(Theatre6Module.ToWire).ToList()
        });
        session.SendResponse(new Theatre6PvpUpdateDefenseResponse(), packet.Id);
    }

    [RequestPacketHandler("Theatre6PvpRefreshMatchRequest")]
    public static void Refresh(Session session,Packet.Request packet){var original=session.player.Theatre6.Pvp;var s=Clone(original);long now=Now();int c=Gate(session.player);if(c==0)c=ReconcileSeason(session,s,now);if(c==0&&s.Battle is {Finished:false})c=AlreadyInBattle;if(c==0&&now-s.LastRefreshTime<Cfg("RefreshMatchCd"))c=RefreshCd;if(c==0&&now-s.RefreshPeriodStart>=Cfg("RefreshMatchPeriodSeconds")){s.RefreshPeriodStart=now;s.RefreshPeriodCount=0;}if(c==0&&s.RefreshPeriodCount>=Cfg("RefreshMatchMaxCountPerPeriod"))c=RefreshLimit;if(c!=0){session.SendResponse(new Theatre6PvpRefreshMatchResponse{Code=c},packet.Id);return;}if(!BuildMatches(session.player,s,now)){session.SendResponse(new Theatre6PvpRefreshMatchResponse{Code=DataError},packet.Id);return;}s.RefreshPeriodCount++;Commit(session,original,s);session.SendResponse(new Theatre6PvpRefreshMatchResponse{MatchResult=new(){Enemies=Enemies(s)},LastRefreshMatchTime=s.LastRefreshTime,RefreshRemainSeconds=Cfg("RefreshMatchCd")},packet.Id);}

    [RequestPacketHandler("Theatre6PvpGetActionPointRequest")]
    public static void GetAp(Session session,Packet.Request packet){var original=session.player.Theatre6.Pvp;var s=Clone(original);long now=Now();int c=Gate(session.player);if(c==0)c=ReconcileSeason(session,s,now);if(c!=0){session.SendResponse(new Theatre6PvpGetActionPointResponse{Code=c},packet.Id);return;}if(ReconcileActionPoint(s,now)||s.InitializedSeasonId!=original.InitializedSeasonId)Commit(session,original,s);session.SendResponse(new Theatre6PvpGetActionPointResponse{ActionPoint=s.ActionPoint,LastActionPointRecoverTime=s.ActionPointTime},packet.Id);}

    [RequestPacketHandler("Theatre6PvpStartFightRequest")]
    public static void StartFight(Session session,Packet.Request packet){int c=Gate(session.player);if(c==0)c=NotOpen;session.SendResponse(new Theatre6PvpStartFightResponse{Code=c},packet.Id);}

    [RequestPacketHandler("Theatre6PvpRestartFightRequest")]
    public static void Restart(Session session,Packet.Request packet){var s=session.player.Theatre6.Pvp;int c=Gate(session.player);if(c==0&&s.Battle==null)c=NotInBattle;if(c==0&&s.Battle!.Finished)c=BattleFinished;if(c==0)c=NotOpen;session.SendResponse(new Theatre6PvpRestartFightResponse{Code=c},packet.Id);}

    [RequestPacketHandler("Theatre6PvpQueryRankRequest")]
    public static void QueryRank(Session session, Packet.Request packet)
    {
        Theatre6PvpState original = session.player.Theatre6.Pvp;
        Theatre6PvpState state = Clone(original);
        int code = Gate(session.player);
        if (code == 0)
            code = ReconcileSeason(session, state, Now());
        if (code != 0)
        {
            session.SendResponse(new Theatre6PvpQueryRankResponse { Code = code }, packet.Id);
            return;
        }
        if (state.InitializedSeasonId != original.InitializedSeasonId)
            Commit(session, original, state);

        int seasonId = state.AuthorizedSeasonId;
        FilterDefinition<Player> participants = Builders<Player>.Filter.And(
            Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.AuthorizedSeasonId, seasonId),
            Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.InitializedSeasonId, seasonId));
        long totalCount = Player.collection.CountDocuments(participants);
        long playerId = session.player.PlayerData.Id;
        bool isParticipant = Player.collection.CountDocuments(Builders<Player>.Filter.And(
            participants,
            Builders<Player>.Filter.Eq(player => player.PlayerData.Id, playerId))) > 0;
        long betterCount = isParticipant
            ? Player.collection.CountDocuments(Builders<Player>.Filter.And(
                participants,
                Builders<Player>.Filter.Or(
                    Builders<Player>.Filter.Gt(player => player.Theatre6.Pvp.Score, state.Score),
                    Builders<Player>.Filter.And(
                        Builders<Player>.Filter.Eq(player => player.Theatre6.Pvp.Score, state.Score),
                        Builders<Player>.Filter.Lt(player => player.PlayerData.Id, playerId)))))
            : 0;
        List<Player> leaders = Player.collection.Find(participants)
            .SortByDescending(player => player.Theatre6.Pvp.Score)
            .ThenBy(player => player.PlayerData.Id)
            .Limit(LeaderboardPageSize)
            .ToList();
        session.SendResponse(new Theatre6PvpQueryRankResponse
        {
            RankPlayerInfos = leaders.Select(player => new Theatre6RankPlayer
            {
                Id = player.PlayerData.Id,
                Name = player.PlayerData.Name,
                HeadPortraitId = player.PlayerData.CurrHeadPortraitId,
                HeadFrameId = player.PlayerData.CurrHeadFrameId,
                Score = player.Theatre6.Pvp.Score
            }).ToList(),
            TotalCount = ToProtocolCount(totalCount),
            SelfRank = isParticipant ? ToProtocolCount(betterCount + 1) : -1
        }, packet.Id);
    }

    private static int ToProtocolCount(long count) =>
        count >= int.MaxValue ? int.MaxValue : checked((int)Math.Max(0, count));

    [RequestPacketHandler("Theatre6PvpGetBattleRecordsRequest")]
    public static void Records(Session session,Packet.Request packet){var original=session.player.Theatre6.Pvp;var s=Clone(original);int c=Gate(session.player);if(c==0)c=ReconcileSeason(session,s,Now());if(c==0&&s.InitializedSeasonId!=original.InitializedSeasonId)Commit(session,original,s);session.SendResponse(new Theatre6PvpGetBattleRecordsResponse{Code=c,BattleRecords=c==0?s.BattleRecords.Take(Cfg("MaxBattleRecordCount")).Select(Record).ToList():[]},packet.Id);}

    [RequestPacketHandler("Theatre6PvpGiveUpFightRequest")]
    public static void GiveUp(Session session,Packet.Request packet){var s=session.player.Theatre6.Pvp;int c=Gate(session.player);if(c==0&&s.Battle==null)c=NotInBattle;if(c==0&&s.Battle!.Finished)c=BattleFinished;if(c==0)c=NotOpen;session.SendResponse(new Theatre6PvpGiveUpFightResponse{Code=c},packet.Id);}

    private static Dictionary<int,Theatre6RankRecord> Ranks(Theatre6PvpState s)=>s.RankRecords.ToDictionary(x=>x.Key,x=>new Theatre6RankRecord{RankId=x.Value.RankId,Score=x.Value.Score});
    private static NotifyTheatre6PvpGetActionPoint ApPush(Theatre6PvpState s)=>new(){ActionPoint=s.ActionPoint,LastActionPointRecoverTime=s.ActionPointTime};
    private static List<Theatre6FileState>? ResolveFiles(Player player, IEnumerable<Theatre6FileSlot?> slots)
    {
        List<Theatre6FileState> resolved = new();
        foreach (Theatre6FileSlot? slot in slots)
        {
            if (slot is null)
                return null;
            Theatre6FileState? file = player.Theatre6.Files.FirstOrDefault(
                candidate => candidate.CharacterId == slot.CharacterId && candidate.SlotId == slot.SlotId);
            if (file is null)
                return null;
            resolved.Add(Theatre6Module.Clone(file));
        }
        return resolved;
    }
    private static int BuffCode(Theatre6PvpState s,int id,bool attack){var rank=Rank(s);if(rank==null)return RankNotFound;if(rank.PvpBuffGroupId<=0)return id==0?0:BuffNotInGroup;var group=TableReaderV2.Parse<Theatre6PvpBuffGroupTable>().FirstOrDefault(x=>x.Id==rank.PvpBuffGroupId);if(group==null)return BuffNotInGroup;if(id==0)return BuffMustSelect;return (attack?group.AttBuffs:group.DefBuffs).Contains(id)&&TableReaderV2.Parse<Theatre6PvpBuffTable>().Any(x=>x.Id==id)?0:BuffNotInGroup;}
    private static Theatre6PlayerBattleDb RobotDb(int id){var r=TableReaderV2.Parse<Theatre6PvpRobotTable>().Single(x=>x.Id==id);var rank=RankForScore(r.Score);return new(){RobotId=r.Id,Name=r.Name,HeadPortraitId=r.HeadIcon,RankId=rank?.Id??0,Score=r.Score,DefenseBuffId=r.BuffId??0};}
    private static bool BuildMatches(Player p,Theatre6PvpState s,long now)
    {
        Theatre6PvpRankTable? rank=Rank(s);
        if(rank==null)return false;
        int expansions=Math.Max(1,Cfg("MatchExpandMaxCount"));
        long lower=(long)s.Score-rank.SearchDown*expansions;
        long upper=(long)s.Score+rank.SearchUp*expansions;
        List<Theatre6PvpRobotTable> eligible=TableReaderV2.Parse<Theatre6PvpRobotTable>()
            .Where(x=>x.Score>=lower&&x.Score<=upper)
            .OrderBy(x=>x.Id).ToList();
        if(eligible.Count==0)return false;
        int count=Math.Min(3,eligible.Count);
        int first=Math.Abs(s.NextMatchUid)%eligible.Count;
        s.Matches=Enumerable.Range(0,count).Select(i=>new Theatre6MatchState{Uid=s.NextMatchUid++,RobotId=eligible[(first+i)%eligible.Count].Id,MistNum=rank.MistNum??0}).ToList();
        s.LastRefreshTime=now;if(s.RefreshPeriodStart==0)s.RefreshPeriodStart=now;
        return true;
    }
    private static List<Theatre6MatchEnemy> Enemies(Theatre6PvpState s)=>s.Matches.Select(x=>new Theatre6MatchEnemy{Uid=x.Uid,BattleData=RobotDb(x.RobotId),MistNum=x.MistNum}).ToList();
    private static Theatre6TinyBattleState? Battle(Theatre6PvpState s)=>s.Battle is null?null:new(){EnemyId=s.Battle.EnemyUid,EnemyData=RobotDb(s.Battle.EnemyRobotId),MyLineups=s.Battle.MyFiles.Select(Theatre6Module.ToWire).ToList(),RoundResults=s.Battle.RoundResults.ToList(),CurrentRound=s.Battle.CurrentRound,IsFinished=s.Battle.Finished};
    private static Theatre6BattleStats Stats(Theatre6PvpState s)=>new(){NormalBattleCounts=s.Stats.Normal.ToDictionary(),NormalBattleWinCounts=s.Stats.NormalWins.ToDictionary(),AdvanceBattleCounts=s.Stats.Advance.ToDictionary(),AdvanceBattleWinCounts=s.Stats.AdvanceWins.ToDictionary()};
    private static Theatre6PvpActivityData Activity(Theatre6PvpState s)=>new(){ActivityId=s.AuthorizedSeasonId,RankId=s.RankId,Score=s.Score,PlayerState=s.PlayerState,TinyBattleState=Battle(s),Enemies=Enemies(s),Lineups=s.DefenseFiles.Select(Theatre6Module.ToWire).ToList(),RewardedRanks=s.RewardedRanks.ToList(),LastRefreshMatchTime=s.LastRefreshTime,RefreshRemainSeconds=Math.Max(0,Cfg("RefreshMatchCd")-(int)Math.Min(int.MaxValue,Math.Max(0,Now()-s.LastRefreshTime))),AttackBuffId=s.Battle is {Finished:false}?s.Battle.BuffId:0,DefenseBuffId=s.DefenseBuffId,PvpRankRecords=Ranks(s),BattleStats=Stats(s),ActionPoint=s.ActionPoint,LastActionPointRecoverTime=s.ActionPointTime};
    private static Theatre6PvpFightResult Result(Theatre6FightResultState r)=>new(){RoundResults=r.RoundResults.ToList(),IsFinalWin=false,RankId=r.RankId,OldScore=r.OldScore,NewScore=r.NewScore,BattlePhase=r.Phase};
    private static Theatre6BattleRecord Record(Theatre6BattleRecordState r)=>new(){BattleId=r.BattleId,BattleTime=r.BattleTime,IsAttacker=true,IsWin=r.IsWin,IsAllWin=r.IsAllWin,ScoreChange=r.ScoreChange,EnemyInfo=RobotDb(r.RobotId),MyRankId=r.MyRankId,RecordStatus=r.Status};
}
