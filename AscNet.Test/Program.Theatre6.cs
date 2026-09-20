using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

// Client-facing key sets in this harness are bound to the INSTALLED client Lua extracted from the
// installed assets/temp/lua/matrix.ab document bundle (index_sha1
// 0dc2ae92069468f963c7ce301c0eeacc5f7ed23a, file_sha1 2a84183f6f7ea02ce3ef5d57dda3f91d4b80aa4e,
// 9555 modules; scratch copy at <ascnet-requiem scratch>/installed-lua/). The repository Lua corpus
// under PGR_DATA/en is STALE for three modules and must not be equated with the current client:
//   XTheatre6ControlPvpNetwork.lua installed 941a311b89e9cf10e8580f984c027cd6d56944ae (3955 B)
//     vs corpus fef33d42544c15936cef9e8dac3cb98759680ae5 - installed handles a RestartFight error
//     code explicitly and clears the client's tiny battle state.
//   XTheatre6Model.lua installed cb9d70a84e3b5a58fd73502f6c7679cc7bf4f237 (23071 B)
//     vs corpus 76b36c22f25c4b99f7aa2225efda12f3c052ed2d - installed also copies
//     settleData.PassStageRecords/PassDiffRecords (asserted below on the settlement response).
//   XTheatre6ControlConfig.lua installed 047fb0ea22ebddc28ab9374b98bb56f1346965c0 (12270 B)
//     vs corpus 4e1f3aa289e8c23ff2cf904903c4cc0550bb67d0 - optional customModelData parameter.
// Everything else extracted is byte-identical modulo CRLF, including XTheatre6BattleAgency.lua
// (installed 13278 B, sha1 7225efeb998b802342a9b147d5adbcf8fc1e7b1d), which is the producer whose
// world/report envelope this harness's synthetic fixtures follow.
// Shrouded Requiem / Phantom Clash harness. Every scenario drives the REAL registered encrypted
// loopback dispatch with one packet/fence counter and reads authoritative state through wire
// payloads (responses/pushes) plus durable BSON. DlcSingleEnterFight/DlcSingleFightSettle reports
// assembled here are SYNTHETIC native boundary inputs: they prove authorization, staged mutation,
// frozen attempt identity and durable outcomes, never Theatre6 native combat.
internal partial class Program
{
    private static readonly HashSet<string> RequiemCalls = [];
    private static readonly HashSet<string> RequiemSuccessfulCalls = [];

    // 15 PvE lifecycle + 5 in-run shop + 3 skill + 10 PvP + 2 shared DLC requests.
    private static readonly string[] RequiemRequestNames =
    [
        "Theatre6PlayModeStartFightRequest", "Theatre6EnterStoryLineRequest", "Theatre6ContinueGameRequest",
        "Theatre6EndGameRequest", "Theatre6RefreshTaskRequest", "Theatre6ConfirmTaskRequest",
        "Theatre6RecvTaskRoomRewardRequest", "Theatre6ChooseEventRequest", "Theatre6FightRoomSlideRequest",
        "Theatre6EndAvgRoomRequest", "Theatre6SaveFileRequest", "Theatre6GiveUpSaveFileRequest",
        "Theatre6StoryModeGuideFinishedRequest", "Theatre6ShopBuySanRequest", "Theatre6ExFloorConfirmRequest",
        "Theatre6ShopFreshRequest", "Theatre6EndShopRequest", "Theatre6ShopGoodLockRequest",
        "Theatre6ShopGoodBuyRequest", "Theatre6ShopGoodSellRequest", "Theatre6SkillMoveOrSwapRequest",
        "Theatre6SkillOverQueueSellRequest", "Theatre6BuffLevelUpSkillRequest", "Theatre6PvpStartRequest",
        "Theatre6PvpUpdateDefenseRequest", "Theatre6PvpRefreshMatchRequest", "Theatre6PvpGetActionPointRequest",
        "Theatre6PvpStartFightRequest", "Theatre6PvpRestartFightRequest", "Theatre6PvpQueryRankRequest",
        "Theatre6PvpGetBattleRecordsRequest", "Theatre6GetPvpPreviewInfoRequest", "Theatre6PvpGiveUpFightRequest",
        "DlcSingleEnterFightRequest", "DlcSingleFightSettleRequest"
    ];

    private static void ValidateTheatre6Compatibility()
    {
        PacketFactory.LoadPacketHandlers();
        RequiemCalls.Clear();
        RequiemSuccessfulCalls.Clear();
        ValidateRequiemAvailabilityChecks();
        ValidateRequiemStoryAndGameplayChecks();
        ValidateRequiemShopAndSkillChecks();
        ValidateRequiemNativeBattleChecks();
        ValidateRequiemBattleRelicRewardsChecks();
        ValidateRequiemPvpChecks();
        ValidateRequiemRewardShopChecks();
        ValidateRequiemMissionProgressChecks();
        ValidateRequiemJournalChecks();
        ValidateRequiemTerminalRecoveryChecks();
        ValidateRequiemReceiptWindowChecks();
        ValidateRequiemDefenceHandoffChecks();
        Require(RequiemRequestNames.All(RequiemCalls.Contains),
            "Theatre6 requests not exercised: " + string.Join(", ", RequiemRequestNames.Except(RequiemCalls)));
        Require(RequiemRequestNames.All(RequiemSuccessfulCalls.Contains),
            "Theatre6 requests without successful flow: " + string.Join(", ", RequiemRequestNames.Except(RequiemSuccessfulCalls)));
        Console.WriteLine("Theatre6 registered-packet/BSON compatibility passed; synthetic native reports prove server boundaries, not Theatre6 native combat. AscNet-authored pool policy is asserted through persisted outcomes, never claimed as retail balance.");
    }

