using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateGuideProgressionCompatibility()
    {
        ValidateGuideProgression();
        ValidateVersion46GuideCompletion();
        ValidateVersion46GuideRewardRecovery();
        Console.WriteLine("Guide progression: native single and group completion, durable reward retry and packet order passed.");
    }

    private static void ValidateGuideProgression()
    {
        var config = AscNet.Common.Common.config;
        if (config.Database.Host != "127.0.0.1" || config.Database.Port == 27017
            || !AscNet.Common.Common.db.DatabaseNamespace.DatabaseName.StartsWith("ascnet_manual_full_", StringComparison.Ordinal))
            throw new InvalidOperationException("Guide progression verification requires an isolated loopback MongoDB and an ascnet_manual_full_ database.");

        PacketFactory.LoadPacketHandlers();
        CommandFactory.commands.Clear();
        CommandFactory.LoadCommands();
        var guides = TableReaderV2.Parse<GuideGroupTable>().Where(guide => guide.Ignore == 0).ToDictionary(guide => guide.Id);
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(condition => condition.Id);
        GuideGroupTable first = TableReaderV2.Parse<GuideFightTable>().Select(fight => guides[fight.Id])
            .OrderBy(guide => guide.Priority).First();
        GuideGroupTable calibration = guides[TableReaderV2.Parse<DrawCalibrationGuideTable>().Single().GuideGroupId];
        GuideGroupTable itemGuide = guides.Values.First(guide => guide.RewardId > 0
            && guide.ConditionId.Count == 1 && conditions[guide.ConditionId[0]].Type == 10105);
        GuideGroupTable mixedGuide = guides.Values.Single(guide => guide.RewardId > 0
            && RewardHandler.GetRewardGoods(guide.RewardId).Any(goods => RewardHandler.GetRewardType(goods) == RewardType.Equip));
        GuideGroupTable loginRecoveryGuide = guides.Values.Single(guide => guide.RewardId > 0
            && guide.Id != itemGuide.Id && guide.Id != mixedGuide.Id);
        GuideGroupTable courseGuide = guides.Values.Single(guide => TableReaderV2.Parse<GuideCompleteTable>()
            .Any(completion => completion.Id == guide.CompleteId && completion.Param.FirstOrDefault() == 12));
        GuideGroupTable roomGuide = guides.Values.First(guide => guide.ConditionId.Any(id => conditions[id].Type == 15206));

        List<long> accounts = [];
        List<LoopbackSessionHarness> sessions = [];
        string oldAllowlist = config.GuideGmPlayerIds;
        int requestId = 59_000;
        try
        {
            Account accountA = CreateAccount();
            Account accountB = CreateAccount();
            config.GuideGmPlayerIds = accountA.Uid.ToString();
            var (a, initialA) = Login(accountA.Uid);
            var (b, initialB) = Login(accountB.Uid);
            AssertEqual((1L, 0), (initialA.PlayerData.Level, initialA.PlayerData.GuideData.Count), "fresh account A");
            AssertEqual((1L, 0), (initialB.PlayerData.Level, initialB.PlayerData.GuideData.Count), "fresh account B");
            string stateB = StateFingerprint(accountB.Uid);

            var level = Chat(a, "/level 120");
            AssertEqual(120, Pushes<NotifyPlayerLevel>(level.Pushes).Single().Level, "level jump live packet");
            AssertEqual((120L, 0), (a.Session.player.PlayerData.Level, a.Session.player.PlayerData.GuideData.Count), "level jump leaves guides alone");
            AssertEqual(0, Pushes<NotifyGuide>(level.Pushes).Length, "level jump sends no guide notifications");

            string stateA = StateFingerprint(accountA.Uid);
            var preview = Chat(a, $"/guide {first.Id} preserve --dry-run");
            Contains(preview.Feedback, $"Guide {first.Id} would complete");
            AssertEqual(0, Pushes<NotifyGuide>(preview.Pushes).Length, "preview guide pushes");
            AssertEqual(stateA, StateFingerprint(accountA.Uid), "preview leaves all four MongoDB documents identical");
            var denied = Chat(b, $"/guide {first.Id} skip", accountA.Uid);
            Contains(denied.Feedback, "disabled for this account");
            AssertEqual(1, Pushes<NotifyChatMessage>(denied.Pushes).Length, "private denied-command feedback");
            AssertEqual(stateB, StateFingerprint(accountB.Uid), "denied command is read-only");
            AssertEqual(stateA, StateFingerprint(accountA.Uid), "forged chat target cannot modify another account");

            var single = Chat(a, $"/guide {first.Id} preserve", accountB.Uid);
            AssertEqual(first.Id, Pushes<NotifyGuide>(single.Pushes).Single().GuideGroupId, "single completion push");
            AssertEqual(true, Player.FromPlayerId(accountA.Uid).PlayerData.GuideData.Contains(first.Id), "single completion persisted");
            AssertEqual(stateB, StateFingerprint(accountB.Uid), "authorized command does not write the chat target");
            AssertNoAvailablePacket(b, "authorized command sends no packet to the other account");
            Close(b);

            var group = Chat(a, $"/guides {first.GroupId} preserve");
            int[] otherPrologue = guides.Values.Where(guide => guide.GroupId == first.GroupId && guide.Id != first.Id)
                .OrderBy(guide => guide.Priority).Select(guide => guide.Id).ToArray();
            AssertIntegerList(otherPrologue.Select(id => (long)id).ToArray(),
                Pushes<NotifyGuide>(group.Pushes).Select(push => (long)push.GuideGroupId).ToArray(), "bulk prologue guide push order");
            var repeatedGroup = Chat(a, $"/guides {first.GroupId} grant");
            AssertEqual(0, Pushes<NotifyGuide>(repeatedGroup.Pushes).Length, "repeated group completion sends no new guides");

            DrawAdjustActivityInfo activity = DrawManager.GetDrawAdjustActivityInfos(a.Session.player).Single();
            DrawAdjustTargetRequest target = new() { ActivityId = activity.ActivityId, TargetId = activity.EffectTargetTemplateIds.First() };
            int SelectTarget() => Exchange<DrawAdjustTargetResponse>(a, nameof(DrawAdjustTargetRequest), target).Response.Code;
            AssertEqual(20019007, SelectTarget(), "draw calibration remains guide-gated at level 120");
            Contains(Chat(a, $"/guide {calibration.Id} skip --ignore-client-triggers").Feedback, "blocked");
            AssertEqual(20019007, SelectTarget(), "GM command preserves draw guide's story prerequisite");
            SeedStagePrerequisites(a, calibration);
            var unlocked = Chat(a, $"/guide {calibration.Id} preserve");
            AssertEqual(calibration.Id, Pushes<NotifyGuide>(unlocked.Pushes).Single().GuideGroupId, "calibration guide push");
            AssertEqual(0, SelectTarget(), "existing feature gate accepts the now-completed guide");
            target.TargetId = int.MaxValue;
            AssertEqual(20018024, SelectTarget(), "existing calibration target validation remains active");
            AssertEqual(0, a.Session.player.PlayerData.Marks.Count, "guide command does not set functional marks");

            SeedStagePrerequisites(a, courseGuide);
            Contains(Chat(a, $"/guide {courseGuide.Id} skip").Feedback, "Requires the course reward");
            SeedStagePrerequisites(a, roomGuide);
            Contains(Chat(a, $"/guide {roomGuide.Id} preserve --dry-run").Feedback, "is a client trigger");
            Contains(Chat(a, $"/guide {roomGuide.Id} preserve --dry-run --ignore-client-triggers").Feedback, "would complete");

            SeedStagePrerequisites(a, itemGuide);
            RewardGoodsTable itemReward = RewardHandler.GetRewardGoods(itemGuide.RewardId).Single();
            long itemsBefore = ItemCount(accountA.Uid, itemReward.TemplateId);
            Contains(Chat(a, $"/guide {itemGuide.Id} preserve").Feedback, "preserved");
            AssertEqual(false, Player.FromPlayerId(accountA.Uid).PlayerData.GuideData.Contains(itemGuide.Id), "preserve leaves native completion available");
            Contains(Chat(a, $"/guide {itemGuide.Id} grant --dry-run").Feedback, $"{itemReward.TemplateId} × {itemReward.Count}");
            AssertEqual(itemsBefore, ItemCount(accountA.Uid, itemReward.TemplateId), "grant preview does not pay");
            var skip = Chat(a, $"/guide {itemGuide.Id} skip");
            AssertEqual(itemGuide.Id, Pushes<NotifyGuide>(skip.Pushes).Single().GuideGroupId, "skip sends completion");
            AssertEqual(0, Pushes<NotifyItemDataList>(skip.Pushes).Length, "skip sends no item reward");
            Contains(Chat(a, $"/guide {itemGuide.Id} grant").Feedback, "Already recorded");
            var skipRetry = Exchange<GuideCompleteResponse>(a, nameof(GuideCompleteRequest), new GuideCompleteRequest { GuideGroupId = itemGuide.Id });
            AssertEqual((0, true), (skipRetry.Response.Code, skipRetry.Response.RewardGoodsList is null), "native retry cannot claim a skipped reward");
            AssertEqual(itemsBefore, ItemCount(accountA.Uid, itemReward.TemplateId), "skip survives retries without payout");
            Console.WriteLine("Guide progression: fresh accounts, level 120, preview, single and bulk, account isolation, feature gate, and all reward policies exercised.");

            SeedStagePrerequisites(a, mixedGuide);
            List<RewardGoodsTable> mixedGoods = RewardHandler.GetRewardGoods(mixedGuide.RewardId);
            int equipId = mixedGoods.Single(goods => RewardHandler.GetRewardType(goods) == RewardType.Equip).TemplateId;
            int beforeEquips = EquipCount(accountA.Uid, equipId);
            var beforeItems = mixedGoods.Where(goods => RewardHandler.GetRewardType(goods) == RewardType.Item)
                .ToDictionary(goods => goods.TemplateId, goods => ItemCount(accountA.Uid, goods.TemplateId));
            string claim = $"guide:{mixedGuide.Id}";
            IMongoCollection<Inventory> inventory = Inventory.collection;
            try
            {
                Inventory.collection = GuideAckLoss(inventory, row => row.Uid == accountA.Uid && row.AppliedRewardClaims.Contains(claim));
                SendChat(a, $"/guides {mixedGuide.GroupId} grant");
                a.Session.Completion.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            }
            finally { Inventory.collection = inventory; }
            Player interrupted = Player.FromPlayerId(accountA.Uid);
            AssertEqual(true, interrupted.PendingGuideCompletion?.Any(entry => entry.GuideId == mixedGuide.Id) == true, "frozen guide intent survives lost inventory acknowledgement");
            AssertEqual(false, interrupted.PlayerData.GuideData.Contains(mixedGuide.Id), "guide unavailable until settlement finishes");
            AssertEqual(true, Inventory.FromUid(accountA.Uid).AppliedRewardClaims.Contains(claim), "inventory committed before failure");
            AssertEqual(false, Character.FromUid(accountA.Uid, []).AppliedRewardClaims.Contains(claim), "character settlement awaits recovery");
            Close(a);

            (a, var recovered) = Reconnect(accountA.Uid);
            AssertEqual(true, Pushes<NotifyGuide>(recovered).Any(push => push.GuideGroupId == mixedGuide.Id), "recovered guide synced after reconnect");
            AssertEqual(true, Pushes<NotifyEquipDataList>(recovered).Single().EquipDataList.Any(equip => equip.TemplateId == equipId), "recovered equipment pushed");
            AssertEqual(true, Pushes<NotifyItemDataList>(recovered).Single().ItemDataList.Any(item => beforeItems.ContainsKey(item.Id)), "committed inventory pushed on reconnect");
            AssertEqual(true, Player.FromPlayerId(accountA.Uid).PendingGuideCompletion is null, "recovery clears the journal");
            AssertEqual(beforeEquips + 1, EquipCount(accountA.Uid, equipId), "one equipment instance after recovery");
            foreach (RewardGoodsTable goods in mixedGoods.Where(goods => beforeItems.ContainsKey(goods.TemplateId)))
                AssertEqual(beforeItems[goods.TemplateId] + goods.Count, ItemCount(accountA.Uid, goods.TemplateId), "no duplicate inventory payout after recovery");
            var groupRetry = Exchange<GuideGroupFinishResponse>(a, nameof(GuideGroupFinishRequest), new GuideGroupFinishRequest { GroupId = mixedGuide.GroupId });
            AssertEqual((0, true), (groupRetry.Response.Code, groupRetry.Response.RewardGoodsList is null), "native group retry shares the guide receipt");

            SeedStagePrerequisites(a, loginRecoveryGuide);
            RewardGoodsTable loginReward = RewardHandler.GetRewardGoods(loginRecoveryGuide.RewardId).Single();
            long beforeLoginRecovery = ItemCount(accountA.Uid, loginReward.TemplateId);
            IMongoCollection<Player> players = Player.collection;
            try
            {
                Player.collection = GuideAckLoss(players, row => row.PlayerData.Id == accountA.Uid
                    && row.PendingGuideCompletion?.Any(entry => entry.GuideId == loginRecoveryGuide.Id) == true);
                SendChat(a, $"/guide {loginRecoveryGuide.Id} grant");
                a.Session.Completion.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            }
            finally { Player.collection = players; }
            AssertEqual(beforeLoginRecovery, ItemCount(accountA.Uid, loginReward.TemplateId), "lost intent acknowledgement happens before reward writes");
            Close(a);
            (a, var recoveredLogin) = Login(accountA.Uid);
            AssertEqual(true, recoveredLogin.PlayerData.GuideData.Contains(loginRecoveryGuide.Id), "full login recovers the accepted guide intent");
            AssertEqual(beforeLoginRecovery + loginReward.Count,
                recoveredLogin.ItemList.Single(item => item.Id == loginReward.TemplateId).Count,
                "full login recovers the payout in the same snapshot");

            stateA = StateFingerprint(accountA.Uid);
            Contains(Chat(a, "/guides all grant --ignore-client-triggers --dry-run").Feedback, "would complete");
            AssertEqual(stateA, StateFingerprint(accountA.Uid), "bulk preview leaves the durable account identical");
            var bulk = Chat(a, "/guides all preserve --ignore-client-triggers");
            AssertEqual(true, Pushes<NotifyGuide>(bulk.Pushes).Any(push => push.GuideGroupId == roomGuide.Id), "explicit client trigger waiver applies in bulk");
            AssertEqual(false, a.Session.player.PlayerData.GuideData.Contains(courseGuide.Id), "bulk preserves the missing course claim");
            AssertEqual(0, Pushes<NotifyGuide>(Chat(a, "/guides all preserve --ignore-client-triggers").Pushes).Length, "bulk completion reaches a stable result");

            long[] expected = Player.FromPlayerId(accountA.Uid).PlayerData.GuideData.Order().ToArray();
            Close(a);
            (a, var reloggedA) = Login(accountA.Uid);
            AssertIntegerList(expected, reloggedA.PlayerData.GuideData.Order().ToArray(), "full login reflects every committed guide");
            AssertEqual(120L, reloggedA.PlayerData.Level, "level after full login");
            AssertEqual(beforeEquips + 1, EquipCount(accountA.Uid, equipId), "relogin does not re-grant equipment");
            Close(a);

            stateA = StateFingerprint(accountA.Uid);
            config.GuideGmPlayerIds = accountB.Uid.ToString();
            (b, var reloggedB) = Login(accountB.Uid);
            AssertEqual((1L, 0), (reloggedB.PlayerData.Level, reloggedB.PlayerData.GuideData.Count), "account B stayed fresh across account A's commands and crashes");
            SeedStagePrerequisites(b, itemGuide);
            long balanceB = ItemCount(accountB.Uid, itemReward.TemplateId);
            try
            {
                Player.collection = GuideAckLoss(players, row => row.PlayerData.Id == accountB.Uid
                    && row.PendingGuideCompletion is null && row.PlayerData.GuideData.Contains(itemGuide.Id));
                SendChat(b, $"/guide {itemGuide.Id} grant");
                b.Session.Completion.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            }
            finally { Player.collection = players; }
            AssertEqual(true, Player.FromPlayerId(accountB.Uid).PlayerData.GuideData.Contains(itemGuide.Id), "final completion survives lost player acknowledgement");
            Close(b);
            (b, var replayed) = Reconnect(accountB.Uid);
            AssertEqual(itemGuide.Id, Pushes<NotifyGuide>(replayed).Single().GuideGroupId, "reconnect replays a committed guide when live pushes were lost");
            AssertEqual(balanceB + itemReward.Count, ItemCount(accountB.Uid, itemReward.TemplateId), "receipt is once per account, including reconnect");
            Contains(Chat(b, $"/guide {itemGuide.Id} grant").Feedback, "Already recorded");
            AssertEqual(balanceB + itemReward.Count, ItemCount(accountB.Uid, itemReward.TemplateId), "repeated GM grant pays once");
            AssertEqual(stateA, StateFingerprint(accountA.Uid), "operating on account B leaves account A identical");
            Console.WriteLine("Guide progression: isolated MongoDB and encrypted TCP passed intent, inventory and final-write acknowledgement loss, reconnect replay, full relogin, and per-account receipts.");
        }
        finally
        {
            foreach (LoopbackSessionHarness harness in sessions.ToArray())
                Close(harness);
            config.GuideGmPlayerIds = oldAllowlist;
            Player.collection.DeleteMany(Builders<Player>.Filter.In(row => row.PlayerData.Id, accounts));
            Character.collection.DeleteMany(Builders<Character>.Filter.In(row => row.Uid, accounts));
            Inventory.collection.DeleteMany(Builders<Inventory>.Filter.In(row => row.Uid, accounts));
            Stage.collection.DeleteMany(Builders<Stage>.Filter.In(row => row.Uid, accounts));
            Account.collection.DeleteMany(Builders<Account>.Filter.In(row => row.Uid, accounts));
        }

        Account CreateAccount()
        {
            long uid;
            do { uid = Random.Shared.NextInt64(100_000_000, 200_000_000); }
            while (Account.FromUID(uid) is not null || Player.TryFromPlayerId(uid) is not null);
            Account account = new() { Uid = uid, Username = $"guide-{Guid.NewGuid():N}", Password = Guid.NewGuid().ToString("N"), Token = Guid.NewGuid().ToString() };
            Account.collection.InsertOne(account);
            accounts.Add(uid);
            _ = Player.FromPlayerId(uid);
            return account;
        }

        LoopbackSessionHarness Open()
        {
            var harness = new LoopbackSessionHarness(new Character(), sessionId: $"guide-regression-{Guid.NewGuid():N}", startClientLoop: false);
            sessions.Add(harness);
            harness.Session.player = null!;
            harness.Session.character = null!;
            harness.Session.inventory = null!;
            harness.Session.stage = null!;
            AssertEqual(true, Server.Instance.Sessions.TryAdd(harness.Session.id, harness.Session), "register guide test session");
            harness.Session.Start();
            return harness;
        }

        (LoopbackSessionHarness, NotifyLogin) Login(long uid)
        {
            LoopbackSessionHarness harness = Open();
            var login = Exchange<LoginResponse>(harness, nameof(LoginRequest), new LoginRequest { Token = Player.FromPlayerId(uid).Token });
            AssertEqual(0, login.Response.Code, "login response");
            return (harness, Pushes<NotifyLogin>(login.Pushes).Single());
        }

        (LoopbackSessionHarness, List<Packet.Push>) Reconnect(long uid)
        {
            LoopbackSessionHarness harness = Open();
            var reconnect = Exchange<ReconnectResponse>(harness, nameof(ReconnectRequest), new ReconnectRequest
            {
                Token = Player.FromPlayerId(uid).Token, PlayerId = checked((uint)uid), LastMsgSeqNo = 99
            });
            AssertEqual(0, reconnect.Response.Code, "reconnect response");
            AssertEqual(0, Pushes<NotifyLogin>(reconnect.Pushes).Length, "reconnect uses deltas on the existing client cache");
            return (harness, reconnect.Pushes);
        }

        void Close(LoopbackSessionHarness harness)
        {
            sessions.Remove(harness);
            harness.Dispose();
            harness.Session.Completion.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
        }

        void SeedStagePrerequisites(LoopbackSessionHarness harness, GuideGroupTable guide)
        {
            // Synthetic persisted story clears exercise existing predicates without changing their implementation.
            lock (Session.GetPlayerOperationLock(harness.Session.player.PlayerData.Id))
            {
                foreach (int id in guide.ConditionId.Where(id => id > 0 && conditions[id].Type == 10105).Select(id => conditions[id].Params[0]))
                    harness.Session.stage.Stages[id] = new StageDatum { StageId = id, Passed = true, StarsMark = 7 };
                harness.Session.stage.SaveChecked();
            }
        }

        (string Feedback, List<Packet.Push> Pushes) Chat(LoopbackSessionHarness harness, string content, long? targetUid = null)
        {
            var exchange = Exchange<SendChatResponse>(harness, nameof(SendChatRequest), ChatRequest(content, targetUid));
            AssertEqual(0, exchange.Response.Code, "chat command response");
            return (Pushes<NotifyChatMessage>(exchange.Pushes).Last().Content!, exchange.Pushes);
        }

        void SendChat(LoopbackSessionHarness harness, string content) =>
            harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(SendChatRequest), ++requestId, ChatRequest(content, null)));

        static SendChatRequest ChatRequest(string content, long? targetUid) => new()
        {
            TargetIdList = targetUid is long target ? [target] : [],
            ChatData = new() { ChannelType = ChatChannelType.World, MsgType = ChatMsgType.Normal, Content = content }
        };

        (T Response, List<Packet.Push> Pushes) Exchange<T>(LoopbackSessionHarness harness, string name, object request)
            where T : class
        {
            int id = ++requestId;
            int barrier = ++requestId;
            harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(name, id, request));
            harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), barrier, new HeartbeatRequest()));
            List<Packet.Push> pushes = [];
            T? result = null;
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} wire packet");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                if (response.Id == barrier)
                    return (result ?? throw new InvalidDataException($"Missing {typeof(T).Name} for {name}."), pushes);
                AssertEqual(id, response.Id, $"{name} response id");
                AssertEqual(typeof(T).Name, response.Name, $"{name} response name");
                result = MessagePackSerializer.Deserialize<T>(response.Content);
            }
        }

        static void Contains(string actual, string text) =>
            AssertEqual(true, actual.Contains(text, StringComparison.Ordinal), $"command feedback contains '{text}' (received '{actual}')");
        static T[] Pushes<T>(List<Packet.Push> pushes) => pushes.Where(push => push.Name == typeof(T).Name)
            .Select(push => MessagePackSerializer.Deserialize<T>(push.Content)).ToArray();
        static long ItemCount(long uid, int id) => Inventory.FromUid(uid).Items.Where(item => item.Id == id).Sum(item => item.Count);
        static int EquipCount(long uid, int id) => Character.FromUid(uid, []).Equips.Count(equip => equip.TemplateId == id);
        static string StateFingerprint(long uid) => string.Join("/", new[]
        {
            Player.collection.Find(row => row.PlayerData.Id == uid).Single().ToBson(),
            Character.collection.Find(row => row.Uid == uid).Single().ToBson(),
            Inventory.collection.Find(row => row.Uid == uid).Single().ToBson(),
            Stage.collection.Find(row => row.Uid == uid).Single().ToBson()
        }.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static IMongoCollection<T> GuideAckLoss<T>(IMongoCollection<T> collection, Predicate<T> fail)
        where T : class
    {
        var wrapped = DispatchProxy.Create<IMongoCollection<T>, GuideAckLossProxy<T>>();
        var proxy = (GuideAckLossProxy<T>)(object)wrapped;
        proxy.Collection = collection;
        proxy.Fail = fail;
        return wrapped;
    }

    private class GuideAckLossProxy<T> : DispatchProxy where T : class
    {
        public IMongoCollection<T> Collection { get; set; } = null!;
        public Predicate<T> Fail { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Collection, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            if (method!.Name == nameof(IMongoCollection<T>.ReplaceOne) && args?.OfType<T>().SingleOrDefault() is T document
                && Fail(document) && result is ReplaceOneResult { IsAcknowledged: true, MatchedCount: 1 })
                throw new MongoException($"Injected lost acknowledgement after committed {typeof(T).Name} write.");
            return result;
        }
    }
}
