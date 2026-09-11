using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre3;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ClientConfig = AscNet.Table.V2.client.theatre3.Theatre3ClientConfigTable;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    private static int LifecycleConfig(string key) => checked((int)TableReaderV2.Parse<Theatre3ConfigTable>().Single(row => row.Key == key).Value);
    private static int LifecycleClientConfig(string key) => int.Parse(TableReaderV2.Parse<ClientConfig>().Single(row => row.Key == key).Values[0], System.Globalization.CultureInfo.InvariantCulture);

    internal static void EnsureAvailable(Mutation mutation, DateTimeOffset now)
    {
        var activity = TableReaderV2.Parse<Theatre3ActivityTable>().Where(row => ActivityScheduleService.IsOpen(row.TimeId, now)).OrderByDescending(row => row.Id).FirstOrDefault();
        Require(activity is not null && mutation.Session.player.PlayerData.Level >= 70, 20203001);
        mutation.Data.CurActivityId = activity!.Id;
        InitializeLifecycle(mutation);
    }

    private static void InitializeLifecycle(Mutation mutation)
    {
        foreach (var group in TableReaderV2.Parse<Theatre3CharacterLevelTable>().GroupBy(row => row.CharacterId))
            if (!mutation.Data.Characters.Any(character => character.CharacterId == group.Key))
                mutation.Data.Characters.Add(new() { CharacterId = group.Key, Level = group.Min(row => row.Level) });
        InitializeMeta(mutation);
    }

    internal static void PrepareLogin(Session session)
    {
        ResumePending(session, forLogin: true);
        Mutation mutation = new(session);
        // Request IDs belong to the connection; retaining them across login collides with
        // a new connection's numbering. Durable offers/results are separate state.
        mutation.State.RequestReceipts.Clear();
        var activity = TableReaderV2.Parse<Theatre3ActivityTable>().Where(row => ActivityScheduleService.IsOpen(row.TimeId, DateTimeOffset.UtcNow)).OrderByDescending(row => row.Id).FirstOrDefault();
        mutation.Data.CurActivityId = session.player.PlayerData.Level >= 70 ? activity?.Id ?? 0 : 0;
        if (mutation.Data.CurActivityId > 0) InitializeLifecycle(mutation);
        // Never invoke BeginAdventure or consume pending choices on login.
        if (!mutation.State.ToBson().AsSpan().SequenceEqual(session.player.Theatre3.ToBson()))
            Persist(mutation, string.Empty, string.Empty, null);
    }

    internal static NotifyTheatre3ActivityData BuildLoginData(Session session)
    {
        var snapshot = BsonSerializer.Deserialize<NotifyTheatre3ActivityData>(session.player.Theatre3.Data.ToBson());
        ProjectSettlementRecovery(snapshot, session.player.Theatre3);
        return snapshot;
    }

    [RequestPacketHandler("Theatre3SelectDifficultyRequest")]
    public static void Theatre3SelectDifficultyRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SelectDifficultyRequest, Theatre3SelectDifficultyResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.Data.DifficultyId == 0, 20203003);
            var difficulty = TableReaderV2.Parse<Theatre3DifficultyTable>().SingleOrDefault(row => row.Id == request.Difficulty);
            Require(difficulty is not null && IsConditionSatisfied(mutation, difficulty.ConditionId ?? 0), 20203002);
            // Local run-currency rule: a new adventure starts from configured startup
            // grants, never an old run/admin wallet. Resume and rejected starts do not drain.
            long previousCoin = mutation.Balance(96189);
            if (previousCoin > 0)
                mutation.Cost(96189, checked((int)previousCoin), recordSpending: false);
            var inherited = mutation.State.LastSettle is { } previous && previous.ChapterCount >= LifecycleConfig("InheritEquipNeedChapterCount")
                ? previous.Equips.Select(equip => equip.EquipId).Distinct().ToList() : [];
            mutation.State.RunId = checked(mutation.State.RunId + 1);
            mutation.State.EffectCounters.Clear();
            mutation.State.AppliedEffectTriggers.Clear();
            mutation.State.LastSettle = null;
            mutation.State.SettlementRecoveryPending = false;
            mutation.Data.DifficultyId = request.Difficulty;
            mutation.Data.DestinyCharacterId = 0;
            mutation.Data.QubitValueA = mutation.Data.QubitValueB = 0;
            mutation.Data.ChapterSwitch = false;
            mutation.Data.Items.Clear();
            mutation.Data.Equips.Clear();
            mutation.Data.FightRecords.Clear();
            mutation.Data.PassEventFightNodes.Clear();
            mutation.Data.CurTeamData = new() { CardIds = [0, 0, 0], RobotIds = [0, 0, 0], CaptainPos = 1, FirstFightPos = 1 };
            mutation.Data.EquipPos = Enumerable.Range(1, 3).Select(pos => new Theatre3EquipPos
            {
                PosId = pos, ColorId = pos == 1 ? 2 : pos == 2 ? 1 : 3, Capacity = LifecycleConfig("InitEquipPosCapacity")
            }).ToList();
            RecomputeCapacities(mutation);
            BeginAdventure(mutation);
            OpenInheritedEquips(mutation, inherited);
            // Documented local order: starting-effect children precede the pending setup
            // choices; the scheduler retains every original offer beneath those children.
            ApplyEffectTrigger(mutation, Theatre3EffectTrigger.RunStart);
            response.DifficultyId = mutation.Data.DifficultyId;
            response.MaxEnergy = mutation.Data.MaxEnergy;
            response.EquipPos = Clone(mutation.Data.EquipPos);
        }, onFailure: failedSession => failedSession.SendPush(
            BsonSerializer.Deserialize<NotifyTheatre3ActivityData>(failedSession.player.Theatre3.Data.ToBson())));

    internal static List<Theatre3Step> CreateInitialSteps(Mutation mutation)
    {
        List<Theatre3Step> steps = [];
        if (IsConditionSatisfied(mutation, LifecycleConfig("DestinyOpenCondition")))
        {
            var threshold = TableReaderV2.Parse<Theatre3DestinyWeightTable>().OrderBy(row => row.Value).FirstOrDefault(row => row.Value >= mutation.Data.DestinyValue)
                ?? TableReaderV2.Parse<Theatre3DestinyWeightTable>().MaxBy(row => row.Value)!;
            // Documented local rule: threshold is an inclusive upper band; weights are
            // basis points. Repeated projection uses RepeatWeight. First projection is
            // guaranteed and includes the configured version character.
            int weight = mutation.State.DestinyTriggered ? threshold.RepeatWeight : threshold.Weight;
            if (!mutation.State.DestinyTriggered || Random.Shared.Next(10000) < weight)
            {
                var pool = TableReaderV2.Parse<Theatre3CharacterRecruitTable>().Where(row => row.Weight > 0).ToList();
                List<int> offered = [];
                int guaranteed = LifecycleConfig("DestinyCharacterId");
                int group = LifecycleConfig("DestinyGuaranteeGroupId");
                if (!mutation.State.DestinyTriggered && pool.Any(row => row.CharacterId == guaranteed)) offered.Add(guaranteed);
                // Three distinct offers match the three-choice client panel. Group0 means
                // unrestricted; each repeat uses its explicit recruitment repeat weight.
                while (offered.Count < 3)
                {
                    var candidates = pool.Where(row => !offered.Contains(row.CharacterId) && (group == 0 || row.GroupId == group)).ToList();
                    int Weight(Theatre3CharacterRecruitTable row) => mutation.State.LastDestinyCharacterIds.Contains(row.CharacterId) ? row.RepeatWeight : row.Weight;
                    int total = candidates.Sum(Weight);
                    Require(total > 0, 20203067);
                    int draw = Random.Shared.Next(total);
                    foreach (var candidate in candidates)
                        if ((draw -= Weight(candidate)) < 0) { offered.Add(candidate.CharacterId); break; }
                }
                if (mutation.State.DestinyTriggered && mutation.Data.DestinyValue < LifecycleConfig("DestinyValueMaxLimit"))
                    RecordProgress(mutation, 104022);
                mutation.State.LastDestinyCharacterIds = offered.ToList();
                mutation.State.DestinyTriggered = true;
                mutation.Data.DestinyValue = 0;
                mutation.Push(new NotifyTheatre3DestinyValue { DestinyValue = 0 });
                steps.Add(new() { StepType = 8, DestinyCharacterIds = offered });
            }
        }
        var difficulty = TableReaderV2.Parse<Theatre3DifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId);
        if (difficulty.InitialItem is > 0)
        {
            int group = LifecycleClientConfig("ChooseItemGroupId");
            var items = TableReaderV2.Parse<Theatre3ItemGroupTable>().Where(row => row.GroupId == group && IsConditionSatisfied(mutation, row.InitialCondition ?? 0)).OrderBy(row => row.Id).Select(row => row.Id).ToList();
            Require(items.Count > 0, 20203078);
            items.Insert(0, 0); // Native prop UI explicitly offers choosing no initial item.
            steps.Add(new() { StepType = 9, ItemIds = items });
        }
        steps.Add(new() { StepType = 1 });
        return steps;
    }

    internal static void ValidateTheatre3Team(Mutation mutation, Theatre3TeamData team)
    {
        Require(mutation.Data.DifficultyId > 0, 20203013);
        Require(team is not null && team.CardIds is { Count: 3 } && team.RobotIds is { Count: 3 }, 20203034);
        Require(team!.CaptainPos is >= 1 and <= 3 && team.FirstFightPos is >= 1 and <= 3, 20203005);
        Require(team.EnterCgIndex is >= 0 and <= 3, 20203006);
        Require(team.SettleCgIndex is >= 0 and <= 3, 20203007);
        HashSet<int> identities = [];
        int energy = 0;
        for (int i = 0; i < 3; i++)
        {
            int card = team.CardIds[i], robot = team.RobotIds[i];
            Require(card >= 0 && robot >= 0 && (card == 0 || robot == 0), 20203034);
            if (card == 0 && robot == 0) continue;
            var row = TableReaderV2.Parse<Theatre3CharacterRecruitTable>().SingleOrDefault(row => robot > 0 ? row.RobotId == robot : row.CharacterId == card);
            Require(row is not null, robot > 0 ? 20203009 : 20203010);
            Require(card == 0 || mutation.Session.character.Characters.Any(character => character.Id == card), 20203011);
            Require(identities.Add(row!.CharacterId), 20203008);
            if (row.CharacterId != mutation.Data.DestinyCharacterId)
                energy = checked(energy + TableReaderV2.Parse<Theatre3CharacterGroupTable>().Single(group => group.Id == row.GroupId).EnergyCost);
        }
        Require(energy <= mutation.Data.MaxEnergy, 20203012);
        Require(team.CardIds[team.CaptainPos - 1] + team.RobotIds[team.CaptainPos - 1] > 0
            && team.CardIds[team.FirstFightPos - 1] + team.RobotIds[team.FirstFightPos - 1] > 0, 20203005);
    }

    [RequestPacketHandler("Theatre3SetTeamRequest")]
    public static void Theatre3SetTeamRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SetTeamRequest, Theatre3SetTeamResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.State.Fight is null, 20203034);
            CurrentStep(mutation, 1, 2);
            ValidateTheatre3Team(mutation, request.TeamData);
            Require(request.EquipPosInfos is { Count: 3 } && request.EquipPosInfos.All(info => info is not null)
                && request.EquipPosInfos.Select(info => info.Pos).Order().SequenceEqual(new[] { 1, 2, 3 })
                && request.EquipPosInfos.Select(info => info.ColorId).Order().SequenceEqual(new[] { 1, 2, 3 }), 20203034);
            foreach (var info in request.EquipPosInfos)
            {
                Require(info.CardId == request.TeamData.CardIds[info.ColorId - 1] && info.RobotId == request.TeamData.RobotIds[info.ColorId - 1], 20203034);
                var pos = mutation.Data.EquipPos.Single(pos => pos.PosId == info.Pos);
                pos.ColorId = info.ColorId;
                pos.CardId = info.CardId;
                pos.RobotId = info.RobotId;
                if (info.CardId > 0 || info.RobotId > 0)
                    mutation.State.RunCharacterIds.Add(TableReaderV2.Parse<Theatre3CharacterRecruitTable>().Single(row => info.RobotId > 0 ? row.RobotId == info.RobotId : row.CharacterId == info.CardId).CharacterId);
            }
            mutation.Data.CurTeamData = Clone(request.TeamData);
            mutation.Push(new NotifyTheatre3EquipPosCapacityChange { EquipPos = Clone(mutation.Data.EquipPos) });
        });

    [RequestPacketHandler("Theatre3EndRecruitRequest")]
    public static void Theatre3EndRecruitRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3EndRecruitRequest, Theatre3EndRecruitResponse>(session, packet, (mutation, request, response) =>
        {
            CurrentStep(mutation, 1);
            Require(mutation.Data.CurTeamData is not null, 20203014);
            ValidateTheatre3Team(mutation, mutation.Data.CurTeamData!);
            CompleteStep(mutation);
        });

    [RequestPacketHandler("Theatre3DestinySelectRequest")]
    public static void Theatre3DestinySelectRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3DestinySelectRequest, Theatre3DestinySelectResponse>(session, packet, (mutation, request, response) =>
        {
            var step = CurrentStep(mutation, 8);
            Require(step.DestinyCharacterIds.Contains(request.CharacterId), 20203067);
            mutation.Data.DestinyCharacterId = request.CharacterId;
            CompleteStep(mutation);
            RecomputeCapacities(mutation);
        });

    [RequestPacketHandler("Theatre3SelectInitialItemRequest")]
    public static void Theatre3SelectInitialItemRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SelectInitialItemRequest, Theatre3SelectInitialItemResponse>(session, packet, (mutation, request, response) =>
        {
            var step = CurrentStep(mutation, 9);
            Require(step.SelectedItemId == 0, 20203041);
            Require(step.ItemIds.Contains(request.ItemId), 20203042);
            step.SelectedItemId = request.ItemId;
            if (request.ItemId != 0)
            {
                // This RPC names ItemId but the prop grid sends ItemGroup.Id.
                var item = TableReaderV2.Parse<Theatre3ItemGroupTable>().Single(row => row.Id == request.ItemId);
                Require(IsConditionSatisfied(mutation, item.InitialCondition ?? 0), 20203079);
                AddItem(mutation, item.ItemId);
            }
            FinishStep(mutation, step);
        });

    [RequestPacketHandler("Theatre3SwitchParallelChapterRequest")]
    public static void Theatre3SwitchParallelChapterRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SwitchParallelChapterRequest, Theatre3SwitchParallelChapterResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.Data.ChapterSwitch, 20203073);
            var step = CurrentStep(mutation, 2);
            if (step.NodeData is not { } node || step.ConnectNodeData is not { } connected)
                throw new ServerCodeException("Parallel chapter does not exist.", 20203074);
            Require(node.Selected == 0 && connected.Selected == 0
                && !node.Slots.Any(slot => slot.Selected != 0) && !connected.Slots.Any(slot => slot.Selected != 0), 20203075);
            var destination = node.ChapterId == request.ChapterId ? node : connected.ChapterId == request.ChapterId ? connected : null;
            Require(destination is not null && destination.Slots.Count > 0 && request.ChapterId != mutation.Data.CurChapterId, 20203074);
            var chapter = TableReaderV2.Parse<Theatre3ChapterTable>().Single(row => row.Id == request.ChapterId);
            Require(chapter.ChapterType != 1 || IsConditionSatisfied(mutation, LifecycleClientConfig("ToALineCloseCondition")), 20203073);
            mutation.Data.CurChapterId = request.ChapterId;
            mutation.Push(new NotifyTheatre3SwitchLine { ChapterId = request.ChapterId });
        });
}
