using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) framework: durable cloned-state mutation, receipt caching,
// login recovery and duplicate-transport replay. Mirrors the Theatre5 prepare/apply/finalize
// contract without its PvP/PvE mode split; the run is identified by PlayerTheatre4State.RunId.
internal static partial class Theatre4Module
{
    internal static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();

    // Canonical clone and request normalization stay in Theatre5Module; a second copy would
    // silently diverge from the wire identity the receipt cache compares against.
    internal static T Clone<T>(T value) => Theatre5Module.Clone(value);

    internal static string SemanticRequestKey<TRequest>(string name, TRequest request) where TRequest : notnull =>
        Theatre5Module.SemanticRequestKey(name, request);

    internal static void Require([DoesNotReturnIf(false)] bool condition, int code)
    {
        if (!condition) throw new ServerCodeException("Theatre4 request rejected.", code);
    }

    internal sealed class Mutation
    {
        public Session Session { get; }
        public PlayerTheatre4State State { get; }
        public Theatre4ActivityData Data => State.Data;
        internal List<Theatre4PendingPacket> Pushes { get; } = [];
        internal List<Theatre4PendingRewardGrant> Grants { get; } = [];
        internal List<int> ClaimedTaskIds { get; } = [];
        internal Dictionary<int, int> ConditionCounters { get; } = [];
        private int grantOrdinal;

        internal Mutation(Session session, bool newOperation = true)
        {
            Session = session;
            State = Clone(session.player.Theatre4);
            State.PendingMutation = null;
            if (newOperation) State.NextMutationId = checked(State.NextMutationId + 1);
        }

        public string NextClaimKey() =>
            $"theatre4:{Session.player.PlayerData.Id}:{State.Epoch}:{State.RunId}:{State.NextMutationId}:{grantOrdinal++}";

        // Claims live in the shared MissionProgress ledger once finalized; the pending delta
        // keeps them visible to the same operation before the durable commit.
        public bool IsTaskClaimed(int id) => ClaimedTaskIds.Contains(id)
            || Session.player.MissionProgress.ClaimedTaskIds.Contains(id);

        public void MarkTaskClaimed(int id)
        {
            if (IsTaskClaimed(id)) return;
            ClaimedTaskIds.Add(id);
        }

        // Names match Theatre5Module so shared TaskModule progress plumbing stays symmetric.
        public int GetTaskConditionProgress(int conditionId) => ConditionCounters.TryGetValue(conditionId, out int value)
            ? value : Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId);

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

