using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal enum GuildPermission { ManageMembers, ManageApplications, EditSettings, SpendGuildFunds, ManageBoss, ManageWar, ManageDorm, TransferLeadership }

internal sealed class GuildMutation
{
    public Session ActorSession { get; }
    public Guild Guild { get; }
    internal Dictionary<long, GuildPendingPlayer> Participants { get; } = [];
    internal List<GuildPendingPush> Pushes { get; } = [];
    internal string OperationId { get; } = Guid.NewGuid().ToString("N");
    private readonly Dictionary<RewardGoods, List<int>> rewardParams = [];
    internal GuildMutation(Session actor, Guild guild)
    {
        ActorSession = actor;
        Guild = BsonSerializer.Deserialize<Guild>(guild.ToBson());
        Guild.PendingOperation = null;
        Guild.Normalize();
    }
    internal GuildPendingPlayer Participant(long uid)
    {
        if (Participants.TryGetValue(uid, out GuildPendingPlayer? participant)) return participant;
        GuildModule.RecoverParticipant(ActorSession, uid);
        Player player = AscNet.Common.Database.Player.TryFromPlayerId(uid)
            ?? throw new ServerCodeException("Guild player does not exist", 20063033);
        participant = new() { Uid = uid, State = BsonSerializer.Deserialize<GuildPlayerState>((player.GuildState ?? new()).ToBson()) };
        GuildModule.SynchronizeGuildState(ActorSession, uid, player.GuildState ?? new());
        Participants.Add(uid, participant);
        return participant;
    }
    public GuildPlayerState Player(long uid) => Participant(uid).State;
    private GuildPendingGrant Grant(long uid)
    {
        GuildPendingPlayer participant = Participant(uid);
        string key = $"guild:{Guild.Id}:{OperationId}:{uid}";
        GuildPendingGrant? grant = participant.Grants.FirstOrDefault(value => value.ClaimKey == key);
        if (grant is null) participant.Grants.Add(grant = new() { ClaimKey = key });
        return grant;
    }
    public long Balance(long uid, int itemId)
    {
        Participant(uid);
        if (GuildModule.IsEconomyGoods(itemId)) return GuildModule.EconomyBalance(Guild, itemId);
        Inventory inventory = uid == ActorSession.player.PlayerData.Id ? ActorSession.inventory :
            Server.Instance.SessionFromUID(uid)?.inventory ?? Inventory.collection.Find(value => value.Uid == uid).FirstOrDefault()
            ?? throw new ServerCodeException("Guild inventory missing", 20063033);
        return checked(inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Participants[uid].Grants.Where(grant => !inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .Sum(grant => grant.Goods.Where(goods => goods.TemplateId == itemId).Sum(goods => (long)goods.Count) - grant.Costs.GetValueOrDefault(itemId)));
    }
    public void AddCost(long uid, int itemId, int count)
    {
        if (GuildModule.IsEconomyGoods(itemId)) { GuildModule.AddEconomyCost(this, uid, itemId, count); return; }
        if (!Inventory.IsValidClientItemId(itemId) || count < 0 || Balance(uid, itemId) < count)
            throw new ServerCodeException("Insufficient guild currency", 20063018);
        if (count == 0) return;
        GuildPendingGrant grant = Grant(uid);
        int remaining = count;
        foreach (RewardGoods goods in grant.Goods.Where(goods => goods.TemplateId == itemId))
        {
            int consumed = Math.Min(remaining, goods.Count);
            goods.Count -= consumed;
            remaining -= consumed;
        }
        // Keep zero-count rows aligned with their frozen parameter rows.
        if (remaining > 0) grant.Costs[itemId] = checked(grant.Costs.GetValueOrDefault(itemId) + remaining);
    }
    public void AddGoods(long uid, int itemId, int count)
    {
        if (GuildModule.IsEconomyGoods(itemId)) { GuildModule.AddEconomyGoods(this, uid, itemId, count); return; }
        if (!Inventory.IsValidClientItemId(itemId) || count < 0) throw new ServerCodeException("Invalid guild goods", 20063355);
        if (count > 0) AddRewardGoods(uid, [new() { Id = itemId, TemplateId = itemId, Count = count, RewardType = (int)RewardType.Item }]);
    }
    public List<RewardGoods> ResolveReward(int rewardId)
    {
        List<RewardGoodsTable> rows = RewardHandler.GetRewardGoods(rewardId);
        if (rows.Count == 0) throw new InvalidDataException($"Missing guild reward {rewardId}");
        return rows.Select(row =>
        {
            RewardGoods goods = new() { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException("Unsupported guild reward")) };
            rewardParams[goods] = row.Params.ToList();
            return goods;
        }).ToList();
    }
    public List<RewardGoods> AddReward(long uid, int rewardId)
    {
        List<RewardGoods> goods = ResolveReward(rewardId);
        AddRewardGoods(uid, goods);
        return goods;
    }
    private void AddFrozenGoods(long uid, GuildPendingGrant grant, RewardGoods goods, List<int> parameters)
    {
        if (goods.Count < 0) throw new InvalidDataException("Negative guild reward");
        if (GuildModule.IsEconomyGoods(goods.TemplateId))
            GuildModule.AddEconomyGoods(this, uid, goods.TemplateId, goods.Count);
        else
        {
            grant.Goods.Add(BsonSerializer.Deserialize<RewardGoods>(goods.ToBson()));
            grant.GoodsParams.Add(parameters.ToList());
        }
    }
    public void AddRewardGoods(long uid, IEnumerable<RewardGoods> goods)
    {
        GuildPendingGrant grant = Grant(uid);
        foreach (RewardGoods value in goods) AddFrozenGoods(uid, grant, value, rewardParams.GetValueOrDefault(value) ?? []);
    }
    private bool HasClaim(long uid, string key)
    {
        Inventory inventory = uid == ActorSession.player.PlayerData.Id ? ActorSession.inventory :
            Server.Instance.SessionFromUID(uid)?.inventory ?? Inventory.collection.Find(value => value.Uid == uid).FirstOrDefault()
            ?? throw new ServerCodeException("Guild inventory missing", 20063033);
        return Player(uid).AppliedRewardClaims.Contains(key) || inventory.AppliedRewardClaims.Contains(key);
    }
    public void AddRewardGrant(long uid, string stableClaimKey, IEnumerable<RewardGoodsTable> goods)
    {
        if (string.IsNullOrWhiteSpace(stableClaimKey) || stableClaimKey.Length > 128) throw new InvalidDataException("Invalid guild reward receipt");
        GuildPendingPlayer participant = Participant(uid);
        if (participant.Grants.Any(value => value.ClaimKey == stableClaimKey) || HasClaim(uid, stableClaimKey)) return;
        GuildPendingGrant grant = new() { ClaimKey = stableClaimKey };
        participant.Grants.Add(grant);
        foreach (RewardGoodsTable row in goods)
            AddFrozenGoods(uid, grant, new RewardGoods { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException("Unsupported guild reward")) }, row.Params);
        participant.State.AppliedRewardClaims.Add(stableClaimKey);
    }
    public void AddCostGrant(long uid, string stableClaimKey, IReadOnlyDictionary<int, int> costs)
    {
        if (string.IsNullOrWhiteSpace(stableClaimKey) || stableClaimKey.Length > 128) throw new InvalidDataException("Invalid guild cost receipt");
        GuildPendingPlayer participant = Participant(uid);
        if (participant.Grants.Any(value => value.ClaimKey == stableClaimKey) || HasClaim(uid, stableClaimKey)) return;
        foreach ((int itemId, int count) in costs)
            if (count <= 0 || !Inventory.IsValidClientItemId(itemId) || Balance(uid, itemId) < count)
                throw new ServerCodeException("Insufficient guild currency", 20063018);
        participant.Grants.Add(new() { ClaimKey = stableClaimKey, Costs = costs.ToDictionary(value => value.Key, value => value.Value) });
        participant.State.AppliedRewardClaims.Add(stableClaimKey);
    }
    public void QueueIdentity(long uid) => Push(uid, GuildModule.BuildLoginData(Guild, uid, Player(uid)));
    public bool IsTaskClaimed(long uid, int taskId) => Participant(uid).ClaimedTasks.Contains(taskId)
        || (uid == ActorSession.player.PlayerData.Id ? ActorSession.player : Server.Instance.SessionFromUID(uid)?.player
            ?? AscNet.Common.Database.Player.TryFromPlayerId(uid))!.MissionProgress.ClaimedTaskIds.Contains(taskId);
    public void MarkTaskClaimed(long uid, int taskId) { if (!IsTaskClaimed(uid, taskId)) Participant(uid).ClaimedTasks.Add(taskId); }
    public void AddTaskConditionProgress(long uid, int conditionId, int amount)
    {
        if (conditionId <= 0 || amount < 0) throw new InvalidDataException("Invalid guild task progress");
        GuildPendingPlayer participant = Participant(uid);
        Player player = uid == ActorSession.player.PlayerData.Id ? ActorSession.player :
            Server.Instance.SessionFromUID(uid)?.player ?? AscNet.Common.Database.Player.TryFromPlayerId(uid)!;
        int delta = checked(participant.ConditionCounters.GetValueOrDefault(conditionId) + amount);
        _ = checked(player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId) + delta);
        participant.ConditionCounters[conditionId] = delta;
    }
    public void AddMail(long uid, PlayerMail mail)
    {
        if (string.IsNullOrWhiteSpace(mail.Id)) throw new InvalidDataException("Guild mail requires a stable ID");
        GuildPendingPlayer participant = Participant(uid);
        if (!participant.Mails.Any(value => value.Id == mail.Id))
            participant.Mails.Add(BsonSerializer.Deserialize<PlayerMail>(mail.ToBson()));
    }
    public void Push<T>(long recipientUid, T payload) => Pushes.Add(new() { Uid = recipientUid, Name = typeof(T).Name, Body = MessagePackSerializer.Serialize(payload) });
    public void Broadcast<T>(T payload) { foreach (long uid in Guild.MemberIds) Push(uid, payload); }
}

