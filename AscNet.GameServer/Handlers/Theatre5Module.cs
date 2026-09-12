using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Driver;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    internal static T Clone<T>(T value) =>
        BsonSerializer.Deserialize<CloneContainer<T>>(new CloneContainer<T> { Value = value }.ToBson()).Value;

    // A BSON envelope preserves ObjectIds and supports non-document roots.
    private sealed class CloneContainer<T>
    {
        public T Value { get; set; } = default!;
        static CloneContainer()
        {
            BsonClassMap.RegisterClassMap<CloneContainer<T>>(map =>
            {
                map.AutoMap();
                BsonMemberMap member = map.GetMemberMap(nameof(Value));
                if (member.GetSerializer() is IDictionaryRepresentationConfigurable dictionary)
                    member.SetSerializer(dictionary.WithDictionaryRepresentation(DictionaryRepresentation.ArrayOfDocuments));
            });
        }
    }

    internal static void Require([DoesNotReturnIf(false)] bool condition, int code)
    {
        if (!condition) throw new ServerCodeException("Theatre5 request rejected.", code);
    }

    internal sealed class Mutation
    {
        public Session Session { get; }
        public PlayerTheatre5State State { get; }
        public Theatre5DataDb Data => State.Data;
        internal List<Theatre5PendingPacket> Pushes { get; } = [];
        internal List<Theatre5PendingRewardGrant> Grants { get; } = [];
        internal List<int> ClaimedTaskIds { get; } = [];
        internal Dictionary<long, int> ShopBuyTimes { get; } = [];
        internal Dictionary<int, int> ConditionCounters { get; } = [];
        private int grantOrdinal;

        internal Mutation(Session session, bool newOperation = true)
        {
            Session = session;
            State = Clone(session.player.Theatre5);
            State.PendingMutation = null;
            if (newOperation) State.NextMutationId = checked(State.NextMutationId + 1);
        }

        public string NextClaimKey() => $"theatre5:{Session.player.PlayerData.Id}:{State.Epoch}:{State.RunId}:{State.NextMutationId}:{grantOrdinal++}";

        public long Balance(int itemId) => checked(Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Grants.Where(grant => !Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .Sum(grant => grant.Goods.Where(goods => (goods.TemplateId > 0 ? goods.TemplateId : goods.Id) == itemId)
                    .Sum(goods => (long)goods.Count) - grant.Costs.GetValueOrDefault(itemId)));

        public void Cost(int itemId, int count, int insufficientCode = 20012004)
        {
            // Global inventory currency uses the shared ItemCountNotEnough code.
            Require(Inventory.IsValidClientItemId(itemId) && count >= 0, 1);
            Require(Balance(itemId) >= count, insufficientCode);
            // The shared reward engine applies grants in order. Keep prior grants intact so
            // their durable claim keys survive even when this operation spends all the goods.
            if (count > 0)
                Grant(new RewardGrant(NextClaimKey(), [], new Dictionary<int, int> { [itemId] = count }));
        }

        public bool IsTaskClaimed(int id) => State.ClaimedTaskIds.Contains(id)
            || ClaimedTaskIds.Contains(id) || Session.player.MissionProgress.ClaimedTaskIds.Contains(id);
        public void MarkTaskClaimed(int id)
        {
            if (IsTaskClaimed(id)) return;
            State.ClaimedTaskIds.Add(id);
            ClaimedTaskIds.Add(id);
        }

        public int GetShopBuyTimes(uint goodsId) =>
            ShopBuyTimes.GetValueOrDefault(goodsId, Session.player.ShopBuyTimes.GetValueOrDefault(goodsId));

        public void RecordShopPurchase(uint goodsId, int count)
        {
            Require(goodsId > 0 && count > 0, 1);
            ShopBuyTimes[goodsId] = checked(GetShopBuyTimes(goodsId) + count);
        }

        public int GetTaskConditionProgress(int conditionId) => ConditionCounters.GetValueOrDefault(
            conditionId, Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId));

        public void AddTaskConditionProgress(int conditionId, int amount)
        {
            Require(conditionId > 0 && amount >= 0, 1);
            if (amount > 0) ConditionCounters[conditionId] = checked(GetTaskConditionProgress(conditionId) + amount);
        }

        public void Push<T>(T payload) where T : notnull => Pushes.Add(new()
        {
            Name = payload.GetType().Name,
            Payload = MessagePackSerializer.Serialize(payload.GetType(), payload)
        });

        public void Grant(int rewardId)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0) throw new InvalidDataException($"Missing Theatre5 reward group {rewardId}.");
            Grant(new RewardGrant(NextClaimKey(), goods));
        }

        public void Grant(IEnumerable<int> rewardIds)
        {
            foreach (int rewardId in rewardIds) Grant(rewardId);
        }

        public void Grant(RewardGrant grant)
        {
            if (string.IsNullOrWhiteSpace(grant.ClaimKey) || grant.ClaimKey.Length > 128
                || Grants.Any(existing => existing.ClaimKey == grant.ClaimKey)
                || (grant.Goods.Count == 0 && grant.Costs is not { Count: > 0 })
                || grant.Goods.Any(goods => goods.Count <= 0)
                || grant.Costs?.Any(cost => cost.Value <= 0 || !Inventory.IsValidClientItemId(cost.Key)) == true)
                throw new InvalidOperationException("Invalid Theatre5 reward grant.");
            Grants.Add(new()
            {
                ClaimKey = grant.ClaimKey,
                Goods = grant.Goods.Select(goods => new Theatre5PendingGoods
                {
                    Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                    // Params is optional table metadata, not a missing goods/reward fallback.
                    Params = goods.Params?.ToList() ?? []
                }).ToList(),
                Costs = grant.Costs?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [],
                EventCause = grant.EventCause
            });
        }
    }

    internal static bool CanDispatchPendingRequest(Session session, Packet.Request packet)
    {
        if (session.player.Theatre5.PendingMutation is not { } pending) return true;
        if (!pending.RequestKey.StartsWith(packet.Name + ":", StringComparison.Ordinal)) return false;
        try
        {
            // Shared routes must match before their dispatcher can choose a foreign owner.
            // Mode-only routes have one owner; Handle validates their typed body before mutation.
            return packet.Name switch
            {
                nameof(FinishTaskRequest) => Matches<FinishTaskRequest>(),
                nameof(FinishMultiTaskRequest) => Matches<FinishMultiTaskRequest>(),
                nameof(BuyRequest) => Matches<BuyRequest>(),
                nameof(DlcSingleEnterFightRequest) => Matches<DlcSingleEnterFightRequest>(),
                nameof(DlcSingleFightSettleRequest) => Matches<DlcSingleFightSettleRequest>(),
                _ => true
            };
        }
        catch (MessagePackSerializationException) { return false; }

        bool Matches<TRequest>() where TRequest : notnull, new()
        {
            TRequest request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                ? new() : packet.Deserialize<TRequest>();
            return pending.RequestKey == SemanticRequestKey(packet.Name, request);
        }
    }

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<Session>? onFailure = null)
        where TRequest : notnull, new() where TResponse : Theatre5Response, new() =>
        Handle(session, packet, action, static (response, code) => response.Code = code, onFailure);

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<TResponse, int> setCode,
        Action<Session>? onFailure = null, Action<Session>? afterCommit = null)
        where TRequest : notnull, new() where TResponse : new()
    {
        // Normalize omitted/null optional fields through the actual typed request contract.
        // Array order remains meaningful; unknown input keys do not become mutation identity.
        byte[] responseBytes;
        bool committed = false;
        try
        {
            TRequest request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                ? new() : packet.Deserialize<TRequest>();
            string key = SemanticRequestKey(packet.Name, request);
            if (session.player.Theatre5.PendingMutation is { } pending)
            {
                Require(pending.RequestKey == key && pending.ResponseName == typeof(TResponse).Name
                    && pending.Response != null, 1);
                Theatre5RequestReceipt pendingReceipt = pending.Outcome.RequestReceipts.Values.First(value =>
                    value.RequestKey == key && value.ResponseName == pending.ResponseName
                    && value.MutationId == pending.Outcome.NextMutationId);
                Require(pendingReceipt.Epoch == pending.Outcome.Epoch && pendingReceipt.RunId == pending.Outcome.RunId
                    && pendingReceipt.Mode == pending.Outcome.Data.PvpType
                    && pendingReceipt.ModeRunId == ActiveModeRunId(pending.Outcome), 1);
                Require(!pending.Outcome.RequestReceipts.TryGetValue(packet.Id, out Theatre5RequestReceipt? existing)
                    || (existing.Epoch == pendingReceipt.Epoch && existing.RunId == pendingReceipt.RunId
                        && existing.Mode == pendingReceipt.Mode && existing.ModeRunId == pendingReceipt.ModeRunId
                        && existing.MutationId == pendingReceipt.MutationId
                        && existing.RequestKey == key && existing.ResponseName == pending.ResponseName), 1);
                StoreReceipt(pending.Outcome, packet.Id, pendingReceipt);
                CompletePending(session).SendPushes(session);
                // The first attempt never passed its durable commit, so these pushes are owed,
                // unlike already committed receipt replays. Do not execute the action again.
                SendPushes(session, pending.Pushes);
                responseBytes = pending.Response!;
            }
            else if (session.player.Theatre5.RequestReceipts.TryGetValue(packet.Id, out Theatre5RequestReceipt? receipt))
            {
                Require(receipt.Epoch == session.player.Theatre5.Epoch && receipt.RunId == session.player.Theatre5.RunId
                    && receipt.Mode == session.player.Theatre5.Data.PvpType
                    && receipt.ModeRunId == ActiveModeRunId(session.player.Theatre5)
                    && receipt.RequestKey == key && receipt.ResponseName == typeof(TResponse).Name, 1);
                // XNetwork correlates this frozen response; stale mode pushes are not replayed.
                responseBytes = receipt.Response;
            }
            else
            {
                TResponse response = new();
                Mutation mutation = new(session);
                action(mutation, request, response);
                responseBytes = MessagePackSerializer.Serialize(response);
                StoreReceipt(mutation.State, packet.Id, new()
                {
                    Epoch = mutation.State.Epoch, RunId = mutation.State.RunId, MutationId = mutation.State.NextMutationId,
                    Mode = mutation.State.Data.PvpType, ModeRunId = ActiveModeRunId(mutation.State),
                    RequestKey = key, ResponseName = typeof(TResponse).Name, Response = responseBytes
                });
                Persist(mutation, key, typeof(TResponse).Name, responseBytes).SendPushes(session);
                SendPushes(session, mutation.Pushes);
            }
            committed = true;
        }
        catch (ServerCodeException exception) { responseBytes = Failure(exception.Code); }
        catch (MessagePackSerializationException) { responseBytes = Failure(1); }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre5 persistence failed: {exception.Message}");
            responseBytes = Failure(1);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            session.log.Error($"Theatre5 request could not be evaluated: {exception.GetType().Name}: {exception.Message}");
            responseBytes = Failure(1);
        }
        if (committed) afterCommit?.Invoke(session);
        session.SendResponse(typeof(TResponse).Name, responseBytes, packet.Id);

        byte[] Failure(int code)
        {
            onFailure?.Invoke(session);
            TResponse response = new();
            setCode(response, code);
            return MessagePackSerializer.Serialize(response);
        }
    }

    internal static string SemanticRequestKey<TRequest>(string name, TRequest request) where TRequest : notnull
    {
        byte[] body = MessagePackSerializer.Serialize(request);
        MessagePackReader reader = new(body.AsMemory());
        ArrayBufferWriter<byte> canonical = new(body.Length);
        MessagePackWriter writer = new(canonical);
        WriteCanonicalRequestValue(ref reader, ref writer);
        writer.Flush();
        return name + ":" + Convert.ToHexString(SHA256.HashData(canonical.WrittenSpan));
    }

    private static void WriteCanonicalRequestValue(ref MessagePackReader reader, ref MessagePackWriter writer)
    {
        MessagePackType type = reader.NextMessagePackType;
        if (type is not (MessagePackType.Map or MessagePackType.Array))
        {
            // Copy scalar encodings verbatim, including extensions and map-key types.
            writer.WriteRaw(reader.ReadRaw());
            return;
        }

        Packet.InboundOptions.Security.DepthStep(ref reader);
        try
        {
            if (type == MessagePackType.Array)
            {
                int count = reader.ReadArrayHeader();
                writer.WriteArrayHeader(count);
                for (int i = 0; i < count; i++)
                    WriteCanonicalRequestValue(ref reader, ref writer);
                return;
            }

            int entries = reader.ReadMapHeader();
            writer.WriteMapHeader(entries);
            if (entries == 0) return;

            // One scratch buffer per map, not one allocation per key/value.
            ArrayBufferWriter<byte> map = new();
            MessagePackWriter mapWriter = new(map);
            var offsets = new (int Start, int Value, int End)[entries];
            for (int i = 0; i < entries; i++)
            {
                int start = map.WrittenCount;
                WriteCanonicalRequestValue(ref reader, ref mapWriter);
                mapWriter.Flush();
                int value = map.WrittenCount;
                WriteCanonicalRequestValue(ref reader, ref mapWriter);
                mapWriter.Flush();
                offsets[i] = (start, value, map.WrittenCount);
            }
            Array.Sort(offsets, (left, right) =>
            {
                int order = map.WrittenSpan.Slice(left.Start, left.Value - left.Start)
                    .SequenceCompareTo(map.WrittenSpan.Slice(right.Start, right.Value - right.Start));
                // Canonically equal compound keys still have deterministic entry order.
                return order != 0 ? order : map.WrittenSpan.Slice(left.Value, left.End - left.Value)
                    .SequenceCompareTo(map.WrittenSpan.Slice(right.Value, right.End - right.Value));
            });
            foreach (var entry in offsets)
                writer.WriteRaw(map.WrittenSpan.Slice(entry.Start, entry.End - entry.Start));
        }
        finally
        {
            reader.Depth--;
        }
    }

    private static long ActiveModeRunId(PlayerTheatre5State state) => state.Data.PvpType switch
    {
        1 => state.PvpRunId,
        2 => state.PveRunId,
        _ => 0
    };

    private static void StoreReceipt(PlayerTheatre5State state, int packetId, Theatre5RequestReceipt receipt)
    {
        // ponytail: bounded retransmission window; inventory/character reward claims remain durable.
        while (!state.RequestReceipts.ContainsKey(packetId) && state.RequestReceipts.Count >= 256)
            state.RequestReceipts.Remove(state.RequestReceipts.MinBy(pair => pair.Value.MutationId).Key);
        state.RequestReceipts[packetId] = receipt;
    }

    internal static Mutation Commit(Session session, Action<Mutation> action) =>
        CommitCore(session, action, string.Empty);

    internal static Mutation Commit<TRequest>(Session session, Action<Mutation> action, string requestName, TRequest request,
        Action<PlayerTheatre5State>? onRecovery = null) where TRequest : notnull =>
        CommitCore(session, action, SemanticRequestKey(requestName, request), onRecovery);

    private static Mutation CommitCore(Session session, Action<Mutation> action, string requestKey,
        Action<PlayerTheatre5State>? onRecovery = null)
    {
        if (session.player.Theatre5.PendingMutation is { } pending)
        {
            Require(requestKey.Length > 0 && pending.RequestKey == requestKey && pending.ResponseName.Length == 0, 1);
            // Receipt metadata only: alias the incoming transport ID after semantic matching.
            // Never rerun gameplay; the alias is saved atomically with the prepared outcome.
            onRecovery?.Invoke(pending.Outcome);
            CompletePending(session).SendPushes(session);
            SendPushes(session, pending.Pushes);
            return new Mutation(session, newOperation: false);
        }
        Mutation mutation = new(session);
        action(mutation);
        Persist(mutation, requestKey, string.Empty, null).SendPushes(session);
        SendPushes(session, mutation.Pushes);
        return mutation;
    }

    internal static RewardApplicationResult Persist(Mutation mutation, string requestKey, string responseName, byte[]? response)
    {
        Session session = mutation.Session;
        PlayerTheatre5State previous = session.player.Theatre5;
        if (previous.PendingMutation is not null) throw new InvalidOperationException("Theatre5 recovery must finish before persistence.");
        previous.PendingMutation = new()
        {
            RequestKey = requestKey, Outcome = mutation.State, Grants = mutation.Grants,
            Pushes = mutation.Pushes, ResponseName = responseName, Response = response,
            ClaimedTaskIds = mutation.ClaimedTaskIds,
            ShopBuyTimes = mutation.ShopBuyTimes, ConditionCounters = mutation.ConditionCounters
        };
        try { session.player.SaveChecked(); }
        catch { previous.PendingMutation = null; throw; }
        return CompletePending(session);
    }

    private static RewardApplicationResult ApplyGrants(Session session, List<Theatre5PendingRewardGrant> grants) =>
        grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants.Select(grant => new RewardGrant(
            grant.ClaimKey, grant.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(), grant.Costs, grant.EventCause)).ToList(), session);

    private static RewardApplicationResult CompletePending(Session session)
    {
        PlayerTheatre5State previous = session.player.Theatre5;
        Theatre5PendingMutation pending = previous.PendingMutation
            ?? throw new InvalidOperationException("No pending Theatre5 operation.");
        RewardApplicationResult rewards = ApplyGrants(session, pending.Grants);
        List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
        session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(pending.ClaimedTaskIds).Distinct().ToList();
        Dictionary<uint, int> oldShopBuyTimes = session.player.ShopBuyTimes;
        if (pending.ShopBuyTimes.Count > 0)
        {
            session.player.ShopBuyTimes = new(oldShopBuyTimes);
            foreach ((long goodsId, int count) in pending.ShopBuyTimes)
            {
                uint id = checked((uint)goodsId);
                session.player.ShopBuyTimes[id] = Math.Max(session.player.ShopBuyTimes.GetValueOrDefault(id), count);
            }
        }
        Dictionary<int, int> oldCounters = session.player.MissionProgress.ConditionCounters;
        if (pending.ConditionCounters.Count > 0)
        {
            session.player.MissionProgress.ConditionCounters = new(oldCounters);
            foreach ((int id, int count) in pending.ConditionCounters)
                session.player.MissionProgress.ConditionCounters[id] = Math.Max(
                    session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id), count);
        }
        session.player.Theatre5 = pending.Outcome;
        session.player.Theatre5.PendingMutation = null;
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Theatre5 = previous;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims;
            session.player.ShopBuyTimes = oldShopBuyTimes;
            session.player.MissionProgress.ConditionCounters = oldCounters;
            throw;
        }
        return rewards;
    }

    internal static void ResumePending(Session session, bool forLogin = false)
    {
        session.player.Theatre5 ??= new();
        if (session.player.Theatre5.PendingMutation is null) return;
        // A live snapshot destroys callback-held adventure objects. Only fresh login may
        // complete an unrelated request and then publish a new full snapshot.
        Require(forLogin, 1);
        CompletePending(session);
    }

    internal static void ReplayPending(Session session)
    {
        if (session.player.Theatre5.PendingMutation is not { } pending) return;
        Require(pending.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse), 1);
        CompletePending(session).SendPushes(session);
        // Task-only recovery may refresh common task UI, never callback-held run objects.
        // Rebuild after recovery rather than sending a frozen pre-reset progress notification.
        TaskModule.SendTaskSync(session);
    }

    private static void SendPushes(Session session, IEnumerable<Theatre5PendingPacket> pushes)
    {
        foreach (Theatre5PendingPacket push in pushes)
            session.SendPush(push.Name, push.Payload);
    }
}
