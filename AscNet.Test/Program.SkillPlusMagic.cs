using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fuben.assign;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// NpcData.CharacterSkillPlus (XCharacterAgency.GetSkillPlus: occupied Assign chapters) and the
    /// liberation-ball MagicIds (Character.MagicList, Config HigherLiberateLvMagicId) in a real PreFight.
    /// Oracle: quickhypertuneandgeneralcapture/0223 sent {x01,x18,x16,x23: 1} for 1221003/1021005/1051005
    /// with MagicIds {199954: 1}; 20260709/0877 sent {} MagicIds for LiberateLv 3 character 1091003.
    /// </summary>
    private static void ValidateSkillPlusAndLiberationBallPreFight()
    {
        const long playerId = 48_121;
        uint[] ids = [1221003, 1021005, 1091003];
        // CharacterSkillTypePlus rows 1..4 select skill types 1,6,4,9: the four chapters of the capture.
        List<AssignChapterTable> chapters = TableReaderV2.Parse<AssignChapterTable>();
        AssignChapterTable ChapterFor(int plusId) => chapters.First(row => row.SkillPlusId == plusId);
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters.Clear();
        foreach (uint id in ids)
            roster.AddCharacter(id, 80);
        CharacterData Owned(uint id) => roster.Characters.Single(row => row.Id == id);
        Owned(1221003).LiberateLv = 5;
        Owned(1021005).LiberateLv = 5;
        Owned(1021005).MagicList = [new CharacterSkill { Id = 199955, Level = 1 }];
        Owned(1091003).LiberateLv = 3;
        foreach (CharacterData data in roster.Characters)
            Character.EnsureLiberateBallMagic(data);

        Player player = CreateDrawCompatibilityPlayer(playerId);
        List<(string Label, int[] PlusIds, long[] Occupants)> states =
        [
            ("no chapter occupied", [], []),
            ("four capture chapters occupied", [1, 2, 3, 4], [ids[0], ids[0], ids[1], ids[1]]),
            ("one chapter occupied, one empty", [5, 1], [ids[2], 0]),
        ];
        int packetId = 12_730;
        foreach (var state in states)
        {
            player.Assign.Chapters = state.PlusIds.Select((plusId, index) => new AssignChapterState
            {
                ChapterId = ChapterFor(plusId).ChapterId,
                CharacterId = state.Occupants[index]
            }).ToList();
            using LoopbackSessionHarness harness = new(roster, player,
                CreateDrawCompatibilityInventory(playerId, []), "skill-plus-prefight");
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packetId, new PreFightRequest
            {
                PreFightData = new() { StageId = 0, CaptainPos = 1, FirstFightPos = 1, CardIds = [.. ids], RobotIds = [] }
            });
            PreFightResponse response = ReadResponsePayload<PreFightResponse>(
                harness, packetId, nameof(PreFightResponse), $"SkillPlus {state.Label}");
            JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(response)));
            var npcs = payload["FightData"]!["RoleData"]!.Single()!["NpcData"]!.Children<JProperty>()
                .Select(property => property.Value).ToList();
            foreach (uint id in ids)
            {
                JToken npc = npcs.Single(value => value["Character"]!.Value<uint>("Id") == id);
                string Map(string name) => string.Join(",", npc[name]!.Children<JProperty>()
                    .OrderBy(property => int.Parse(property.Name)).Select(property => $"{property.Name}:{property.Value}"));

                // Independent re-derivation from XCharacterAgency.GetSkillPlus / GetSkillPlusList.
                int type = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == id).Type;
                var plusRows = TableReaderV2.Parse<CharacterSkillTypePlusTable>().ToDictionary(row => row.Id);
                var skillTypes = TableReaderV2.Parse<CharacterSkillTypeTable>().ToDictionary(row => row.Id, row => row.Type);
                var groups = TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(row => row.Id);
                SortedDictionary<int, int> expected = [];
                foreach (int plusId in state.PlusIds.Where((_, index) => state.Occupants[index] != 0))
                {
                    var plus = plusRows[plusId];
                    if (!plus.CharacterType.Contains(type)) continue;
                    foreach (int groupId in TableReaderV2.Parse<CharacterSkillTable>().Single(row => row.CharacterId == id).SkillGroupId.Where(x => x != 0))
                        foreach (int skillId in groups[groupId].SkillId.Where(x => x != 0))
                            if (skillTypes.TryGetValue(skillId, out int skillType) && skillType != 0
                                && plus.SkillType.Contains(skillType)
                                && Owned(id).SkillList.Any(skill => skill.Id == skillId && skill.Level > 0))
                                expected[skillId] = expected.GetValueOrDefault(skillId) + 1;
                }
                AssertEqual(string.Join(",", expected.Select(pair => $"{pair.Key}:{pair.Value}")), Map("CharacterSkillPlus"),
                    $"SkillPlus {state.Label}: {id} CharacterSkillPlus");
                if (state.PlusIds.Length == 4)
                {
                    long skillBase = id / 10000 * 1000 + id % 10 * 100;
                    AssertEqual($"{skillBase + 1}:1,{skillBase + 16}:1,{skillBase + 18}:1,{skillBase + 23}:1",
                        Map("CharacterSkillPlus"), $"SkillPlus capture oracle shape for {id}");
                }
                AssertEqual(id == 1221003 ? "199954:1" : id == 1021005 ? "199955:1" : "", Map("MagicIds"),
                    $"Liberation ball MagicIds for {id}");
            }
        }
        AssertEqual(false, Character.EnsureLiberateBallMagic(Owned(1021005)), "Switched ball colour is never overwritten");
        AssertEqual(false, Character.EnsureLiberateBallMagic(Owned(1091003)), "LiberateLv 3 gets no ball");
    }
}
