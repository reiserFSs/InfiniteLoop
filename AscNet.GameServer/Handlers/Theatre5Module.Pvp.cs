using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre5;
using MongoDB.Driver;
using System.Globalization;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static Theatre5RankTable RankForRating(int rating) => Rows<Theatre5RankTable>()
        .Where(row => Convert.ToInt32(row.Rating, CultureInfo.InvariantCulture) <= rating)
        .MaxBy(row => row.Id) ?? throw new InvalidDataException("Theatre5 has no initial rank.");

    [RequestPacketHandler("Theatre5InitGameRequest")]
    public static void Theatre5InitGame(Session session, Packet.Request packet) =>
        Handle<Theatre5InitGameRequest, Theatre5InitGameResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 1);
            Require(m.Data.PvpAdventureData is null, 20280009);
            response.PvpAdventureData = (Theatre5PvpAdventureData)InitializeAdventure(m, 1, request.CharacterId);
        });

    [RequestPacketHandler("XTheatre5QueryRankRequest")]
    public static void Theatre5QueryRank(Session session, Packet.Request packet) =>
        Handle<XTheatre5QueryRankRequest, XTheatre5QueryRankResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 1);
            Require(request.CharacterId == 0 || Rows<Theatre5CharacterTable>()
                .Any(row => row.Id == request.CharacterId && row.Priority > 0), 20280014);
            // ponytail: full persisted-board scan; add indexed score projections when population requires it.
            // Overall board is each real player's best character, ties by actual player ID. Robots never rank.
            var board = Player.collection.Find(player => player.Theatre5.Data.ActivityId == m.Data.ActivityId)
                .ToList().Select(player => RankEntry(player, request.CharacterId)).Where(entry => entry is not null)
                .Select(entry => entry!).OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Id).ToList();
            response.TotalCount = board.Count;
            response.SelfRank = board.FindIndex(entry => entry.Id == session.player.PlayerData.Id) + 1;
            response.RankPlayerInfos = board.Take(100).ToList();
        });

    private static Theatre5RankPlayer? RankEntry(Player player, int characterId)
    {
        var characters = player.Theatre5.Data.Characters.Values.Where(character => characterId == 0 || character.Id == characterId);
        var character = characters.OrderByDescending(row => row.Rating).ThenBy(row => row.Id).FirstOrDefault();
        if (character is null || player.PlayerData.Id <= 0 || player.PlayerData.Id > int.MaxValue) return null;
        return new()
        {
            Id = checked((int)player.PlayerData.Id), Name = player.PlayerData.Name,
            HeadPortraitId = checked((int)player.PlayerData.CurrHeadPortraitId),
            HeadFrameId = checked((int)player.PlayerData.CurrHeadFrameId), Score = character.Rating,
            Theatre5RankCharacterId = character.Id
        };
    }

    [RequestPacketHandler("Theatre5MatchRequest")]
    public static void Theatre5Match(Session session, Packet.Request packet) =>
        Handle<Theatre5MatchRequest, Theatre5MatchResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 1);
            var adventure = (Theatre5PvpAdventureData)Adventure(m);
            if (adventure.Status == 5)
            {
                Require(adventure.EnemyData is not null && m.State.PvpOpponent is not null, 20281012);
            }
            else
            {
                Require(adventure.Status == 4, 20281001);
                Require(adventure.BagData.TempItemDict.Count == 0, 20281006);
                Require(adventure.RandomRelics.Count == 0, 20283006);
                Require(adventure.ChooseMissions.Count == 0 && adventure.Missioning?.MissionState != 2, 20285007);
                Require(!adventure.IsCanFreeUnlockGrid, 20283013);
                Require(!CanLevelUp(m), 20283009);
                PreparePvpOpponent(m);
                adventure.LeaveShopCnt = checked(adventure.LeaveShopCnt + 1);
                adventure.Status = 5;
            }
            response.EnemyData = adventure.EnemyData;
            response.LeaveShopCnt = adventure.LeaveShopCnt;
        });

    internal static void PreparePvpOpponent(Mutation m)
    {
        var adventure = m.Data.PvpAdventureData;
        Require(adventure is not null, 20280007);
        if (m.State.PvpOpponent is not null && adventure.EnemyData is not null) return;
        int rank = RankForRating(m.Data.Characters[adventure.CharacterId].Rating).Id;
        // LOCAL matching ledger: authored cup/defeat row for the rank, one-rank reduction after
        // ReduceRankFailCount consecutive losses; exact desired cup/defeat human builds first, then authored robots.
        // Stable persisted-player ordering bounds the pool; RNG selects once and the entire actor is durable.
        if (m.State.PvpContinueLose >= ConfigInt("ReduceRankFailCount")) rank = Math.Max(1, rank - 1);
        var matches = Rows<Theatre5MatchConfigTable>().Where(row =>
            Convert.ToInt32(row.CupCount, CultureInfo.InvariantCulture) == adventure.TrophyNum &&
            Convert.ToInt32(row.DefeatCount, CultureInfo.InvariantCulture) == ConfigInt("PvpHealth") - adventure.Health)
            .OrderBy(row => row.Id).ToList();
        Require(matches.Count >= rank, 20280012);
        var match = matches[rank - 1];
        int cups = Convert.ToInt32(match.EnemyCupCount, CultureInfo.InvariantCulture);
        int defeats = Convert.ToInt32(match.EnemyDefeatCount, CultureInfo.InvariantCulture);
        var pool = Player.collection.Find(player => player.PlayerData.Id != m.Session.player.PlayerData.Id
            && player.Theatre5.Data.ActivityId == m.Data.ActivityId && player.Theatre5.Data.PvpAdventureData != null)
            .SortBy(player => player.PlayerData.Id).Limit(ConfigInt("BattlePoolMaxLen")).ToList()
            .Where(player => player.Theatre5.PendingMutation is null && player.Theatre5.Data.PvpAdventureData is { } run
                && run.TrophyNum == cups && ConfigInt("PvpHealth") - run.Health == defeats
                && run.BagData.SkillDict.Count > 0 && player.Theatre5.Data.Characters.TryGetValue(run.CharacterId, out var character)
                && RankForRating(character.Rating).Id == rank).ToList();
        if (pool.Count > 0)
        {
            var player = pool[RandomNumberGenerator.GetInt32(pool.Count)];
            var run = player.Theatre5.Data.PvpAdventureData!;
            var character = player.Theatre5.Data.Characters[run.CharacterId];
            m.State.PvpOpponent = BuildPlayerActor(player, run, character.FashionId);
            var sourceState = Clone(player.Theatre5);
            sourceState.Data.PvpType = 1;
            FreezeOpponentBuffs(player, sourceState, m.State.PvpOpponent);
            m.State.PvpOpponentPlayerId = checked((int)player.PlayerData.Id);
            m.State.PvpOpponentRobotId = 0;
            m.State.PvpOpponentRating = character.Rating;
            adventure.EnemyData = MatchDisplay(m.State.PvpOpponent, checked((int)player.PlayerData.CurrHeadPortraitId), run.Missioning);
        }
        else
        {
            var robots = Rows<Theatre5MatchRobotTable>().Where(row => row.GradeId == rank
                && Convert.ToInt32(row.CupCount, CultureInfo.InvariantCulture) == cups
                && Convert.ToInt32(row.DefeatCount, CultureInfo.InvariantCulture) == defeats).ToList();
            Require(robots.Count > 0, 20280012);
            var robot = robots[RandomNumberGenerator.GetInt32(robots.Count)];
            var character = Rows<Theatre5CharacterTable>().Single(row => row.Id == robot.CharacterId);
            // An NPC is explicitly labelled; no fictional user ID, identity, leaderboard entry, or native enemy.
            m.State.PvpOpponent = new()
            {
                TemplateId = character.TemplateId, Name = $"{character.Name} (NPC)",
                HeadFrameId = ConfigInt("RobotHeadHeadFrames"), AutoChessData = new()
                {
                    CharacterId = robot.CharacterId, CharacterLevel = robot.Level,
                    FashionId = character.FashionIds[0], Skills = robot.SkillIds.Where(id => id > 0).ToList(),
                    RuneEvolves = robot.EquipIds.Select((id, index) => new Theatre5RuneEvolve
                    {
                        RuneId = id, IsStrengthen = index < robot.RuneEvolves.Count && robot.RuneEvolves[index] != 0
                    }).Where(row => row.RuneId > 0).ToList(),
                    Relics = robot.RelicIds.Where(id => id > 0).ToList()
                }
            };
            // A robot owns exactly its authored build and cup/defeat history, never an account's unlocks.
            var robotRun = new Theatre5PvpAdventureData
            {
                CharacterId = robot.CharacterId, CharacterLv = robot.Level, TrophyNum = cups,
                Health = ConfigInt("PvpHealth") - defeats, RoundNum = cups + defeats + 1,
                NormalOriginRating = robot.Score
            };
            int instance = 0;
            foreach (int id in robot.SkillIds.Where(id => id > 0))
                robotRun.BagData.SkillDict[++instance] = new() { InstanceId = instance, ItemId = id, ItemType = 1 };
            foreach (var rune in m.State.PvpOpponent.AutoChessData!.RuneEvolves)
                robotRun.BagData.RuneDict[++instance] = new() { InstanceId = instance, ItemId = rune.RuneId, ItemType = 2, IsStrengthen = rune.IsStrengthen };
            foreach (int id in robot.RelicIds.Where(id => id > 0))
                robotRun.BagData.RelicDict[++instance] = new() { InstanceId = instance, ItemId = id, ItemType = 7 };
            var robotState = new PlayerTheatre5State
            {
                PvpWinCount = cups, PvpLoseCount = defeats,
                Data = new() { PvpType = 1, PvpAdventureData = robotRun }
            };
            robotState.Data.Characters[robot.CharacterId] = new() { Id = robot.CharacterId, Rating = robot.Score };
            FreezeOpponentBuffs(null, robotState, m.State.PvpOpponent);
            m.State.PvpOpponentPlayerId = 0;
            m.State.PvpOpponentRobotId = robot.Id;
            m.State.PvpOpponentRating = robot.Score;
            adventure.EnemyData = MatchDisplay(m.State.PvpOpponent, ConfigInt("RobotHeadPortraits"), null);
        }
    }

    private static void FreezeOpponentBuffs(Player? player, PlayerTheatre5State source, Theatre5AutoChessNpcData actor)
    {
        // All imported relic-condition trees read only the supplied source state. Never use the matcher's run.
        foreach (var effect in GetRelicBattleEffects(player, source))
            if (effect.AddBuffResult is { } buffs)
                foreach (int id in buffs.Buffs) actor.AutoChessData!.MagicIds[id] = 1;
        // The native client overwrites enemy base attributes; AddAttr cannot be pre-added without double counting.
    }

    private static Theatre5MatchEnemy MatchDisplay(Theatre5AutoChessNpcData actor, int portrait, Theatre5Mission? mission) => new()
    {
        Name = actor.Name, HeadPortraitId = portrait, HeadFrameId = actor.HeadFrameId,
        CharacterId = actor.AutoChessData!.CharacterId, IsUseSkin = actor.AutoChessData.FashionId,
        SkillIds = actor.AutoChessData.Skills.ToList(), RuneEvolves = Clone(actor.AutoChessData.RuneEvolves),
        MissionId = mission?.MissionId ?? 0, MissionBountyLevel = mission?.MissionBounty.BountyLevel ?? 0,
        MissionRelicId = mission?.MissionRelicId ?? 0
    };

    internal static void ApplyPvpBattleResult(Mutation m, Theatre5DlcFightResultData report, Theatre5AutoChessGameplayResult result)
    {
        var adventure = m.Data.PvpAdventureData;
        Require(adventure is not null, 20280007);
        Require(adventure.Status is 2 or 3 or 4 or 5 or 6 or 7, 20280006);
        Require(report.SettleState is >= 0 and <= 3, 20280017);
        if (report.SettleState == 3)
        {
            // Relog interruption restarts the already-frozen opponent, never consumes an outcome.
            adventure.Status = m.State.PvpOpponent is not null ? 5 : 7;
            FillPvpResult(m, result);
            return;
        }
        bool retreat = report.SettleState is 1 or 2;
        // Native Level_1071_Logic resolves self death first: simultaneous deaths are losses, not draws.
        // WDraw exists in config, but the actual native result exposes no draw outcome to apply it to.
        bool win = !retreat && report.IsPlayerWin;
        m.State.PvpContinueWin = win ? checked(m.State.PvpContinueWin + 1) : 0;
        m.State.PvpContinueLose = !win ? checked(m.State.PvpContinueLose + 1) : 0;
        if (win) { adventure.TrophyNum++; m.State.PvpWinCount++; }
        else { adventure.Health = Math.Max(0, adventure.Health - 1); m.State.PvpLoseCount++; }
        m.State.PvpInitialRating ??= adventure.NormalOriginRating;
        int initialRating = m.State.PvpInitialRating.Value;
        var rank = RankForRating(initialRating);
        // LOCAL rating ledger: Elo expected score (400-point scale), authored W/10000 and K.
        // Accumulate double deltas; round away from zero once at finalization. Retreat has at least KLoseMin loss.
        double expected = 1.0 / (1.0 + Math.Pow(10.0, Math.Clamp(
            (m.State.PvpOpponentRating - initialRating) / 400.0, -8.0, 8.0)));
        double actual = ConfigInt(win ? "WWin" : "WLose") / 10000.0;
        int k = win ? (adventure.IsPvpExtra ? rank.KExWin : rank.KWin)
            : Convert.ToInt32(rank.KLose, CultureInfo.InvariantCulture);
        double delta = k * (actual - expected);
        if (retreat) delta = Math.Min(delta, -ConfigInt("KLoseMin"));
        m.State.PvpProcessRating += delta;
        m.Data.CommonFightCnt[adventure.CharacterId] = checked(m.Data.CommonFightCnt.GetValueOrDefault(adventure.CharacterId) + 1);
        if (win)
        {
            m.State.CharacterWinCounts[adventure.CharacterId] = checked(m.State.CharacterWinCounts.GetValueOrDefault(adventure.CharacterId) + 1);
            RecordMetaProgress(m, "BattleWin", adventure.CharacterId);
        }
        RecordMetaProgress(m, "BattleFinish", adventure.CharacterId);
        string trigger = win ? "BattleWin" : "BattleLose";
        TriggerEffects(m, trigger);
        UpdateMissionProgress(m, trigger);
        TriggerEffects(m, "RoundEnd");
        UpdateMissionProgress(m, "RoundEnd");
        adventure.RoundNum = checked(adventure.RoundNum + 1);
        m.State.PvpOpponent = null;
        adventure.EnemyData = null;
        bool normalComplete = adventure.TrophyNum >= ConfigInt("PvpTarget");
        bool final = retreat || adventure.Health == 0
            || adventure.IsPvpExtra && adventure.TrophyNum >= ConfigInt("PvpExtraTarget");
        adventure.Status = !final && normalComplete && !adventure.IsPvpExtra ? 8 : 7;
        if (adventure.Status == 8) adventure.NormalOriginRating = ProjectPvpFinish(m).Rating;
        FillPvpResult(m, result);
        if (final) FinishPvp(m, result);
    }

    private static void FillPvpResult(Mutation m, Theatre5AutoChessGameplayResult result)
    {
        var adventure = m.Data.PvpAdventureData!;
        var character = m.Data.Characters[adventure.CharacterId];
        result.CheckFailTimes = adventure.CheckFailTimes;
        result.RoundNum = adventure.RoundNum;
        result.ProcessRating = m.State.PvpProcessRating;
        result.TrophyNum = adventure.TrophyNum;
        result.Health = adventure.Health;
        result.IsPvpExtra = adventure.IsPvpExtra;
        result.IsPvpExtraChoice = adventure.Status == 8;
        result.NormalOriginRating = adventure.NormalOriginRating;
        result.Rating = character.Rating;
        result.RankProtectNum = character.RankProtectNum;
        result.Rank = RankForRating(character.Rating).Id;
        result.CommonFightCnt = new(m.Data.CommonFightCnt);
        result.IsCanFreeUnlockGrid = adventure.IsCanFreeUnlockGrid;
    }

    // Pure projection shared by overtime preview and committed decline/final settlement.
    // Reading the preview must not consume protection, update rank, or grant meta progress.
    private static (int Rating, int RankProtectNum, double ProcessRating) ProjectPvpFinish(Mutation m)
    {
        var adventure = m.Data.PvpAdventureData!;
        var character = m.Data.Characters[adventure.CharacterId];
        int initialRating = m.State.PvpInitialRating ?? adventure.NormalOriginRating;
        var rank = RankForRating(initialRating);
        double change = m.State.PvpProcessRating;
        // LOCAL finish/protection ledger: normal clear adds rank bonus and health-indexed guarantee;
        // perfect clear adds perfect bonus. Overtime adds authored success bonus or failure penalty.
        // One persisted guard prevents crossing the current rank floor, not all rating loss.
        if (adventure.TrophyNum >= ConfigInt("PvpTarget"))
        {
            change += rank.PvpBonusRating;
            if (adventure.Health == ConfigInt("PvpHealth")) change += rank.PvpPerfectRating;
            var guarantees = Rows<Theatre5ConfigTable>().Single(row => row.Key == "MinPvpFinishScores").Values;
            change = Math.Max(change, Convert.ToInt32(guarantees[Math.Clamp(adventure.Health - 1, 0, guarantees.Count - 1)], CultureInfo.InvariantCulture));
        }
        if (adventure.IsPvpExtra) change += adventure.TrophyNum >= ConfigInt("PvpExtraTarget")
            ? rank.PvpExBonusRating : -Convert.ToInt32(rank.PvpExLoseRating, CultureInfo.InvariantCulture);
        int rating = Math.Max(0, checked(initialRating + (int)Math.Round(change, MidpointRounding.AwayFromZero)));
        int floor = Convert.ToInt32(rank.Rating, CultureInfo.InvariantCulture);
        int protection = character.RankProtectNum;
        if (rating < floor && protection > 0)
        {
            rating = floor;
            protection--;
        }
        var newRank = RankForRating(rating);
        if (newRank.Id > rank.Id) protection = newRank.RankProtect;
        return (rating, protection, change);
    }

    private static void FinishPvp(Mutation m, Theatre5AutoChessGameplayResult result)
    {
        var adventure = m.Data.PvpAdventureData!;
        var character = m.Data.Characters[adventure.CharacterId];
        var projection = ProjectPvpFinish(m);
        character.Rating = projection.Rating;
        character.RankProtectNum = projection.RankProtectNum;
        m.State.PvpProcessRating = projection.ProcessRating;
        FillPvpResult(m, result);
        result.IsFinish = true;
        result.IsPvpExtraChoice = false;
        RecordMetaProgress(m, "PvpRankChanged", adventure.CharacterId);
        // Rank and Activity carry no payout RewardId. Authored rank achievements use shared meta tasks.
        // Never invent direct currency payouts or mark unclaimed task rewards as granted.
        m.Data.PvpAdventureData = null;
        m.State.PvpOpponent = null;
    }

    [RequestPacketHandler("Theatre5SettleExtraChoiceRequest")]
    public static void Theatre5SettleExtraChoice(Session session, Packet.Request packet) =>
        Handle<Theatre5SettleExtraChoiceRequest, Theatre5SettleExtraChoiceResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session, 1);
            var adventure = m.Data.PvpAdventureData;
            Require(adventure is not null && adventure.Status == 8 && !adventure.IsPvpExtra, 20285017);
            response.SettleResult = new();
            if (request.IsExtra)
            {
                adventure.IsPvpExtra = true;
                adventure.Status = 7;
                FillPvpResult(m, response.SettleResult);
            }
            else FinishPvp(m, response.SettleResult);
        });
}
