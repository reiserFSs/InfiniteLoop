using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// 4.9 teaching activity 53 (Leia, stages 30100249-254): the client sends an empty team for a stage that
    /// carries its own robots (XFubenNewCharActivityManager.PreFight), so the PreFight FightData must be complete
    /// on its own: configured robots (TeachingRobot, else Stage.RobotId) deployed in order, every id resolvable in
    /// the Robot/Character/Equip/Fashion tables the client loads the NPC from, the stage's RebootId authored, and
    /// the stage owned by the 4.9 Study catalog (study_compatibility_4.9.1.json).
    /// </summary>
    private static void ValidateTeachingStage49FightData()
    {
        const int leiaActivityId = 53;
        const int leiaCharacterId = 1_431_003;
        JObject compatibility = JsonSnapshot.LoadObject("Configs/study_compatibility_4.9.1.json");
        JObject activity = (JObject)((JArray)compatibility["TeachingActivities"]!).Single(row => row.Value<int>("Id") == leiaActivityId);
        List<uint> stageIds = activity["StageId"]!.Values<uint>().Concat(activity["ChallengeStage"]!.Values<uint>()).ToList();
        AssertEqual(6, stageIds.Count, "4.9 Leia teaching activity stage count");
        AssertEqual(30_100_249u, stageIds[0], "4.9 Leia first teaching stage");

        Dictionary<int, CharacterTable> characters = TableReaderV2.Parse<CharacterTable>().ToDictionary(row => row.Id);
        Dictionary<int, EquipTable> equips = TableReaderV2.Parse<EquipTable>().ToDictionary(row => row.Id);
        Dictionary<int, FashionTable> fashions = TableReaderV2.Parse<FashionTable>().ToDictionary(row => row.Id);
        Dictionary<int, RobotTable> robots = TableReaderV2.Parse<RobotTable>().ToDictionary(row => row.Id);
        HashSet<int> rebootIds = TableReaderV2.Parse<FightRebootTable>().Select(row => row.Id).ToHashSet();
        Dictionary<int, int[]> teachingRobots = ((JArray)compatibility["TeachingRobots"]!)
            .ToDictionary(row => row.Value<int>("StageId"), row => row["RobotId"]!.Values<int>().Where(id => id > 0).ToArray());

        foreach (uint stageId in stageIds)
        {
            string name = $"4.9 teaching stage {stageId}";
            JObject stage = (JObject)((JArray)compatibility["Stages"]!).Single(row => row.Value<uint>("StageId") == stageId);
            int[] configured = teachingRobots.TryGetValue((int)stageId, out int[]? teaching) && teaching.Length > 0
                ? teaching
                : stage["RobotId"]!.Values<int>().Where(id => id > 0).ToArray();
            AssertEqual(true, configured.Length > 0, $"{name} configures robots");
            AssertEqual(true, CurrentClientStudyTables.TryGetStage(stageId, out _), $"{name} is owned by the 4.9 Study catalog");

            PreFightResponse fight = AssertStudyStageRobotDeployments(
                stageId, [], [], configured.Select(id => (robots[id].CharacterId, id)).ToList(),
                1_021_001, name, stage.Value<int>("RebootId"));
            var fightData = fight.FightData!;
            if (stage.Value<int>("RebootId") > 0)
                AssertEqual(true, rebootIds.Contains(fightData.RebootId), $"{name} RebootId {fightData.RebootId} exists in FightReboot");

            foreach (JObject npc in fightData.RoleData.SelectMany(role => role.NpcData.Values).Select(value => JObject.FromObject((object)value)))
            {
                int robotId = npc["RobotId"]!.Value<int>();
                RobotTable robot = robots[robotId];
                string npcName = $"{name} robot {robotId}";
                AssertEqual(true, characters.ContainsKey(robot.CharacterId), $"{npcName} character exists");
                JObject character = (JObject)npc["Character"]!;
                // Client contract (XRobot:GenarateRobotSkillList): exactly the first skill of every skill group,
                // minus RemoveSkillId. A switch group's alternate (Leia 143328) must not be deployed beside it.
                CharacterSkillTable characterSkills = TableReaderV2.Parse<CharacterSkillTable>().Single(row => row.CharacterId == robot.CharacterId);
                List<int> expectedSkills = characterSkills.SkillGroupId.Where(id => id > 0)
                    .Select(id => TableReaderV2.Parse<CharacterSkillGroupTable>().Single(row => row.Id == id).SkillId.First())
                    .Where(id => !robot.RemoveSkillId.Contains(id)).Order().ToList();
                AssertEqual(true, expectedSkills.Count > 0, $"{npcName} has skills to deploy");
                AssertIntegerList(expectedSkills.Select(id => (long)id).ToArray(),
                    ((JArray)character["SkillList"]!).Select(skill => (long)skill["Id"]!.Value<int>()).Order().ToArray(),
                    $"{npcName} deploys one skill per group");
                AssertEqual(robot.FashionId, character["FashionId"]!.Value<int>(), $"{npcName} fashion");
                AssertEqual(true, fashions.TryGetValue(robot.FashionId, out FashionTable? fashion) && fashion.CharacterId == robot.CharacterId,
                    $"{npcName} fashion {robot.FashionId} belongs to its character");
                JArray deployedEquips = (JArray)npc["Equips"]!;
                AssertEqual(true, equips.ContainsKey(robot.WeaponId), $"{npcName} weapon exists");
                AssertEqual(1 + robot.WaferId.Count, deployedEquips.Count, $"{npcName} equip count");
                foreach (JToken equip in deployedEquips)
                    AssertEqual(true, equips.ContainsKey(equip["TemplateId"]!.Value<int>()), $"{npcName} equip {equip["TemplateId"]} exists");
            }
        }

        // The Leia stages must deploy her frame, not the roster team (the client sends no team for them).
        AssertStudyStageRobotDeployment(30_100_249, [], [], leiaCharacterId, 2_343, 1_021_001, "4.9 Leia teaching stage 30100249", expectedRebootId: 3);
        AssertEqual(false, ((JArray)JObject.FromObject((object)AssertStudyStageRobotDeployment(30_100_249, [], [], leiaCharacterId, 2_343, 1_021_001, "4.9 Leia skills")
            .FightData!.RoleData.Single(role => role.Id == 88_031).NpcData[0])["Character"]!["SkillList"]!).Any(skill => skill["Id"]!.Value<int>() == 143_328),
            "4.9 Leia deploys 143321 only, not its switch alternate 143328");
        AssertStudyStageRobotDeployment(30_100_253, [], [], leiaCharacterId, 2_344, 1_021_001, "4.9 Leia challenge stage 30100253");
    }
}
