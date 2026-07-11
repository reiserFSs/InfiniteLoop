using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    internal static class RepeatChallengeModule
    {
        private const string BattlefieldSnapshotPath = "Configs/simulated_battlefield.json";
        private static readonly Lazy<JObject> Config = new(() =>
            JsonSnapshot.LoadObject(BattlefieldSnapshotPath)["RepeatChallenge"] as JObject
            ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge must be an object."));

        public static NotifyRepeatChallengeData BuildLoginData(Player player)
        {
            SimulatedBattlefieldState state = GetState(player);
            return new NotifyRepeatChallengeData
            {
                Id = RequiredInt("ActivityId"),
                ExpInfo = new NotifyRepeatChallengeData.NotifyRepeatChallengeDataExpInfo
                {
                    Level = state.RepeatChallengeLevel,
                    Exp = state.RepeatChallengeExp
                },
                RcChapters =
                [
                    new Dictionary<string, object>
                    {
                        ["Id"] = RequiredInt("ChapterId"),
                        ["FinishStages"] = state.RepeatChallengeCleared
                            ? new[] { RequiredInt("StageId") }
                            : Array.Empty<int>()
                    }
                ]
            };
        }

        public static bool IsStage(uint stageId)
        {
            return stageId == (uint)RequiredInt("StageId");
        }

        public static void ApplyPreFight(Player player, PreFightResponse.PreFightResponseFightData fightData)
        {
            if (!IsStage(fightData.StageId))
                return;

            SimulatedBattlefieldState state = GetState(player);
            fightData.RebootId = RequiredInt("RebootId");
            fightData.MonsterLevel = ResolveMonsterLevels((int)player.PlayerData.Level);

            JArray fightEventIds = RequiredArray("FightEventIdsByLevel");
            int eventCount = Math.Min(state.RepeatChallengeLevel, fightEventIds.Count);
            fightData.EventIds = fightEventIds
                .Take(eventCount)
                .Select((eventId, index) => (dynamic)RequiredPositiveInt(eventId, $"FightEventIdsByLevel[{index}]"))
                .ToList();
        }

        public static bool RecordStageClear(Player player, uint stageId, int challengeCount)
        {
            if (!IsStage(stageId) || challengeCount <= 0)
                return false;

            SimulatedBattlefieldState state = GetState(player);
            JArray levelUpExp = RequiredArray("LevelUpExp");
            int maxLevel = RequiredPositiveInt(Config.Value["MaxLevel"], "MaxLevel");
            if (levelUpExp.Count != maxLevel)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.LevelUpExp must contain one entry per authority level.");
            int previousLevel = state.RepeatChallengeLevel;
            int previousExp = state.RepeatChallengeExp;
            bool wasCleared = state.RepeatChallengeCleared;
            state.RepeatChallengeExp = checked(state.RepeatChallengeExp + checked(RequiredInt("ExpPerClear") * challengeCount));

            int cumulativeExp = 0;
            int resolvedLevel = 1;
            for (int levelIndex = 0; levelIndex < maxLevel - 1; levelIndex++)
            {
                cumulativeExp = checked(cumulativeExp + RequiredPositiveInt(
                    levelUpExp[levelIndex],
                    $"LevelUpExp[{levelIndex}]"));
                if (state.RepeatChallengeExp >= cumulativeExp)
                    resolvedLevel = levelIndex + 2;
            }
            int maximumExp = checked(cumulativeExp + RequiredPositiveInt(
                levelUpExp[maxLevel - 1],
                $"LevelUpExp[{maxLevel - 1}]"));
            state.RepeatChallengeExp = Math.Min(state.RepeatChallengeExp, maximumExp);
            state.RepeatChallengeLevel = resolvedLevel;
            state.RepeatChallengeCleared = true;

            return state.RepeatChallengeLevel != previousLevel
                || state.RepeatChallengeExp != previousExp
                || state.RepeatChallengeCleared != wasCleared;
        }

        private static SimulatedBattlefieldState GetState(Player player)
        {
            player.SimulatedBattlefield ??= new SimulatedBattlefieldState();
            int maxLevel = RequiredPositiveInt(Config.Value["MaxLevel"], "MaxLevel");
            if (player.SimulatedBattlefield.RepeatChallengeLevel <= 0)
                player.SimulatedBattlefield.RepeatChallengeLevel = RequiredPositiveInt(Config.Value["InitialLevel"], "InitialLevel");
            player.SimulatedBattlefield.RepeatChallengeLevel = Math.Min(player.SimulatedBattlefield.RepeatChallengeLevel, maxLevel);
            if (player.SimulatedBattlefield.RepeatChallengeExp < 0)
                player.SimulatedBattlefield.RepeatChallengeExp = RequiredInt("InitialExp");
            return player.SimulatedBattlefield;
        }

        private static List<int> ResolveMonsterLevels(int playerLevel)
        {
            JArray bands = RequiredArray("MonsterLevelBands");
            JObject? selectedBand = bands
                .OfType<JObject>()
                .FirstOrDefault(band => playerLevel <= RequiredPositiveInt(band["MaxPlayerLevel"], "MonsterLevelBands[].MaxPlayerLevel"))
                ?? bands.OfType<JObject>().LastOrDefault();
            if (selectedBand is null)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.MonsterLevelBands must not be empty.");

            JArray levels = selectedBand["MonsterLevel"] as JArray
                ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.MonsterLevelBands[].MonsterLevel must be an array.");
            List<int> result = levels
                .Select((level, index) => RequiredPositiveInt(level, $"MonsterLevelBands[].MonsterLevel[{index}]"))
                .ToList();
            if (result.Count == 0)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.MonsterLevelBands[].MonsterLevel must not be empty.");
            return result;
        }

        private static int RequiredInt(string fieldName)
        {
            return Config.Value[fieldName]?.Value<int>()
                ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.{fieldName} is required.");
        }

        private static JArray RequiredArray(string fieldName)
        {
            return Config.Value[fieldName] as JArray
                ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.{fieldName} must be an array.");
        }

        private static int RequiredPositiveInt(JToken? token, string fieldName)
        {
            int value = token?.Value<int>()
                ?? throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.{fieldName} is required.");
            if (value <= 0)
                throw new InvalidDataException($"{BattlefieldSnapshotPath}: RepeatChallenge.{fieldName} must be positive.");
            return value;
        }
    }
}