        // Account inventory spend (e.g. Theatre4TechTreeCoin 96200) with the same once-only
        // claim-key semantics as a grant, so a retried operation cannot double-spend.
        public long Balance(int itemId) => checked(Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Grants.Where(grant => !Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .Sum(grant => grant.Goods.Where(goods => (goods.TemplateId > 0 ? goods.TemplateId : goods.Id) == itemId)
                    .Sum(goods => (long)goods.Count) - grant.Costs.GetValueOrDefault(itemId)));

        public void Cost(int itemId, int count, int insufficientCode)
        {
            Require(Inventory.IsValidClientItemId(itemId) && count >= 0, 1);
            Require(Balance(itemId) >= count, insufficientCode);
            // Prior grants stay intact so their durable claim keys survive even when this
            // operation spends all the goods.
            if (count > 0)
                Grant(new RewardGrant(NextClaimKey(), [], new Dictionary<int, int> { [itemId] = count }));
        }

        public void Grant(int rewardId)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0) throw new InvalidDataException($"Missing Theatre4 reward group {rewardId}.");
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
                throw new InvalidOperationException("Invalid Theatre4 reward grant.");
            Grants.Add(new()
            {
                ClaimKey = grant.ClaimKey,
                Goods = grant.Goods.Select(goods => new Theatre4PendingGoods
                {
                    Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                    Params = goods.Params?.ToList() ?? []
                }).ToList(),
                Costs = grant.Costs?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [],
                EventCause = grant.EventCause
            });
        }
    }

    internal static bool CanDispatchPendingRequest(Session session, Packet.Request packet)
    {
        if (session.player.Theatre4.PendingMutation is not { } pending) return true;
        if (!pending.RequestKey.StartsWith(packet.Name + ":", StringComparison.Ordinal)) return false;
        try
        {
            // Shared routes must match before their dispatcher can choose a foreign owner.
            // Mode-only routes have one owner; Handle validates their typed body before mutation.
            return packet.Name switch
            {
                nameof(FinishTaskRequest) => Matches<FinishTaskRequest>(),
                nameof(FinishMultiTaskRequest) => Matches<FinishMultiTaskRequest>(),
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
        where TRequest : notnull, new() where TResponse : Theatre4Response, new() =>
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
            responseBytes = CommitResponse<TRequest, TResponse>(session, packet.Id, packet.Name, request,
                (Mutation mutation, TResponse response) => action(mutation, request, response), null, out _);
            committed = true;
        }
        catch (ServerCodeException exception) { responseBytes = Failure(exception.Code); }
        catch (MessagePackSerializationException) { responseBytes = Failure(1); }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            responseBytes = Failure(1);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            session.log.Error($"Theatre4 request could not be evaluated: {exception.GetType().Name}: {exception.Message}");
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

    private static void StoreReceipt(PlayerTheatre4State state, int packetId, Theatre4RequestReceipt receipt)
    {
        // ponytail: bounded retransmission window; inventory/character reward claims remain durable.
        while (!state.RequestReceipts.ContainsKey(packetId) && state.RequestReceipts.Count >= 256)
            state.RequestReceipts.Remove(state.RequestReceipts.MinBy(pair => pair.Value.MutationId).Key);
        state.RequestReceipts[packetId] = receipt;
    }

    internal static Theatre4AdventureData RequireAdventure(Mutation mutation)
    {
        Require(mutation.Data.AdventureData is not null, 20218016);
        return mutation.Data.AdventureData!;
    }

    // Callers receive a new adventure-scoped id; persistence is the caller's Commit.
    internal static int NextId(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        adventure.IdSequence = checked(adventure.IdSequence + 1);
        return adventure.IdSequence;
    }

    // Deterministic splitmix64 over the run's persisted (seed, position) pair. Advance lives in
    // the mutation clone and only becomes durable when the caller commits, so a rejected or
    // retried operation cannot reroll an offer; a committed map/offer stays frozen across relog.
    internal static int RandomIndex(Mutation mutation, int exclusiveMax)
    {
        Require(exclusiveMax > 0, 1);
        ulong x = unchecked((ulong)(uint)mutation.State.RandomSeed * 0x9E3779B97F4A7C15UL);
        x = unchecked(x + (ulong)mutation.State.RandomCounter++ * 0xBF58476D1CE4E5B9UL);
        x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
        x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
        x ^= x >> 31;
        return (int)(x % (ulong)exclusiveMax);
    }

    // Starts a fresh run: new identity, fresh RNG stream, and no leftover run-scoped state
    // (frozen receipts, in-battle encounter, timeback snapshots). Callers begin building the
    // map after this; the new identity is durable only when they commit.
    internal static long StartRun(Mutation mutation)
    {
        PlayerTheatre4State state = mutation.State;
        state.RunId = checked(state.RunId + 1);
        state.RandomSeed = unchecked((int)((ulong)state.RunId * 0x9E3779B97F4A7C15UL
            ^ (ulong)mutation.Session.player.PlayerData.Id));
        state.RandomCounter = 0;
        state.RequestReceipts.Clear();
        state.ActiveEncounter = null;
        state.TracebackSnapshots.Clear();
        // Starting the next run is the client's acknowledgement of the previous ending.
        state.PendingSettleAdventure = null;
        return state.RunId;
    }

    internal static TResponse Commit<TRequest, TResponse>(Session session, int packetId, string requestName,
        TRequest request, Action<Mutation, TResponse> action)
        where TRequest : notnull where TResponse : new()
    {
        _ = CommitResponse<TRequest, TResponse>(session, packetId, requestName, request, action,
            static bytes => MessagePackSerializer.Deserialize<TResponse>(bytes), out TResponse response);
        return response;
    }

    private static byte[] CommitResponse<TRequest, TResponse>(Session session, int packetId, string requestName,
        TRequest request, Action<Mutation, TResponse> action, Func<byte[], TResponse>? decode,
        out TResponse response)
        where TRequest : notnull where TResponse : new()
    {
        string requestKey = SemanticRequestKey(requestName, request);
        if (session.player.Theatre4.PendingMutation is { } pending)
        {
            Require(requestKey.Length > 0 && pending.RequestKey == requestKey
                && pending.ResponseName == typeof(TResponse).Name && pending.Response is not null, 1);
            response = decode is null ? default! : decode(pending.Response!);
            Theatre4RequestReceipt pendingReceipt = pending.Outcome.RequestReceipts.Values.FirstOrDefault(value =>
                value.RequestKey == requestKey && value.ResponseName == pending.ResponseName
                && value.MutationId == pending.Outcome.NextMutationId)
                ?? throw new ServerCodeException("Theatre4 response receipt is missing.", 1);
            Require(pendingReceipt.Epoch == pending.Outcome.Epoch && pendingReceipt.RunId == pending.Outcome.RunId, 1);
            Require(!pending.Outcome.RequestReceipts.TryGetValue(packetId, out Theatre4RequestReceipt? existing)
                || (existing.Epoch == pendingReceipt.Epoch && existing.RunId == pendingReceipt.RunId
                    && existing.MutationId == pendingReceipt.MutationId
                    && existing.RequestKey == requestKey && existing.ResponseName == pending.ResponseName), 1);
            StoreReceipt(pending.Outcome, packetId, pendingReceipt);
            CompletePending(session).SendPushes(session);
            SendPushes(session, pending.Pushes);
            return pending.Response!;
        }
        if (session.player.Theatre4.RequestReceipts.TryGetValue(packetId, out Theatre4RequestReceipt? receipt))
        {
            Require(receipt.Epoch == session.player.Theatre4.Epoch && receipt.RunId == session.player.Theatre4.RunId
                && receipt.RequestKey == requestKey && receipt.ResponseName == typeof(TResponse).Name, 1);
            response = decode is null ? default! : decode(receipt.Response);
            return receipt.Response;
        }

        response = new();
        Mutation mutation = new(session);
        action(mutation, response);
        byte[] responseBytes = MessagePackSerializer.Serialize(response);
        StoreReceipt(mutation.State, packetId, new()
        {
            Epoch = mutation.State.Epoch, RunId = mutation.State.RunId, MutationId = mutation.State.NextMutationId,
            RequestKey = requestKey, ResponseName = typeof(TResponse).Name, Response = responseBytes
        });
        Persist(mutation, requestKey, typeof(TResponse).Name, responseBytes).SendPushes(session);
        SendPushes(session, mutation.Pushes);
        return responseBytes;
    }

    internal static RewardApplicationResult Persist(Mutation mutation, string requestKey, string responseName, byte[]? response)
    {
        Session session = mutation.Session;
        PlayerTheatre4State previous = session.player.Theatre4;
        if (previous.PendingMutation is not null) throw new InvalidOperationException("Theatre4 recovery must finish before persistence.");
        previous.PendingMutation = new()
        {
            RequestKey = requestKey, Outcome = mutation.State, Grants = mutation.Grants,
            Pushes = mutation.Pushes, ResponseName = responseName, Response = response,
            ClaimedTaskIds = mutation.ClaimedTaskIds, ConditionCounters = mutation.ConditionCounters
        };
        try { session.player.SaveChecked(); }
        catch
        {
            // SaveChecked cannot distinguish a rejected write from an accepted write whose
            // acknowledgement was lost. Keep the frozen journal for an exact semantic retry;
            // a genuinely pre-write failure leaves it in memory only.
            throw;
        }
        return CompletePending(session);
    }

    private static RewardApplicationResult ApplyGrants(Session session, List<Theatre4PendingRewardGrant> grants) =>
        grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants.Select(grant => new RewardGrant(
            grant.ClaimKey, grant.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(), grant.Costs, grant.EventCause)).ToList(), session);

    private static RewardApplicationResult CompletePending(Session session)
    {
        PlayerTheatre4State previous = session.player.Theatre4;
        Theatre4PendingMutation pending = previous.PendingMutation
            ?? throw new InvalidOperationException("No pending Theatre4 operation.");
        RewardApplicationResult rewards = ApplyGrants(session, pending.Grants);
        List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
        session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(pending.ClaimedTaskIds).Distinct().ToList();
        Dictionary<int, int> oldCounters = session.player.MissionProgress.ConditionCounters;
        if (pending.ConditionCounters.Count > 0)
        {
            session.player.MissionProgress.ConditionCounters = new(oldCounters);
            foreach ((int id, int count) in pending.ConditionCounters)
                session.player.MissionProgress.ConditionCounters[id] = Math.Max(
                    session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id), count);
        }
        session.player.Theatre4 = pending.Outcome;
        session.player.Theatre4.PendingMutation = null;
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Theatre4 = previous;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims;
            session.player.MissionProgress.ConditionCounters = oldCounters;
            throw;
        }
        return rewards;
    }

    internal static void ResumePending(Session session, bool forLogin = false)
    {
        session.player.Theatre4 ??= new();
        if (session.player.Theatre4.PendingMutation is null) return;
        // A live snapshot destroys callback-held adventure objects. Only fresh login may
        // complete an unrelated request and then publish a new full snapshot.
        Require(forLogin, 1);
        CompletePending(session);
    }

    internal static void ReplayPending(Session session)
    {
        if (session.player.Theatre4.PendingMutation is not { } pending) return;
        Require(pending.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse), 1);
        CompletePending(session).SendPushes(session);
        // Task-only recovery may refresh common task UI, never callback-held run objects.
        // Rebuild after recovery rather than sending a frozen pre-reset progress notification.
        TaskModule.SendTaskSync(session);
    }

    private static void SendPushes(Session session, IEnumerable<Theatre4PendingPacket> pushes)
    {
        foreach (Theatre4PendingPacket push in pushes)
            session.SendPush(push.Name, push.Payload);
    }
}
