using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.maintaineraction;
using AscNet.Table.V2.share.functional;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateMaintainerActionCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        foreach (string name in new[] { nameof(MaintainerActionNodeEventRequest), nameof(MaintainerActionPlayCardRequest),
            nameof(MaintainerActionRecordRequest), nameof(PreFightRequest), nameof(FightSettleRequest) })
            _ = GetRegisteredRequestHandlerMethod(name);

        MaintainerActionConfigTable cfg = TableReaderV2.Parse<MaintainerActionConfigTable>().Single();
        MaintainerActionLevelTable[] levels = TableReaderV2.Parse<MaintainerActionLevelTable>().OrderBy(row => row.Level).ToArray();
        Dictionary<int, MaintainerActionEventTable> events = TableReaderV2.Parse<MaintainerActionEventTable>().ToDictionary(row => row.Id);
        int ring = cfg.NodeCount + 1;
        AssertEqual(16, ring, "client UI ring (XUiPanelIntermediate MapNodeMaxCount) matches NodeCount + start node");
        foreach (int eventId in new[] { MaintainerActionModule.EventEmptyRoad, MaintainerActionModule.EventMove, MaintainerActionModule.EventFight,
            MaintainerActionModule.EventBox, MaintainerActionModule.EventForward, MaintainerActionModule.EventFallBack, MaintainerActionModule.EventCardChange,
            MaintainerActionModule.EventDirectionChange, MaintainerActionModule.EventHighMorale, MaintainerActionModule.EventUnknownArea,
            MaintainerActionModule.EventStart, MaintainerActionModule.EventExternalReinforcement, MaintainerActionModule.EventStartBonus,
            MaintainerActionModule.EventBonusAction, MaintainerActionModule.EventWarehouse, MaintainerActionModule.EventExplore })
            AssertEqual(true, events.ContainsKey(eventId), $"authored MaintainerActionEvent {eventId} exists");

        // The entry gate is derived from FunctionalOpen 1302's authored level condition.
        ConditionTable[] allConditions = TableReaderV2.Parse<ConditionTable>().ToArray();
        int[] gateIds = TableReaderV2.Parse<FunctionalOpenTable>().Single(row => row.Id == 1302).Condition.Where(id => id > 0).ToArray();
        int gateLevel = gateIds.SelectMany(id => allConditions.Single(row => row.Id == id) is { Formula: { Length: > 0 } formula }
                ? formula.Split('|', '&').Select(term => int.Parse(term.Trim())) : [id])
            .Select(id => allConditions.Single(row => row.Id == id)).Where(row => row.Type == 10101).Min(row => row.Params[0]);

        const long uid = 98_800;
        uint[] ownedIds = TableReaderV2.Parse<AscNet.Table.V2.share.character.CharacterTable>()
            .Where(row => row.Type == 1).OrderBy(row => row.Id).Take(3).Select(row => (uint)row.Id).ToArray();
        DateTimeOffset origin = DateTimeOffset.UtcNow;
        TimeSpan shift = TimeSpan.Zero;
        Func<DateTimeOffset> savedClock = MaintainerActionModule.Clock;
        MaintainerActionModule.Clock = () => origin + shift;
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out var inventories, out var stages);
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(uid);
            foreach (uint id in ownedIds) character.Characters.Add(CreateLoginAccountCompatibilityCharacter(id, fashionId: 0));
            using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "maintainer-action");
            Session session = harness.Session;
            int packetId = 98_801;

            (T Response, List<(string Name, JObject Body)> Pushes) Call<T>(string requestName, object request, string responseName)
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler(requestName, session, id, request);
                List<(string Name, JObject Body)> pushes = [];
                for (int index = 0; index < 64; index++)
                {
                    Packet packet = harness.ReadPacket($"MaintainerAction {requestName} packet {index}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
                        continue;
                    }
                    AssertEqual(id, MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Id, "MaintainerAction response correlation");
                    return (ReadResponsePayload<T>(packet, responseName), pushes);
                }
                throw new InvalidDataException($"Missing {responseName}.");
            }
            List<(string Name, JObject Body)> ReadPushes(int count)
            {
                List<(string Name, JObject Body)> pushes = [];
                for (int index = 0; index < count; index++)
                {
                    Packet packet = harness.ReadPacket($"MaintainerAction push {index}");
                    AssertEqual(Packet.ContentType.Push, packet.Type, "server-initiated packet is a push");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushes.Add((push.Name, JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content))));
                }
                return pushes;
            }
            MaintainerActionState State() => session.player.MaintainerAction;
            string Bag() => string.Join(";", session.inventory.Items.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
            (MaintainerActionNodeEventResponse Response, List<(string Name, JObject Body)> Pushes) NodeEvent() =>
                Call<MaintainerActionNodeEventResponse>(nameof(MaintainerActionNodeEventRequest), new MaintainerActionNodeEventRequest(), nameof(MaintainerActionNodeEventResponse));
            MaintainerActionPlayCardResponse PlayCard(int card) => Call<MaintainerActionPlayCardResponse>(
                nameof(MaintainerActionPlayCardRequest), new MaintainerActionPlayCardRequest { Card = card }, nameof(MaintainerActionPlayCardResponse)).Response;
            NotifyMaintainerActionData Login() => MessagePackSerializer.Deserialize<NotifyMaintainerActionData>(
                MessagePackSerializer.Serialize(MaintainerActionModule.BuildNotify(session) ?? throw new InvalidDataException("Maintainer Action closed for an eligible player.")));
            void Stand(int nodeId)
            {
                State().PlayerNodeId = nodeId;
                State().Triggered = false;
                State().Landing++;
                State().LastEvent = null;
            }
            MaintainerActionNodeState Find(int type) => State().Nodes.First(node => node.NodeType == type);
            void Reveal() { foreach (MaintainerActionNodeState node in State().Nodes) node.Revealed = true; }

            // ---- closed until the authored FunctionalOpen 1302 gate is met ----
            session.player.PlayerData.Level = gateLevel - 1;
            AssertEqual(true, MaintainerActionModule.BuildNotify(session) is null, "mode closed below the authored level gate");
            AssertEqual(20103012, PlayCard(1).Code, "play card while closed -> MaintainerActionNotOpen");
            AssertEqual(20103012, NodeEvent().Response.Code, "node event while closed -> MaintainerActionNotOpen");
            AssertEqual(20103012, Call<MaintainerActionRecordResponse>(nameof(MaintainerActionRecordRequest), new MaintainerActionRecordRequest(), nameof(MaintainerActionRecordResponse)).Response.Code, "records while closed");
            AssertEqual(true, AscNet.GameServer.Game.ActivityScheduleService.IsOpen(cfg.TimeId, origin), "authored TimeId available via labelled permanent policy");

            // ---- login shape ----
            session.player.PlayerData.Level = gateLevel;
            NotifyMaintainerActionData fresh = Login();
            AssertEqual(cfg.Id, fresh.Id, "login Id is the config row");
            AssertEqual(ring, fresh.Nodes.Count, "login map has NodeCount + start nodes");
            AssertEqual(true, fresh.Nodes.Select(node => node.NodeId).SequenceEqual(Enumerable.Range(0, ring)), "node ids 0..NodeCount");
            AssertEqual(1, fresh.Nodes[0].NodeType, "node 0 is the start node");
            AssertEqual(cfg.MaxHandCardCount, fresh.Cards.Count, "hand size from config");
            AssertEqual(true, fresh.Cards.All(card => card >= 1 && card <= cfg.MaxCardNumber), "cards within MaxCardNumber");
            AssertEqual(1, fresh.Players.Count, "single local player on board");
            AssertEqual(true, fresh.Players[0].PlayerId == (uint)uid && fresh.Players[0].NodeId == 0 && fresh.Players[0].IsNodeTriggered && !fresh.Players[0].Reverse, "player starts triggered at node 0");
            AssertEqual(false, fresh.HasMentorNode, "mentor system absent: no mentor node");
            AssertEqual(true, fresh.Nodes.All(node => node.NodeType != 13), "no mentor node on map");
            AssertEqual(true, fresh.ResetTime > origin.ToUnixTimeSeconds() && fresh.ResetTime <= origin.ToUnixTimeSeconds() + 7 * 86400, "ResetTime is the next weekly reset");
            AssertEqual(true, fresh.Nodes.Where(node => node.NodeType == 0).All(node => node.EventId == MaintainerActionModule.EventUnknownArea && node.Value is null), "hidden nodes expose only Unknown Area");
            AssertEqual(true, fresh.Nodes.Count(node => node.NodeType == 0) >= 2, "board hides nodes");
            AssertEqual(fresh.HasWarehouseNode, State().Nodes.Any(node => node.NodeType == MaintainerActionModule.TypeWarehouse), "HasWarehouseNode reflects board");
            MaintainerActionNodeState[] real = State().Nodes.ToArray();
            AssertEqual(true, real.Count(n => n.NodeType == MaintainerActionModule.TypeFight) is >= 1 && real.Count(n => n.NodeType == MaintainerActionModule.TypeFight) <= cfg.MaxFightWinCount, "fight nodes within MaxFightWinCount");
            AssertEqual(true, real.Count(n => n.NodeType == MaintainerActionModule.TypeBox) <= cfg.MaxBoxCount, "box nodes within MaxBoxCount");
            AssertEqual(true, real.Count(n => n.NodeType == MaintainerActionModule.TypeWarehouse) <= 1, "single warehouse node");
            int saves = players.ReplaceOneCalls;
            NotifyMaintainerActionData again = Login();
            AssertEqual(MessagePackSerializer.SerializeToJson(fresh), MessagePackSerializer.SerializeToJson(again), "repeat login projects the same persisted board");
            AssertEqual(saves, players.ReplaceOneCalls, "repeat login does not save");

            // node values come from tables
            MaintainerActionLevelTable level = levels.Last(row => row.Level <= gateLevel);
            foreach (MaintainerActionNodeState node in real)
            {
                JObject value = node.Value is null ? [] : JObject.Parse(node.Value);
                if (node.NodeType is 2 or 10) AssertEqual(true, level.StageIds.Contains(value.Value<int>("StageId")), "fight node stage is an authored Level stage");
                if (node.NodeType is 3 or 11) AssertEqual(level.BoxRewardIds, value.Value<int>("RewardId"), "box/warehouse reward is the authored BoxRewardIds entry");
                if (node.NodeType is 5 or 6)
                {
                    int step = value.Value<int>("Step"), target = value.Value<int>("TargetNodeId");
                    AssertEqual(node.NodeType == 5 ? Math.Min(node.NodeId + step, ring) % ring : node.NodeId - step, target, "relocation target arithmetic (clamped at the start node)");
                    AssertEqual(true, step >= 1 && (node.NodeType == 5 ? target == 0 || target > node.NodeId : target >= 0 && target < node.NodeId), "relocation never passes the start node");
                    AssertEqual(true, real[target].NodeType is not (5 or 6), "relocation never lands on a relocation node");
                }
            }
            // a board saved before the Starting Point clamp wrapped relocations past the start; login repairs it
            MaintainerActionNodeState back = State().Nodes.First(n => n.NodeType == 6), forward = State().Nodes.First(n => n.NodeType == 5);
            back.Value = new JObject { ["Step"] = back.NodeId + 1, ["TargetNodeId"] = ring - 1 }.ToString(Newtonsoft.Json.Formatting.None);
            forward.Value = new JObject { ["Step"] = ring - forward.NodeId + 1, ["TargetNodeId"] = 1 }.ToString(Newtonsoft.Json.Formatting.None);
            int repairSaves = players.ReplaceOneCalls;
            _ = Login();
            AssertEqual(repairSaves + 1, players.ReplaceOneCalls, "login persists the repaired relocations");
            AssertEqual(0, JObject.Parse(back.Value!).Value<int>("TargetNodeId"), "legacy back-relocation stops at the Starting Point");
            AssertEqual(back.NodeId, JObject.Parse(back.Value!).Value<int>("Step"), "legacy back-relocation shows the distance travelled");
            AssertEqual(0, JObject.Parse(forward.Value!).Value<int>("TargetNodeId"), "legacy forward relocation stops at the Starting Point");
            AssertEqual(ring - forward.NodeId, JObject.Parse(forward.Value!).Value<int>("Step"), "legacy forward relocation shows the distance travelled");
            // level 80+ pulls the other authored stage list (distinct state -> independent derivation)
            session.player.PlayerData.Level = levels.Last().Level;
            shift = TimeSpan.FromDays(7);
            NotifyMaintainerActionData otherWeek = Login();
            shift = TimeSpan.Zero;
            AssertEqual(true, MessagePackSerializer.SerializeToJson(otherWeek.Nodes) != MessagePackSerializer.SerializeToJson(fresh.Nodes), "different week -> different map");
            AssertEqual(true, State().Nodes.Where(n => n.NodeType is 2 or 10).All(n => levels.Last().StageIds.Contains(JObject.Parse(n.Value!).Value<int>("StageId"))), "high-level board uses the high Level stage list");
            // same uid + week at the same level regenerates identically; different player differs
            session.player.PlayerData.Level = gateLevel;
            State().ConfigId = 0;
            AssertEqual(MessagePackSerializer.SerializeToJson(fresh.Nodes), MessagePackSerializer.SerializeToJson(Login().Nodes), "board is deterministic per player and week");
            long otherUid = uid + 1;
            session.player.PlayerData.Id = otherUid;
            State().ConfigId = 0;
            NotifyMaintainerActionData otherPlayer = Login();
            session.player.PlayerData.Id = uid;
            AssertEqual(true, MessagePackSerializer.SerializeToJson(otherPlayer.Nodes) != MessagePackSerializer.SerializeToJson(fresh.Nodes), "different player -> different map");
            State().ConfigId = 0;
            Login();

            // ---- request validation ----
            int missingCard = Enumerable.Range(1, cfg.MaxCardNumber + 1).First(card => !State().Cards.Contains(card));
            AssertEqual(20103001, PlayCard(missingCard).Code, "card not in hand rejected");
            AssertEqual(20103011, NodeEvent().Response.Code, "node event on a resolved node -> NodeIsTriggered");
            AssertEqual(0, NodeEvent().Response.Node!.NodeId, "triggered error still carries the node");

            // ---- card moves follow the client's CradRoute exactly; collect oracle data for the original Lua ----
            List<(int From, bool Reverse, int Card, int Target)> moves = [];
            List<JObject> nodeEventNodes = [];
            JObject Wire(object value) => JObject.Parse(MessagePackSerializer.SerializeToJson(value));
            for (int from = 0; from < ring; from++)
            {
                foreach (bool reverse in new[] { false, true })
                {
                    foreach (int card in Enumerable.Range(1, cfg.MaxCardNumber))
                    {
                        State().PlayerNodeId = from;
                        State().Reverse = reverse;
                        State().Triggered = true;
                        State().UsedActionCount = 0;
                        State().ExtraActionCount = 0;
                        if (!State().Cards.Contains(card)) State().Cards[0] = card;
                        List<int> handBefore = [.. State().Cards];
                        MaintainerActionPlayCardResponse move = PlayCard(card);
                        AssertEqual(0, move.Code, $"card {card} from {from} reverse={reverse}");
                        List<int> route = MaintainerActionModule.CardRoute(from, reverse, card, ring);
                        AssertEqual(route[^1], move.NodeId, "server target equals the client's CradRoute end");
                        moves.Add((from, reverse, card, move.NodeId));
                        AssertEqual(move.NodeId, State().PlayerNodeId, "player persisted on target");
                        AssertEqual(false, State().Triggered, "arrival is untriggered");
                        handBefore.Remove(card);
                        handBefore.Add(move.NewCard);
                        AssertEqual(true, handBefore.SequenceEqual(State().Cards), "hand: played card removed, new card appended (client CardChange order)");
                        AssertEqual(true, move.NewCard >= 1 && move.NewCard <= cfg.MaxCardNumber, "new card within pool");
                        AssertEqual(1, State().UsedActionCount, "one action consumed");
                    }
                }
            }
            AssertEqual(true, moves.Select(m => m.Target).Distinct().Count() > 3, "targets vary with state");
            State().Reverse = false;
            State().PlayerNodeId = 0;
            State().Triggered = true;
            State().UsedActionCount = 0;
            State().ExtraActionCount = 0;
            State().Records.Clear();

            // ---- move guards ----
            // A revealed fight node may be left unresolved (fights are optional), so the guard only holds after landing
            // elsewhere. The board is seeded per player and week: choose a card whose target is not a revealed fight.
            bool LeavesUnresolvable(int card)
            {
                int target = MaintainerActionModule.CardRoute(0, false, card, ring)[^1];
                return State().Nodes.First(n => n.NodeId == target) is not { Revealed: true, NodeType: MaintainerActionModule.TypeFight };
            }
            int held = Enumerable.Range(1, cfg.MaxCardNumber).First(LeavesUnresolvable);
            if (!State().Cards.Contains(held)) State().Cards[0] = held;
            AssertEqual(0, PlayCard(held).Code, "valid card accepted");
            AssertEqual(20103010, PlayCard(State().Cards[0]).Code, "cannot move before the landing's event is resolved");
            State().Triggered = true;
            State().UsedActionCount = cfg.MaxDailyActionCount;
            AssertEqual(20103005, PlayCard(State().Cards[0]).Code, "daily action limit exhausted");
            State().ExtraActionCount = 1;
            AssertEqual(0, PlayCard(State().Cards[0]).Code, "extra action raises the limit");
            State().UsedActionCount = 0;
            State().ExtraActionCount = 0;

            // ---- node events, every implemented type, across weekly boards ----
            HashSet<string> covered = [];
            bool sawWarehouse = false, sawNoWarehouse = false;
            string baseBag = Bag();
            void ExerciseBoard()
            {
                Reveal();
                MaintainerActionState s = State();
                sawWarehouse |= s.HasWarehouseNode;
                sawNoWarehouse |= !s.HasWarehouseNode;
                void ResetCounters() { s.FightWinCount = s.BoxCount = s.WarehouseFinishCount = 0; s.UsedActionCount = 0; s.ExtraActionCount = 0; s.StartBonusClaimed = false; }
                ResetCounters();

                // Start: first landing grants the bonus, later landings do not.
                if (covered.Add("start"))
                {
                    Stand(0);
                    var first = NodeEvent();
                    AssertEqual(0, first.Response.Code, "start event");
                    AssertEqual(MaintainerActionModule.EventStartBonus, first.Response.Node!.EventId, "first start landing grants the bonus event");
                    AssertEqual(1, s.ExtraActionCount, "start bonus extra action");
                    AssertEqual(true, s.Triggered, "start resolved");
                    nodeEventNodes.Add(Wire(first.Response.Node));
                    var retry = NodeEvent();
                    AssertEqual(0, retry.Response.Code, "start retry replays");
                    AssertEqual(MessagePackSerializer.SerializeToJson(first.Response), MessagePackSerializer.SerializeToJson(retry.Response), "start retry identical");
                    AssertEqual(1, s.ExtraActionCount, "start retry does not grant twice");
                    Stand(0);
                    var second = NodeEvent();
                    AssertEqual(MaintainerActionModule.EventStart, second.Response.Node!.EventId, "second start landing is the plain start event");
                    AssertEqual(1, s.ExtraActionCount, "no second daily start bonus");
                    nodeEventNodes.Add(Wire(second.Response.Node));
                    ResetCounters();
                }
                // None
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 4) is { } none && covered.Add("none"))
                {
                    Stand(none.NodeId);
                    var result = NodeEvent();
                    AssertEqual(0, result.Response.Code, "empty road");
                    AssertEqual(MaintainerActionModule.EventEmptyRoad, result.Response.Node!.EventId, "empty road event");
                    AssertEqual(MaintainerActionModule.EventEmptyRoad, s.Records[0].EventId, "empty road recorded");
                    nodeEventNodes.Add(Wire(result.Response.Node));
                }
                // Box: reward, counter, retry, cap
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 3) is { } box && covered.Add("box"))
                {
                    Stand(box.NodeId);
                    string before = Bag();
                    var result = NodeEvent();
                    AssertEqual(0, result.Response.Code, "box event");
                    AssertEqual(1, s.BoxCount, "box counted");
                    AssertEqual(true, before != Bag(), "box reward granted from the authored reward row");
                    AssertEqual(true, result.Pushes.Any(push => push.Name == "NotifyItemDataList"), "box reward pushes inventory");
                    string afterBag = Bag();
                    var retry = NodeEvent();
                    AssertEqual(MessagePackSerializer.SerializeToJson(result.Response), MessagePackSerializer.SerializeToJson(retry.Response), "box retry replays the receipt");
                    AssertEqual(afterBag, Bag(), "box retry does not duplicate reward");
                    AssertEqual(1, s.BoxCount, "box retry does not recount");
                    nodeEventNodes.Add(Wire(result.Response.Node!));
                    s.BoxCount = cfg.MaxBoxCount;
                    Stand(box.NodeId);
                    var full = NodeEvent();
                    AssertEqual(20103003, full.Response.Code, "weekly box limit reached");
                    AssertEqual(true, s.Triggered && full.Response.Node is not null, "capped box resolves so the player can move on");
                    AssertEqual(afterBag, Bag(), "capped box grants nothing");
                    s.BoxCount = 0;
                }
                // Warehouse (only on some boards)
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 11) is { } warehouse && covered.Add("warehouse"))
                {
                    Stand(warehouse.NodeId);
                    string before = Bag();
                    var result = NodeEvent();
                    AssertEqual(0, result.Response.Code, "warehouse event");
                    AssertEqual(1, s.WarehouseFinishCount, "warehouse counted");
                    AssertEqual(true, before != Bag(), "warehouse reward granted");
                    nodeEventNodes.Add(Wire(result.Response.Node!));
                    s.WarehouseFinishCount = cfg.MaxWarehouseFinishCount;
                    Stand(warehouse.NodeId);
                    AssertEqual(20103017, NodeEvent().Response.Code, "warehouse limit reached");
                    s.WarehouseFinishCount = 0;
                }
                // Forward / FallBack
                foreach ((int type, string name) in new[] { (5, "forward"), (6, "fallback") })
                {
                    if (s.Nodes.FirstOrDefault(n => n.NodeType == type) is { } relocate && covered.Add(name))
                    {
                        JObject value = JObject.Parse(relocate.Value!);
                        Stand(relocate.NodeId);
                        int landing = s.Landing;
                        var result = NodeEvent();
                        AssertEqual(0, result.Response.Code, $"{name} event");
                        AssertEqual(value.Value<int>("TargetNodeId"), s.PlayerNodeId, $"{name} relocates the player to TargetNodeId");
                        AssertEqual(false, s.Triggered, $"{name} arrival awaits its own event");
                        AssertEqual(landing + 1, s.Landing, $"{name} is a fresh landing");
                        nodeEventNodes.Add(Wire(result.Response.Node!));
                    }
                }
                // CardChange
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 7) is { } change && covered.Add("cardchange"))
                {
                    Stand(change.NodeId);
                    List<int> handBefore = [.. s.Cards];
                    var result = NodeEvent();
                    JObject value = JObject.Parse(result.Response.Node!.Value);
                    int oldCard = value.Value<int>("OldCard"), newCard = value.Value<int>("NewCard");
                    handBefore.Remove(oldCard);
                    handBefore.Add(newCard);
                    AssertEqual(true, handBefore.SequenceEqual(s.Cards), "card change replaces OldCard with NewCard at the end of the hand");
                    nodeEventNodes.Add(Wire(result.Response.Node));
                }
                // Direction
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 8) is { } direction && covered.Add("direction"))
                {
                    Stand(direction.NodeId);
                    bool reverse = s.Reverse;
                    var result = NodeEvent();
                    AssertEqual(!reverse, s.Reverse, "direction flipped");
                    nodeEventNodes.Add(Wire(result.Response.Node!));
                    Stand(direction.NodeId);
                    NodeEvent();
                    AssertEqual(reverse, s.Reverse, "second landing flips back");
                }
                // ActionPoint
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 9) is { } action && covered.Add($"actionpoint{action.EventId}"))
                {
                    int amount = JObject.Parse(action.Value!).Value<int>("ExtraActionPoint");
                    Stand(action.NodeId);
                    int before = s.ExtraActionCount;
                    var result = NodeEvent();
                    AssertEqual(before + amount, s.ExtraActionCount, "extra action points granted");
                    AssertEqual(action.EventId, result.Response.Node!.EventId, "authored event id");
                    nodeEventNodes.Add(Wire(result.Response.Node));
                }
                // Simulation fight
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 10) is { } sim && covered.Add("simulation"))
                {
                    Stand(sim.NodeId);
                    string before = Bag();
                    var result = NodeEvent();
                    AssertEqual(0, result.Response.Code, "external reinforcement");
                    AssertEqual(1, s.FightWinCount, "external reinforcement counts as a fight win");
                    JObject value = JObject.Parse(result.Response.Node!.Value);
                    AssertEqual(true, value["RewardGoodsList"] is JArray, "reward goods ride the node value");
                    AssertEqual(((JArray)value["RewardGoodsList"]!).Count > 0, before != Bag(), "stage drops granted iff authored");
                    nodeEventNodes.Add(Wire(result.Response.Node));
                    s.FightWinCount = cfg.MaxFightWinCount;
                    Stand(sim.NodeId);
                    AssertEqual(20103009, NodeEvent().Response.Code, "fight limit reached");
                    s.FightWinCount = 0;
                }
                // Explore reveals a hidden node
                if (s.Nodes.FirstOrDefault(n => n.NodeType == 12) is { } explore && covered.Add("explore"))
                {
                    foreach (MaintainerActionNodeState node in s.Nodes.Where(n => n.NodeId != explore.NodeId).Take(2)) node.Revealed = false;
                    Stand(explore.NodeId);
                    var result = NodeEvent();
                    AssertEqual(0, result.Response.Code, "precision sensing");
                    JObject inner = (JObject)JObject.Parse(result.Response.Node!.Value)["Node"]!;
                    MaintainerActionNodeState sensed = s.Nodes[inner.Value<int>("NodeId")];
                    AssertEqual(true, sensed.Revealed && inner.Value<int>("NodeType") == sensed.NodeType, "sensed node revealed with its real type");
                    nodeEventNodes.Add(Wire(result.Response.Node));
                    foreach (MaintainerActionNodeState node in s.Nodes) node.Revealed = true;
                    Stand(explore.NodeId);
                    AssertEqual(20103016, NodeEvent().Response.Code, "nothing left to sense");
                }
                // Unknown area: resolving a hidden node reveals its real face
                if (s.Nodes.FirstOrDefault(n => n.NodeId > 0 && n.NodeType == 4) is { } hidden && covered.Add("unknown"))
                {
                    hidden.Revealed = false;
                    Stand(hidden.NodeId);
                    AssertEqual(0, Wire(MaintainerActionModule.BuildNotify(session)!)["Nodes"]![hidden.NodeId]!.Value<int>("NodeType"), "hidden node is UnKnow on the wire");
                    var result = NodeEvent();
                    AssertEqual(true, hidden.Revealed && result.Response.Node!.NodeType == 4, "unknown node revealed on arrival");
                    nodeEventNodes.Add(Wire(result.Response.Node!));
                }
                Reveal();
            }

            string[] required = ["start", "none", "box", "forward", "fallback", "cardchange", "direction", "simulation", "explore", "actionpoint8", "actionpoint13", "warehouse"];
            int weeks = 0;
            ExerciseBoard();
            while ((!required.All(covered.Contains) || !sawNoWarehouse) && ++weeks < 80)
            {
                shift = TimeSpan.FromDays(7 * weeks);
                MaintainerActionModule.EnsureResets(session);
                ReadPushes(1);
                ExerciseBoard();
            }
            AssertEqual(true, required.All(covered.Contains), $"every implemented node type exercised ({string.Join(",", required.Where(r => !covered.Contains(r)))} missing after {weeks} weeks)");
            AssertEqual(true, sawWarehouse && sawNoWarehouse, "boards both with and without the optional warehouse");

            // ---- fight: generic PreFight/FightSettle own combat, the mode owns node resolution ----
            shift = TimeSpan.FromDays(7 * (weeks + 1));
            MaintainerActionModule.EnsureResets(session);
            ReadPushes(1);
            Reveal();
            MaintainerActionNodeState fightNode = Find(MaintainerActionModule.TypeFight);
            int fightStage = JObject.Parse(fightNode.Value!).Value<int>("StageId");
            uint StageId(int id) => checked((uint)id);
            PreFightRequest PreRequest(int stage) => new() { PreFightData = new() { StageId = StageId(stage), ChallengeCount = 1,
                CardIds = [.. ownedIds], CaptainPos = 1, FirstFightPos = 1 } };
            int otherStage = level.StageIds.First(id => id != fightStage);
            Stand(State().Nodes.First(n => n.NodeType == 4).NodeId);
            AssertEqual(20103007, Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(fightStage), nameof(PreFightResponse)).Response.Code, "fight stage off a fight node rejected");
            Stand(fightNode.NodeId);
            AssertEqual(20103008, Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(otherStage), nameof(PreFightResponse)).Response.Code, "stage must be the node's stage");
            PreFightRequest multi = PreRequest(fightStage); multi.PreFightData.ChallengeCount = 2;
            AssertEqual(20103008, Call<PreFightResponse>(nameof(PreFightRequest), multi, nameof(PreFightResponse)).Response.Code, "multi-challenge rejected");
            State().FightWinCount = cfg.MaxFightWinCount;
            AssertEqual(20103009, Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(fightStage), nameof(PreFightResponse)).Response.Code, "fight limit reached rejected at PreFight");
            var finished = NodeEvent();
            AssertEqual(20103009, finished.Response.Code, "fight node at the cap -> MaintainerActionFightFinish");
            AssertEqual(true, State().Triggered && finished.Response.Node!.NodeType == 2, "capped fight node resolves with its node");
            State().FightWinCount = 0;
            Stand(fightNode.NodeId);
            var fightEvent = NodeEvent();
            AssertEqual(0, fightEvent.Response.Code, "fight node event presents the stage");
            AssertEqual(fightStage, JObject.Parse(fightEvent.Response.Node!.Value).Value<int>("StageId"), "node value carries StageId");
            AssertEqual(false, State().Triggered, "fight node stays unresolved until the fight is won");
            nodeEventNodes.Add(Wire(fightEvent.Response.Node));
            // The client's UseCard masks input and only unmasks on success, so leaving an unfought fight must succeed.
            var skip = PlayCard(State().Cards[0]);
            AssertEqual(0, skip.Code, "player may leave an unfought fight node");
            AssertEqual(true, State().PlayerNodeId == skip.NodeId && skip.NodeId != fightNode.NodeId, "skipping the fight moves the player");
            AssertEqual(0, State().FightWinCount, "skipping a fight does not count a win");
            Stand(fightNode.NodeId);
            var pre = Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(fightStage), nameof(PreFightResponse));
            AssertEqual(0, pre.Response.Code, "valid fight pre-fight");
            FightSettleRequest lose = CreateMissingStageSettleRequest(StageId(fightStage), pre.Response.FightData.FightId, uid);
            lose.Result.IsWin = false;
            Call<FightSettleResponse>(nameof(FightSettleRequest), lose, nameof(FightSettleResponse));
            AssertEqual(0, State().FightWinCount, "lost fight does not count");
            AssertEqual(false, State().Triggered, "lost fight does not resolve the node");
            pre = Call<PreFightResponse>(nameof(PreFightRequest), PreRequest(fightStage), nameof(PreFightResponse));
            AssertEqual(0, pre.Response.Code, "pre-fight again after a loss");
            FightSettleRequest win = CreateMissingStageSettleRequest(StageId(fightStage), pre.Response.FightData.FightId, uid);
            var settled = Call<FightSettleResponse>(nameof(FightSettleRequest), win, nameof(FightSettleResponse));
            AssertEqual(0, settled.Response.Code, "fight settlement accepted");
            AssertEqual(true, settled.Response.Settle?.IsWin == true, "fight settled as a win");
            JObject winPush = settled.Pushes.Single(push => push.Name == nameof(NotifyMaintainerActionFightWin)).Body;
            AssertEqual(1, winPush.Value<int>("FightWinCount"), "FightWin push carries the new count");
            AssertEqual(1, State().FightWinCount, "fight win persisted");
            AssertEqual(true, State().Triggered, "fight win resolves the node");
            AssertEqual(true, State().Records[0].EventId == 2 && State().Records[0].Values.SequenceEqual([fightStage]), "fight win recorded with the stage id");
            int winSaves = players.ReplaceOneCalls;
            var duplicate = Call<FightSettleResponse>(nameof(FightSettleRequest), win, nameof(FightSettleResponse));
            AssertEqual(true, duplicate.Pushes.All(push => push.Name != nameof(NotifyMaintainerActionFightWin)), "duplicate settlement pushes no second win");
            AssertEqual(1, State().FightWinCount, "duplicate settlement does not recount");
            AssertEqual(winSaves, players.ReplaceOneCalls, "duplicate settlement does not save the board");
            JObject fightWinWire = winPush;

            // ---- records ----
            var recordsResponse = Call<MaintainerActionRecordResponse>(nameof(MaintainerActionRecordRequest), new MaintainerActionRecordRequest(), nameof(MaintainerActionRecordResponse)).Response;
            AssertEqual(0, recordsResponse.Code, "records");
            AssertEqual(true, recordsResponse.Records.Count > 0 && recordsResponse.Records[0].EventId == 2, "records newest first");
            AssertEqual(true, recordsResponse.Records.All(record => events.ContainsKey(record.EventId)), "every record event is authored");
            JArray recordsJson = (JArray)Wire(recordsResponse)["Records"]!;
            // seed a move and a numeric event so the Lua formatter covers %d records
            State().Records.Add(new MaintainerActionRecordState { Time = origin.ToUnixTimeSeconds(), EventId = MaintainerActionModule.EventMove, Values = [3] });
            State().Records.Add(new MaintainerActionRecordState { Time = origin.ToUnixTimeSeconds(), EventId = MaintainerActionModule.EventForward, Values = [2] });
            recordsJson = (JArray)Wire(Call<MaintainerActionRecordResponse>(nameof(MaintainerActionRecordRequest), new MaintainerActionRecordRequest(), nameof(MaintainerActionRecordResponse)).Response)["Records"]!;

            // ---- daily reset ----
            // keep +1 day inside the current weekly period so only the daily rollover is observed
            while (AscNet.GameServer.Handlers.TaskModule.WeeklyResetDayIndex((origin + shift).ToUnixTimeSeconds()) >= 6)
                shift += TimeSpan.FromDays(1);
            bool rolled = State().Day != AscNet.GameServer.Handlers.TaskModule.CurrentDailyResetPeriod((origin + shift).ToUnixTimeSeconds());
            MaintainerActionModule.EnsureResets(session);
            if (rolled) ReadPushes(1);
            Stand(0);
            State().Triggered = true;
            State().UsedActionCount = 5;
            State().ExtraActionCount = 2;
            State().FightWinCount = 2;
            string boardBefore = MessagePackSerializer.SerializeToJson(Login().Nodes);
            List<int> handBeforeDay = [.. State().Cards];
            shift += TimeSpan.FromDays(1);
            MaintainerActionModule.EnsureResets(session);
            var dailyReset = ReadPushes(1).Single();
            AssertEqual(nameof(NotifyMaintainerActionDailyReset), dailyReset.Name, "daily reset push name");
            JObject dailyPush = dailyReset.Body;
            AssertEqual(0, dailyPush.Value<int>("UsedActionCount"), "daily reset clears used actions");
            AssertEqual(0, dailyPush.Value<int>("ExtraActionCount"), "daily reset clears extra actions");
            AssertEqual(0, State().UsedActionCount + State().ExtraActionCount, "daily reset persisted");
            AssertEqual(2, State().FightWinCount, "daily reset keeps weekly counters");
            AssertEqual(boardBefore, MessagePackSerializer.SerializeToJson(Login().Nodes), "daily reset keeps the board");
            AssertEqual(true, handBeforeDay.SequenceEqual(State().Cards), "daily reset keeps the hand");
            int resetSaves = players.ReplaceOneCalls;
            MaintainerActionModule.EnsureResets(session);
            AssertEqual(resetSaves, players.ReplaceOneCalls, "no second daily reset in the same period");

            // ---- weekly reset ----
            State().FightWinCount = 3; State().BoxCount = 2; State().UsedActionCount = 4;
            Stand(5); State().Reverse = true;
            string boardWeek1 = MessagePackSerializer.SerializeToJson(Login().Nodes);
            shift += TimeSpan.FromDays(7);
            MaintainerActionModule.EnsureResets(session);
            var weekly = ReadPushes(1).Single();
            AssertEqual(nameof(NotifyMaintainerActionData), weekly.Name, "weekly reset pushes the full board");
            JObject weekPush = weekly.Body;
            AssertEqual(false, JToken.DeepEquals(weekPush["Nodes"], JToken.Parse(boardWeek1)), "weekly reset builds a new map");
            AssertEqual(0, State().FightWinCount + State().BoxCount + State().UsedActionCount + State().WarehouseFinishCount, "weekly reset clears every counter");
            AssertEqual(0, State().PlayerNodeId, "weekly reset returns to start");
            AssertEqual(false, State().Reverse, "weekly reset clears direction");
            AssertEqual(0, State().Records.Count, "weekly reset clears records");
            AssertEqual(true, State().Triggered, "weekly reset leaves the start resolved");

            // ---- BSON reload ----
            NotifyMaintainerActionData progressed = Login();
            session.player = BsonSerializer.Deserialize<Player>(session.player.ToBson());
            NotifyMaintainerActionData reloaded = Login();
            AssertEqual(MessagePackSerializer.SerializeToJson(progressed), MessagePackSerializer.SerializeToJson(reloaded), "BSON reload preserves the complete client projection");
            Stand(fightNode.NodeId);
            Stand(State().Nodes.First(n => n.NodeType == 4).NodeId);
            var preReload = NodeEvent();
            session.player = BsonSerializer.Deserialize<Player>(session.player.ToBson());
            var replay = NodeEvent();
            AssertEqual(MessagePackSerializer.SerializeToJson(preReload.Response), MessagePackSerializer.SerializeToJson(replay.Response), "event receipt survives BSON reload");
            AssertEqual(true, MessagePackSerializer.SerializeToJson(fresh) != MessagePackSerializer.SerializeToJson(reloaded), "fresh and progressed projections derive from state");

            // ---- UI-driven landing flow replayed through the original client (every node type, relocation chains) ----
            JArray flow = [];
            Random rng = new(4801);
            int relocations = 0, nearStart = 0;
            HashSet<int> landedTypes = [];
            for (int board = 0; board < 8; board++)
            {
                shift = TimeSpan.FromDays(7 * (weeks + 3 + board));
                MaintainerActionModule.EnsureResets(session);
                ReadPushes(1);
                JObject segLogin = Wire(Login());
                JArray steps = [];
                JObject Expect() => new()
                {
                    ["Node"] = State().PlayerNodeId, ["Triggered"] = State().Triggered, ["Reverse"] = State().Reverse,
                    ["Cards"] = new JArray(State().Cards), ["Used"] = State().UsedActionCount, ["Extra"] = State().ExtraActionCount,
                    ["Fight"] = State().FightWinCount, ["Box"] = State().BoxCount, ["Warehouse"] = State().WarehouseFinishCount
                };
                JObject Response(string name, object body) => new() { ["Name"] = name, ["Response"] = Wire(body) };
                // force coverage: also place the player right after the start so back-relocations are exercised there
                for (int landing = 0; landing < 45; landing++)
                {
                    if (State().UsedActionCount >= cfg.MaxDailyActionCount + State().ExtraActionCount)
                    {
                        State().UsedActionCount = 0; State().ExtraActionCount = 0;
                        steps.Add(new JObject { ["Kind"] = "daily", ["Push"] = Wire(new NotifyMaintainerActionDailyReset()), ["Expect"] = Expect() });
                    }
                    if (!State().Triggered) throw new InvalidDataException("walker must start each landing resolved");
                    int card = State().Cards[rng.Next(State().Cards.Count)];
                    var play = Call<MaintainerActionPlayCardResponse>(nameof(MaintainerActionPlayCardRequest), new MaintainerActionPlayCardRequest { Card = card }, nameof(MaintainerActionPlayCardResponse));
                    AssertEqual(0, play.Response.Code, "walker card");
                    JArray responses = [Response(nameof(MaintainerActionPlayCardRequest), play.Response)];
                    ((JObject)responses[0]).Add("Card", card);
                    for (int chain = 0; chain < 4 && !State().Triggered; chain++)
                    {
                        var ev = NodeEvent();
                        landedTypes.Add(ev.Response.Node?.NodeType ?? -1);
                        responses.Add(Response(nameof(MaintainerActionNodeEventRequest), ev.Response));
                        if (ev.Response.Node?.NodeType is 5 or 6) relocations++;
                        if (ev.Response.Node?.NodeType == 6 && State().PlayerNodeId <= 1) nearStart++;
                        if (State().Nodes[State().PlayerNodeId].NodeType == MaintainerActionModule.TypeFight && !State().Triggered
                            && ev.Response.Node?.NodeType == MaintainerActionModule.TypeFight) break;
                    }
                    JArray flowResponses = responses;
                    steps.Add(new JObject { ["Kind"] = "card", ["Card"] = card, ["Responses"] = flowResponses, ["Expect"] = Expect() });
                    if (!State().Triggered)
                    {
                        // unresolved fight node: win it so the walk continues (settlement itself is covered above)
                        State().FightWinCount = Math.Min(State().FightWinCount + 1, cfg.MaxFightWinCount);
                        State().Triggered = true;
                        steps.Add(new JObject { ["Kind"] = "fightwin", ["Push"] = Wire(new NotifyMaintainerActionFightWin { FightWinCount = State().FightWinCount }), ["Expect"] = Expect() });
                    }
                }
                flow.Add(new JObject { ["Login"] = segLogin, ["Steps"] = steps });
            }
            AssertEqual(true, landedTypes.Contains(MaintainerActionModule.TypeWarehouse) && landedTypes.Contains(MaintainerActionModule.TypeBox), "flow walk landed on the warehouse and box nodes");
            AssertEqual(true, nearStart > 0, "flow walk exercised a back-relocation near the start");
            AssertEqual(true, relocations > 4, "flow walk exercised relocations");
            AssertEqual(true, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }.Count(landedTypes.Contains) >= 9, "flow walk landed on most node types");

            // ---- original EN Lua consumers ----
            Reveal();
            Stand(0); State().Triggered = true;
            JObject loginWire = Wire(Login());
            MaintainerActionOriginalLua(loginWire, dailyPush, fightWinWire,
                Wire(new NotifyMaintainerActionNodeChange { Node = new() { NodeId = 3, NodeType = 4, EventId = 0 } }),
                nodeEventNodes, moves, (uint)uid, recordsJson, flow);
            Console.WriteLine("Maintainer Action compatibility: table-selected board, card routes, every node event, limits, fight settle + push, daily/weekly reset, BSON reload, retries, original EN Lua consumers passed.");
        }
        finally
        {
            MaintainerActionModule.Clock = savedClock;
        }
    }
}
