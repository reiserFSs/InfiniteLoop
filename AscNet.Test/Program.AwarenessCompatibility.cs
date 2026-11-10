using System.Collections;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.exhibition;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateAwarenessCompatibility()
    {
        Type chapterType = AwarenessType("AwarenessChapterTable");
        Type teamType = AwarenessType("AwarenessTeamInfoTable");
        object[] chapters = ParseTableRows(chapterType, "Awareness chapters")
            .Where(row => AwarenessInts(row, "StageId").Count == AwarenessInts(row, "TeamInfoId").Count && AwarenessInts(row, "StageId").Count > 0)
            .OrderBy(row => AwarenessInt(row, "Site")).ToArray();
        if (chapters.Length < 2)
            throw new InvalidDataException("Awareness compatibility requires two configured chapters.");
        object[] selected = chapters.Take(2).ToArray();
        Dictionary<int, int> needs = ParseTableRows(teamType, "Awareness teams")
            .ToDictionary(row => AwarenessInt(row, "Id"), row => AwarenessInt(row, "NeedCharacter"));
        foreach (object chapter in selected)
            foreach (int teamId in AwarenessInts(chapter, "TeamInfoId"))
                if (!needs.TryGetValue(teamId, out int need) || need <= 0)
                    throw new InvalidDataException($"Awareness chapter {AwarenessInt(chapter, "Id")} has no usable team definition.");

        Type getRequest = AwarenessType("AwarenessGetDataRequest");
        Type getResponse = AwarenessType("AwarenessGetDataResponse");
        Type setCharacterRequest = AwarenessType("AwarenessSetCharacterRequest");
        Type setCharacterResponse = AwarenessType("AwarenessSetCharacterResponse");
        Type setTeamRequest = AwarenessType("AwarenessSetTeamRequest");
        Type setTeamResponse = AwarenessType("AwarenessSetTeamResponse");
        Type resetRequest = AwarenessType("AwarenessResetStageRequest");
        Type resetResponse = AwarenessType("AwarenessResetStageResponse");
        Type notify = AwarenessType("NotifyLoginAwarenessInfo");
        Type info = AwarenessType("AwarenessInfo");
        Type chapterRecord = AwarenessType("AwarenessChapterInfo");
        Type challengeRecord = AwarenessType("AwarenessChallengeInfo");
        Type teamRecord = AwarenessType("AwarenessTeamInfo");

        AssertMailNamedMapKeys(getRequest, null, [], "AwarenessGetDataRequest");
        AssertMailNamedMapKeys(getResponse, null, ["AwarenessInfo"], "AwarenessGetDataResponse");
        AssertMailNamedMapKeys(setCharacterRequest, null, ["ChapterId", "CharacterId"], "AwarenessSetCharacterRequest");
        AssertMailNamedMapKeys(setCharacterResponse, null, ["Code"], "AwarenessSetCharacterResponse");
        AssertMailNamedMapKeys(setTeamRequest, null, ["TeamList", "FirstFightPosList", "ChapterId", "CaptainPosList"], "AwarenessSetTeamRequest");
        AssertMailNamedMapKeys(setTeamResponse, null, ["Code"], "AwarenessSetTeamResponse");
        AssertMailNamedMapKeys(resetRequest, null, ["ChapterId", "StageId"], "AwarenessResetStageRequest");
        AssertMailNamedMapKeys(resetResponse, null, ["Code"], "AwarenessResetStageResponse");
        AssertMailNamedMapKeys(notify, null, ["AwarenessInfo"], "NotifyLoginAwarenessInfo");
        AssertMailNamedMapKeys(info, null, ["ChapterRecords", "ChallengeRecords", "TeamRecords"], "AwarenessInfo");
        AssertMailNamedMapKeys(chapterRecord, null, ["ChapterId", "CharacterId"], "Awareness chapter record");
        AssertMailNamedMapKeys(challengeRecord, null, ["ChapterId", "Count", "FinishStageIds"], "Awareness challenge record");
        AssertMailNamedMapKeys(teamRecord, null, ["ChapterId", "TeamInfoList", "CaptainPosList", "FirstFightPosList"], "Awareness team record");
        foreach (string request in new[] { "AwarenessGetDataRequest", "AwarenessSetCharacterRequest", "AwarenessSetTeamRequest", "AwarenessResetStageRequest" })
            AssertEqual("AscNet.GameServer.Handlers.AwarenessModule", GetRegisteredRequestHandlerMethod(request).DeclaringType?.FullName, $"{request} handler registration");

        int chapterId = AwarenessInt(selected[0], "Id");
        int secondChapterId = AwarenessInt(selected[1], "Id");
        int stageId = AwarenessInts(selected[0], "StageId")[0];
        List<List<int>> team = AwarenessTeam(selected[0], needs);
        ExhibitionRewardTable growthReward = TableReaderV2.Parse<ExhibitionRewardTable>().First(row => row.LevelId >= 5);
        int characterId = growthReward.CharacterId;
        int requiredCharacters = team.SelectMany(row => row).Distinct().Count();
        uint[] owned = TableReaderV2.Parse<CharacterTable>().Select(row => (uint)row.Id).Where(id => id > 0).Distinct().Take(Math.Max(requiredCharacters, 12)).Append((uint)characterId).Distinct().ToArray();
        if (owned.Length < requiredCharacters)
            throw new InvalidDataException("Awareness compatibility requires enough character table rows.");
        int next = 0;
        team = team.Select(row => row.Select(_ => (int)owned[next++ % owned.Length]).ToList()).ToList();
        Character roster = CreateDrawCompatibilityCharacter(47_101);
        List<CharacterData> rosterCharacters = owned.Select(id => new CharacterData { Id = id }).ToList();
        CharacterData selectable = rosterCharacters.Single(row => row.Id == characterId);
        selectable.Level = 100;
        selectable.LiberateLv = 0;
        roster.Characters = rosterCharacters;
        roster.Equips = TableReaderV2.Parse<EquipTable>().Where(row => row.Site is >= 1 and <= 6).GroupBy(row => row.Site).Select(group => group.First()).Select((row, index) => new EquipData
        {
            Id = (uint)(47_200 + index),
            TemplateId = (uint)row.Id,
            CharacterId = characterId,
            AwakeSlotList = [1, 2],
            ResonanceInfo = [new ResonanceInfo { Slot = 1, CharacterId = characterId }, new ResonanceInfo { Slot = 2, CharacterId = characterId }]
        }).ToList();
        Player player = CreateDrawCompatibilityPlayer(47_101);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AwarenessModule");
        MethodInfo buildLogin = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player)]);
        object loginAwareness = buildLogin.Invoke(null, [player])!;
        AwarenessAssertConfigured(loginAwareness, chapters, "NotifyLoginAwarenessInfo initial snapshot");
        AssertIntegerList([], AwarenessChapterIds(loginAwareness).Select(Convert.ToInt64).ToArray(), "NotifyLoginAwarenessInfo initial ChapterRecords");

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(roster, player, CreateDrawCompatibilityInventory(47_101, []), "awareness-compat");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(47_101);

        object initial = AwarenessCall(harness, "AwarenessGetDataRequest", 47_101, Activator.CreateInstance(getRequest)!, getResponse);
        AwarenessAssertConfigured(initial, chapters, "initial AwarenessGetData");
        AssertIntegerList([], AwarenessChapterIds(initial).Select(Convert.ToInt64).ToArray(), "initial AwarenessGetData ChapterRecords");
        AwarenessChallengeState challenge = new() { ChapterId = chapterId, Count = 0, FinishStageIds = [stageId] };
        player.Awareness.Challenges.Add(challenge);
        object partial = AwarenessCall(harness, "AwarenessGetDataRequest", 47_1000, Activator.CreateInstance(getRequest)!, getResponse);
        AssertIntegerList([], AwarenessChapterIds(partial).Select(Convert.ToInt64).ToArray(), "partial Awareness ChapterRecords");
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_1001,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", characterId)), setCharacterResponse), setCharacterResponse, 20182012, "partial Awareness deployment rejected");
        challenge.Count = 1;
        challenge.FinishStageIds.Clear();
        player.GatherRewards.Add(growthReward.Id);
        int savesBeforeCharacter = saves.ReplaceOneCalls;
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_102,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", characterId)), setCharacterResponse), setCharacterResponse, 0, "set owned character");
        object completed = AwarenessCall(harness, "AwarenessGetDataRequest", 47_1002, Activator.CreateInstance(getRequest)!, getResponse);
        AssertIntegerList([chapterId], AwarenessChapterIds(completed).Select(Convert.ToInt64).ToArray(), "completed Awareness ChapterRecords");
        AssertEqual(characterId, AwarenessInt(((IEnumerable)AwarenessValue(AwarenessValue(completed, "AwarenessInfo"), "ChapterRecords")).Cast<object>().Single(), "CharacterId"), "completed Awareness character");
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_103,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", 0)), setCharacterResponse), setCharacterResponse, 0, "clear character");
        int savesBeforeDuplicateClear = saves.ReplaceOneCalls;
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_104,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", 0)), setCharacterResponse), setCharacterResponse, 0, "duplicate clear");
        AssertEqual(savesBeforeDuplicateClear, saves.ReplaceOneCalls, "duplicate Awareness character clear does not persist");
        AssertEqual(true, saves.ReplaceOneCalls >= savesBeforeCharacter + 2, "Awareness character mutations persist");
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_105,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", characterId)), setCharacterResponse), setCharacterResponse, 0, "restore owned character");

        object validTeam = AwarenessRequest(setTeamRequest, ("ChapterId", chapterId), ("TeamList", team),
            ("CaptainPosList", Enumerable.Repeat(1, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList()));
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetTeamRequest", 47_106, validTeam, setTeamResponse), setTeamResponse, 0, "set valid Awareness team");
        object persisted = AwarenessCall(harness, "AwarenessGetDataRequest", 47_107, Activator.CreateInstance(getRequest)!, getResponse);
        AwarenessAssertTeam(persisted, chapterId, team, "saved Awareness team");

        StrongholdModule.Reconcile(harness.Session); // PreFight reconciles Stronghold first; settle it so the snapshots below compare only Awareness effects.
        byte[] protectedState = player.ToBson();
        foreach ((string name, object request) invalid in new[]
        {
            ("chapter", AwarenessRequest(setTeamRequest, ("ChapterId", secondChapterId + 1_000_000), ("TeamList", team), ("CaptainPosList", Enumerable.Repeat(1, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList()))),
            ("dimensions", AwarenessRequest(setTeamRequest, ("ChapterId", chapterId), ("TeamList", team.Take(team.Count - 1).ToList()), ("CaptainPosList", Enumerable.Repeat(1, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList()))),
            ("duplicate", AwarenessRequest(setTeamRequest, ("ChapterId", chapterId), ("TeamList", team.Select(row => row.Select((id, index) => index == 1 ? row[0] : id).ToList()).ToList()), ("CaptainPosList", Enumerable.Repeat(1, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList()))),
            ("position", AwarenessRequest(setTeamRequest, ("ChapterId", chapterId), ("TeamList", team), ("CaptainPosList", Enumerable.Repeat(0, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList()))),
            ("ownership", AwarenessRequest(setTeamRequest, ("ChapterId", chapterId), ("TeamList", team.Select(row => row.Select((id, index) => index == 0 ? int.MaxValue : id).ToList()).ToList()), ("CaptainPosList", Enumerable.Repeat(1, team.Count).ToList()), ("FirstFightPosList", Enumerable.Repeat(1, team.Count).ToList())))
        })
        {
            AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetTeamRequest", 47_110 + invalid.name.Length, invalid.request, setTeamResponse), setTeamResponse, 0, $"reject invalid Awareness team {invalid.name}", expectedSuccess: false);
            AssertEqual(Convert.ToHexString(protectedState), Convert.ToHexString(player.ToBson()), $"invalid Awareness team {invalid.name} does not mutate player");
        }

        int equippedId = team[0][0]; // the deployed character wearing the awakened equipment (the guard occupant need not be deployed)
        foreach (EquipData equip in harness.Session.character.Equips) equip.CharacterId = equippedId;
        PreFightRequest mismatch = new() { PreFightData = new() { StageId = (uint)stageId, CardIds = team[0].AsEnumerable().Reverse().Select(id => (uint)id).ToList(), CaptainPos = 1, FirstFightPos = 1 } };
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 47_120, mismatch);
        AssertEqual(true, ReadResponsePayload<PreFightResponse>(harness, 47_120, nameof(PreFightResponse), "Awareness mismatched pre-fight").Code != 0, "Awareness pre-fight rejects unsaved team");
        AssertEqual(Convert.ToHexString(protectedState), Convert.ToHexString(player.ToBson()), "Awareness pre-fight mismatch does not mutate player");
        AssertEqual(null, harness.Session.fight, "Awareness mismatch does not start fight");
        PreFightRequest preFight = new() { PreFightData = new() { StageId = (uint)stageId, CardIds = team[0].Select(id => (uint)id).ToList(), CaptainPos = 1, FirstFightPos = 1 } };
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 47_121, preFight);
        PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(harness, 47_121, nameof(PreFightResponse), "Awareness saved-team pre-fight");
        AssertEqual(0, preFightResponse.Code, "Awareness saved-team pre-fight code");
        // Hypertuned Memory: the occupied chapter is the only set position; its grid event level is 1 + awakened slots of that character's own equip at the chapter Site.
        int occupiedEvent = AwarenessInt(selected[0], "FightEventId");
        AwarenessAssertHypertuned(harness, preFightResponse, equippedId, 3, [chapterId], [occupiedEvent], "occupied, 2 awakened slots");

        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, 47_122, CreateMissingStageSettleRequest((uint)stageId, preFightResponse.FightData.FightId, 47_101));
        FightSettleResponse settled = AwarenessReadSettle(harness, 47_122);
        AssertEqual(0, settled.Code, "Awareness win settle code");
        AssertEqual(0, settled.Settle?.RewardGoodsList.Count ?? -1, "Awareness win has no generic rewards");
        AssertEqual(0, settled.Settle?.ChallengeCount ?? -1, "Awareness win has no generic challenge count");
        object afterWin = AwarenessCall(harness, "AwarenessGetDataRequest", 47_123, Activator.CreateInstance(getRequest)!, getResponse);
        AssertEqual(true, AwarenessFinished(afterWin, chapterId).Contains(stageId), "Awareness win persists stage");
        byte[] finishedState = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, 47_122 + 1, CreateMissingStageSettleRequest((uint)stageId, preFightResponse.FightData.FightId, 47_101));
        AssertEqual(true, ReadResponsePayload<FightSettleResponse>(harness, 47_123, nameof(FightSettleResponse), "Awareness repeated win settle").Code != 0, "Awareness repeated win is idempotent");
        AssertEqual(Convert.ToHexString(finishedState), Convert.ToHexString(player.ToBson()), "Awareness repeated win does not mutate state");
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetTeamRequest", 47_124, validTeam, setTeamResponse), setTeamResponse, 0, "finished Awareness team rejected", expectedSuccess: false);
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessResetStageRequest", 47_125,
            AwarenessRequest(resetRequest, ("ChapterId", chapterId), ("StageId", stageId)), resetResponse), resetResponse, 0, "Awareness reset stage");
        AssertEqual(false, AwarenessFinished(AwarenessCall(harness, "AwarenessGetDataRequest", 47_126, Activator.CreateInstance(getRequest)!, getResponse), chapterId).Contains(stageId), "Awareness reset persists");
        Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
        using LoopbackSessionHarness reloginHarness = new(roster, reloaded, CreateDrawCompatibilityInventory(47_101, []), "awareness-compat-relogin");
        object relogin = AwarenessCall(reloginHarness, "AwarenessGetDataRequest", 47_127, Activator.CreateInstance(getRequest)!, getResponse);
        object repeatedRelogin = AwarenessCall(reloginHarness, "AwarenessGetDataRequest", 47_128, Activator.CreateInstance(getRequest)!, getResponse);
        AssertEqual(Convert.ToHexString(MessagePackSerializer.Serialize(info, AwarenessValue(relogin, "AwarenessInfo"))), Convert.ToHexString(MessagePackSerializer.Serialize(info, AwarenessValue(repeatedRelogin, "AwarenessInfo"))), "Awareness GetData/relogin equivalence");
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 47_129, preFight);
        PreFightResponse lossPreFight = ReadResponsePayload<PreFightResponse>(harness, 47_129, nameof(PreFightResponse), "Awareness loss pre-fight");
        AssertEqual(0, lossPreFight.Code, "Awareness loss pre-fight code");
        AwarenessAssertHypertuned(harness, lossPreFight, equippedId, 3, [chapterId], [occupiedEvent], "occupied, 2 awakened slots after reset");
        byte[] beforeLoss = player.ToBson();
        FightSettleRequest loss = CreateMissingStageSettleRequest((uint)stageId, lossPreFight.FightData.FightId, 47_101);
        loss.Result.IsWin = false;
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, 47_130, loss);
        AssertEqual(0, AwarenessReadSettle(harness, 47_130).Code, "Awareness loss settle code");
        AssertEqual(Convert.ToHexString(beforeLoss), Convert.ToHexString(player.ToBson()), "Awareness loss does not mutate progress");
        foreach (EquipData equip in harness.Session.character.Equips) equip.AwakeSlotList = [1];
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 47_131, preFight);
        AwarenessAssertHypertuned(harness, ReadResponsePayload<PreFightResponse>(harness, 47_131, nameof(PreFightResponse), "Awareness 1-awake pre-fight"), equippedId, 2, [chapterId], [occupiedEvent], "occupied, 1 awakened slot");
        AwarenessAssertCode(AwarenessCall(harness, "AwarenessSetCharacterRequest", 47_132,
            AwarenessRequest(setCharacterRequest, ("ChapterId", chapterId), ("CharacterId", 0)), setCharacterResponse), setCharacterResponse, 0, "clear occupant before hypertuned check");
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 47_133, preFight);
        AwarenessAssertHypertuned(harness, ReadResponsePayload<PreFightResponse>(harness, 47_133, nameof(PreFightResponse), "Awareness unoccupied pre-fight"), equippedId, 0, [], [], "unoccupied");
        AwarenessAssertConfigured(relogin, chapters, "Awareness relogin snapshot");
        AssertIntegerList([chapterId], AwarenessChapterIds(relogin).Select(Convert.ToInt64).ToArray(), "Awareness relogin ChapterRecords");
    }

    private static void AwarenessAssertHypertuned(LoopbackSessionHarness harness, PreFightResponse response, long characterId, int occupantLevel, int[] positions, int[] eventIds, string label)
    {
        JToken npcs = JToken.FromObject(response.FightData.RoleData.Single(role => role.Id == harness.Session.player.PlayerData.Id).NpcData);
        bool sawOccupant = false;
        foreach (JToken npc in npcs.Children().Select(property => ((JProperty)property).Value))
        {
            bool isOccupant = npc["Character"]!.Value<long>("Id") == characterId;
            sawOccupant |= isOccupant;
            AssertIntegerList(positions.Select(id => (long)id).ToArray(), npc["AwarenessSetPositions"]!.Select(id => id.Value<long>()).ToArray(), $"{label} AwarenessSetPositions");
            AssertIntegerList(eventIds.Select(id => (long)id).ToArray(), npc["FightEventsWithLevel"]!.Select(entry => entry.Value<long>("FightEventId")).ToArray(), $"{label} FightEventsWithLevel ids");
            foreach (JToken entry in npc["FightEventsWithLevel"]!)
                AssertEqual(isOccupant ? occupantLevel : 1, entry.Value<int>("FightEventLevel"), $"{label} FightEventLevel (occupant={isOccupant})");
        }
        AssertEqual(true, sawOccupant, $"{label}: equipped character is deployed");
    }

    private static Type AwarenessType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetTypes().FirstOrDefault(type => type.Name == name)).FirstOrDefault(type => type is not null) ?? throw new InvalidDataException($"Awareness compatibility: missing {name}.");
    private static int AwarenessInt(object value, string name) => Convert.ToInt32(AwarenessValue(value, name));
    private static List<int> AwarenessInts(object value, string name) => ((IEnumerable)AwarenessValue(value, name)).Cast<object>().Select(Convert.ToInt32).ToList();
    private static object AwarenessValue(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value) ?? throw new InvalidDataException($"{value.GetType().Name}: missing {name}.");
    private static object AwarenessRequest(Type type, params (string Name, object Value)[] values) { object result = Activator.CreateInstance(type)!; foreach ((string name, object value) in values) { PropertyInfo property = type.GetProperty(name) ?? throw new InvalidDataException($"{type.Name}: missing {name}."); property.SetValue(result, AwarenessConvert(property.PropertyType, value)); } return result; }
    private static object AwarenessConvert(Type target, object value) { if (target.IsInstanceOfType(value)) return value; if (value is IEnumerable values && value is not string && target.IsGenericType) { Type item = target.GetGenericArguments()[0]; IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item))!; foreach (object entry in values) list.Add(AwarenessConvert(item, entry)); return list; } return Convert.ChangeType(value, target); }
    private static object AwarenessCall(LoopbackSessionHarness harness, string requestName, int id, object request, Type responseType) { InvokeRegisteredRequestHandler(requestName, harness.Session, id, request); return ReadResponsePayload(harness, id, requestName.Replace("Request", "Response"), requestName, responseType); }
    private static void AwarenessAssertCode(object response, Type _, int expected, string name, bool expectedSuccess = true) { int code = AwarenessInt(response, "Code"); if (expectedSuccess) AssertEqual(expected, code, name); else AssertEqual(true, code != expected, name); }
    private static List<List<int>> AwarenessTeam(object chapter, IReadOnlyDictionary<int, int> needs) => AwarenessInts(chapter, "TeamInfoId").Select(id => Enumerable.Repeat(0, needs[id]).ToList()).ToList();
    private static void AwarenessAssertConfigured(object response, IEnumerable<object> chapters, string name) { object value = AwarenessValue(response, "AwarenessInfo"); long[] expected = chapters.Select(row => (long)AwarenessInt(row, "Id")).ToArray(); foreach (string record in new[] { "ChallengeRecords", "TeamRecords" }) AssertIntegerList(expected, ((IEnumerable)AwarenessValue(value, record)).Cast<object>().Select(row => (long)AwarenessInt(row, "ChapterId")).ToArray(), $"{name} {record}"); }
    private static List<int> AwarenessChapterIds(object response) => ((IEnumerable)AwarenessValue(AwarenessValue(response, "AwarenessInfo"), "ChapterRecords")).Cast<object>().Select(row => AwarenessInt(row, "ChapterId")).ToList();
    private static void AwarenessAssertTeam(object response, int chapterId, List<List<int>> expected, string name) { object info = AwarenessValue(response, "AwarenessInfo"); object team = ((IEnumerable)AwarenessValue(info, "TeamRecords")).Cast<object>().Single(row => AwarenessInt(row, "ChapterId") == chapterId); List<List<int>> actual = ((IEnumerable)AwarenessValue(team, "TeamInfoList")).Cast<IEnumerable>().Select(row => row.Cast<object>().Select(Convert.ToInt32).ToList()).ToList(); AssertEqual(true, expected.SelectMany(row => row).SequenceEqual(actual.SelectMany(row => row)), name); }
    private static List<int> AwarenessFinished(object response, int chapterId) { object info = AwarenessValue(response, "AwarenessInfo"); object challenge = ((IEnumerable)AwarenessValue(info, "ChallengeRecords")).Cast<object>().Single(row => AwarenessInt(row, "ChapterId") == chapterId); return AwarenessInts(challenge, "FinishStageIds"); }
    private static FightSettleResponse AwarenessReadSettle(LoopbackSessionHarness harness, int packetId) { for (int index = 0; index < 8; index++) { Packet packet = harness.ReadPacket("Awareness settle packet"); if (packet.Type == Packet.ContentType.Push) { Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content); AssertEqual(false, push.Name == nameof(NotifyStageData), "Awareness settle emits no NotifyStageData"); continue; } Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content); AssertEqual(packetId, response.Id, "Awareness settle response id"); AssertEqual(nameof(FightSettleResponse), response.Name, "Awareness settle response name"); return MessagePackSerializer.Deserialize<FightSettleResponse>(response.Content); } throw new InvalidDataException("Awareness settle: response missing."); }
}
