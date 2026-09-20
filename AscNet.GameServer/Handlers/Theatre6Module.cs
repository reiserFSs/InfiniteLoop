using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Driver;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using GlobalConditionTable = AscNet.Table.V2.share.condition.ConditionTable;
using TaskCondition = AscNet.Table.V2.share.task.ConditionTable;
using TaskRow = AscNet.Table.V2.share.task.TaskTable;
using TaskTimeLimitRow = AscNet.Table.V2.share.task.TaskTimeLimitTable;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre6Module
{
    // PlayMode: GamePlay = the free gameplay run, Story = the story-line run. Both may exist
    // at once and are addressed independently; unscoped notifications use State.CurrentMode.
    internal const int GamePlayMode = 1;
    internal const int StoryMode = 2;

    // Client-authored error catalogue (EN CodeText 20423001..20423107). Core owns these names;
    // RequiemRun declares the Err* set for its own paths.
    internal const int CodeInvalidRequest = 1;
    internal const int CodeNoCurrentMode = 20423013;
    internal const int CodeArchiveData = 20423043;
    internal const int CodeSlotId = 20423044;
    internal const int CodeArchiveFileData = 20423046;
    internal const int CodePlayModeNotUnlocked = 20423050;
    internal const int CodeStoryId = 20423087;
    internal const int CodeAlreadySettle = 20423098;
    internal const int CodeInsufficientItem = 20012004;

    //region table rows

    private static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static readonly Lazy<Dictionary<int, Theatre6ActivityTable>> Activities = new(() => Rows<Theatre6ActivityTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6CharacterTable>> Characters = new(() => Rows<Theatre6CharacterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6CharacterFashionTable>> Fashions = new(() => Rows<Theatre6CharacterFashionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrTable>> Attrs = new(() => Rows<Theatre6AttrTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6SkillTable>> Skills = new(() => Rows<Theatre6SkillTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrPackTable>> AttrPacks = new(() => Rows<Theatre6AttrPackTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<HashSet<int>> Buffs = new(() => Rows<Theatre6StageBuffTable>().Select(row => row.Id).ToHashSet());
    private static readonly Lazy<HashSet<int>> BuildTags = new(() => Rows<Theatre6BuildTagTable>().Select(row => row.Id).ToHashSet());
    private static readonly Lazy<Dictionary<int, Theatre6StageTable>> Stages = new(() => Rows<Theatre6StageTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, Theatre6StoryDetailTable>> StoryDetails = new(() => Rows<Theatre6StoryDetailTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<List<Theatre6TalentTable>> Talents = new(() => Rows<Theatre6TalentTable>().OrderBy(row => row.Level).ToList());
    private static readonly Lazy<Dictionary<string, Theatre6ConfigTable>> Configs = new(() => Rows<Theatre6ConfigTable>().ToDictionary(row => row.Key));
    private static readonly Lazy<int> MaxSaveFiles = new(() => Cfg("MaxSaveFileCount"));
    private static readonly Lazy<Dictionary<int, Theatre6PvpRankTable>> PvpRanks = new(() => Rows<Theatre6PvpRankTable>().ToDictionary(row => row.Id));

    //endregion

    //region availability

    // Base PvE is permanent: the client's inherited entry check (XFubenSimulationChallengeAgency
    // ExCheckInTime) always returns true, so the only authored gate is the functional chain
    // SkipFunctional(UiName XTheatre6) -> FunctionalOpen -> Condition type 10101 (commandant level).
    // ActivitySchedule has no row for the base activity TimeId, which confirms PvE is not seasonal.
    // Phantom Clash keeps its authored season window (Theatre6PvpActivity.TimeId, 48601/48602)
    // here; its separate functional 10506 unlock condition (stage clear AND two archives) is the
    // Phantom Clash gate's own check, not part of whether a season exists for the account.
    private readonly record struct Availability(int BaseActivityId, int RequiredLevel, int PvpSeasonId, int PvpTimeId);

    private static readonly Lazy<Availability?> CurrentAvailability = new(LoadAvailability);

    private static Availability? LoadAvailability()
    {
        List<SkipFunctionalTable> skips = Rows<SkipFunctionalTable>();
        Dictionary<int, FunctionalOpenTable> functions = Rows<FunctionalOpenTable>().ToDictionary(row => row.Id);
        Dictionary<int, GlobalConditionTable> conditions = Rows<GlobalConditionTable>().ToDictionary(row => row.Id);
        if (Rows<Theatre6ActivityTable>() is not [Theatre6ActivityTable activity])
            return null;

        int pvpConditionId = PvpConfig("UnlockPvpModeConditionId");
        // The Phantom Clash functional is the one whose authored condition is the PvP unlock
        // condition; the remaining XTheatre6 entry is the permanent base (level) entry.
        HashSet<int> pvpFunctions = functions.Values
            .Where(row => row.Condition.Contains(pvpConditionId))
            .Select(row => row.Id).ToHashSet();
        List<int> theatre6Functions = skips
            .Where(row => row.UiName == "XTheatre6" && row.FunctionalId is int id && id > 0 && functions.ContainsKey(id))
            .Select(row => row.FunctionalId!.Value).Distinct().ToList();
        int? pvpFunctionId = theatre6Functions.Where(pvpFunctions.Contains).Select(id => (int?)id).FirstOrDefault();
        int? baseFunctionId = theatre6Functions.Where(id => !pvpFunctions.Contains(id)).Select(id => (int?)id).FirstOrDefault();
        if (baseFunctionId is not int baseId || pvpFunctionId is null)
            return null;

        FunctionalOpenTable baseFunction = functions[baseId];
        if (baseFunction.Condition.Count != 1
            || !conditions.TryGetValue(baseFunction.Condition[0], out GlobalConditionTable? levelCondition)
            || levelCondition.Type != 10101
            || levelCondition.Params.Count != 1)
            return null;

        Theatre6PvpActivityTable? season = Rows<Theatre6PvpActivityTable>().OrderBy(row => row.Id).FirstOrDefault(row => row.TimeId > 0);
        if (season is null)
            return null;

        return new Availability(activity.Id, levelCondition.Params[0], season.Id, season.TimeId);
    }

    private static int PvpConfig(string key) => Rows<Theatre6PvpConfigTable>().Single(row => row.Key == key).Values;
    internal static int Cfg(string key)
    {
        if (!Configs.Value.TryGetValue(key, out Theatre6ConfigTable? row) || row.Values.Count == 0)
            throw new InvalidDataException($"Missing Theatre6 config '{key}'.");
        return row.Values[0];
    }

    /// <summary>Authored base PvE gate: level gate from the functional chain only, never a calendar.</summary>
    internal static bool IsBaseAvailable(Player player) =>
        CurrentAvailability.Value is Availability availability
        && player.PlayerData.Level >= availability.RequiredLevel;

    /// <summary>
    /// Authored seasonal availability: the permanent base gate plus the published Phantom Clash
    /// season window being open. This answers "does an authored season exist for this account right
    /// now", which the login authorization and the leaderboard key on; the separate functional 10506
    /// unlock condition is enforced by the Phantom Clash request gate
    /// (`HasCondition(Player, UnlockPvpModeConditionId)`), so an account that has not finished the
    /// tutorial is available but not yet admitted to PvP play.
    /// </summary>
    internal static bool IsAvailable(Player player, DateTimeOffset now, out int pvpSeasonId, out int timeId)
    {
        pvpSeasonId = 0;
        timeId = 0;
        if (CurrentAvailability.Value is not Availability availability || !IsBaseAvailable(player))
            return false;
        if (!ActivityScheduleService.IsOpen(availability.PvpTimeId, now))
            return false;
        pvpSeasonId = availability.PvpSeasonId;
        timeId = availability.PvpTimeId;
        return true;
    }

    internal static bool ReconcileAvailability(Player player, DateTimeOffset now) =>
        ReconcileAvailability(player.Theatre6, player, now);

    /// <summary>
    /// Derives activity/season authorization from live inputs (account level, functional chain,
    /// published schedule) onto the supplied document. Called on the staged state by every
    /// mutation, so a level-up or season crossing inside a live connection takes effect on the
    /// very request that observes it; the live document is never touched before the intent write.
    /// </summary>
    internal static bool ReconcileAvailability(Theatre6State state, Player player, DateTimeOffset now)
    {
        if (CurrentAvailability.Value is not Availability availability)
            return false;
        bool changed = false;
        // The base activity is published only while the authored functional gate holds, so a
        // below-level player never keeps a stale authorization.
        int authorizedActivity = IsBaseAvailable(player) ? availability.BaseActivityId : 0;
        if (state.ActivityId != authorizedActivity)
        {
            state.ActivityId = authorizedActivity;
            changed = true;
        }
        bool pvpOpen = IsAvailable(player, now, out int pvpSeasonId, out int timeId);
        int authorizedSeason = pvpOpen ? pvpSeasonId : 0;
        if (state.Pvp.AuthorizedSeasonId != authorizedSeason)
        {
            state.Pvp.AuthorizedSeasonId = authorizedSeason;
            changed = true;
        }
        List<int> authorizedTimes = pvpOpen ? [timeId] : [];
        if (!state.Pvp.AuthorizedTimeIds.SequenceEqual(authorizedTimes))
        {
            state.Pvp.AuthorizedTimeIds = authorizedTimes;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Throws the authored mode error instead of returning a boolean so gameplay paths cannot
    /// silently continue on an unavailable mode. requiredMode enforces the addressed run.
    /// </summary>
    internal static void EnsureAvailable(Session session, int? requiredMode = null)
    {
        Require(IsBaseAvailable(session.player), CodePlayModeNotUnlocked);
        if (requiredMode is int mode)
            Require(session.player.Theatre6.CurrentMode == mode, CodeNoCurrentMode);
    }

    //endregion

    //region state helpers

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

    internal static T Clone<T>(T value) =>
        BsonSerializer.Deserialize<CloneContainer<T>>(new CloneContainer<T> { Value = value }.ToBson()).Value;

    internal static Theatre6State Clone(Theatre6State value) => Clone<Theatre6State>(value);
    internal static Theatre6FileState Clone(Theatre6FileState value) => Clone<Theatre6FileState>(value);
    internal static Theatre6StorySaveState Clone(Theatre6StorySaveState value) => Clone<Theatre6StorySaveState>(value);

    internal static void Require([DoesNotReturnIf(false)] bool condition, int code)
    {
        if (!condition) throw new ServerCodeException("Theatre6 request rejected.", code);
    }

    /// <summary>
    /// The run addressed by the current mode. A settled run stays readable through
    /// allowSettled until the client acknowledges it with SaveFile/GiveUpSaveFile.
    /// </summary>
    internal static Theatre6RunState CurrentRun(Mutation m, bool allowSettled = false)
    {
        int mode = m.State.CurrentMode;
        Require(mode is GamePlayMode or StoryMode, CodeNoCurrentMode);
        Require(m.State.ActiveRuns.TryGetValue(mode, out Theatre6RunState? run), CodeNoCurrentMode);
        Require(allowSettled || !run.Settled, CodeAlreadySettle);
        return run;
    }

    /// <summary>
    /// Persisted RNG. Every random offer/roll in the mode draws from here so a reconnected
    /// client sees the same frozen content instead of a fresh roll.
    /// </summary>
    internal static int Roll(Theatre6RunState run, int maxExclusive)
    {
        Require(maxExclusive > 0, CodeInvalidRequest);
        unchecked
        {
            ulong state = (ulong)run.RandomState;
            // A run that never rolled is seeded from its identity, never from the clock.
            if (state == 0) state = ((ulong)(uint)run.RunId << 32) | 0x9E3779B9UL;
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            run.RandomState = unchecked((long)state);
            return (int)(state % (ulong)maxExclusive);
        }
    }

    //endregion

    //region conditions

    /// <summary>
    /// Authoritative Theatre6/global condition evaluation. Unknown or unevaluable conditions
    /// fail closed (false) instead of throwing, so an unrecognised condition can never unlock.
    /// </summary>
    internal static bool HasCondition(Player player, int conditionId) =>
        ResolveCondition(player, conditionId, 0, out bool value) && value;

    internal static bool HasCondition(Player player, GlobalConditionTable condition) =>
        ResolveConditionRow(player, condition, 0, out bool value) && value;

    private static bool ResolveCondition(Player player, int conditionId, int depth, out bool value)
    {
        value = false;
        if (conditionId <= 0 || depth > 32)
            return false;
        Dictionary<int, GlobalConditionTable> rows = ConditionRows.Value;
        return rows.TryGetValue(conditionId, out GlobalConditionTable? row)
            && ResolveConditionRow(player, row, depth, out value);
    }

    private static readonly Lazy<Dictionary<int, GlobalConditionTable>> ConditionRows =
        new(() => Rows<GlobalConditionTable>().ToDictionary(row => row.Id));

    private static bool ResolveConditionRow(Player player, GlobalConditionTable row, int depth, out bool value)
    {
        value = false;
        if (!string.IsNullOrWhiteSpace(row.Formula))
        {
            bool valid = true;
            int position = 0;
            string formula = row.Formula;
            value = Expression(depth);
            return valid && position == formula.Length;

            bool Expression(int level)
            {
                bool current = Atom(level);
                while (valid && position < formula.Length && formula[position] is '&' or '|')
                {
                    char op = formula[position++];
                    bool right = Atom(level);
                    current = op == '&' ? current && right : current || right;
                }
                return current;
            }

            bool Atom(int level)
            {
                if (!valid || level > 32) { valid = false; return false; }
                while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
                if (position == formula.Length) { valid = false; return false; }
                if (formula[position] == '!')
                {
                    position++;
                    return !Atom(level + 1);
                }
                if (formula[position] == '(')
                {
                    position++;
                    bool nested = Expression(level + 1);
                    while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
                    if (position == formula.Length || formula[position++] != ')') { valid = false; return false; }
                    return nested;
                }
                int start = position;
                while (position < formula.Length && char.IsAsciiDigit(formula[position])) position++;
                if (start == position || !int.TryParse(formula.AsSpan(start, position - start), out int id) || id <= 0)
                {
                    valid = false;
                    return false;
                }
                if (!ResolveCondition(player, id, level + 1, out bool operand))
                {
                    valid = false;
                    return false;
                }
                return operand;
            }
        }

        List<int> p = row.Params;
        Theatre6State state = player.Theatre6;
        switch (row.Type)
        {
            // Commandant level (functional entry gates).
            case 10101 when p.Count >= 1:
                value = player.PlayerData.Level >= p[0];
                return true;
            // Stage clear count: Params[1] is the required number of clears.
            case 23201 when p.Count >= 2:
                value = state.PassStageRecords.GetValueOrDefault(p[0]) >= Math.Max(1, p[1]);
                return true;
            // Difficulty clear count.
            case 23202 when p.Count >= 2:
                value = state.PassDiffRecords.GetValueOrDefault(p[0]) >= Math.Max(1, p[1]);
                return true;
            // Owned archive count. Archive identity is character + slot.
            case 23203 when p.Count >= 1:
                value = state.Files.Select(file => (file.CharacterId, file.SlotId)).Distinct().Count() >= Math.Max(1, p[0]);
                return true;
            // Phantom Clash rank reached in the current season: Params[0] selects "at least
            // this rank", Params[1] is the authored rank row.
            case 23212 when p.Count >= 2 && p[0] == 1 && PvpRanks.Value.ContainsKey(p[1]):
                value = state.Pvp.InitializedSeasonId > 0
                    && state.Pvp.InitializedSeasonId == state.Pvp.AuthorizedSeasonId
                    && state.Pvp.RankId >= p[1];
                return true;
            // Published schedule window.
            case 23001 when p.Count >= 1:
                value = ActivityScheduleService.IsOpen(p[0], DateTimeOffset.UtcNow);
                return true;
            default:
                return false;
        }
    }

    //endregion

    //region transaction

    internal sealed class Mutation
    {
        public Session Session { get; }
        public Theatre6State State { get; }
        /// <summary>
        /// The owning player of the current request. Gameplay state is staged in State; this is
        /// the live cross-document owner used for inventory/condition reads, never for writes.
        /// </summary>
        public Player Player => Session.player;

        internal List<Theatre6PendingPacket> Pushes { get; } = [];
        internal List<Theatre6PendingPacket> AfterResponsePushes { get; } = [];
        internal List<Theatre6PendingRewardGrant> Grants { get; } = [];
        internal List<int> ClaimedTaskIds { get; } = [];
        internal Dictionary<int, int> TaskProgress { get; } = [];
        internal Dictionary<long, int> ShopBuyTimes { get; } = [];
        private int grantOrdinal;

        internal Mutation(Session session, bool newOperation = true)
        {
            Session = session;
            State = Clone(session.player.Theatre6);
            State.PendingMutation = null;
            // Authority is derived from live inputs onto the staged document, so a level-up or a
            // season crossing inside one connection takes effect on this request without touching
            // the live document before the intent is durable.
            ReconcileAvailability(State, session.player, DateTimeOffset.UtcNow);
            if (newOperation) State.NextMutationId = checked(State.NextMutationId + 1);
        }

        public string NextClaimKey() =>
            $"theatre6:{Session.player.PlayerData.Id}:{State.Epoch}:{State.CurrentMode}:{State.NextMutationId}:{grantOrdinal++}";

        public long Balance(int itemId) => checked(Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count)
            + Grants.Where(grant => !Session.inventory.AppliedRewardClaims.Contains(grant.ClaimKey))
                .Sum(grant => grant.Goods.Where(goods => (goods.TemplateId > 0 ? goods.TemplateId : goods.Id) == itemId)
                    .Sum(goods => (long)goods.Count) - grant.Costs.GetValueOrDefault(itemId)));

        public void Cost(int itemId, int amount, int errorCode = CodeInsufficientItem)
        {
            Require(Inventory.IsValidClientItemId(itemId) && amount >= 0, CodeInvalidRequest);
            Require(Balance(itemId) >= amount, errorCode);
            // Prior grants stay intact so their durable claim keys survive this spend.
            if (amount > 0)
                Grant(new RewardGrant(NextClaimKey(), [], new Dictionary<int, int> { [itemId] = amount }));
        }

        public void Grant(int rewardId)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0) throw new InvalidDataException($"Missing Theatre6 reward group {rewardId}.");
            GrantGoods(goods);
        }

        public void GrantGoods(IReadOnlyList<RewardGoodsTable> goods) => Grant(new RewardGrant(NextClaimKey(), goods));

        public void Grant(RewardGrant grant)
        {
            if (string.IsNullOrWhiteSpace(grant.ClaimKey) || grant.ClaimKey.Length > 128
                || Grants.Any(existing => existing.ClaimKey == grant.ClaimKey)
                || (grant.Goods.Count == 0 && grant.Costs is not { Count: > 0 })
                || grant.Goods.Any(goods => goods.Count <= 0)
                || grant.Costs?.Any(cost => cost.Value <= 0 || !Inventory.IsValidClientItemId(cost.Key)) == true)
                throw new InvalidOperationException("Invalid Theatre6 reward grant.");
            Grants.Add(new Theatre6PendingRewardGrant
            {
                ClaimKey = grant.ClaimKey,
                Goods = grant.Goods.Select(goods => new Theatre6PendingGoods
                {
                    Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                    // Params is optional table metadata, not a missing goods/reward fallback.
                    Params = goods.Params?.ToList() ?? []
                }).ToList(),
                Costs = grant.Costs?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [],
                EventCause = grant.EventCause
            });
        }

        public void Push<T>(T payload, bool afterResponse = false) where T : notnull
        {
            Theatre6PendingPacket packet = new()
            {
                Name = payload.GetType().Name,
                Payload = MessagePackSerializer.Serialize(payload.GetType(), payload)
            };
            if (afterResponse) AfterResponsePushes.Add(packet);
            else Pushes.Add(packet);
        }

        public bool IsTaskClaimed(int id) => State.ClaimedTaskIds.Contains(id)
            || ClaimedTaskIds.Contains(id) || Session.player.MissionProgress.ClaimedTaskIds.Contains(id);

        public void MarkTaskClaimed(int id)
        {
            if (IsTaskClaimed(id)) return;
            State.ClaimedTaskIds.Add(id);
            ClaimedTaskIds.Add(id);
        }

        /// <summary>
        /// Current value of a task condition counter, staged increments first. Counters are kept
        /// in two stores on purpose: the conditions of the authored Theatre6 mission groups live
        /// in the mode's permanent ledger, everything else (generic/current-task conditions such
        /// as the authored shop purchase conditions) lives in the shared counter store, which may
        /// be reset per period. Reading one store for the other would either lose a qualifying
        /// increment against a higher baseline or resurrect a value across a reset.
        /// </summary>
        public int GetTaskConditionProgress(int conditionId) => TaskProgress.GetValueOrDefault(
            conditionId, IsModeCondition(conditionId)
                ? State.TaskProgress.GetValueOrDefault(conditionId)
                : Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId));

        public void AddTaskConditionProgress(int conditionId, int amount)
        {
            Require(conditionId > 0 && amount >= 0, CodeInvalidRequest);
            if (amount > 0) TaskProgress[conditionId] = checked(GetTaskConditionProgress(conditionId) + amount);
        }

        public int GetShopBuyTimes(uint goodsId) =>
            ShopBuyTimes.GetValueOrDefault(goodsId, Session.player.ShopBuyTimes.GetValueOrDefault(goodsId));

        public void RecordShopPurchase(uint goodsId, int count)
        {
            Require(goodsId > 0 && count > 0, CodeInvalidRequest);
            ShopBuyTimes[goodsId] = checked(GetShopBuyTimes(goodsId) + count);
        }
    }

    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<TResponse, int> setCode)
        where TRequest : notnull, new() where TResponse : new() =>
        Handle(session, packet, action, setCode, null, null);

    /// <summary>
    /// Stages one request: deserialize once, mutate a clone, persist the intent with
    /// SaveChecked, apply cross-document rewards, then send pushes and the frozen response.
    /// A transport retry with the same semantic body and epoch replays the frozen bytes;
    /// a retry from an earlier epoch or with a different body is rejected.
    /// </summary>
    internal static void Handle<TRequest, TResponse>(Session session, Packet.Request packet,
        Action<Mutation, TRequest, TResponse> action, Action<TResponse, int> setCode,
        Action<Session>? onFailure = null, Action<Session>? afterCommit = null)
        where TRequest : notnull, new() where TResponse : new()
    {
        byte[] responseBytes;
        bool committed = false;
        try
        {
            TRequest request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                ? new() : packet.Deserialize<TRequest>();
            string key = SemanticRequestKey(packet.Name, request);
            if (session.player.Theatre6.PendingMutation is { } pending)
            {
                // A crash left a prepared outcome behind. Only the identical semantic request
                // (or login recovery) may finish it, and it must never be executed twice.
                Require(pending.RequestKey == key && pending.ResponseName == typeof(TResponse).Name
                    && pending.Response is not null, CodeInvalidRequest);
                // The frozen outcome carries the original attempt's receipt. Alias THIS transport
                // id to the same receipt before completing, so a further retry of this id replays
                // the committed response instead of re-running the action against new state.
                Theatre6RequestReceipt? origin = pending.Outcome.RequestReceipts.Values.FirstOrDefault(receipt =>
                    receipt.RequestKey == key && receipt.ResponseName == pending.ResponseName
                    && receipt.MutationId == pending.Outcome.NextMutationId);
                // Receipts are session-scoped: only compare against receipts this same connection
                // stored. A new session's id space is unrelated, and StoreReceipt switches the
                // namespace as it aliases the id, so foreign receipts must not reject the retry.
                bool sameSession = string.Equals(pending.Outcome.ReceiptSessionId, session.id, StringComparison.Ordinal);
                Require(!sameSession || origin is null
                    || (!pending.Outcome.RequestReceipts.TryGetValue(packet.Id, out Theatre6RequestReceipt? existing)
                        || (existing.Epoch == origin.Epoch && existing.MutationId == origin.MutationId
                            && existing.RequestKey == key && existing.ResponseName == origin.ResponseName)), CodeInvalidRequest);
                StoreReceipt(pending.Outcome, session.id, packet.Id, new Theatre6RequestReceipt
                {
                    Epoch = origin?.Epoch ?? pending.Outcome.Epoch,
                    RunId = origin?.RunId ?? pending.Outcome.NextRunId,
                    Mode = origin?.Mode ?? pending.Outcome.CurrentMode,
                    ModeRunId = origin?.ModeRunId ?? RunIdOf(pending.Outcome, pending.Outcome.CurrentMode),
                    MutationId = origin?.MutationId ?? pending.Outcome.NextMutationId,
                    RequestKey = key, ResponseName = pending.ResponseName, Response = pending.Response!
                });
                CompletePending(session);
                SendPushes(session, pending.Pushes);
                responseBytes = pending.Response!;
                committed = true;
                session.SendResponse(typeof(TResponse).Name, responseBytes, packet.Id);
                SendPushes(session, pending.AfterResponsePushes);
                afterCommit?.Invoke(session);
                return;
            }
            if (TryGetReceipt(session, packet.Id, out Theatre6RequestReceipt? receipt))
            {
                // Same transport attempt: replay the frozen bytes, never re-execute. Receipts are
                // session-scoped and packet ids are monotonic per connection, so identity plus the
                // authority epoch are sufficient; Mode/ModeRunId are recorded for diagnostics only
                // (a terminal acknowledgement legitimately clears the run it answered for).
                Require(receipt.Epoch == session.player.Theatre6.Epoch
                    && receipt.RequestKey == key && receipt.ResponseName == typeof(TResponse).Name, CodeInvalidRequest);
                session.SendResponse(typeof(TResponse).Name, receipt.Response, packet.Id);
                return;
            }
            if (IsRetiredAttempt(session.player.Theatre6, session, packet.Id))
            {
                // The attempt was already committed, but its frozen response is no longer cached.
                // Re-running the action could double-apply a non-idempotent change, so the retry is
                // rejected with no mutation of any kind.
                session.SendResponse(typeof(TResponse).Name, Failure(CodeInvalidRequest), packet.Id);
                return;
            }
            TResponse response = new();
            Mutation mutation = new(session);
            action(mutation, request, response);
            responseBytes = MessagePackSerializer.Serialize(response);
            StoreReceipt(mutation.State, session.id, packet.Id, new Theatre6RequestReceipt
            {
                Epoch = mutation.State.Epoch, RunId = mutation.State.NextRunId, MutationId = mutation.State.NextMutationId,
                Mode = mutation.State.CurrentMode, ModeRunId = RunIdOf(mutation.State, mutation.State.CurrentMode),
                RequestKey = key, ResponseName = typeof(TResponse).Name, Response = responseBytes
            });
            Persist(mutation, key, typeof(TResponse).Name, responseBytes);
            SendPushes(session, mutation.Pushes);
            committed = true;
            session.SendResponse(typeof(TResponse).Name, responseBytes, packet.Id);
            SendPushes(session, mutation.AfterResponsePushes);
        }
        catch (ServerCodeException exception) { responseBytes = Failure(exception.Code); }
        catch (MessagePackSerializationException) { responseBytes = Failure(CodeInvalidRequest); }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre6 persistence failed: {exception.Message}");
            responseBytes = Failure(CodeInvalidRequest);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or InvalidOperationException)
        {
            session.log.Error($"Theatre6 request could not be evaluated: {exception.GetType().Name}: {exception.Message}");
            responseBytes = Failure(CodeInvalidRequest);
        }
        if (!committed) session.SendResponse(typeof(TResponse).Name, responseBytes, packet.Id);
        if (committed) afterCommit?.Invoke(session);

        byte[] Failure(int code)
        {
            onFailure?.Invoke(session);
            TResponse response = new();
            setCode(response, code);
            return MessagePackSerializer.Serialize(response);
        }
    }

    private static int RunIdOf(Theatre6State state, int mode) =>
        state.ActiveRuns.TryGetValue(mode, out Theatre6RunState? run) ? run.RunId : 0;

    // NotNullWhen tells the compiler the out value is real exactly when the lookup succeeded, so
    // callers dereference it without a suppression and without a redundant null check.
    private static bool TryGetReceipt(Session session, int packetId, [NotNullWhen(true)] out Theatre6RequestReceipt? receipt)
    {
        receipt = null;
        Theatre6State state = session.player.Theatre6;
        // Packet ids are transport-local and restart with a new connection: a receipt is only
        // meaningful for the session that stored it, otherwise a fresh request would be
        // answered with a stale frozen body.
        if (!string.Equals(state.ReceiptSessionId, session.id, StringComparison.Ordinal))
            return false;
        return state.RequestReceipts.TryGetValue(packetId, out receipt);
    }

    // Bounded replay window: the frozen response of the most recent transport attempts is kept so
    // an exact retry replays byte-for-byte, while the persisted high-water id guarantees that an
    // attempt retired from the window is REJECTED, never re-executed (re-running a non-idempotent
    // action is worse than a rejection). Both bounds keep the player document well inside the
    // 16 MiB BSON limit even when a request's response is a full mode snapshot.
    internal const int ReceiptWindowCount = 128;
    internal const int ReceiptWindowBytes = 256 * 1024;

    private static void StoreReceipt(Theatre6State state, string sessionId, int packetId, Theatre6RequestReceipt receipt)
    {
        if (!string.Equals(state.ReceiptSessionId, sessionId, StringComparison.Ordinal))
        {
            // A new connection starts a new transport id space; the previous session's frozen
            // responses are unreachable and are dropped with the namespace.
            state.RequestReceipts.Clear();
            state.ReceiptSessionId = sessionId;
            state.ReceiptHighWaterId = -1;
        }
        state.RequestReceipts[packetId] = receipt;
        state.ReceiptHighWaterId = Math.Max(state.ReceiptHighWaterId, packetId);
        TrimReceipts(state);
    }

    private static void TrimReceipts(Theatre6State state)
    {
        long bytes = state.RequestReceipts.Values.Sum(value => (long)value.Response.Length);
        while (state.RequestReceipts.Count > 1
            && (state.RequestReceipts.Count > ReceiptWindowCount || bytes > ReceiptWindowBytes))
        {
            // Packet ids are monotone per connection, so the smallest id is the oldest attempt.
            KeyValuePair<int, Theatre6RequestReceipt> oldest = state.RequestReceipts.MinBy(pair => pair.Key);
            bytes -= oldest.Value.Response.Length;
            state.RequestReceipts.Remove(oldest.Key);
        }
    }

    /// <summary>
    /// True when the attempt must be rejected without executing anything: the id is negative
    /// (never a valid transport id), or it is at or below the session's high-water mark while its
    /// frozen response is no longer cached. There is deliberately no distance heuristic —
    /// request.Id is not authenticated, so a same-connection id-space restart is indistinguishable
    /// from a stale retry and must be rejected rather than re-executed. Only a new authenticated
    /// Session.id opens a new id space.
    /// </summary>
    private static bool IsRetiredAttempt(Theatre6State state, Session session, int packetId)
    {
        if (packetId < 0)
            return true;
        return string.Equals(state.ReceiptSessionId, session.id, StringComparison.Ordinal)
            && state.ReceiptHighWaterId >= 0
            && packetId <= state.ReceiptHighWaterId;
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

    /// <summary>
    /// True when the request may proceed while a Theatre6 operation is pending. Shared routes
    /// are matched by typed body so an unrelated request cannot be mistaken for the retry;
    /// mode-only routes are validated by Handle itself.
    /// </summary>
    internal static bool CanDispatchPendingRequest(Session session, Packet.Request packet)
    {
        if (session.player.Theatre6.PendingMutation is not { } pending) return true;
        if (!pending.RequestKey.StartsWith(packet.Name + ":", StringComparison.Ordinal)) return false;
        try
        {
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

    private static void Persist(Mutation mutation, string requestKey, string responseName, byte[]? response)
    {
        Session session = mutation.Session;
        Theatre6State previous = session.player.Theatre6;
        if (previous.PendingMutation is not null)
            throw new InvalidOperationException("Theatre6 recovery must finish before persistence.");
        previous.PendingMutation = new Theatre6PendingMutation
        {
            RequestKey = requestKey, Outcome = mutation.State, Grants = mutation.Grants,
            Pushes = mutation.Pushes, AfterResponsePushes = mutation.AfterResponsePushes,
            ResponseName = responseName, Response = response,
            ClaimedTaskIds = mutation.ClaimedTaskIds, TaskProgress = mutation.TaskProgress,
            ShopBuyTimes = mutation.ShopBuyTimes
        };
        try { session.player.SaveChecked(); }
        catch { previous.PendingMutation = null; throw; }
        CompletePending(session);
    }

    private static void CompletePending(Session session)
    {
        Theatre6State previous = session.player.Theatre6;
        Theatre6PendingMutation pending = previous.PendingMutation
            ?? throw new InvalidOperationException("No pending Theatre6 operation.");
        ApplyGrants(session, pending.Grants).SendPushes(session);
        List<int> oldClaims = session.player.MissionProgress.ClaimedTaskIds;
        Dictionary<int, int> oldCounters = session.player.MissionProgress.ConditionCounters;
        Dictionary<uint, int> oldShopBuyTimes = session.player.ShopBuyTimes;
        if (pending.ClaimedTaskIds.Count > 0)
            session.player.MissionProgress.ClaimedTaskIds = oldClaims.Concat(pending.ClaimedTaskIds).Distinct().ToList();
        if (pending.TaskProgress.Count > 0)
        {
            // Generic (resettable) conditions commit to the shared counter store; the mode's own
            // permanent mission counters commit to the mode ledger below. Never both.
            session.player.MissionProgress.ConditionCounters = new(oldCounters);
            foreach ((int id, int count) in pending.TaskProgress)
            {
                if (IsModeCondition(id)) continue;
                session.player.MissionProgress.ConditionCounters[id] = Math.Max(
                    session.player.MissionProgress.ConditionCounters.GetValueOrDefault(id), count);
            }
        }
        Dictionary<uint, int> shopBuyTimes = oldShopBuyTimes;
        if (pending.ShopBuyTimes.Count > 0)
        {
            // Purchase limits are shared with the global shop catalogue; record the attempt count.
            shopBuyTimes = new(oldShopBuyTimes);
            foreach ((long goodsId, int count) in pending.ShopBuyTimes)
            {
                uint id = checked((uint)goodsId);
                shopBuyTimes[id] = Math.Max(shopBuyTimes.GetValueOrDefault(id), count);
            }
            session.player.ShopBuyTimes = shopBuyTimes;
        }
        session.player.Theatre6 = pending.Outcome;
        session.player.Theatre6.PendingMutation = null;
        foreach ((int id, int count) in pending.TaskProgress)
        {
            if (!IsModeCondition(id)) continue;
            session.player.Theatre6.TaskProgress[id] = Math.Max(
                session.player.Theatre6.TaskProgress.GetValueOrDefault(id), count);
        }
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Theatre6 = previous;
            session.player.MissionProgress.ClaimedTaskIds = oldClaims;
            session.player.MissionProgress.ConditionCounters = oldCounters;
            session.player.ShopBuyTimes = oldShopBuyTimes;
            throw;
        }
    }

    private static RewardApplicationResult ApplyGrants(Session session, List<Theatre6PendingRewardGrant> grants) =>
        grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants.Select(grant => new RewardGrant(
            grant.ClaimKey, grant.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params.ToList()
            }).ToList(), grant.Costs, grant.EventCause)).ToList(), session);

    /// <summary>
    /// Login recovery. forLogin:true finishes an abandoned operation before login accounting
    /// saves the player document. Otherwise only a shared task claim may be finished here,
    /// because a live snapshot would destroy client-held run objects.
    /// </summary>
    internal static void ResumePending(Session session, bool forLogin = false)
    {
        if (session.player.Theatre6.PendingMutation is not { } pending) return;
        if (forLogin)
        {
            CompletePending(session);
            return;
        }
        Require(pending.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse), CodeInvalidRequest);
        CompletePending(session);
        SendPushes(session, pending.Pushes);
        SendPushes(session, pending.AfterResponsePushes);
        TaskModule.SendTaskSync(session);
    }

    /// <summary>Login-side repair: provenance of every stale field is derived, never defaulted.</summary>
    internal static void PrepareLogin(Session session)
    {
        Theatre6State original = session.player.Theatre6;
        bool changed = ReconcileAvailability(session.player, DateTimeOffset.UtcNow);
        Theatre6State state = Clone(session.player.Theatre6);
        changed |= Normalize(state);
        if (!changed) return;
        session.player.Theatre6 = state;
        try { session.player.SaveChecked(); }
        catch { session.player.Theatre6 = original; throw; }
    }

    private static bool Normalize(Theatre6State state)
    {
        bool changed = false;
        foreach (int mode in state.ActiveRuns.Keys.Where(mode => mode is not (GamePlayMode or StoryMode)).ToList())
        {
            state.ActiveRuns.Remove(mode);
            changed = true;
        }
        foreach (int mode in state.Settlements.Keys.Where(mode => mode is not (GamePlayMode or StoryMode)).ToList())
        {
            state.Settlements.Remove(mode);
            changed = true;
        }
        int nextRunId = Math.Max(1, state.NextRunId);
        foreach ((int mode, Theatre6RunState run) in state.ActiveRuns)
        {
            if (run.RunId <= 0)
            {
                // Documents written before runs carried identity get one derived from the
                // durable counter so per-run reward claims stay unique.
                run.RunId = nextRunId++;
                changed = true;
            }
            nextRunId = Math.Max(nextRunId, run.RunId + 1);
            if (run.ModeId != mode)
            {
                run.ModeId = mode;
                changed = true;
            }
            if (!run.Settled || run.SettleData is not null || !state.Settlements.TryGetValue(mode, out Theatre6SettlementState? settlement))
                continue;
            run.SettleData = BuildSettleData(settlement);
            changed = true;
        }
        if (state.NextRunId != nextRunId)
        {
            state.NextRunId = nextRunId;
            changed = true;
        }
        if (state.CurrentMode is not (0 or GamePlayMode or StoryMode))
        {
            state.CurrentMode = 0;
            changed = true;
        }
        return changed;
    }

    private static void SendPushes(Session session, IEnumerable<Theatre6PendingPacket> pushes)
    {
        foreach (Theatre6PendingPacket push in pushes)
            session.SendPush(push.Name, push.Payload);
    }

    //endregion

    //region snapshots

    internal static NotifyTheatre6ActivityData? BuildNotify(Player player) => BuildNotify(player, DateTimeOffset.UtcNow);

    /// <summary>
    /// Pushes that must follow the login activity snapshot. An interrupted star-up choice is
    /// reopened so the client cannot lose buff-directed upgrade chances across a relog.
    /// Called by the login sequence right after NotifyTheatre6ActivityData.
    /// </summary>
    internal static void PushLoginExtras(Session session)
    {
        foreach (Theatre6RunState run in session.player.Theatre6.ActiveRuns.Values.OrderBy(run => run.ModeId))
        {
            List<Theatre6LiveBuffData> pending = PendingSkillUpBuffs(run);
            if (pending.Count > 0)
                session.SendPush(new NotifyTheatre6SkillUpEffect { BuffDatas = pending });
        }
    }

    internal static NotifyTheatre6ActivityData? BuildNotify(Player player, DateTimeOffset now)
    {
        Theatre6State state = player.Theatre6;
        if (CurrentAvailability.Value is not Availability availability
            || !IsBaseAvailable(player)
            || state.ActivityId != availability.BaseActivityId
            || !Activities.Value.ContainsKey(state.ActivityId))
            return null;

        Theatre6RunState? gamePlay = state.ActiveRuns.GetValueOrDefault(GamePlayMode);
        Theatre6RunState? story = state.ActiveRuns.GetValueOrDefault(StoryMode);
        Theatre6StoryModeSaveDb storySave = ToWire(state.StorySave);
        return new NotifyTheatre6ActivityData
        {
            ActivityId = state.ActivityId,
            CurrentMode = state.CurrentMode,
            PlayModeDataDb = gamePlay is null ? null : BuildModeData(gamePlay),
            StoryModeDataDb = story is null ? null : BuildModeData(story),
            // Archive identity is character + slot, so every slot of every character is sent.
            FileDatas = state.Files
                .Where(file => file.CharacterId > 0 && file.SlotId >= 1 && file.SlotId <= MaxSaveFiles.Value)
                .OrderBy(file => file.CharacterId).ThenBy(file => file.SlotId)
                .Select(ToWire).ToList(),
            StoryLineDatas = storySave.StoryLineDatas,
            PassStageId = state.PassStageRecords.Keys.Order().ToList(),
            CharacterIds = UnlockedCharacterIds(player),
            PlayModeSaveDb = new Theatre6PlayModeSaveDb
            {
                StoryIds = state.PlaySave.StoryIds.ToList(),
                TalentLevel = state.PlaySave.TalentLevel,
                TalentExp = state.PlaySave.TalentExp
            },
            StoryModeSaveDb = storySave,
            PassStageRecords = new(state.PassStageRecords),
            PassDiffRecords = new(state.PassDiffRecords)
        };
    }

    // Characters are unlocked by their authored ConditionId; a locked character is never listed.
    private static List<int> UnlockedCharacterIds(Player player) => Characters.Value.Values
        .Where(row => row.ConditionId is not int conditionId || conditionId <= 0 || HasCondition(player, conditionId))
        .Select(row => row.Id).Distinct().Order().ToList();

    internal static Theatre6ModeDataDb BuildModeData(Theatre6RunState run) => new()
    {
        ModeId = run.ModeId,
        StageId = run.StageId,
        CharacterId = run.File.CharacterId,
        FashionId = run.File.FashionId,
        DifficultyId = run.DifficultyId,
        StoryLineId = run.StoryLineId,
        CurFloorIdx = run.CurFloorIdx,
        CurrentRoomDataDb = run.CurrentRoomDataDb,
        BossRoomDataDb = run.BossRoomDataDb,
        San = run.CurSan,
        MinSan = run.MinSan,
        MaxSan = run.MaxSan,
        Health = run.CurHealth,
        MaxHealth = run.MaxHealth,
        InitHealth = run.InitHealth,
        GoldAmount = run.GoldAmount,
        ScoreTotal = run.ScoreTotal,
        Goods = run.Goods.Values.OrderBy(goods => goods.GoodsId).ToList(),
        Attrs = BuildAttrs(run.File.Attrs),
        AttrPacks = run.File.AttrPacks
            .GroupBy(pack => pack.PackId)
            .ToDictionary(group => group.Key, group => new Theatre6AttrPackData { PackId = group.Key, Num = group.Last().Num }),
        Skills = run.File.Skills.Select(skill => new Theatre6SkillData
        {
            SlotType = skill.SlotType, Position = skill.Position, SkillId = skill.SkillId
        }).ToList(),
        SkillOverQueue = run.SkillOverQueue.ToList(),
        Buffs = run.Buffs.ToDictionary(pair => pair.Key, pair => ToWireLiveBuff(pair.Value)),
        DestroyedBuffs = run.DestroyedBuffs.ToDictionary(pair => pair.Key, pair => ToWireLiveBuff(pair.Value)),
        Bgms = run.Bgms.ToDictionary(pair => pair.Key, pair => new Theatre6BgmData
        {
            CueId = pair.Value.CueId, Priority = pair.Value.Priority
        }),
        MessyCodes = new(run.MessyCodes),
        CurCodeId = run.CurCodeId,
        FloorBuffUid = run.FloorBuffUid.ToList(),
        StageTasks = run.StageTasks.ToDictionary(pair => pair.Key, pair => pair.Value),
        TaskSlotData = run.TaskSlotData.ToList(),
        TaskGroupId = run.TaskGroupId,
        WaitingExFloorConfirm = run.WaitingExFloorConfirm,
        HasClearedBeforeExFloor = run.HasClearedBeforeExFloor,
        IsSettle = run.Settled,
        SettleData = run.SettleData
    };

    // The client indexes Attrs by list position (ipairs index == AttrId) and only replaces
    // entries whose position exists, so the list must stay positionally aligned.
    private static List<Theatre6AttrData> BuildAttrs(List<Theatre6AttrState> attrs)
    {
        int maxId = attrs.Where(attr => attr.AttrId > 0).Select(attr => attr.AttrId).DefaultIfEmpty(0).Max();
        Dictionary<int, Theatre6AttrState> byId = attrs.GroupBy(attr => attr.AttrId).ToDictionary(group => group.Key, group => group.Last());
        List<Theatre6AttrData> wire = new(maxId);
        for (int id = 1; id <= maxId; id++)
            wire.Add(new Theatre6AttrData { AttrId = id, Value = byId.TryGetValue(id, out Theatre6AttrState? attr) ? attr.Value : 0 });
        return wire;
    }

    internal static Theatre6FileData ToWire(Theatre6FileState file) => new()
    {
        SlotId = file.SlotId,
        CharacterId = file.CharacterId,
        Score = file.Score,
        BuildTags = file.BuildTags.ToList(),
        FashionId = file.FashionId,
        Attrs = file.Attrs.Select(attr => new Theatre6AttrData { AttrId = attr.AttrId, Value = attr.Value }).ToList(),
        Skills = file.Skills.Select(skill => new Theatre6SkillData { SlotType = skill.SlotType, Position = skill.Position, SkillId = skill.SkillId }).ToList(),
        AttrPacks = file.AttrPacks.Select(pack => new Theatre6AttrPackData { PackId = pack.PackId, Num = pack.Num }).ToList(),
        Buffs = file.Buffs.Select(buff => new Theatre6BuffData { BuffId = buff.BuffId, TriggerCount = buff.TriggerCount, AddMagic = buff.AddMagic }).ToList()
    };

    internal static Theatre6StoryModeSaveDb ToWire(Theatre6StorySaveState save) => new()
    {
        StoryIds = save.StoryIds.ToList(),
        StoryLineDatas = save.StoryLineDatas.Select(line => new Theatre6StoryLineData
        {
            StoryLineId = line.StoryLineId,
            StageIndex = line.StageIndex,
            IsCompletedBefore = line.IsCompletedBefore,
            IsBuy = line.IsBuy,
            BuyIndex = line.BuyIndex.ToList()
        }).ToList()
    };

    private static Theatre6SettleData BuildSettleData(Theatre6SettlementState settlement) => new()
    {
        IsWin = settlement.IsWin,
        FileData = ToWire(settlement.File),
        CurHeath = settlement.CurHealth,
        MaxHeath = settlement.MaxHealth,
        CurSan = settlement.CurSan,
        MaxSan = settlement.MaxSan,
        FightRecords = settlement.Fights.Select(record => new Theatre6FightRecord
        {
            DifficultyType = record.DifficultyType, FightResultType = record.FightResultType,
            FightId = record.FightId, MonsterId = record.MonsterId
        }).ToList(),
        RewardList = settlement.Rewards?.Select(ToWireReward).ToList(),
        PassStageRecords = new(settlement.PassStageRecords),
        PassDiffRecords = new(settlement.PassDiffRecords)
    };

    // The settlement UI consumes a generic reward object (TemplateId/Count), while the durable
    // record keeps the legacy id/type/count/is_first elements plus the granted item identity.
    private static RewardGoods ToWireReward(Theatre6SettleRewardState reward) => new()
    {
        RewardType = reward.RewardType != 0 ? reward.RewardType : reward.Type,
        TemplateId = reward.TemplateId != 0 ? reward.TemplateId : reward.Id,
        Id = reward.Id,
        Count = reward.Count
    };

    private static Theatre6SettleRewardState ToStateReward(RewardGoods goods, bool isFirst) => new()
    {
        Id = goods.Id,
        TemplateId = goods.TemplateId,
        RewardType = goods.RewardType,
        Type = goods.RewardType,
        Count = goods.Count,
        IsFirst = isFirst
    };

    //endregion

    //region settlement

    /// <summary>
    /// Freezes the terminal state of one run: archive build score/tags, story and pass
    /// progression, exactly-once settlement rewards and permanent talent growth. The run is
    /// retained (Settled + SettleData) until the client acknowledges it with Save/GiveUp.
    /// </summary>
    internal static Theatre6SettleData FinalizeRun(Mutation m, Theatre6RunState run, bool isWin)
    {
        Theatre6State state = m.State;
        Require(run.ModeId is GamePlayMode or StoryMode, CodeNoCurrentMode);
        if (run.Settled && state.Settlements.TryGetValue(run.ModeId, out Theatre6SettlementState? frozen))
            return BuildSettleData(frozen);
        Require(!run.Settled, CodeAlreadySettle);
        Require(TryFinalize(run, out Theatre6SettlementState? settled), CodeArchiveData);

        settled!.IsWin = isWin;
        settled.RunId = run.RunId;
        settled.Epoch = state.Epoch;
        // Rewards are frozen here: the granted goods are what the terminal response replays.
        bool firstClear = !state.PassStageRecords.ContainsKey(run.StageId);
        MergeStory(state.StorySave, settled.StorySave);
        MergeMax(state.PassStageRecords, settled.PassStageRecords);
        MergeMax(state.PassDiffRecords, settled.PassDiffRecords);
        settled.StorySave = Clone(state.StorySave);
        settled.PassStageRecords = new(state.PassStageRecords);
        settled.PassDiffRecords = new(state.PassDiffRecords);
        run.StorySave = Clone(state.StorySave);
        run.PassStageRecords = new(state.PassStageRecords);
        run.PassDiffRecords = new(state.PassDiffRecords);
        run.IsWin = isWin;
        run.Settled = true;

        List<RewardGoods> granted = GrantSettlementRewards(m, run, settled);
        settled.Rewards = granted.Select(goods => ToStateReward(goods, firstClear)).ToList();
        run.Rewards = settled.Rewards.Select(reward => Clone(reward)).ToList();
        // Permanent growth: the granted authored growth currency advances the talent level, and
        // the client is told the new level/exp in the same operation.
        int growth = granted.Where(goods => goods.TemplateId == Cfg("TalentCoin")).Sum(goods => goods.Count);
        if (growth > 0 && AddTalentExp(state, growth))
            m.Push(new NotifyTheatre6TalentLevel { Level = state.PlaySave.TalentLevel, Exp = state.PlaySave.TalentExp });
        state.Settlements[run.ModeId] = settled;
        run.SettleData = BuildSettleData(settled);
        return run.SettleData;
    }

    /// <summary>
    /// AscNet policy for the authored but unproven score-to-currency composition of
    /// Theatre6Stage: each slot grants its authored item when the run score reaches
    /// MinScores[i], the quantity is ScoreProps[i] score per unit capped by MaxRewards[i]
    /// (a slot without a score operand grants the authored cap directly), plus AddRewards[i].
    /// Both authored currencies keep their authored roles: the granted goods reach the bag and
    /// the settlement UI, and the growth currency (Config.TalentCoin) additionally advances the
    /// permanent talent level, because the client has no request that spends it. Reward identity
    /// is frozen per (epoch, run, item) so a crash retry cannot double grant.
    /// </summary>
    private static List<RewardGoods> GrantSettlementRewards(Mutation m, Theatre6RunState run, Theatre6SettlementState settlement)
    {
        if (!Stages.Value.TryGetValue(run.StageId, out Theatre6StageTable? stage))
            throw new InvalidDataException($"Missing Theatre6 stage {run.StageId}.");
        int score = Math.Max(0, run.ScoreTotal);
        List<RewardGoods> granted = [];
        for (int slot = 0; slot < stage.RewardIds.Count; slot++)
        {
            int itemId = stage.RewardIds[slot];
            if (itemId <= 0) continue;
            int scoreProp = ValueAt(stage.ScoreProps, slot);
            int minScore = ValueAt(stage.MinScores, slot);
            int maxReward = ValueAt(stage.MaxRewards, slot);
            int addReward = ValueAt(stage.AddRewards, slot);
            if (minScore > 0 && score < minScore) continue;
            int units = scoreProp > 0 ? score / scoreProp : maxReward;
            int count = (maxReward > 0 ? Math.Min(units, maxReward) : units) + addReward;
            if (count <= 0) continue;

            RewardGoodsTable goods = new() { Id = itemId, TemplateId = itemId, Count = count };
            RewardType? type = RewardHandler.GetRewardType(goods);
            if (type is null) throw new InvalidDataException($"Theatre6 stage {run.StageId} grants unsupported item {itemId}.");
            m.Grant(new RewardGrant(
                $"theatre6-settle:{m.Session.player.PlayerData.Id}:{settlement.Epoch}:{run.RunId}:{itemId}", [goods]));
            granted.Add(new RewardGoods { Id = itemId, TemplateId = itemId, Count = count, RewardType = (int)type.Value });
        }
        return granted;
    }

    private static int ValueAt(List<int> values, int index) => index < values.Count ? values[index] : 0;

    /// <summary>
    /// Permanent growth. The authored growth currency (Config.TalentCoin) advances the
    /// talent level through Theatre6Talent thresholds; the client reads level/exp directly
    /// and pushes them back with NotifyTheatre6TalentLevel.
    /// </summary>
    private static bool AddTalentExp(Theatre6State state, int exp)
    {
        if (exp <= 0) return false;
        List<Theatre6TalentTable> levels = Talents.Value;
        if (levels.Count == 0) return false;
        int maxLevel = levels[^1].Level;
        Theatre6PlaySaveState save = state.PlaySave;
        int level = Math.Clamp(save.TalentLevel, 0, maxLevel);
        long current = save.TalentExp + (long)exp;
        while (level < maxLevel)
        {
            Theatre6TalentTable? next = levels.FirstOrDefault(row => row.Level == level + 1);
            if (next is null || current < next.NextExp) break;
            current -= next.NextExp;
            level++;
        }
        if (level >= maxLevel)
            current = Math.Min(current, levels[^1].NextExp);
        save.TalentLevel = level;
        save.TalentExp = (int)Math.Clamp(current, 0, int.MaxValue);
        return true;
    }

    private static void MergeStory(Theatre6StorySaveState target, Theatre6StorySaveState source)
    {
        foreach (int id in source.StoryIds.Where(id => id > 0 && !target.StoryIds.Contains(id)))
            target.StoryIds.Add(id);
        foreach (Theatre6StoryLineState line in source.StoryLineDatas)
        {
            Theatre6StoryLineState? existing = target.StoryLineDatas.FirstOrDefault(row => row.StoryLineId == line.StoryLineId);
            if (existing is null)
            {
                target.StoryLineDatas.Add(Clone(line));
                continue;
            }
            existing.StageIndex = Math.Max(existing.StageIndex, line.StageIndex);
            existing.IsCompletedBefore |= line.IsCompletedBefore;
            existing.IsBuy |= line.IsBuy;
            foreach (int index in line.BuyIndex.Where(index => !existing.BuyIndex.Contains(index)))
                existing.BuyIndex.Add(index);
        }
    }

    private static void MergeMax(Dictionary<int, int> target, Dictionary<int, int> source)
    {
        foreach ((int key, int value) in source)
            target[key] = target.TryGetValue(key, out int old) ? Math.Max(old, value) : value;
    }

    internal static bool TryFinalize(Theatre6RunState run, out Theatre6SettlementState? result)
    {
        result = null;
        Theatre6FileState file = Clone(run.File);
        file.SlotId = 0;
        if (!Characters.Value.TryGetValue(file.CharacterId, out Theatre6CharacterTable? character)
            // Theatre6Character authors exactly one FashionIds column, so the generated member is
            // the scalar authored fashion of that character.
            || character.FashionIds != file.FashionId
            || !Fashions.Value.ContainsKey(file.FashionId))
            return false;

        const int activeSkillSlotType = 2;
        const int insertSkillSlotType = 3;
        const int specialSkillSlotType = 1;
        const int bagSkillSlotType = 4;
        Dictionary<int, int> slotCaps = new()
        {
            [specialSkillSlotType] = 1,
            [activeSkillSlotType] = Cfg("ActiveSkillSlotLimit"),
            [insertSkillSlotType] = Cfg("InsertSkillSlotLimit"),
            [bagSkillSlotType] = Cfg("SkillBagSlotLimit")
        };
        long score = 0;
        HashSet<int> tags = new();
        HashSet<int> attrIds = new();
        foreach (Theatre6AttrState attr in file.Attrs)
        {
            if (!attrIds.Add(attr.AttrId)
                || !Attrs.Value.TryGetValue(attr.AttrId, out Theatre6AttrTable? attrRow)
                || attr.Value < attrRow.Min
                || attr.Value > attrRow.Max)
                return false;
            score = checked(score + (long)attr.Value * (attrRow.SaveScore ?? 0));
        }
        HashSet<(int SlotType, int Position)> slots = new();
        foreach (Theatre6SkillState skill in file.Skills)
        {
            if (!slotCaps.TryGetValue(skill.SlotType, out int cap)
                || skill.Position < 1
                || skill.Position > cap
                || !slots.Add((skill.SlotType, skill.Position))
                || !Skills.Value.TryGetValue(skill.SkillId, out Theatre6SkillTable? skillRow)
                || (skillRow.Character > 0 && skillRow.Character != file.CharacterId))
                return false;
            if (skill.SlotType != bagSkillSlotType)
            {
                score = checked(score + skillRow.SaveScore);
                tags.UnionWith(skillRow.BuildTags);
            }
        }
        // A base active skill contributes per EMPTY active position, never by equipped count: a
        // legal move can leave a hole before a filled slot, so selection is by position. This is
        // the same rule as Economy's RecalculateScore and the client's saved-build score.
        HashSet<int> occupiedActivePositions = file.Skills
            .Where(skill => skill.SlotType == activeSkillSlotType)
            .Select(skill => skill.Position)
            .ToHashSet();
        for (int position = 1; position <= character.BaseSkill.Count; position++)
        {
            int baseSkill = character.BaseSkill[position - 1];
            if (baseSkill <= 0 || occupiedActivePositions.Contains(position))
                continue;
            if (!Skills.Value.TryGetValue(baseSkill, out Theatre6SkillTable? skillRow)) return false;
            score = checked(score + skillRow.SaveScore);
            tags.UnionWith(skillRow.BuildTags);
        }
        HashSet<int> packIds = new();
        foreach (Theatre6AttrPackState pack in file.AttrPacks)
        {
            if (!packIds.Add(pack.PackId)
                || !AttrPacks.Value.TryGetValue(pack.PackId, out Theatre6AttrPackTable? packRow)
                || pack.Num < 0
                || pack.Num > packRow.LimitCount
                || (packRow.Character > 0 && packRow.Character != file.CharacterId))
                return false;
            score = checked(score + (long)pack.Num * (packRow.SaveScore ?? 0));
            tags.UnionWith(packRow.BuildTags);
        }
        if (file.Buffs.Any(buff => buff.TriggerCount < 0 || buff.TriggerCount > Cfg("BuffMaxTriggerCount") || !Buffs.Value.Contains(buff.BuffId))
            || tags.Any(tag => !BuildTags.Value.Contains(tag))
            || score > int.MaxValue)
            return false;

        file.Score = checked((int)score);
        file.BuildTags = tags.Order().ToList();
        result = new Theatre6SettlementState
        {
            ModeId = run.ModeId,
            IsWin = run.IsWin,
            File = file,
            CurHealth = run.CurHealth,
            MaxHealth = run.MaxHealth,
            CurSan = run.CurSan,
            MaxSan = run.MaxSan,
            Fights = run.Fights.Select(record => Clone(record)).ToList(),
            StorySave = Clone(run.StorySave),
            PassStageRecords = new(run.PassStageRecords),
            PassDiffRecords = new(run.PassDiffRecords)
        };
        return true;
    }

    //endregion

    //region core handlers

    [RequestPacketHandler("Theatre6EndGameRequest")]
    public static void EndGame(Session session, Packet.Request packet) =>
        Handle<Theatre6EndGameRequest, Theatre6EndGameResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Require(request.ModeId is GamePlayMode or StoryMode, CodeNoCurrentMode);
            Theatre6SettleData settle;
            if (m.State.ActiveRuns.TryGetValue(request.ModeId, out Theatre6RunState? run))
                settle = FinalizeRun(m, run, run.IsWin);
            else
            {
                Require(m.State.Settlements.TryGetValue(request.ModeId, out Theatre6SettlementState? settled), CodeNoCurrentMode);
                settle = BuildSettleData(settled);
            }
            response.SettleData = settle;
            response.StoryModeSaveDb = ToWire(m.State.StorySave);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6SaveFileRequest")]
    public static void SaveFile(Session session, Packet.Request packet) =>
        Handle<Theatre6SaveFileRequest, Theatre6SaveFileResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Require(request.SlotId >= 1 && request.SlotId <= MaxSaveFiles.Value, CodeSlotId);
            Require(m.State.Settlements.TryGetValue(request.ModeId, out Theatre6SettlementState? settlement), CodeArchiveFileData);
            Theatre6FileState archived = Clone(settlement.File);
            archived.SlotId = request.SlotId;
            // Archive identity is the character + slot pair the client shows in its archive UI.
            int index = m.State.Files.FindIndex(file => file.CharacterId == archived.CharacterId && file.SlotId == request.SlotId);
            if (index >= 0) m.State.Files[index] = archived;
            else m.State.Files.Add(archived);
            AcknowledgeTerminal(m, request.ModeId);
            response.FileData = ToWire(archived);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6GiveUpSaveFileRequest")]
    public static void GiveUpSaveFile(Session session, Packet.Request packet) =>
        Handle<Theatre6GiveUpSaveFileRequest, Theatre6GiveUpSaveFileResponse>(session, packet, (m, request, _) =>
        {
            EnsureAvailable(session);
            Require(m.State.ActiveRuns.ContainsKey(request.ModeId) || m.State.Settlements.ContainsKey(request.ModeId), CodeNoCurrentMode);
            AcknowledgeTerminal(m, request.ModeId);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ContinueGameRequest")]
    public static void ContinueGame(Session session, Packet.Request packet) =>
        Handle<Theatre6ContinueGameRequest, Theatre6ContinueGameResponse>(session, packet, (m, request, _) =>
        {
            EnsureAvailable(session);
            Require(request.ModeId is GamePlayMode or StoryMode && m.State.ActiveRuns.ContainsKey(request.ModeId), CodeNoCurrentMode);
            m.State.CurrentMode = request.ModeId;
            // The response carries no state: the client rebuilds the chain from the snapshot it
            // already holds, so the mode switch is announced as a post-response push.
            m.Push(new NotifyTheatre6ModeChange { ModeId = request.ModeId }, afterResponse: true);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6StoryModeGuideFinishedRequest")]
    public static void StoryModeGuideFinished(Session session, Packet.Request packet) =>
        Handle<Theatre6StoryModeGuideFinishedRequest, Theatre6StoryModeGuideFinishedResponse>(session, packet, (m, request, _) =>
        {
            EnsureAvailable(session);
            Require(StoryDetails.Value.ContainsKey(request.StoryId), CodeStoryId);
            // The client records guide ids in the story-mode save; keep the same target so a
            // relog does not replay an already watched guide.
            if (!m.State.StorySave.StoryIds.Contains(request.StoryId))
                m.State.StorySave.StoryIds.Add(request.StoryId);
        }, static (response, code) => response.Code = code);

    /// <summary>
    /// Terminal acknowledgement: the client keeps the run and its frozen settlement until it
    /// saves the archive or gives up, then clears that mode. Rewards are already durable.
    /// </summary>
    private static void AcknowledgeTerminal(Mutation m, int modeId)
    {
        m.State.ActiveRuns.Remove(modeId);
        m.State.Settlements.Remove(modeId);
        if (m.State.CurrentMode == modeId)
            m.State.CurrentMode = 0;
    }

    //endregion

    //region shared permanent mission group

    private static readonly Lazy<List<TaskTimeLimitRow>> MetaTaskGroups = new(() =>
    {
        // Theatre6Reward is the authored mission/shop catalogue: rows carrying TaskTimeLimitId
        // are the permanent mission groups, rows carrying ShopId are reward shops.
        List<int> groupIds = Rows<Theatre6RewardTable>().Where(row => row.TaskTimeLimitId is > 0)
            .Select(row => row.TaskTimeLimitId!.Value).Distinct().ToList();
        return Rows<TaskTimeLimitRow>().Where(row => groupIds.Contains(row.Id)).ToList();
    });

    private static readonly Lazy<Dictionary<int, TaskRow>> MetaTasks = new(() =>
    {
        HashSet<int> ids = MetaTaskGroups.Value.SelectMany(row => row.TaskId).ToHashSet();
        // Every task authored in a Theatre6Reward group belongs to this mode, whatever its
        // global Task.Type is: the permanent group is 707 (whose rows are Type 108) and 708/735
        // carry the limited groups. The client lists these same ids from the same config
        // (XUiTheatre6RewardShop derives them from TaskTimeLimit), so filtering by type would
        // silently drop the permanent tab.
        return Rows<TaskRow>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    private static readonly Lazy<Dictionary<int, TaskCondition>> MetaTaskConditions = new(() =>
    {
        HashSet<int> ids = MetaTasks.Value.Values.Select(row => row.Condition).ToHashSet();
        return Rows<TaskCondition>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    internal static bool IsMetaTask(int id) => MetaTasks.Value.ContainsKey(id);

    /// <summary>
    /// True for the conditions authored on the Theatre6 mission groups. Those counters live in the
    /// mode's permanent ledger; every other condition (generic/current-task progress, resettable
    /// periods) lives in the shared counter store.
    /// </summary>
    private static bool IsModeCondition(int conditionId) => MetaTaskConditions.Value.ContainsKey(conditionId);

    /// <summary>
    /// A mission is open while the base mode is available and its authored group is available:
    /// a group with no published window is the permanent tab (Theatre6Reward row 3 / group 707,
    /// exactly what the client's own CheckRewardCfgIsPermanent reads), a group with a window
    /// (708 -> 47121, 735 -> 48601) is open only while ActivityScheduleService reports it open.
    /// </summary>
    private static bool IsMetaTaskOpen(Mutation m, TaskRow task, DateTimeOffset now)
    {
        if (!IsBaseAvailable(m.Player) || m.State.ActivityId <= 0)
            return false;
        foreach (TaskTimeLimitRow group in MetaTaskGroups.Value)
        {
            if (!group.TaskId.Contains(task.Id))
                continue;
            int timeId = group.TimeId ?? 0;
            if (timeId <= 0 || ActivityScheduleService.IsOpen(timeId, now))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Authored Theatre6 mission conditions. Unknown types fail loudly: a mission must never
    /// report success because its condition could not be evaluated.
    /// </summary>
    internal static int EvaluateMetaCondition(Mutation m, TaskCondition condition)
    {
        List<int> p = condition.Params;
        switch (condition.Type)
        {
            // 136001 [characterId, score]: an owned archive of that character reached the score.
            case 136001 when p.Count >= 2:
                return m.State.Files.Any(file => (p[0] <= 0 || file.CharacterId == p[0]) && file.Score >= p[1]) ? 1 : 0;
            // 136004 [count]: choices made in stages, accumulated by the shared ledger.
            // 136006 [count]: stage missions completed, accumulated by the shared ledger.
            // 136005 [count, battle kind]: battles completed in stages, same ledger.
            case 136004 or 136005 or 136006:
                return m.GetTaskConditionProgress(condition.Id);
            // 136009 [mode, difficulty]: difficulty cleared at least once.
            case 136009 when p.Count >= 2:
                return m.State.PassDiffRecords.GetValueOrDefault(p[1]) >= 1 ? 1 : 0;
            // 136010 [mode, battle type, count]: completed Phantom Clash matches. Params[0] is the
            // mode selector (2 = Phantom Clash) and Params[1] the battle kind recorded by the
            // PvP producer; any other authored selector fails closed.
            case 136010 when p.Count >= 3 && p[0] == 2 && p[1] == 2:
                return m.GetTaskConditionProgress(condition.Id);
            default:
                throw new InvalidDataException($"Unsupported Theatre6 mission condition {condition.Id}/{condition.Type}.");
        }
    }

    private static int MetaTaskValue(Mutation mutation, TaskRow task) =>
        Math.Min(Math.Max(0, EvaluateMetaCondition(mutation, MetaTaskConditions.Value[task.Condition])),
            Math.Max(1, task.Result ?? 1));

    private static int MetaTaskState(Mutation mutation, TaskRow task, int value) =>
        mutation.IsTaskClaimed(task.Id) ? 4 : value >= Math.Max(1, task.Result ?? 1) ? 3 : 1;

    internal static List<LoginTask> BuildTasks(Session session) =>
        BuildTaskUpdates(new Mutation(session, newOperation: false)).Select(task => new LoginTask
        {
            Id = task.Id, State = task.State, RecordTime = task.RecordTime,
            Schedule = task.Schedule.Select(value => new LoginTask.NotifyTaskDataTaskDataTaskSchedule
            {
                Id = value.Id, Value = value.Value
            }).ToList()
        }).ToList();

    internal static List<SyncTask> BuildTaskUpdates(Mutation mutation)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return MetaTasks.Value.Values.Select(task =>
        {
            int value = MetaTaskValue(mutation, task);
            return new SyncTask
            {
                Id = (uint)task.Id,
                State = MetaTaskState(mutation, task, value),
                RecordTime = now,
                Schedule = [new() { Id = (uint)task.Condition, Value = value }]
            };
        }).ToList();
    }

    internal static bool CanClaimMetaTask(Mutation mutation, int id) =>
        MetaTasks.Value.TryGetValue(id, out TaskRow? task) && !mutation.IsTaskClaimed(id)
        && IsMetaTaskOpen(mutation, task, DateTimeOffset.UtcNow)
        && (task.ShowAfterTaskId is not > 0 || mutation.IsTaskClaimed(task.ShowAfterTaskId.Value))
        && MetaTaskValue(mutation, task) >= Math.Max(1, task.Result ?? 1);

    internal static List<RewardGoods> ClaimMetaTask(Mutation mutation, int id)
    {
        Require(MetaTasks.Value.TryGetValue(id, out TaskRow? task), 20026005);
        Require(!mutation.IsTaskClaimed(id), 20026006);
        Require(CanClaimMetaTask(mutation, id), 20026007);
        List<RewardGoods> result = [];
        if (task!.RewardId is > 0)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(task.RewardId.Value);
            Require(goods.Count > 0, 20026003);
            foreach (RewardGoodsTable good in goods)
            {
                RewardType? type = RewardHandler.GetRewardType(good);
                Require(type is not null, 20026003);
                result.Add(new RewardGoods { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type!.Value });
            }
            mutation.Grant(new RewardGrant($"theatre6-task:{mutation.Session.player.PlayerData.Id}:{id}", goods));
        }
        mutation.MarkTaskClaimed(id);
        mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
        return result;
    }

    /// <summary>
    /// Producers report the authored mission counters. "Choice", "Task" and "Battle" advance the
    /// stage choice/mission/battle totals; "PvpBattle" advances completed Phantom Clash matches.
    /// </summary>
    internal static void RecordMetaProgress(Mutation mutation, string trigger, int value = 1, int parameter = 0)
    {
        Require(value > 0 && parameter >= 0, CodeInvalidRequest);
        (int conditionType, int expectedParameter) = trigger switch
        {
            "Choice" => (136004, 0),
            "Task" => (136006, 0),
            // Authored stage battles carry kind 3 (params [count, 3]); any other kind is ignored.
            "Battle" => (136005, 3),
            // Authored Phantom Clash matches exclude promotion challenges (params [2, 2, count]).
            "PvpBattle" => (136010, 2),
            _ => throw new InvalidDataException($"Unsupported Theatre6 mission trigger {trigger}.")
        };
        if (expectedParameter != 0 && parameter != expectedParameter)
            return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = false;
        foreach (TaskCondition condition in MetaTaskConditions.Value.Values.Where(row => row.Type == conditionType))
        {
            if (!MetaTasks.Value.Values.Any(task => task.Condition == condition.Id && IsMetaTaskOpen(mutation, task, now)))
                continue;
            mutation.AddTaskConditionProgress(condition.Id, value);
            changed = true;
        }
        if (changed) mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
    }

    //endregion
}
