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
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    internal static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    internal static T Clone<T>(T value) =>
        BsonSerializer.Deserialize<CloneContainer<T>>(new CloneContainer<T> { Value = value }.ToBson()).Value;

    // BSON requires a document root, even when the cloned value is a list or map.
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
    internal static int AllocateUid(Mutation mutation) => mutation.State.NextUid = checked(mutation.State.NextUid + 1);
    internal static void Require(bool condition, int code)
    {
        if (!condition) throw new ServerCodeException("Theatre3 request rejected.", code);
    }

    internal sealed class Mutation
    {
        public Session Session { get; }
        public PlayerTheatre3State State { get; }
        public Theatre3ActivityData Data => State.Data;
        internal List<Theatre3PendingPacket> Pushes { get; } = [];
        internal List<Theatre3PendingRewardGrant> Grants { get; } = [];
        internal List<int> ClaimedTaskIds { get; } = [];
        internal List<int> ClaimedStoryTaskIds { get; } = [];
        internal Dictionary<int, int> BiancaTaskProgress { get; } = [];
        internal List<int> BiancaReachedChapterIds { get; } = [];

        public long Balance(int itemId) => checked(Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Grants.Where(grant => !Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .Sum(grant => grant.Goods.Where(goods => (goods.TemplateId > 0 ? goods.TemplateId : goods.Id) == itemId)
                    .Sum(goods => (long)goods.Count) - grant.Costs.GetValueOrDefault(itemId)));

        public void Cost(int itemId, int count, int insufficientCode = 20203037, bool recordSpending = true)
        {
            // EN CodeText: Theatre3ItemIdError / Theatre3InnerCoinNotEnough.
            Require(Inventory.IsValidClientItemId(itemId) && count >= 0, 20203042);
            Require(Balance(itemId) >= count, insufficientCode);
            // EN Theatre3 currency binding: Processing Units; track actual spending before netting.
            if (recordSpending && itemId == 96189) State.TotalCoinSpent = checked(State.TotalCoinSpent + count);
            int remaining = count;
            foreach (Theatre3PendingRewardGrant grant in Grants)
            {
                if (remaining == 0) break;
                if (Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey)) continue;
                foreach (Theatre3PendingGoods goods in grant.Goods)
                {
                    if ((goods.TemplateId > 0 ? goods.TemplateId : goods.Id) != itemId) continue;
                    int consumed = Math.Min(remaining, goods.Count);
                    goods.Count -= consumed;
                    remaining -= consumed;
                }
                grant.Goods.RemoveAll(goods => goods.Count == 0);
            }
            Grants.RemoveAll(grant => grant.Goods.Count == 0 && grant.Costs.Count == 0);
            if (remaining > 0)
                Grant(new RewardGrant($"theatre3:{Session.player.PlayerData.Id}:{Guid.NewGuid():N}", [],
                    new Dictionary<int, int> { [itemId] = remaining }));
        }

        public bool IsTaskClaimed(int id) => ClaimedTaskIds.Contains(id) || Session.player.MissionProgress.ClaimedTaskIds.Contains(id);
        public bool IsStoryTaskClaimed(int id) => ClaimedStoryTaskIds.Contains(id) || Session.stage.FinishedTasks.Contains(id);
        public void MarkTaskClaimed(int id) { if (!IsTaskClaimed(id)) ClaimedTaskIds.Add(id); }
        public void MarkStoryTaskClaimed(int id) { if (!IsStoryTaskClaimed(id)) ClaimedStoryTaskIds.Add(id); }

        public void RecordBiancaTaskProgress(IReadOnlyDictionary<int, int> taskProgress, IEnumerable<int> reachedChapterIds)
        {
            foreach ((int id, int progress) in taskProgress)
                BiancaTaskProgress[id] = Math.Max(BiancaTaskProgress.GetValueOrDefault(id), progress);
            foreach (int id in reachedChapterIds)
                if (!BiancaReachedChapterIds.Contains(id)) BiancaReachedChapterIds.Add(id);
        }

        internal Mutation(Session session)
        {
            Session = session;
            State = Clone(session.player.Theatre3);
            State.PendingMutation = null;
        }

        public void Push(object payload) => Pushes.Add(new()
        {
            Name = payload.GetType().Name,
            Payload = MessagePackSerializer.Serialize(payload.GetType(), payload)
        });

        public void Grant(int rewardId)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0) throw new InvalidDataException($"Missing Theatre3 reward group {rewardId}.");
            Grant(new RewardGrant($"theatre3:{Session.player.PlayerData.Id}:{Guid.NewGuid():N}", goods));
        }

        public void Grant(IEnumerable<int> rewardIds)
        {
            foreach (int rewardId in rewardIds) Grant(rewardId);
        }

        public void Grant(RewardGrant grant)
        {
            if (string.IsNullOrWhiteSpace(grant.ClaimKey) || grant.ClaimKey.Length > 128
                || Grants.Any(existing => existing.ClaimKey == grant.ClaimKey)
                || (grant.Goods.Count == 0 && grant.Costs is not { Count: > 0 }))
                throw new InvalidOperationException("Invalid Theatre3 reward grant.");
            Grants.Add(new()
            {
                ClaimKey = grant.ClaimKey,
                Goods = grant.Goods.Select(goods => new Theatre3PendingGoods
                {
                    Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params?.ToList() ?? []
                }).ToList(),
                Costs = grant.Costs?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [],
                EventCause = grant.EventCause
            });
        }
    }

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<Session>? onFailure = null)
        where TRequest : new() where TResponse : Theatre3Response, new() =>
        Handle(session, packet, action, static (response, code) => response.Code = code, onFailure);

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<TResponse, int> setCode,
        Action<Session>? onFailure = null, Action<Session>? afterCommit = null)
        where TRequest : new() where TResponse : new()
    {
        string key = packet.Name + ":" + Convert.ToHexString(SHA256.HashData(packet.Content ?? []));
        byte[] responseBytes;
        bool committed = false;
        try
        {
            // Recover the durable predecessor before considering this request. A new request
            // never replaces a pending outcome, nor gets silently consumed by its recovery.
            if (session.player.Theatre3.PendingMutation is { } pending)
            {
                RewardApplicationResult recovered = CompletePending(session);
                if (!session.player.Theatre3.RequestReceipts.TryGetValue(packet.Id, out Theatre3RequestReceipt? recoveredReceipt)
                    || recoveredReceipt.RequestKey != key || recoveredReceipt.ResponseName != typeof(TResponse).Name)
                {
                    recovered.SendPushes(session);
                    SendPushes(session, pending.Pushes, replay: true);
                    ReconcileRecoveredRequest(session, pending);
                }
            }
            if (session.player.Theatre3.RequestReceipts.TryGetValue(packet.Id, out Theatre3RequestReceipt? receipt))
            {
                Require(receipt.RunId == session.player.Theatre3.RunId
                    && receipt.RequestKey == key && receipt.ResponseName == typeof(TResponse).Name, 1);
                ApplyGrants(session, receipt.Grants).SendPushes(session);
                SendPushes(session, receipt.Pushes, replay: true);
                responseBytes = PreparePacketReplay(session, receipt.ResponseName, receipt.Response);
            }
            else
            {
                TRequest request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                    ? new() : packet.Deserialize<TRequest>();
                TResponse response = new();
                Mutation mutation = new(session);
                EnsureAvailable(mutation, DateTimeOffset.UtcNow);
                action(mutation, request, response);
                responseBytes = MessagePackSerializer.Serialize(response);
                mutation.State.RequestReceipts.Add(packet.Id, new()
                {
                    RunId = mutation.State.RunId,
                    RequestKey = key, ResponseName = typeof(TResponse).Name, Response = responseBytes,
                    Pushes = mutation.Pushes, Grants = mutation.Grants
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
            session.log.Error($"Theatre3 persistence failed: {exception.Message}");
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

    internal static Mutation Commit(Session session, Action<Mutation> action)
    {
        ReplayPending(session);
        Mutation mutation = new(session);
        EnsureAvailable(mutation, DateTimeOffset.UtcNow);
        action(mutation);
        Persist(mutation, string.Empty, string.Empty, null).SendPushes(session);
        SendPushes(session, mutation.Pushes);
        return mutation;
    }

    internal static RewardApplicationResult Persist(Mutation mutation, string requestKey, string responseName, byte[]? response)
    {
        Session session = mutation.Session;
        PlayerTheatre3State previous = session.player.Theatre3;
        if (previous.PendingMutation is not null) throw new InvalidOperationException("Theatre3 recovery must finish before persistence.");
        if (mutation.Grants.Count == 0 && mutation.ClaimedStoryTaskIds.Count == 0
            && mutation.BiancaTaskProgress.Count == 0 && mutation.BiancaReachedChapterIds.Count == 0)
        {
            List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(mutation.ClaimedTaskIds).Distinct().ToList();
            session.player.Theatre3 = mutation.State;
            try { session.player.SaveChecked(); }
            catch
            {
                session.player.Theatre3 = previous;
                session.player.MissionProgress.ClaimedTaskIds = oldClaims;
                throw;
            }
            return new();
        }
        previous.PendingMutation = new()
        {
            RequestKey = requestKey, Outcome = mutation.State, Grants = mutation.Grants,
            Pushes = mutation.Pushes, ResponseName = responseName, Response = response,
            ClaimedTaskIds = mutation.ClaimedTaskIds, ClaimedStoryTaskIds = mutation.ClaimedStoryTaskIds,
            BiancaTaskProgress = mutation.BiancaTaskProgress, BiancaReachedChapterIds = mutation.BiancaReachedChapterIds
        };
        try { session.player.SaveChecked(); }
        catch { previous.PendingMutation = null; throw; }
        return CompletePending(session);
    }

    private static RewardApplicationResult ApplyGrants(Session session, List<Theatre3PendingRewardGrant> grants) =>
        grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants.Select(grant => new RewardGrant(
            grant.ClaimKey, grant.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(), grant.Costs, grant.EventCause)).ToList(), session);

    private static RewardApplicationResult CompletePending(Session session)
    {
        PlayerTheatre3State previous = session.player.Theatre3;
        Theatre3PendingMutation pending = previous.PendingMutation
            ?? throw new InvalidOperationException("No pending Theatre3 operation.");
        RewardApplicationResult rewards = ApplyGrants(session, pending.Grants);
        if (pending.ClaimedStoryTaskIds.Any(id => !session.stage.FinishedTasks.Contains(id)))
        {
            Stage previousStage = session.stage;
            Stage stagedStage = Clone(previousStage);
            foreach (int id in pending.ClaimedStoryTaskIds)
                if (!stagedStage.FinishedTasks.Contains(id)) stagedStage.FinishedTasks.Add(id);
            session.stage = stagedStage;
            try { stagedStage.SaveChecked(); }
            catch { session.stage = previousStage; throw; }
        }
        BiancaTheatreState previousBianca = session.player.BiancaTheatre;
        if (pending.BiancaTaskProgress.Count > 0 || pending.BiancaReachedChapterIds.Count > 0)
        {
            BiancaTheatreState stagedBianca = Clone(previousBianca);
            foreach ((int id, int progress) in pending.BiancaTaskProgress)
                stagedBianca.TaskProgress[id] = Math.Max(stagedBianca.TaskProgress.GetValueOrDefault(id), progress);
            foreach (int id in pending.BiancaReachedChapterIds)
                if (!stagedBianca.ReachedChapterIds.Contains(id)) stagedBianca.ReachedChapterIds.Add(id);
            session.player.BiancaTheatre = stagedBianca;
        }
        List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
        session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(pending.ClaimedTaskIds).Distinct().ToList();
        session.player.Theatre3 = pending.Outcome;
        session.player.Theatre3.PendingMutation = null;
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Theatre3 = previous;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims;
            session.player.BiancaTheatre = previousBianca;
            throw;
        }
        return rewards;
    }

    internal static void ResumePending(Session session, bool forLogin = false)
    {
        session.player.Theatre3 ??= new();
        if (session.player.Theatre3.PendingMutation is not null) CompletePending(session);
    }

    internal static void ReplayPending(Session session)
    {
        if (session.player.Theatre3.PendingMutation is not { } pending) return;
        CompletePending(session).SendPushes(session);
        SendPushes(session, pending.Pushes, replay: true);
        ReconcileRecoveredRequest(session, pending);
    }

    private static void SendPushes(Session session, IEnumerable<Theatre3PendingPacket> pushes, bool replay = false)
    {
        foreach (Theatre3PendingPacket push in pushes)
            session.SendPush(push.Name, replay ? PreparePacketReplay(session, push.Name, push.Payload) : push.Payload);
    }
}
