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
    public class JoinActivityResponse
    {
        public int Code { get; set; }
        public int ChallengeId { get; set; }
    }

    [MessagePackObject(true)]
    public class ScoreQueryResponse
    {
        public int Code { get; set; }
        public double WaveRate { get; set; }
        public List<dynamic> GroupPlayerList { get; set; } = new();
        public List<dynamic> TeamPlayerList { get; set; } = new();
        public int ChallengeId { get; set; }
        public int ActivityNo { get; set; }
        public int ArenaLevel { get; set; }
        public int ContributeScore { get; set; }
    }

    [MessagePackObject(true)]
    public class AreaDataResponse
    {
        public int Code { get; set; }
        public int TotalPoint { get; set; }
        public List<dynamic> AreaList { get; set; } = new();
        public Dictionary<int, int> AreaDistributeMaxPointDict { get; set; } = new();
        public List<int> GroupFightEvents { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GroupMemberResponse
    {
        public int Code { get; set; }
        public double WaveRate { get; set; }
        public List<dynamic> GroupPlayerList { get; set; } = new();
        public dynamic PlayerInfo { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class ArenaModule
    {
        private const string BattlefieldSnapshotPath = "Configs/simulated_battlefield.json";
        private static readonly Lazy<JObject> BattlefieldSnapshot = new(() => JsonSnapshot.LoadObject(BattlefieldSnapshotPath));

        [RequestPacketHandler("JoinActivityRequest")]
        public static void JoinActivityRequestHandler(Session session, Packet.Request packet)
        {
            session.player.SimulatedBattlefield ??= new();
            session.player.SimulatedBattlefield.ArenaJoined = true;
            session.player.Save();
            session.SendResponse(new JoinActivityResponse
            {
                Code = 0,
                ChallengeId = ArenaConfig.Value<int>("ChallengeId")
            }, packet.Id);
        }

        [RequestPacketHandler("ScoreQueryRequest")]
        public static void ScoreQueryRequestHandler(Session session, Packet.Request packet)
        {
            Dictionary<string, object> playerInfo = BuildArenaPlayerInfo(session);
            session.SendResponse(new ScoreQueryResponse
            {
                Code = 0,
                WaveRate = ArenaConfig.Value<double>("WaveRate"),
                GroupPlayerList = [playerInfo],
                TeamPlayerList = [],
                ChallengeId = ArenaConfig.Value<int>("ChallengeId"),
                ActivityNo = ArenaConfig.Value<int>("ActivityNo"),
                ArenaLevel = ArenaConfig.Value<int>("ArenaLevel"),
                ContributeScore = ResolveContributeScore(session.player)
            }, packet.Id);
        }

        [RequestPacketHandler("AreaDataRequest")]
        public static void AreaDataRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(BuildAreaData(session), packet.Id);
        }

        [RequestPacketHandler("GroupMemberRequest")]
        public static void GroupMemberRequestHandler(Session session, Packet.Request packet)
        {
            Dictionary<string, object> playerInfo = BuildArenaPlayerInfo(session);
            session.SendResponse(new GroupMemberResponse
            {
                Code = 0,
                WaveRate = ArenaConfig.Value<double>("WaveRate"),
                GroupPlayerList = [playerInfo],
                PlayerInfo = playerInfo
            }, packet.Id);
        }

        internal static NotifyArenaActivity BuildLoginData(Player player, long? now = null)
        {
            long currentTime = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long cycleSeconds = RequiredPositiveLong(BattlefieldSnapshot.Value, "CycleSeconds");
            long baseTeamTime = RequiredPositiveLong(ArenaConfig, "TeamTime");
            long cycleOffset = currentTime > baseTeamTime
                ? ((currentTime - baseTeamTime) / cycleSeconds) * cycleSeconds
                : 0;

            return new NotifyArenaActivity
            {
                ActivityNo = ArenaConfig.Value<int>("ActivityNo"),
                ChallengeId = ArenaConfig.Value<int>("ChallengeId"),
                Status = ArenaConfig.Value<int>("Status"),
                NextStatusTime = ShiftTimestamp("NextStatusTime", cycleOffset),
                ArenaLevel = ArenaConfig.Value<int>("ArenaLevel"),
                JoinActivity = player.SimulatedBattlefield?.ArenaJoined == true ? 1 : 0,
                UnlockCount = ArenaConfig.Value<int>("UnlockCount"),
                TeamTime = ShiftTimestamp("TeamTime", cycleOffset),
                FightTime = ShiftTimestamp("FightTime", cycleOffset),
                ResultTime = ShiftTimestamp("ResultTime", cycleOffset),
                ContributeScore = ResolveContributeScore(player),
                ProtectedScore = ArenaConfig.Value<int>("ProtectedScore"),
                ArenaIndex = ArenaConfig.Value<int>("ArenaIndex")
            };
        }

        private static AreaDataResponse BuildAreaData(Session session)
        {
            int totalPoint = Math.Max(0, session.player.SimulatedBattlefield?.ArenaPoint ?? 0);
            List<dynamic> areas = new();
            if (BattlefieldSnapshot.Value["ArenaAreas"] is not JArray areaConfigs)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: ArenaAreas must be an array.");

            foreach (JObject area in areaConfigs.OfType<JObject>())
            {
                int areaId = JsonSnapshot.ReadInt(area, "AreaId");
                areas.Add(new Dictionary<string, object?>
                {
                    ["AreaId"] = areaId,
                    ["Lock"] = JsonSnapshot.ReadInt(area, "Lock"),
                    ["Point"] = totalPoint,
                    ["LordList"] = Array.Empty<object>(),
                    ["StageInfos"] = null
                });
            }

            Dictionary<int, int> maxPoints = new();
            if (BattlefieldSnapshot.Value["AreaDistributeMaxPointDict"] is JObject maxPointConfig)
            {
                foreach (JProperty property in maxPointConfig.Properties())
                {
                    if (int.TryParse(property.Name, out int level))
                        maxPoints[level] = property.Value.Value<int>();
                }
            }

            List<int> fightEvents = BattlefieldSnapshot.Value["GroupFightEvents"] is JArray eventConfig
                ? eventConfig.Values<int>().ToList()
                : [];
            return new AreaDataResponse
            {
                Code = 0,
                TotalPoint = totalPoint,
                AreaList = areas,
                AreaDistributeMaxPointDict = maxPoints,
                GroupFightEvents = fightEvents
            };
        }

        private static Dictionary<string, object> BuildArenaPlayerInfo(Session session)
        {
            PlayerData playerData = session.player.PlayerData;
            return new Dictionary<string, object>
            {
                ["Id"] = playerData.Id,
                ["Name"] = playerData.Name,
                ["CurrHeadPortraitId"] = playerData.CurrHeadPortraitId,
                ["CurrHeadFrameId"] = playerData.CurrHeadFrameId,
                ["Point"] = Math.Max(0, session.player.SimulatedBattlefield?.ArenaPoint ?? 0),
                ["ContributeScore"] = ResolveContributeScore(session.player),
                ["LastPointTime"] = 0L,
                ["CurrMedalId"] = playerData.CurrMedalId
            };
        }

        private static int ResolveContributeScore(Player player)
        {
            int savedScore = player.SimulatedBattlefield?.ArenaContributeScore ?? 0;
            return savedScore > 0 ? savedScore : ArenaConfig.Value<int>("ContributeScore");
        }

        private static JObject ArenaConfig => BattlefieldSnapshot.Value["ArenaActivity"] as JObject
            ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: ArenaActivity must be an object.");

        private static uint ShiftTimestamp(string fieldName, long offset)
        {
            long value = RequiredPositiveLong(ArenaConfig, fieldName) + offset;
            return checked((uint)value);
        }

        private static long RequiredPositiveLong(JObject data, string fieldName)
        {
            long value = data.Value<long>(fieldName);
            return value > 0
                ? value
                : throw new InvalidDataException($"{BattlefieldSnapshotPath}: {fieldName} must be positive.");
        }
    }
}
