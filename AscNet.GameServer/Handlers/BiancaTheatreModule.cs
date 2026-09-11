using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.biancatheatre;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.condition;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public class BiancaTheatreResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreEmptyRequest { }
[MessagePackObject(true)]
public sealed class BiancaTheatreSelectDifficultyRequest { public int Difficulty { get; set; } }
[MessagePackObject(true)]
public sealed class BiancaTheatreSelectDifficultyResponse : BiancaTheatreResponse { public int ChapterId { get; set; } }
[MessagePackObject(true)]
public sealed class NotifyBiancaTheatreAddStep
{
    public int ChapterId { get; set; }
    public BiancaTheatreStep Step { get; set; } = new();
}

internal static partial class BiancaTheatreModule
{
    // EN XBiancaTheatreConfigs.lua:214-239; these IDs are protocol currency bindings.
    internal const int TheatreExp = 96117;
    internal const int OutCoin = 96118;
    internal const int InnerCoin = 96119;
    internal const int ActionPoint = 96120;
    internal const int VisionItem = 96185;
    internal static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static T Clone<T>(T value) => BsonSerializer.Deserialize<T>(value.ToBson());

    internal sealed class Mutation
    {
        public Session Session { get; }
        public BiancaTheatreState State { get; }
        public NotifyBiancaTheatreActivityData Data => State.Data;
        public List<RewardGoodsTable> Goods { get; } = [];
        public Dictionary<int, int> Costs { get; } = [];
        internal List<BiancaTheatrePendingPacket> Pushes { get; } = [];
        internal List<int> ClaimedTaskIds { get; } = [];
        internal List<int> ClaimedStoryTaskIds { get; } = [];
        internal List<BiancaTheatrePendingRewardGrant> NamedGrants { get; } = [];
        internal Mutation(Session session)
        {
            Session = session;
            State = Clone(session.player.BiancaTheatre ?? new());
            State.PendingMutation = null;
        }
        public void Push<T>(T payload) => Pushes.Add(new()
        {
            Name = typeof(T).Name,
            Payload = MessagePackSerializer.Serialize(payload)
        });
        public long Balance(int itemId) => checked(Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Goods.Where(item => (item.TemplateId > 0 ? item.TemplateId : item.Id) == itemId).Sum(item => (long)item.Count)
            + NamedGrants.Where(grant => !Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .SelectMany(grant => grant.Goods).Where(item => (item.TemplateId > 0 ? item.TemplateId : item.Id) == itemId).Sum(item => (long)item.Count)
            - Costs.GetValueOrDefault(itemId));
        public void AddGoods(int itemId, int count)
        {
            if (!Inventory.IsValidClientItemId(itemId) || count < 0)
                throw new ServerCodeException("Invalid Bianca item grant.", 1);
            if (count > 0) Goods.Add(new RewardGoodsTable { Id = itemId, TemplateId = itemId, Count = count, Params = [] });
        }
        public void AddCost(int itemId, int count)
        {
            if (!Inventory.IsValidClientItemId(itemId) || count < 0 || Balance(itemId) < count)
                throw new ServerCodeException("Insufficient Bianca currency.", 1);
            if (count == 0) return;
            // Net staged grants against costs: reward receipts debit before crediting.
            int remaining = count;
            for (int index = 0; index < Goods.Count && remaining > 0; index++)
            {
                RewardGoodsTable goods = Goods[index];
                if ((goods.TemplateId > 0 ? goods.TemplateId : goods.Id) != itemId) continue;
                int consumed = Math.Min(remaining, goods.Count);
                Goods[index] = new RewardGoodsTable
                {
                    Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count - consumed, Params = goods.Params
                };
                remaining -= consumed;
            }
            Goods.RemoveAll(goods => goods.Count == 0);
            if (remaining > 0) Costs[itemId] = checked(Costs.GetValueOrDefault(itemId) + remaining);
        }
        public void MarkTaskClaimed(int taskId)
        {
            if (!IsTaskClaimed(taskId)) ClaimedTaskIds.Add(taskId);
        }
        public bool IsTaskClaimed(int taskId) => ClaimedTaskIds.Contains(taskId)
            || Session.player.MissionProgress.ClaimedTaskIds.Contains(taskId);
        public void MarkStoryTaskClaimed(int taskId)
        {
            if (!IsStoryTaskClaimed(taskId)) ClaimedStoryTaskIds.Add(taskId);
        }
        public bool IsStoryTaskClaimed(int taskId) => ClaimedStoryTaskIds.Contains(taskId)
            || Session.stage.FinishedTasks.Contains(taskId);
        public void AddRewardGrant(string claimKey, IReadOnlyList<RewardGoodsTable> goods)
        {
            if (string.IsNullOrWhiteSpace(claimKey) || claimKey.Length > 128
                || NamedGrants.Any(grant => grant.ClaimKey == claimKey))
                throw new InvalidOperationException("Invalid or duplicate staged task reward claim.");
            if (goods.Count == 0) return;
            NamedGrants.Add(new()
            {
                ClaimKey = claimKey,
                Goods = goods.Select(item => new BiancaTheatrePendingGoods
                {
                    Id = item.Id, TemplateId = item.TemplateId, Count = item.Count, Params = item.Params.ToList()
                }).ToList()
            });
        }
    }

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action)
        where TRequest : new() where TResponse : BiancaTheatreResponse, new() =>
        Handle(session, packet, action, static (response, code) => response.Code = code);

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<TResponse, int> setCode, Action<Session>? afterCommit = null)
        where TRequest : new() where TResponse : new()
    {
        string key = packet.Name + ":" + Convert.ToHexString(SHA256.HashData(packet.Content ?? []));
        RewardApplicationResult? rewards = null;
        List<BiancaTheatrePendingPacket> pushes = [];
        byte[] responseBytes;
        bool committed = false;
        try
        {
            if (session.player.BiancaTheatre.PendingMutation is { } pending)
            {
                if (pending.RequestKey != key || pending.ResponseName != typeof(TResponse).Name || pending.Response is null)
                    throw new ServerCodeException("A different Bianca operation is awaiting recovery.", 1);
                if (pending.Outcome.RequestReceipts.TryGetValue(packet.Id, out BiancaTheatreRequestReceipt? prior)
                    && (prior.RequestKey != key || prior.ResponseName != pending.ResponseName))
                    throw new ServerCodeException("Bianca request identity was reused.", 1);
                pending.Outcome.RequestReceipts[packet.Id] = new()
                {
                    RequestKey = key, ResponseName = pending.ResponseName,
                    Response = pending.Response, Pushes = pending.Pushes
                };
                rewards = CompletePending(session);
                pushes = pending.Pushes;
                responseBytes = pending.Response;
            }
            else if (session.player.BiancaTheatre.RequestReceipts.TryGetValue(packet.Id, out BiancaTheatreRequestReceipt? receipt))
            {
                if (receipt.RequestKey != key || receipt.ResponseName != typeof(TResponse).Name)
                    throw new ServerCodeException("Bianca request identity was reused.", 1);
                pushes = receipt.Pushes;
                responseBytes = receipt.Response;
            }
            else
            {
                TRequest request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                    ? new() : packet.Deserialize<TRequest>();
                TResponse response = new();
                Mutation mutation = new(session);
                EnsureAvailable(mutation, DateTimeOffset.UtcNow);
                action(mutation, request, response);
                RecordCoreProgress(mutation);
                responseBytes = MessagePackSerializer.Serialize(response);
                mutation.State.RequestReceipts.Add(packet.Id, new()
                {
                    RequestKey = key, ResponseName = typeof(TResponse).Name,
                    Response = responseBytes, Pushes = mutation.Pushes
                });
                rewards = Persist(mutation, key, typeof(TResponse).Name, responseBytes);
                pushes = mutation.Pushes;
            }
            committed = true;
        }
        catch (ServerCodeException exception)
        {
            responseBytes = Failure(exception.Code);
        }
        catch (MessagePackSerializationException)
        {
            responseBytes = Failure(1);
        }
        catch (MongoException exception)
        {
            session.log.Error($"Bianca persistence failed: {exception.Message}");
            responseBytes = Failure(1);
        }
        rewards?.SendPushes(session);
        foreach (BiancaTheatrePendingPacket push in pushes) session.SendPush(push.Name, push.Payload);
        if (committed) afterCommit?.Invoke(session);
        session.SendResponse(typeof(TResponse).Name, responseBytes, packet.Id);

        byte[] Failure(int code)
        {
            TResponse response = new();
            setCode(response, code);
            return MessagePackSerializer.Serialize(response);
        }
    }

    internal static Mutation Commit(Session session, Action<Mutation> action)
    {
        if (session.player.BiancaTheatre.PendingMutation is not null)
            throw new ServerCodeException("A Bianca operation requires recovery.", 1);
        Mutation mutation = new(session);
        EnsureAvailable(mutation, DateTimeOffset.UtcNow);
        action(mutation);
        RecordCoreProgress(mutation);
        RewardApplicationResult? rewards;
        try { rewards = Persist(mutation, string.Empty, string.Empty, null); }
        catch (MongoException exception) { throw new ServerCodeException($"Bianca persistence failed: {exception.Message}", 1); }
        rewards?.SendPushes(session);
        foreach (BiancaTheatrePendingPacket push in mutation.Pushes) session.SendPush(push.Name, push.Payload);
        return mutation;
    }

    private static RewardApplicationResult? Persist(Mutation mutation, string requestKey, string responseName, byte[]? response)
    {
        Session session = mutation.Session;
        BiancaTheatreState previous = session.player.BiancaTheatre;
        if (mutation.Goods.Count == 0 && mutation.Costs.Count == 0 && mutation.NamedGrants.Count == 0 && mutation.ClaimedStoryTaskIds.Count == 0)
        {
            List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
            session.player.BiancaTheatre = mutation.State;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(mutation.ClaimedTaskIds).Distinct().ToList();
            try { session.player.SaveChecked(); }
            catch
            {
                session.player.BiancaTheatre = previous;
                session.player.MissionProgress.ClaimedTaskIds = oldClaims;
                throw;
            }
            return null;
        }
        BiancaTheatrePendingMutation pending = new()
        {
            ClaimKey = $"bianca:{session.player.PlayerData.Id}:{Guid.NewGuid():N}",
            RequestKey = requestKey,
            Outcome = mutation.State,
            Goods = mutation.Goods.Select(goods => new BiancaTheatrePendingGoods
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(),
            Costs = new(mutation.Costs),
            ClaimedTaskIds = mutation.ClaimedTaskIds.ToList(),
            NamedGrants = mutation.NamedGrants,
            ClaimedStoryTaskIds = mutation.ClaimedStoryTaskIds.ToList(),
            Pushes = mutation.Pushes,
            ResponseName = responseName,
            Response = response
        };
        previous.PendingMutation = pending;
        try { session.player.SaveChecked(); }
        catch { previous.PendingMutation = null; throw; }
        return CompletePending(session);
    }

    private static RewardApplicationResult CompletePending(Session session)
    {
        BiancaTheatreState previous = session.player.BiancaTheatre;
        BiancaTheatrePendingMutation pending = previous.PendingMutation
            ?? throw new InvalidOperationException("No pending Bianca operation.");
        List<RewardGrant> grants = pending.NamedGrants.Select(grant => new RewardGrant(grant.ClaimKey,
            grant.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList())).ToList();
        if (pending.Goods.Count > 0 || pending.Costs.Count > 0)
            grants.Add(new RewardGrant(pending.ClaimKey, pending.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(), pending.Costs));
        RewardApplicationResult rewards = grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
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
        List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
        session.player.BiancaTheatre = pending.Outcome;
        session.player.BiancaTheatre.PendingMutation = null;
        session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(pending.ClaimedTaskIds).Distinct().ToList();
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.BiancaTheatre = previous;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims;
            throw;
        }
        return rewards;
    }

    internal static void ResumePending(Session session, bool forLogin = false)
    {
        session.player.BiancaTheatre ??= new();
        if (session.player.BiancaTheatre.PendingMutation is not { } pending) return;
        if (forLogin && session.player.BiancaTheatre.Data.CurChapterId > 0
            && pending.Outcome.Data.CurChapterId == 0 && pending.Outcome.LastSettleData is not null)
            pending.Outcome.PendingSettleNotification = true;
        CompletePending(session);
    }

    internal static void ReplayPending(Session session)
    {
        if (session.player.BiancaTheatre.PendingMutation is not { } pending) return;
        if (!string.IsNullOrEmpty(pending.RequestKey)
            && pending.ResponseName is not (nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse)))
            throw new ServerCodeException("A different request owns the pending Bianca operation.", 1);
        RewardApplicationResult rewards = CompletePending(session);
        rewards.SendPushes(session);
        foreach (BiancaTheatrePendingPacket push in pending.Pushes) session.SendPush(push.Name, push.Payload);
    }

    internal static void PrepareLogin(Session session)
    {
        ResumePending(session, forLogin: true);
        RecoverCombatOnLogin(session);
        if (session.player.BiancaTheatre.RequestReceipts.Count == 0) return;
        Dictionary<int, BiancaTheatreRequestReceipt> receipts = session.player.BiancaTheatre.RequestReceipts;
        session.player.BiancaTheatre.RequestReceipts = [];
        try { session.player.SaveChecked(); }
        catch { session.player.BiancaTheatre.RequestReceipts = receipts; throw; }
    }

    internal static NotifyBiancaTheatreActivityData BuildLoginData(Session session, DateTimeOffset now)
    {
        ResumePending(session, forLogin: true);
        Mutation mutation = new(session);
        BiancaTheatreActivityTable? activity = OpenActivity(now);
        mutation.Data.CurActivityId = activity?.Id ?? 0;
        if (activity is not null) RefreshMetaUnlocks(mutation);
        if (!mutation.State.ToBson().AsSpan().SequenceEqual(session.player.BiancaTheatre.ToBson()))
            Persist(mutation, string.Empty, string.Empty, null);
        return Clone(session.player.BiancaTheatre.Data);
    }

    internal static void SendRecoveredSettlement(Session session)
    {
        BiancaTheatreState state = session.player.BiancaTheatre;
        bool abnormal = state.PendingAbnormalExitNotification;
        bool settle = state.PendingSettleNotification;
        if (!abnormal && !settle) return;
        if (abnormal) session.SendPush(new NotifyBiancaTheatreAbnormalExit());
        if (settle && state.LastSettleData is not null)
            session.SendPush(new NotifyBiancaTheatreAdventureSettle { SettleData = state.LastSettleData });
        state.PendingAbnormalExitNotification = false;
        state.PendingSettleNotification = false;
        try { session.player.SaveChecked(); }
        catch
        {
            state.PendingAbnormalExitNotification = abnormal;
            state.PendingSettleNotification = settle;
            throw;
        }
    }

    private static BiancaTheatreActivityTable? OpenActivity(DateTimeOffset now) => Rows<BiancaTheatreActivityTable>()
        .Where(row => ActivityScheduleService.IsOpen(row.TimeId, now)).OrderByDescending(row => row.Id).FirstOrDefault();

    private static void EnsureAvailable(Mutation mutation, DateTimeOffset now)
    {
        BiancaTheatreActivityTable? activity = OpenActivity(now);
        FunctionalOpenTable? function = Rows<FunctionalOpenTable>().SingleOrDefault(row => row.Id == 10433);
        if (activity is null || function is null || !function.Condition.All(id => Condition(mutation, id)))
            throw new ServerCodeException("Bianca Theatre is unavailable.", 1);
        mutation.Data.CurActivityId = activity.Id;
        RefreshUnlocks(mutation);
    }

    private static void RefreshUnlocks(Mutation mutation)
    {
        foreach (BiancaTheatreDifficultyTable row in Rows<BiancaTheatreDifficultyTable>())
            if (row.ConditionId is > 0 && Condition(mutation, row.ConditionId.Value) && !mutation.Data.UnlockDifficultyId.Contains(row.Id)) mutation.Data.UnlockDifficultyId.Add(row.Id);
        foreach (BiancaTheatreTeamTable row in Rows<BiancaTheatreTeamTable>())
            if (row.ConditionId is > 0 && Condition(mutation, row.ConditionId.Value) && !mutation.Data.UnlockTeamId.Contains(row.Id)) mutation.Data.UnlockTeamId.Add(row.Id);
        foreach (BiancaTheatreItemTable row in Rows<BiancaTheatreItemTable>())
            if (row.UnlockConditionId is > 0 && Condition(mutation, row.UnlockConditionId.Value) && !mutation.Data.UnlockItemId.Contains(row.Id)) mutation.Data.UnlockItemId.Add(row.Id);
    }

    [RequestPacketHandler("BiancaTheatreSelectDifficultyRequest")]
    public static void BiancaTheatreSelectDifficultyRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSelectDifficultyRequest, BiancaTheatreSelectDifficultyResponse>(session, packet, (mutation, request, response) =>
        {
            BiancaTheatreDifficultyTable? difficulty = Rows<BiancaTheatreDifficultyTable>().SingleOrDefault(row => row.Id == request.Difficulty);
            if (difficulty is null || (difficulty.ConditionId is > 0 && !mutation.Data.UnlockDifficultyId.Contains(request.Difficulty)))
                throw new ServerCodeException("Bianca difficulty is locked.", 1);
            if (mutation.Data.CurChapterId != 0)
            {
                if (mutation.Data.DifficultyId != request.Difficulty || mutation.Data.CurTeamId != 0)
                    throw new ServerCodeException("An adventure is already active.", 1);
                response.ChapterId = mutation.Data.CurChapterId;
                return;
            }
            BiancaTheatreChapterGroupTable group = Rows<BiancaTheatreChapterGroupTable>()
                .OrderBy(row => row.Id).FirstOrDefault(row => Condition(mutation, row.ConditionId ?? 0))
                ?? throw new ServerCodeException("No available Bianca chapter group.", 1);
            BiancaTheatreChapterTable chapter = Rows<BiancaTheatreChapterTable>().Single(row => row.Id == group.ChapterStartId);
            mutation.State.RunId = checked(mutation.State.RunId + 1);
            mutation.State.LastSettleData = null;
            mutation.State.PendingSettleNotification = false;
            mutation.State.AbnormalExitCount = 0;
            mutation.Data.DifficultyId = request.Difficulty;
            mutation.Data.CurChapterId = chapter.Id;
            mutation.Data.CurChapterDb = new() { ChapterId = chapter.Id };
            mutation.State.ReachedChapterIds.Add(chapter.Id);
            response.ChapterId = chapter.Id;
        });

    internal static int Uid(Mutation mutation) => mutation.State.NextUid = checked(mutation.State.NextUid + 1);

    internal static T PickWeighted<T>(IEnumerable<T> pool, Func<T, int> weight)
    {
        List<(T Value, int Weight)> entries = [];
        long total = 0;
        foreach (T value in pool)
        {
            int amount = weight(value);
            if (amount < 0) throw new InvalidDataException("Negative Bianca selection weight.");
            if (amount == 0) continue;
            total = checked(total + amount);
            entries.Add((value, amount));
        }
        if (total == 0) throw new ServerCodeException("No eligible Bianca selection candidates.", 1);
        long selected = Random.Shared.NextInt64(total);
        foreach ((T value, int amount) in entries)
        {
            if (selected < amount) return value;
            selected -= amount;
        }
        throw new InvalidOperationException("Invalid Bianca weighted selection.");
    }

    internal static BiancaTheatreStep? CurrentStep(Mutation mutation) => mutation.Data.CurChapterDb?.Steps.LastOrDefault(step => step.Overdue == 0);

    internal static void AppendStep(Mutation mutation, BiancaTheatreStep step, bool expireCurrent = true, bool notify = true)
    {
        BiancaTheatreChapterDb chapter = mutation.Data.CurChapterDb ?? throw new ServerCodeException("No active Bianca chapter.", 1);
        BiancaTheatreStep? current = CurrentStep(mutation);
        if (expireCurrent && current is not null && current.Uid != step.RootUid) current.Overdue = 1;
        if (step.Uid == 0) step.Uid = Uid(mutation);
        if (chapter.Steps.Any(existing => existing.Uid == step.Uid)) throw new InvalidOperationException("Duplicate Bianca step UID.");
        chapter.Steps.Add(step);
        if (notify) mutation.Push(new NotifyBiancaTheatreAddStep { ChapterId = chapter.ChapterId, Step = step });
    }

    internal static void ContinueRun(Mutation mutation)
    {
        if (mutation.Data.CurChapterDb is null) return;
        if (mutation.State.QueuedSteps.Count > 0)
        {
            BiancaTheatreStep next = mutation.State.QueuedSteps[0];
            mutation.State.QueuedSteps.RemoveAt(0);
            AppendStep(mutation, next);
            return;
        }
        BiancaTheatreStep? current = CurrentStep(mutation);
        if (current is not null)
        {
            BiancaTheatreNodeSlot? selected = current.NodeData?.Slots.SingleOrDefault(slot => slot.Selected == 1);
            if (selected is { SlotType: 2, CurStepId: 0 }) CompleteNode(mutation);
            return;
        }
        AppendNodeStep(mutation);
    }

    internal static bool Condition(Mutation mutation, int conditionId) => Condition(mutation, conditionId, []);

    private static bool Condition(Mutation mutation, int conditionId, HashSet<int> visiting)
    {
        if (conditionId == 0) return true;
        if (conditionId < 0 || visiting.Count >= 32 || !visiting.Add(conditionId)) return false;
        try
        {
            ConditionTable? row = Rows<ConditionTable>().SingleOrDefault(row => row.Id == conditionId);
            if (row is null) return false;
            if (!string.IsNullOrWhiteSpace(row.Formula))
                return EvaluateConditionFormula(row.Formula, id => id > 0 && Condition(mutation, id, visiting));
            List<int> args = row.Params;
            if (args.Count == 0) return false;
            int value = args[0];
            return row.Type switch
            {
                10101 => mutation.Session.player.PlayerData.Level >= value,
                17100 => mutation.Data.PassChapterIds.Contains(value),
                17101 => mutation.Data.HistoryTotalPassFightNodeCount >= value,
                17102 => mutation.Data.TeamRecords.Any(record => record.TeamId == value && record.EndRecords.Count > 0),
                17103 => mutation.State.TotalRecruitCount >= value,
                17105 => mutation.State.TotalCookieSpent >= value,
                17106 => mutation.State.CompletionCount >= value,
                17107 => mutation.Data.TeamRecords.Any(record => record.EndRecords.Contains(value)),
                17110 => args.Count >= 2 && mutation.State.ComboPhaseHistory.GetValueOrDefault(value) >= args[1],
                17111 => mutation.Data.PassedEventRecord.Values.Any(steps => steps.Contains(value)),
                17112 => mutation.State.SuccessfulDifficultyIds.Contains(value),
                17113 => args.Count >= 2 && (args[1] == 0 ? mutation.Data.HistoryTotalItemCount
                    : mutation.Data.HistoryItemObtainRecords.GetValueOrDefault(args[1])) >= value,
                17116 => mutation.State.PreviousRunPassChapterIds.Contains(value),
                17122 => mutation.Data.IsOpenVision != 0 && CurrentVision(mutation)?.Id >= value,
                _ => false
            };
        }
        finally { visiting.Remove(conditionId); }
    }

    private static bool EvaluateConditionFormula(string formula, Func<int, bool> evaluate)
    {
        int position = 0;
        bool valid = true;
        bool result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length && result;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        bool Expression(int depth)
        {
            if (depth > 32) { valid = false; return false; }
            bool value = Atom(depth);
            SkipWhitespace();
            // EN XConditionFormula gives '&' and '|' equal precedence, evaluated left to right.
            while (position < formula.Length && formula[position] is '&' or '|')
            {
                char op = formula[position++];
                bool right = Atom(depth);
                value = op == '&' ? value & right : value | right;
                SkipWhitespace();
            }
            return value;
        }
        bool Atom(int depth)
        {
            SkipWhitespace();
            if (position == formula.Length || depth > 32) { valid = false; return false; }
            if (formula[position] == '!')
            {
                position++;
                return !Atom(depth + 1);
            }
            if (formula[position] == '(')
            {
                position++;
                bool nested = Expression(depth + 1);
                SkipWhitespace();
                if (position == formula.Length || formula[position++] != ')') valid = false;
                return nested;
            }
            int start = position;
            while (position < formula.Length && char.IsAsciiDigit(formula[position])) position++;
            if (start == position || !int.TryParse(formula.AsSpan(start, position - start), out int id))
            {
                valid = false;
                return false;
            }
            return evaluate(id);
        }
    }

    private static BiancaTheatreVisionTable? CurrentVision(Mutation mutation)
    {
        long value = mutation.Balance(VisionItem);
        return Rows<BiancaTheatreVisionTable>().FirstOrDefault(row => value >= (row.Min ?? 0) && value <= row.Max);
    }

    internal static IReadOnlyList<BiancaTheatreComboTable> ActiveCombos(Mutation mutation)
    {
        List<BiancaTheatreComboTable> result = [];
        foreach (BiancaTheatreChildComboTable child in Rows<BiancaTheatreChildComboTable>())
        {
            HashSet<int> ids = Rows<BiancaTheatreBaseCharacterTable>()
                .Where(character => character.ReferenceComboId.Contains(child.Id)).Select(character => character.CharacterId).ToHashSet();
            List<BiancaTheatreCharacter> members = mutation.Data.Characters.Where(character => ids.Contains(character.CharacterId)).ToList();
            if (members.Count == 0) continue;
            int stars = members.Sum(character => character.Level);
            BiancaTheatreComboTable? active = Rows<BiancaTheatreComboTable>().Where(combo => combo.ChildComboId == child.Id
                && ((combo.IsDecay ?? 0) == 0 || members.Any(character => character.IsDecay != 0))
                && (child.ActivationType switch
                {
                    1 => members.Count >= combo.ConditionNum,
                    2 => stars >= combo.ConditionLevel,
                    3 => members.Count(character => character.Level >= combo.ConditionLevel) >= combo.ConditionNum,
                    4 => members.Count >= combo.ConditionNum && stars >= combo.ConditionLevel,
                    _ => false
                })).OrderByDescending(combo => combo.Id).FirstOrDefault();
            if (active is not null) result.Add(active);
        }
        return result;
    }

    internal static IReadOnlyDictionary<int, int> ActiveComboLevels(Mutation mutation) => ActiveCombos(mutation).ToDictionary(
        combo => combo.ChildComboId,
        combo => Rows<BiancaTheatreComboTable>().Count(row => row.ChildComboId == combo.ChildComboId && row.Id <= combo.Id));

    internal static IReadOnlyList<BiancaTheatreEffectGroupTable> EffectGroups(Mutation mutation, bool includeCombos = true)
    {
        List<int> ids = [];
        foreach (BiancaTheatreItem item in mutation.Data.Items)
        {
            int? group = Rows<BiancaTheatreItemTable>().Single(row => row.Id == item.ItemId).EffectGroupId;
            if (group is > 0) ids.Add(group.Value);
        }
        foreach (int strengthenId in mutation.Data.StrengthenDbs)
            ids.Add(Rows<BiancaTheatreStrengthenTable>().Single(row => row.Id == strengthenId).EffectGroupId);
        if (mutation.Data.CurTeamId > 0)
            ids.Add(Rows<BiancaTheatreTeamTable>().Single(row => row.Id == mutation.Data.CurTeamId).EffectGroupId);
        if (mutation.Data.DifficultyId > 0)
            ids.Add(Rows<BiancaTheatreDifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId).EffectGroupId);
        if (mutation.Data.IsOpenVision != 0 && CurrentVision(mutation) is { } vision) ids.Add(vision.EffectGroupId);
        BiancaTheatreNodeSlot? selected = mutation.Data.CurChapterDb?.Steps.LastOrDefault(step => step.NodeData is not null)
            ?.NodeData?.Slots.SingleOrDefault(slot => slot.Selected == 1);
        if (selected is { SlotType: 2, CurStepId: > 0 })
        {
            int? eventGroup = Rows<BiancaTheatreEventTable>().Single(row => row.EventId == selected.EventId && row.StepId == selected.CurStepId).EffectGroupId;
            if (eventGroup is > 0) ids.Add(eventGroup.Value);
        }
        if (includeCombos)
            foreach (BiancaTheatreComboTable combo in ActiveCombos(mutation)) ids.AddRange(combo.EffectId.Where(id => id > 0));
        return ids.Where(id => id > 0).Select(id => Rows<BiancaTheatreEffectGroupTable>().Single(row => row.Id == id)).ToList();
    }

    internal static IReadOnlyList<BiancaTheatreSystemEffectTable> SystemEffects(Mutation mutation) => EffectGroups(mutation)
        .SelectMany(group => group.SystemEvents).Where(id => id > 0)
        .Select(id => Rows<BiancaTheatreSystemEffectTable>().Single(row => row.Id == id)).ToList();

    private static void RecordCoreProgress(Mutation mutation)
    {
        foreach ((int comboId, int phase) in ActiveComboLevels(mutation))
            mutation.State.ComboPhaseHistory[comboId] = Math.Max(mutation.State.ComboPhaseHistory.GetValueOrDefault(comboId), phase);
        RefreshMetaUnlocks(mutation);
        RecordMetaProgress(mutation);
    }
}
