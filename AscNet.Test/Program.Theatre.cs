using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.theatre;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

internal partial class Program
{
    private static readonly HashSet<string> TheatreCalls = [];
    private static List<T> TheatreRows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static Type TheatreModuleType => RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TheatreModule");
    private static object TheatreMutation(TheatreCase test) => Activator.CreateInstance(
        TheatreModuleType.GetNestedType("Mutation", BindingFlags.NonPublic)!,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [test.Session, true], null)!;
    private static object? TheatreCall(string name, object mutation, params object[] args)
    {
        object[] values = [mutation, .. args];
        var method = TheatreModuleType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(method => method.Name == name && method.GetParameters().Length == values.Length
                && method.GetParameters().Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(values[index])).All(value => value));
        return method.Invoke(null, values);
    }
    private static object? TheatreInvoke(string name, TheatreCase test, params object[] args) => TheatreCall(name, TheatreMutation(test), args);
    private static TheatreSlot CurrentTheatreSlot(TheatreCase test) => test.Data.CurChapterDb!.CurNodeDb.Slots.Single(slot => slot.Selected == 1);
    private static TheatreSlot TheatreFixtureSlot(TheatreCase test, TheatreSlot slot)
    {
        slot.SlotId = ++test.State.NextUid;
        slot.Selected = 1;
        test.Data.CurChapterDb!.CurNodeDb.Slots = [slot];
        test.Data.CurChapterDb.SkillToSelect.Clear();
        test.State.NodeCompletionPending = false;
        test.State.ShopSkillOpened = false;
        test.State.PendingSkillShopType = 0;
        test.State.PendingSkillPowers.Clear();
        test.State.Fight = null;
        test.SaveFixture();
        return slot;
    }
    private static TheatreTeamData TheatreTeam(TheatreCase test, int index = 0, int roleOffset = 0)
    {
        int level = test.Data.CurRoleLv;
        int role = test.Data.RecruitRole[roleOffset];
        int robot = TheatreRows<TheatreRoleAttrTable>().Single(row => row.RoleId == role && row.Lv == level).RobotId;
        return new() { TeamIndex = index, CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [robot, 0, 0] };
    }

    private static void ValidateTheatreCompatibility()
    {
        TheatreCalls.Clear();
        string[] requests = typeof(TheatreStartAdventureRequest).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(TheatreStartAdventureRequest).Namespace
                && type.Name.StartsWith("Theatre", StringComparison.Ordinal)
                && type.Name.Length > "Theatre".Length
                && char.IsUpper(type.Name["Theatre".Length])
                && type.Name.EndsWith("Request", StringComparison.Ordinal))
            .Select(type => type.Name).Order().ToArray();
        foreach (string request in requests) _ = GetRegisteredRequestHandler(request);
        ValidateTheatreLifecycleCompatibility();
        ValidateTheatreLiveRecoveryCompatibility();
        ValidateTheatreMargStartCompatibility();
        ValidateTheatreNodeCompatibility();
        ValidateTheatreSkillCompatibility();
        ValidateTheatreMetaCompatibility();
        ValidateTheatreTaskCompatibility();
        ValidateTheatreCombatCompatibility();
        ValidateTheatreEffectsCompatibility();
        foreach (var route in TheatreRows<TheatreChapterGroupTable>().OrderBy(row => row.Id))
        {
            int mode = route.Mode ?? 0;
            using TheatreCase test = new($"complete-route-{route.Id}");
            TheatreRouteFixture(test, route.ConditionId ?? 0);
            test.SaveFixture();
            test.Start(mode);
            AssertEqual(route.ChapterStartId, test.Data.CurChapterDb!.ChapterId, "Source route fixture selects intended branch");
            HashSet<int> runEvents = [], chapters = [];
            int limit = TheatreRows<TheatreNodeTable>().Count * 128;
            for (int transition = 0; test.Data.CurChapterDb is not null && transition < limit; transition++)
            {
                runEvents.UnionWith(test.State.RunEventIds);
                chapters.UnionWith(test.State.CompletedChapterIds);
                int chapter = test.Data.CurChapterDb!.ChapterId;
                var selected = test.Data.CurChapterDb.CurNodeDb.Slots.SingleOrDefault(slot => slot.Selected == 1);
                AdvanceTheatre(test);
                if (test.Data.CurChapterDb is null)
                {
                    chapters.Add(chapter);
                    if (selected?.SlotType == 1) runEvents.Add(selected.ConfigId);
                }
                if (test.Data.CurChapterDb is not null && transition % 17 == 0) test.Relog($"complete-mode-{mode}");
            }
            AssertEqual(true, test.Data.CurChapterDb is null, $"Mode {mode} completes authored adventure");
            var settle = test.State.LastSettle ?? throw new InvalidDataException("Missing durable final settlement.");
            AssertEqual(test.State.RunId, test.State.SettledRunId, "Final settlement belongs to the active run");
            int expectedEnding = TheatreRows<TheatreEndingTable>().Where(row => row.Type.Select((type, index) => type switch
            {
                1 => row.Param[index] == 0,
                2 => runEvents.Contains(row.Param[index]),
                3 => chapters.Contains(row.Param[index]),
                _ => false
            }).All(value => value)).OrderByDescending(row => row.Priority).First().Id;
            AssertEqual(expectedEnding, settle.Ending, "Highest eligible authored ending wins for actual run achievements");
            var factors = TheatreRows<TheatreSettleFactorTable>().ToDictionary(row => row.Id, row => (decimal)(row.Factor ?? 0));
            int[] counts = [settle.SettleNodeCount, settle.SettleFightCount, settle.SettleEventCount, settle.SettleBossCount, settle.SettleLeftReopenCount];
            int[] points = [settle.SettleNodeCountPoint, settle.SettleFightCountPoint, settle.SettleEventCountPoint, settle.SettleBossCountPoint, settle.SettleLeftReopenCountPoint];
            for (int i = 0; i < counts.Length; i++) AssertEqual((int)decimal.Floor(counts[i] * factors[i + 1]), points[i], "Authored settlement factor term");
            AssertEqual(points.Sum(), settle.TotalPoint, "Score sums five independently floored terms");
            AssertEqual(true, test.Data.EndingRecord.Contains(settle.Ending), "Final ending is recorded durably");
            AssertEqual(true, test.State.LastRunData is { CurChapterDb: not null }, "Final presentation keeps its old adventure");
            test.Replay("terminal callback");
            test.Relog("before final presentation", pending: true);
            AssertEqual(true, test.Login().CurChapterDb is not null, "Reconnect can reconstruct final presentation");
            test.Reject(nameof(TheatreSettleAdventureRequest), null, "Inactive final settlement cannot pay twice");
            test.Start(mode);
            AssertEqual(false, test.State.SettlementRecoveryPending, "Next adventure dismisses old final presentation");
            test.Relog("after final presentation");
        }
        AssertEqual("", string.Join(",", requests.Where(name => !TheatreCalls.Contains(name))), "Every original RPC has a correlated protocol response");
        Console.WriteLine($"Original Theatre compatibility: {requests.Length} RPCs, normal/SP complete adventures, durable offers, node/skill/meta/task/shop and recovery checks passed.");
    }

    private static void TheatreRouteFixture(TheatreCase test, int conditionId)
    {
        if (conditionId == 0) return;
        var condition = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().Single(row => row.Id == conditionId);
        if (!string.IsNullOrWhiteSpace(condition.Formula))
        {
            // These authored route formulas are conjunctions of decoration-level leaves.
            foreach (string child in condition.Formula.Split('&')) TheatreRouteFixture(test, int.Parse(child));
            return;
        }
        AssertEqual(17002, condition.Type, "Route fixture comes from authored decoration-level condition");
        if (condition.Params[1] == 0) return;
        var decoration = TheatreRows<TheatreDecorationTable>().Single(row => row.DecorationId == condition.Params[0] && row.Lv == condition.Params[1]);
        test.Data.Decorations.RemoveAll(id => TheatreRows<TheatreDecorationTable>().Single(row => row.Id == id).DecorationId == decoration.DecorationId);
        test.Data.Decorations.Add(decoration.Id);
    }

    private static void ValidateTheatreLifecycleCompatibility()
    {
        using TheatreCase test = new("lifecycle");
        test.Reject(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = int.MaxValue }, "Unknown difficulty");
        AssertEqual(20155001, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "Invalid difficulty uses source error");
        test.Reject(nameof(TheatreRecruitCharacterRequest), new TheatreRecruitCharacterRequest { RoleId = 1 }, "Recruit without adventure");
        test.Start();
        test.Replay("start deployment");
        var preview = test.Data.CurChapterDb!.CurNodeDb.Slots.Single(slot => slot.RewardType == 1);
        AssertEqual(true, preview.PowerId > 0 && test.Data.UnlockPowerIds.Contains(preview.PowerId), "Initial authored skill bucket has a usable preview faction");
        int previewSlot = preview.SlotId, previewPower = preview.PowerId;
        test.Relog("stable recruited adventure");
        AssertEqual(previewPower, test.Data.CurChapterDb!.CurNodeDb.Slots.Single(slot => slot.SlotId == previewSlot).PowerId, "Reconnect cannot reroll preview faction");
        test.Reject(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = test.Data.DifficultyId }, "Cannot overwrite active adventure");
        test.Reject(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest(), "Empty captain cannot deploy");
        test.Reject(nameof(TheatreSelectNodeRequest), new TheatreSelectNodeRequest { NodeId = int.MaxValue, SlotId = int.MaxValue }, "UnOffered node rejected");
        test.Call(nameof(TheatreSettleAdventureRequest));
        test.Replay("abandon settlement");
        AssertEqual(true, test.Data.CurChapterDb is null, "Abandon closes the adventure");
        int receiptId = test.LastPacketId;
        var receipt = BsonSerializer.Deserialize<TheatreRequestReceipt>(test.State.RequestReceipts[receiptId].ToBson());
        test.Start();
        test.State.RequestReceipts[receiptId] = receipt;
        test.SaveFixture();
        test.Call(nameof(TheatreSettleAdventureRequest), success: false, reusePacketId: receiptId);
        AssertEqual(1, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "Receipt from another RunId is rejected");
        AssertEqual(true, test.Data.CurChapterDb is not null, "Stale receipt cannot settle the new adventure");
        test.Call(nameof(TheatreSettleAdventureRequest));
        test.Call(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = TheatreRows<TheatreDifficultyTable>().Min(row => row.Id) });
        byte[] offers = MessagePackSerializer.Serialize(test.Data.CurChapterDb!.RefreshRole);
        test.Relog("unconsumed recruit offers");
        AssertEqual(true, offers.SequenceEqual(MessagePackSerializer.Serialize(test.Data.CurChapterDb!.RefreshRole)), "Reconnect retains exact offered roles");
        test.Call(nameof(TheatreRefreshCharacterRequest));
        AssertEqual(1, test.Data.CurChapterDb.RefreshRoleCount, "Refresh wire count records uses, not remaining uses");
        test.Replay("recruit refresh");
        test.Reject(nameof(TheatreRecruitCharacterRequest), new TheatreRecruitCharacterRequest { RoleId = int.MaxValue }, "Role outside frozen offered pool");
        var chapter = TheatreRows<TheatreChapterTable>().Single(row => row.Id == test.Data.CurChapterDb.ChapterId);
        while (test.Data.CurChapterDb.RefreshRoleCount < chapter.RecruitRefreshCount) test.Call(nameof(TheatreRefreshCharacterRequest));
        test.Reject(nameof(TheatreRefreshCharacterRequest), null, "Authored refresh uses exhausted");
        AssertEqual(20155006, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "Exhausted refresh source error");
    }

    private sealed class TheatreCase : IDisposable
    {
        private static long nextPlayerId = 99_700;
        private int packetId;
        private readonly MongoCollectionOverride collections;
        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public PlayerTheatreState State => Session.player.Theatre;
        public TheatreData Data => State.Data;
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public int LastPacketId => packetId;
        public string LastRequestName { get; private set; } = "";
        public object? LastRequest { get; private set; }
        public TheatreCase(string name)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(out Players, out Characters, out Inventories, out Stages);
            long id = ++nextPlayerId;
            Harness = new(CreateDrawCompatibilityCharacter(id), CreateDrawCompatibilityPlayer(id), CreateDrawCompatibilityInventory(id, []), $"theatre-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            Session.player.PlayerData.Level = 70;
            PrepareLogin();
            _ = BuildTaskData(Session);
            SaveFixture();
        }
        private void PrepareLogin() => RequiredMethod(TheatreModuleType, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);
        public NotifyTheatreData Login() => (NotifyTheatreData)RequiredMethod(TheatreModuleType, "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session])!;
        public void SaveFixture()
        {
            Session.player.SaveChecked(); Session.character.SaveChecked(); Session.inventory.SaveChecked(); Session.stage.SaveChecked();
        }
        public void Start(int mode = 0)
        {
            if (mode == 1)
            {
                int gateId = TheatreRows<TheatreConfigTable>().Single(row => row.Key == "SPModeConditionId").Value;
                var gate = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().Single(row => row.Id == gateId);
                AssertEqual(17000, gate.Type, "SP fixture uses authored chapter achievement");
                if (!Data.PassChapterId.Contains(gate.Params[0])) Data.PassChapterId.Add(gate.Params[0]);
                SaveFixture();
            }
            int difficulty = TheatreRows<TheatreDifficultyTable>().OrderBy(row => row.Id).First(row => (bool)TheatreInvoke("IsConditionSatisfied", this, row.ConditionId ?? 0)!).Id;
            Call(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = difficulty, Mode = mode });
            Relog("initial stable offers");
            Recruit();
            Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(this) });
        }
        public void Recruit()
        {
            while ((int)TheatreInvoke("RemainingRecruitCount", this)! > 0)
            {
                int role = Data.CurChapterDb!.RefreshRole.FirstOrDefault(role => !Data.RecruitRole.Contains(role));
                if (role == 0)
                {
                    Call(nameof(TheatreRefreshCharacterRequest));
                    continue;
                }
                Call(nameof(TheatreRecruitCharacterRequest), new TheatreRecruitCharacterRequest { RoleId = role });
            }
        }
        public JObject Call(string requestName, object? request = null, bool? success = true, int? reusePacketId = null)
        {
            Pushes.Clear();
            int id = reusePacketId ?? ++packetId;
            TheatreCalls.Add(requestName);
            LastRequestName = requestName; LastRequest = request;
            InvokeRegisteredRequestHandler(requestName, Session, id, request);
            for (int i = 0; i < 256; i++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {i}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} response type");
                var response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name, $"{requestName} response name");
                LastResponseContent = response.Content;
                JObject body = requestName == nameof(PreFightRequest)
                    ? new JObject { ["Code"] = MessagePackSerializer.Deserialize<PreFightResponse>(response.Content).Code }
                    : JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (success.HasValue) AssertEqual(success.Value, code == 0, $"{requestName} accepted={success}, code={code}");
                if (Harness.TryReadAvailablePacket($"{requestName} unexpected trailing packet", out Packet extra))
                    throw new InvalidDataException($"{requestName}: packet after response ({extra.Type}).");
                if (code == 0 && requestName.StartsWith("Theatre", StringComparison.Ordinal))
                {
                    var saved = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Success without durable player."));
                    AssertEqual(true, State.ToBson().SequenceEqual(saved.Theatre.ToBson()), $"{requestName} persists before acknowledgement");
                    foreach (var slot in Data.CurChapterDb?.CurNodeDb.Slots.Where(slot => slot.RewardType == 1) ?? [])
                        AssertEqual(true, slot.PowerId > 0 && Data.UnlockPowerIds.Contains(slot.PowerId), "Published skill reward freezes an unlocked positive faction before preview");
                }
                return body;
            }
            throw new InvalidDataException($"{requestName}: missing correlated response.");
        }
        public void Reject(string requestName, object? request, string name)
        {
            byte[] state = State.ToBson(), inventory = Session.inventory.ToBson(), characters = Session.character.ToBson();
            Call(requestName, request, success: false);
            AssertEqual(false, Pushes.Any(push => !(requestName == nameof(TheatreStartAdventureRequest) && push.Name == nameof(NotifyTheatreData))
                && !(requestName == nameof(FinishTaskRequest) && push.Name == nameof(NotifyTask))), $"{name}: no additive success pushes");
            AssertEqual(true, state.SequenceEqual(State.ToBson()), $"{name}: preserves theatre");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: preserves inventory");
            AssertEqual(true, characters.SequenceEqual(Session.character.ToBson()), $"{name}: preserves roster");
        }
        public void Replay(string name)
        {
            byte[] response = LastResponseContent.ToArray(), state = State.ToBson(), inventory = Session.inventory.ToBson();
            Call(LastRequestName, LastRequest, reusePacketId: LastPacketId);
            AssertEqual(true, response.SequenceEqual(LastResponseContent), $"{name}: exact frozen response");
            AssertEqual(0, Pushes.Count, $"{name}: committed replay emits no bonus/mode pushes");
            AssertEqual(true, state.SequenceEqual(State.ToBson()), $"{name}: no new mutation");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: no duplicate reward");
        }
        public void Relog(string name, bool pending = false)
        {
            byte[] data = MessagePackSerializer.Serialize(Data);
            int uid = State.NextUid;
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson!);
            Character characters = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson!);
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson!);
            Stage stage = BsonSerializer.Deserialize<Stage>(Stages.LastSuccessfulReplacementBson ?? Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(characters, player, inventory, $"theatre-relog-{name}"); Session.stage = stage;
            PrepareLogin();
            if (!pending)
            {
                AssertEqual(true, data.SequenceEqual(MessagePackSerializer.Serialize(Data)), $"{name}: immutable durable data survives reconnect");
                AssertEqual(uid, State.NextUid, $"{name}: reconnect does not reroll identities");
            }
        }
        public long Balance(int itemId) => Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        public void Fund(int itemId, int count)
        {
            Item? item = Session.inventory.Items.SingleOrDefault(item => item.Id == itemId);
            if (item is null) Session.inventory.Items.Add(new Item { Id = itemId, Count = count }); else item.Count = count;
        }
        public void Dispose() { Harness.Dispose(); collections.Dispose(); }
    }
    private static void ValidateTheatreMetaCompatibility()
    {
        TheatreMetaCommonShops();
        using TheatreCase test = new("meta-current-rows");
        var decorations = TheatreRows<TheatreDecorationTable>();
        var current = decorations.First(row => (row.Lv ?? 0) == 0 && !(row.ConditionId > 0) && row.UpgradeCostCount > 0);
        var next = decorations.Single(row => row.DecorationId == current.DecorationId && row.Lv == 1);
        AssertEqual(true, test.Data.Decorations.Contains(current.Id), "Decoration initialization owns authored level-zero row");
        var upgrade = new TheatreDecorationUpgradeRequest { DecorationId = current.Id };
        test.Fund(current.UpgradeCostItemId!.Value, current.UpgradeCostCount!.Value);
        test.SaveFixture();
        test.Reject(nameof(TheatreDecorationUpgradeRequest), upgrade, "Decoration chapter condition");
        TheatreMetaUnlockChapters(test);
        test.Fund(current.UpgradeCostItemId.Value, current.UpgradeCostCount.Value - 1);
        test.SaveFixture();
        test.Reject(nameof(TheatreDecorationUpgradeRequest), upgrade, "Decoration exact current-row cost gate");
        test.Fund(current.UpgradeCostItemId.Value, current.UpgradeCostCount.Value);
        test.SaveFixture();
        test.Call(nameof(TheatreDecorationUpgradeRequest), upgrade);
        AssertEqual(0L, test.Balance(current.UpgradeCostItemId.Value), "Decoration charges current row, not next row");
        AssertEqual(false, test.Data.Decorations.Contains(current.Id), "Decoration replaces old row");
        AssertEqual(true, test.Data.Decorations.Contains(next.Id), "Decoration owns next authored row");
        test.Reject(nameof(TheatreDecorationUpgradeRequest), upgrade, "Stale decoration row cannot purchase twice");
        test.Relog("decoration-current-row");

        var favorRows = TheatreRows<TheatrePowerFavorTable>();
        int power = TheatreRows<TheatrePowerConditionTable>().First(row => !(row.ConditionId > 0)).Id;
        var initial = favorRows.Single(row => row.PowerId == power && (row.Lv ?? 0) == 0);
        var first = favorRows.Single(row => row.PowerId == power && row.Lv == 1);
        var favorUpgrade = new TheatrePowerFavorUpgradeRequest { PowerFavorId = initial.Id };
        test.Fund(96103, initial.UpgradeCost!.Value - 1);
        test.SaveFixture();
        test.Reject(nameof(TheatrePowerFavorUpgradeRequest), favorUpgrade, "Favor insufficient current-row cost");
        test.Fund(96103, initial.UpgradeCost.Value);
        test.SaveFixture();
        test.Call(nameof(TheatrePowerFavorUpgradeRequest), favorUpgrade);
        AssertEqual(0L, test.Balance(96103), "Favor charges current-row UpgradeCost");
        AssertEqual(true, test.Data.UnlockPowerFavorIds.Contains(initial.Id) && test.Data.UnlockPowerFavorIds.Contains(first.Id), "Favor preserves purchased history");
        AssertEqual(false, test.Data.EffectPowerFavorIds.Contains(first.Id), "Favor purchase does not activate reward");
        int keepsake = first.RewardParam[first.RewardType.IndexOf(5)];
        AssertEqual(false, test.Data.Keepsakes.Any(row => row.KeepsakeId == keepsake), "Favor purchase does not unlock keepsake");
        test.Reject(nameof(TheatrePowerFavorUpgradeRequest), favorUpgrade, "Already purchased next favor cannot be charged again");
        test.Call(nameof(TheatreGetPowerFavorRewardRequest), new TheatreGetPowerFavorRewardRequest { PowerFavorId = first.Id });
        AssertEqual(true, test.Data.EffectPowerFavorIds.Contains(first.Id), "Separate favor claim activates row");
        AssertEqual(1, test.Data.Keepsakes.Count(row => row.KeepsakeId == keepsake), "Separate favor claim unlocks one keepsake");
        AssertEqual(1, test.Pushes.Count(push => push.Name == nameof(NotifyUnlockKeepsake)), "Keepsake claim emits its unlock once");
        test.Reject(nameof(TheatreGetPowerFavorRewardRequest), new TheatreGetPowerFavorRewardRequest { PowerFavorId = first.Id }, "Favor claim is one-shot");
        test.Reject(nameof(TheatreGetPowerFavorRewardRequest), new TheatreGetPowerFavorRewardRequest { PowerFavorId = initial.Id }, "Level-zero favor is not a reward");
        test.Relog("separate-favor-claim");

        var rewardRow = favorRows.First(row => row.PowerId == power && row.RewardType.Contains(1));
        int[] rewards = rewardRow.RewardType.Select((type, index) => type == 1 ? rewardRow.RewardParam[index] : 0).Where(id => id > 0).ToArray();
        TheatreMetaRecover("favor", nameof(TheatreGetPowerFavorRewardRequest),
            new TheatreGetPowerFavorRewardRequest { PowerFavorId = rewardRow.Id }, rewards,
            fixture =>
            {
                TheatreMetaUnlockChapters(fixture);
                // Valid historical purchases: one contiguous prefix for the initially unlocked power.
                fixture.Data.UnlockPowerFavorIds = favorRows.Where(row => row.PowerId == power && (row.Lv ?? 0) <= rewardRow.Lv).Select(row => row.Id).ToList();
            }, fixture => AssertEqual(true, fixture.Data.EffectPowerFavorIds.Contains(rewardRow.Id), "Recovered favor reward claim commits"));
    }

    private static void TheatreMetaUnlockChapters(TheatreCase test)
    {
        foreach (string key in new[] { "FavorConditionId", "DecorationConditionId" })
        {
            int id = TheatreRows<TheatreConfigTable>().Single(row => row.Key == key).Value;
            var condition = TheatreRows<AscNet.Table.V2.share.condition.ConditionTable>().Single(row => row.Id == id);
            AssertEqual(17000, condition.Type, "Authored permanent-meta gate is exact chapter history");
            if (!test.Data.PassChapterId.Contains(condition.Params[0])) test.Data.PassChapterId.Add(condition.Params[0]);
        }
    }

    private static void TheatreMetaAssertRewards(TheatreCase test, IReadOnlyDictionary<int, long> before, IEnumerable<int> rewardIds)
    {
        var items = TheatreRows<AscNet.Table.V2.share.item.ItemTable>().Select(row => row.Id).ToHashSet();
        var goods = rewardIds.SelectMany(id => ResolveRewardGoods(id,
            TheatreRows<AscNet.Table.V2.share.reward.RewardGoodsTable>(), "Original Theatre authored rewards")).ToList();
        AssertEqual(true, goods.All(row => items.Contains(row.TemplateId)), "Meta fixture rewards are inventory items");
        var expected = goods.GroupBy(row => row.TemplateId).ToDictionary(group => group.Key, group => group.Sum(row => (long)row.Count));
        foreach (int id in before.Keys.Concat(test.Session.inventory.Items.Select(item => item.Id)).Concat(expected.Keys).Distinct())
            AssertEqual(before.GetValueOrDefault(id) + expected.GetValueOrDefault(id), test.Balance(id), $"Theatre reward item {id} credited exactly once, no incidental items");
    }

    private static void TheatreMetaRecover(string name, string requestName, object request, int[] rewards,
        Action<TheatreCase> fixture, Action<TheatreCase> committed)
    {
        foreach (int boundary in new[] { 0, 1, 2 })
        {
            using TheatreCase test = new($"meta-{name}-boundary-{boundary}");
            fixture(test);
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            byte[] inventoryBefore = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            if (boundary == 0) test.Inventories.ThrowOnReplaceOne = true;
            if (boundary == 1) test.Characters.ThrowOnReplaceOne = true;
            if (boundary == 2) test.Players.BeforeReplaceOne = player =>
            {
                if (player.Theatre.PendingMutation is null) throw new MongoDB.Driver.MongoException("Injected original Theatre final commit failure.");
            };
            try { test.Call(requestName, request, success: false); }
            finally
            {
                test.Inventories.ThrowOnReplaceOne = false;
                test.Characters.ThrowOnReplaceOne = false;
                test.Players.BeforeReplaceOne = null;
            }
            AssertEqual(0, test.Pushes.Count, "Failed meta commit emits no success pushes");
            byte[] savedIntent = test.Players.LastSuccessfulReplacementBson!.ToArray();
            var pending = BsonSerializer.Deserialize<Player>(savedIntent).Theatre.PendingMutation
                ?? throw new InvalidDataException("Meta failure must retain a durable pending intent.");
            AssertEqual(boundary == 0, inventoryBefore.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Injected boundary brackets inventory persistence");
            int failedPacket = test.LastPacketId;
            JObject busy = test.Call(nameof(TheatreDecorationUpgradeRequest), new TheatreDecorationUpgradeRequest { DecorationId = int.MaxValue }, success: false);
            AssertEqual(1, busy.Value<int>("Code"), "Different meta intent remains busy, not validation failure");
            AssertEqual(true, savedIntent.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Busy intent cannot replace saved journal");
            test.Call(requestName, request);
            int retryPacket = test.LastPacketId;
            AssertEqual(true, failedPacket != retryPacket, "Recovery uses a new packet with identical semantic intent");
            AssertEqual(true, pending.Response!.SequenceEqual(test.LastResponseContent), "Semantic retry returns frozen response");
            foreach (var group in pending.Pushes.GroupBy(push => (push.Name, Payload: Convert.ToBase64String(push.Payload))))
                AssertEqual(group.Count(), test.Pushes.Count(push => push.Name == group.Key.Name && Convert.ToBase64String(push.Content) == group.Key.Payload), "Frozen mode pushes are owed once");
            committed(test);
            TheatreMetaAssertRewards(test, before, rewards);
            byte[] durable = test.Players.LastSuccessfulReplacementBson!.ToArray();
            test.Call(requestName, request, reusePacketId: retryPacket);
            AssertEqual(true, durable.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Committed receipt replay does not write another mutation");
            if (requestName != nameof(FinishTaskRequest)) AssertEqual(0, test.Pushes.Count, "Committed meta receipt emits no owed pushes again");
            TheatreMetaAssertRewards(test, before, rewards);
            test.Relog($"{name}-recovered-{boundary}");
            test.Reject(requestName, request, "Recovered new-packet claim cannot grant twice");
            TheatreMetaAssertRewards(test, before, rewards);
            AssertEqual(true, BsonSerializer.Deserialize<Player>(savedIntent).Theatre.PendingMutation != null, "Captured BSON remains the immutable failed intent after recovery");
        }
    }

    private static void ValidateTheatreTaskCompatibility()
    {
        var ids = TheatreRows<AscNet.Table.V2.client.theatre.TheatreTaskTable>().SelectMany(row => row.TaskId).Where(id => id > 0).ToHashSet();
        var tasks = TheatreRows<AscNet.Table.V2.share.task.TaskTable>().Where(row => ids.Contains(row.Id)).ToList();
        var conditions = TheatreRows<AscNet.Table.V2.share.task.ConditionTable>().ToDictionary(row => row.Id);
        var daily = tasks.First(row => row.Type == 59);
        var permanent = tasks.First(row => row.Type != 59 && conditions[row.Condition].Type == 73015);
        using (TheatreCase test = new("meta-daily-progress"))
        {
            test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id }, "Unearned daily node objective");
            test.State.RunNodeCount = daily.Result ?? 1;
            foreach (var task in tasks.Where(row => row.Type == 59)) test.State.TaskProgress[task.Condition] = test.State.RunNodeCount;
            test.State.TaskProgress[permanent.Condition] = permanent.Result ?? 1;
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id });
            TheatreMetaAssertRewards(test, before, [daily.RewardId!.Value]);
            AssertEqual(true, test.State.DailyClaimedTaskIds.Contains(daily.Id), "Daily claim stored in period-owned Theatre ledger");
            AssertEqual(false, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(daily.Id), "Daily Theatre claim is not permanent mission ownership");
            test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id }, "Same-day task cannot repay");
            long period = test.State.TaskDailyPeriod;
            test.State.TaskDailyPeriod = period - 1;
            // Move the completed claim into yesterday consistently across every durable receipt owner.
            string currentKey = $"theatre-task:{test.Session.player.PlayerData.Id}:{daily.Id}:{period}";
            string historicalKey = $"theatre-task:{test.Session.player.PlayerData.Id}:{daily.Id}:{period - 1}";
            foreach (var claims in new[] { test.Session.inventory.AppliedRewardClaims, test.Session.character.AppliedRewardClaims })
            {
                int claimIndex = claims.IndexOf(currentKey);
                if (claimIndex < 0) throw new InvalidDataException("Completed daily claim lacks its durable reward receipt.");
                claims[claimIndex] = historicalKey;
            }
            long grantedAt = test.Session.inventory.RewardClaimTimes[currentKey];
            test.Session.inventory.RewardClaimTimes.Remove(currentKey);
            test.Session.inventory.RewardClaimTimes.Add(historicalKey, grantedAt - 86_400);
            foreach (var receipt in test.Session.player.WheelchairManualGuideRewardReceipts.Where(receipt => receipt.ClaimKey == currentKey))
            {
                receipt.ClaimKey = historicalKey;
                receipt.GrantedAt -= 86_400;
            }
            test.SaveFixture();
            byte[] yesterday = test.Players.LastSuccessfulReplacementBson!.ToArray();
            test.Relog("daily-historical-period");
            AssertEqual(period, test.State.TaskDailyPeriod, "Login rolls historical daily fixture into current period");
            AssertEqual(0, test.State.DailyClaimedTaskIds.Count, "Daily reset clears owned claims");
            AssertEqual(true, tasks.Where(row => row.Type == 59).All(row => !test.State.TaskProgress.ContainsKey(row.Condition)), "Daily reset clears only daily earned node progress");
            AssertEqual(permanent.Result ?? 1, test.State.TaskProgress[permanent.Condition], "Daily reset preserves permanent earned progress");
            test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id }, "New day requires new progress");
            foreach (var task in tasks.Where(row => row.Type == 59)) test.State.TaskProgress[task.Condition] = daily.Result ?? 1;
            test.State.RunNodeCount += daily.Result ?? 1;
            test.SaveFixture();
            var todayBefore = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id });
            TheatreMetaAssertRewards(test, todayBefore, [daily.RewardId.Value]);
            AssertEqual(true, test.Session.inventory.AppliedRewardClaims.Contains(historicalKey)
                && test.Session.inventory.AppliedRewardClaims.Contains(currentKey), "Old and new day anti-replay receipts coexist");
            test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id }, "Current-day claim still cannot repay after historical rollover");
            TheatreMetaAssertRewards(test, todayBefore, [daily.RewardId.Value]);
            AssertEqual(period - 1, BsonSerializer.Deserialize<Player>(yesterday).Theatre.TaskDailyPeriod, "Saved yesterday BSON is immutable");
        }
        TheatreMetaRecover("task", nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = permanent.Id }, [permanent.RewardId!.Value],
            test => test.State.TaskProgress[permanent.Condition] = permanent.Result ?? 1,
            test => AssertEqual(true, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(permanent.Id), "Recovered permanent task claim commits"));

        using (TheatreCase test = new("meta-crossmode-task-ownership"))
        {
            var foreignIds = TheatreRows<AscNet.Table.V2.client.biancatheatre.BiancaTheatreTaskTable>().SelectMany(row => row.TaskId).ToHashSet();
            var foreign = TheatreRows<AscNet.Table.V2.share.task.TaskTable>().First(row => foreignIds.Contains(row.Id) && row.RewardId > 0);
            var theatre3 = TheatreRows<AscNet.Table.V2.share.task.TaskTable>().Single(row => row.Id == 91301);
            test.State.TaskProgress[permanent.Condition] = permanent.Result ?? 1;
            test.Session.player.BiancaTheatre.TaskProgress[foreign.Condition] = foreign.Result ?? 1;
            test.Session.player.Theatre3.TaskProgress[theatre3.Condition] = theatre3.Result ?? 1;
            test.SaveFixture();
            byte[] biancaBefore = test.Session.player.BiancaTheatre.ToBson();
            byte[] theatre3Before = test.Session.player.Theatre3.ToBson();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            JObject result = test.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = [permanent.Id, foreign.Id, theatre3.Id, permanent.Id] });
            AssertEqual(true, result["SuccessTaskIds"]!.Values<int>().SequenceEqual(new[] { permanent.Id }), "Original Theatre batch claims its own distinct IDs only");
            AssertEqual(true, result["NotDealTaskIds"]!.Values<int>().SequenceEqual(new[] { foreign.Id, theatre3.Id }), "Original Theatre batch leaves foreign owners explicit NotDeal");
            AssertEqual(true, biancaBefore.SequenceEqual(test.Session.player.BiancaTheatre.ToBson()), "Original Theatre does not mutate Bianca progress");
            AssertEqual(true, theatre3Before.SequenceEqual(test.Session.player.Theatre3.ToBson()), "Original Theatre does not mutate Theatre3 progress");
            AssertEqual(false, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(foreign.Id) || test.Session.player.MissionProgress.ClaimedTaskIds.Contains(theatre3.Id), "Foreign task ownership is not stolen");
            TheatreMetaAssertRewards(test, before, [permanent.RewardId.Value]);
            test.Relog("crossmode-owned-task-claim");
        }
        using (TheatreCase aggregate = new("meta-authored-level-aggregates"))
        {
            TheatreMetaUnlockChapters(aggregate);
            var favorTask = tasks.First(row => conditions[row.Condition].Type == 73007);
            int power = TheatreRows<TheatrePowerConditionTable>().First(row => !(row.ConditionId > 0)).Id;
            var favorRows = TheatreRows<TheatrePowerFavorTable>().Where(row => row.PowerId == power).ToList();
            int required = favorTask.Result ?? 1;
            foreach (var row in favorRows.Where(row => (row.Lv ?? 0) < required))
                if (!aggregate.Data.UnlockPowerFavorIds.Contains(row.Id)) aggregate.Data.UnlockPowerFavorIds.Add(row.Id);
            aggregate.SaveFixture();
            aggregate.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = favorTask.Id },
                "Historical favor rows count highest level per power, never sum of row levels or IDs");
            aggregate.Data.UnlockPowerFavorIds.Add(favorRows.Single(row => row.Lv == required).Id);
            aggregate.SaveFixture();
            var before = aggregate.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            aggregate.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = favorTask.Id });
            TheatreMetaAssertRewards(aggregate, before, [favorTask.RewardId!.Value]);
            var decorationTask = tasks.First(row => conditions[row.Condition].Type == 73005);
            var decorations = TheatreRows<TheatreDecorationTable>();
            var current = decorations.First(row => (row.Lv ?? 0) == 0 && !(row.ConditionId > 0));
            int level = decorationTask.Result ?? 1;
            aggregate.Data.Decorations.RemoveAll(id => decorations.Any(row => row.Id == id && row.DecorationId == current.DecorationId));
            var owned = decorations.Single(row => row.DecorationId == current.DecorationId && row.Lv == level - 1);
            aggregate.Data.Decorations.Add(owned.Id);
            aggregate.SaveFixture();
            aggregate.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = decorationTask.Id }, "Decoration aggregate uses authored level not row identity");
            aggregate.Fund(owned.UpgradeCostItemId!.Value, owned.UpgradeCostCount!.Value);
            aggregate.SaveFixture();
            aggregate.Call(nameof(TheatreDecorationUpgradeRequest), new TheatreDecorationUpgradeRequest { DecorationId = owned.Id });
            var afterUpgrade = aggregate.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            aggregate.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = decorationTask.Id });
            TheatreMetaAssertRewards(aggregate, afterUpgrade, [decorationTask.RewardId!.Value]);
        }
    }

    private static void TheatreMetaCommonShops()
    {
        var guide = TheatreRows<AscNet.Table.V2.share.wheelchairmanual.WheelchairManualGuideActivityTable>().Single(row => row.Id == 10008);
        foreach (string key in new[] { "ShopIdByNormal", "ShopIdBySpecial" })
        {
            using TheatreCase test = new($"meta-common-{key}");
            uint shopId = checked((uint)TheatreRows<TheatreConfigTable>().Single(row => row.Key == key).Value);
            var shop = (GetShopInfoResponse.GetShopInfoResponseClientShop)RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ShopModule"), "ShopCatalog",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(uint)]).Invoke(null, [shopId])!;
            var offer = shop.GoodsList.First(row => row.RewardGoods.RewardType == 1 && row.RewardGoods.Count > 0
                && row.ConditionIds.Count == 0 && row.AutoResetClockId == 0
                && (row.BuyTimesLimit == 0 || row.BuyTimesLimit >= 3)
                && row.ConsumeList.Count > 0 && row.ConsumeList.All(cost => cost.Id > 0 && cost.Count > 0));
            int rewardItem = checked((int)offer.RewardGoods.TemplateId);
            var costs = offer.ConsumeList.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.Sum(row => (long)row.Count));
            var request = new BuyRequest { ShopId = shopId, GoodsId = offer.Id, Count = 1 };
            foreach (var cost in costs) test.Fund(cost.Key, checked((int)cost.Value - 1));
            test.SaveFixture();
            test.Reject(nameof(BuyRequest), request, "Common Theatre shop cannot overdraft");
            foreach (var cost in costs) test.Fund(cost.Key, checked((int)(cost.Value * 3)));
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);

            void AssertPurchase(int total, params (int Previous, int Delta)[] purchases)
            {
                foreach (int item in before.Keys.Concat(test.Session.inventory.Items.Select(row => row.Id)).Append(rewardItem).Distinct())
                    AssertEqual(before.GetValueOrDefault(item) - costs.GetValueOrDefault(item) * total
                        + (item == rewardItem ? (long)offer.RewardGoods.Count * total : 0), test.Balance(item),
                        "Common shop grants authored goods and charges only the requested delta");
                AssertEqual(total, test.Session.player.ShopBuyTimes[offer.Id], "Buy Count is a delta, not previous plus delta");
                var saved = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!);
                AssertEqual(total, saved.ShopBuyTimes[offer.Id], "Common-shop purchase count is durable");
                foreach (var purchase in purchases)
                {
                    string claimKey = $"theatre-shop:{shopId}:{offer.Id}:{purchase.Previous}";
                    var receipt = test.Session.player.WheelchairManualGuideRewardReceipts.Single(row => row.ClaimKey == claimKey);
                    AssertEqual(key == "ShopIdByNormal" ? 92004 : 92008, receipt.EventCause, "Common shop normal/special guide cause");
                    AssertEqual(true, guide.EventCause.Contains(receipt.EventCause), "Guide10008 owns the common-shop producer");
                    AssertEqual(1, receipt.Goods.Count, "Guide receipt contains the purchased reward, not the cost");
                    AssertEqual(rewardItem, receipt.Goods.Single().TemplateId, "Guide receipt names the actual reward item");
                    AssertEqual(checked(offer.RewardGoods.Count * purchase.Delta), receipt.Goods.Single().Count,
                        "Guide receipt contains exactly the purchased reward delta");
                    AssertEqual(1, saved.WheelchairManualGuideRewardReceipts.Count(row => row.ClaimKey == claimKey),
                        "Guide attribution is durable and unique per purchase");
                }
            }

            void AssertReplay(BuyRequest intent, int packet, byte[] response)
            {
                byte[] durable = test.Players.LastSuccessfulReplacementBson!.ToArray();
                byte[] inventory = test.Session.inventory.ToBson();
                byte[] state = test.State.ToBson();
                test.Call(nameof(BuyRequest), intent, reusePacketId: packet);
                AssertEqual(true, response.SequenceEqual(test.LastResponseContent), "Committed Buy replay returns its frozen response");
                AssertEqual(true, durable.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Committed Buy replay does not write a new purchase");
                AssertEqual(true, inventory.SequenceEqual(test.Session.inventory.ToBson()), "Committed Buy replay does not charge or grant twice");
                AssertEqual(true, state.SequenceEqual(test.State.ToBson()), "Committed Buy replay preserves Theatre state");
                // Buy's afterCommit callback also sends the common task snapshot on receipt replay.
                AssertEqual(false, test.Pushes.Any(push => push.Name != nameof(NotifyTask)), "Committed Buy replay emits no additive reward or mode pushes");
            }

            test.Call(nameof(BuyRequest), request);
            AssertPurchase(1, (0, 1));
            AssertReplay(request, test.LastPacketId, test.LastResponseContent.ToArray());

            var second = new BuyRequest { ShopId = shopId, GoodsId = offer.Id, Count = 2 };
            byte[] inventoryBefore = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            test.Inventories.ThrowOnReplaceOne = true;
            try { test.Call(nameof(BuyRequest), second, success: false); }
            finally { test.Inventories.ThrowOnReplaceOne = false; }
            AssertEqual(0, test.Pushes.Count, "Failed Buy emits no success pushes");
            int failedPacket = test.LastPacketId;
            byte[] savedIntent = test.Players.LastSuccessfulReplacementBson!.ToArray();
            var pending = BsonSerializer.Deserialize<Player>(savedIntent).Theatre.PendingMutation
                ?? throw new InvalidDataException("Failed Buy must retain a durable pending purchase.");
            AssertEqual(true, inventoryBefore.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Failed inventory write leaves durable balances unchanged");

            test.Reject(nameof(BuyRequest), request, "Changed Count conflicts with pending Buy intent");
            AssertEqual(1, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "Conflicting pending Buy is busy");
            AssertEqual(true, savedIntent.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Conflicting Buy cannot replace the pending journal");
            test.Call(nameof(BuyRequest), second);
            int retryPacket = test.LastPacketId;
            AssertEqual(true, retryPacket != failedPacket, "Pending Buy retries on a new packet with the same semantic intent");
            AssertEqual(true, pending.Response!.SequenceEqual(test.LastResponseContent), "Pending Buy recovery returns the frozen response");
            AssertEqual(true, test.State.PendingMutation is null, "Recovered Buy clears the pending journal");
            var responseGoods = MessagePackSerializer.Deserialize<BuyResponse>(test.LastResponseContent).GoodList.Single();
            AssertEqual(rewardItem, responseGoods.TemplateId, "Recovered Buy response identifies the actual reward");
            AssertEqual(checked(offer.RewardGoods.Count * second.Count), responseGoods.Count, "Recovered Buy response reports only the second purchase delta");
            AssertPurchase(3, (0, 1), (1, 2));
            byte[] frozenResponse = test.LastResponseContent.ToArray();
            AssertReplay(second, retryPacket, frozenResponse);
            AssertReplay(second, failedPacket, frozenResponse);
            AssertPurchase(3, (0, 1), (1, 2));
            test.Relog($"common-shop-{shopId}");
            AssertPurchase(3, (0, 1), (1, 2));
        }
    }
    private static void ValidateTheatreLiveRecoveryCompatibility()
    {
        using TheatreCase test = new("live-recovery");
        var decorations = TheatreRows<TheatreDecorationTable>();
        var initial = decorations.Where(row => row.Type.Select((type, index) => type == 1 && row.Param[index] > 0).Any(value => value))
            .OrderBy(row => row.Lv ?? 0).First();
        test.Data.Decorations.RemoveAll(id => decorations.Single(row => row.Id == id).DecorationId == initial.DecorationId);
        test.Data.Decorations.Add(initial.Id);
        int initialCoin = decorations.Where(row => test.Data.Decorations.Contains(row.Id))
            .GroupBy(row => row.DecorationId).Select(group => group.MaxBy(row => row.Lv ?? 0)!)
            .Sum(row => row.Type.Select((type, index) => type == 1 ? checked((int)row.Param[index]) : 0).Sum());
        test.SaveFixture();
        int difficulty = TheatreRows<TheatreDifficultyTable>().OrderBy(row => row.Id)
            .First(row => (bool)TheatreInvoke("IsConditionSatisfied", test, row.ConditionId ?? 0)!).Id;
        test.Call(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = difficulty, KeepsakeId = 0 });
        AssertEqual((long)initialCoin, test.Balance(96101), "Current decoration is the new run's initial coin balance");
        test.Replay("initial coin exact start receipt");
        test.Relog("initial coin committed start");
        AssertEqual((long)initialCoin, test.Balance(96101), "Login does not grant initial coin again");
        AssertEqual(0, test.Data.UseOwnCharacter, "No trial roster has earned own-character permission before recruitment");
        int role = test.Data.CurChapterDb!.RefreshRole.First(id =>
            TheatreRows<TheatreRoleAttrTable>().Single(row => row.RoleId == id && row.Lv == test.Data.CurRoleLv).FightAbility
                >= TheatreRows<TheatreConfigTable>().Single(row => row.Key == "UseOwnCharacterFa").Value);
        test.Call(nameof(TheatreRecruitCharacterRequest), new TheatreRecruitCharacterRequest { RoleId = role });
        AssertEqual(0, test.Data.KeepsakeId, "Permission fixture has no keepsake");
        AssertEqual(1, test.Data.UseOwnCharacter, "Authored recruited trial mean BP earns own-character permission");
        var permission = MessagePackSerializer.Deserialize<NotifyTheatreUseOwnCharacter>(
            test.Pushes.Single(push => push.Name == nameof(NotifyTheatreUseOwnCharacter)).Content);
        AssertEqual(1, permission.UseOwnCharacter, "Recruitment delivers earned permission to the client");
        test.Replay("earned own-character permission");
        test.Relog("earned own-character permission");
        AssertEqual(1, test.Login().UseOwnCharacter, "Earned permission survives login snapshot");
        test.Recruit();
        test.Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(test) });

        int firstChapter = test.Data.CurChapterDb!.ChapterId;
        int limit = TheatreRows<TheatreNodeTable>().Count * 128;
        for (int transition = 0; transition < limit && test.Data.CurChapterDb is { } chapter
            && (chapter.ChapterId == firstChapter || chapter.PassNodeCount == 0); transition++)
            AdvanceTheatre(test);
        AssertEqual(true, test.Data.CurChapterDb is not null && test.Data.CurChapterDb.ChapterId != firstChapter,
            "Recovery fixture reaches a later live chapter, not another complete run");
        int chapterCount = test.Data.CurChapterDb!.PassNodeCount;
        int cumulativeCount = test.Data.PassNodeCount;
        AssertEqual(true, chapterCount > 0 && cumulativeCount > chapterCount, "Fixture distinguishes chapter count from cumulative progress");
        test.Relog("later chapter pass count");
        AssertEqual(cumulativeCount, test.Data.PassNodeCount, "Relog preserves server cumulative pass count");
        AssertEqual(chapterCount, test.Data.CurChapterDb!.PassNodeCount, "Relog preserves durable current-chapter pass count");
        var liveLogin = test.Login();
        AssertEqual(chapterCount, liveLogin.PassNodeCount, "Login top-level count initializes only the current chapter");
        AssertEqual(chapterCount, liveLogin.CurChapterDb!.PassNodeCount, "Login nested current-chapter count agrees");
        var sendRecovered = RequiredMethod(TheatreModuleType, "SendRecoveredSettlement", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        sendRecovered.Invoke(null, [test.Session]);
        AssertEqual(false, test.Harness.TryReadAvailablePacket("live adventure has no recovered ending", out _), "Live adventure does not receive a stale ending");

        test.Call(nameof(TheatreSettleAdventureRequest));
        byte[] settledResponse = test.LastResponseContent.ToArray();
        var settle = MessagePackSerializer.Deserialize<TheatreSettleAdventureResponse>(settledResponse).SettleData!;
        AssertEqual(true, test.Data.CurChapterDb is null && test.State.SettlementRecoveryPending, "Settlement closes durable adventure and retains presentation recovery");
        test.Replay("final presentation settlement receipt");
        long decorationBalance = test.Balance(96102), favorBalance = test.Balance(96103);
        byte[] retainedChapter = MessagePackSerializer.Serialize(test.State.LastRunData!.CurChapterDb);
        for (int login = 0; login < 2; login++)
        {
            test.Relog($"final presentation {login}");
            byte[] durable = test.State.ToBson();
            test.Session.SendPush(test.Login());
            sendRecovered.Invoke(null, [test.Session]);
            Packet snapshotPacket = test.Harness.ReadPacket("recovered Theatre snapshot");
            AssertEqual(Packet.ContentType.Push, snapshotPacket.Type, "Recovery starts with a real snapshot push");
            var snapshotPush = MessagePackSerializer.Deserialize<Packet.Push>(snapshotPacket.Content);
            AssertEqual(nameof(NotifyTheatreData), snapshotPush.Name, "Final recovery snapshot precedes ending");
            var snapshot = MessagePackSerializer.Deserialize<NotifyTheatreData>(snapshotPush.Content);
            AssertEqual(true, retainedChapter.SequenceEqual(MessagePackSerializer.Serialize(snapshot.CurChapterDb)), "Final recovery restores exact retained adventure chapter");
            AssertEqual(chapterCount, snapshot.PassNodeCount, "Final recovery snapshot retains chapter-local pass count");
            AssertEqual(true, snapshot.EndingRecord.Contains(settle.Ending), "Recovered adventure contains settled ending meta");
            Packet endingPacket = test.Harness.ReadPacket("recovered Theatre ending");
            AssertEqual(Packet.ContentType.Push, endingPacket.Type, "Actual recovery sender emits a push");
            var endingPush = MessagePackSerializer.Deserialize<Packet.Push>(endingPacket.Content);
            AssertEqual(nameof(NotifyTheatreAdventureSettle), endingPush.Name, "Final recovery delivers ending after snapshot");
            var ending = MessagePackSerializer.Deserialize<NotifyTheatreAdventureSettle>(endingPush.Content);
            AssertEqual(true, MessagePackSerializer.Serialize(settle).SequenceEqual(MessagePackSerializer.Serialize(ending.SettleData)), "Recovery delivers exact frozen settlement");
            AssertEqual(false, test.Harness.TryReadAvailablePacket("recovery has no additive reward pushes", out _), "Recovery sends only snapshot and ending");
            AssertEqual(true, durable.SequenceEqual(test.State.ToBson()), "Final presentation does not reactivate or mutate durable run");
            AssertEqual(decorationBalance, test.Balance(96102), "Ending recovery does not grant inspiration again");
            AssertEqual(favorBalance, test.Balance(96103), "Ending recovery does not grant cadenza again");
        }
    }
    // Source-derived coverage: every authored event step and visible option kind,
    // all six node kinds, frozen shops, and current-row currency modifiers;
    // keepsake node thresholds; grid acquisition/upgrade/replacement, passive acquisition,
    // decline and level-based robot remapping. No RNG branch hunting.
    private static void ValidateTheatreNodeCompatibility()
    {
        using TheatreCase test = new("node-branches");
        test.Start();
        test.Reject(nameof(TheatreSelectNodeRequest), new TheatreSelectNodeRequest { NodeId = int.MaxValue, SlotId = int.MaxValue }, "Forged node identity");
        test.Reject(nameof(TheatreEndNodeRequest), null, "Unselected node cannot end");
        var events = TheatreRows<TheatreEventTable>().ToList();
        var targets = events.Where(row => row.Type != 2).GroupBy(row => row.Type)
            .Select(group => (Row: group.First(), Option: 0)).ToList();
        targets.AddRange(events.Where(row => row.Type == 2)
            .SelectMany(row => row.OptionDesc.Select((description, index) => (Row: row, Option: index + 1, Description: description)))
            .Where(value => !string.IsNullOrWhiteSpace(value.Description))
            .GroupBy(value => value.Row.OptionType[value.Option - 1])
            .Select(group => (group.First().Row, group.First().Option)));
        foreach (var target in targets)
        {
            var row = target.Row;
            TheatreSlot slot = TheatreNodeEventFixture(test, row);
            test.SaveFixture();
            test.Relog($"event {row.EventId}/{row.StepId}");
            test.Reject(nameof(TheatreEndNodeRequest), null, "Event cannot bypass its current step");
            test.Reject(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = int.MaxValue }, "Stale event step");
            if (row.Type == 5)
            {
                test.Reject(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = row.StepId }, "Battle step requires fight settlement");
                TheatreNodeRecruit(test);
                TheatreFight(test, test.Data.CurChapterDb!.CurNodeDb.Slots.Single(value => value.SlotId == slot.SlotId));
            }
            else
            {
                int index = target.Option - 1;
                int kind = index >= 0 ? row.OptionType[index] : 0;
                int item = kind is 1 or 2 ? row.OptionItemId[index] : row.Type == 4 ? row.StepRewardItemId!.Value : 0;
                int count = kind is 1 or 2 ? row.OptionItemCount[index] : row.Type == 4 ? row.StepRewardItemCount!.Value : 0;
                if (kind is 1 or 2)
                {
                    test.Fund(item, count - 1);
                    test.SaveFixture();
                    test.Reject(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = row.StepId, OptionId = target.Option }, "Item-gated event fails atomically");
                    test.Fund(item, count);
                    test.SaveFixture();
                }
                if (row.Type == 2)
                    test.Reject(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = row.StepId, OptionId = row.OptionDesc.Count + 1 }, "Unoffered authored option index");
                long before = item > 0 ? test.Balance(item) : 0;
                JObject response = test.Call(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = row.StepId, OptionId = target.Option });
                int next = row.Type == 2 ? row.OptionNextStepId[index] : row.NextStepId ?? 0;
                AssertEqual(next, response.Value<int>("NextStepId"), "Event follows authored edge");
                if (item > 0)
                    AssertEqual(before + (row.Type == 4 ? count : kind == 1 ? -count : 0), test.Balance(item), "Consume, check-only, and permanent reward have distinct economics");
                if (row.Type == 4)
                    AssertEqual(count, response["RewardGoodsList"]!.Single()!.Value<int>("Count"), "Event response carries authored reward count");
            }
            AssertEqual(true, test.Data.PassEventRecord[row.EventId][row.StepId], "Event records source step once");
        }

        var decorations = TheatreRows<TheatreDecorationTable>().ToList();
        test.Data.Decorations = decorations.Where(row => row.Type.Contains(5) || row.Type.Contains(4) || row.Type.Contains(12) || row.Type.Contains(13))
            .GroupBy(row => row.DecorationId).Select(group => group.MaxBy(row => row.Lv ?? 0)!.Id).ToList();
        // Normalize the persisted progression fixture through the real login path before freezing offers.
        RequiredMethod(TheatreModuleType, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [test.Session]);
        var shop = TheatreRows<TheatreNodeShopTable>().First();
        TheatreNodePosition(test, TheatreRows<TheatreNodeTable>().First(row => row.Type.Select((type, index) => type == 2 && row.Param[index] == shop.Id.ToString()).Any(value => value)));
        var shopSlot = TheatreFixtureSlot(test, new TheatreSlot { SlotType = 2, Selected = 1 });
        TheatreInvoke("InitialiseShopSlot", test, shopSlot, shop.Id);
        test.SaveFixture();
        AssertEqual(4, shopSlot.ShopItems.Count, "Highest authored stock bonus exposes all four offer kinds");
        decimal Factor(int type) => decorations.Where(row => test.Data.Decorations.Contains(row.Id))
            .GroupBy(row => row.DecorationId).Select(group => group.MaxBy(row => row.Lv ?? 0)!)
            .SelectMany(row => row.Type.Select((value, index) => value == type ? (decimal)row.Param[index] : 1m)).Aggregate(1m, (a, b) => a * b);
        var prices = new Dictionary<int, int> { [1] = shop.SkillPrice, [2] = shop.LvPrice, [3] = shop.DecorationPrice, [4] = shop.FavorPrice };
        foreach (var offer in shopSlot.ShopItems)
            AssertEqual((int)decimal.Floor(prices[offer.ItemType] * Factor(4)), offer.Price, "Shop uses highest current decoration row, not accumulated levels");
        byte[] frozen = MessagePackSerializer.Serialize(shopSlot.ShopItems);
        test.Relog("frozen source shop");
        AssertEqual(true, frozen.SequenceEqual(MessagePackSerializer.Serialize(test.Data.CurChapterDb!.CurNodeDb.Slots.Single().ShopItems)), "Relog preserves complete stock");
        test.Reject(nameof(TheatreNodeShopBuyItemRequest), new TheatreNodeShopBuyItemRequest { Type = 96101 }, "Reward enum is not inventory identity");
        test.Reject(nameof(TheatreNodeShopBuyItemRequest), new TheatreNodeShopBuyItemRequest { Type = 1 }, "Skill purchase requires opening frozen choice");
        JObject opened = test.Call(nameof(TheatreNodeShopOpenSkillRequest));
        JObject reopened = test.Call(nameof(TheatreNodeShopOpenSkillRequest));
        AssertEqual(true, JToken.DeepEquals(opened["Skills"], reopened["Skills"]), "Repeated open cannot reroll skills");
        // Valid accumulated rewards from earlier nodes make absolute totals distinguishable from deltas.
        test.Data.DecorationCoin = shop.DecorationCount;
        test.Data.FavorCoin = shop.FavorCount;
        test.SaveFixture();
        foreach (int type in new[] { 1, 2, 3, 4 })
        {
            var offer = test.Data.CurChapterDb!.CurNodeDb.Slots.Single().ShopItems.Single(value => value.ItemType == type);
            test.Fund(96101, offer.Price - 1);
            test.SaveFixture();
            test.Reject(nameof(TheatreNodeShopBuyItemRequest), new TheatreNodeShopBuyItemRequest { Type = type }, "Insufficient shop coin preserves stock and balances");
            test.Fund(96101, offer.Price);
            test.SaveFixture();
            int inspiration = test.Data.DecorationCoin, favor = test.Data.FavorCoin, level = test.Data.CurRoleLv;
            test.Call(nameof(TheatreNodeShopBuyItemRequest), new TheatreNodeShopBuyItemRequest { Type = type });
            var purchasePushes = test.Pushes.ToList();
            test.Replay("node shop purchase");
            AssertEqual(0L, test.Balance(96101), "Purchase spends frozen price exactly once");
            if (type == 1)
            {
                AssertEqual(true, offer.Skills.SequenceEqual(test.Data.CurChapterDb!.SkillToSelect), "Purchased choice equals opened offer");
                test.Reject(nameof(TheatreEndNodeRequest), null, "Cannot leave with pending skill choice");
                test.Call(nameof(TheatreSkipSelectSkillRequest));
                test.Relog("bought skill shop reentry");
                byte[] boughtInventory = test.Session.inventory.ToBson();
                JObject reentered = test.Call(nameof(TheatreNodeShopOpenSkillRequest));
                AssertEqual(true, JToken.DeepEquals(opened, reentered), "Bought skill shop reentry returns the original frozen choice");
                AssertEqual(1, CurrentTheatreSlot(test).ShopItems.Single(value => value.ItemType == 1).IsBuy, "Reentry preserves sold state");
                AssertEqual(true, boughtInventory.SequenceEqual(test.Session.inventory.ToBson()), "Reopening sold skill does not pay or grant again");
            }
            if (type == 2) AssertEqual(TheatreRows<TheatreLvTable>().Where(row => row.Lv > level).OrderBy(row => row.Lv).Select(row => row.Lv).DefaultIfEmpty(level).First(), test.Data.CurRoleLv, "Shop level reward advances actual authored level");
            if (type == 3) AssertEqual(inspiration + (int)decimal.Floor(shop.DecorationCount * Factor(12)), test.Data.DecorationCoin, "Current-row inspiration factor");
            if (type == 4) AssertEqual(favor + (int)decimal.Floor(shop.FavorCount * Factor(13)), test.Data.FavorCoin, "Current-row cadenza factor");
            if (type is 3 or 4)
            {
                var absolute = MessagePackSerializer.Deserialize<NotifyTheatreCoinChange>(purchasePushes.Single(push => push.Name == nameof(NotifyTheatreCoinChange)).Content);
                AssertEqual(test.Data.DecorationCoin, absolute.DecorationCoin, "Live shop inspiration notification is absolute accumulated total");
                AssertEqual(test.Data.FavorCoin, absolute.FavorCoin, "Live shop cadenza notification is absolute accumulated total");
                AssertEqual(inspiration + (type == 3 ? (int)decimal.Floor(shop.DecorationCount * Factor(12)) : 0), absolute.DecorationCoin, "Currency purchase retains accumulated inspiration");
                AssertEqual(favor + (type == 4 ? (int)decimal.Floor(shop.FavorCount * Factor(13)) : 0), absolute.FavorCoin, "Currency purchase retains accumulated cadenza");
                AssertEqual(true, purchasePushes.FindIndex(push => push.Name == nameof(NotifyTheatreCoinChange))
                    < purchasePushes.FindIndex(push => push.Name == nameof(NotifyTheatreNodeReward)), "Absolute totals arrive before the reward popup callback");
            }
            test.Reject(nameof(TheatreNodeShopBuyItemRequest), new TheatreNodeShopBuyItemRequest { Type = type }, "Bought offer cannot be purchased again");
        }
        test.Call(nameof(TheatreEndNodeRequest));

        var token = TheatreRows<TheatreItemTable>().First(row => row.Type == 1 && row.Lv == 2 && row.FightCount > 0);
        test.Data.KeepsakeId = token.KeepsakeId!.Value;
        test.Data.Keepsakes = [new TheatreKeepsake { KeepsakeId = token.KeepsakeId.Value, Lv = token.Lv!.Value, FightCount = token.FightCount!.Value - 1 }];
        var movieNode = TheatreRows<TheatreNodeTable>().First(row => row.Type.Contains(6));
        TheatreNodePosition(test, movieNode);
        TheatreFixtureSlot(test, new TheatreSlot { SlotType = 6, Selected = 1, StoryId = movieNode.Param[movieNode.Type.IndexOf(6)] });
        int passed = test.Data.PassNodeCount, nodes = test.State.RunNodeCount;
        test.SaveFixture();
        test.Call(nameof(TheatreEndNodeRequest));
        AssertEqual(passed, test.Data.PassNodeCount, "Movie excluded from client passed-node count");
        AssertEqual(nodes + 1, test.State.RunNodeCount, "Movie still completes a server node");
        AssertEqual(3, test.Data.Keepsakes.Single().Lv, "Keepsake upgrades using current level's node threshold");
        AssertEqual(0, test.Data.Keepsakes.Single().FightCount, "Keepsake carries only threshold remainder");
        test.Relog("keepsake upgrade");

        foreach (int type in new[] { 3, 4, 5 })
        {
            var node = TheatreRows<TheatreNodeTable>().First(row => type == 4 ? row.IsRandom == 1 : row.Type.Contains(type));
            TheatreNodePosition(test, node);
            var stage = type == 4
                ? TheatreRows<TheatreStageTable>().First(row => row.ChapterId == node.ChapterId && row.IsRandom == 1 && row.Weight > 0 && row.StageCount == 1)
                : TheatreRows<TheatreStageTable>().Single(row => row.Id == int.Parse(node.Param[node.Type.IndexOf(type)]));
            var slot = TheatreFixtureSlot(test, new TheatreSlot { SlotType = type, Selected = 1, TheatreStageId = stage.Id, StageIds = stage.StageId.Where(id => id > 0).ToList() });
            test.SaveFixture();
            TheatreNodeRecruit(test);
            test.Reject(nameof(TheatreEndNodeRequest), null, "Combat node cannot bypass fight");
            int fights = test.State.RunFightCount;
            int passedBefore = test.Data.PassNodeCount;
            foreach (int index in Enumerable.Range(1, slot.StageIds.Count))
                TheatreFight(test, test.Data.CurChapterDb!.CurNodeDb.Slots.Single(value => value.SlotId == slot.SlotId), slot.StageIds.Count > 1 ? index : 0);
            if (test.Data.CurChapterDb is not null)
            {
                AssertEqual(fights + slot.StageIds.Count, test.State.RunFightCount, $"Battle node kind {type} completes through fight");
                AssertEqual(passedBefore + 1, test.Data.PassNodeCount, "Completed battle advances active passed-node count");
            }
            else
            {
                var saved = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Terminal node has no durable player snapshot.")).Theatre;
                var settled = saved.LastSettle ?? throw new InvalidDataException("Terminal battle has no durable settlement.");
                var retained = saved.LastRunData ?? throw new InvalidDataException("Terminal battle has no retained adventure.");
                AssertEqual(fights + slot.StageIds.Count, settled.SettleFightCount, "Terminal settlement retains completed fight count before active reset");
                AssertEqual(passedBefore + 1, retained.PassNodeCount, "Terminal presentation retains completed passed-node count");
                AssertEqual(saved.RunId, saved.SettledRunId, "Terminal node settles its own durable run");
                AssertEqual(true, saved.Data.EndingRecord.Contains(settled.Ending), "Terminal ending is recorded in permanent state");
                AssertEqual(true, retained.CurChapterDb is not null, "Terminal presentation retains its chapter");
            }
        }
    }

    private static TheatreSlot TheatreNodeEventFixture(TheatreCase test, TheatreEventTable row)
    {
        TheatreNodePosition(test, TheatreRows<TheatreNodeTable>().First(node => node.Type.Select((type, index) => type == 1 && node.Param[index] == row.EventId.ToString()).Any(value => value)));
        // Reconstruct an authored path to the offered step, never fabricate a step number.
        var rows = TheatreRows<TheatreEventTable>().Where(value => value.EventId == row.EventId).OrderBy(value => value.Id).ToList();
        var paths = new Queue<List<int>>();
        paths.Enqueue([rows[0].StepId]);
        List<int>? path = null;
        while (paths.Count > 0)
        {
            var candidate = paths.Dequeue();
            if (candidate[^1] == row.StepId) { path = candidate; break; }
            var current = rows.Single(value => value.StepId == candidate[^1]);
            IEnumerable<int> next = current.Type == 2 ? current.OptionNextStepId : new[] { current.NextStepId ?? 0 };
            foreach (int step in next.Where(step => step > 0 && !candidate.Contains(step)).Distinct())
                paths.Enqueue([.. candidate, step]);
        }
        if (path is null) throw new InvalidDataException("Event fixture has no authored path from entry.");
        var slot = TheatreFixtureSlot(test, new TheatreSlot
        {
            SlotType = 1,
            Selected = 1,
            ConfigId = row.EventId,
            CurStepId = row.StepId,
            StageIds = row.Type == 5 ? [row.StageId!.Value] : [],
            StoryId = row.Type == 6 ? row.StoryId! : string.Empty
        });
        test.State.EventVisitedSteps[slot.SlotId] = path.Take(path.Count - 1).ToList();
        if (!test.Data.PassEventRecord.TryGetValue(row.EventId, out var record))
            test.Data.PassEventRecord[row.EventId] = record = [];
        foreach (int step in test.State.EventVisitedSteps[slot.SlotId]) record[step] = true;
        return slot;
    }

    private static void ValidateTheatreSkillCompatibility()
    {
        using TheatreCase test = new("skill-branches");
        test.Start();
        var skills = TheatreRows<TheatreSkillTable>().ToList();
        var initial = skills.First(row => row.Type == 1 && row.Quality == 1);
        var upgrade = skills.Single(row => row.Type == 1 && row.PowerId == initial.PowerId && row.Pos == initial.Pos && row.Quality == 2);
        var replacement = skills.First(row => row.Type == 1 && row.PowerId != initial.PowerId && row.Pos == initial.Pos && row.Quality == 1);
        var passive = skills.First(row => row.Type == 2 && !(row.NeedPowerFavor > 0));
        test.Data.UnlockPowerIds = skills.Select(row => row.PowerId).Distinct().ToList();
        test.Data.Skills.Clear();
        test.Data.EffectPowerFavorIds.Clear();
        void Offer(int skill)
        {
            var selected = skills.Single(row => row.Id == skill);
            var eligible = skills.Where(row => row.PowerId == selected.PowerId && (row.Type == 2
                ? !(row.NeedPowerFavor > 0) && !test.Data.Skills.Contains(row.Id)
                : row.Quality == Math.Min(skills.Where(value => value.Type == 1 && value.PowerId == row.PowerId && value.Pos == row.Pos).Max(value => value.Quality ?? 0),
                    skills.Where(value => value.Type == 1 && value.PowerId == row.PowerId && value.Pos == row.Pos && test.Data.Skills.Contains(value.Id)).Select(value => (value.Quality ?? 0) + 1).DefaultIfEmpty(1).Single())
                    && !test.Data.Skills.Contains(row.Id))).ToList();
            AssertEqual(true, eligible.Any(row => row.Id == skill), "Fixture skill is eligible for its persisted owned grid");
            var choices = eligible.OrderBy(row => row.Id == skill ? 0 : 1).Take(3).Select(row => row.Id).ToList();
            var shop = TheatreRows<TheatreNodeShopTable>().First();
            TheatreNodePosition(test, TheatreRows<TheatreNodeTable>().First(row => row.Type.Select((type, index) => type == 2 && row.Param[index] == shop.Id.ToString()).Any(value => value)));
            TheatreFixtureSlot(test, new TheatreSlot
            {
                SlotType = 2,
                ConfigId = shop.Id,
                ShopItems =
            [
                new TheatreShopItem { ItemType = 1, Price = shop.SkillPrice, Count = 1, IsBuy = 1, PowerId = selected.PowerId, Skills = choices },
                new TheatreShopItem { ItemType = 2, Price = shop.LvPrice, Count = 1 }
            ]
            });
            test.Data.CurChapterDb!.SkillToSelect = choices.ToList();
            test.State.ShopSkillOpened = true;
            test.State.PendingSkillShopType = 1;
            test.SaveFixture();
        }
        foreach (var selected in new[] { initial, upgrade, replacement, passive })
        {
            Offer(selected.Id);
            test.Relog($"offered skill {selected.Id}");
            test.Reject(nameof(TheatreSelectSkillRequest), new TheatreSelectSkillRequest { SkillId = int.MaxValue }, "Unoffered skill cannot mutate grid");
            test.Call(nameof(TheatreSelectSkillRequest), new TheatreSelectSkillRequest { SkillId = selected.Id });
            AssertEqual(true, test.Data.Skills.Contains(selected.Id), "Offered skill becomes active");
            if (selected.Type == 1)
                AssertEqual(1, skills.Count(row => row.Type == 1 && row.Pos == selected.Pos && test.Data.Skills.Contains(row.Id)), "Upgrade and faction replacement leave one skill in the grid position");
            AssertEqual(true, test.Data.SkillIllustratedBook.Contains(selected.Id), "Acquisition preserves illustration across replacements");
            AssertEqual(0, test.Data.CurChapterDb!.SkillToSelect.Count, "Selection consumes offer");
        }
        AssertEqual(false, test.Data.Skills.Contains(initial.Id) || test.Data.Skills.Contains(upgrade.Id), "Old quality and old faction removed");
        Offer(initial.Id);
        byte[] owned = MessagePackSerializer.Serialize(test.Data.Skills);
        test.Call(nameof(TheatreSkipSelectSkillRequest));
        AssertEqual(true, owned.SequenceEqual(MessagePackSerializer.Serialize(test.Data.Skills)), "Decline preserves owned skills");
        AssertEqual(0, test.Data.CurChapterDb!.SkillToSelect.Count, "Decline consumes the offered choice");
        test.Reject(nameof(TheatreSkipSelectSkillRequest), null, "Cannot decline without an offer");

        TheatreNodeRecruit(test);
        var levels = TheatreRows<TheatreLvTable>().OrderBy(row => row.Id).ToList();
        var attrs = TheatreRows<TheatreRoleAttrTable>().ToList();
        test.Data.CurRoleLv = levels[0].Lv;
        int role = test.Data.RecruitRole.First();
        int robot = attrs.Single(row => row.RoleId == role && row.Lv == levels[0].Lv).RobotId;
        test.Data.SingleTeamData = new TheatreTeamData { TeamIndex = 0, CaptainPos = 1, FirstFightPos = 1, RobotIds = [robot, 0, 0] };
        var multi = TheatreRows<TheatreStageTable>().First(row => row.StageCount > 1);
        var multiNode = TheatreRows<TheatreNodeTable>().First(row => row.Type.Select((type, index) => type is 3 or 4 or 5 && row.Param[index] == multi.Id.ToString()).Any(value => value));
        TheatreNodePosition(test, multiNode);
        TheatreFixtureSlot(test, new TheatreSlot { SlotType = multiNode.Type[multiNode.Param.IndexOf(multi.Id.ToString())], TheatreStageId = multi.Id, StageIds = multi.StageId.Where(id => id > 0).ToList() });
        TheatreNodeRecruit(test);
        test.Data.MultiTeamDatas = Enumerable.Range(1, multi.StageCount).Select(index =>
            new TheatreTeamData
            {
                TeamIndex = index,
                CaptainPos = 1,
                FirstFightPos = 1,
                RobotIds = [attrs.Single(row => row.RoleId == test.Data.RecruitRole[index - 1] && row.Lv == levels[0].Lv).RobotId, 0, 0]
            }).ToList();
        test.SaveFixture();
        TheatreNodeLevelFixture(test, 1);
        AssertEqual(levels[1].Lv, test.Data.CurRoleLv, "Role level follows authored level domain");
        int expected = attrs.Single(row => row.RoleId == role && row.Lv == levels[1].Lv).RobotId;
        AssertEqual(expected, test.Data.SingleTeamData!.RobotIds[0], "Single team robot remapped by role and authored level");
        foreach (var team in test.Data.MultiTeamDatas)
            AssertEqual(attrs.Single(row => row.RoleId == test.Data.RecruitRole[team.TeamIndex - 1] && row.Lv == levels[1].Lv).RobotId,
                team.RobotIds[0], "Every multi team robot remapped by role and authored level");
        AssertEqual(true, test.Data.SingleTeamData.RobotIds.Skip(1).All(id => id == 0), "Empty team slots survive remap");
        TheatreNodeLevelFixture(test, levels.Count + 1);
        AssertEqual(levels[^1].Lv, test.Data.CurRoleLv, "Level reward caps at last authored level");
        test.SaveFixture();
        test.Relog("level remapped teams");
    }

    private static void TheatreNodeLevelFixture(TheatreCase test, int count)
    {
        object mutation = TheatreMutation(test);
        TheatreCall("QueueLevelUp", mutation, count);
        test.Session.player.Theatre = (PlayerTheatreState)mutation.GetType().GetProperty("State")!.GetValue(mutation)!;
    }

    private static void TheatreNodePosition(TheatreCase test, TheatreNodeTable node)
    {
        test.Data.CurChapterDb ??= new TheatreChapterDb();
        test.Data.CurChapterDb.ChapterId = node.ChapterId;
        test.Data.CurChapterDb.CurNodeDb.NodeId = node.Id;
        test.State.NodeCursor = TheatreRows<TheatreNodeTable>().Where(row => row.ChapterId == node.ChapterId).OrderBy(row => row.Id).ToList().FindIndex(row => row.Id == node.Id);
        test.Data.CurChapterDb.PassNodeCount = test.State.NodeCursor;
    }

    private static void TheatreNodeRecruit(TheatreCase test)
    {
        var chapter = TheatreRows<TheatreChapterTable>().Single(row => row.Id == test.Data.CurChapterDb!.ChapterId);
        var active = TheatreRows<TheatreDecorationTable>().Where(row => test.Data.Decorations.Contains(row.Id))
            .GroupBy(row => row.DecorationId).Select(group => group.MaxBy(row => row.Lv ?? 0)!).ToList();
        int refreshBonus = active.Sum(row => row.Type.Select((type, index) => type == 11 ? (int)row.Param[index] : 0).Sum());
        while ((int)(TheatreInvoke("RemainingRecruitCount", test)
            ?? throw new InvalidDataException("RemainingRecruitCount returned no value.")) > 0)
        {
            int role = test.Data.CurChapterDb!.RefreshRole.FirstOrDefault(id => !test.Data.RecruitRole.Contains(id));
            if (role > 0) test.Call(nameof(TheatreRecruitCharacterRequest), new TheatreRecruitCharacterRequest { RoleId = role });
            else if (test.Data.CurChapterDb.RefreshRoleCount < chapter.RecruitRefreshCount + refreshBonus)
                test.Call(nameof(TheatreRefreshCharacterRequest));
            else break;
        }
    }

    // One finite, source-defined transition per call; callers retain their adventure guard.
    private static void AdvanceTheatre(TheatreCase test)
    {
        var chapter = test.Data.CurChapterDb ?? throw new InvalidOperationException("No active Theatre adventure.");
        if (chapter.SkillToSelect.Count > 0)
        {
            // Keep the complete-run route independent of finite skill-pool exhaustion.
            test.Call(nameof(TheatreSkipSelectSkillRequest));
            return;
        }
        var slot = chapter.CurNodeDb.Slots.SingleOrDefault(value => value.Selected == 1);
        if (slot == null)
        {
            TheatreNodeRecruit(test);
            slot = test.Data.CurChapterDb!.CurNodeDb.Slots.OrderBy(value => value.SlotType is 3 or 4 or 5 ? 0 : value.SlotType).First();
            test.Call(nameof(TheatreSelectNodeRequest), new TheatreSelectNodeRequest { NodeId = chapter.CurNodeDb.NodeId, SlotId = slot.SlotId });
            return;
        }
        if (slot.SlotType is 2 or 6) { test.Call(nameof(TheatreEndNodeRequest)); return; }
        if (slot.SlotType is 3 or 4 or 5)
        {
            int stageIndex = Enumerable.Range(1, slot.StageIds.Count).First(index => !slot.PassedStageIndexs.Contains(index));
            TheatreFight(test, slot, slot.StageIds.Count > 1 ? stageIndex : 0);
            return;
        }
        if (slot.SlotType != 1) throw new InvalidDataException($"Unsupported Theatre slot kind {slot.SlotType}.");
        var row = TheatreRows<TheatreEventTable>().Single(value => value.EventId == slot.ConfigId && value.StepId == slot.CurStepId);
        if (row.Type == 5) { TheatreFight(test, slot); return; }
        int option = 0;
        if (row.Type == 2)
        {
            option = Enumerable.Range(0, row.OptionDesc.Count).Where(index => !string.IsNullOrWhiteSpace(row.OptionDesc[index])
                && (row.OptionType[index] is 3 or 4 || row.OptionType[index] is 1 or 2
                    && test.Balance(row.OptionItemId[index]) >= row.OptionItemCount[index])).Select(index => index + 1).FirstOrDefault();
            AssertEqual(true, option > 0, $"Event {row.EventId}/{row.StepId} has an affordable authored option");
        }
        test.Call(nameof(TheatreEventNodeNextStepRequest), new TheatreEventNodeNextStepRequest { CurStepId = row.StepId, OptionId = option });
    }
    private static void ValidateTheatreMargStartCompatibility()
    {
        var decoration = TheatreRows<TheatreDecorationTable>().OrderBy(row => row.Id).First(row =>
            row.Type.Select((type, index) => type == 1 ? checked((int)row.Param[index]) : 0).Sum() > 0);
        int bonus = decoration.Type.Select((type, index) => type == 1 ? checked((int)decoration.Param[index]) : 0).Sum();
        foreach (var (carry, preGrant) in new[] { (137, true), (911, false) })
        {
            using TheatreCase test = new($"marg-start-{carry}");
            test.Data.Decorations = [decoration.Id];
            test.Fund(96101, carry);
            test.SaveFixture();
            var taskProgress = test.State.TaskProgress.OrderBy(pair => pair.Key).ToArray();
            var missionCounters = test.Session.player.MissionProgress.ConditionCounters.OrderBy(pair => pair.Key).ToArray();
            void UnchangedProgress()
            {
                AssertEqual(true, taskProgress.SequenceEqual(test.State.TaskProgress.OrderBy(pair => pair.Key)), "Discarded starting Marg gives no Theatre spend-task credit");
                AssertEqual(true, missionCounters.SequenceEqual(test.Session.player.MissionProgress.ConditionCounters.OrderBy(pair => pair.Key)), "Discarded starting Marg gives no mission-counter credit");
            }
            byte[] initialInventory = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            test.Reject(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = int.MaxValue }, "Invalid start preserves prior Marg");
            AssertEqual((long)carry, test.Balance(96101), "Invalid start cannot discard Marg");
            AssertEqual(true, initialInventory.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Invalid start preserves durable Marg");
            UnchangedProgress();

            int difficulty = TheatreRows<TheatreDifficultyTable>().OrderBy(row => row.Id)
                .First(row => (bool)TheatreInvoke("IsConditionSatisfied", test, row.ConditionId ?? 0)!).Id;
            var request = new TheatreStartAdventureRequest { Difficulty = difficulty, Mode = 0 };
            if (preGrant) test.Inventories.ThrowOnReplaceOne = true;
            else test.Players.BeforeReplaceOne = player =>
            {
                if (player.Theatre.PendingMutation is null)
                    throw new MongoDB.Driver.MongoException("Injected Marg start final player-save failure.");
            };
            try { test.Call(nameof(TheatreStartAdventureRequest), request, success: false); }
            finally
            {
                test.Inventories.ThrowOnReplaceOne = false;
                test.Players.BeforeReplaceOne = null;
            }
            AssertEqual(false, test.Pushes.Any(push => push.Name != nameof(NotifyTheatreData)), "Failed start may restore Theatre snapshot but emits no additive rewards");
            int failedPacket = test.LastPacketId;
            byte[] failedPlayer = test.Players.LastSuccessfulReplacementBson!.ToArray();
            byte[] failedInventory = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            var pending = BsonSerializer.Deserialize<Player>(failedPlayer).Theatre.PendingMutation
                ?? throw new InvalidDataException("Failed Marg start must retain a durable pending intent.");
            AssertEqual(preGrant, initialInventory.SequenceEqual(failedInventory), "Failure boundary brackets durable inventory grant");
            AssertEqual(preGrant ? (long)carry : bonus, BsonSerializer.Deserialize<Inventory>(failedInventory).Items.Single(item => item.Id == 96101).Count,
                "Failed start retains old Marg before grant or only bonus after grant");
            UnchangedProgress();

            // Restore immutable durable documents without login auto-completing the pending start.
            test.Session.player = BsonSerializer.Deserialize<Player>(failedPlayer);
            test.Session.inventory = BsonSerializer.Deserialize<Inventory>(failedInventory);
            test.Session.character = BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!);
            test.Reject(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = int.MaxValue, Mode = 0 }, "Conflicting pending start is busy");
            AssertEqual(1, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "Pending start rejects changed semantic intent as busy");
            AssertEqual(true, failedPlayer.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Conflicting start cannot replace durable pending intent");
            AssertEqual(true, failedInventory.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Conflicting start cannot alter durable Marg");
            UnchangedProgress();

            test.Call(nameof(TheatreStartAdventureRequest), new TheatreStartAdventureRequest { Difficulty = difficulty, Mode = 0 });
            AssertEqual(true, failedPacket != test.LastPacketId, "Durable start recovery uses a new packet with identical semantic intent");
            AssertEqual(true, pending.Response!.SequenceEqual(test.LastResponseContent), "Start recovery returns frozen response");
            AssertEqual(true, test.Data.CurChapterDb is not null && test.State.PendingMutation is null, "Recovered start commits the adventure");
            AssertEqual((long)bonus, test.Balance(96101), "Accepted start retains only authored bonus, never carry plus bonus");
            UnchangedProgress();
            byte[] completedPlayer = test.Players.LastSuccessfulReplacementBson!.ToArray();
            byte[] completedInventory = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            AssertEqual((long)bonus, BsonSerializer.Deserialize<Inventory>(completedInventory).Items.Single(item => item.Id == 96101).Count, "Only initial bonus is durably stored");
            test.Replay("Marg start recovery");
            AssertEqual(true, completedPlayer.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Start replay does not rewrite durable player");
            AssertEqual(true, completedInventory.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Start replay does not rewrite durable inventory");
            UnchangedProgress();
            test.Relog($"marg-start-{carry}-recovered");
            AssertEqual((long)bonus, test.Balance(96101), "Relog preserves bonus-only Marg");
            UnchangedProgress();
            test.Reject(nameof(TheatreStartAdventureRequest), request, "New connection cannot restart an active adventure");
            AssertEqual((long)bonus, test.Balance(96101), "Start after relog cannot discard or award Marg again");
            UnchangedProgress();
            AssertEqual(true, BsonSerializer.Deserialize<Player>(failedPlayer).Theatre.PendingMutation is not null, "Captured failed BSON remains immutable after recovery");
        }
    }
}
