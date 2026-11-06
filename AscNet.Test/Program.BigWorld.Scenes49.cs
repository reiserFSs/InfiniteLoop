using AscNet.Common.Util;
using AscNet.Table.V2.share.dlcworld.questsystem.environmentquest;
using AscNet.Table.V2.share.statussyncfight.level;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using AscNet.Table.V2.share.statussyncfight.npc;
using AscNet.Table.V2.share.statussyncfight.quest;
using AscNet.Table.V2.share.statussyncfight.scenecollider;
using AscNet.Table.V2.share.statussyncfight.sceneobject;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-49-only: the 4.9 Babylonia data the server serves. The 4.9 client ships NewScene0700 map/collider/static-object
        // tables (Level01-08, NewScene070001-7) with no Level row, scene config group, Lua reference or scene asset, so there is no entry
        // path to serve; the audit pins that, and that every actor/pin/collider a reachable level replicates resolves in the 4.9 tables.
        private static void ValidateBigWorld49Scenes()
        {
            List<LevelTable> levels = TableReaderV2.Parse<LevelTable>();
            AssertEqual("", string.Join(",", levels.Where(l => $"{l.Prefab}|{l.SectorName}".Contains("newscene07", StringComparison.OrdinalIgnoreCase)).Select(l => l.Id)),
                "no Level row enters the unshipped NewScene0700 scenes (add its importers/handlers when one does)");
            AssertEqual("", string.Join(",", TableReaderV2.Parse<SceneColliderTable>().Select(c => c.Sector).Distinct().Where(s => !s.StartsWith("skygarden"))),
                "SceneCollider holds BigWorld (skygarden*) sectors only");

            // Retail 4.9 QuestData: every environment (Encounter Journal) quest is popup-shielded; 4.8 data had 7 of them at 0.
            Dictionary<int, DlcQuestObjectiveTable> objectives = TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(o => o.Id);
            Dictionary<int, DlcQuestTable> quests = TableReaderV2.Parse<DlcQuestTable>().ToDictionary(q => q.Id);
            List<int> envQuests = TableReaderV2.Parse<DlcEnvironmentQuestTable>().SelectMany(e => e.ObjectiveIds).SelectMany(ids => $"{ids}".Split('|'))
                .Select(id => objectives[int.Parse(id)].QuestId).Distinct().OrderBy(id => id).ToList();
            AssertEqual(true, envQuests.Count >= 14, "environment quests resolved");
            AssertEqual("", string.Join(",", envQuests.Where(id => quests[id].ShieldPopViewType != 3)), "environment quests with ShieldPopViewType != 3");

            // Referential integrity of what a level replicates.
            HashSet<int> fightNpcs = TableReaderV2.Parse<NpcTable>().Select(n => n.Id).ToHashSet();
            Dictionary<int, LevelNpcBaseTable> npcBases = TableReaderV2.Parse<LevelNpcBaseTable>().ToDictionary(b => b.Id);
            AssertEqual("", string.Join(",", TableReaderV2.Parse<LevelNpcTable>().Where(n => !npcBases.TryGetValue(n.BaseId, out var b) || !fightNpcs.Contains(b.FightNpcId))
                .Select(n => $"{n.Sector}/{n.ConfigGroupId}/{n.PlaceId}")), "LevelNpc without LevelNpcBase/Npc");
            HashSet<int> objectBases = TableReaderV2.Parse<SceneObjectBaseTable>().Select(b => b.Id).ToHashSet();
            AssertEqual("", string.Join(",", TableReaderV2.Parse<LevelSceneObjectTable>().Where(o => !objectBases.Contains(o.BaseId)).Select(o => $"{o.Sector}/{o.ConfigGroupId}/{o.PlaceId}")),
                "LevelSceneObject without SceneObjectBase");
            HashSet<int> levelIds = levels.Select(l => l.Id).ToHashSet();
            AssertEqual("", string.Join(",", TableReaderV2.Parse<LevelMapPinTable>().Where(p => p.TeleportLevelId != 0 && !levelIds.Contains(p.TeleportLevelId)).Select(p => p.PinId)),
                "map pins teleporting to a missing level");
            Console.WriteLine($"big world 4.9 scenes: ok ({envQuests.Count} environment quests, {levels.Count} levels)");
        }
    }
}
