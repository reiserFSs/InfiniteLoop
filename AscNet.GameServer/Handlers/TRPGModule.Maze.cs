using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.trpg;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGMazePos
    {
        public int LayerId { get; set; }
        public int NodeId { get; set; }
        public int Index { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGMazeLayerInfo
    {
        public int Id { get; set; }
        public List<int> FinishCardId { get; set; } = new();
    }

    /// <summary>XTRPGMaze.UpdateData: cards finished for good per layer plus the record-group cards behind the progress bar.</summary>
    [MessagePackObject(true)]
    public class TRPGMazeInfo
    {
        public int Id { get; set; }
        public List<TRPGMazeLayerInfo> LayerInfos { get; set; } = new();
        public List<int> RecordCardIds { get; set; } = new();
    }

    /// <summary>XTRPGMazeCard.OnResult input. Success=false keeps the card pending (fight/examine) or, with ReplaceId,
    /// reveals what a random card turned into.</summary>
    [MessagePackObject(true)]
    public class TRPGMazeCardResult
    {
        public bool Success { get; set; }
        public int ReplaceId { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
        public TRPGMazePos CurPos { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGEnterMazeRequest
    {
        public int MazeId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGEnterMazeResponse
    {
        public int Code { get; set; }
        public TRPGMazePos CurrentPos { get; set; } = new();
        public TRPGMazeInfo MazeInfo { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGMazeSelectCardRequest
    {
        public TRPGMazePos SelectPos { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGMazeSelectCardResponse
    {
        public int Code { get; set; }
        public TRPGMazeCardResult Result { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGMazeGiveUpChallengeRequest
    {
        public TRPGMazePos Pos { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGMazeGiveUpChallengeResponse
    {
        public int Code { get; set; }
        public TRPGMazeCardResult Result { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGMazeQuickChallengeRequest
    {
        public TRPGMazePos Pos { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGMazeQuickChallengeResponse
    {
        public int Code { get; set; }
        public TRPGMazeCardResult Result { get; set; } = new();
    }

    /// <summary>XTRPGManager.NotifyTRPGMazeSelectCardResult: the late result of a fight/examine card.</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGMazeSelectCardResult
    {
        public TRPGMazeCardResult Result { get; set; }
    }

    /// <summary>XTRPGManager.NotifyMazeRecordCardId: one more record-group card finished.</summary>
    [MessagePackObject(true)]
    public class NotifyMazeRecordCardId
    {
        public int Id { get; set; }
        public int RecordCardId { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class TRPGModule
    {
        private const int FuncNotOpen = 20104002, FuncGroupPreFuncNotFinish = 20104010, ConditionPlayerLevel = 10101;

        /// <summary>Client condition for areas/function groups: finished target, passed stage or player level; 0 = none.</summary>
        private static bool AreaConditionPassed(Session session, int conditionId)
        {
            if (conditionId == 0) return true;
            ConditionTable? row = TableReaderV2.Parse<ConditionTable>().Find(x => x.Id == conditionId);
            if (row is { Type: ConditionPlayerLevel } && row.Params.Count > 0) return session.player.PlayerData.Level >= row.Params[0];
            return ConditionSatisfied(session, conditionId);
        }

        /// <summary>A maze opens with its TRPGSecondArea condition (XTRPGManager.CheckOpenNewMazeTips reads the same one).</summary>
        internal static bool MazeAreaOpen(Session session, int mazeId) =>
            TableReaderV2.Parse<TRPGSecondAreaTable>().Where(x => x.MazeId == mazeId).Any(x => AreaConditionPassed(session, x.Condition));

        /// <summary>Order rules of TRPGFunctionFinish from the TRPGFunctionGroup/TRPGThirdArea tables: the function's group and
        /// third area must be open (20104002) and every earlier saved function of the group finished (20104010).
        /// AscNet rule: the request carries no group id, so a function that no group lists (e.g. 110011) is unconstrained
        /// and 20104009 (not in group) is never raised.</summary>
        internal static int CheckFunctionOrder(Session session, TRPGFunctionTable function)
        {
            List<TRPGFunctionGroupTable> groups = TableReaderV2.Parse<TRPGFunctionGroupTable>().Where(x => x.FunctionId.Contains(function.Id)).ToList();
            if (groups.Count == 0) return 0;
            List<TRPGThirdAreaTable> areas = TableReaderV2.Parse<TRPGThirdAreaTable>();
            List<TRPGFunctionGroupTable> open = groups.Where(group => AreaConditionPassed(session, group.Condition)
                && (areas.All(area => !area.FuncGroup.Contains(group.Id))
                    || areas.Any(area => area.FuncGroup.Contains(group.Id) && AreaConditionPassed(session, area.Condition)))).ToList();
            if (open.Count == 0) return FuncNotOpen;
            Dictionary<int, TRPGFunctionTable> functions = TableReaderV2.Parse<TRPGFunctionTable>().ToDictionary(x => x.Id);
            bool ordered = open.Any(group => group.FunctionId.TakeWhile(id => id != function.Id)
                .All(id => !functions.TryGetValue(id, out TRPGFunctionTable? earlier) || earlier.NeedSave != 1 || session.player.Trpg.Functions.Contains(id)));
            return ordered ? 0 : FuncGroupPreFuncNotFinish;
        }

        /// <summary>NotifyTRPGData.MazeInfos: the mazes that already have finished cards.</summary>
        internal static List<dynamic> TrpgMazeLogin(Session session) =>
            TrpgMaze.Mazes.Keys.Where(id => TrpgMaze.HasProgress(session.player, id))
                .Select(id => (dynamic)TrpgMaze.BuildInfo(session.player, id)).ToList();

        [RequestPacketHandler("TRPGEnterMazeRequest")]
        public static void TRPGEnterMazeRequestHandler(Session session, Packet.Request packet)
        {
            TRPGEnterMazeRequest request = packet.Deserialize<TRPGEnterMazeRequest>();
            TRPGEnterMazeResponse response = new();
            if (!TrpgMaze.Mazes.ContainsKey(request.MazeId)) response.Code = TrpgMaze.MazeIdNotExist;
            else if (!MazeAreaOpen(session, request.MazeId)) response.Code = ConditionNotPass;
            else
            {
                response.CurrentPos = TrpgMaze.Enter(session.player, request.MazeId);
                response.MazeInfo = TrpgMaze.BuildInfo(session.player, request.MazeId);
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGMazeSelectCardRequest")]
        public static void TRPGMazeSelectCardRequestHandler(Session session, Packet.Request packet)
        {
            TRPGMazeSelectCardRequest request = packet.Deserialize<TRPGMazeSelectCardRequest>();
            (int code, TRPGMazeCardResult? result) = TrpgMaze.Select(session, request.SelectPos);
            session.SendResponse(new TRPGMazeSelectCardResponse { Code = code, Result = result ?? new() }, packet.Id);
        }

        [RequestPacketHandler("TRPGMazeGiveUpChallengeRequest")]
        public static void TRPGMazeGiveUpChallengeRequestHandler(Session session, Packet.Request packet)
        {
            TRPGMazeGiveUpChallengeRequest request = packet.Deserialize<TRPGMazeGiveUpChallengeRequest>();
            (int code, TRPGMazeCardResult? result) = TrpgMaze.Resolve(session, request.Pos, quick: false);
            session.SendResponse(new TRPGMazeGiveUpChallengeResponse { Code = code, Result = result ?? new() }, packet.Id);
        }

        [RequestPacketHandler("TRPGMazeQuickChallengeRequest")]
        public static void TRPGMazeQuickChallengeRequestHandler(Session session, Packet.Request packet)
        {
            TRPGMazeQuickChallengeRequest request = packet.Deserialize<TRPGMazeQuickChallengeRequest>();
            (int code, TRPGMazeCardResult? result) = TrpgMaze.Resolve(session, request.Pos, quick: true);
            session.SendResponse(new TRPGMazeQuickChallengeResponse { Code = code, Result = result ?? new() }, packet.Id);
        }
    }

    /// <summary>Server side of XTRPGMaze/XTRPGMazeCard. The client moves itself (MoveNext/MoveTo) after a successful card
    /// result, so the run mirrors that arithmetic exactly and uses it to validate every selection.</summary>
    internal static class TrpgMaze
    {
        // CodeText 20104xxx (Trpg*).
        internal const int MazeIdNotExist = 20104021, MazeInstNotExist = 20104022, LayerNotInCurrentMaze = 20104023,
            LayerCfgNotExist = 20104024, SelectLayerIsError = 20104026, SelectNodeIsError = 20104027,
            NodeNotExist = 20104028, IndexNotExist = 20104029, CardCfgNotExist = 20104030, IndexIsCross = 20104031,
            CardCanNotGiveUp = 20104032, LevelNotEnough = 20104033, BlockCardCanNotSelect = 20104034,
            ExamineCardMissing = 20104071, ExamineIdNotMatch = 20104072;

        // XTRPGConfigs.CardType
        private const int Block = 1, Pass = 2, FightWin = 3, Story = 4, Examine = 5, Reward = 6, Random = 7, Skip = 8, Fight = 9, Over = 10;
        // Card param slots: Fight = [challengeLevel, stageId, quickRewardId, ...], FightWin = [stageId], Examine = [examineId],
        // Reward = [rewardId], Skip = [layerId, nodeId, cardIndex], Random = [cardId, weight, cardId, weight, ...].
        private const int FightBuffType = 7, RandomBuffType = 6;

        // XTRPGMazeNode geometry: 9 cards per node, 17 display positions, selectable window = pivot +-1.
        private const int MidPos = 9;

        private sealed record Layer(int Id, int StartNode, int StartCard, int[] NodeIds, Dictionary<int, int[]> Cards);
        private sealed record Spot(TrpgMazeRun Run, Layer Layer, int Node, int Index, TRPGMazeCardTable Card);
        private enum Mode { PassThrough, Clear, GiveUp }

        /// <summary>Random-event source; tests replace it to pin an outcome.</summary>
        internal static Func<int, int> Roll = max => System.Random.Shared.Next(max);

        private static readonly Lazy<Dictionary<int, TRPGMazeTable>> MazeTable = new(() => TableReaderV2.Parse<TRPGMazeTable>().ToDictionary(x => x.Id));
        private static readonly Lazy<Dictionary<int, TRPGMazeCardTable>> CardTable = new(() => TableReaderV2.Parse<TRPGMazeCardTable>().ToDictionary(x => x.Id));
        internal static Dictionary<int, TRPGMazeTable> Mazes => MazeTable.Value;
        private static Dictionary<int, TRPGMazeCardTable> Cards => CardTable.Value;

        private static readonly Lazy<Dictionary<int, Layer>> Layers = new(() =>
        {
            ILookup<int, TRPGMazeMapTable> maps = TableReaderV2.Parse<TRPGMazeMapTable>().ToLookup(x => x.GroupId);
            return TableReaderV2.Parse<TRPGMazeLayerTable>().ToDictionary(x => x.Id, x =>
            {
                // The client builds its node list from the map ids ascending; StartNodeId is an index into it.
                Dictionary<int, int[]> cards = maps[x.MapId].OrderBy(node => node.Id).ToDictionary(node => node.Id, node => node.CardId.Where(id => id > 0).ToArray());
                return new Layer(x.Id, x.StartNodeId, x.StartCardIndex, cards.Keys.ToArray(), cards);
            });
        });

        private static int P(TRPGMazeCardTable card, int slot) => int.Parse(card.Param[slot]);

        private static int Next(int index, int count) => index + 1 > count ? 1 : index + 1;

        private static int NodeAt(Layer layer, int index) => layer.NodeIds[index - 1];

        private static bool Reachable(int startPos, int index)
        {
            int pos = startPos + index - 1;
            return pos >= MidPos - 1 && pos <= MidPos + 1;
        }

        /// <summary>XTRPGMazeLayer.Enter: stand before the start node and centre every node on StartCardIndex.</summary>
        private static void EnterLayer(TrpgMazeRun run, Layer layer)
        {
            run.NodeIndex = Next(layer.StartNode, layer.NodeIds.Length);
            foreach (int node in layer.NodeIds)
            {
                int count = layer.Cards[node].Length;
                run.StartPos[(layer.Id, node)] = MidPos - count / 2 + ((count + 1) / 2 - layer.StartCard);
            }
        }

        internal static TRPGMazePos Enter(Player player, int mazeId)
        {
            TRPGMazeTable maze = Mazes[mazeId];
            Layer layer = Layers.Value[maze.StartLayerId];
            TrpgMazeRun run = new() { MazeId = mazeId, LayerId = layer.Id };
            EnterLayer(run, layer);
            player.TrpgMazeRun = run;
            return new() { LayerId = layer.Id, NodeId = NodeAt(layer, Math.Min(layer.StartNode, layer.NodeIds.Length)), Index = layer.StartCard };
        }

        /// <summary>The client restarts the current layer locally after a go-to-origin examine punishment; mirror it.</summary>
        internal static void RestartCurrentLayer(Session session)
        {
            if (session.player.TrpgMazeRun is { } run) EnterLayer(run, Layers.Value[run.LayerId]);
        }

        private static void MoveNext(TrpgMazeRun run, Layer layer, int index)
        {
            foreach (int node in layer.NodeIds)
            {
                int start = run.StartPos.GetValueOrDefault((layer.Id, node));
                if (Reachable(start, index)) run.StartPos[(layer.Id, node)] = start - (start + index - 1 - MidPos);
            }
            run.NodeIndex = Next(run.NodeIndex, layer.NodeIds.Length);
        }

        private static void MoveTo(TrpgMazeRun run, int layerId, int nodeId, int index)
        {
            Layer layer = Layers.Value[layerId];
            run.LayerId = layerId;
            foreach (int node in layer.NodeIds)
            {
                int start = run.StartPos.GetValueOrDefault((layerId, node));
                run.StartPos[(layerId, node)] = start - (start + index - 1 - MidPos);
            }
            run.NodeIndex = Next(Array.IndexOf(layer.NodeIds, nodeId) + 1, layer.NodeIds.Length);
        }

        private static IEnumerable<int> MazeCards(int mazeId) =>
            Mazes[mazeId].LayerId.Where(id => id > 0).SelectMany(id => Layers.Value[id].Cards.Values.SelectMany(x => x));

        internal static bool HasProgress(Player player, int mazeId) =>
            MazeCards(mazeId).Any(player.TrpgMaze.FinishedCards.Contains);

        internal static TRPGMazeInfo BuildInfo(Player player, int mazeId)
        {
            HashSet<int> finished = player.TrpgMaze.FinishedCards.ToHashSet();
            Dictionary<int, TRPGMazeCardTable> cards = Cards;
            return new()
            {
                Id = mazeId,
                LayerInfos = Mazes[mazeId].LayerId.Where(id => id > 0).Select(id => new TRPGMazeLayerInfo
                {
                    Id = id,
                    FinishCardId = Layers.Value[id].Cards.Values.SelectMany(x => x).Distinct().Where(finished.Contains).ToList()
                }).ToList(),
                RecordCardIds = MazeCards(mazeId).Distinct().Where(id => finished.Contains(id) && cards[id].RecordGroupId > 0).ToList()
            };
        }

        private static (int Code, Spot? Spot) Locate(Player player, TRPGMazePos? pos)
        {
            if (player.TrpgMazeRun is not { } run) return (MazeInstNotExist, null);
            if (pos is null) return (SelectLayerIsError, null);
            if (!Mazes[run.MazeId].LayerId.Contains(pos.LayerId)) return (LayerNotInCurrentMaze, null);
            if (!Layers.Value.TryGetValue(pos.LayerId, out Layer? layer)) return (LayerCfgNotExist, null);
            if (pos.LayerId != run.LayerId) return (SelectLayerIsError, null);
            if (!layer.Cards.TryGetValue(pos.NodeId, out int[]? nodeCards)) return (NodeNotExist, null);
            if (pos.NodeId != NodeAt(layer, run.NodeIndex)) return (SelectNodeIsError, null);
            if (pos.Index < 1 || pos.Index > nodeCards.Length) return (IndexNotExist, null);
            if (!Reachable(run.StartPos.GetValueOrDefault((layer.Id, pos.NodeId)), pos.Index)) return (IndexIsCross, null);
            if (!Cards.TryGetValue(nodeCards[pos.Index - 1], out TRPGMazeCardTable? card)) return (CardCfgNotExist, null);
            return (0, new Spot(run, layer, pos.NodeId, pos.Index, card));
        }

        private static bool IsFinished(Player player, TrpgMazeRun run, TRPGMazeCardTable card) =>
            run.SingleDone.Contains(card.Id) || (card.Disposeable == 1 && player.TrpgMaze.FinishedCards.Contains(card.Id));

        private static void SetPending(Spot spot, TRPGMazeCardTable effective)
        {
            spot.Run.PendingCard = effective.Id;
            spot.Run.PendingLayer = spot.Layer.Id;
            spot.Run.PendingNode = spot.Node;
            spot.Run.PendingIndex = spot.Index;
        }

        /// <summary>Weighted pick among the random card's outcomes. AscNet rule (the tables publish only the weights): the
        /// roll is proportional to them, and an active type-6 talent buff [cardType, extraWeight] adds to the outcomes of that card type.</summary>
        private static TRPGMazeCardTable RollOutcome(Session session, TRPGMazeCardTable card)
        {
            Dictionary<int, TRPGMazeCardTable> cards = Cards;
            List<AscNet.Table.V2.share.trpg.TRPGBuffTable> buffs = TrpgTalents.ActiveBuffs(session).Where(x => x.Type == RandomBuffType && x.Params.Count >= 2).ToList();
            List<(TRPGMazeCardTable Card, int Weight)> outcomes = new();
            for (int slot = 0; slot + 1 < card.Param.Count; slot += 2)
            {
                TRPGMazeCardTable outcome = cards[P(card, slot)];
                outcomes.Add((outcome, P(card, slot + 1) + buffs.Where(x => x.Params[0] == outcome.Type).Sum(x => x.Params[1])));
            }
            int pick = Roll(outcomes.Sum(x => x.Weight));
            foreach ((TRPGMazeCardTable outcome, int weight) in outcomes)
            {
                if (pick < weight) return outcome;
                pick -= weight;
            }
            return outcomes[^1].Card;
        }

        /// <summary>Reward ids resolve through the ordinary reward table; TRPG items go through TrpgItems.Grant for the carry limit.</summary>
        private static List<RewardGoods> GrantReward(Session session, int rewardId)
        {
            List<RewardGoods> shown = new();
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> plain = new();
            foreach (AscNet.Table.V2.share.reward.RewardGoodsTable goods in RewardHandler.GetRewardGoods(rewardId))
            {
                if (TrpgItems.Config(goods.TemplateId) is null) { plain.Add(goods); continue; }
                TrpgItems.Grant(session, goods.TemplateId, goods.Count);
                shown.Add(new RewardGoods { RewardType = (int)RewardType.Item, TemplateId = goods.TemplateId, Count = goods.Count });
            }
            if (plain.Count > 0)
            {
                RewardApplicationResult applied = RewardHandler.ApplyRewards(plain, session);
                session.inventory.SaveChecked();
                session.character.SaveChecked();
                session.player.SaveChecked();
                applied.SendPushes(session);
                shown.AddRange(applied.RewardGoods);
            }
            return shown;
        }

        internal static (int Code, TRPGMazeCardResult? Result) Select(Session session, TRPGMazePos? pos)
        {
            (int code, Spot? spot) = Locate(session.player, pos);
            if (spot is null) return (code, null);
            TrpgMazeRun run = spot.Run;
            TRPGMazeCardTable card = spot.Card;
            if (card.Type == Block) return (BlockCardCanNotSelect, null);
            if (IsFinished(session.player, run, card)) return (0, Finish(session, spot, card, 0, Mode.PassThrough));

            TRPGMazeCardTable effective = card;
            int replace = 0;
            if (card.Type == Random)
            {
                // AscNet rule: closing the fight tips and selecting the same card again keeps the rolled outcome.
                bool again = run.PendingCard != 0 && run.PendingLayer == spot.Layer.Id && run.PendingNode == spot.Node && run.PendingIndex == spot.Index;
                effective = again ? Cards[run.PendingCard] : RollOutcome(session, card);
                replace = effective.Id;
            }
            run.PendingCard = 0;
            switch (effective.Type)
            {
                case Fight:
                case FightWin:
                case Examine:
                    SetPending(spot, effective);
                    return (0, new() { Success = false, ReplaceId = replace, CurPos = PosOf(spot) });
                case Block:
                case Random:
                    return (0, new() { Success = false, ReplaceId = replace, CurPos = PosOf(spot) });
                default:
                    return (0, Finish(session, spot, effective, replace, Mode.Clear));
            }
        }

        /// <summary>Quick challenge (needs the exploration level of the card) or give-up of the pending fight card.</summary>
        internal static (int Code, TRPGMazeCardResult? Result) Resolve(Session session, TRPGMazePos? pos, bool quick)
        {
            (int code, Spot? spot) = Locate(session.player, pos);
            if (spot is null) return (code, null);
            TrpgMazeRun run = spot.Run;
            if (run.PendingCard == 0 || run.PendingLayer != spot.Layer.Id || run.PendingNode != spot.Node || run.PendingIndex != spot.Index
                || Cards[run.PendingCard] is not { Type: Fight } fight)
                return (CardCanNotGiveUp, null);
            if (!quick) return (0, Finish(session, spot, fight, spot.Card.Type == Random ? fight.Id : 0, Mode.GiveUp));
            if (TrpgExplore.BaseInfo(session).Level < P(fight, 0)) return (LevelNotEnough, null);
            return (0, Finish(session, spot, fight, spot.Card.Type == Random ? fight.Id : 0, Mode.Clear, P(fight, 2)));
        }

        private static TRPGMazePos PosOf(Spot spot) => new() { LayerId = spot.Layer.Id, NodeId = spot.Node, Index = spot.Index };

        /// <summary>Completes the selected board card: effects, durable progress, targets, then the client-mirrored move.</summary>
        private static TRPGMazeCardResult Finish(Session session, Spot spot, TRPGMazeCardTable effective, int replace, Mode mode, int rewardId = 0)
        {
            TrpgMazeRun run = spot.Run;
            TRPGMazeCardTable board = spot.Card;
            TRPGMazeCardResult result = new() { Success = true, ReplaceId = replace, CurPos = PosOf(spot) };
            run.PendingCard = 0;
            if (mode != Mode.PassThrough)
            {
                if (mode == Mode.Clear)
                {
                    if (effective.Type == Reward) rewardId = P(effective, 0);
                    if (rewardId > 0) result.RewardList.AddRange(GrantReward(session, rewardId));
                    // AscNet reading of TRPGBuff (the table only has numbers): type 7 [9, rewardId] = bonus reward for a cleared battle event.
                    if (effective.Type == Fight)
                        foreach (var buff in TrpgTalents.ActiveBuffs(session).Where(x => x.Type == FightBuffType && x.Params.Count >= 2 && x.Params[0] == Fight))
                            result.RewardList.AddRange(GrantReward(session, buff.Params[1]));
                }
                if (board.SingleDisposeable == 1) run.SingleDone.Add(board.Id);
                bool record = false;
                if (board.Disposeable == 1 && !session.player.TrpgMaze.FinishedCards.Contains(board.Id))
                {
                    session.player.TrpgMaze.FinishedCards.Add(board.Id);
                    session.player.SaveChecked();
                    record = board.RecordGroupId > 0;
                }
                if (mode == Mode.Clear)
                {
                    // Story targets (TRPGTarget.Type 1) name the board card that completes them.
                    List<int> targets = TableReaderV2.Parse<TRPGTargetTable>().Where(x => x.Type == 1 && x.Params == board.Id).Select(x => x.Id).ToList();
                    if (targets.Count > 0) TRPGModule.FinishTargets(session, targets);
                }
                if (record) session.SendPush(new NotifyMazeRecordCardId { Id = run.MazeId, RecordCardId = board.Id });
            }

            if (mode == Mode.Clear && effective.Type == Skip)
                MoveTo(run, P(effective, 0), P(effective, 1), P(effective, 2));
            else if (mode == Mode.Clear && effective.Type == Over)
                session.player.TrpgMazeRun = null;
            else
                MoveNext(run, spot.Layer, spot.Index);
            return result;
        }

        /// <summary>FightSettle hook: a won stage completes the pending Fight/FightWin card that names it.</summary>
        internal static void OnStageWon(Session session, int stageId)
        {
            if (session.player.TrpgMazeRun is not { PendingCard: not 0 } run) return;
            TRPGMazeCardTable effective = Cards[run.PendingCard];
            int stage = effective.Type switch { FightWin => P(effective, 0), Fight => P(effective, 1), _ => -1 };
            if (stage != stageId) return;
            CompletePending(session, run, effective);
        }

        /// <summary>The pending examine card is done (success) when its examine succeeds; failure leaves it pending.</summary>
        internal static void OnExamineFinished(Session session, int examineId, bool success)
        {
            if (!success || session.player.TrpgMazeRun is not { PendingCard: not 0 } run) return;
            TRPGMazeCardTable effective = Cards[run.PendingCard];
            if (effective.Type != Examine || P(effective, 0) != examineId) return;
            CompletePending(session, run, effective);
        }

        private static void CompletePending(Session session, TrpgMazeRun run, TRPGMazeCardTable effective)
        {
            (int code, Spot? spot) = Locate(session.player, new() { LayerId = run.PendingLayer, NodeId = run.PendingNode, Index = run.PendingIndex });
            if (spot is null) throw new InvalidOperationException($"Pending maze card {effective.Id} no longer matches the run position (code {code}).");
            int replace = spot.Card.Type == Random ? effective.Id : 0;
            session.SendPush(new NotifyTRPGMazeSelectCardResult { Result = Finish(session, spot, effective, replace, Mode.Clear) });
        }

        /// <summary>0, or the code a TRPGExamine* request gets when the selected maze card is not this examine.</summary>
        internal static int CheckExamineCard(Session session, int examineId)
        {
            if (session.player.TrpgMazeRun is not { PendingCard: not 0 } run || Cards[run.PendingCard].Type != Examine) return ExamineCardMissing;
            return P(Cards[run.PendingCard], 0) == examineId ? 0 : ExamineIdNotMatch;
        }
    }
}
