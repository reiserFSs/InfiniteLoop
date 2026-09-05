using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.fuben.bfrt;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.equip;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateBfrtCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 48_902;
        Character roster = CreateDrawCompatibilityCharacter(uid);
        roster.Characters.Clear();
        roster.Equips.Clear();
        Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStudyProgressionCompatibility(out var stageSaves);
        using MongoCollectionOverride playerMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
        using LoopbackSessionHarness h = new(roster, player, CreateDrawCompatibilityInventory(uid, []), "bfrt-loopback");
        h.Session.stage = new Stage { Uid = uid, Stages = new() };
        BfrtChapterTable chapter = TableReaderV2.Parse<BfrtChapterTable>().First();
        var row = TableReaderV2.Parse<BfrtGroupTable>().Single(group => group.GroupId == chapter.GroupId.Last());
        int group = row.GroupId, stage = row.StageId.First();
        int requiredMembers = row.FightInfoId.Sum(id =>
            TableReaderV2.Parse<EchelonInfoTable>().Single(echelon => echelon.Id == id).NeedCharacter);
        var templates = TableReaderV2.Parse<CharacterTable>()
            .Where(character => Character.IsOwnableCharacter((uint)character.Id)).Take(requiredMembers).ToArray();
        foreach (var template in templates) roster.AddCharacter((uint)template.Id);
        uint[] ids = roster.Characters.Select(character => character.Id).ToArray();
        List<List<uint>> fightTeams = ids.Chunk(3).Select(team => team.ToList()).ToList();
        int packetId = 49_100;
        BfrtTeamSetResponse SetTeam(List<List<uint>> teams)
        {
            InvokeRegisteredRequestHandler(nameof(BfrtTeamSetRequest), h.Session, ++packetId,
                new BfrtTeamSetRequest { BfrtGroupId = group, FightTeam = teams, LogisticsTeam = [],
                    CaptainPosList = teams.Select(_ => 1).ToList(), FirstFightPosList = teams.Select(_ => 1).ToList() });
            return ReadResponsePayload<BfrtTeamSetResponse>(h, packetId, nameof(BfrtTeamSetResponse), "Bfrt team save");
        }
        void RejectClear(int expectedCode, string reason, int? chapterId = null, int? groupId = null)
        {
            InvokeRegisteredRequestHandler(nameof(BfrtOneKeyPassGroupRequest), h.Session, ++packetId,
                new BfrtOneKeyPassGroupRequest { BfrtChapterId = chapterId ?? chapter.ChapterId, BfrtGroupId = groupId ?? group });
            var response = ReadResponsePayload<BfrtOneKeyPassGroupResponse>(h, packetId, nameof(BfrtOneKeyPassGroupResponse), reason);
            AssertEqual(expectedCode, response.Code, reason);
            AssertEqual(true, response.BfrtGroupRecord is null, reason + " has no completion record");
            AssertEqual(0, player.Bfrt.Groups.Count, reason + " leaves group completion unchanged");
            AssertEqual(false, h.Session.stage.Stages.ContainsKey((uint)row.BaseStage), reason + " leaves stages unchanged");
        }
        void Call<T>(string name, int id, object request, string response) where T : class
        { InvokeRegisteredRequestHandler(name, h.Session, id, request); _ = ReadResponsePayload<T>(h, id, response, name); }
        Call<GetBfrtDataResponse>("GetBfrtDataRequest", 49_001, new GetBfrtDataRequest(), nameof(GetBfrtDataResponse));
        RejectClear(20003169, "Bfrt rejects missing saved formation");
        AssertEqual(0, SetTeam(fightTeams).Code, "Bfrt saves ordinary owned formation");
        player.Bfrt.ProgressGroupId = group;
        player.Bfrt.ProgressStageIds = [stage];
        RejectClear(20003171, "Bfrt initial table-derived power is below NeedPoint");
        AssertEqual(stage, player.Bfrt.ProgressStageIds.Single(), "Bfrt failed clear preserves pending suppression");
        RejectClear(20003028, "Bfrt rejects nonexistent group", groupId: -1);
        RejectClear(20003159, "Bfrt rejects unrelated chapter", chapterId: -1);
        AssertEqual(true, SetTeam([[uint.MaxValue]]).Code != 0, "Bfrt rejects foreign team save");
        AssertEqual(ids[0], player.Bfrt.Teams.Single().FightTeamList[0][0], "Bfrt rejected save retains prior team");
        var saved = player.Bfrt.Teams.Single();
        uint first = saved.FightTeamList[0][0];
        saved.FightTeamList[0][0] = uint.MaxValue;
        RejectClear(20009011, "Bfrt rejects stale unowned saved member without throwing");
        saved.FightTeamList[0][0] = first;
        saved.FightTeamList[0][1] = first;
        RejectClear(20003033, "Bfrt rejects duplicate saved member");
        AssertEqual(0, SetTeam(fightTeams).Code, "Bfrt restores saved formation");
        saved = player.Bfrt.Teams.Single();
        saved.FightTeamList[0][1] = 0;
        RejectClear(20003033, "Bfrt rejects insufficient occupied slots");
        AssertEqual(0, SetTeam(fightTeams).Code, "Bfrt restores full formation");
        foreach (CharacterData member in roster.Characters)
        {
            var quality = TableReaderV2.Parse<CharacterQualityTable>().Where(value => value.CharacterId == member.Id)
                .MaxBy(value => value.Quality)!;
            member.Quality = quality.Quality;
            member.Star = quality.AttrId.Count;
            member.Level = 80;
            member.Grade = TableReaderV2.Parse<CharacterGradeTable>().Where(value => value.CharacterId == member.Id).Max(value => value.Grade);
            foreach (var skill in member.SkillList)
                skill.Level = TableReaderV2.Parse<CharacterSkillUpgradeTable>().Where(value => value.SkillId == skill.Id)
                    .Select(value => value.Level).DefaultIfEmpty(skill.Level).Max();
            var template = templates.Single(value => value.Id == member.Id);
            foreach (var equip in roster.Equips.Where(equip => equip.CharacterId == member.Id)) equip.CharacterId = 0;
            var weapon = TableReaderV2.Parse<EquipTable>()
                .Where(value => value.Site == 0 && value.Type == template.EquipType && Character.IsOwnableEquipTemplate(value))
                .OrderByDescending(value => value.Quality).First();
            roster.AddEquip((uint)weapon.Id, (int)member.Id);
            for (int site = 1; site <= 6; site++)
            {
                var memory = TableReaderV2.Parse<EquipTable>()
                    .Where(value => value.Site == site && value.CharacterType == template.Type && Character.IsOwnableEquipTemplate(value))
                    .OrderByDescending(value => value.Quality).First();
                roster.AddEquip((uint)memory.Id, (int)member.Id);
            }
        }
        foreach (var equip in roster.Equips.Where(equip => equip.CharacterId > 0))
        {
            var breakthrough = TableReaderV2.Parse<EquipBreakThroughTable>()
                .Where(value => value.EquipId == equip.TemplateId).MaxBy(value => value.Times)!;
            equip.Breakthrough = breakthrough.Times;
            equip.Level = breakthrough.LevelLimit;
        }
        AssertEqual(true, roster.Characters.All(member => member.Ability == 0), "Bfrt power never relies on persisted Ability");
        AssertEqual(0, SetTeam(fightTeams).Code, "Bfrt upgraded team save precedes quick clear");
        Call<BfrtResetGroupStageResponse>(nameof(BfrtResetGroupStageRequest), ++packetId,
            new BfrtResetGroupStageRequest { IsClear = true }, nameof(BfrtResetGroupStageResponse));
        AssertEqual(0, player.Bfrt.ProgressGroupId, "Bfrt optional reset clears pending group");
        AssertEqual(0, player.Bfrt.ProgressStageIds.Count, "Bfrt optional reset clears pending stages");
        InvokeRegisteredRequestHandler(nameof(BfrtOneKeyPassGroupRequest), h.Session, 49_004,
            new BfrtOneKeyPassGroupRequest { BfrtChapterId = chapter.ChapterId, BfrtGroupId = group });
        _ = ReadPushPayload<NotifyTask>(h, nameof(NotifyTask), "Bfrt quick-clear task progress");
        BfrtOneKeyPassGroupResponse quickClear = ReadResponsePayload<BfrtOneKeyPassGroupResponse>(
            h, 49_004, nameof(BfrtOneKeyPassGroupResponse), "Bfrt quick-clear response");
        AssertEqual(0, quickClear.Code, "Bfrt quick-clear code");
        AssertEqual(1, quickClear.BfrtGroupRecord?.Count ?? 0, "Bfrt quick-clear group count");
        AssertEqual(true, roster.Characters.All(member => member.Ability == 0), "Bfrt successful computed clear does not persist a power cache");
        AssertEqual(0, player.Bfrt.ProgressStageIds.Count, "Bfrt successful clear has no pending suppression");
        AssertEqual(true, h.Session.stage.Stages.TryGetValue((uint)row.BaseStage, out StageDatum? clearedStage)
            && clearedStage.Passed, "Bfrt quick-clear tracks chapter task stage");
        h.Session.stage.Stages.Remove((uint)row.BaseStage);
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"),
            "ReconcileTaskStages", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [h.Session]);
        AssertEqual(true, h.Session.stage.Stages.TryGetValue((uint)row.BaseStage, out clearedStage)
            && clearedStage.Passed, "Bfrt login repairs existing chapter task progress");
        AssertEqual(0, SetTeam(fightTeams).Code, "Bfrt team save also supports clear without reset");
        InvokeRegisteredRequestHandler(nameof(BfrtOneKeyPassGroupRequest), h.Session, ++packetId,
            new BfrtOneKeyPassGroupRequest { BfrtChapterId = chapter.ChapterId, BfrtGroupId = group });
        _ = ReadPushPayload<NotifyTask>(h, nameof(NotifyTask), "Bfrt repeat quick-clear task progress");
        var repeatedClear = ReadResponsePayload<BfrtOneKeyPassGroupResponse>(
            h, packetId, nameof(BfrtOneKeyPassGroupResponse), "Bfrt no-reset quick-clear");
        AssertEqual(0, repeatedClear.Code, "Bfrt no-reset quick-clear succeeds");
        AssertEqual(2, repeatedClear.BfrtGroupRecord?.Count ?? 0, "Bfrt preserves repeat completion semantics");
        player.Bfrt.ProgressGroupId = group;
        player.Bfrt.ProgressStageIds = [stage];
        Call<BfrtResetGroupStageResponse>("BfrtResetGroupStageRequest", 49_005, new BfrtResetGroupStageRequest { BfrtStageId = stage }, nameof(BfrtResetGroupStageResponse));
        Call<BfrtReceiveCourseRewardResponse>("BfrtReceiveCourseRewardRequest", 49_006, new BfrtReceiveCourseRewardRequest(), nameof(BfrtReceiveCourseRewardResponse));
        Call<BfrtReceiveChapterGroupRewardResponse>("BfrtReceiveChapterGroupRewardRequest", 49_007, new BfrtReceiveChapterGroupRewardRequest { BfrtChapterId = -1, BfrtGroupId = -1 }, nameof(BfrtReceiveChapterGroupRewardResponse));
        MethodInfo authorize = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"),
            "TryAuthorizePreFight", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(Player), typeof(uint), typeof(int).MakeByRefType()]);
        object?[] authorizeArgs = [player, (uint)stage, 0];
        AssertEqual(true, (bool)authorize.Invoke(null, authorizeArgs)!, "Bfrt configured stage is authorized");
        AssertEqual(0, (int)authorizeArgs[2]!, "Bfrt configured stage authorization code");
        object?[] baseStageArgs = [player, (uint)row.BaseStage, 0];
        AssertEqual(true, (bool)authorize.Invoke(null, baseStageArgs)!, "Bfrt base mission stage is authorized");
        AssertEqual(0, (int)baseStageArgs[2]!, "Bfrt base mission authorization code");
        NotifyBfrtData login = BfrtModuleLogin(player);
        AssertEqual(1, login.BfrtData.BfrtTeamInfos.Count, "Bfrt team survives login projection");
        Player reloaded = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
            (playerSaves.LastReplacement ?? throw new InvalidDataException("Bfrt did not persist player state.")).ToBson());
        AssertEqual(player.Bfrt.Teams.Count, reloaded.Bfrt.Teams.Count, "Bfrt state survives relogin");
        AssertEqual(2, reloaded.Bfrt.Groups.Single(value => value.Id == group).Count, "Bfrt completed group survives reload");
        Stage reloadedStages = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
            (stageSaves.LastReplacement ?? throw new InvalidDataException("Bfrt did not persist stage state.")).ToBson());
        AssertEqual(2L, reloadedStages.Stages[(uint)row.BaseStage].PassTimesTotal, "Bfrt completions persist stage clear count");
        byte[] wire = MessagePackSerializer.Serialize(login);
        _ = MessagePackSerializer.Deserialize<NotifyBfrtData>(wire);
        Console.WriteLine("Bfrt compatibility: endpoints, validation, one-key/reset, login, and relogin passed.");
    }
    private static NotifyBfrtData BfrtModuleLogin(Player player) =>
        (NotifyBfrtData)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BfrtModule"), "BuildLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, [typeof(Player)]).Invoke(null, [player])!;
}
