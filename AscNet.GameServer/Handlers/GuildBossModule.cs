using AscNet.Table.V2.client.mail;
using AscNet.Table.V2.client.text;
using AscNet.Common;
using AscNet.GameServer.Game;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guild.boss;
using AscNet.Table.V2.share.reward;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildBossModule
{
    internal static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    internal static int ConfigInt(string key) => int.Parse(Rows<ConfigTable>().Single(row => row.Key == key).Value, CultureInfo.InvariantCulture);
    internal static double ConfigDouble(string key) => double.Parse(Rows<ConfigTable>().Single(row => row.Key == key).Value, CultureInfo.InvariantCulture);
    private static void Require(bool condition, int code) { if (!condition) throw new ServerCodeException("Guild Boss request rejected", code); }
    internal static long NextEndTime(DateTimeOffset now) => GuildModule.NextWeeklyReset(now).ToUnixTimeSeconds();
    // Explicit local version policy: missing retail Time rows do not invent a calendar.
    // The shipped reward transition timestamp selects new rewards; installed V3 is enabled.
    private static bool NewRewardsActive(DateTimeOffset now) => now.ToUnixTimeSeconds() >= ConfigInt("GuildBossNewRewardDate");
    private static bool IsTemporarilyBanned(DateTimeOffset now) =>
        ActivityScheduleService.TryGet(ConfigInt("GuildBossTempBanTimeId"), out ActivityScheduleEntry entry) && entry.IsOpen(now);
    internal static IEnumerable<TimeLimitCtrlConfigList> BuildTimeLimits(DateTimeOffset now)
    {
        long start = ConfigInt("GuildBossNewRewardDate");
        foreach (int id in Rows<GuildBossScoreRewardTable>().Select(row => row.TimeId)
            .Concat(Rows<GuildBossHpRewardTable>().Select(row => row.TimeId)).Distinct())
            yield return new() { Id = id, StartTime = start, EndTime = 0 };
        yield return new() { Id = ConfigInt("GuildBossThirdVersionTimeId"), StartTime = 0, EndTime = 0 };
    }
    private static long Uid(GuildMutation mutation) => mutation.ActorSession.player.PlayerData.Id;
    private static long Score(GuildBossParticipantState participant) => checked(participant.Stages.Sum(stage => stage.Score) + participant.BonusScore);
    private static long Score(GuildBossState state) => checked((state.Participants.Sum(Score) + state.BonusScore)
        * (100L + (Rows<GuildBossLevelTable>().SingleOrDefault(row => row.Level == state.BossLevel)?.AdditionPercent ?? 0)) / 100);

    internal static void PrepareLogin(Session session)
    {
        Guild? guild = GuildModule.FindMembership(session.player.PlayerData.Id);
        if (guild is null || GuildModule.Rank(guild, session.player.PlayerData.Id) == 5) return;
        GuildModule.Commit(session, guild, EnsurePeriod, notify: false);
    }

    internal static void EnsurePeriod(GuildMutation mutation)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long period = GuildModule.WeeklyPeriod(now);
        GuildBossState boss = mutation.Guild.Boss;
        if (boss.Period != period || boss.Stages.Count == 0)
        {
            if (boss.Stages.Count > 0)
            {
                GuildBossArchiveState archive = new()
                {
                    Period = boss.Period, BossLevel = boss.BossLevel, HpMax = boss.HpMax, HpLeft = boss.HpLeft,
                    Score = Score(boss), RankEnteredAt = boss.RankEnteredAt, Participants = boss.Participants,
                    EndTime = checked(NextEndTime(now) - (period - boss.Period) * 604800)
                };
                archive.RankRewardId = ArchiveRankReward(mutation.Guild, archive);
                if (archive.RankRewardId > 0)
                    archive.RankMailId = Rows<GuildBossRankRewardTable>().Single(row => row.RewardId == archive.RankRewardId).MailId;
                boss.Archives.Add(archive);
            }
            int levelId = boss.BossLevelNext > 0 ? boss.BossLevelNext : Rows<GuildBossLevelTable>().Min(row => row.Level);
            GuildBossLevelTable level = Rows<GuildBossLevelTable>().Single(row => row.Level == levelId);
            boss.Period = period;
            boss.BossLevel = boss.BossLevelNext = levelId;
            boss.HpMax = boss.HpLeft = level.BossHp;
            boss.Participants = [];
            boss.Logs = [];
            boss.Stages = [];
            boss.Robots = [];
            boss.OrderList = [];
            boss.RankEnteredAt = 0;
            boss.Charged = false;
            boss.BonusScore = 0;
            // Local weekly policy: rotate authored weekly slot groups, not captured stage/robot selections.
            foreach (GuildBossDataTable row in Rows<GuildBossDataTable>().OrderBy(row => row.StageType))
            {
                int slot = (int)((period % row.StageId.Count + row.StageId.Count) % row.StageId.Count);
                int[] stages = row.StageId[slot].Split('|').Select(int.Parse).ToArray();
                Require(stages.Length >= row.StageCount, 20063308);
                for (int index = 0; index < row.StageCount; index++)
                {
                    boss.Stages.Add(new()
                    {
                        StageId = stages[index], Type = row.StageType,
                        EffectId = index < row.StageEffect.Count ? row.StageEffect[index] : 0,
                        EffectCount = index < row.EffectCount.Count ? row.EffectCount[index] : 0
                    });
                    if (row.StageType != 3) boss.OrderList.Add(index + 1);
                }
            }
        }
        ReconcileRobots(boss);
        GuildBossPlayerState player = mutation.Player(Uid(mutation)).Boss;
        if (player.GuildId != mutation.Guild.Id || player.Period != period)
        {
            player.GuildId = mutation.Guild.Id;
            player.Period = period;
            player.Attempt = null;
        }
        SettleArchives(mutation);
    }

    private static void ReconcileRobots(GuildBossState boss)
    {
        foreach (GuildBossDataTable row in Rows<GuildBossDataTable>().OrderBy(row => row.StageType))
        {
            List<int> groups = row.FixedRobot.ToList();
            // RobotCount is the rotating group count; fixed groups are additional and cannot be drawn twice.
            int offset = (int)((boss.Period % row.Robot.Count + row.Robot.Count) % row.Robot.Count);
            for (int index = 0; groups.Count < row.RobotCount + row.FixedRobot.Count && index < row.Robot.Count; index++)
            {
                int group = row.Robot[(offset + index) % row.Robot.Count];
                if (!groups.Contains(group)) groups.Add(group);
            }
            GuildBossRobot? roster = boss.Robots.SingleOrDefault(robot => robot.Type == row.StageType);
            List<int> selected = [];
            foreach (int group in groups)
            {
                List<int> variants = Rows<GuildBossStageRobotTable>().Single(robot => robot.Id == group).RobotId;
                List<int> existing = variants.Where(id => roster?.RobotIds.Contains(id) == true).ToList();
                // Local weekly policy: one authored variant per group, not the entire candidate pool.
                // Preserve already-selected variants; repair old flattened catalogs without changing frozen attempts.
                int variant = (int)((boss.Period % variants.Count + variants.Count) % variants.Count);
                selected.Add(existing.Count == 1 ? existing[0] : variants[variant]);
            }
            if (roster is null) boss.Robots.Add(new() { Type = row.StageType, RobotIds = selected });
            else if (!roster.RobotIds.SequenceEqual(selected)) roster.RobotIds = selected;
        }
    }

    internal static GuildBossParticipantState Participant(GuildMutation mutation)
    {
        GuildBossParticipantState? participant = mutation.Guild.Boss.Participants.SingleOrDefault(row => row.PlayerId == Uid(mutation));
        if (participant is null) mutation.Guild.Boss.Participants.Add(participant = new() { PlayerId = Uid(mutation) });
        return participant;
    }

    internal static GuildBossStageState Stage(GuildMutation mutation, int stageId) =>
        mutation.Guild.Boss.Stages.SingleOrDefault(row => row.StageId == stageId)
        ?? throw new ServerCodeException("Guild Boss stage is not selected this week", 20063273);

    private static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<GuildMutation, TRequest, TResponse> action, int membershipCode)
        where TRequest : new() where TResponse : GuildResponse, new() =>
        GuildModule.Handle<TRequest, TResponse>(session, packet, (mutation, request, response) =>
        {
            EnsurePeriod(mutation);
            Require(!IsTemporarilyBanned(DateTimeOffset.UtcNow), 20063364);
            action(mutation, request, response);
        }, membershipCode: membershipCode);

    [RequestPacketHandler("GuildBossInfoRequest")]
    public static void Info(Session session, Packet.Request packet) => Handle<GuildBossInfoRequest, GuildBossInfoResponse>(session, packet, (m, q, r) =>
    {
        GuildBossState b = m.Guild.Boss;
        List<GuildBossPlayerRank> ranks = PlayerRanks(m.Guild);
        r.ActivityId = b.Period; r.HpMax = b.HpMax; r.HpLeft = b.HpLeft; r.EndTime = NextEndTime(DateTimeOffset.UtcNow); r.TotalScore = Score(b);
        r.MyRank = ranks.FirstOrDefault(rank => rank.Id == Uid(m)) ?? PlayerRank(m.Guild, Participant(m));
        r.MyRankNum = ranks.FindIndex(rank => rank.Id == Uid(m)) + 1;
        r.Logs = Logs(m.Guild, q.LogId);
    }, 20063264);

    [RequestPacketHandler("GuildBossActivityRequest")]
    public static void Activity(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildBossActivityResponse>(session, packet, (m, _, r) =>
    {
        GuildBossState b = m.Guild.Boss;
        GuildBossParticipantState p = Participant(m);
        r.ActivityId = b.Period; r.GuildScoreSumBest = b.GuildScoreSumBest; r.HpMax = b.HpMax; r.HpLeft = b.HpLeft; r.GuildScoreSum = Score(b);
        r.BossLevel = b.BossLevel; r.BossLevelNext = b.BossLevelNext; r.ScoreBoxGot = p.ScoreBoxGot; r.HpBoxGotNew = p.HpBoxGot;
        r.RobotList = b.Robots; r.PlayerFinalScore = Score(p); r.OrderList = b.OrderList;
        r.BossList = b.Stages.Select(stage =>
        {
            GuildBossPlayerStageState? own = p.Stages.SingleOrDefault(row => row.StageId == stage.StageId);
            return new GuildBossStageInfo
            {
                StageId = stage.StageId, Type = stage.Type, Score = own?.Score ?? 0, UploadCount = own?.UploadCount ?? 0,
                EffectId = stage.EffectId, CurEffectCount = stage.CurEffectCount, TotalEffectCount = stage.EffectCount,
                CardIds = own?.CardIds ?? [], CharacterHeadInfoList = own?.CharacterHeadInfoList ?? []
            };
        }).ToList();
    }, 20063267);

    [RequestPacketHandler("GuildBossStageRequest")]
    public static void StageInfo(Session session, Packet.Request packet) => Handle<GuildBossStageRequest, GuildBossStageResponse>(session, packet, (m, q, r) =>
    {
        GuildBossStageState stage = Stage(m, q.StageId);
        r.BuffLeft = Math.Max(0, stage.EffectCount - stage.CurEffectCount); r.CurEffectCount = stage.CurEffectCount; r.TotalEffectCount = stage.EffectCount;
        r.Logs = Logs(m.Guild, q.LogId);
        r.TopPlayers = PlayerRanks(m.Guild, stage.StageId).Take(3).Select(rank => new GuildBossStageTopPlayer
        { Id = rank.Id, PlayerName = rank.Name, HeadPortraitId = rank.HeadPortraitId, HeadFrameId = rank.HeadFrameId, Score = rank.Score }).ToList();
    }, 20063272);

    [RequestPacketHandler("GuildBossPlayerRankRequest")]
    public static void MembersRank(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildBossPlayerRankResponse>(session, packet,
        (m, _, r) => r.RankList = PlayerRanks(m.Guild).Take(ConfigInt("GuildBossRankListCount")).ToList(), 20063276);

    [RequestPacketHandler("GuildBossPlayerStageRankRequest")]
    public static void StageRank(Session session, Packet.Request packet) => Handle<GuildBossPlayerStageRankRequest, GuildBossPlayerStageRankResponse>(session, packet,
        (m, q, r) => r.RankList = PlayerRanks(m.Guild, Stage(m, q.StageId).StageId).Take(ConfigInt("GuildBossRankListCount")).ToList(), 20063278);

    [RequestPacketHandler("GuildBossGuildRankRequest")]
    public static void GuildRank(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildBossGuildRankResponse>(session, packet, (m, _, r) =>
    {
        List<Guild> guilds = Guild.AllActive();
        guilds.RemoveAll(guild => guild.Id == m.Guild.Id); guilds.Add(m.Guild);
        List<Guild> ranked = guilds.Where(guild => guild.Boss.Period == m.Guild.Boss.Period && Score(guild.Boss) > 0)
            .OrderByDescending(guild => Score(guild.Boss)).ThenBy(guild => guild.Boss.RankEnteredAt).ThenBy(guild => guild.Id).ToList();
        r.RankList = ranked.Take(ConfigInt("GuildBossRankListCount")).Select(GuildRankData).ToList();
        r.MyRank = GuildRankData(m.Guild);
        int place = ranked.FindIndex(guild => guild.Id == m.Guild.Id);
        r.MyRankNum = place < 0 ? 0 : place < ConfigInt("GuildBossRankListCount") ? place + 1 : (place + 1d) / ranked.Count;
    }, 20063281);

    private static GuildBossGuildRank GuildRankData(Guild guild) => new() { Id = guild.Id, Name = guild.Name, IconId = guild.IconId, Score = Score(guild.Boss) };
    private static List<GuildBossLog> Logs(Guild guild, long after) => guild.Boss.Logs.Where(log => log.LogId > after && guild.MemberIds.Contains(log.PlayerId)).ToList();
    private static GuildBossPlayerRank PlayerRank(Guild guild, GuildBossParticipantState participant, int stageId = 0)
    {
        Player? player = Server.Instance.SessionFromUID(participant.PlayerId)?.player ?? Player.TryFromPlayerId(participant.PlayerId);
        GuildBossPlayerStageState? stage = participant.Stages.SingleOrDefault(row => row.StageId == stageId);
        return new()
        {
            Id = participant.PlayerId, Name = player?.PlayerData.Name ?? string.Empty,
            Score = stageId == 0 ? Score(participant) : stage?.Score ?? 0, RankLevel = GuildModule.Rank(guild, participant.PlayerId),
            HeadPortraitId = (int)(player?.PlayerData.CurrHeadPortraitId ?? 0), HeadFrameId = (int)(player?.PlayerData.CurrHeadFrameId ?? 0),
            CardIds = stage?.CardIds ?? [], CharacterHeadInfoList = stage?.CharacterHeadInfoList ?? []
        };
    }
    private static List<GuildBossPlayerRank> PlayerRanks(Guild guild, int stageId = 0) => guild.Boss.Participants
        .Where(p => guild.MemberIds.Contains(p.PlayerId) && GuildModule.Rank(guild, p.PlayerId) != 5
            && (stageId == 0 ? Score(p) > 0 : p.Stages.Any(stage => stage.StageId == stageId && stage.Score > 0)))
        .OrderByDescending(p => stageId == 0 ? Score(p) : p.Stages.Single(stage => stage.StageId == stageId).Score)
        .ThenBy(p => stageId == 0 ? p.RankEnteredAt : p.Stages.Single(stage => stage.StageId == stageId).RankEnteredAt)
        .ThenBy(p => p.PlayerId).Select(p => PlayerRank(guild, p, stageId)).ToList();

    [RequestPacketHandler("GuildBossLevelRequest")]
    public static void Level(Session session, Packet.Request packet) => Handle<GuildBossLevelRequest, GuildBossLevelResponse>(session, packet, (m, q, _) =>
    {
        Require(q.ActivityId == m.Guild.Boss.Period, 20063313);
        GuildModule.RequirePermission(m.Guild, Uid(m), GuildPermission.ManageBoss);
        GuildBossLevelTable level = Rows<GuildBossLevelTable>().SingleOrDefault(row => row.Level == q.BossLevelNext)
            ?? throw new ServerCodeException("Unknown Guild Boss difficulty", 20063315);
        Require(m.Guild.Boss.GuildScoreSumBest >= (level.UnlockScore ?? 0), 20063316);
        m.Guild.Boss.BossLevelNext = level.Level;
    }, 20063312);

    [RequestPacketHandler("GuildBossSetOrderRequest")]
    public static void Order(Session session, Packet.Request packet) => Handle<GuildBossSetOrderRequest, GuildBossSetOrderResponse>(session, packet, (m, q, _) =>
    {
        Require(q.ActivityId == m.Guild.Boss.Period, 20063320);
        GuildModule.RequirePermission(m.Guild, Uid(m), GuildPermission.ManageBoss);
        List<GuildBossStageState> stages = m.Guild.Boss.Stages.Where(stage => stage.Type != 3).ToList();
        Require(q.OrderList is not null && q.OrderList.Count == stages.Count, 20063300);
        foreach (int type in stages.Select(stage => stage.Type).Distinct())
        {
            int[] order = stages.Select((stage, index) => (stage, index)).Where(pair => pair.stage.Type == type)
                .Select(pair => q.OrderList![pair.index]).Order().ToArray();
            Require(order.SequenceEqual(Enumerable.Range(1, order.Length)), 20063300);
        }
        m.Guild.Boss.OrderList = q.OrderList!;
    }, 20063319);

    [RequestPacketHandler("GuildFightStyleRequest")]
    public static void StyleInfo(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildFightStyleResponse>(session, packet,
        (m, _, r) => r.FightStyle = m.Player(Uid(m)).Boss.FightStyle, 20063267);

    [RequestPacketHandler("GuildSelectFightStyleRequest")]
    public static void SelectStyle(Session session, Packet.Request packet) => Handle<GuildSelectFightStyleRequest, GuildSelectFightStyleResponse>(session, packet, (m, q, _) =>
    {
        Require(Rows<GuildBossFightStyleTable>().Any(row => row.Id == q.StyleId), 20063328);
        GuildBossFightStyle style = m.Player(Uid(m)).Boss.FightStyle;
        Require(style.StyleId != q.StyleId, 20063329);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Require(style.LastEffectTime == 0 || now >= style.LastEffectTime + ConfigInt("GuildFightStyleCd"), 20063330);
        style.StyleId = q.StyleId; style.LastEffectTime = now; style.EffectedSkillId = [];
    }, 20063267);

    [RequestPacketHandler("GuildSelectFightStyleSkillRequest")]
    public static void SelectSkill(Session session, Packet.Request packet) => Handle<GuildSelectFightStyleSkillRequest, GuildSelectFightStyleSkillResponse>(session, packet, (m, q, _) =>
    {
        GuildBossFightStyle style = m.Player(Uid(m)).Boss.FightStyle;
        GuildBossFightStyleTable rule = Rows<GuildBossFightStyleTable>().SingleOrDefault(row => row.Id == style.StyleId)
            ?? throw new ServerCodeException("No Guild Boss combat style selected", 20063328);
        Require(q.OperType is >= 1 and <= 3, 20063331);
        if (q.OperType == 3) { style.EffectedSkillId.Clear(); return; }
        GuildBossFightStyleSkillTable skill = Rows<GuildBossFightStyleSkillTable>().SingleOrDefault(row => row.Id == q.SkillId)
            ?? throw new ServerCodeException("Unknown Guild Boss combat skill", 20063331);
        Require(skill.Style == style.StyleId, 20063334);
        Require(skill.IsPermanent is not > 0, 20063331);
        // Installed skill authority has no nonzero UnlockLv; all listed selectable skills are unlocked.
        if (q.OperType == 2) { Require(style.EffectedSkillId.Remove(skill.Id), 20063331); return; }
        Require(!style.EffectedSkillId.Contains(skill.Id), 20063332);
        Require(style.EffectedSkillId.Count < rule.MaxCount, 20063335);
        Require(skill.IsCore is not > 0 || !Rows<GuildBossFightStyleSkillTable>().Any(row => style.EffectedSkillId.Contains(row.Id) && row.IsCore > 0), 20063336);
        style.EffectedSkillId.Add(skill.Id);
    }, 20063267);

    [RequestPacketHandler("GuildBossUploadRequest")]
    public static void Upload(Session session, Packet.Request packet) => Handle<GuildBossUploadRequest, GuildBossUploadResponse>(session, packet, (m, q, r) =>
    {
        GuildBossState b = m.Guild.Boss;
        GuildBossAttemptState attempt = m.Player(Uid(m)).Boss.Attempt ?? throw new ServerCodeException("No settled Guild Boss candidate", 20063304);
        Require(attempt.GuildId == m.Guild.Id && attempt.Period == b.Period, 20063307);
        Require(attempt.StageId == q.StageId, 20063303);
        Require(attempt.Settled && attempt.Won, 20063304);
        Require(!attempt.Uploaded, 20063309);
        GuildBossStageState stage = Stage(m, q.StageId);
        GuildBossParticipantState p = Participant(m);
        Require(!p.Stages.Any(own => own.StageId != q.StageId && Stage(m, own.StageId).Type == stage.Type), 20063309);
        GuildBossPlayerStageState? own = p.Stages.SingleOrDefault(row => row.StageId == q.StageId);
        Require((own?.UploadCount ?? 0) < ConfigInt("GuildBossStageUploadCount"), 20063305);
        Require(attempt.Result.TotalScore > (own?.Score ?? 0), 20063311);
        bool first = own is null;
        if (own is null) p.Stages.Add(own = new() { StageId = q.StageId, RankEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        long delta = checked(attempt.Result.TotalScore - own.Score);
        long oldHp = b.HpLeft;
        b.HpLeft = Math.Max(0, b.HpLeft - delta);
        long effectHp = 0;
        int effectValue = 0;
        if (first && stage.EffectId != 0 && stage.CurEffectCount < stage.EffectCount)
        {
            stage.CurEffectCount++;
            long before = b.HpLeft;
            ApplyEffect(b, stage.EffectId, out effectValue);
            effectHp = before - b.HpLeft;
        }
        own.Score = attempt.Result.TotalScore; own.UploadCount++; own.CardIds = attempt.CardIds.Zip(attempt.RobotIds, (card, robot) => robot > 0 ? robot : card).ToList();
        own.CharacterHeadInfoList = attempt.CharacterHeadInfoList;
        if (first) m.AddTaskConditionProgress(Uid(m), 35010, 1);
        if (p.RankEnteredAt == 0) p.RankEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (b.RankEnteredAt == 0) b.RankEnteredAt = p.RankEnteredAt;
        if (b.HpLeft == 0)
            foreach (GuildBossParticipantState participant in b.Participants.Where(participant => participant.Stages.Count > 0 && !participant.DeathBonus))
            { participant.BonusScore = checked(participant.BonusScore + ConfigInt("GuildBossDeathAddScore")); participant.DeathBonus = true; }
        b.GuildScoreSumBest = Math.Max(b.GuildScoreSumBest, Score(b));
        int contribute = checked((int)Math.Floor(delta * ConfigDouble("GuildBossScoreContributeRatio")));
        if (contribute > 0) GuildModule.AddEconomyGoods(m, Uid(m), 38, contribute);
        attempt.Uploaded = true; attempt.SubHp = r.SubHp = oldHp - b.HpLeft; attempt.Contribute = r.Contribute = contribute;
        b.Logs.Add(new()
        {
            LogId = b.Logs.Count == 0 ? 1 : checked(b.Logs[^1].LogId + 1), PlayerId = Uid(m), PlayerName = session.player.PlayerData.Name,
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), StageId = q.StageId, SubHp = r.SubHp,
            CurEffectCount = stage.CurEffectCount, TotalEffectCount = stage.EffectCount, EffectHp = effectHp, EffectValue = effectValue
        });
        m.Broadcast(new NotifyGuildEvent { Type = 20, Value = checked((uint)b.HpLeft), Value2 = b.BossLevel });
        foreach (GuildBossParticipantState participant in b.Participants.Where(participant => m.Guild.MemberIds.Contains(participant.PlayerId)))
            m.Push(participant.PlayerId, new NotifyGuildEvent { Type = 21 });
    }, 20063302);

    private static void ApplyEffect(GuildBossState boss, int id, out int effectValue)
    {
        // Derived from EN GuildBossStageBuff descriptions; explicit local arithmetic, never capture-specific cases.
        long damage = id switch
        {
            101 => boss.HpLeft * 4 < boss.HpMax ? 100000 : 50000,
            102 => (boss.HpMax - boss.HpLeft) * 12 / 1000,
            103 => boss.HpMax * 8 / 1000,
            104 => boss.HpLeft * 12 / 1000,
            105 => 60000,
            106 or 107 => 0,
            _ => throw new InvalidDataException($"Unknown authored Guild Boss effect {id}")
        };
        effectValue = 0;
        if (id == 106) { boss.Charged = true; return; }
        if (id == 107) { effectValue = 10000; boss.BonusScore = checked(boss.BonusScore + effectValue); return; }
        if (boss.Charged) { damage = damage * 5 / 2; boss.Charged = false; }
        boss.HpLeft = Math.Max(0, boss.HpLeft - damage);
    }

    [RequestPacketHandler("GuildBossScoreBoxRequest")]
    public static void ScoreBox(Session session, Packet.Request packet) => Handle<GuildBossScoreBoxRequest, GuildBossScoreBoxResponse>(session, packet,
        (m, q, r) => r.RewardGoods = ClaimScore(m, q.BoxId), 20063290);
    [RequestPacketHandler("GuildBossHpBoxRequest")]
    public static void HpBox(Session session, Packet.Request packet) => Handle<GuildBossHpBoxRequest, GuildBossHpBoxResponse>(session, packet,
        (m, q, r) => r.RewardGoods = ClaimHp(m, q.BoxId), 20063283);
    [RequestPacketHandler("GuildBossGetAllBossRewardRequest")]
    public static void AllRewards(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildBossGetAllBossRewardResponse>(session, packet, (m, _, r) =>
    {
        GuildBossParticipantState p = Participant(m);
        foreach (GuildBossScoreRewardTable row in Rows<GuildBossScoreRewardTable>().Where(row => !p.ScoreBoxGot.Contains(row.Id) && Score(p) >= row.Score))
        { r.RewardGoods.AddRange(ClaimScore(m, row.Id)); r.BossScoreId.Add(row.Id); }
        foreach (GuildBossHpRewardTable row in Rows<GuildBossHpRewardTable>().Where(row => !p.HpBoxGot.Contains(row.Id) && HpEligible(m, row)))
        { r.RewardGoods.AddRange(ClaimHp(m, row.Id)); r.BossHpId.Add(row.Id); }
        Require(r.BossScoreId.Count + r.BossHpId.Count > 0, 20063286);
    }, 20063283);

    private static List<RewardGoods> ClaimScore(GuildMutation m, int id)
    {
        GuildBossScoreRewardTable rule = Rows<GuildBossScoreRewardTable>().SingleOrDefault(row => row.Id == id)
            ?? throw new ServerCodeException("Unknown Guild Boss score reward", 20063292);
        GuildBossParticipantState p = Participant(m);
        Require(!p.ScoreBoxGot.Contains(id), 20063291); Require(Score(p) >= rule.Score, 20063293);
        p.ScoreBoxGot.Add(id);
        return Grant(m, Uid(m), $"score:{m.Guild.Boss.Period}:{id}", NewRewardsActive(DateTimeOffset.UtcNow) ? rule.NewRewardId : rule.RewardId);
    }
    private static bool HpEligible(GuildMutation m, GuildBossHpRewardTable rule) =>
        m.Guild.Boss.HpLeft * 100 <= m.Guild.Boss.HpMax * (rule.HpPercent ?? 0);
    private static List<RewardGoods> ClaimHp(GuildMutation m, int id)
    {
        GuildBossHpRewardTable rule = Rows<GuildBossHpRewardTable>().SingleOrDefault(row => row.Id == id)
            ?? throw new ServerCodeException("Unknown Guild Boss HP reward", 20063285);
        GuildBossParticipantState p = Participant(m);
        Require(!p.HpBoxGot.Contains(id), 20063284); Require(HpEligible(m, rule), 20063286);
        p.HpBoxGot.Add(id);
        return Grant(m, Uid(m), $"hp:{m.Guild.Boss.Period}:{id}", NewRewardsActive(DateTimeOffset.UtcNow) ? rule.NewRewardId : rule.RewardId);
    }
    private static List<RewardGoods> Grant(GuildMutation m, long uid, string receipt, int rewardId)
    {
        List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
        if (goods.Count == 0) throw new InvalidDataException($"Missing Guild Boss reward {rewardId}");
        m.AddRewardGrant(uid, $"guild-boss:{m.Guild.Id}:{uid}:{receipt}", goods);
        return goods.Select(row => new RewardGoods { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
            RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException("Unsupported Guild Boss reward")) }).ToList();
    }

    private static void SettleArchives(GuildMutation m)
    {
        long uid = Uid(m);
        foreach (GuildBossArchiveState archive in m.Guild.Boss.Archives.Where(archive => !archive.RewardedPlayerIds.Contains(uid)))
        {
            GuildBossParticipantState? participant = archive.Participants.SingleOrDefault(p => p.PlayerId == uid && p.Stages.Count > 0);
            if (participant is null) continue;
            DateTimeOffset rewardTime = DateTimeOffset.FromUnixTimeSeconds(archive.EndTime - 1);
            if (archive.RankRewardId > 0) QueueRankMail(m, archive, uid);
            // Settle unclaimed earned boxes before resetting the weekly UI, with separate durable receipts.
            foreach (GuildBossScoreRewardTable box in Rows<GuildBossScoreRewardTable>().Where(box => !participant.ScoreBoxGot.Contains(box.Id) && Score(participant) >= box.Score))
                Grant(m, uid, $"score:{archive.Period}:{box.Id}", NewRewardsActive(rewardTime) ? box.NewRewardId : box.RewardId);
            foreach (GuildBossHpRewardTable box in Rows<GuildBossHpRewardTable>().Where(box => !participant.HpBoxGot.Contains(box.Id) && archive.HpLeft * 100 <= archive.HpMax * (box.HpPercent ?? 0)))
                Grant(m, uid, $"hp:{archive.Period}:{box.Id}", NewRewardsActive(rewardTime) ? box.NewRewardId : box.RewardId);
            archive.RewardedPlayerIds.Add(uid);
        }
    }

    private static void QueueRankMail(GuildMutation mutation, GuildBossArchiveState archive, long uid)
    {
        MailTable template = Rows<MailTable>().Single(row => row.Id == archive.RankMailId);
        List<RewardGoodsTable> rewards = RewardHandler.GetRewardGoods(archive.RankRewardId);
        if (rewards.Count == 0) throw new InvalidDataException($"Missing Guild Boss rank reward {archive.RankRewardId}");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string claim = $"guild-boss:{mutation.Guild.Id}:{uid}:rank:{archive.Period}";
        PlayerMail mail = new()
        {
            Id = claim, RewardClaimKey = claim, Type = template.Type,
            SendName = mutation.Guild.Name,
            // Mail template text is absent from both dumps; reuse authoritative EN rank-reward UI wording.
            Title = Rows<TextTable>().Single(row => row.Key == "GuildBossRankRewardTitle").Text,
            Content = Rows<TextTable>().Single(row => row.Key == "GuildBossRankRewardDesc").Text,
            CreateTime = now, SendTime = now,
            ExpireTime = template.ValidTime == 0 ? 0 : checked(now + template.ValidTime),
            ReserveTime = template.ReserveTime == 0 ? 0 : checked(now + template.ReserveTime),
            RewardGoodsList = rewards.Select(row => new PlayerMailRewardGoods
            {
                Id = row.Id, TemplateId = checked((uint)row.TemplateId), Count = row.Count,
                RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException("Unsupported Guild Boss rank reward"))
            }).ToList()
        };
        mutation.AddMail(uid, mail);
        mutation.Push(uid, new NotifyMails { NewMailList = [MailModule.ToNotify(mail)] });
    }

    private static int ArchiveRankReward(Guild own, GuildBossArchiveState archive)
    {
        if (archive.Score <= 0) return 0;
        List<(uint Id, long Score, long Entered)> ranks = [];
        foreach (Guild guild in Guild.AllActive().Where(guild => guild.Id != own.Id))
        {
            GuildBossArchiveState? other = guild.Boss.Archives.SingleOrDefault(old => old.Period == archive.Period);
            if (other is not null && other.Score > 0) ranks.Add((guild.Id, other.Score, other.RankEnteredAt));
            else if (guild.Boss.Period == archive.Period && Score(guild.Boss) > 0)
                ranks.Add((guild.Id, Score(guild.Boss), guild.Boss.RankEnteredAt));
        }
        ranks.Add((own.Id, archive.Score, archive.RankEnteredAt));
        ranks = ranks.OrderByDescending(rank => rank.Score).ThenBy(rank => rank.Entered).ThenBy(rank => rank.Id).ToList();
        // Local percentile policy: percent of real ranked guilds strictly ahead; a lone guild is first, not last.
        double percentile = 100d * ranks.FindIndex(rank => rank.Id == own.Id) / ranks.Count;
        return Rows<GuildBossRankRewardTable>().OrderBy(row => row.Percent)
            .FirstOrDefault(row => percentile <= row.Percent)?.RewardId ?? 0;
    }
}
