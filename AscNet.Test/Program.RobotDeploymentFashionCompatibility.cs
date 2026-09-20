using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.fashionstory;
using AscNet.Table.V2.share.robot;
using MessagePack;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // EN XFashionManager.lua GetCharacterModelName substitutes Character.DefaultNpcFashtionId only
    // when the deployed Character.FashionId is non-positive, so the wire value must be the robot's.
    private static void ValidateRobotDeploymentFashionCompatibility()
    {
        Type fightModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule");
        MethodInfo buildRobot = RequiredMethod(fightModule, "BuildRobotDeployment",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(RobotTable)]);
        StageTable[] stages = TableReaderV2.Parse<StageTable>().ToArray();
        Dictionary<int, CharacterTable> characters = TableReaderV2.Parse<CharacterTable>().ToDictionary(row => row.Id);
        Dictionary<int, RobotTable> robots = TableReaderV2.Parse<RobotTable>().ToDictionary(row => row.Id);
        // Lowest stage whose fixed robot authors a coating other than the character default.
        StageTable stage = stages
            .Where(row => row.RobotId.Any(id => IsAuthoredRobotCoating(id, robots, characters)))
            .OrderBy(row => row.StageId)
            .First();
        int robotId = stage.RobotId.First(id => IsAuthoredRobotCoating(id, robots, characters));
        RobotTable robot = robots[robotId];
        CharacterTable character = characters[robot.CharacterId];

        const long playerId = 88_067;
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []),
            "robot-deployment-fashion-compat-test");
        const int packetId = 13_230;
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, packetId, new PreFightRequest
        {
            PreFightData = new()
            {
                StageId = (uint)stage.StageId,
                CardIds = [],
                CaptainPos = 1,
                FirstFightPos = 1
            }
        });
        PreFightResponse response = ReadResponsePayload<PreFightResponse>(
            harness, packetId, nameof(PreFightResponse), "Robot deployment PreFightResponse");
        AssertEqual(0, response.Code, "Robot deployment PreFight succeeds");

        JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(response)));
        JToken role = payload["FightData"]!["RoleData"]!.Children<JToken>()
            .Single(value => value.Value<uint>("Id") == playerId);
        JToken npc = role["NpcData"]!.Children<JProperty>()
            .Select(property => property.Value)
            .Single(value => value.Value<int>("RobotId") == robotId);
        AssertEqual((uint)robot.FashionId, npc["Character"]!.Value<uint>("FashionId"),
            "PreFight deploys the authored robot coating");
        AssertEqual((uint)robot.FashionId, npc["Character"]!["CharacterHeadInfo"]!.Value<uint>("HeadFashionId"),
            "PreFight deploys the authored robot head coating");

        // No table row pairs FashionId 0 with a real character; exercise the fallback directly.
        RobotTable fallbackRobot = new()
        {
            CharacterId = robot.CharacterId,
            CharacterLevel = robot.CharacterLevel,
            CharacterQuality = robot.CharacterQuality,
            CharacterStar = robot.CharacterStar,
            CharacterGrade = robot.CharacterGrade,
            FashionId = 0
        };
        var fallback = ((CharacterData Character, List<EquipData> Equips))buildRobot.Invoke(null, [fallbackRobot])!;
        AssertEqual((uint)character.DefaultNpcFashtionId, fallback.Character.FashionId,
            "Robot without authored coating falls back to the character default");
        AssertEqual((uint)character.DefaultNpcFashtionId, fallback.Character.CharacterHeadInfo.HeadFashionId,
            "Robot without authored coating falls back to the default head coating");

        // Fashion trial stages are activity-locked, so their authored-coating robots are asserted
        // through the same helper the PreFight path calls instead of over the wire.
        FashionStoryTable trialActivity = TableReaderV2.Parse<FashionStoryTable>()
            .OrderBy(row => row.Id)
            .First(activity => activity.TrialStages.Any(stageId => stages.Any(row =>
                row.StageId == stageId && row.RobotId.Any(id => IsAuthoredRobotCoating(id, robots, characters)))));
        RobotTable trialRobot = robots[stages
            .Where(row => trialActivity.TrialStages.Contains(row.StageId))
            .OrderBy(row => row.StageId)
            .SelectMany(row => row.RobotId)
            .First(id => IsAuthoredRobotCoating(id, robots, characters))];
        var trial = ((CharacterData Character, List<EquipData> Equips))buildRobot.Invoke(null, [trialRobot])!;
        AssertEqual((uint)trialRobot.FashionId, trial.Character.FashionId,
            "Fashion trial robot deploys the authored coating");
        AssertEqual((uint)trialRobot.FashionId, trial.Character.CharacterHeadInfo.HeadFashionId,
            "Fashion trial robot deploys the authored head coating");
    }

    private static bool IsAuthoredRobotCoating(int robotId, Dictionary<int, RobotTable> robots,
        Dictionary<int, CharacterTable> characters) =>
        robots.TryGetValue(robotId, out RobotTable? robot)
        && robot.FashionId > 0
        && characters.TryGetValue(robot.CharacterId, out CharacterTable? character)
        && character.DefaultNpcFashtionId > 0
        && robot.FashionId != character.DefaultNpcFashtionId;
}