    private static Dictionary<string, object?> Req(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value);

    // Gate chain: Theatre6PvpActivity.TimeId -> EventCatalog.SkipId -> SkipFunctional.FunctionalId
    // -> FunctionalOpen.Condition -> Condition type10101 level. Derived from the authored tables so
    // no literal activity id can pin the wrong promotion.
    private static (Theatre6PvpActivityTable Season, int TimeId, int RequiredLevel) RequiemGate()
    {
        List<EventCatalogTable> catalog = TableReaderV2.Parse<EventCatalogTable>();
        List<SkipFunctionalTable> skips = TableReaderV2.Parse<SkipFunctionalTable>();
        Dictionary<int, FunctionalOpenTable> functions = TableReaderV2.Parse<FunctionalOpenTable>().ToDictionary(row => row.Id);
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        foreach (Theatre6PvpActivityTable season in TableReaderV2.Parse<Theatre6PvpActivityTable>().OrderBy(row => Convert.ToInt32(row.Id)))
        {
            List<EventCatalogTable> entries = catalog.Where(row => Convert.ToInt32(row.TimeId) == Convert.ToInt32(season.TimeId)).ToList();
            if (entries.Count != 1)
                continue;
            List<SkipFunctionalTable> skipRows = skips
                .Where(row => Convert.ToInt32(row.SkipId) == Convert.ToInt32(entries[0].SkipId) && Convert.ToInt32(row.FunctionalId) > 0)
                .ToList();
            if (skipRows.Count != 1 || !functions.TryGetValue(Convert.ToInt32(skipRows[0].FunctionalId), out FunctionalOpenTable? function))
                continue;
            if (function.Condition.Count != 1 || !conditions.TryGetValue(Convert.ToInt32(function.Condition[0]), out ConditionTable? condition))
                continue;
            if (Convert.ToInt32(condition.Type) != 10101 || condition.Params.Count != 1)
                continue;
            return (season, Convert.ToInt32(season.TimeId), Convert.ToInt32(condition.Params[0]));
        }

        throw new InvalidDataException("Theatre6 PVP activity has no authoritative gate chain in the tables.");
    }

