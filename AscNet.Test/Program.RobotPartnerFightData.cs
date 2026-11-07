using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.partner;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.character;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // XRobot:InitPartner gives a premade robot the CUB of Robot.RobotPartnerId (RobotPartner.tab). The 4.9
    // Adaptation Fitting stages (Zafkiel 30101135, Kelpie 30101149, Vulpecula 30101152) are single-robot stages
    // whose only CUB comes from that row; PreFight used to send Partner = null, so the CUB never spawned.
    private static void ValidateRobotPartnerFightData()
    {
        Dictionary<int, RobotTable> robots = TableReaderV2.Parse<RobotTable>().ToDictionary(row => row.Id);
        Dictionary<int, RobotPartnerTable> robotPartners = TableReaderV2.Parse<RobotPartnerTable>().ToDictionary(row => row.Id);
        HashSet<int> partnerIds = TableReaderV2.Parse<PartnerTable>().Select(row => row.Id).ToHashSet();
        Dictionary<int, PartnerMainSkillGroupTable> mainGroups = TableReaderV2.Parse<PartnerMainSkillGroupTable>().ToDictionary(row => row.Id);
        HashSet<int> passiveSkillIds = TableReaderV2.Parse<PartnerPassiveSkillGroupTable>().Select(row => row.SkillId).ToHashSet();
        Dictionary<int, CharacterTable> characters = TableReaderV2.Parse<CharacterTable>().ToDictionary(row => row.Id);

        // Every authored robot CUB resolves to valid ids and skills.
        List<RobotTable> withPartner = robots.Values.Where(row => Convert.ToInt32(row.RobotPartnerId) > 0).ToList();
        AssertEqual(true, withPartner.Count >= 44, "4.9 authors robots with a CUB");
        foreach (RobotTable robot in withPartner)
            AssertRobotPartner(robot, robot.CharacterId, Convert.ToInt32(robot.RobotPartnerId),
                BuildRobotPartnerForTest(robot), $"robot {robot.Id}");

        const long playerId = 88_068;
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []),
            "robot-partner-fight-data-test");
        int packetId = 13_240;
        // Three Adaptation Fitting stages plus a teaching stage whose robot carries a CUB (Reject and Accept, robot 9085).
        foreach (uint stageId in new uint[] { 30_101_135, 30_101_149, 30_101_152, 30_101_008 })
        {
            StageTable stage = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == stageId);
            RobotTable robot = robots[stage.RobotId.First(id => Convert.ToInt32(robots[id].RobotPartnerId) > 0)];
            packetId++;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, packetId, new PreFightRequest
            {
                PreFightData = new() { StageId = stageId, CardIds = [], CaptainPos = 1, FirstFightPos = 1 }
            });
            PreFightResponse response = ReadResponsePayload<PreFightResponse>(
                harness, packetId, nameof(PreFightResponse), $"Robot CUB PreFightResponse {stageId}");
            AssertEqual(0, response.Code, $"Stage {stageId} PreFight succeeds");

            JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(response)));
            JToken npc = payload["FightData"]!["RoleData"]!.Children<JToken>()
                .Single(value => value.Value<uint>("Id") == playerId)["NpcData"]!.Children<JProperty>()
                .Select(property => property.Value)
                .Single(value => value.Value<int>("RobotId") == robot.Id);
            JToken partner = npc["Partner"]!;
            AssertEqual(true, partner.Type == JTokenType.Object, $"Stage {stageId} deploys the robot CUB");
            AssertRobotPartner(robot, robot.CharacterId, Convert.ToInt32(robot.RobotPartnerId),
                partner.ToObject<PartnerData>()!, $"stage {stageId}");
        }

        void AssertRobotPartner(RobotTable robot, int characterId, int robotPartnerId, PartnerData? partner, string name)
        {
            AssertEqual(true, partner is not null, $"{name} has a CUB");
            RobotPartnerTable row = robotPartners[robotPartnerId];
            AssertEqual(robotPartnerId, partner!.Id, $"{name} CUB Id is the RobotPartner row");
            AssertEqual(row.PartnerId, partner.TemplateId, $"{name} CUB TemplateId");
            AssertEqual(true, partnerIds.Contains(partner.TemplateId), $"{name} CUB template exists in Partner");
            AssertEqual(characterId, partner.CharacterId, $"{name} CUB CharacterId");
            AssertEqual(row.Level, partner.Level, $"{name} CUB level");
            AssertEqual(row.BreakThrough, partner.BreakThrough, $"{name} CUB breakthrough");
            AssertEqual(row.Quality, partner.Quality, $"{name} CUB quality");
            AssertEqual(row.StarSchedule, partner.StarSchedule, $"{name} CUB star schedule");
            PartnerSkillData main = partner.SkillList.Single(skill => skill.Type == 1);
            PartnerMainSkillGroupTable group = mainGroups[row.MainSkillGroup];
            AssertEqual(group.SkillId[group.Element.IndexOf(characters[characterId].Element)], main.Id, $"{name} CUB main skill matches the character element");
            AssertEqual(row.MainSkillLevel, main.Level, $"{name} CUB main skill level");
            AssertEqual(true, main.IsWear, $"{name} CUB main skill is worn");
            AssertEqual(main.Id, partner.UnlockSkillGroup.Single(), $"{name} CUB unlocks its main skill");
            foreach (PartnerSkillData passive in partner.SkillList.Where(skill => skill.Type == 2))
            {
                AssertEqual(true, passiveSkillIds.Contains(passive.Id), $"{name} CUB passive {passive.Id} exists");
                AssertEqual(true, passive.IsWear && passive.Level >= 1, $"{name} CUB passive {passive.Id} is worn at a valid level");
            }
            AssertEqual(row.PassiveSkillId.Count(id => id > 0), partner.SkillList.Count(skill => skill.Type == 2), $"{name} CUB passive count");
        }
    }

    private static PartnerData? BuildRobotPartnerForTest(RobotTable robot)
        => (PartnerData?)RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"), "BuildRobotPartner",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(RobotTable)])
            .Invoke(null, [robot]);
}
