using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;
using AscNet.Logging;
using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Options;

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

    public class Player
    {
        public static readonly IMongoCollection<Player> collection = Common.db.GetCollection<Player>("players");
        private static readonly Logger log = new(typeof(Player), LogLevel.WARN, LogLevel.WARN);

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
                    CurrHeadPortraitId = 9000003,
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
            player.AddHead(9000001);
            player.AddHead(9000002);
            player.AddHead(9000003);
            
            collection.InsertOne(player);

            return player;
        }

        public void AddHead(int id)
        {
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

        public void Save()
        {
            collection.ReplaceOne(Builders<Player>.Filter.Eq(x => x.Id, Id), this);
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

        [BsonElement("purchase_buy_times")]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<uint, int> PurchaseBuyTimes { get; set; } = new();

        [BsonElement("team_groups")]
        [BsonRequired]
        [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
        public Dictionary<int, TeamGroupDatum> TeamGroups { get; set; }

        [BsonElement("fuben_main_line_data")]
        public FubenMainLineData FubenMainLineData { get; set; } = new();

        [BsonElement("mission_progress")]
        public MissionProgressState MissionProgress { get; set; } = new();

        [BsonElement("big_world_state")]
        public BigWorldPlayerState BigWorldState { get; set; } = new();

        [BsonElement("fuben_main_line2_data")]
        public FubenMainLine2Data FubenMainLine2Data { get; set; } = new();
    }
}
