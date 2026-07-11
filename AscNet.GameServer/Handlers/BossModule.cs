using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class BossSingleRankInfoRequest
    {
        public int SectionId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleRankInfoResponse
    {
        public int Code { get; set; }
        public int Rank { get; set; }
        public int TotalRank { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSelectLevelTypeRequest
    {
        public int LevelId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSelectLevelTypeResponse
    {
        public int Code { get; set; }
        public NotifyFubenBossSingleData.NotifyFubenBossSingleDataFubenBossSingleData FubenBossSingleData { get; set; }
        public Dictionary<int, List<int>> BossListDict { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BossSingleAutoFightRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleAutoFightResponse
    {
        public int Code { get; set; }
        public int Supply { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleGetAllRewardResponse
    {
        public int Code { get; set; }
        public List<dynamic> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class NotifyBossSingleRankInfo
    {
        public int RankType { get; set; }
        public int Rank { get; set; }
        public int TotalRank { get; set; }
    }

    [MessagePackObject(true)]
    public class GetActivityBossDataRequest
    {
    }

    [MessagePackObject(true)]
    public class GetActivityBossDataResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class BossModule
    {
        private const string BattlefieldSnapshotPath = "Configs/simulated_battlefield.json";
        private static readonly Lazy<JObject> BattlefieldSnapshot = new(() => JsonSnapshot.LoadObject(BattlefieldSnapshotPath));

        [RequestPacketHandler("BossSingleRankInfoRequest")]
        public static void BossSingleRankInfoRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleRankInfoRequest request = packet.Deserialize<BossSingleRankInfoRequest>();
            bool knownSection = ReadSectionIds().Contains(request.SectionId)
                || ReadBossListDict().Values.Any(sectionIds => sectionIds.Contains(request.SectionId));
            session.SendResponse(new BossSingleRankInfoResponse
            {
                Code = knownSection ? 0 : 1,
                Rank = 0,
                TotalRank = 0
            }, packet.Id);
        }

        [RequestPacketHandler("BossSingleSelectLevelTypeRequest")]
        public static void BossSingleSelectLevelTypeRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleSelectLevelTypeRequest request = packet.Deserialize<BossSingleSelectLevelTypeRequest>();
            Dictionary<int, List<int>> bossLists = ReadBossListDict();
            if (!bossLists.ContainsKey(request.LevelId))
            {
                session.SendResponse(new BossSingleSelectLevelTypeResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.SimulatedBattlefield ??= new();
            session.player.SimulatedBattlefield.BossLevelType = request.LevelId;
            session.player.Save();
            NotifyFubenBossSingleData notification = BuildLoginData(session.player);
            session.SendResponse(new BossSingleSelectLevelTypeResponse
            {
                Code = 0,
                FubenBossSingleData = notification.FubenBossSingleData,
                BossListDict = notification.BossListDict
            }, packet.Id);
        }

        [RequestPacketHandler("BossSingleAutoFightRequest")]
        public static void BossSingleAutoFightRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleAutoFightRequest request = packet.Deserialize<BossSingleAutoFightRequest>();
            bool knownStage = ReadTrialStageIds().Contains(request.StageId)
                || ReadBestiaryStageIds().Contains(request.StageId);
            bool clearedStage = session.stage.Stages.TryGetValue((uint)request.StageId, out StageDatum? stage)
                && stage.Passed;
            if (!knownStage || !clearedStage)
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.SimulatedBattlefield ??= new();
            if (!session.player.SimulatedBattlefield.BossClearedStageIds.Contains(request.StageId))
                session.player.SimulatedBattlefield.BossClearedStageIds.Add(request.StageId);
            session.player.Save();
            session.SendPush(BuildLoginData(session.player));
            session.SendResponse(new BossSingleAutoFightResponse
            {
                Code = 0,
                Supply = 0
            }, packet.Id);
        }

        [RequestPacketHandler("BossSingleGetAllRewardRequest")]
        public static void BossSingleGetAllRewardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BossSingleGetAllRewardResponse
            {
                Code = 0,
                RewardGoodsList = []
            }, packet.Id);
        }

        [RequestPacketHandler("GetActivityBossDataRequest")]
        public static void GetActivityBossDataRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetActivityBossDataResponse { Code = 1 }, packet.Id);
        }

        internal static bool RecordStageClear(Player player, int stageId)
        {
            if (!ReadTrialStageIds().Contains(stageId) && !ReadBestiaryStageIds().Contains(stageId))
                return false;

            player.SimulatedBattlefield ??= new();
            player.SimulatedBattlefield.BossClearedStageIds ??= [];
            if (!player.SimulatedBattlefield.BossClearedStageIds.Contains(stageId))
                player.SimulatedBattlefield.BossClearedStageIds.Add(stageId);
            return true;
        }

        internal static NotifyFubenBossSingleData BuildLoginData(Player player, long? now = null)
        {
            JObject config = BossConfig;
            int configuredLevelType = config.Value<int>("LevelType");
            int levelType = player.SimulatedBattlefield?.BossLevelType ?? 0;
            if (!ReadBossListDict().ContainsKey(levelType))
                levelType = configuredLevelType;

            uint resultTime = ArenaModule.BuildLoginData(player, now).ResultTime;
            long currentTime = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint remainTime = resultTime > currentTime
                ? checked((uint)(resultTime - currentTime))
                : 0;
            List<dynamic> trialStages = ReadTrialStageIds()
                .Select(stageId => BuildStageInfo(player, stageId))
                .Cast<dynamic>()
                .ToList();
            List<dynamic> bestiaryStages = ReadBestiaryStageIds()
                .Select(stageId => BuildStageInfo(player, stageId))
                .Cast<dynamic>()
                .ToList();
            List<dynamic> teams = ReadSectionIds()
                .Select(sectionId => (dynamic)new Dictionary<string, object>
                {
                    ["SectionId"] = sectionId,
                    ["CharacterIds"] = Array.Empty<int>()
                })
                .ToList();

            return new NotifyFubenBossSingleData
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = config.Value<int>("ActivityNo"),
                    OldLevelType = configuredLevelType,
                    LevelType = levelType,
                    RemainTime = remainTime,
                    RankPlatform = config.Value<int>("RankPlatform"),
                    AfreshId = config.Value<int>("AfreshId"),
                    IsResetOpen = config.Value<bool>("IsResetOpen"),
                    TrialStageInfoList = trialStages,
                    BestiraryStageInfoList = bestiaryStages,
                    NormalStageTeamInfos = teams
                },
                BossListDict = ReadBossListDict()
            };
        }

        private static Dictionary<string, object> BuildStageInfo(Player player, int stageId)
        {
            bool cleared = player.SimulatedBattlefield?.BossClearedStageIds?.Contains(stageId) == true;
            return new Dictionary<string, object>
            {
                ["StageId"] = stageId,
                ["Score"] = cleared ? BossConfig.Value<int>("ClearedStageScore") : 0
            };
        }

        private static IReadOnlyList<int> ReadTrialStageIds()
        {
            return ReadIntArray("TrialStageIds");
        }

        private static IReadOnlyList<int> ReadBestiaryStageIds()
        {
            return ReadIntArray("BestiaryStageIds");
        }

        private static IReadOnlyList<int> ReadSectionIds()
        {
            return ReadIntArray("SectionIds");
        }

        private static IReadOnlyList<int> ReadIntArray(string fieldName)
        {
            return BossConfig[fieldName] is JArray values
                ? values.Select(value => value.Value<int>()).ToArray()
                : throw new InvalidDataException($"{BattlefieldSnapshotPath}: BossSingle.{fieldName} must be an array.");
        }

        private static Dictionary<int, List<int>> ReadBossListDict()
        {
            if (BossConfig["BossListDict"] is not JObject values)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: BossSingle.BossListDict must be an object.");

            Dictionary<int, List<int>> result = new();
            foreach (JProperty property in values.Properties())
            {
                if (int.TryParse(property.Name, out int levelType) && property.Value is JArray sections)
                    result[levelType] = sections.Values<int>().ToList();
            }
            return result;
        }

        private static JObject BossConfig => BattlefieldSnapshot.Value["BossSingle"] as JObject
            ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: BossSingle must be an object.");
    }
}
