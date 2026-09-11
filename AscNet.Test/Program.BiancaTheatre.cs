using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.biancatheatre;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.client.biancatheatre;
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
    private static void ValidateBiancaTheatreCompatibility()
    {
        ValidateBiancaStartAndRecovery();
        ValidateBiancaRecruitment();
        ValidateBiancaEventBranches();
        ValidateBiancaShopAndChoices();
        ValidateBiancaMetaprogression();
        ValidateBiancaCharacterReceiptRecovery();
        ValidateBiancaFightLifecycle();
        ValidateBiancaApprovedCombatPolicy();
        foreach (int teamId in TableReaderV2.Parse<BiancaTheatreTeamTable>()
                     .Where(row => Convert.ToInt32(row.ConditionId) == 0).OrderBy(row => row.Id).Take(2).Select(row => row.Id))
        {
            using BiancaCase test = new($"complete-team-{teamId}");
            test.Start(teamId);
            int team = test.Data.CurTeamId;
            int oldExp = test.Data.TotalExp;
            DriveBiancaRun(test);
            BiancaTheatreSettleData result = test.State.LastSettleData
                ?? throw new InvalidDataException("Completed adventure must expose its durable settlement.");
            AssertEqual(team, result.TeamId, "Complete run settles selected archetype");
            AssertEqual(oldExp + result.TotalExp, test.Data.TotalExp, "Complete run credits settlement EXP once");
            AssertEqual(true, result.ChapterCount > 0 && result.FightNodeCount > 0, "Complete run traverses chapters and battles");
            AssertEqual(true, TableReaderV2.Parse<BiancaTheatreEndingTable>().Any(row => row.Id == result.EndId && row.PassType == 2),
                "Complete run resolves a table-defined successful ending");
            AssertEqual(result.NodeCountScore + result.FightNodeCountScore + result.TotalCharacterLevelScore +
                result.TotalItemCountScore + result.ChapterCountScore, result.TotalScore, "Settlement score components balance");
            test.Relog("completed adventure");
            AssertEqual(0, test.Data.CurChapterId, "Completed adventure cannot resume old chapter");
            test.Repeat("BiancaTheatreSettleAdventureRequest", null, "Repeated settlement cannot recredit rewards");
        }
        Console.WriteLine("BiancaTheatre compatibility: branching runs, local recruitment, events, shops, combat, durable claims and recovery passed.");
    }

    private sealed class BiancaCase : IDisposable
    {
        private static long nextPlayerId = 99_100;
        private int packetId;
        private readonly MongoCollectionOverride collections;
        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public BiancaTheatreState State => Session.player.BiancaTheatre;
        public NotifyBiancaTheatreActivityData Data => State.Data;
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public BiancaTheatreStep Step => Data.CurChapterDb?.Steps.LastOrDefault(step => step.Overdue == 0)
            ?? throw new InvalidDataException("Adventure has no actionable step.");
        public int LastPacketId => packetId;

        public BiancaCase(string name)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(out Players, out Characters, out Inventories, out Stages);
            long id = ++nextPlayerId;
            Harness = new(CreateDrawCompatibilityCharacter(id), CreateDrawCompatibilityPlayer(id),
                CreateDrawCompatibilityInventory(id, []), $"bianca-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            Login();
            SaveFixture();
        }

        public NotifyBiancaTheatreActivityData Login() => (NotifyBiancaTheatreActivityData)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BiancaTheatreModule"), "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session), typeof(DateTimeOffset)])
            .Invoke(null, [Session, DateTimeOffset.UtcNow])!;

        public void SaveFixture()
        {
            Session.player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }

        public void Start(int? teamId = null)
        {
            int difficulty = TableReaderV2.Parse<BiancaTheatreDifficultyTable>()
                .Where(row => Convert.ToInt32(row.ConditionId) == 0).OrderBy(row => row.Id).First().Id;
            JObject response = Call(nameof(BiancaTheatreSelectDifficultyRequest), new BiancaTheatreSelectDifficultyRequest { Difficulty = difficulty });
            AssertEqual(Data.CurChapterId, response.Value<int>("ChapterId"), "Difficulty response exposes resumable chapter");
            int team = teamId ?? TableReaderV2.Parse<BiancaTheatreTeamTable>()
                .Where(row => Convert.ToInt32(row.ConditionId) == 0).OrderBy(row => row.Id).First().Id;
            Call(nameof(BiancaTheatreSelectTeamRequest), new BiancaTheatreSelectTeamRequest { TeamId = team });
        }

        public JObject Call(string requestName, object? request = null, bool? success = true, int? reusePacketId = null)
        {
            Pushes.Clear();
            int id = reusePacketId ?? ++packetId;
            InvokeRegisteredRequestHandler(requestName, Session, id, request);
            for (int index = 0; index < 256; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name, $"{requestName} response name");
                LastResponseContent = response.Content;
                JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (success.HasValue) AssertEqual(success.Value, code == 0, $"{requestName} accepted={success}, actual code={code}");
                if (Harness.TryReadAvailablePacket($"{requestName} unexpected trailing packet", out Packet extra))
                    throw new InvalidDataException($"{requestName}: mutation push/duplicate response after response ({extra.Type}).");
                if (code == 0 && requestName.StartsWith("BiancaTheatre", StringComparison.Ordinal))
                {
                    Player saved = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException($"{requestName}: success without a durable player record."));
                    AssertEqual(true, State.ToBson().SequenceEqual(saved.BiancaTheatre.ToBson()), $"{requestName} persists before acknowledgement");
                }
                return body;
            }
            throw new InvalidDataException($"{requestName}: no response within 256 packets.");
        }

        public void Reject(string requestName, object? request, string name)
        {
            byte[] before = State.ToBson();
            byte[] inventory = Session.inventory.ToBson();
            byte[] character = Session.character.ToBson();
            Call(requestName, request, success: false);
            AssertEqual(false, Pushes.Any(push => requestName != nameof(FinishTaskRequest) || push.Name != nameof(NotifyTask)),
                $"{name}: rejection emits no success pushes (generic task status sync is permitted)");
            AssertEqual(true, before.SequenceEqual(State.ToBson()), $"{name}: rejection preserves adventure");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: rejection preserves inventory");
            AssertEqual(true, character.SequenceEqual(Session.character.ToBson()), $"{name}: rejection preserves roster");
        }

        public void Repeat(string requestName, object? request, string name)
        {
            byte[] state = Data.ToBson();
            int nextUid = State.NextUid;
            byte[] inventory = Session.inventory.ToBson();
            byte[] roster = Session.character.ToBson();
            Call(requestName, request, success: null);
            AssertEqual(true, state.SequenceEqual(Data.ToBson()), $"{name}: repeat cannot advance client state");
            AssertEqual(nextUid, State.NextUid, $"{name}: repeat cannot allocate identities");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: repeat cannot change balances");
            AssertEqual(true, roster.SequenceEqual(Session.character.ToBson()), $"{name}: repeat cannot grant an account character");
        }

        public void Relog(string name, bool pending = false)
        {
            byte[] chapter = MessagePackSerializer.Serialize(Data.CurChapterDb);
            byte[] localCharacters = MessagePackSerializer.Serialize(Data.Characters);
            byte[] items = MessagePackSerializer.Serialize(Data.Items);
            int nextUid = State.NextUid;
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson!);
            Character character = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            Stage stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(character, player, inventory, $"bianca-relog-{name}");
            Session.stage = stage;
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BiancaTheatreModule"), "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);
            NotifyBiancaTheatreActivityData login = Login();
            if (!pending)
            {
                AssertEqual(true, chapter.SequenceEqual(MessagePackSerializer.Serialize(login.CurChapterDb)), $"{name}: login preserves choices/node/reward UIDs");
                AssertEqual(true, localCharacters.SequenceEqual(MessagePackSerializer.Serialize(login.Characters)), $"{name}: login preserves recruits and upgrades");
                AssertEqual(true, items.SequenceEqual(MessagePackSerializer.Serialize(login.Items)), $"{name}: login preserves item occurrences");
                AssertEqual(nextUid, State.NextUid, $"{name}: login does not reroll or allocate identities");
            }
            AssertEqual(Data.CurChapterId > 0, login.CurChapterDb is not null, $"{name}: chapter gate matches resumable snapshot");
        }

        public long Balance(int itemId) => Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        public void Fund(int itemId, int count)
        {
            Item? item = Session.inventory.Items.SingleOrDefault(item => item.Id == itemId);
            if (item is null) Session.inventory.Items.Add(new Item { Id = itemId, Count = count });
            else item.Count = count;
        }
        public void Dispose() { Harness.Dispose(); collections.Dispose(); }
    }

    // EN XBiancaTheatreConfigs.lua ItemId enum, not captured balances or runtime payloads.
    private const int BiancaInnerCoin = 96119;
    private const int BiancaOuterCoin = 96118;

    private static void ValidateBiancaStartAndRecovery()
    {
        using BiancaCase test = new("start-and-recovery");
        int difficulty = TableReaderV2.Parse<BiancaTheatreDifficultyTable>()
            .Where(row => Convert.ToInt32(row.ConditionId) == 0).OrderBy(row => row.Id).First().Id;
        int team = TableReaderV2.Parse<BiancaTheatreTeamTable>()
            .Where(row => Convert.ToInt32(row.ConditionId) == 0).OrderBy(row => row.Id).First().Id;
        test.Reject(nameof(BiancaTheatreSelectTeamRequest), new BiancaTheatreSelectTeamRequest { TeamId = team }, "Team before difficulty");
        test.Reject(nameof(BiancaTheatreSelectDifficultyRequest), new BiancaTheatreSelectDifficultyRequest { Difficulty = int.MaxValue }, "Unknown difficulty");
        test.Reject(nameof(BiancaTheatreRecvFightRewardRequest), new BiancaTheatreRecvFightRewardRequest { Uid = int.MaxValue }, "Reward without run");
        test.Players.ThrowOnReplaceOne = true;
        try
        {
            test.Reject(nameof(BiancaTheatreSelectDifficultyRequest), new BiancaTheatreSelectDifficultyRequest { Difficulty = difficulty }, "Initial run save failure");
        }
        finally { test.Players.ThrowOnReplaceOne = false; }
        test.Call(nameof(BiancaTheatreSelectDifficultyRequest), new BiancaTheatreSelectDifficultyRequest { Difficulty = difficulty });
        test.Repeat(nameof(BiancaTheatreSelectDifficultyRequest), new BiancaTheatreSelectDifficultyRequest { Difficulty = difficulty }, "Active run cannot be reset by selecting difficulty");
        test.Inventories.ThrowOnReplaceOne = true;
        try
        {
            test.Call(nameof(BiancaTheatreSelectTeamRequest), new BiancaTheatreSelectTeamRequest { TeamId = team }, success: false);
            AssertEqual(0, test.Pushes.Count, "Initial currency save failure cannot expose a successful choice");
        }
        finally { test.Inventories.ThrowOnReplaceOne = false; }
        BiancaTheatrePendingMutation pending = test.State.PendingMutation
            ?? throw new InvalidDataException("Cross-document failure must retain its frozen adventure outcome.");
        byte[] frozenChoices = MessagePackSerializer.Serialize(pending.Outcome.Data.CurChapterDb);
        test.Relog("initial currency recovery", pending: true);
        AssertEqual(true, frozenChoices.SequenceEqual(MessagePackSerializer.Serialize(test.Data.CurChapterDb)), "Recovery does not reroll initial choices");
        AssertEqual(true, test.State.PendingMutation is null, "Login completes pending economy mutation");
        byte[] balances = test.Session.inventory.ToBson();
        test.Relog("repeat completed recovery");
        AssertEqual(true, balances.SequenceEqual(test.Session.inventory.ToBson()), "Repeated login cannot credit initial currency twice");
        test.Repeat(nameof(BiancaTheatreSelectTeamRequest), new BiancaTheatreSelectTeamRequest { TeamId = team }, "Active archetype cannot be selected twice");
        test.Reject(nameof(BiancaTheatreSelectItemRewardRequest), new BiancaTheatreSelectItemRewardRequest { InnerItemId = int.MaxValue }, "Unadvertised item choice");
    }

    private static BiancaTheatreStep BiancaFixtureStep(BiancaCase test, int stepType)
    {
        foreach (BiancaTheatreStep previous in test.Data.CurChapterDb!.Steps) previous.Overdue = 1;
        test.State.QueuedSteps.Clear();
        test.State.Fight = null;
        BiancaTheatreStep step = new() { Uid = ++test.State.NextUid, StepType = stepType };
        test.Data.CurChapterDb.Steps.Add(step);
        return step;
    }

    private static BiancaTheatreStep BiancaFixtureNode(BiancaCase test, params BiancaTheatreNodeSlot[] slots)
    {
        BiancaTheatreStep step = BiancaFixtureStep(test, 5);
        List<BiancaTheatreNodeTable> chapterNodes = TableReaderV2.Parse<BiancaTheatreNodeTable>()
            .Where(row => row.ChapterId == test.Data.CurChapterId).OrderBy(row => row.Id).ToList();
        int position = chapterNodes.FindIndex(row => slots.All(slot => slot.SlotType switch
        {
            1 => Convert.ToInt32(row.FightWeight) > 0,
            2 => Convert.ToInt32(row.EventWeight) > 0,
            3 => Convert.ToInt32(row.ShopWeight) > 0,
            _ => false
        }));
        if (position < 0) throw new InvalidDataException("No source node supports this fixture's slot types.");
        int nodeId = chapterNodes[position].Id;
        test.Data.CurChapterDb!.PassNodeCount = position;
        for (int index = 0; index < slots.Length; index++) slots[index].SlotId = index + 1;
        step.NodeData = new() { NodeId = nodeId, Slots = slots.ToList() };
        test.SaveFixture();
        return step;
    }

    private static void ValidateBiancaRecruitment()
    {
        using BiancaCase test = new("recruitment-boundaries");
        test.Start();
        while (test.Step.StepType != 4) AdvanceBianca(test);
        test.Reject(nameof(BiancaTheatreRecruitCharacterRequest), new BiancaTheatreRecruitCharacterRequest { CharacterId = int.MaxValue }, "Recruit must be offered");
        BiancaTheatreRecruitTicketTable ticket = TableReaderV2.Parse<BiancaTheatreRecruitTicketTable>()
            .Where(row => row.RefreshCount > 0 && row.RecruitCount > 0).OrderBy(row => row.Id).First();
        int candidate = TableReaderV2.Parse<BiancaTheatreRecruitCharacterGroupTable>()
            .Where(row => ticket.GroupId.Contains(row.GroupId) && row.Weight > 0).Select(row => row.CharacterId).First();
        int initialLevel = TableReaderV2.Parse<BiancaTheatreCharacterLevelTable>().Where(row => row.CharacterId == candidate)
            .Min(row => row.Level);
        BiancaTheatreStep recruit = BiancaFixtureStep(test, 4);
        recruit.TickId = ticket.Id;
        recruit.RefreshCharacterIds = [candidate];
        recruit.RefreshCount = ticket.RefreshCount;
        recruit.RecruitCount = ticket.RecruitCount;
        test.Data.Characters = [new() { CharacterId = candidate, Level = initialLevel }];
        test.SaveFixture();
        test.Relog("upgrade offer");
        test.Reject("BiancaTheatreEndRecruitRequest", null, "An eligible owned-character upgrade still enforces minimum recruitment");
        byte[] account = test.Session.character.ToBson();
        test.Call(nameof(BiancaTheatreRecruitCharacterRequest), new BiancaTheatreRecruitCharacterRequest { CharacterId = candidate });
        AssertEqual(initialLevel + 1, test.Data.Characters.Single().Level, "Duplicate recruit upgrades rather than duplicates");
        AssertEqual(true, account.SequenceEqual(test.Session.character.ToBson()), "Upgrade remains mode-local");
        test.Repeat(nameof(BiancaTheatreRecruitCharacterRequest), new BiancaTheatreRecruitCharacterRequest { CharacterId = candidate }, "Repeated recruit cannot reuse an offer");
        test.Relog("recruited upgrade");

        recruit = test.Step;
        int refreshBefore = recruit.CurRefreshCount;
        test.Call("BiancaTheatreRecruitRefreshRequest");
        AssertEqual(refreshBefore + 1, test.Step.CurRefreshCount, "Refresh consumes exactly one allowance");
        AssertEqual(0, test.Step.RecruitCharacterIds.Count, "New refresh releases the prior refresh's recruitment exclusions");
        byte[] refreshed = MessagePackSerializer.Serialize(test.Step);
        int refreshPacket = test.LastPacketId;
        test.Call("BiancaTheatreRecruitRefreshRequest", reusePacketId: refreshPacket);
        AssertEqual(true, refreshed.SequenceEqual(MessagePackSerializer.Serialize(test.Step)), "Exact refresh packet replay cannot consume another attempt or reroll candidates");
        test.Relog("refreshed candidates");
        test.Reject("BiancaTheatreRecruitRefreshRequest", null, "Exhausted refresh cannot reroll");

        recruit = BiancaFixtureStep(test, 4);
        recruit.TickId = ticket.Id;
        recruit.RefreshCount = ticket.RefreshCount;
        recruit.RecruitCount = ticket.RecruitCount;
        int maximum = Convert.ToInt32(TableReaderV2.Parse<BiancaTheatreConfigTable>().Single(row => row.Key == "MaxCharacterLevel").Value);
        test.Data.Characters = TableReaderV2.Parse<BiancaTheatreBaseCharacterTable>()
            .Select(row => new BiancaTheatreCharacter { CharacterId = row.CharacterId, Level = maximum }).ToList();
        test.SaveFixture();
        JObject exhausted = test.Call("BiancaTheatreRecruitRefreshRequest", success: false);
        AssertEqual(20176010, exhausted.Value<int>("Code"), "No-candidate refresh reports the client-special outcome");
        AssertEqual(1, test.Step.CurRefreshCount, "No-candidate refresh still consumes its allowance");
        test.Relog("no-candidate refresh");
        test.Call("BiancaTheatreEndRecruitRequest");
        AssertEqual(5, test.Step.StepType, "Exhausted recruitment can continue instead of trapping the run");
    }

    private static void ValidateBiancaEventBranches()
    {
        List<BiancaTheatreEventTable> events = TableReaderV2.Parse<BiancaTheatreEventTable>();
        List<BiancaTheatreEventStepGroupTable> groups = TableReaderV2.Parse<BiancaTheatreEventStepGroupTable>();
        BiancaTheatreEventTable branch = events.Where(row => row.Type == 2 && row.OptionType.Count >= 2 &&
                row.OptionType.Take(2).All(value => value == 3) && row.OptionCondition.All(value => value == 0) &&
                row.OptionNextStepGroupId.Take(2).Distinct().Count() == 2)
            .OrderBy(row => row.Id).First(row => row.OptionNextStepGroupId.Take(2).All(group =>
                groups.Count(value => value.GroupId == group && value.Weight > 0) == 1));
        List<int> selectedNextSteps = [];
        BiancaTheatreEventTable sibling = events.First(row => row.Type == 2 && row.EventId != branch.EventId);
        for (int option = 1; option <= 2; option++)
        {
            using BiancaCase test = new($"event-branch-{option}");
            test.Start();
            BiancaTheatreStep node = BiancaFixtureNode(test,
                new() { SlotType = 2, EventId = branch.EventId, CurStepId = branch.StepId },
                new() { SlotType = 2, EventId = sibling.EventId, CurStepId = sibling.StepId });
            test.Reject(nameof(BiancaTheatreSelectNodeRequest), new BiancaTheatreSelectNodeRequest { NodeId = node.NodeData!.NodeId, SlotId = int.MaxValue }, "Unknown node slot");
            test.Call(nameof(BiancaTheatreSelectNodeRequest), new BiancaTheatreSelectNodeRequest { NodeId = node.NodeData!.NodeId, SlotId = 1 });
            test.Reject(nameof(BiancaTheatreSelectNodeRequest), new BiancaTheatreSelectNodeRequest { NodeId = node.NodeData.NodeId, SlotId = 2 }, "Selected branch excludes its sibling");
            test.Relog("event branch");
            test.Reject(nameof(BiancaTheatreEventNodeNextStepRequest), new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = int.MaxValue, OptionId = option }, "Stale event step");
            test.Reject(nameof(BiancaTheatreEventNodeNextStepRequest), new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = branch.StepId, OptionId = 0 }, "Option indices are one-based");
            JObject response = test.Call(nameof(BiancaTheatreEventNodeNextStepRequest),
                new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = branch.StepId, OptionId = option });
            int expected = groups.Single(row => row.GroupId == branch.OptionNextStepGroupId[option - 1] && row.Weight > 0).StepId;
            AssertEqual(expected, response.Value<int>("NextEventStepId"), "Event option follows its own configured edge");
            selectedNextSteps.Add(expected);
            test.Relog("event next step");
            AssertEqual(expected, test.Step.NodeData!.Slots.Single(slot => slot.Selected != 0).CurStepId, "Selected event continuation survives login");
            test.Reject(nameof(BiancaTheatreEventNodeNextStepRequest), new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = branch.StepId, OptionId = option }, "Consumed event option cannot replay");
            AdvanceBiancaEvent(test, test.Step.NodeData.Slots.Single(slot => slot.Selected != 0), 0);
            AssertEqual(true, test.Data.PassedEventRecord[branch.EventId].Contains(expected), "Terminal event records reward-bearing source step");
        }
        AssertEqual(2, selectedNextSteps.Distinct().Count(), "Different choices reach independently derived event outcomes");
        using (BiancaCase sparse = new("sparse-event-condition"))
        {
            sparse.Start();
            BiancaTheatreEventTable row = events.First(value => value.Type == 2 && value.OptionCondition.Count > 1 &&
                value.OptionCondition[0] == 0 && value.OptionCondition.Skip(1).Any(condition => condition > 0) && value.OptionType[0] == 3);
            int gated = row.OptionCondition.FindIndex(condition => condition > 0);
            BiancaFixtureNode(sparse, new BiancaTheatreNodeSlot { SlotType = 2, Selected = 1, EventId = row.EventId, CurStepId = row.StepId });
            sparse.Reject(nameof(BiancaTheatreEventNodeNextStepRequest),
                new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = row.StepId, OptionId = gated + 1 }, "Late option retains its own vision condition");
            sparse.Call(nameof(BiancaTheatreEventNodeNextStepRequest),
                new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = row.StepId, OptionId = 1 });
            AssertEqual(true, sparse.Data.PassedEventRecord[row.EventId].Contains(row.StepId), "Leading blank option conditions do not shift onto the first choice");
        }
        using (BiancaCase itemCosts = new("event-driver-missing-inner-item"))
        {
            itemCosts.Start();
            BiancaTheatreEventTable row = events.First(value => value.Type == 2 && value.OptionType.Count == 2 &&
                value.OptionType[0] is 1 or 2 && value.OptionItemType[0] == 2 && value.OptionItemCount[0] > 0 &&
                value.OptionType[1] == 3 && value.OptionCondition.All(condition => condition == 0));
            itemCosts.Data.Items.RemoveAll(item => item.ItemId == row.OptionItemId[0]);
            BiancaTheatreNodeSlot slot = new() { SlotType = 2, Selected = 1, EventId = row.EventId, CurStepId = row.StepId };
            BiancaFixtureNode(itemCosts, slot);
            AdvanceBiancaEvent(itemCosts, slot, 0);
        }

        using BiancaCase costs = new("event-costs");
        costs.Start();
        var costOption = events.Where(row => row.Type == 2).SelectMany(row => row.OptionType.Select((type, index) => (row, type, index)))
            .First(value => value.type == 1 && Convert.ToInt32(value.row.StepRewardItemType) == 0 &&
                value.index < value.row.OptionItemType.Count && value.row.OptionItemType[value.index] == 1 &&
                value.row.OptionItemCount[value.index] > 0 && (value.index >= value.row.OptionCondition.Count || value.row.OptionCondition[value.index] == 0));
        BiancaTheatreEventTable costRow = costOption.row;
        int costItem = costRow.OptionItemId[costOption.index];
        int cost = costRow.OptionItemCount[costOption.index];
        BiancaFixtureNode(costs, new BiancaTheatreNodeSlot { SlotType = 2, Selected = 1, EventId = costRow.EventId, CurStepId = costRow.StepId });
        costs.Fund(costItem, cost - 1);
        costs.SaveFixture();
        BiancaTheatreEventNodeNextStepRequest choice = new() { CurEventStepId = costRow.StepId, OptionId = costOption.index + 1 };
        costs.Reject(nameof(BiancaTheatreEventNodeNextStepRequest), choice, "Event cost cannot overdraft");
        costs.Fund(costItem, cost);
        costs.SaveFixture();
        costs.Call(nameof(BiancaTheatreEventNodeNextStepRequest), choice);
        AssertEqual(0L, costs.Balance(costItem), "Event consumes its configured exact cost");
    }

    private static void ValidateBiancaShopAndChoices()
    {
        using (BiancaCase test = new("shop-recovery"))
        {
            test.Start();
            List<BiancaTheatreNodeShopItemTable> offers = TableReaderV2.Parse<BiancaTheatreNodeShopItemTable>();
            var fixture = (from candidate in offers
                           from catalog in TableReaderV2.Parse<BiancaTheatreNodeShopTable>()
                           where candidate.ItemType == 1 && candidate.Price > 0 && catalog.GroupId.Contains(candidate.GroupId) &&
                               offers.Any(other => other.ItemType == 2 && catalog.GroupId.Contains(other.GroupId)) &&
                               TableReaderV2.Parse<BiancaTheatreItemTable>().Any(item => item.Id == candidate.ItemId && Convert.ToInt32(item.UnlockConditionId) == 0)
                           select (Offer: candidate, Shop: catalog)).First();
            BiancaTheatreNodeShopItemTable offer = fixture.Offer;
            BiancaTheatreNodeShopTable shop = fixture.Shop;
            // Approved private-server rule: source percentages are rounded upward, including fractional prices.
            int price = checked((int)Math.Ceiling(Math.Max(0, offer.Price * shop.DiscountRate / 100d)));
            int uid = ++test.State.NextUid;
            BiancaTheatreShopItem item = new()
            {
                Uid = uid, ItemType = offer.ItemType, ItemId = Convert.ToInt32(offer.ItemId),
                Price = offer.Price, DiscountPrice = price
            };
            BiancaTheatreStep node = BiancaFixtureNode(test, new BiancaTheatreNodeSlot { SlotType = 3, ShopId = shop.Id, ShopItems = [item] });
            BiancaTheatreNodeShopBuyItemRequest buy = new() { ShopItemUid = uid };
            test.Reject(nameof(BiancaTheatreNodeShopBuyItemRequest), buy, "Unselected shop cannot sell");
            test.Call(nameof(BiancaTheatreSelectNodeRequest), new BiancaTheatreSelectNodeRequest { NodeId = node.NodeData!.NodeId, SlotId = 1 });
            test.Reject(nameof(BiancaTheatreNodeShopBuyItemRequest), new BiancaTheatreNodeShopBuyItemRequest { ShopItemUid = int.MaxValue }, "Foreign shop UID");
            test.Fund(BiancaInnerCoin, price - 1);
            test.SaveFixture();
            test.Reject(nameof(BiancaTheatreNodeShopBuyItemRequest), buy, "Discount price cannot overdraft");
            test.Fund(BiancaInnerCoin, price);
            test.Step.NodeData!.Slots.Single().ShopItems.Single().IsLock = 1;
            test.SaveFixture();
            test.Reject(nameof(BiancaTheatreNodeShopBuyItemRequest), buy, "Locked shop slot cannot sell");
            test.Step.NodeData.Slots.Single().ShopItems.Single().IsLock = 0;
            test.SaveFixture();
            int beforeItems = test.Data.Items.Count(value => value.ItemId == offer.ItemId);
            test.Inventories.ThrowOnReplaceOne = true;
            try
            {
                test.Call(nameof(BiancaTheatreNodeShopBuyItemRequest), buy, success: false);
                AssertEqual(0, test.Pushes.Count, "Failed shop debit emits no item grant");
                AssertEqual(beforeItems, test.Data.Items.Count(value => value.ItemId == offer.ItemId), "Failed shop debit leaves visible item inventory unchanged");
            }
            finally { test.Inventories.ThrowOnReplaceOne = false; }
            test.Call(nameof(BiancaTheatreNodeShopBuyItemRequest), buy);
            AssertEqual(0L, test.Balance(BiancaInnerCoin), "Shop charges persisted discount rather than full price");
            AssertEqual(beforeItems + 1, test.Data.Items.Count(value => value.ItemId == offer.ItemId), "Shop retry grants exactly one local item");
            test.Relog("purchased shop slot");
            AssertEqual(1, test.Step.NodeData!.Slots.Single().ShopItems.Single().IsBuy, "Purchased shop receipt survives login");
            test.Reject(nameof(BiancaTheatreNodeShopBuyItemRequest), buy, "Purchased shop slot cannot be bought twice");

            BiancaTheatreNodeShopItemTable invitation = offers.First(row => row.ItemType == 2 && row.Price > 0 && shop.GroupId.Contains(row.GroupId));
            int invitationUid = ++test.State.NextUid;
            test.Step.NodeData.Slots.Single().ShopItems.Add(new()
            {
                Uid = invitationUid, ItemType = invitation.ItemType, TicketId = Convert.ToInt32(invitation.TicketId),
                Price = invitation.Price, DiscountPrice = invitation.Price
            });
            test.Fund(BiancaInnerCoin, invitation.Price);
            test.SaveFixture();
            int rootUid = test.Step.Uid;
            test.Call(nameof(BiancaTheatreNodeShopBuyItemRequest), new BiancaTheatreNodeShopBuyItemRequest { ShopItemUid = invitationUid });
            AssertEqual(rootUid, test.Step.RootUid, "Shop invitation preserves return-to-shop parent");
            test.Relog("shop recruitment");
            while (test.Step.StepType is 4 or 7) AdvanceBianca(test);
            AssertEqual(rootUid, test.Step.Uid, "Recruitment resumes the purchased shop instead of generating a new node");
            AssertEqual(2, test.Step.NodeData!.Slots.Single().ShopItems.Count(value => value.IsBuy == 1), "Both shop receipts remain after nested recruitment");
            int nodeCount = test.State.RunNodeCount;
            test.Call("BiancaTheatreEndNodeRequest");
            AssertEqual(nodeCount + 1, test.State.RunNodeCount, "Leaving the shop completes exactly one node");
        }

        int[] choices = TableReaderV2.Parse<BiancaTheatreItemTable>().Where(row => Convert.ToInt32(row.UnlockConditionId) == 0)
            .OrderBy(row => row.Id).Take(2).Select(row => row.Id).ToArray();
        foreach (int selected in choices)
        {
            using BiancaCase test = new($"item-choice-{selected}");
            test.Start();
            BiancaTheatreStep step = BiancaFixtureStep(test, 2);
            step.ItemIds = choices.ToList();
            test.SaveFixture();
            test.Relog("unresolved item box");
            BiancaTheatreSelectItemRewardRequest request = new() { InnerItemId = selected };
            test.Players.ThrowOnReplaceOne = true;
            try { test.Reject(nameof(BiancaTheatreSelectItemRewardRequest), request, "Item choice save rollback"); }
            finally { test.Players.ThrowOnReplaceOne = false; }
            test.Call(nameof(BiancaTheatreSelectItemRewardRequest), request);
            AssertEqual(selected, test.Data.Items.Single().ItemId, "Each item input grants its own table-defined effect item");
            AssertEqual(false, test.Data.Items.Any(item => item.ItemId == choices.Single(id => id != selected)), "Unchosen branch grants nothing");
            test.Relog("resolved item box");
            test.Reject(nameof(BiancaTheatreSelectItemRewardRequest), request, "Resolved box cannot grant twice");
        }
    }

    private static void ValidateBiancaMetaprogression()
    {
        using BiancaCase test = new("meta-boundaries");
        BiancaTheatreStrengthenTable strengthen = TableReaderV2.Parse<BiancaTheatreStrengthenTable>()
            .Where(row => row.PreStrengthenIds.All(id => id == 0)).OrderBy(row => row.Id).First();
        test.Fund(BiancaOuterCoin, strengthen.UnlockPrice);
        test.SaveFixture();
        test.Reject(nameof(BiancaTheatreStrengthenRequest), new BiancaTheatreStrengthenRequest { Id = strengthen.Id }, "Strengthen feature requires chapter progress");
        // Either chapter-group's first cleared chapter satisfies the shared strengthen condition.
        int firstChapter = TableReaderV2.Parse<BiancaTheatreChapterGroupTable>().OrderBy(row => row.Id).First().ChapterStartId;
        test.Data.PassChapterIds.Add(firstChapter);
        test.Fund(BiancaOuterCoin, strengthen.UnlockPrice - 1);
        test.SaveFixture();
        test.Reject(nameof(BiancaTheatreStrengthenRequest), new BiancaTheatreStrengthenRequest { Id = strengthen.Id }, "Strengthen cannot overdraft");
        AssertEqual(20176035, MessagePackSerializer.Deserialize<BiancaTheatreStrengthenResponse>(test.LastResponseContent).Code,
            "Strengthen shortage reports insufficient Curse-Dispelling Chapters");
        AssertEqual((long)strengthen.UnlockPrice - 1, test.Balance(BiancaOuterCoin), "Rejected strengthen preserves funds");
        AssertEqual(false, test.Data.StrengthenDbs.Contains(strengthen.Id), "Rejected strengthen remains locked");
        BiancaTheatreStrengthenTable dependent = TableReaderV2.Parse<BiancaTheatreStrengthenTable>()
            .First(row => row.PreStrengthenIds.Any(id => id > 0));
        test.Fund(BiancaOuterCoin, Math.Max(dependent.UnlockPrice, strengthen.UnlockPrice));
        test.SaveFixture();
        test.Reject(nameof(BiancaTheatreStrengthenRequest), new BiancaTheatreStrengthenRequest { Id = dependent.Id }, "Strengthen requires its prerequisite purchase");
        long coin = test.Balance(BiancaOuterCoin);
        test.Call(nameof(BiancaTheatreStrengthenRequest), new BiancaTheatreStrengthenRequest { Id = strengthen.Id });
        AssertEqual(coin - strengthen.UnlockPrice, test.Balance(BiancaOuterCoin), "Strengthen charges authoritative price");
        AssertEqual(true, test.Data.StrengthenDbs.Contains(strengthen.Id), "Funded strengthen unlocks the purchased node");
        test.Relog("strengthen purchase");
        test.Repeat(nameof(BiancaTheatreStrengthenRequest), new BiancaTheatreStrengthenRequest { Id = strengthen.Id }, "Strengthen duplicate is not charged twice");

        List<BiancaTheatreLevelRewardTable> levels = TableReaderV2.Parse<BiancaTheatreLevelRewardTable>().OrderBy(row => row.Id).Take(3).ToList();
        int threshold = levels[0].UnlockScore;
        test.Data.TotalExp = threshold - 1;
        test.SaveFixture();
        test.Reject(nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = levels[0].Id }, "Reward threshold is inclusive, not rounded upward");
        test.Data.TotalExp = threshold;
        test.SaveFixture();
        AssertBiancaRewardGrant(test, nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = levels[0].Id }, [levels[0].RewardId]);
        test.Relog("level reward receipt");
        test.Repeat(nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = levels[0].Id }, "Claimed level cannot reward again");
        test.Reject(nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = levels[1].Id }, "UnlockScore is cumulative across levels");

        test.Data.TotalExp = levels.Sum(row => row.UnlockScore);
        test.SaveFixture();
        Dictionary<int, long> beforeFailure = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        test.Players.BeforeReplaceOne = player =>
        {
            if (player.BiancaTheatre.PendingMutation is null) throw new MongoDB.Driver.MongoException("Injected Bianca final player acknowledgement failure.");
        };
        try
        {
            test.Call("BiancaTheatreGetAllRewardRequest", success: false);
            AssertEqual(0, test.Pushes.Count, "Unacknowledged claim cannot send reward or claim-set success pushes");
        }
        finally { test.Players.BeforeReplaceOne = null; }
        AssertEqual(true, test.State.PendingMutation is not null, "Final acknowledgement failure retains receipt recovery state");
        test.Relog("partial meta reward commit", pending: true);
        AssertBiancaInventoryRewards(test, beforeFailure, levels.Skip(1).Select(row => row.RewardId));
        AssertIntegerList(levels.Select(row => (long)row.Id).ToArray(), test.Data.GetRewardIds.Order().Select(id => (long)id).ToArray(),
            "Recovery commits every eligible claim and preserves previously claimed levels");
        test.Repeat("BiancaTheatreGetAllRewardRequest", null, "Claim-all retry cannot duplicate recovered goods");

        BiancaTheatreActivityTable activity = TableReaderV2.Parse<BiancaTheatreActivityTable>().Single(row => row.Id == test.Data.CurActivityId);
        test.Data.AchievementCondition = 1;
        test.SaveFixture();
        test.Reject(nameof(BiancaTheatreGetAchievementRewardRequest), new BiancaTheatreGetAchievementRewardRequest { NeedCountId = 1 }, "Achievement milestone requires finished tasks");
        int[] taskIds = TableReaderV2.Parse<BiancaTheatreAchievementTable>().SelectMany(row => row.TaskIds).Distinct().Take(activity.NeedCounts[0]).ToArray();
        test.Session.player.MissionProgress.ClaimedTaskIds.AddRange(taskIds);
        test.SaveFixture();
        AssertBiancaRewardGrant(test, nameof(BiancaTheatreGetAchievementRewardRequest),
            new BiancaTheatreGetAchievementRewardRequest { NeedCountId = 1 }, [activity.RewardIds[0]]);
        test.Relog("achievement milestone");
        test.Repeat(nameof(BiancaTheatreGetAchievementRewardRequest), new BiancaTheatreGetAchievementRewardRequest { NeedCountId = 1 }, "Achievement milestone cannot be claimed twice");
        test.Data.NewStage = 1;
        test.SaveFixture();
        byte[] durableClaims = MessagePackSerializer.Serialize(test.Data.GetRewardIds);
        test.Call("BiancaTheatreNewStageRequest");
        test.Relog("NewStage acknowledgement");
        AssertEqual(0, test.Data.NewStage, "NewStage acknowledgement survives login");
        AssertEqual(true, durableClaims.SequenceEqual(MessagePackSerializer.Serialize(test.Data.GetRewardIds)), "NewStage does not erase metaprogression");
        ValidateBiancaTaskTransition(test);
    }

    private static void AssertBiancaRewardGrant(BiancaCase test, string requestName, object? request, IReadOnlyList<int> rewardIds)
    {
        Dictionary<int, long> before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        Dictionary<uint, int> titleScoresBefore = test.Session.character.ScoreTitles.ToDictionary(title => title.Id, title => title.Score);
        JObject response = test.Call(requestName, request);
        List<RewardGoodsTable> expected = rewardIds.SelectMany(id => ResolveRewardGoods(id, TableReaderV2.Parse<RewardGoodsTable>(), requestName)).ToList();
        JArray actual = RequiredValue<JArray>(response, "RewardGoodsList", JTokenType.Array, requestName);
        foreach (IGrouping<int, RewardGoodsTable> group in expected.GroupBy(goods => goods.TemplateId))
            AssertEqual(group.Sum(goods => goods.Count), actual.OfType<JObject>().Where(goods => goods.Value<int>("TemplateId") == group.Key)
                .Sum(goods => goods.Value<int>("Count")), $"{requestName} reports the granted table reward");
        AssertBiancaInventoryRewards(test, before, rewardIds);
        HashSet<int> scoreTitleIds = TableReaderV2.Parse<AscNet.Table.V2.share.scoretitle.ScoreTitleTable>().Select(title => title.Id).ToHashSet();
        var titleAwards = expected.Where(goods => scoreTitleIds.Contains(goods.TemplateId)).GroupBy(goods => goods.TemplateId)
            .Select(group => (Id: checked((uint)group.Key), Score: checked(titleScoresBefore.GetValueOrDefault((uint)group.Key) + group.Sum(goods => goods.Count))))
            .ToArray();
        if (titleAwards.Length > 0)
        {
            Character persisted = BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Score-title reward was acknowledged without a persisted character entitlement."));
            foreach (var title in titleAwards)
            {
                AssertEqual(title.Score, test.Session.character.ScoreTitles.Single(owned => owned.Id == title.Id).Score,
                    "Bianca score-title reward grants the authored cumulative entitlement score");
                AssertEqual(title.Score, persisted.ScoreTitles.Single(owned => owned.Id == title.Id).Score,
                    "Bianca score-title ownership and score persist before reward acknowledgement");
            }
            test.Relog("score-title entitlement");
            foreach (var title in titleAwards)
                AssertEqual(title.Score, test.Session.character.ScoreTitles.Single(owned => owned.Id == title.Id).Score,
                    "Bianca score-title entitlement remains owned after relog");
        }
    }

    private static void AssertBiancaInventoryRewards(BiancaCase test, IReadOnlyDictionary<int, long> before, IEnumerable<int> rewardIds)
    {
        HashSet<int> itemIds = TableReaderV2.Parse<ItemTable>().Select(row => row.Id).ToHashSet();
        foreach (IGrouping<int, RewardGoodsTable> group in rewardIds.SelectMany(id =>
                     ResolveRewardGoods(id, TableReaderV2.Parse<RewardGoodsTable>(), "Bianca rewards"))
                 .Where(row => itemIds.Contains(row.TemplateId)).GroupBy(row => row.TemplateId))
            AssertEqual(before.GetValueOrDefault(group.Key) + group.Sum(row => (long)row.Count), test.Balance(group.Key), "Bianca reward is spendable exactly once");
    }

    private static void ValidateBiancaTaskTransition(BiancaCase test)
    {
        HashSet<int> ids = TableReaderV2.Parse<BiancaTheatreTaskTable>().SelectMany(row => row.TaskId).ToHashSet();
        var task = (from row in TableReaderV2.Parse<TaskTable>()
                    join condition in TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>() on row.Condition equals condition.Id
                    where ids.Contains(row.Id) && condition.Type == 91003 && (row.Result ?? 1) > 0
                    orderby row.Id select row).First();
        test.Start();
        while (test.Step.StepType != 4) AdvanceBianca(test);
        test.State.TotalRecruitCount = (task.Result ?? 1) - 1;
        test.State.TaskProgress[task.Condition] = (task.Result ?? 1) - 1;
        test.SaveFixture();
        test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, "Bianca generic task cannot finish before objective");
        AdvanceBianca(test);
        test.Relog("task objective crossed by recruitment");
        FinishTaskRequest claim = new() { TaskId = task.Id };
        int rewardId = task.RewardId ?? throw new InvalidDataException("Recruitment task fixture requires an authored reward.");
        Dictionary<int, long> beforeClaim = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        test.Players.BeforeReplaceOne = player =>
        {
            if (player.BiancaTheatre.PendingMutation is null) throw new MongoDB.Driver.MongoException("Injected generic Bianca task final-save failure.");
        };
        try
        {
            test.Call(nameof(FinishTaskRequest), claim, success: false);
            AssertEqual(false, test.Pushes.Any(push => push.Name != nameof(NotifyTask)), "Failed generic task finalization cannot announce granted rewards");
        }
        finally { test.Players.BeforeReplaceOne = null; }
        AssertEqual(true, test.State.PendingMutation is not null, "Generic task claim retains its frozen successful response after final-save failure");
        int differentTask = ids.Order().First(id => id != task.Id && !test.Session.player.MissionProgress.ClaimedTaskIds.Contains(id));
        test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = differentTask }, "Different task intent cannot silently complete the pending claim");
        JObject retry = test.Call(nameof(FinishTaskRequest), claim);
        JArray rewards = RequiredValue<JArray>(retry, "RewardGoodsList", JTokenType.Array, "Generic Bianca task recovery");
        foreach (IGrouping<int, RewardGoodsTable> group in ResolveRewardGoods(rewardId, TableReaderV2.Parse<RewardGoodsTable>(), "Recruitment task reward").GroupBy(row => row.TemplateId))
            AssertEqual(group.Sum(row => row.Count), rewards.OfType<JObject>().Where(row => row.Value<int>("TemplateId") == group.Key).Sum(row => row.Value<int>("Count")),
                "Generic task retry returns the originally successful reward response, not already-claimed failure");
        AssertBiancaInventoryRewards(test, beforeClaim, [rewardId]);
        JObject exactReplay = test.Call(nameof(FinishTaskRequest), claim, reusePacketId: test.LastPacketId);
        AssertEqual(true, JToken.DeepEquals(retry, exactReplay), "Exact generic task packet replay returns the identical successful body");
        AssertBiancaInventoryRewards(test, beforeClaim, [rewardId]);
        AssertEqual(true, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id), "Generic task claim records real Bianca progression");
        test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, "Generic task retry cannot reward twice");
    }

    private static void ValidateBiancaFightLifecycle()
    {
        foreach ((bool win, bool forceExit) in new[] { (true, false), (false, true), (false, false) })
        {
            using BiancaCase test = new($"fight-win-{win}-force-{forceExit}");
            test.Start();
            while (test.State.Fight is null) AdvanceBianca(test);
            int nodesBefore = test.State.RunNodeCount;
            int fightsBefore = test.State.RunFightNodeCount;
            uint stageId = test.State.Fight.StageId;
            uint otherStage = checked((uint)TableReaderV2.Parse<BiancaTheatreFightStageTemplateTable>().First(row => row.StageId != stageId).StageId);
            test.Reject(nameof(PreFightRequest), new PreFightRequest
            {
                PreFightData = new() { StageId = otherStage, ChallengeCount = 1, CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [0, 0, 0] }
            }, "Another Bianca stage is not authorized by the selected node");
            test.Reject(nameof(BiancaTheatreSetSingleTeamRequest), new BiancaTheatreSetSingleTeamRequest
            {
                TeamData = new() { CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [0, 0, 0] }
            }, "Captain cannot occupy an empty deployment slot");
            PreFightResponse entered = FightBianca(test, win, forceExit, duringFight: fight =>
            {
                long fightId = fight.FightData!.FightId;
                FightSettleRequest foreign = CreateMissingStageSettleRequest(stageId, unchecked((uint)(fightId + 1)), test.Session.player.PlayerData.Id);
                test.Reject(nameof(FightSettleRequest), foreign, "Foreign fight cannot settle selected node");
                test.Reject(nameof(FightRestartRequest), new FightRestartRequest { FightId = unchecked((int)(fightId + 1)) }, "Foreign fight cannot restart");
                test.Relog("in-progress fight");
                AssertEqual(0L, test.State.Fight!.FightId, "Approved recovery policy revokes interrupted fight authorization");
                test.Reject(nameof(FightSettleRequest), CreateMissingStageSettleRequest(stageId, fightId, test.Session.player.PlayerData.Id),
                    "Interrupted fight cannot settle after actual login preparation");
                BiancaTheatreTeamData team = test.Data.SingleTeamData
                    ?? throw new InvalidDataException("Interrupted fight lost its persisted deployment.");
                PreFightRequest resumeRequest = new()
                {
                    PreFightData = new()
                    {
                        StageId = stageId, ChallengeCount = 1, CaptainPos = team.CaptainPos, FirstFightPos = team.FirstFightPos,
                        CardIds = team.CardIds.Select(id => (uint)id).ToList(), RobotIds = team.RobotIds.ToList()
                    }
                };
                test.Call(nameof(PreFightRequest), resumeRequest);
                fight.FightData = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent).FightData;
                fightId = fight.FightData.FightId;
                AssertEqual(fightId, test.State.Fight!.FightId, "Re-entry authorizes the pending node's new attempt");
                FightSettleRequest unpaidRevive = CreateMissingStageSettleRequest(stageId, fightId, test.Session.player.PlayerData.Id);
                unpaidRevive.Result.IsWin = win;
                unpaidRevive.Result.IsForceExit = forceExit;
                unpaidRevive.Result.RebootCount = test.State.Fight.RebootCount + 1;
                test.Reject(nameof(FightSettleRequest), unpaidRevive, "Settlement cannot claim an unauthorized resurrection");
                if (!win) return;
                test.State.Fight!.Seed = uint.MaxValue;
                test.SaveFixture();
                byte[] seedBoundary = test.Players.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Unsigned fight seed was not persisted.");
                test.Session.player = BsonSerializer.Deserialize<Player>(seedBoundary);
                AssertEqual(uint.MaxValue, test.State.Fight!.Seed, "Full unsigned seed survives active-fight Player BSON");
                Player pendingSeed = BsonSerializer.Deserialize<Player>(seedBoundary);
                pendingSeed.BiancaTheatre.PendingMutation = new()
                {
                    Outcome = BsonSerializer.Deserialize<BiancaTheatreState>(pendingSeed.BiancaTheatre.ToBson())
                };
                Player pendingSeedReload = BsonSerializer.Deserialize<Player>(pendingSeed.ToBson());
                AssertEqual(uint.MaxValue, pendingSeedReload.BiancaTheatre.PendingMutation!.Outcome.Fight!.Seed,
                    "Full unsigned seed survives nested pending-outcome Player BSON");
                JObject seedWire = test.Call(nameof(PreFightRequest), resumeRequest);
                AssertEqual(uint.MaxValue, seedWire["FightData"]!.Value<uint>("Seed"), "Reloaded unsigned seed remains intact in the real pre-fight wire payload");
                int closeCost = TableReaderV2.Parse<BiancaTheatreConfigTable>().Single(row => row.Key == "CloseCheckCost").Value;
                int tolerance = TableReaderV2.Parse<BiancaTheatreConfigTable>().Single(row => row.Key == "CloseCheckCount").Value;
                test.State.AbnormalExitCount = tolerance;
                const int actionPoint = 96120; // EN mode action-point currency.
                test.Fund(actionPoint, closeCost);
                test.SaveFixture();
                test.Call(nameof(LeaveFightRequest), new LeaveFightRequest());
                AssertEqual(tolerance + 1, test.State.AbnormalExitCount, "LeaveFight consumes an abnormal-exit occurrence");
                AssertEqual(0L, test.Balance(actionPoint), "LeaveFight cannot bypass the configured post-tolerance cost");
                AssertEqual(0L, test.State.Fight!.FightId, "LeaveFight revokes the old active attempt");
                test.Repeat(nameof(LeaveFightRequest), new LeaveFightRequest(), "Duplicate leave cannot pay or count a second exit");
                AssertEqual(tolerance + 1, test.State.AbnormalExitCount, "Duplicate leave preserves abnormal-exit count");
                test.Call(nameof(PreFightRequest), resumeRequest);
                fight.FightData = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent).FightData;
                fightId = fight.FightData.FightId;
                AssertEqual(tolerance + 1, test.State.AbnormalExitCount, "PreFight re-entry cannot erase charged exit history");
                StageTable stage = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == stageId);
                FightRebootTable reboot = TableReaderV2.Parse<FightRebootTable>().Single(row => row.Id == stage.RebootId);
                int cost = reboot.ConsumeCount[0];
                test.Fund(reboot.RebootItemId, cost);
                test.SaveFixture();
                test.Call(nameof(FightRebootRequest), new FightRebootRequest { FightId = unchecked((int)fightId), RebootCount = 1 });
                AssertEqual(0L, test.Balance(reboot.RebootItemId), "Resurrection consumes the stage's configured currency cost");
                test.Repeat(nameof(FightRebootRequest), new FightRebootRequest { FightId = unchecked((int)fightId), RebootCount = 1 }, "Resurrection retry cannot charge twice");
                test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = unchecked((int)fightId), RebootCount = 3 }, "Resurrection count cannot skip");
                test.Fund(reboot.RebootItemId, reboot.ConsumeCount[Math.Min(test.State.Fight!.ReviveCostCount, reboot.ConsumeCount.Count - 1)]);
                test.SaveFixture();
                int retries = test.State.Fight!.RestartCount;
                test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = unchecked((int)fightId) });
                AssertEqual(retries + 1, test.State.Fight!.RestartCount, "Restart remains attached to the durable fight");
            });
            FightSettleRequest repeated = CreateMissingStageSettleRequest(stageId, entered.FightData!.FightId, test.Session.player.PlayerData.Id);
            repeated.Result.IsWin = win;
            repeated.Result.IsForceExit = forceExit;
            test.Repeat(nameof(FightSettleRequest), repeated, "Settlement retry cannot grant a second fight reward");
            if (win)
            {
                AssertEqual(6, test.Step.StepType, "Win opens unclaimed fight rewards");
                test.Reject(nameof(BiancaTheatreRecvFightRewardRequest), new BiancaTheatreRecvFightRewardRequest { Uid = int.MaxValue }, "Foreign fight reward UID");
                test.Reject("BiancaTheatreEndRecvFightRewardRequest", null, "Unclaimed rewards cannot be silently discarded");
                while (test.State.RunNodeCount == nodesBefore) AdvanceBianca(test);
                AssertEqual(fightsBefore + 1, test.State.RunFightNodeCount, "One completed battle advances fight count once across settlement and reward closure");
                AssertEqual(nodesBefore + 1, test.State.RunNodeCount, "Reward closure completes its root node once");
                test.Relog("closed fight reward");
            }
            else
            {
                AssertEqual(0, test.Data.CurChapterId, "Loss or force exit settles and clears the adventure");
                BiancaTheatreSettleData settled = test.State.LastSettleData!;
                AssertEqual(true, TableReaderV2.Parse<BiancaTheatreEndingTable>().Any(row => row.Id == settled.EndId && row.PassType == 1),
                    "Loss resolves a failed ending, not a successful ending replay");
                test.Relog("failed adventure");
                test.Reject(nameof(PreFightRequest), new PreFightRequest
                {
                    PreFightData = new() { StageId = stageId, ChallengeCount = 1, CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [0, 0, 0] }
                }, "Failed run cannot resurrect an expired node");
            }
        }
    }

    private static void ValidateBiancaApprovedCombatPolicy()
    {
        // These assertions defend the explicitly approved private-server combat projection,
        // not an inferred retail algorithm or a captured combat payload.
        List<BiancaTheatreComboValidTable> validators = TableReaderV2.Parse<BiancaTheatreComboValidTable>();
        List<BiancaTheatreEffectGroupTable> groups = TableReaderV2.Parse<BiancaTheatreEffectGroupTable>();
        BiancaTheatreComboTable combo = TableReaderV2.Parse<BiancaTheatreComboTable>().OrderBy(row => row.Id).First(row =>
            row.ConditionLevel > 1 && row.ConditionLevel <= Convert.ToInt32(TableReaderV2.Parse<BiancaTheatreConfigTable>()
                .Single(config => config.Key == "MaxCharacterLevel").Value) &&
            TableReaderV2.Parse<BiancaTheatreChildComboTable>().Single(child => child.Id == row.ChildComboId).ActivationType == 2 &&
            row.EffectTarget.All(target => target == 1) && row.EffectValid.All(id => validators.Any(valid => valid.Id == id && valid.TriggerType == 1 && valid.ComboType == 1)) &&
            row.EffectId.Any(id => groups.Single(group => group.Id == id).FightEvents.Count > 0));
        int requiredStars = Convert.ToInt32(combo.ConditionLevel);
        BiancaTheatreBaseCharacterTable member = TableReaderV2.Parse<BiancaTheatreBaseCharacterTable>().First(row =>
            row.ReferenceComboId.Contains(combo.ChildComboId) &&
            TableReaderV2.Parse<BiancaTheatreCharacterLevelTable>().Any(level => level.CharacterId == row.CharacterId && level.Type == 1 && level.Level == combo.ConditionLevel));
        HashSet<long> comboEvents = combo.EffectId.SelectMany(id => groups.Single(group => group.Id == id).FightEvents)
            .Select(id => (long)id).ToHashSet();
        List<BiancaTheatreVisionTable> visions = TableReaderV2.Parse<BiancaTheatreVisionTable>().OrderBy(row => row.Id).ToList();
        List<BiancaTheatreVisionFactorLevelTable> bands = TableReaderV2.Parse<BiancaTheatreVisionFactorLevelTable>().OrderBy(row => row.Id).ToList();
        const int visionItem = 96185; // EN XBiancaTheatreConfigs.lua VisionItem.
        foreach ((bool open, int amount) in new[] { (false, 0), (true, 0), (true, Convert.ToInt32(visions[1].Min)) })
        {
            using BiancaCase test = new($"approved-combat-open-{open}-vision-{amount}");
            test.Start();
            while (test.State.Fight is null) AdvanceBianca(test);
            test.Data.Characters = [new() { CharacterId = member.CharacterId, Level = open ? requiredStars : requiredStars - 1 }];
            test.Data.IsOpenVision = open ? 1 : 0;
            test.Fund(visionItem, amount);
            int coins = TableReaderV2.Parse<BiancaTheatreGoldTable>().OrderBy(row => row.Id).First().Count;
            test.Fund(BiancaInnerCoin, coins);
            test.SaveFixture();
            int nodeId = test.Step.NodeData!.NodeId;
            int factorIndex = bands.FindIndex(band => amount >= Convert.ToInt32(band.Min) && amount <= band.Max);
            int expectedFactor = TableReaderV2.Parse<BiancaTheatreNodeTable>().Single(row => row.Id == nodeId).FightFactor[factorIndex];
            int expectedVision = open ? visions.Single(vision => amount >= Convert.ToInt32(vision.Min) && amount <= vision.Max).Id : 0;
            FightBianca(test, duringFight: entered =>
            {
                var data = entered.FightData!;
                System.Collections.IDictionary parameters = RequiredDynamicMap((object?)data.StageParams, "Approved Bianca stage parameters");
                AssertEqual(coins, Convert.ToInt32(RequiredDynamicValue(parameters, "GoldCount", "Bianca gold")), "Approved policy projects persisted inner currency");
                AssertEqual(amount, Convert.ToInt32(RequiredDynamicValue(parameters, "VisionCount", "Bianca vision")), "Approved policy projects real vision currency");
                AssertEqual(expectedVision, Convert.ToInt32(RequiredDynamicValue(parameters, "VisionStageId", "Bianca vision phase")), "Closed vision differs from open phase one at the same amount");
                AssertEqual(expectedFactor, Convert.ToInt32(RequiredDynamicValue(parameters, "FightFactor", "Bianca node factor")), "Approved factor follows the source vision band");
                var role = data.RoleData.Single(player => player.Id == (uint)test.Session.player.PlayerData.Id);
                System.Collections.IDictionary npc = RequiredDynamicMap((object?)role.NpcData.Single().Value, "Qualified combo NPC");
                HashSet<long> npcEvents = ReadIntegerList(RequiredDynamicValue(npc, "EventIds", "Qualified combo NPC"), "Qualified combo NPC events").ToHashSet();
                AssertEqual(open, comboEvents.IsSubsetOf(npcEvents), "Combo threshold changes the deployed NPC's actual combat events");
                object globalEventPayload = data.EventIds
                    ?? throw new InvalidDataException("Bianca fight omitted its global event list.");
                HashSet<long> globalEvents = ReadIntegerList(globalEventPayload, "Bianca global events").ToHashSet();
                AssertEqual(false, comboEvents.Overlaps(globalEvents), "Qualified target-only combo events cannot leak into global battle events");
            });
        }
    }

    private static void ValidateBiancaCharacterReceiptRecovery()
    {
        using BiancaCase test = new("character-receipt-recovery");
        BiancaTheatreLevelRewardTable reward = TableReaderV2.Parse<BiancaTheatreLevelRewardTable>().OrderBy(row => row.Id).First();
        test.Data.TotalExp = reward.UnlockScore;
        test.SaveFixture();
        Dictionary<int, long> before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        test.Characters.ThrowOnReplaceOne = true;
        try
        {
            test.Call(nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = reward.Id }, success: false);
            AssertEqual(0, test.Pushes.Count, "Partial inventory/character receipt commit emits no successful claim");
        }
        finally { test.Characters.ThrowOnReplaceOne = false; }
        AssertEqual(true, test.State.PendingMutation is not null, "Character receipt failure retains recoverable player intent");
        test.Relog("character receipt retry", pending: true);
        AssertBiancaInventoryRewards(test, before, [reward.RewardId]);
        AssertEqual(true, test.Data.GetRewardIds.Contains(reward.Id), "Character receipt retry finishes the same frozen claim");
        test.Repeat(nameof(BiancaTheatreGetRewardRequest), new BiancaTheatreGetRewardRequest { Id = reward.Id }, "Recovered split-document claim cannot duplicate goods");
    }

    private static void DriveBiancaRun(BiancaCase test)
    {
        int limit = TableReaderV2.Parse<BiancaTheatreNodeTable>().Count * 64;
        for (int transition = 0; test.Data.CurChapterId > 0 && transition < limit; transition++)
            AdvanceBianca(test, transition);
        AssertEqual(0, test.Data.CurChapterId, "Finite table graph reaches automatic settlement");
    }

    private static void AdvanceBianca(BiancaCase test, int variant = 0)
    {
        BiancaTheatreStep step = test.Step;
        switch (step.StepType)
        {
            case 1:
            case 2:
                int item = step.ItemIds[variant % step.ItemIds.Count];
                int oldCount = test.Data.Items.Count(value => value.ItemId == item);
                test.Relog("item choice");
                test.Call(nameof(BiancaTheatreSelectItemRewardRequest), new BiancaTheatreSelectItemRewardRequest { InnerItemId = item });
                List<BiancaTheatreItem> grantedItems = test.Data.CurChapterId > 0 ? test.Data.Items : test.State.LastSettleData!.Items;
                AssertEqual(oldCount + 1, grantedItems.Count(value => value.ItemId == item), "Item choice grants one selected occurrence");
                break;
            case 3:
                test.Relog("recruit ticket choice");
                test.Call(nameof(BiancaTheatreSelectRecruitTickRequest), new BiancaTheatreSelectRecruitTickRequest { TickId = step.TickIds[variant % step.TickIds.Count] });
                break;
            case 4:
            case 7:
                int candidate = step.RefreshCharacterIds.FirstOrDefault(id =>
                {
                    BiancaTheatreCharacter? role = test.Data.Characters.SingleOrDefault(character => character.CharacterId == id);
                    int type = step.StepType == 7 || role?.IsDecay > 0 ? 2 : 1;
                    return id > 0 && !step.RecruitCharacterIds.Contains(id) &&
                        (step.StepType != 7 || role is { IsDecay: 0 }) &&
                        TableReaderV2.Parse<BiancaTheatreCharacterLevelTable>().Any(row => row.CharacterId == id &&
                            row.Level == (role?.Level ?? 0) + 1 && row.Type == type);
                });
                if (step.CurRecruitCount < step.RecruitCount && candidate > 0)
                {
                    byte[] roster = test.Session.character.ToBson();
                    int oldLevel = test.Data.Characters.SingleOrDefault(character => character.CharacterId == candidate)?.Level ?? 0;
                    test.Call(nameof(BiancaTheatreRecruitCharacterRequest), new BiancaTheatreRecruitCharacterRequest { CharacterId = candidate });
                    AssertEqual(oldLevel + 1, test.Data.Characters.Single(character => character.CharacterId == candidate).Level, "Recruit adds or upgrades one local character");
                    AssertEqual(true, roster.SequenceEqual(test.Session.character.ToBson()), "Recruitment cannot grant an account character");
                }
                else test.Call("BiancaTheatreEndRecruitRequest");
                break;
            case 5:
                BiancaTheatreNodeData nodes = step.NodeData ?? throw new InvalidDataException("Node step must contain node data.");
                BiancaTheatreNodeSlot? selected = nodes.Slots.SingleOrDefault(slot => slot.Selected != 0);
                if (selected is null)
                {
                    selected = nodes.Slots.OrderBy(slot => slot.SlotType == 1 ? 0 : slot.SlotType).ThenBy(slot => slot.SlotId).First();
                    test.Call(nameof(BiancaTheatreSelectNodeRequest), new BiancaTheatreSelectNodeRequest { NodeId = nodes.NodeId, SlotId = selected.SlotId });
                    AssertEqual(1, test.Step.NodeData!.Slots.Count(slot => slot.Selected != 0), "Node selection excludes sibling branches");
                    test.Relog("selected node");
                }
                else if (test.State.Fight is not null) FightBianca(test);
                else if (selected.SlotType == 3) test.Call("BiancaTheatreEndNodeRequest");
                else if (selected.SlotType == 2) AdvanceBiancaEvent(test, selected, variant);
                else throw new InvalidDataException("Selected fight node did not authorize a pending fight.");
                break;
            case 6:
                BiancaTheatreFightReward? reward = step.FightRewards.FirstOrDefault(value => value.Received == 0);
                test.Relog("fight reward choice");
                if (reward is null) test.Call("BiancaTheatreEndRecvFightRewardRequest");
                else
                {
                    int uid = step.Uid;
                    test.Call(nameof(BiancaTheatreRecvFightRewardRequest), new BiancaTheatreRecvFightRewardRequest { Uid = reward.Uid });
                    AssertEqual(1, test.Data.CurChapterDb!.Steps.Single(value => value.Uid == uid).FightRewards.Single(value => value.Uid == reward.Uid).Received,
                        "Fight reward records its receipt before opening a nested choice");
                }
                break;
            default: throw new InvalidDataException($"Unknown client step discriminant {step.StepType}.");
        }
    }

    private static void AdvanceBiancaEvent(BiancaCase test, BiancaTheatreNodeSlot slot, int variant)
    {
        BiancaTheatreEventTable row = TableReaderV2.Parse<BiancaTheatreEventTable>().Single(value => value.EventId == slot.EventId && value.StepId == slot.CurStepId);
        int[] options = row.Type == 2
            ? Enumerable.Range(0, row.OptionType.Count).Where(index => row.OptionType[index] > 0 &&
                (index >= row.OptionCondition.Count || row.OptionCondition[index] == 0) &&
                (row.OptionType[index] is not (1 or 2) || (row.OptionItemType[index] switch
                {
                    1 => test.Balance(row.OptionItemId[index]) >= row.OptionItemCount[index],
                    2 => test.Data.Items.Count(item => item.ItemId == row.OptionItemId[index]) >= row.OptionItemCount[index],
                    _ => false
                }))).Select(index => index + 1).ToArray()
            : [0];
        if (options.Length == 0) throw new InvalidDataException($"Event {row.EventId}/{row.StepId} offers no ungated fixture route.");
        int option = options[variant % options.Length];
        test.Call(nameof(BiancaTheatreEventNodeNextStepRequest), new BiancaTheatreEventNodeNextStepRequest { CurEventStepId = slot.CurStepId, OptionId = option });
        AssertEqual(true, test.State.HistoryPassedEventRecord.TryGetValue(slot.EventId, out List<int>? passed) && passed.Contains(row.StepId),
            "Event transition records its source step for subsequent conditions");
    }

    private static PreFightResponse FightBianca(BiancaCase test, bool win = true, bool forceExit = false, Action<PreFightResponse>? duringFight = null)
    {
        BiancaTheatreFightState pending = test.State.Fight ?? throw new InvalidDataException("No authorized Bianca battle.");
        BiancaTheatreCharacter recruit = test.Data.Characters.First();
        int robot = TableReaderV2.Parse<BiancaTheatreCharacterLevelTable>()
            .Single(row => row.CharacterId == recruit.CharacterId && row.Level == recruit.Level && row.Type == (recruit.IsDecay > 0 ? 2 : 1)).RobotId;
        test.Call(nameof(BiancaTheatreSetSingleTeamRequest), new BiancaTheatreSetSingleTeamRequest
        {
            TeamData = new BiancaTheatreTeamData { CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [robot, 0, 0] }
        });
        PreFightRequest request = new()
        {
            PreFightData = new() { StageId = pending.StageId, ChallengeCount = 1, CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = [robot, 0, 0] }
        };
        test.Call(nameof(PreFightRequest), request);
        PreFightResponse preFight = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent);
        if (preFight.FightData is null) throw new InvalidDataException("Authorized fight has no deployment.");
        AssertPreFightDeployedCharacterIds(preFight, test.Session.player.PlayerData.Id, [recruit.CharacterId], "Bianca fight deploys the recruited role");
        AssertEqual(pending.StageId, preFight.FightData.StageId, "Pending node controls stage authorization");
        duringFight?.Invoke(preFight);
        FightSettleRequest settle = CreateMissingStageSettleRequest(pending.StageId, preFight.FightData.FightId, test.Session.player.PlayerData.Id);
        settle.Result.IsWin = win;
        settle.Result.IsForceExit = forceExit;
        settle.Result.RebootCount = test.State.Fight!.RebootCount;
        int stagesBefore = test.Stages.ReplaceOneCalls;
        test.Call(nameof(FightSettleRequest), settle);
        AssertEqual(stagesBefore, test.Stages.ReplaceOneCalls, "Bianca settlement does not mutate generic stage progression");
        AssertEqual(false, test.Pushes.Any(push => push.Name == nameof(NotifyStageData)), "Bianca battle cannot emit generic stage unlock rewards");
        return preFight;
    }
}