    private static DateTimeOffset RequiemOpenMoment(int timeId)
    {
        if (!ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry schedule))
            throw new InvalidDataException($"Theatre6 PVP time {timeId} has no authoritative schedule entry.");
        if (schedule.StartTime <= 0 || schedule.EndTime <= schedule.StartTime)
            throw new InvalidDataException($"Theatre6 PVP time {timeId} has invalid schedule bounds.");
        DateTimeOffset moment = DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime + ((schedule.EndTime - schedule.StartTime) / 2));
        if (!ActivityScheduleService.IsOpen(timeId, moment))
            throw new InvalidDataException($"Theatre6 PVP time {timeId} is not open inside its own authored window.");
        return moment;
    }

    private static void ValidateRequiemAvailabilityChecks()
    {
        (Theatre6PvpActivityTable season, int timeId, int requiredLevel) = RequiemGate();
        Require(requiredLevel > 1, "Theatre6 gate level must leave an unauthorized boundary below it.");
        DateTimeOffset open = RequiemOpenMoment(timeId);
        AssertEqual(true, ActivityScheduleService.IsOpen(timeId, open), "Theatre6 PVP schedule is open inside its authored window");
        int baseActivity = Convert.ToInt32(TableReaderV2.Parse<Theatre6ActivityTable>().Single().Id);

        // Below the authored level: nothing is published and the gated requests stay rejected. The
        // pure time-parameterized projection is asserted instead of the mutating call's return value.
        using (RequiemCase below = new("gate-below", level: requiredLevel - 1))
        {
            AssertEqual(false, below.PureAvailable(open), "Theatre6 projection refuses an under-levelled player at an open window");
            AssertEqual<JObject?>(null, below.TryLogin(), "Under-levelled Theatre6 login emits no activity state");
            below.Call("Theatre6GetPvpPreviewInfoRequest", null, success: false);
            below.Reject("Theatre6PvpStartRequest", null, "unauthorized Theatre6 PVP start");
            AssertEqual(0, below.State.Pvp.AuthorizedSeasonId, "Unauthorized Theatre6 login never activates a season");
        }

        using (RequiemCase authorized = new("gate-authorized", level: requiredLevel))
        {
            AssertEqual(true, authorized.PureAvailable(open), "Theatre6 projection authorizes the authored required level");
            JObject notify = authorized.Login();
            AssertEqual(baseActivity, notify.Value<int>("ActivityId"), "Theatre6 login snapshot carries the authored base activity");
            AssertEqual(Convert.ToInt32(season.Id), authorized.State.Pvp.AuthorizedSeasonId,
                "A level-eligible login authorizes the authored PVP season id");

            // Level-eligible is not admitted: the authored unlock condition (stage progress plus two
            // archives) is enforced by the Phantom Clash entry point, so a fresh account is
            // authorized but locked, and the refusal must not move any durable state.
            byte[] beforeLockedEntry = authorized.State.ToBson();
            JObject locked = authorized.Call("Theatre6PvpStartRequest", null, success: false);
            AssertEqual(20427023, locked.Value<int>("Code"),
                "An unadmitted Theatre6 account is refused with the authored ModeLocked code");
            AssertEqual(Convert.ToHexString(beforeLockedEntry), Convert.ToHexString(authorized.State.ToBson()),
                "A locked Phantom Clash entry cannot mutate durable Theatre6 state");
            AssertEqual(true, authorized.State.Pvp.Battle is null, "An unadmitted Theatre6 account opens no battle");
            AssertEqual(0, authorized.RankQueryTotal(), "An unadmitted Theatre6 account never appears on the leaderboard");
            if (ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry schedule))
            {
                DateTimeOffset closed = DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime);
                AssertEqual(false, ActivityScheduleService.IsOpen(timeId, closed), "Theatre6 PVP schedule is closed at its authored end");
                // The closed projection is time-parameterized: the same fixture projects unavailable
                // at the authored end and available inside the window. A real request always runs on
                // the real clock, so it must re-authorize rather than inherit a stale projection.
                AssertEqual(false, authorized.PureAvailable(closed),
                    "Theatre6 projection reports the authored end as unavailable");
                AssertEqual(true, authorized.PureAvailable(open),
                    "Theatre6 projection stays available inside the authored window");
                Require(authorized.TryLogin() is not null,
                    "Permanent Theatre6 base visibility keeps publishing login state at the authored end.");
            }
        }
    }

    private static void ValidateRequiemJournalChecks()
    {
        foreach (bool finalCommit in new[] { false, true })
        {
            using RequiemCase test = new(finalCommit ? "journal-final" : "journal-intent");
            test.UnlockGameplay();
            (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
            string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
            test.WalkToRoom(modeKey, 3, "journal");
            (string request, Dictionary<string, object?> payload) = test.AffordableShopRequest(modeKey);
            // Consumer-visible state only: the run snapshot the client reads plus the wallet and
            // goods the UI shows. A failed write may legitimately have advanced journal/receipt
            // metadata, so raw document identity is not a contract here.
            string modeBefore = test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None);
            long walletBefore = test.Balance(test.StoryLineCoin());
            long inventoryBefore = test.InventoryFingerprint();
            int saves = 0;
            test.Players.BeforeReplaceOne = _ =>
            {
                if (++saves == (finalCommit ? 2 : 1)) throw new MongoException("Theatre6 injected journal boundary");
            };
            try
            {
                test.Call(request, payload, success: false);
            }
            finally
            {
                test.Players.BeforeReplaceOne = null;
            }

            AssertEqual(modeBefore, test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None),
                $"{request} failed Theatre6 journal boundary preserves the visible run snapshot");
            AssertEqual(walletBefore, test.Balance(test.StoryLineCoin()),
                $"{request} failed Theatre6 journal boundary preserves the visible wallet");
            AssertEqual(inventoryBefore, test.InventoryFingerprint(),
                $"{request} failed Theatre6 journal boundary preserves the visible goods");
            AssertEqual(finalCommit, test.State.PendingMutation is not null,
                "Only a durable Theatre6 intent survives a failed journal boundary");
            AssertEqual(0, test.Pushes.Count, "Failed Theatre6 journal does not acknowledge mutation pushes");
            if (finalCommit)
            {
                long inventoryPendingFingerprint = test.InventoryFingerprint();
                string modePending = test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None);
                long walletPending = test.Balance(test.StoryLineCoin());
                // The Session guard owns the player while the journal is pending: foreign Theatre6
                // requests and mismatched shared task claims must be rejected before any counter,
                // inventory entry or epoch can move.
                test.Call("Theatre6GetPvpPreviewInfoRequest", null, success: false);
                test.Call("Theatre6EndAvgRoomRequest", null, success: false);
                test.RejectForeignTaskClaim();
                AssertEqual(modePending, test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None),
                    "Foreign Theatre6 request cannot consume the pending journal owner");
                AssertEqual(walletPending, test.Balance(test.StoryLineCoin()),
                    "Foreign requests cannot move the visible wallet while the Theatre6 journal is pending");
                AssertEqual(inventoryPendingFingerprint, test.InventoryFingerprint(),
                    "Foreign requests cannot move the visible goods while the Theatre6 journal is pending");
            }

            test.Call(request, payload);
            AssertEqual(true, test.State.PendingMutation is null, $"Semantic {request} retry recovers and clears the journal");
            Require(test.Pushes.Count > 0, $"Recovered {request} must republish the frozen mutation pushes.");
            string committed = test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None);
            long walletCommitted = test.Balance(test.StoryLineCoin());
            long goodsCommitted = test.InventoryFingerprint();
            byte[] response = test.LastResponseContent;
            int committedId = test.LastPacketId;
            test.Call(request, payload, reusePacketId: committedId);
            AssertEqual(Convert.ToHexString(response), Convert.ToHexString(test.LastResponseContent),
                $"Committed {request} duplicate returns frozen response bytes");
            AssertEqual(0, test.Pushes.Count, $"Committed {request} duplicate is response only");
            AssertEqual(committed, test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None),
                $"Committed {request} duplicate cannot mutate the visible run");
            test.Relog("journal");
            test.Call(request, payload, success: false);
            AssertEqual(committed, test.Mode(modeKey).ToString(Newtonsoft.Json.Formatting.None),
                $"Reapplying recovered {request} after relog is rejected");
            AssertEqual(walletCommitted, test.Balance(test.StoryLineCoin()),
                $"Reapplying recovered {request} after relog cannot charge the wallet again");
            AssertEqual(goodsCommitted, test.InventoryFingerprint(),
                $"Reapplying recovered {request} after relog cannot grant goods again");
            AssertEqual(true, test.State.PendingMutation is null, "Rejected Theatre6 replay leaves no journal");
        }
    }


    // Bounded receipt window: a retired frozen response must be refused instead of re-executing a
    // non-idempotent action, a recent id still replays its frozen bytes, and a new connection's ids
    // never collide with the previous session's receipts.
    private static void ValidateRequiemReceiptWindowChecks()
    {
        using RequiemCase test = new("receipt-window");
        test.UnlockGameplay();
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkRun(modeKey, "receipt-window");
        test.AcknowledgeRun(modeKey, modeId: 1, slot: 1);
        Theatre6FileState duplicate = BsonSerializer.Deserialize<Theatre6FileState>(test.State.Files[0].ToBson());
        duplicate.SlotId = test.State.Files[0].SlotId + 1;
        test.State.Files.Add(duplicate);
        test.SaveFixture();
        test.Reconcile(DateTimeOffset.UtcNow);
        test.Call("Theatre6PvpStartRequest", null);
        test.PvpStartBattle();

        int restartId = test.NextPacketId();
        JObject restart = test.Call("Theatre6PvpRestartFightRequest", null, reusePacketId: restartId);
        byte[] frozenRestart = test.LastResponseContent;
        int restartCount = test.State.Pvp.Battle?.RestartCount ?? -1;
        Require(restartCount >= 1, "A committed Theatre6 restart must persist its attempt count.");
        Require(restart["BattleState"] is JObject, "Theatre6 restart returns the resumed battle state.");

        // Positive: an id inside the window replays the frozen bytes without repeating the action.
        test.Call("Theatre6PvpRestartFightRequest", null, reusePacketId: restartId);
        AssertEqual(Convert.ToHexString(frozenRestart), Convert.ToHexString(test.LastResponseContent),
            "An id inside the receipt window replays the frozen Theatre6 restart response");
        AssertEqual(restartCount, test.State.Pvp.Battle?.RestartCount ?? -1,
            "Replayed Theatre6 restart does not consume another attempt");

        // Retire the frozen response with a bounded sequence of intervening requests. The count is a
        // sequence budget, not the module's window size: it only has to exceed any bounded retention,
        // and nothing here pins a private constant or a window length.
        const int interveningRequests = 200;
        for (int index = 0; index < interveningRequests; index++)
            test.Call("Theatre6PvpGetActionPointRequest", null);
        int beforeStale = test.State.Pvp.Battle?.RestartCount ?? -1;
        byte[] beforeState = test.State.ToBson();
        JObject stale = test.Call("Theatre6PvpRestartFightRequest", null, reusePacketId: restartId, success: false);
        AssertEqual(true, stale.Value<int>("Code") != 0,
            "A retired Theatre6 receipt must be rejected instead of re-executing the action");
        AssertEqual(Convert.ToHexString(beforeState), Convert.ToHexString(test.State.ToBson()),
            "A retired Theatre6 receipt cannot mutate battle state");
        AssertEqual(beforeStale, test.State.Pvp.Battle?.RestartCount ?? -1,
            "A retired Theatre6 receipt cannot consume another attempt");
        Require(test.State.Pvp.Battle is { Finished: false }, "A retired Theatre6 receipt cannot force a loss.");

        // A fresh id still performs the legitimate action.
        test.Call("Theatre6PvpRestartFightRequest", null);
        AssertEqual(beforeStale + 1, test.State.Pvp.Battle?.RestartCount ?? -1,
            "A fresh Theatre6 restart id still applies exactly once");

        // New connection: the previous session's ids must neither replay nor collide.
        int previousSessionId = restartId;
        test.Relog("receipt-window");
        test.Call("Theatre6PvpGetActionPointRequest", null);
        test.Call("Theatre6PvpRestartFightRequest", null, reusePacketId: previousSessionId, success: null);
        AssertEqual(true, test.LastResponseContent.Length > 0,
            "A new session answers the reused transport id through the live path");
    }

    private sealed partial class RequiemCase : IDisposable
    {
        private static long nextPlayerId = 46_700;

        private readonly MongoCollectionOverride collections;
        private int packetCounter;
        public RecordingMongoCollectionProxy<Player> Players { get; }
        public RecordingMongoCollectionProxy<Character> Characters { get; }
        public RecordingMongoCollectionProxy<Inventory> Inventories { get; }
        public RecordingMongoCollectionProxy<Stage> Stages { get; }
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public Player Player => Session.player;
        public Theatre6State State => Player.Theatre6;
        public List<(string Name, JObject Body)> Pushes { get; } = [];
        public int WireSession { get; private set; } = 1;
        private string? lastModeKey;
        private int lastRunId = -1;
        private long lastWeightedScore;
        private int lastScoreTotal;
        public HashSet<string> CaseSuccessful { get; } = [];
        public HashSet<string> ObservedPushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public int LastPacketId { get; private set; }
        public string LastRequestName { get; private set; } = "";
        public object? LastRequest { get; private set; }
        public string Name { get; }
        public int Level { get; }

        public RequiemCase(string name, int level = 80, bool prepareLogin = true, Player? existing = null)
        {
            Name = name;
            Level = level;
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(
                out RecordingMongoCollectionProxy<Player> players,
                out RecordingMongoCollectionProxy<Character> characters,
                out RecordingMongoCollectionProxy<Inventory> inventories,
                out RecordingMongoCollectionProxy<Stage> stages);
            Players = players;
            Characters = characters;
            Inventories = inventories;
            Stages = stages;
            Players.FindResults = [];
            long id = existing?.PlayerData.Id ?? ++nextPlayerId;
            Player seeded = existing ?? CreateDrawCompatibilityPlayer(id);
            Harness = new LoopbackSessionHarness(CreateDrawCompatibilityCharacter(id), seeded,
                CreateDrawCompatibilityInventory(id, []), $"theatre6-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            Player.PlayerData.Level = level;
            if (prepareLogin) PrepareLogin();
            SaveFixture();
        }

        // A cross-player fixture reuses an existing player document through the same harness.
        public RequiemCase(string name, Player player, bool prepareLogin = false)
            : this(name, checked((int)player.PlayerData.Level), prepareLogin, player)
        {
        }

        // The published leaderboard size; an unadmitted account legitimately answers with an empty
        // board rather than a distinguishable error, so only the observable count is asserted.
        public int RankQueryTotal() => Call("Theatre6PvpQueryRankRequest", null, success: null).Value<int?>("TotalCount") ?? 0;

        // Pure availability projection at a caller-supplied time: no durable state changes.
        public bool PureAvailable(DateTimeOffset now) => (bool)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "IsAvailable", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()])
            .Invoke(null, [Player, now, 0, 0])!;

        public void PrepareLogin() => RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);

        public bool? Reconcile(DateTimeOffset now) => (bool?)RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "ReconcileAvailability", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]).Invoke(null, [Player, now]);

        public void ResumePending() => RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "ResumePending", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(bool)]).Invoke(null, [Session, true]);

        public JObject? TryLogin() => RequiemWireContent(RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module"),
            "BuildNotify", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Player), typeof(DateTimeOffset)]).Invoke(null, [Player, DateTimeOffset.UtcNow]));

        public JObject Login() => TryLogin() ?? throw new InvalidDataException($"Theatre6 login emitted no activity state for {Name}.");

        private static JObject? RequiemWireContent(object? payload) =>
            payload is null ? null : JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerialize(payload.GetType(), payload)));


        // Authored weighting of the run score: attributes, equipped (non-bag) skills and relic
        // packs, each with its own SaveScore. Constants outside this set cancel in the delta.
        private long WeightedScore(JObject mode)
        {
            long score = 0;
            foreach (JObject attr in (mode["Attrs"] as JArray ?? []).Children<JObject>())
            {
                Theatre6AttrTable? row = TableReaderV2.Parse<Theatre6AttrTable>()
                    .FirstOrDefault(candidate => Convert.ToInt32(candidate.Id) == attr.Value<int>("AttrId"));
                score += (long)attr.Value<int>("Value") * Convert.ToInt32(row?.SaveScore ?? 0);
            }

            int bag = 4;
            foreach (JObject skill in (mode["Skills"] as JArray ?? []).Children<JObject>())
            {
                if (skill.Value<int>("SlotType") == bag) continue;
                Theatre6SkillTable? row = TableReaderV2.Parse<Theatre6SkillTable>()
                    .FirstOrDefault(candidate => Convert.ToInt32(candidate.Id) == skill.Value<int>("SkillId"));
                if (row is not null) score += Convert.ToInt32(row.SaveScore);
            }

            // Implicit authored base skills: the client (CalcEquippedSkillScore) and the runtime both
            // score the character's base skill for every Active position that carries no equipped
            // skill, so equipping an earned skill over a base slot is not score-neutral.
            score += WeightedBaseSkillScore(mode);

            foreach ((int packId, int num) in RequiemAttrPacks(mode))
            {
                Theatre6AttrPackTable? row = TableReaderV2.Parse<Theatre6AttrPackTable>()
                    .FirstOrDefault(candidate => Convert.ToInt32(candidate.Id) == packId);
                if (row is not null) score += (long)num * Convert.ToInt32(row.SaveScore ?? 0);
            }

            return score;
        }

        // AttrPacks travel as a map keyed by PackId, which is the authoritative wire shape (the client
    // assigns mode.AttrPacks[pack.PackId]); anything else is a malformed snapshot, not a legacy form.
    internal static List<(int PackId, int Num)> RequiemAttrPacks(JObject mode)
    {
        if (mode["AttrPacks"] is not JObject map)
            throw new InvalidDataException(
                $"Theatre6 mode snapshot must publish AttrPacks as a map keyed by pack id (mode={mode.Value<int?>("ModeId")}, " +
                $"shape={mode["AttrPacks"]?.Type.ToString() ?? "absent"}).");
        List<(int, int)> packs = [];
        foreach (JProperty property in map.Properties())
        {
            if (!int.TryParse(property.Name, out int packId))
                throw new InvalidDataException($"Theatre6 AttrPacks key '{property.Name}' is not an authored pack id.");
            if (property.Value["Num"] is not { Type: JTokenType.Integer } num)
                throw new InvalidDataException($"Theatre6 AttrPacks entry {packId} must publish an integer Num.");
            packs.Add((packId, num.Value<int>()));
        }

        return packs;
    }

    // The authored base-skill term: BaseSkill[i] belongs to Active position i+1 and counts while
        // that position is empty in the snapshot.
        private static long WeightedBaseSkillScore(JObject mode)
        {
            int characterId = mode.Value<int?>("CharacterId") ?? 0;
            Theatre6CharacterTable? character = TableReaderV2.Parse<Theatre6CharacterTable>()
                .FirstOrDefault(row => Convert.ToInt32(row.Id) == characterId);
            if (character is null) return 0;
            HashSet<int> equipped = (mode["Skills"] as JArray ?? []).Children<JObject>()
                .Where(skill => skill.Value<int>("SlotType") == 2 && skill.Value<int>("Position") > 0)
                .Select(skill => skill.Value<int>("Position")).ToHashSet();
            long score = 0;
            for (int index = 0; index < character.BaseSkill.Count; index++)
            {
                if (equipped.Contains(index + 1)) continue;
                Theatre6SkillTable? baseSkill = TableReaderV2.Parse<Theatre6SkillTable>()
                    .FirstOrDefault(row => Convert.ToInt32(row.Id) == character.BaseSkill[index]);
                if (baseSkill is not null) score += Convert.ToInt32(baseSkill.SaveScore);
            }

            return score;
        }

        // Persisted run identity: Core's ActiveRuns metadata for the observed mode (never a wire
        // field), or zero when no run is held, which is what marks a start boundary.
        private int PersistedRunIdentity(string modeKey)
        {
            int modeId = modeKey == PlayModeKey ? 1 : modeKey == StoryModeKey ? 2 : 0;
            return modeId != 0 && State.ActiveRuns.TryGetValue(modeId, out Theatre6RunState? run) ? run.RunId : 0;
        }

        // Per-term breakdown of the authored weighting, used only as diagnostic context so a
        // mismatch names which authored family moved the score.
        private static string WeightedScoreTerms(JObject mode)
        {
            long attrs = 0;
            foreach (JObject attr in (mode["Attrs"] as JArray ?? []).Children<JObject>())
                attrs += (long)attr.Value<int>("Value") * Convert.ToInt32(TableReaderV2.Parse<Theatre6AttrTable>()
                    .FirstOrDefault(row => Convert.ToInt32(row.Id) == attr.Value<int>("AttrId"))?.SaveScore ?? 0);
            long skills = (mode["Skills"] as JArray ?? []).Children<JObject>()
                .Where(skill => skill.Value<int>("SlotType") != 4)
                .Sum(skill => Convert.ToInt32(TableReaderV2.Parse<Theatre6SkillTable>()
                    .FirstOrDefault(row => Convert.ToInt32(row.Id) == skill.Value<int>("SkillId"))?.SaveScore ?? 0));
            long packs = RequiemAttrPacks(mode)
                .Sum(pack => (long)pack.Num * Convert.ToInt32(TableReaderV2.Parse<Theatre6AttrPackTable>()
                    .FirstOrDefault(row => Convert.ToInt32(row.Id) == pack.PackId)?.SaveScore ?? 0));
            return $"attrs:{attrs},skills:{skills},base:{WeightedBaseSkillScore(mode)},packs:{packs}";
        }

        private void ObserveMode(JObject mode, string modeKey)
        {
            lastModeKey = modeKey;
            lastRunId = PersistedRunIdentity(modeKey);
            lastWeightedScore = WeightedScore(mode);
            lastScoreTotal = mode.Value<int>("ScoreTotal");
        }

        // The client-visible total must equal the server's table-derived weighting: whenever the
        // authored weighting moved, the request must publish Theatre6TotalScoreNotify and the
        // published delta must match the complete post-response snapshot. Attributes or skills with
        // no authored SaveScore legitimately publish no score change, and pushes are never counted
        // or ordered, only required and compared.
        private void AssertScoreFollowsAuthoredWeights(string requestName)
        {
            if (lastModeKey is not string modeKey || Login()[modeKey] is not JObject mode)
                return;
            // A new run starts from a zero score: reset the baseline at the observed run boundary so
            // the start request's own pushes are judged against that starting point, never against a
            // previously settled run.
            int runIdentity = PersistedRunIdentity(modeKey);
            if (runIdentity != lastRunId)
            {
                lastWeightedScore = 0;
                lastScoreTotal = 0;
                lastRunId = runIdentity;
            }

            long weighted = WeightedScore(mode);
            int total = mode.Value<int>("ScoreTotal");
            long weightedDelta = weighted - lastWeightedScore;
            int totalDelta = total - lastScoreTotal;
            if (weightedDelta != 0)
            {
                string context = $"mode={modeKey}, weighted {lastWeightedScore}->{weighted} " +
                    $"(attrs={WeightedScoreTerms(mode)}), ScoreTotal {lastScoreTotal}->{total}, " +
                    $"pushes=[{string.Join(", ", Pushes.Select(push => push.Name))}]";
                List<JObject> scorePushes = Pushes.Where(candidate => candidate.Name == "Theatre6TotalScoreNotify")
                    .Select(candidate => candidate.Body).ToList();
                if (scorePushes.Count == 0)
                    throw new InvalidDataException(
                        $"{requestName}: a weighted authored score change must publish Theatre6TotalScoreNotify ({context}).");
                int pushedDelta = scorePushes[^1].Value<int>("TotalScoreNew") - scorePushes[0].Value<int>("TotalScoreOld");
                AssertEqual(totalDelta, (int)weightedDelta,
                    $"{requestName}: published total score change must follow the authored SaveScore weights ({context})");
                AssertEqual(pushedDelta, totalDelta,
                    $"{requestName}: the {scorePushes.Count} score pushes must aggregate to the snapshot change ({context})");
            }

            lastWeightedScore = weighted;
            lastScoreTotal = total;
        }

        // A buff-pool reward must carry buff records (BuffId/TriggerCount/AddMagic), never bare
        // ids: the client's reward consumer indexes buffData.BuffId.
        public void AssertBuffRewardShape(JArray? rewards)
        {
            foreach (JObject reward in (rewards ?? []).Children<JObject>())
            {
                if (reward["BuffList"] is not JArray buffs)
                    continue;
                foreach (JToken buff in buffs)
                    Require(buff is JObject record && record["BuffId"] is not null,
                        "Theatre6 buff rewards must carry buff records with BuffId instead of bare ids.");
            }
        }

        public void SaveFixture()
        {
            Player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }

        public JObject Call(string requestName, object? request = null, bool? success = true, int? reusePacketId = null)
        {
            Pushes.Clear();
            int id = reusePacketId ?? ++packetCounter;
            LastPacketId = id;
            LastRequestName = requestName;
            LastRequest = request;
            RequiemCalls.Add(requestName);
            RequiemWire.Record(Name, WireSession, "Request", requestName, id, request,
                request is null ? [] : MessagePackSerialize(request.GetType(), request));
            int fenceId = 0;
            JObject? body = null;
            Harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(requestName, id, request));
            for (int index = 0; index < 512; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    JObject pushBody = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
                    Pushes.Add((push.Name, pushBody));
                    ObservedPushes.Add(push.Name);
                    RequiemWire.Record(Name, WireSession, "Push", push.Name, null, pushBody, push.Content);
                    continue;
                }

                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                if (fenceId != 0)
                {
                    AssertEqual(fenceId, response.Id, $"{requestName} completion fence correlation");
                    AssertEqual(nameof(HandshakeResponse), response.Name, $"{requestName} completion fence response");
                    AssertEqual(0, MessagePackSerializer.Deserialize<HandshakeResponse>(response.Content).Code,
                        $"{requestName} completion fence success");
                    RequiemWire.Record(Name, WireSession, "Response", response.Name, response.Id,
                        JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content)), response.Content);
                    AssertScoreFollowsAuthoredWeights(requestName);
                    return body ?? throw new InvalidDataException($"{requestName}: fence preceded the tested response");
                }

                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName[..^"Request".Length] + "Response", response.Name, $"{requestName} response name");
                LastResponseContent = response.Content;
                body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = body.Value<int>("Code");
                if (code == 0)
                {
                    RequiemSuccessfulCalls.Add(requestName);
                    CaseSuccessful.Add(requestName);
                }
                if (success.HasValue)
                    AssertEqual(success.Value, code == 0, $"{requestName} success={success}, code={code}, body={body}");
                RequiemWire.Record(Name, WireSession, "Response", response.Name, response.Id, body, response.Content);
                // Inbound dispatch is serial: this transport-only fence reports that the tested
                // request's post-hooks finished. Same counter, never replacing packet id or coverage.
                fenceId = ++packetCounter;
                Harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HandshakeRequest), fenceId,
                    new HandshakeRequest { DocumentVersion = "", Sha1 = "", ApplicationVersion = "" }));
            }

            throw new InvalidDataException($"{requestName}: response missing");
        }

        public void Reject(string name, object? request, string label)
        {
            byte[] state = State.ToBson();
            byte[] inventory = Session.inventory.ToBson();
            byte[] character = Session.character.ToBson();
            Call(name, request, false);
            AssertEqual(Convert.ToHexString(state), Convert.ToHexString(State.ToBson()), $"{label}: theatre6 state unchanged");
            AssertEqual(Convert.ToHexString(inventory), Convert.ToHexString(Session.inventory.ToBson()), $"{label}: inventory unchanged");
            AssertEqual(Convert.ToHexString(character), Convert.ToHexString(Session.character.ToBson()), $"{label}: character unchanged");
        }

        // A shared task claim routed at the wrong Theatre6 journal owner: the Session guard must
        // reject it before any claim counter or inventory entry can move.
        public void RejectForeignTaskClaim()
        {
            int claimCount = Session.player.MissionProgress.ClaimedTaskIds.Count;
            JObject body = Call("FinishMultiTaskRequest", Req(("TaskIds", new List<object?> { 900_001 })), false);
            AssertEqual(true, body.Value<int>("Code") != 0,
                "Mismatched shared task claim must be rejected while the Theatre6 journal is pending");
            AssertEqual(claimCount, Session.player.MissionProgress.ClaimedTaskIds.Count,
                "Mismatched shared task claim cannot append claim counters");
        }

        public void Relog(string label, bool pending = false)
        {
            byte[] before = State.ToBson();
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson!);
            Character character = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            Stage stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new LoopbackSessionHarness(character, player, inventory, $"theatre6-relog-{label}");
            Session.stage = stage;
            WireSession++;
            if (!pending)
            {
                ResumePending();
                PrepareLogin();
            }

            if (!pending && !before.SequenceEqual(State.ToBson()))
            {
                BsonDocument expected = BsonSerializer.Deserialize<BsonDocument>(before);
                BsonDocument actual = State.ToBsonDocument();
                string differences = string.Join("; ", expected.Names.Concat(actual.Names).Distinct()
                    .Where(key => !expected.GetValue(key, BsonNull.Value).Equals(actual.GetValue(key, BsonNull.Value)))
                    .Select(key => $"{key}: {expected.GetValue(key, BsonNull.Value)} -> {actual.GetValue(key, BsonNull.Value)}"));
                throw new InvalidDataException($"{label}: login changed durable Theatre6 state: {differences}");
            }
        }

        public JObject Mode(string modeKey)
        {
            JObject mode = Login()[modeKey] as JObject
                ?? throw new InvalidDataException($"{Name}: login omitted {modeKey}.");
            ObserveMode(mode, modeKey);
            return mode;
        }

        public bool SettledMode(string modeKey) => Mode(modeKey).Value<bool>("IsSettle");

        public int ArchiveCount() => State.Files.Count;

        public int NextPacketId() => ++packetCounter;

        public int ActiveRunId => State.ActiveRuns.TryGetValue(State.CurrentMode, out Theatre6RunState? run)
            ? run.RunId
            : throw new InvalidDataException($"{Name}: Theatre6 has no active run in mode {State.CurrentMode}.");

        public int NextFreeSaveSlot()
        {
            HashSet<int> used = State.Files.Select(file => file.SlotId).ToHashSet();
            for (int slot = 1; slot <= RequiemConfigValue("MaxSaveFileCount"); slot++)
                if (!used.Contains(slot)) return slot;
            throw new InvalidDataException($"{Name}: Theatre6 save slots are exhausted.");
        }

        public long Balance(int itemId) => Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;

        public int RequiemConfigValue(string key) => Convert.ToInt32(TableReaderV2.Parse<Theatre6ConfigTable>()
            .Single(row => row.Key == key).Values[0]);

        // Authored gameplay gate: config PlayModeConditionId resolves to condition type 23201 over
        // Theatre6 PassStageRecords. The story scenario proves the state is produced naturally by
        // clearing the common storyline; other scenarios start from that same authored state.
        public int PlayModeConditionId() => RequiemConfigValue("PlayModeConditionId");

        public void UnlockGameplay()
        {
            ConditionTable condition = TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == PlayModeConditionId());
            Require(Convert.ToInt32(condition.Type) == 23201 && condition.Params.Count >= 2,
                "Theatre6 gameplay gate must remain the authored stage-progress condition.");
            int stage = Convert.ToInt32(condition.Params[0]);
            State.PassStageRecords[stage] = Math.Max(State.PassStageRecords.GetValueOrDefault(stage), Convert.ToInt32(condition.Params[1]));
            SaveFixture();
        }

        public void Dispose()
        {
            Harness.Dispose();
            collections.Dispose();
        }

        // One line per wire message for the original-Lua replay owner: requests carry the exact
        // client key set this harness constructs, pushes keep arrival order, and every request,
        // response and fence shares one transport counter.
        private static class RequiemWire
        {
            private static readonly object Gate = new();
            private static readonly Dictionary<string, StreamWriter> Writers = [];

            public static void Record(string caseName, int session, string type, string name, int? id, object? content, byte[]? payload)
            {
                string directory = Environment.GetEnvironmentVariable("ASCNET_REQUIEM_TRANSCRIPT_DIR")
                    ?? Path.Combine(Path.GetTempPath(), "ascnet-requiem-proof");
                try
                {
                    lock (Gate)
                    {
                        if (!Writers.TryGetValue(caseName, out StreamWriter? writer))
                        {
                            Directory.CreateDirectory(directory);
                            writer = new StreamWriter(Path.Combine(directory, $"theatre6-wire-{caseName}.jsonl"), append: false, new UTF8Encoding(false))
                            {
                                AutoFlush = true
                            };
                            Writers.Add(caseName, writer);
                        }

                        writer.WriteLine(new JObject
                        {
                            ["Name"] = name,
                            ["Id"] = id is null ? JValue.CreateNull() : new JValue(id.Value),
                            ["Type"] = type,
                            ["Session"] = session,
                            ["Bytes"] = payload is null ? JValue.CreateNull() : new JValue(Convert.ToBase64String(payload)),
                            ["Content"] = content switch
                            {
                                null => JValue.CreateNull(),
                                JObject body => body,
                                _ => JToken.FromObject(content)
                            }
                        }.ToString(Formatting.None));
                    }
                }
                catch (IOException)
                {
                    // Transcript export is diagnostic only; it must never fail a compatibility run.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
