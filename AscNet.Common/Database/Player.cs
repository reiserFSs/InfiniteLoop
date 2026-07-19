using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;
using AscNet.Logging;
using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Options;
using AscNet.Common.Util;
using AscNet.Table.V2.share.headportrait;

namespace AscNet.Common.Database
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public class BigWorldPlayerState
    {
        [BsonElement("last_world_id")]
        public int LastWorldId { get; set; }

        [BsonElement("last_level_id")]
        public int LastLevelId { get; set; }

        [BsonElement("last_position")]
        public BigWorldVector3? LastPosition { get; set; }

        [BsonElement("last_rotation")]
        public BigWorldVector4? LastRotation { get; set; }

        [BsonElement("last_rotation_y")]
        public double? LastRotationY { get; set; }

        [BsonElement("last_self_runtime_id")]
        public int LastSelfRuntimeId { get; set; }

        [BsonElement("claimed_scene_objects")]
        public List<BigWorldClaimedSceneObject> ClaimedSceneObjects { get; set; } = new();

        [BsonElement("big_world_course_read_element_ids")]
        public List<int> BigWorldCourseReadElementIds { get; set; } = new();

        [BsonElement("big_world_course_task_progress")]
        public int BigWorldCourseTaskProgress { get; set; }
    }

    public class BigWorldVector3
    {
        [BsonElement("x")]
        public double X { get; set; }

        [BsonElement("y")]
        public double Y { get; set; }

        [BsonElement("z")]
        public double Z { get; set; }
    }

    public class BigWorldVector4
    {
        [BsonElement("x")]
        public double X { get; set; }

        [BsonElement("y")]
        public double Y { get; set; }

        [BsonElement("z")]
        public double Z { get; set; }

        [BsonElement("w")]
        public double W { get; set; }
    }

    public class BigWorldClaimedSceneObject
    {
        [BsonElement("level_id")]
        public int LevelId { get; set; }

        [BsonElement("place_id")]
        public int PlaceId { get; set; }

        [BsonElement("uuid")]
        public int Uuid { get; set; }

        [BsonElement("claimed_at")]
        public long ClaimedAt { get; set; }
    }

    public class MissionProgressState
    {
        [BsonElement("condition_counters")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> ConditionCounters { get; set; } = new();

        [BsonElement("claimed_task_ids")]
        public List<int> ClaimedTaskIds { get; set; } = new();

        [BsonElement("new_player_reward_records")]
        public List<int> NewPlayerRewardRecords { get; set; } = new();

        [BsonElement("newbie_reward_records")]
        public List<int> NewbieRewardRecords { get; set; } = new();

        [BsonElement("newbie_honor_reward")]
        public bool NewbieHonorReward { get; set; }

        [BsonElement("daily_reset_day")]
        public long DailyResetDay { get; set; } = -1;

        [BsonElement("weekly_reset_week")]
        public long WeeklyResetWeek { get; set; } = -1;
    }

    public class BossSingleHistoryRecordState
    {
        [BsonElement("stage_id")]
        public int StageId { get; set; }

        [BsonElement("score")]
        public int Score { get; set; }

        [BsonElement("characters")]
        public List<int> Characters { get; set; } = new();

        [BsonElement("partners")]
        public List<int> Partners { get; set; } = new();
    }

    public class BossSingleStageRecordState
    {
        [BsonElement("stage_id")]
        public int StageId { get; set; }

        [BsonElement("score")]
        public int Score { get; set; }

        [BsonElement("characters")]
        public List<int> Characters { get; set; } = new();

        [BsonElement("is_use_auto_fight")]
        public bool IsUseAutoFight { get; set; }

        [BsonElement("max_score")]
        public int MaxScore { get; set; }

        [BsonElement("max_characters")]
        public List<int> MaxCharacters { get; set; } = new();

        [BsonElement("max_partners")]
        public List<int> MaxPartners { get; set; } = new();
    }

    [BsonIgnoreExtraElements]
    public class SimulatedBattlefieldState
    {
        [BsonElement("arena_joined")]
        public bool ArenaJoined { get; set; }

        [BsonElement("arena_activity_no")]
        public int ArenaActivityNo { get; set; }

        [BsonElement("arena_challenge_id")]
        public int ArenaChallengeId { get; set; }

        [BsonElement("arena_level")]
        public int ArenaLevel { get; set; }

        [BsonElement("arena_join_activity")]
        public int ArenaJoinActivity { get; set; }

        [BsonElement("arena_point")]
        public int ArenaPoint { get; set; }

        [BsonElement("arena_last_point_time")]
        public long ArenaLastPointTime { get; set; }

        [BsonElement("arena_area_max_points")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> ArenaAreaMaxPoints { get; set; } = new();

        [BsonElement("arena_stage_max_points")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<uint, int> ArenaStageMaxPoints { get; set; } = new();

        [BsonElement("arena_distribute_max_points")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> ArenaDistributeMaxPoints { get; set; } = new();

        [BsonElement("arena_contribute_score")]
        public int ArenaContributeScore { get; set; }

        [BsonElement("arena_protected_score")]
        public int ArenaProtectedScore { get; set; }

        [BsonElement("boss_activity_no")]
        public int BossActivityNo { get; set; }

        [BsonElement("boss_rank_platform")]
        public int BossRankPlatform { get; set; }

        [BsonElement("boss_old_level_type")]
        public int BossOldLevelType { get; set; }

        [BsonElement("boss_level_type")]
        public int BossLevelType { get; set; }

        [BsonElement("boss_list_options")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<int>> BossListOptions { get; set; } = new();

        [BsonElement("boss_list")]
        public List<int> BossList { get; set; } = new();

        [BsonElement("boss_max_score")]
        public int BossMaxScore { get; set; }

        [BsonElement("boss_total_score")]
        public int BossTotalScore { get; set; }

        [BsonElement("boss_current_total_score")]
        public int BossCurrentTotalScore { get; set; }

        [BsonElement("boss_challenge_count")]
        public int BossChallengeCount { get; set; }

        [BsonElement("boss_challenge_reset_day")]
        public long BossChallengeResetDay { get; set; } = -1;

        [BsonElement("boss_auto_fight_count")]
        public int BossAutoFightCount { get; set; }

        [BsonElement("boss_character_points")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> BossCharacterPoints { get; set; } = new();

        [BsonElement("boss_history")]
        public List<BossSingleHistoryRecordState> BossHistory { get; set; } = new();

        [BsonElement("boss_stage_records")]
        public List<BossSingleStageRecordState> BossStageRecords { get; set; } = new();

        [BsonElement("boss_reset_stage_ids")]
        public List<int> BossResetStageIds { get; set; } = new();

        [BsonElement("boss_normal_stage_teams")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, List<int>> BossNormalStageTeams { get; set; } = new();

        [BsonElement("boss_trial_scores")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> BossTrialScores { get; set; } = new();

        [BsonElement("boss_bestiary_scores")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, int> BossBestiaryScores { get; set; } = new();

        [BsonElement("boss_last_score_time")]
        public long BossLastScoreTime { get; set; }

        [BsonElement("boss_claimed_reward_ids")]
        public List<int> BossClaimedRewardIds { get; set; } = new();

        [BsonElement("repeat_challenge_level")]
        public int RepeatChallengeLevel { get; set; } = 1;

        [BsonElement("repeat_challenge_exp")]
        public int RepeatChallengeExp { get; set; }

        [BsonElement("repeat_challenge_cleared")]
        public bool RepeatChallengeCleared { get; set; }
    }

    public class MedalUnlockState
    {
        [BsonElement("id")]
        public int Id { get; set; }

        [BsonElement("time")]
        public long Time { get; set; }

        [BsonElement("keep_time")]
        public long KeepTime { get; set; }
    }

    public class ChatBoardUnlockState
    {
        [BsonElement("id")]
        public long Id { get; set; }

        [BsonElement("get_time")]
        public long GetTime { get; set; }

        [BsonElement("end_time")]
        public long EndTime { get; set; }
    }

    public partial class Player
    {
        public static readonly IMongoCollection<Player> collection = Common.db.GetCollection<Player>("players");
        private static readonly Logger log = new(typeof(Player), LogLevel.WARN, LogLevel.WARN);

        public static void EnsureLeaderboardIndexes()
        {
            collection.Indexes.CreateOne(
                new CreateIndexModel<Player>(
                    Builders<Player>.IndexKeys
                        .Ascending(player => player.Theatre6.Pvp.AuthorizedSeasonId)
                        .Ascending(player => player.Theatre6.Pvp.InitializedSeasonId)
                        .Descending(player => player.Theatre6.Pvp.Score)
                        .Ascending(player => player.PlayerData.Id),
                    new CreateIndexOptions
                    {
                        Name = "theatre6_pvp_season_score_player",
                        Sparse = true
                    }));
            BossInshotRankEntry.EnsureIndexes();
        }

        public static Player FromPlayerId(long id)
        {
            return collection.AsQueryable().FirstOrDefault(x => x.PlayerData.Id == id) ?? Create(id);
        }

        public static Player? TryFromPlayerId(long id)
        {
            try
            {
                return collection.AsQueryable().FirstOrDefault(x => x.PlayerData.Id == id);
            }
            catch (Exception ex)
            {
                log.Warn($"Player lookup failed for id {id}; falling back to minimal player info.", ex);
                return null;
            }
        }

        public static Player? FromToken(string token)
        {
            return collection.AsQueryable().FirstOrDefault(x => x.Token == token);
        }

        private static Player Create(long id)
        {
            List<HeadPortraitTable> initialHeads = TableReaderV2.Parse<HeadPortraitTable>()
                .Where(head => head.IsInit == 1)
                .ToList();
            int initialPortraitId = initialHeads
                .Where(head => head.Type == 1)
                .MaxBy(head => head.Priority)?.Id ?? 0;
            Player player = new()
            {
                Token = Guid.NewGuid().ToString(),
                PlayerData = new()
                {
                    Id = id,
                    Name = $"Commandant{id}",
                    Level = 1,
                    Sign = "",
                    DisplayCharId = 1021001,
                    DisplayCharIdList = new() { 1021001 },
                    Birthday = null,
                    Gender = 0,
                    HonorLevel = 1,
                    ServerId = "1",
                    CurrTeamId = 1,
                    CurrHeadPortraitId = initialPortraitId,
                    CurrHeadFrameId = 0,
                    CurrMedalId = 0,
                    CurrentChatBoardId = 25000001,
                    AppearanceSettingInfo = new()
                    {
                        TitleType = 1,
                        CharacterType = 1,
                        FashionType = 1,
                        WeaponFashionType = 1,
                        DormitoryType = 1
                    },
                    CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    LastLoginTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    ChangeGenderTime = 0,
                    Flags = 1,
                    NewPlayerTaskActiveDay = 0
                },
                HeadPortraits = new(),
                TeamGroups = new()
                {
                    {1, new TeamGroupDatum()
                    {
                        TeamType = 1,
                        TeamId = 1,
                        CaptainPos = 1,
                        FirstFightPos = 1,
                        TeamData = new()
                        {
                            {1, 1021001},
                            {2, 0},
                            {3, 0}
                        },
                        TeamName = null
                    }}
                },
                FubenMainLineData = new(),
            };
            foreach (HeadPortraitTable head in initialHeads)
                player.AddHead(head.Id);
            
            collection.InsertOne(player);

            return player;
        }

        public void AddHead(int id)
        {
            if (HeadPortraits.Any(head => head.Id == id))
                return;

            HeadPortraits.Add(new()
            {
                Id = id,
                LeftCount = 1,
                BeginTime = DateTimeOffset.Now.ToUnixTimeSeconds()
            });
        }

        public bool AddTreasure(int id)
        {
            if (FubenMainLineData.TreasureData.Contains(id))
            {
                return false;
            }

            FubenMainLineData.TreasureData.Add(id);
            return true;
        }

        public bool AddMainLine2MainTreasure(int mainId, int treasureIdx)
        {
            FubenMainLine2Data ??= new();
            MainLine2MainDatum? mainData = FubenMainLine2Data.MainDatas.FirstOrDefault(x => x.Id == mainId);
            if (mainData is null)
            {
                mainData = new MainLine2MainDatum
                {
                    Id = mainId
                };
                FubenMainLine2Data.MainDatas.Add(mainData);
            }

            if (mainData.MainTreasureIdxs.Contains(treasureIdx))
            {
                return false;
            }

            mainData.MainTreasureIdxs.Add(treasureIdx);
            mainData.MainTreasureIdxs.Sort();
            return true;
        }

        public bool AddGatherReward(int id)
        {
            if (GatherRewards.Contains(id))
            {
                return false;
            }

            GatherRewards.Add(id);
            return true;
        }

        public bool NormalizeTeamPrefabs()
        {
            List<TeamPrefabData> normalized = (TeamPrefabs ?? [])
                .OfType<TeamPrefabData>()
                .ToList();
            bool changed = TeamPrefabs is null || normalized.Count != TeamPrefabs.Count;
            if (changed)
                TeamPrefabs = normalized;
            return changed;
        }

        public bool IsEquipInTeamPrefab(uint equipId)
        {
            if (equipId == 0)
                return false;

            foreach (TeamPrefabData? teamPrefab in TeamPrefabs ?? [])
            {
                if (teamPrefab?.EquipData is null)
                    continue;

                foreach (TeamPrefabEquipData? equipData in teamPrefab.EquipData.Values)
                {
                    if (equipData?.EquipDataDict?.Values.Any(equip => equip?.EquipId == equipId) == true)
                        return true;
                }
            }

            return false;
        }

        public void Save()
        {
            collection.ReplaceOne(Builders<Player>.Filter.Eq(x => x.Id, Id), this);
        }

        public void SaveChecked()
        {
            ReplaceOneResult result = collection.ReplaceOne(
                Builders<Player>.Filter.Eq(x => x.Id, Id),
                this);
            if (!result.IsAcknowledged || result.MatchedCount != 1)
            {
                string matchCount = result.IsAcknowledged ? result.MatchedCount.ToString() : "unacknowledged";
                throw new MongoException($"Player save for id {PlayerData.Id} matched {matchCount} documents.");
            }
        }

        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("token")]
        [BsonRequired]
        public string Token { get; set; }

        [BsonElement("player_data")]
        [BsonRequired]
        public PlayerData PlayerData { get; set; }

        [BsonElement("head_portraits")]
        [BsonRequired]
        public List<HeadPortraitList> HeadPortraits { get; set; }

        [BsonElement("gather_rewards")]
        public List<int> GatherRewards { get; set; } = [5];

        [BsonElement("use_background_id")]
        public int UseBackgroundId { get; set; } = 14000001;

        [BsonElement("last_sign_in_time")]
        public long LastSignInTime { get; set; }

        [BsonElement("sign_in_claim_count")]
        public long SignInClaimCount { get; set; }

        [BsonElement("red_point_records")]
        public RedPointRecords RedPointRecords { get; set; } = new();

        [BsonElement("assist_character_id")]
        public int AssistCharacterId { get; set; }

        [BsonElement("unlock_comics")]
        public List<int> UnlockComics { get; set; } = AscNet.Common.ArchiveDefaults.CreateDefaultUnlockedArchiveComics();

        [BsonElement("life_tree_data")]
        public NotifyLifeTreeData LifeTreeData { get; set; } = new();

        [BsonElement("unlocked_medals")]
        public List<MedalUnlockState> UnlockedMedals { get; set; } = new();

        [BsonElement("unlocked_chat_boards")]
        public List<ChatBoardUnlockState> UnlockedChatBoards { get; set; } = new();

        [BsonElement("purchase_buy_times")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<uint, int> PurchaseBuyTimes { get; set; } = new();

        [BsonElement("shop_buy_times")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<uint, int> ShopBuyTimes { get; set; } = new();

        [BsonElement("team_groups")]
        [BsonRequired]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, TeamGroupDatum> TeamGroups { get; set; }

        [BsonElement("team_prefabs")]
        public List<TeamPrefabData> TeamPrefabs { get; set; } = new();

        [BsonElement("fuben_main_line_data")]
        public FubenMainLineData FubenMainLineData { get; set; } = new();

        [BsonElement("mission_progress")]
        public MissionProgressState MissionProgress { get; set; } = new();

        [BsonElement("simulated_battlefield")]
        public SimulatedBattlefieldState SimulatedBattlefield { get; set; } = new();

        [BsonElement("big_world_state")]
        public BigWorldPlayerState BigWorldState { get; set; } = new();

        [BsonElement("fuben_main_line2_data")]
        public FubenMainLine2Data FubenMainLine2Data { get; set; } = new();
    }
}