internal partial class GuildModule
{
    internal static Guild? FindMembership(long uid) { Guild? guild = Guild.FindByMember(uid); guild?.Normalize(); return guild; }
    internal static int Rank(Guild guild, long uid) => !guild.MemberIds.Contains(uid) ? 9 : guild.LeaderId == uid ? 1 : guild.Members.GetValueOrDefault(uid)?.Rank ?? 4;
    internal static Guild RequireMembership(Session session, bool allowTourist = false)
    {
        Guild? guild = FindMembership(session.player.PlayerData.Id);
        Require(guild is not null && (allowTourist || Rank(guild, session.player.PlayerData.Id) != 5), 20063029);
        return guild!;
    }
    internal static void RequirePermission(Guild guild, long uid, GuildPermission permission)
    {
        int rank = Rank(guild, uid);
        Require(permission == GuildPermission.TransferLeadership ? rank == 1 : rank is 1 or 2, 20063032);
    }
    internal static DateTimeOffset GameDate(DateTimeOffset now) => now.ToOffset(TimeSpan.FromHours(Setting("GlobalTimeZone")));
    internal static long DailyPeriod(DateTimeOffset now) => (long)Math.Floor((now.ToUnixTimeSeconds() - Setting("DailyResetTimestamp")) / 86400d);
    internal static long WeeklyPeriod(DateTimeOffset now)
    {
        long day = DailyPeriod(now);
        int epochWeekday = (int)DateTimeOffset.UnixEpoch.DayOfWeek;
        int target = Setting("DayOfWeeklyReset") % 7;
        return (long)Math.Floor((day + epochWeekday - target) / 7d);
    }
    internal static DateTimeOffset NextWeeklyReset(DateTimeOffset now)
    {
        long day = DailyPeriod(now);
        do { day++; } while (((day + (int)DateTimeOffset.UnixEpoch.DayOfWeek) % 7 + 7) % 7 != Setting("DayOfWeeklyReset") % 7);
        return DateTimeOffset.FromUnixTimeSeconds(checked(day * 86400 + Setting("DailyResetTimestamp")));
    }
    internal static void Broadcast(Guild guild, object payload)
    {
        byte[] body = MessagePackSerializer.Serialize(payload.GetType(), payload);
        foreach (long uid in guild.MemberIds) Server.Instance.SessionFromUID(uid)?.SendPush(payload.GetType().Name, body);
    }
    static partial void OnMembershipChangedState(long uid);
    internal static void OnMembershipChanged(long uid)
    {
        OnMembershipChangedState(uid);
        Session? session = Server.Instance.SessionFromUID(uid);
        if (session is null) return;
        RecoverMembershipProgress(session);
        session.SendPush(BuildLoginData(session));
    }
    private static string RequestKey(Session session, Packet.Request packet) =>
        session.player.GuildState.RequestEpoch + ":" + packet.Name + ":" + Convert.ToHexString(SHA256.HashData(packet.Content ?? []));
    internal static void BeginLogin(Session session)
    {
        lock (MembershipLock)
        {
            RecoverPending(session, false);
            long previous = session.player.GuildState.RequestEpoch;
            session.player.GuildState.RequestEpoch = checked(previous + 1);
            try { session.player.SaveChecked(); }
            catch { session.player.GuildState.RequestEpoch = previous; throw; }
            SynchronizeGuildState(session, session.player.PlayerData.Id, session.player.GuildState);
        }
    }
    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<GuildMutation, TRequest, TResponse> action, bool allowTourist = false, int membershipCode = 20063029,
        int fallbackCode = 20063019, int duplicateKeyCode = 20063019)
        where TRequest : new() where TResponse : GuildResponse, new() =>
        HandleTarget(session, packet, 0, action, true, allowTourist, membershipCode, fallbackCode, duplicateKeyCode);
    internal static void HandleTarget<TRequest, TResponse>(Session session, Packet.Request packet, uint guildId,
        Action<GuildMutation, TRequest, TResponse> action, bool requireMembership = false, bool allowTourist = false, int membershipCode = 20063029,
        int fallbackCode = 20063019, int duplicateKeyCode = 20063019)
        where TRequest : new() where TResponse : GuildResponse, new()
    {
        string key = RequestKey(session, packet);
        byte[] responseBody;
        TResponse response = new();
        lock (MembershipLock)
        {
            try
            {
                RecoverPending(session, false);
                GuildRequestReceipt? receipt = session.player.GuildState.RequestReceipts.LastOrDefault(value => value.RequestId == packet.Id && value.RequestKey == key && value.ResponseName == typeof(TResponse).Name);
                if (receipt is not null) responseBody = receipt.ResponseBody;
                else
                {
                    Guild? guild = guildId == 0 ? FindMembership(session.player.PlayerData.Id) : Guild.FindById(guildId);
                    if (guild?.PendingOperation is not null)
                    {
                        CompletePending(session, guild, false);
                        guild = Guild.FindById(guild.Id);
                    }
                    if (guild is { Active: true })
                    {
                        PrepareManagementTarget(session, guild);
                        guild = Guild.FindById(guild.Id);
                    }
                    Require(guild is { Active: true } && (!requireMembership || (guild.MemberIds.Contains(session.player.PlayerData.Id)
                        && (allowTourist || Rank(guild, session.player.PlayerData.Id) != 5))), membershipCode);
                    GuildMutation mutation = new(session, guild!);
                    PrepareEconomy(mutation);
                    TRequest request = packet.Content is { Length: > 0 } ? packet.Deserialize<TRequest>() : new();
                    action(mutation, request, response);
                    if (response.Code != 0) throw new ServerCodeException("Guild request rejected", response.Code);
                    responseBody = MessagePackSerializer.Serialize(response);
                    mutation.Player(session.player.PlayerData.Id).RequestReceipts.Add(new() { RequestId = packet.Id, RequestKey = key,
                        ResponseName = typeof(TResponse).Name, ResponseBody = responseBody });
                    Persist(mutation, guild!, packet.Id, key, typeof(TResponse).Name, responseBody, true);
                }
            }
            catch (Exception exception)
            {
                response.Code = exception is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey }
                    ? duplicateKeyCode : Failure(exception, fallbackCode);
                responseBody = MessagePackSerializer.Serialize(response);
            }
        }
        session.SendResponse(typeof(TResponse).Name, responseBody, packet.Id);
    }
    internal static GuildMutation Commit(Session session, Guild guild, Action<GuildMutation> action, bool notify = true) =>
        CommitTarget(session, guild, action, true, notify: notify);
    internal static GuildMutation CommitTarget(Session session, Guild guild, Action<GuildMutation> action, bool requireMembership = false, bool allowTourist = false, bool notify = true)
    {
        lock (MembershipLock)
        {
            RecoverPending(session, false);
            Guild current = Guild.FindById(guild.Id) ?? throw new ServerCodeException("Guild missing", 20063026);
            if (current.PendingOperation is not null) CompletePending(session, current, false);
            current = Guild.FindById(guild.Id)!;
            Require(current.Active && (!requireMembership || (current.MemberIds.Contains(session.player.PlayerData.Id)
                && (allowTourist || Rank(current, session.player.PlayerData.Id) != 5))), 20063029);
            GuildMutation mutation = new(session, current);
            PrepareEconomy(mutation);
            action(mutation);
            Persist(mutation, current, 0, string.Empty, string.Empty, [], notify);
            return mutation;
        }
    }
    private static void Persist(GuildMutation mutation, Guild current, int requestId, string key, string responseName, byte[] responseBody, bool notify)
    {
        mutation.Guild.Normalize();
        // Validate every participant before any durable pending record or debit.
        foreach (GuildPendingPlayer participant in mutation.Participants.Values)
        {
            Session? online = participant.Uid == mutation.ActorSession.player.PlayerData.Id ? mutation.ActorSession : Server.Instance.SessionFromUID(participant.Uid);
            if (AscNet.Common.Database.Player.TryFromPlayerId(participant.Uid) is null)
                throw new ServerCodeException("Guild player missing", 20063033);
            if (participant.Grants.Count == 0) continue;
            Inventory storedInventory = Inventory.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault()
                ?? throw new ServerCodeException("Guild inventory missing", 20063033);
            Inventory inventory = online?.inventory ?? storedInventory;
            if (Character.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault() is null
                || Stage.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault() is null)
                throw new ServerCodeException("Guild participant documents missing", 20063033);
            foreach (IGrouping<int, KeyValuePair<int, int>> costs in participant.Grants
                .Where(grant => !inventory.AppliedRewardClaims.Contains(grant.ClaimKey)).SelectMany(grant => grant.Costs).GroupBy(cost => cost.Key))
                Require(costs.All(cost => cost.Value > 0 && Inventory.IsValidClientItemId(cost.Key))
                    && costs.Sum(cost => (long)cost.Value) <= (inventory.Items.FirstOrDefault(item => item.Id == costs.Key)?.Count ?? 0), 20063018);
        }
        current.PendingOperation = new() { Id = mutation.OperationId, ActorId = mutation.ActorSession.player.PlayerData.Id,
            RequestId = requestId, RequestKey = key, ResponseName = responseName, ResponseBody = responseBody,
            GuildState = mutation.Guild.ToBson(), Players = mutation.Participants.Values.ToList(), Pushes = mutation.Pushes };
        current.SaveChecked();
        CompletePending(mutation.ActorSession, current, notify);
    }
    private static void RecoverPending(Session session, bool notify)
    {
        session.player.GuildState ??= new();
        foreach (Guild guild in Guild.FindPendingByParticipant(session.player.PlayerData.Id)) CompletePending(session, guild, notify);
        foreach (Guild guild in Guild.FindPendingMembershipChanges(session.player.PlayerData.Id)) CompleteMembershipChanges(guild);
    }
    internal static void RecoverParticipant(Session actor, long uid)
    {
        foreach (Guild guild in Guild.FindPendingByParticipant(uid)) CompletePending(actor, guild, false);
        foreach (Guild guild in Guild.FindPendingMembershipChanges(uid)) CompleteMembershipChanges(guild);
    }
    private static void CompleteMembershipChanges(Guild guild)
    {
        foreach (long uid in guild.PendingMembershipChanges.ToArray())
        {
            Player? persisted = Player.TryFromPlayerId(uid);
            if (persisted is not null) SynchronizeGuildState(null, uid, persisted.GuildState ?? new());
            OnMembershipChangedState(uid);
            persisted = Player.TryFromPlayerId(uid);
            if (persisted is not null) SynchronizeGuildState(null, uid, persisted.GuildState ?? new());
            foreach (Session online in Server.Instance.Sessions.Values.Where(value => value.player?.PlayerData.Id == uid && value.GuildIdentityReady))
            {
                RecoverMembershipProgress(online);
                online.SendPush(BuildLoginData(online));
            }
            UpdateResult acknowledged = Guild.collection.UpdateOne(value => value.Id == guild.Id,
                Builders<Guild>.Update.Pull(value => value.PendingMembershipChanges, uid).Inc(value => value.Version, 1));
            if (!acknowledged.IsAcknowledged || acknowledged.MatchedCount != 1)
                throw new InvalidOperationException("Guild membership cleanup acknowledgement failed");
        }
    }
    internal static void SynchronizeGuildState(Session? actor, long uid, GuildPlayerState state)
    {
        byte[] frozen = state.ToBson();
        foreach (Session session in Server.Instance.Sessions.Values.Where(value => value.player?.PlayerData.Id == uid)
            .Append(actor).OfType<Session>().Distinct())
        {
            if (session.player?.PlayerData.Id == uid)
                session.player.GuildState = BsonSerializer.Deserialize<GuildPlayerState>(frozen);
        }
    }
    private static void CompletePending(Session actor, Guild current, bool notify)
    {
        GuildPendingOperation pending = current.PendingOperation ?? throw new InvalidOperationException("No pending guild operation");
        Guild outcome = BsonSerializer.Deserialize<Guild>(pending.GuildState);
        List<(Session Session, RewardApplicationResult Rewards)> rewards = [];
        foreach (GuildPendingPlayer participant in pending.Players.OrderBy(value => value.Uid))
        {
            lock (Session.GetPlayerOperationLock(participant.Uid))
            {
                Session? online = participant.Uid == actor.player.PlayerData.Id ? actor : Server.Instance.SessionFromUID(participant.Uid);
                using TcpClient? offlineClient = online is null ? new TcpClient() : null;
                Session target = online ?? new Session($"guild-recovery:{participant.Uid}", offlineClient!);
                Player persistedPlayer = AscNet.Common.Database.Player.TryFromPlayerId(participant.Uid)
                    ?? throw new InvalidDataException("Pending guild participant disappeared");
                if (online is null) target.player = persistedPlayer;
                GuildPlayerState persistedState = persistedPlayer.GuildState ?? new();
                SynchronizeGuildState(actor, participant.Uid, persistedState);
                target.player.GuildState = BsonSerializer.Deserialize<GuildPlayerState>(persistedState.ToBson());
                if (persistedState.AppliedOperationIds.Contains(pending.Id)) continue;
                List<RewardGrant> grants = participant.Grants.Where(grant => grant.Goods.Any(goods => goods.Count > 0) || grant.Costs.Count > 0)
                    .Select(grant => new RewardGrant(grant.ClaimKey, grant.Goods.Select((goods, index) => new RewardGoodsTable {
                        Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                        Params = index < grant.GoodsParams.Count ? grant.GoodsParams[index].ToList() : [] })
                        .Where(goods => goods.Count > 0).ToList(), grant.Costs)).ToList();
                if (online is null && grants.Count > 0)
                {
                    target.inventory = Inventory.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault() ?? throw new InvalidDataException("Pending guild inventory disappeared");
                    target.character = Character.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault() ?? throw new InvalidDataException("Pending guild characters disappeared");
                    target.stage = Stage.collection.Find(value => value.Uid == participant.Uid).FirstOrDefault() ?? throw new InvalidDataException("Pending guild stages disappeared");
                }
                RewardApplicationResult result = grants.Count > 0 ? RewardHandler.ApplyRewardsOnceAndPersist(grants, target) : new();
                GuildPlayerState previousState = target.player.GuildState;
                List<int> previousTasks = target.player.MissionProgress.ClaimedTaskIds;
                List<PlayerMail> previousMails = target.player.Mails;
                Dictionary<int, int> previousCounters = target.player.MissionProgress.ConditionCounters;
                Dictionary<int, int> counters = new(previousCounters);
                foreach ((int conditionId, int amount) in participant.ConditionCounters)
                    counters[conditionId] = checked(counters.GetValueOrDefault(conditionId) + amount);
                GuildPlayerState state = BsonSerializer.Deserialize<GuildPlayerState>(participant.State.ToBson());
                state.RequestEpoch = Math.Max(state.RequestEpoch, previousState.RequestEpoch);
                state.AppliedOperationIds.UnionWith(previousState.AppliedOperationIds);
                state.AppliedOperationIds.Add(pending.Id);
                state.AppliedRewardClaims.UnionWith(previousState.AppliedRewardClaims);
                foreach (GuildRequestReceipt receipt in previousState.RequestReceipts)
                    if (!state.RequestReceipts.Any(value => value.RequestId == receipt.RequestId && value.RequestKey == receipt.RequestKey && value.ResponseName == receipt.ResponseName))
                        state.RequestReceipts.Add(receipt);
                target.player.GuildState = state;
                target.player.MissionProgress.ClaimedTaskIds = previousTasks.Concat(participant.ClaimedTasks).Distinct().ToList();
                target.player.MissionProgress.ConditionCounters = counters;
                target.player.Mails = previousMails.Concat(participant.Mails
                    .Where(mail => !previousMails.Any(value => value.Id == mail.Id) && !target.player.MailExpireIds.Contains(mail.Id))
                    .Select(mail => BsonSerializer.Deserialize<PlayerMail>(mail.ToBson()))).ToList();
                try { target.player.SaveChecked(); }
                catch
                {
                    target.player.GuildState = previousState;
                    target.player.MissionProgress.ClaimedTaskIds = previousTasks;
                    target.player.MissionProgress.ConditionCounters = previousCounters;
                    target.player.Mails = previousMails;
                    throw;
                }
                SynchronizeGuildState(actor, participant.Uid, state);
                if (online is not null) rewards.Add((target, result));
            }
        }
        outcome.Version = current.Version;
        outcome.PendingOperation = null;
        outcome.PendingMembershipChanges = outcome.PendingMembershipChanges
            .Union(current.PendingMembershipChanges)
            .Union(current.MemberIds.Union(outcome.MemberIds).Where(uid => current.Active != outcome.Active || Rank(current, uid) != Rank(outcome, uid))).ToList();
        outcome.SaveChecked();
        CompleteMembershipChanges(outcome);
        if (!notify) return;
        foreach ((Session session, RewardApplicationResult result) in rewards) result.SendPushes(session);
        foreach (GuildPendingPush push in pending.Pushes)
            (push.Uid == actor.player.PlayerData.Id ? actor : Server.Instance.SessionFromUID(push.Uid))?.SendPush(push.Name, push.Body);
    }
}
